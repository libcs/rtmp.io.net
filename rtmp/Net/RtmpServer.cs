using Hina;
using Hina.Collections;
using Hina.IO;
using Hina.Linq;
using Hina.Net;
using Hina.Security;
using Hina.Threading;
using Konseki;
using Rtmp.FlexMessages;
using Rtmp.IO;
using Rtmp.Net.RtmpMessages;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Rtmp.rtmp;

namespace Rtmp.Net
{
    #region RtmpServer

    public class RtmpServer : IDisposable
    {
        const int DefaultPort = 1935;

        // the cancellation source (and token) that this server internally uses to signal disconnection
        readonly CancellationToken token;
        readonly CancellationTokenSource source;

        // the serialization context for this rtmp client
        readonly SerializationContext context;

        // the callback manager that handles completing invocation requests
        readonly TaskCallbackManager<uint, object> callbacks;

        RtmpClient[] clients;
        //int num_connected_clients;

        RtmpServer(SerializationContext context, int max_clients)
        {
            this.context = context;
            callbacks = new TaskCallbackManager<uint, object>();
            source = new CancellationTokenSource();
            token = source.Token;
            printf(LOG_LEVEL_INFO, $"server started with {max_clients} client slots\n");
            clients = new RtmpClient[max_clients];
            //num_connected_clients = 0;
        }

        public void Dispose() =>
            CloseAsync(true).Wait();

        #region (static) connectasync()

        public class Options
        {
            public string Url;
            public int ChunkLength = 4192;
            public SerializationContext Context;

            public RemoteCertificateValidationCallback Validate;
            public X509Certificate ServerCertificate;
        }

        public static async Task<RtmpServer> ConnectAsync(Options options, int max_clients = 5)
        {
            Check.NotNull(options.Url, options.Context);

            var url = options.Url;
            var chunkLength = options.ChunkLength;
            var context = options.Context;
            var validate = options.Validate ?? ((sender, certificate, chain, errors) => true);
            var serverCertificate = options.ServerCertificate;

            var uri = new Uri(url);
            var tcpListener = await TcpListenerEx.AcceptClientAsync(uri.Host, uri.Port != -1 ? uri.Port : DefaultPort);
            var server = new RtmpServer(context, max_clients);

            async Task<RtmpClient> CreateClient(TcpClient tcp)
            {
                var stream = await GetStreamAsync(uri, tcp.GetStream(), validate, serverCertificate);
                Console.Write($"connect: {tcp.Client.LocalEndPoint}");
                var client = await RtmpClient.ConnectAsync(server, context, stream, chunkLength);
                return client;
            }

            tcpListener.Start();
            tcpListener.AcceptTcpClientExAsync().ContinueWith(x => CreateClient(x.Result)).Forget();

            return server;
        }

        public void Wait()
        {
            token.WaitHandle.WaitOne();
        }

        static async Task<Stream> GetStreamAsync(Uri uri, Stream stream, RemoteCertificateValidationCallback validate, X509Certificate serverCertificate)
        {
            CheckDebug.NotNull(uri, stream, validate);

            switch (uri.Scheme)
            {
                case "rtmp":
                    return stream;

                case "rtmps":
                    Check.NotNull(validate);

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
            return Task.CompletedTask;
        }
    }

    #endregion
}
