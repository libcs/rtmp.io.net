using Hina;
using Hina.Net;
using Hina.Threading;
using Konseki;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Rtmp.Net.RtmpMessages;

namespace Rtmp.Net
{
    #region ServerDisconnectedException

    public class ServerDisconnectedException : Exception
    {
        public ServerDisconnectedException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion

    #region RtmpServer

    public class RtmpServer : IDisposable
    {
        const int DefaultPort = 1935;
        internal readonly Options options;

        public event EventHandler<ServerDisconnectedException> Disconnected;
        public event EventHandler<Exception> CallbackException;

        // the cancellation source (and token) that this server internally uses to signal disconnection
        readonly CancellationToken token;
        readonly CancellationTokenSource source;

        // the serialization context for this rtmp client
        readonly SerializationContext context;

        // the callback manager that handles completing invocation requests
        readonly TaskCallbackManager<uint, object> callbacks;

        //// fn(message: RtmpMessage, chunk_stream_id: int) -> None
        ////     queues a message to be written. this is assigned post-construction by `connectasync`.
        //Action<object, int> queue;

        // counter for monotonically increasing invoke ids
        int invokeId;

        // true if this connection is no longer connected
        bool disconnected;

        // a tuple describing the cause of the disconnection. either value may be null.
        (string message, Exception inner) cause;

        // clients
        public Dictionary<string, (RtmpClient client, RtmpClient.Options options)> clients;

        RtmpServer(SerializationContext context, Options options)
        {
            this.context = context;
            this.options = options;
            callbacks = new TaskCallbackManager<uint, object>();
            source = new CancellationTokenSource();
            token = source.Token;
            clients = new Dictionary<string, (RtmpClient client, RtmpClient.Options options)>();
            Kon.Emit($"server started\n");
        }

        public void Dispose()
        {
            if (!disconnected)
                CloseAsync(true).Wait();
        }

        #region internal callbacks

        // called internally when an error that would invalidate this connection occurs.
        // `inner` may be null.
        void InternalCloseConnection(string reason, Exception inner)
        {
            Volatile.Write(ref cause.message, reason);
            Volatile.Write(ref cause.inner, inner);
            Volatile.Write(ref disconnected, true);

            Task.WaitAll(clients.Select(x => x.Value.client.CloseAsync()).ToArray());

            source.Cancel();
            callbacks.SetExceptionForAll(DisconnectedException());

            WrapCallback(() => Disconnected?.Invoke(this, DisconnectedException()));
            Kon.Assert(clients.Count == 0);
        }

        // this method will never throw an exception unless that exception will be fatal to this server, and thus
        // the server would be forced to close.
        void InternalReceiveEvent(object message)
        {
        }

        #endregion

        #region internal helper methods

        uint NextInvokeId() => (uint)Interlocked.Increment(ref invokeId);

        ServerDisconnectedException DisconnectedException() => new ServerDisconnectedException(cause.message, cause.inner);

        // calls a remote endpoint, sent along the specified chunk stream id, on message stream id #0
        //Task<object> InternalCallAsync(object request, int chunkStreamId)
        //{
        //    if (disconnected) throw DisconnectedException();

        //    var task = callbacks.Create(0); //request.InvokeId

        //    queue(request, chunkStreamId);
        //    return task;
        //}

        void WrapCallback(Action action)
        {
            try
            {
                try { action(); }
                catch (Exception e) { CallbackException?.Invoke(this, e); }
            }
            catch (Exception e)
            {
                Kon.DebugRun(() =>
                {
                    Kon.DebugException("unhandled exception in callback", e);
                    Debugger.Break();
                });
            }
        }

        #endregion

        #region (static) connectasync()

        public class Options
        {
            public string Url;
            public SerializationContext Context = new SerializationContext();

            public RemoteCertificateValidationCallback Validate;
            public X509Certificate ServerCertificate;
            //
            public int WindowAcknowledgementSize = 2500000;
            public (int Bandwidth, PeerBandwidthLimitType LimitType) PeerBandwidth = (2500000, PeerBandwidthLimitType.Dynamic);
            public int ChunkLength = 4096;
        }

        static void DisconnectClient(object s, ClientDisconnectedException e)
        {
            var client = (RtmpClient)s;
            Kon.Trace($"client {client.clientId} disconnected");
            client.Disconnected -= DisconnectClient;
            var clients = client.server.clients;
            foreach (var x in clients.Where(x => x.Value.client == client).ToList())
                clients.Remove(x.Key);
        }

        async Task ConnectClientAsync(TcpClient tcp, Uri uri, Options options, Action<RtmpClient> onConnected, int max_clients)
        {
            var validate = options.Validate ?? ((sender, certificate, chain, errors) => true);
            var serverCertificate = options.ServerCertificate;
            var clientId = $"{NextInvokeId()}";
            Kon.Emit($"client {clientId} connected from {tcp.Client.LocalEndPoint}\n");

            var stream = await GetStreamAsync(uri, tcp.GetStream(), validate, serverCertificate);
            var clientOptions = new RtmpClient.Options
            {
                ChunkLength = options.ChunkLength,
                Context = options.Context,
            };
            var client = await RtmpClient.ServerConnectAsync(this, clientOptions, stream, clientId, DisconnectClient);

            Kon.Assert(clients.Count < max_clients);
            clients.Add(clientId, (client, clientOptions));
            onConnected?.Invoke(client);
        }

        public static async Task<RtmpServer> ConnectAsync(Options options, Action<RtmpClient> onConnected, int max_clients = 5)
        {
            Check.NotNull(options, options.Context);

            var url = options.Url ?? "rtmp://any";
            var context = options.Context;

            var uri = new Uri(url);
            var tcpListener = await TcpListenerEx.AcceptClientAsync(uri.Host, uri.Port != -1 ? uri.Port : DefaultPort);

            var server = new RtmpServer(context, options);

            server.RunAsync(tcpListener, async tcp => await server.ConnectClientAsync(tcp, uri, options, onConnected, max_clients)).Forget();

            return server;
        }

        // this method must only be called once
        async Task RunAsync(TcpListener tcpListener, Action<TcpClient> action)
        {
            tcpListener.Start();
            while (!token.IsCancellationRequested)
                try
                {
                    var tcp = await tcpListener.AcceptTcpClientExAsync();
                    action(tcp);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Kon.DebugException("rtmpserver::accepttcpclient encountered an error", e);

                    InternalCloseConnection("accept-exception", e);
                    return;
                }
        }

        public void Wait() =>
            token.WaitHandle.WaitOne();

        static async Task<Stream> GetStreamAsync(Uri uri, Stream stream, RemoteCertificateValidationCallback validate, X509Certificate serverCertificate)
        {
            CheckDebug.NotNull(uri, stream);

            switch (uri.Scheme)
            {
                case "rtmp":
                    return stream;

                case "rtmps":
                    Check.NotNull(validate, serverCertificate);

                    var ssl = new SslStream(stream, false, validate);
                    await ssl.AuthenticateAsServerAsync(serverCertificate);

                    return ssl;

                default:
                    throw new ArgumentException($"scheme \"{uri.Scheme}\" must be one of rtmp:// or rtmps://");
            }
        }

        #endregion

        public Task CloseAsync(bool forced = false)
        {
            // currently we don't have a notion of gracefully closing a connection. all closes are hard force closes,
            // but we leave the possibility for properly implementing graceful closures in the future
            InternalCloseConnection("close-requested-by-user", null);

            return Task.CompletedTask;
        }
    }

    #endregion
}
