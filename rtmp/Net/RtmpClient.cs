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
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rtmp.Net
{
    #region ClientDisconnectedException

    public class ClientDisconnectedException : Exception
    {
        public ClientDisconnectedException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion

    #region PacketContentType

    enum PacketContentType : byte
    {
        SetChunkSize = 1,
        AbortMessage = 2,
        Acknowledgement = 3,
        UserControlMessage = 4,
        WindowAcknowledgementSize = 5,
        SetPeerBandwith = 6,

        Audio = 8,
        Video = 9,

        DataAmf3 = 15, // 0x0f | stream send
        SharedObjectAmf3 = 16, // 0x10 | shared obj
        CommandAmf3 = 17, // 0x11 | aka invoke

        DataAmf0 = 18, // 0x12 | stream metadata
        SharedObjectAmf0 = 19, // 0x13 | shared object
        CommandAmf0 = 20, // 0x14 | aka invoke

        Aggregate = 22,
    }

    #endregion

    #region RtmpClient

    static class Ts
    {
        // because we do not currently transmit audio or video packets, or any kind of data that requires a per-chunk
        // timestamp, can get away without timestamps at all. however, we may need this in the future should we decide
        // to support them.
        public static uint CurrentTime => 0;
    }

    public class RtmpClient : IDisposable
    {
        const int DefaultPort = 1935;

        public event EventHandler<MessageReceivedEventArgs> MessageReceived;
        public event EventHandler<ClientDisconnectedException> Disconnected;
        public event EventHandler<Exception> CallbackException;

        // the cancellation source (and token) that this client internally uses to signal disconnection
        readonly CancellationToken token;
        readonly CancellationTokenSource source;

        // the serialization context for this rtmp client
        readonly SerializationContext context;

        // the callback manager that handles completing invocation requests
        readonly TaskCallbackManager<uint, object> callbacks;

        // fn(message: RtmpMessage, chunk_stream_id: int) -> None
        //     queues a message to be written. this is assigned post-construction by `connectasync`.
        Action<RtmpMessage, int> queue;

        // the client id that was assigned to us by the remote peer. this is assigned post-construction by
        // `connectasync`, and may be null if no explicit client id was provided.
        string clientId;

        // counter for monotonically increasing invoke ids
        int invokeId;

        // true if this connection is no longer connected
        bool disconnected;

        // a tuple describing the cause of the disconnection. either value may be null.
        (string message, Exception inner) cause;

        RtmpClient(SerializationContext context)
        {
            this.context = context;
            callbacks = new TaskCallbackManager<uint, object>();
            source = new CancellationTokenSource();
            token = source.Token;
        }

        public void Dispose() =>
            CloseAsync(true).Wait();

        #region internal callbacks

        // `internalreceivesubscriptionvalue` will never throw an exception
        void InternalReceiveSubscriptionValue(string clientId, string subtopic, object body) =>
            WrapCallback(() => MessageReceived?.Invoke(this, new MessageReceivedEventArgs(clientId, subtopic, body)));

        // called internally by the readers and writers when an error that would invalidate this connection occurs.
        // `inner` may be null.
        void InternalCloseConnection(string reason, Exception inner)
        {
            Volatile.Write(ref cause.message, reason);
            Volatile.Write(ref cause.inner, inner);
            Volatile.Write(ref disconnected, true);

            source.Cancel();
            callbacks.SetExceptionForAll(DisconnectedException());

            WrapCallback(() => Disconnected?.Invoke(this, DisconnectedException()));
        }

        // this method will never throw an exception unless that exception will be fatal to this connection, and thus
        // the connection would be forced to close.
        void InternalReceiveEvent(RtmpMessage message)
        {
            switch (message)
            {
                case UserControlMessage u when u.EventType == UserControlMessage.Type.PingRequest:
                    queue(new UserControlMessage(UserControlMessage.Type.PingResponse, u.Values), 2);
                    break;

                case Invoke i:
                    var param = i.Arguments?.FirstOrDefault();

                    switch (i.MethodName)
                    {
                        case "_result":
                            // unwrap the flex wrapper object if it is present
                            var a = param as AcknowledgeMessage;

                            callbacks.SetResult(i.InvokeId, a?.Body ?? param);
                            break;

                        case "_error":
                            // try to unwrap common rtmp and flex error types, if we recognize any.
                            switch (param)
                            {
                                case string v:
                                    callbacks.SetException(i.InvokeId, new Exception(v));
                                    break;

                                case ErrorMessage e:
                                    callbacks.SetException(i.InvokeId, new InvocationException(e, e.FaultCode, e.FaultString, e.FaultDetail, e.RootCause, e.ExtendedData));
                                    break;

                                case AsObject o:
                                    object x;

                                    var code = o.TryGetValue("code", out x) && x is string q ? q : null;
                                    var description = o.TryGetValue("description", out x) && x is string r ? r : null;
                                    var cause = o.TryGetValue("cause", out x) && x is string s ? s : null;

                                    var extended = o.TryGetValue("ex", out x) || o.TryGetValue("extended", out x) ? x : null;

                                    callbacks.SetException(i.InvokeId, new InvocationException(o, code, description, cause, null, extended));
                                    break;

                                default:
                                    callbacks.SetException(i.InvokeId, new InvocationException());
                                    break;
                            }

                            break;

                        case "receive":
                            if (param is AsyncMessage c)
                            {
                                var id = c.ClientId;
                                var value = c.Headers.TryGetValue(AsyncMessageHeaders.Subtopic, out var x) ? x as string : null;
                                var body = c.Body;

                                InternalReceiveSubscriptionValue(id, value, body);
                            }

                            break;

                        case "onstatus":
                            Kon.Trace("received status");
                            break;

                        // [2016-12-26] workaround roslyn compiler bug that would cause the following default cause to
                        // cause a nullreferenceexception on the owning switch statement.
                        //     default:
                        //         Kon.DebugRun(() =>
                        //         {
                        //             Kon.Trace("unknown rtmp invoke method requested", new { method = i.MethodName, args = i.Arguments });
                        //             Debugger.Break();
                        //         });
                        //
                        //         break;

                        default:
                            break;
                    }

                    break;
            }
        }

        #endregion

        #region internal helper methods

        uint NextInvokeId() => (uint)Interlocked.Increment(ref invokeId);

        ClientDisconnectedException DisconnectedException() => new ClientDisconnectedException(cause.message, cause.inner);

        // calls a remote endpoint, sent along the specified chunk stream id, on message stream id #0
        Task<object> InternalCallAsync(Invoke request, int chunkStreamId)
        {
            if (disconnected) throw DisconnectedException();

            var task = callbacks.Create(request.InvokeId);

            queue(request, chunkStreamId);
            return task;
        }

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
            public int ChunkLength = 4192;
            public SerializationContext Context;

            // the below fields are optional, and may be null
            public string AppName;
            public string PageUrl;
            public string SwfUrl;

            public string FlashVersion = "WIN 21,0,0,174";

            public object[] Arguments;
            public RemoteCertificateValidationCallback Validate;
        }

        public static async Task<RtmpClient> ConnectAsync(Options options)
        {
            Check.NotNull(options.Url, options.Context);

            var url = options.Url;
            var chunkLength = options.ChunkLength;
            var context = options.Context;
            var validate = options.Validate ?? ((sender, certificate, chain, errors) => true);

            var uri = new Uri(url);
            var tcp = await TcpClientEx.ConnectAsync(uri.Host, uri.Port != -1 ? uri.Port : DefaultPort);
            var stream = await GetStreamAsync(uri, tcp.GetStream(), validate);

            await Handshake.GoAsync(stream);

            var client = new RtmpClient(context);
            var reader = new Reader(client, stream, context, client.token);
            var writer = new Writer(client, stream, context, client.token);

            reader.RunAsync().Forget();
            writer.RunAsync(chunkLength).Forget();

            client.queue = (message, chunkStreamId) => writer.QueueWrite(message, chunkStreamId);
            client.clientId = await RtmpConnectAsync(
                client: client,
                appName: options.AppName,
                pageUrl: options.PageUrl,
                swfUrl: options.SwfUrl,
                tcUrl: uri.ToString(),
                flashVersion: options.FlashVersion,
                arguments: options.Arguments);

            return client;
        }

        //internal RtmpClient(RtmpServer server, SerializationContext context, Stream stream, int chunkLength)
        //{
        //    this.context = context;
        //    callbacks = new TaskCallbackManager<uint, object>();
        //    source = new CancellationTokenSource();
        //    token = source.Token;
        //}

        internal static async Task<RtmpClient> ConnectAsync(RtmpServer server, SerializationContext context, Stream stream, int chunkLength)
        {
            await Handshake.GoAsyncServer(stream);

            var client = new RtmpClient(context);
            var writer = new Writer(client, stream, context, client.token);
            var reader = new Reader(client, stream, context, client.token);

            writer.RunAsync(chunkLength).Forget();
            reader.RunAsync().Forget();

            //client.queue = (message, chunkStreamId) => writer.QueueWrite(message, chunkStreamId);
            //client.clientId = await RtmpConnectAsync(
            //    client: client,
            //    appName: options.AppName,
            //    pageUrl: options.PageUrl,
            //    swfUrl: options.SwfUrl,
            //    tcUrl: uri.ToString(),
            //    flashVersion: options.FlashVersion,
            //    arguments: options.Arguments);

            return client;
        }

        static async Task<Stream> GetStreamAsync(Uri uri, Stream stream, RemoteCertificateValidationCallback validate)
        {
            CheckDebug.NotNull(uri, stream, validate);

            switch (uri.Scheme)
            {
                case "rtmp":
                    return stream;

                case "rtmps":
                    Check.NotNull(validate);

                    var ssl = new SslStream(stream, false, validate);
                    await ssl.AuthenticateAsClientAsync(uri.Host);

                    return ssl;

                default:
                    throw new ArgumentException($"scheme \"{uri.Scheme}\" must be one of rtmp:// or rtmps://");
            }
        }

        // attempts to perform an rtmp connect, and returns the client id assigned to us (if any - this may be null)
        static async Task<string> RtmpConnectAsync(RtmpClient client, string appName, string pageUrl, string swfUrl, string tcUrl, string flashVersion, object[] arguments)
        {
            var request = new InvokeAmf0
            {
                InvokeId = client.NextInvokeId(),
                MethodName = "connect",
                Arguments = arguments ?? EmptyArray<object>.Instance,
                Headers = new AsObject()
                {
                    { "app",            appName          },
                    { "audioCodecs",    3575             },
                    { "capabilities",   239              },
                    { "flashVer",       flashVersion     },
                    { "fpad",           false            },
                    { "objectEncoding", (double)3        }, // currently hard-coded to amf3
                    { "pageUrl",        pageUrl          },
                    { "swfUrl",         swfUrl           },
                    { "tcUrl",          tcUrl            },
                    { "videoCodecs",    252              },
                    { "videoFunction",  1                },
                },
            };

            var response = await client.InternalCallAsync(request, chunkStreamId: 3) as IDictionary<string, object>;

            return response != null && (response.TryGetValue("clientId", out var clientId) || response.TryGetValue("id", out clientId))
                ? clientId as string
                : null;
        }

        #endregion

        #region rtmpclient methods

        // some servers will fail if `destination` is null (but not if it's an empty string)
        const string NoDestination = "";

        public async Task<T> InvokeAsync<T>(string method, params object[] arguments) =>
            NanoTypeConverter.ConvertTo<T>(
                await InternalCallAsync(new InvokeAmf0() { MethodName = method, Arguments = arguments, InvokeId = NextInvokeId() }, 3));

        public async Task<T> InvokeAsync<T>(string endpoint, string destination, string method, params object[] arguments)
        {
            // this is a flex-style invoke, which *requires* amf3 encoding. fortunately, we always default to amf3
            // decoding and don't have a way to specify amf0 encoding in this iteration of rtmpclient, so no check is
            // needed.

            var request = new InvokeAmf3()
            {
                InvokeId = NextInvokeId(),
                MethodName = null,
                Arguments = new[]
               {
                    new RemotingMessage
                    {
                        ClientId    = Guid.NewGuid().ToString("D"),
                        Destination = destination,
                        Operation   = method,
                        Body        = arguments,
                        Headers     = new StaticDictionary<string, object>()
                        {
                            { FlexMessageHeaders.Endpoint,     endpoint },
                            { FlexMessageHeaders.FlexClientId, clientId ?? "nil" }
                        }
                    }
                }
            };

            return NanoTypeConverter.ConvertTo<T>(
                await InternalCallAsync(request, chunkStreamId: 3));
        }

        public async Task<bool> SubscribeAsync(string endpoint, string destination, string subtopic, string clientId)
        {
            Check.NotNull(endpoint, destination, subtopic, clientId);

            var message = new CommandMessage
            {
                ClientId = clientId,
                CorrelationId = null,
                Operation = CommandMessage.Operations.Subscribe,
                Destination = destination,
                Headers = new StaticDictionary<string, object>()
                {
                    { FlexMessageHeaders.Endpoint,     endpoint },
                    { FlexMessageHeaders.FlexClientId, clientId },
                    { AsyncMessageHeaders.Subtopic,    subtopic }
                }
            };

            return await InvokeAsync<string>(null, message) == "success";
        }

        public async Task<bool> UnsubscribeAsync(string endpoint, string destination, string subtopic, string clientId)
        {
            Check.NotNull(endpoint, destination, subtopic, clientId);

            var message = new CommandMessage
            {
                ClientId = clientId,
                CorrelationId = null,
                Operation = CommandMessage.Operations.Unsubscribe,
                Destination = destination,
                Headers = new KeyDictionary<string, object>()
                {
                    { FlexMessageHeaders.Endpoint,     endpoint },
                    { FlexMessageHeaders.FlexClientId, clientId },
                    { AsyncMessageHeaders.Subtopic,    subtopic }
                }
            };

            return await InvokeAsync<string>(null, message) == "success";
        }

        public async Task<bool> LoginAsync(string username, string password)
        {
            Check.NotNull(username, password);

            var credentials = $"{username}:{password}";
            var message = new CommandMessage
            {
                ClientId = clientId,
                Destination = NoDestination,
                Operation = CommandMessage.Operations.Login,
                Body = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)),
            };

            return await InvokeAsync<string>(null, message) == "success";
        }

        public Task LogoutAsync()
        {
            var message = new CommandMessage
            {
                ClientId = clientId,
                Destination = NoDestination,
                Operation = CommandMessage.Operations.Logout
            };

            return InvokeAsync<object>(null, message);
        }

        public Task PingAsync()
        {
            var message = new CommandMessage
            {
                ClientId = clientId,
                Destination = NoDestination,
                Operation = CommandMessage.Operations.ClientPing
            };

            return InvokeAsync<object>(null, message);
        }

        #endregion

        public Task CloseAsync(bool forced = false)
        {
            // currently we don't have a notion of gracefully closing a connection. all closes are hard force closes,
            // but we leave the possibility for properly implementing graceful closures in the future

            InternalCloseConnection("close-requested-by-user", null);

            return Task.CompletedTask;
        }

        #region Handshake

        static class Handshake
        {
            public static async Task GoAsync(Stream stream)
            {
                var c1 = await WriteC1Async(stream);
                var s1 = await ReadS1Async(stream);

                if (s1.zero != 0 || s1.three != 3)
                    throw InvalidHandshakeException();

                await WriteC2Async(stream, s1.time, s1.random);
                var s2 = await ReadS2Async(stream);

                if (c1.time != s2.echoTime || !ByteSpaceComparer.IsEqual(c1.random, s2.echoRandom))
                    throw InvalidHandshakeException();
            }

            public static async Task GoAsyncServer(Stream stream)
            {
                var s1 = await ReadS1Async(stream);
                var c1 = await WriteC1Async(stream);

                if (s1.zero != 0 || s1.three != 3)
                    throw InvalidHandshakeException();

                var s2 = await ReadS2Async(stream);
                await WriteC2Async(stream, s1.time, s1.random);

                if (c1.time != s2.echoTime || !ByteSpaceComparer.IsEqual(c1.random, s2.echoRandom))
                    throw InvalidHandshakeException();
            }

            static Exception InvalidHandshakeException() => throw new ArgumentException("remote server failed the rtmp handshake");

            // "c1" and "s1" are actually a concatenation of c0 and c1. as described in the spec, we can send and receive them together.
            const int C1Length = FrameLength + 1;
            const int FrameLength = RandomLength + 4 + 4;
            const int RandomLength = 1528;

            static readonly SerializationContext EmptyContext = new SerializationContext();

            static async Task<(uint time, Space<byte> random)> WriteC1Async(Stream stream)
            {
                var writer = new AmfWriter(new byte[C1Length], EmptyContext);
                var random = RandomEx.GetBytes(RandomLength);
                var time = Ts.CurrentTime;

                writer.WriteByte(3);       // rtmp version (constant 3) [c0]
                writer.WriteUInt32(time);  // time                      [c1]
                writer.WriteUInt32(0);     // zero                      [c1]
                writer.WriteBytes(random); // random bytes              [c1]

                await stream.WriteAsync(writer.Span);
                writer.Return();

                return (time, random);
            }

            static async Task<(uint three, uint time, uint zero, Space<byte> random)> ReadS1Async(Stream stream)
            {
                var buffer = await stream.ReadBytesAsync(C1Length);
                var reader = new AmfReader(buffer, EmptyContext);

                var three = reader.ReadByte();             // rtmp version (constant 3) [s0]
                var time = reader.ReadUInt32();           // time                      [s1]
                var zero = reader.ReadUInt32();           // zero                      [s1]
                var random = reader.ReadSpan(RandomLength); // random bytes              [s1]

                return (three, time, zero, random);
            }

            static async Task WriteC2Async(Stream stream, uint remoteTime, Space<byte> remoteRandom)
            {
                var time = Ts.CurrentTime;

                var writer = new AmfWriter(new byte[FrameLength], EmptyContext);
                writer.WriteUInt32(remoteTime);  // "time":        a copy of s1 time
                writer.WriteUInt32(time);        // "time2":       current local time
                writer.WriteBytes(remoteRandom); // "random echo": a copy of s1 random

                await stream.WriteAsync(writer.Span);
                writer.Return();
            }

            static async Task<(uint echoTime, Space<byte> echoRandom)> ReadS2Async(Stream stream)
            {
                var buffer = await stream.ReadBytesAsync(FrameLength);
                var reader = new AmfReader(buffer, EmptyContext);

                var time = reader.ReadUInt32();           // "time":        a copy of c1 time
                var ____ = reader.ReadUInt32();           // "time2":       current local time
                var echo = reader.ReadSpan(RandomLength); // "random echo": a copy of c1 random

                return (time, echo);
            }
        }

        #endregion

        #region Protocol

        // - each rtmp connection carries multiple chunk streams
        // - each chunk stream    carries multiple message streams
        // - each message stream  carries multiple messages

        // chunk format:
        //
        // +--------------+----------------+--------------------+------------+
        // | basic header | message header | extended timestamp | chunk data |
        // +--------------+----------------+--------------------+------------+
        // |                                                    |
        // |<------------------- chunk header ----------------->|

        static class BasicHeader
        {
            public static void WriteTo(AmfWriter writer, byte format, int chunkStreamId)
            {
                if (chunkStreamId <= 63)
                {
                    //  0 1 2 3 4 5 6 7
                    // +-+-+-+-+-+-+-+-+
                    // |fmt|   cs id   |
                    // +-+-+-+-+-+-+-+-+
                    writer.WriteByte((byte)((format << 6) + chunkStreamId));
                }
                else if (chunkStreamId <= 320)
                {
                    //  0             1
                    //  0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7
                    // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                    // |fmt|    0     |   cs id - 64   |
                    // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                    writer.WriteByte((byte)(format << 6));
                    writer.WriteByte((byte)(chunkStreamId - 64));
                }
                else
                {
                    //  0               1               3
                    //  0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7 0 1 2 3 4 5 6 7
                    // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                    // |fmt|     1    |           cs id - 64           |
                    // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
                    writer.WriteByte((byte)((format << 6) | 1));
                    writer.WriteByte((byte)((chunkStreamId - 64) & 0xff));
                    writer.WriteByte((byte)((chunkStreamId - 64) >> 8));
                }
            }

            public static bool ReadFrom(AmfReader reader, out int format, out int chunkStreamId)
            {
                format = 0;
                chunkStreamId = 0;

                if (!reader.HasLength(1))
                    return false;

                var b0 = reader.ReadByte();
                var v = b0 & 0x3f;
                var fmt = b0 >> 6;

                switch (v)
                {
                    // 2 byte variant
                    case 0:
                        if (!reader.HasLength(1))
                            return false;

                        format = fmt;
                        chunkStreamId = reader.ReadByte() + 64;
                        return true;

                    // 3 byte variant
                    case 1:
                        if (!reader.HasLength(2))
                            return false;

                        format = fmt;
                        chunkStreamId = reader.ReadByte() + reader.ReadByte() * 256 + 64;
                        return true;

                    // 1 byte variant
                    default:
                        format = fmt;
                        chunkStreamId = v;
                        return true;
                }
            }
        }

        static class MessageHeader
        {
            const uint ExtendedTimestampSentinel = 0xFFFFFF;

            public static void WriteTo(AmfWriter writer, Type type, ChunkStream.Snapshot stream)
            {
                var extendTs = stream.Timestamp >= ExtendedTimestampSentinel;
                var inlineTs = extendTs ? ExtendedTimestampSentinel : stream.Timestamp;

                switch (type)
                {
                    case Type.Type0:
                        writer.WriteUInt24((uint)inlineTs);
                        writer.WriteUInt24((uint)stream.MessageLength);
                        writer.WriteByte((byte)stream.ContentType);
                        writer.WriteLittleEndianInt(stream.MessageStreamId);
                        if (extendTs) writer.WriteUInt32(stream.Timestamp);
                        break;

                    case Type.Type1:
                        writer.WriteUInt24((uint)inlineTs);
                        writer.WriteUInt24((uint)stream.MessageLength);
                        writer.WriteByte((byte)stream.ContentType);
                        if (extendTs) writer.WriteUInt32(stream.Timestamp);
                        break;

                    case Type.Type2:
                        writer.WriteUInt24((uint)inlineTs);
                        if (extendTs) writer.WriteUInt32(stream.Timestamp);
                        break;

                    case Type.Type3:
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(type));
                }
            }

            public static bool ReadFrom(AmfReader reader, Type type, ChunkStream.Snapshot previous, out ChunkStream.Snapshot next)
            {
                next = default(ChunkStream.Snapshot);
                next.Ready = true;
                next.ChunkStreamId = previous.ChunkStreamId;

                if (!reader.HasLength(TypeByteLengths[(byte)type]))
                    return false;

                switch (type)
                {
                    case Type.Type0:
                        next.Timestamp = reader.ReadUInt24();
                        next.MessageLength = (int)reader.ReadUInt24();
                        next.ContentType = (PacketContentType)reader.ReadByte();
                        next.MessageStreamId = (uint)reader.ReadLittleEndianInt();

                        return MaybeReadExtraTimestamp(ref next.Timestamp);

                    case Type.Type1:
                        next.Timestamp = reader.ReadUInt24();
                        next.MessageLength = (int)reader.ReadUInt24();
                        next.ContentType = (PacketContentType)reader.ReadByte();

                        next.MessageStreamId = previous.MessageStreamId;
                        return MaybeReadExtraTimestamp(ref next.Timestamp);

                    case Type.Type2:
                        next.Timestamp = reader.ReadUInt24();

                        next.MessageLength = previous.MessageLength;
                        next.ContentType = previous.ContentType;
                        next.MessageStreamId = previous.MessageStreamId;
                        return MaybeReadExtraTimestamp(ref next.Timestamp);

                    case Type.Type3:
                        next.Timestamp = previous.Timestamp;
                        next.MessageLength = previous.MessageLength;
                        next.ContentType = previous.ContentType;
                        next.MessageStreamId = previous.MessageStreamId;
                        return true;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(type), "unknown type");
                }

                bool MaybeReadExtraTimestamp(ref uint timestamp)
                {
                    if (timestamp != ExtendedTimestampSentinel)
                        return true;

                    if (!reader.HasLength(4))
                        return false;

                    timestamp = (uint)reader.ReadInt32();
                    return true;
                }
            }

            // byte lengths for the message headers. if timestamp indicates an extended timestamp, then add an extra 4
            // bytes to this value.
            static readonly int[] TypeByteLengths = { 11, 7, 3, 0 };

            public enum Type : byte
            {
                // all fields are included
                Type0 = 0,

                // timestamp delta + message length + message type id only. assumes the same message stream id as previous.
                Type1 = 1,

                // timestamp delta only. assumes the same message stream id and length as previous. ideal for
                // constant-sized media.
                Type2 = 2,

                // no values. assumes the same values (including timestamp) as previous.
                Type3 = 3
            }
        }

        static class ChunkStream
        {
            public static void WriteTo(AmfWriter writer, Snapshot previous, Snapshot next, int chunkLength, Space<byte> message)
            {
                Kon.Assert(
                    !previous.Ready || previous.ChunkStreamId == next.ChunkStreamId,
                    "previous and next describe two different chunk streams");

                Kon.Assert(
                    next.MessageLength == message.Length,
                    "mismatch between reported message length and actual message length");

                // we don't write zero-length packets, and as state for `next` and `previous` won't match what our peer
                // sees if we pass a zero-length message here. zero-length sends should be filtered out at a higher level.
                Kon.Assert(
                    next.MessageLength != 0,
                    "message length cannot be zero");


                var header = GetInitialHeaderType();

                for (var i = 0; i < next.MessageLength; i += chunkLength)
                {
                    if (i == 0)
                    {
                        BasicHeader.WriteTo(writer, (byte)header, next.ChunkStreamId);
                        MessageHeader.WriteTo(writer, header, next);
                    }
                    else
                    {
                        BasicHeader.WriteTo(writer, (byte)MessageHeader.Type.Type3, next.ChunkStreamId);
                    }

                    var count = Math.Min(chunkLength, next.MessageLength - i);
                    var slice = message.Slice(i, count);

                    writer.WriteBytes(slice);
                }


                MessageHeader.Type GetInitialHeaderType()
                {
                    if (!previous.Ready || next.MessageStreamId != previous.MessageStreamId)
                        return MessageHeader.Type.Type0;

                    if (next.MessageLength != previous.MessageLength || next.ContentType != previous.ContentType)
                        return MessageHeader.Type.Type1;

                    if (next.Timestamp != previous.Timestamp)
                        return MessageHeader.Type.Type2;

                    return MessageHeader.Type.Type3;
                }
            }

            // part 1: read from the chunk stream, returning true if enough data is here. you must take the value
            // returned at `chunkStreamId`, find the chunk stream snapshot associated with that chunk stream and
            // pass it to the second stage (part2) along with the opaque value.
            public static bool ReadFrom1(AmfReader reader, out int chunkStreamId, out MessageHeader.Type opaque)
            {
                if (BasicHeader.ReadFrom(reader, out var format, out var streamId))
                {
                    opaque = (MessageHeader.Type)format;
                    chunkStreamId = streamId;
                    return true;
                }
                else
                {
                    opaque = default(MessageHeader.Type);
                    chunkStreamId = default(int);
                    return false;
                }
            }

            public static bool ReadFrom2(AmfReader reader, Snapshot previous, MessageHeader.Type opaque, out Snapshot next)
            {
                return MessageHeader.ReadFrom(reader, opaque, previous, out next);
            }


            // a point-in-time snapshot of some chunk stream, including the currently packet in transit
            public struct Snapshot
            {
                // if false, the value of this object is semantically equivalent to `null`
                public bool Ready;


                // * * * * * * * * * *
                // chunk stream values

                // the "chunk stream id" for this chunk stream
                public int ChunkStreamId;

                // the current timestamp for this chunk stream
                public uint Timestamp;


                // * * * * * * * * * *
                // message stream values

                // message stream id
                public uint MessageStreamId;


                // * * * * * * * * * *
                // current message values

                // the "message type" for the current packet
                public PacketContentType ContentType;

                // size of the last chunk, in bytes, of the message currently being transmitted
                public int MessageLength;


                // * * * * * * * * * *
                // methods

                public Snapshot Clone() => (Snapshot)MemberwiseClone();
            }
        }

        #endregion

        #region Reader

        class Reader
        {
            const int DefaultBufferLength = 4192;

            readonly RtmpClient owner;
            readonly CancellationToken token;
            readonly AsyncAutoResetEvent reset;
            readonly ConcurrentQueue<Builder> queue;
            readonly Stream stream;
            readonly SerializationContext context;

            // an intermediate processing buffer of data read from `stream`. this is always at least `chunkLength` bytes large.
            byte[] buffer;

            // the number of bytes available in `buffer`
            int available;

            // all current chunk streams, keyed by chunk stream id
            readonly IDictionary<int, ChunkStream.Snapshot> streams;

            // all current message streams
            readonly IDictionary<(int chunkStreamId, uint messageStreamId), Builder> messages;

            // the current chunk length for this stream. by the rtmp spec, this defaults to 128 bytes.
            int chunkLength = 128;

            // the current acknowledgement window for this stream. after we receive more than `acknowledgementLength`
            // bytes, we must send an acknowledgement back to the remote peer.
            int acknowledgementLength = 0;

            // tracking counter for the number of bytes we've received, in order to send acknowledgements.
            long readTotal = 0;
            long readSinceLastAcknowledgement = 0;

            // cached amfreaders so that we do not pay the cost of allocation on every payload or message. these are
            // exclusively owned by their respective methods.
            readonly AmfReader __readFramesFromBufferReader;
            readonly AmfReader __readSingleFrameReader;


            public Reader(RtmpClient owner, Stream stream, SerializationContext context, CancellationToken cancellationToken)
            {
                this.owner = owner;
                this.stream = stream;
                this.context = context;
                this.token = cancellationToken;

                this.reset = new AsyncAutoResetEvent();
                this.queue = new ConcurrentQueue<Builder>();
                this.streams = new KeyDictionary<int, ChunkStream.Snapshot>();
                this.messages = new KeyDictionary<(int, uint), Builder>();

                this.buffer = new byte[DefaultBufferLength];
                this.available = 0;

                this.__readSingleFrameReader = new AmfReader(context);
                this.__readFramesFromBufferReader = new AmfReader(context);
            }


            // this method must only be called once
            public async Task RunAsync()
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await ReadOnceAsync();
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        Kon.DebugException("rtmpclient::reader encountered an error", e);

                        owner.InternalCloseConnection("reader-exception", e);
                        return;
                    }
                }
            }

            async Task ReadOnceAsync()
            {
                // read a bunch of bytes from the remote server
                await ReadFromStreamAsync();

                // send an acknowledgement if we need it
                MaybeSendAcknowledgements();

                // then, read all frames, chunks and messages that are complete within it
                ReadFramesFromBuffer();
            }

            void MaybeSendAcknowledgements()
            {
                if (acknowledgementLength == 0)
                    return;

                while (readSinceLastAcknowledgement >= acknowledgementLength)
                {
                    readSinceLastAcknowledgement -= acknowledgementLength;

                    var ack = new Acknowledgement((uint)(readTotal - readSinceLastAcknowledgement));
                    owner.queue(ack, 2);
                }
            }

            async Task ReadFromStreamAsync()
            {
                var read = await stream.ReadAsync(new Space<byte>(buffer, available), token);

                if (read == 0)
                    throw new EndOfStreamException("rtmp connection was closed by the remote peer");

                available += read;
                readTotal += read;
                readSinceLastAcknowledgement += read;
            }

            unsafe void ReadFramesFromBuffer()
            {
                // the index that we have successfully read into `buffer`. that is, all bytes before this have been
                // successfully read and processed into a valid packet.
                var index = 0;
                var reader = __readFramesFromBufferReader;

                // read as many frames as we can from the buffer
                while (index < available)
                {
                    reader.Rebind(buffer, index, available - index);

                    if (!ReadSingleFrame(reader))
                        break;

                    index += reader.Position;
                }

                // then, shift unread bytes back to the start of the array
                if (index > 0)
                {
                    if (available != index)
                    {
                        fixed (byte* pSource = &buffer[index])
                        fixed (byte* pDestination = &buffer[0])
                        {
                            Buffer.MemoryCopy(
                                source: pSource,
                                destination: pDestination,
                                destinationSizeInBytes: buffer.Length,
                                sourceBytesToCopy: available - index);
                        }
                    }

                    available -= index;
                }
            }

            bool ReadSingleFrame(AmfReader reader)
            {
                if (!ChunkStream.ReadFrom1(reader, out var streamId, out var opaque))
                    return false;

                if (!streams.TryGetValue(streamId, out var previous))
                    previous = new ChunkStream.Snapshot() { ChunkStreamId = streamId };

                if (!ChunkStream.ReadFrom2(reader, previous, opaque, out var next))
                    return false;

                streams[streamId] = next;
                context.RequestReadAllocation(next.MessageLength);

                var key = (next.ChunkStreamId, next.MessageStreamId);
                var builder = messages.TryGetValue(key, out var packet) ? packet : messages[key] = new Builder(next.MessageLength);
                var length = Math.Min(chunkLength, builder.Remaining);

                if (!reader.HasLength(length))
                    return false;

                builder.AddData(
                    reader.ReadSpan(length));

                if (builder.Current == builder.Length)
                {
                    messages.Remove(key);

                    using (builder)
                    {
                        var dereader = __readSingleFrameReader;
                        dereader.Rebind(builder.Span);

                        var message = Deserialize(next.ContentType, dereader);
                        DispatchMessage(message);
                    }
                }

                return true;
            }

            void DispatchMessage(RtmpMessage message)
            {
                switch (message)
                {
                    case ChunkLength chunk:
                        Kon.Trace("received: chunk-length", new { length = chunk.Length });

                        if (chunk.Length < 0)
                            throw new ArgumentException("invalid chunk length");

                        context.RequestReadAllocation(chunk.Length);
                        chunkLength = chunk.Length;
                        break;

                    case WindowAcknowledgementSize acknowledgement:
                        if (acknowledgement.Count < 0)
                            throw new ArgumentException("invalid acknowledgement window length");

                        acknowledgementLength = acknowledgement.Count;
                        break;

                    case Abort abort:
                        Kon.Trace("received: abort", new { chunk = abort.ChunkStreamId });

                        // delete the chunk stream
                        streams.Remove(abort.ChunkStreamId);

                        // then, delete all message streams associated with that chunk stream
                        foreach (var (key, builder) in messages.FilterArray(x => x.Key.chunkStreamId == abort.ChunkStreamId))
                        {
                            messages.Remove(key);
                            builder.Dispose();
                        }

                        break;

                    default:
                        owner.InternalReceiveEvent(message);
                        break;
                }
            }

            // todo: refactor
            RtmpMessage Deserialize(PacketContentType contentType, AmfReader r)
            {
                // (this comment must be kept in sync at rtmpclient.reader.cs and rtmpclient.writer.cs)
                //
                // unsupported type summary:
                //
                // - aggregate:      we have never encountered this packet in the wild
                // - shared objects: we have not found a use case for this
                // - data commands:  we have not found a use case for this, though it should be extremely easy to
                //                       support. it's just a one-way equivalent of command (invoke). that is, we don't
                //                       generate an invoke id for it, and it does not contain headers. other than that,
                //                       they're identical. we can use existing logic and add if statements to surround
                //                       writing the invokeid + headers if needed.

                switch (contentType)
                {
                    case PacketContentType.SetChunkSize:
                        return new ChunkLength(
                            length: r.ReadInt32());

                    case PacketContentType.AbortMessage:
                        return new Abort(
                            chunkStreamId: r.ReadInt32());

                    case PacketContentType.Acknowledgement:
                        return new Acknowledgement(
                            read: r.ReadUInt32());

                    case PacketContentType.UserControlMessage:
                        var type = r.ReadUInt16();
                        var values = EnumerableEx.Range(r.Remaining / 4, r.ReadUInt32);

                        return new UserControlMessage(
                            type: (UserControlMessage.Type)type,
                            values: values);

                    case PacketContentType.WindowAcknowledgementSize:
                        return new WindowAcknowledgementSize(
                            count: r.ReadInt32());

                    case PacketContentType.SetPeerBandwith:
                        return new PeerBandwidth(
                            acknowledgementWindowSize: r.ReadInt32(),
                            type: r.ReadByte());

                    case PacketContentType.Audio:
                        return new AudioData(
                            r.ReadBytes(r.Remaining));

                    case PacketContentType.Video:
                        return new VideoData(
                            r.ReadBytes(r.Remaining));

                    case PacketContentType.DataAmf0:
                        throw NotSupportedException("data-amf0");

                    case PacketContentType.SharedObjectAmf0:
                        throw NotSupportedException("sharedobject-amf0");

                    case PacketContentType.CommandAmf0:
                        return ReadCommand(ObjectEncoding.Amf0, contentType, r);

                    case PacketContentType.DataAmf3:
                        throw NotSupportedException("data-amf3");

                    case PacketContentType.SharedObjectAmf3:
                        throw NotSupportedException("sharedobject-amf0");

                    case PacketContentType.CommandAmf3:
                        var encoding = (ObjectEncoding)r.ReadByte();
                        return ReadCommand(encoding, contentType, r);

                    case PacketContentType.Aggregate:
                        throw NotSupportedException("aggregate");

                    default:
                        throw NotSupportedException($"unknown ({contentType})");
                }
            }

            static RtmpMessage ReadCommand(ObjectEncoding encoding, PacketContentType type, AmfReader r)
            {
                var name = (string)r.ReadAmfObject(encoding);
                var invokeId = Convert.ToUInt32(r.ReadAmfObject(encoding));
                var headers = r.ReadAmfObject(encoding);

                var args = new List<object>();

                while (r.Remaining > 0)
                    args.Add(r.ReadAmfObject(encoding));

                return new Invoke(type) { MethodName = name, Arguments = args.ToArray(), InvokeId = invokeId, Headers = headers };
            }

            class Builder : IDisposable
            {
                public byte[] Buffer;
                public int Current;
                public int Length;
                public int Remaining => Length - Current;
                public Space<byte> Span => new Space<byte>(Buffer, 0, Length);

                public Builder(int length)
                {
                    Buffer = ArrayPool<byte>.Shared.Rent(length);
                    Current = 0;
                    Length = length;
                }

                public void Dispose()
                {
                    ArrayPool<byte>.Shared.Return(Buffer);
                }

                public void AddData(Space<byte> source)
                {
                    if (source.Length > Remaining)
                        throw new ArgumentException("source span would overflow destination");

                    var destination = new Space<byte>(Buffer, Current, source.Length);

                    source.CopyTo(destination);
                    Current += source.Length;
                }
            }

            static Exception NotSupportedException(string type) => new NotSupportedException($"packets with the type \"{type}\" aren't supported right now.");
        }

        #endregion

        #region Writer

        class Writer
        {
            readonly RtmpClient owner;
            readonly CancellationToken token;
            readonly AsyncAutoResetEvent reset;
            readonly ConcurrentQueue<Packet> queue;
            readonly Stream stream;
            readonly SerializationContext context;

            // all current chunk streams, keyed by chunk stream id. though the rtmp spec allows for many message
            // streams in each chunk stream, we only ever use one message stream for each chunk stream (no qos is
            // currently implemented).
            readonly IDictionary<int, ChunkStream.Snapshot> streams;

            // the current chunk length for this upstream connection. by the rtmp spec, this defaults to 128 bytes.
            int chunkLength = 128;


            public Writer(RtmpClient owner, Stream stream, SerializationContext context, CancellationToken cancellationToken)
            {
                this.owner = owner;
                this.stream = stream;
                this.context = context;

                this.token = cancellationToken;
                this.reset = new AsyncAutoResetEvent();
                this.queue = new ConcurrentQueue<Packet>();
                this.streams = new KeyDictionary<int, ChunkStream.Snapshot>();
            }


            public void QueueWrite(RtmpMessage message, int chunkStreamId, bool external = true)
            {
                // we save ourselves from synchronizing on chunk length because we never modify it post-initialization
                if (external && message is ChunkLength)
                    throw new InvalidOperationException("cannot modify chunk length after stream has begun");

                queue.Enqueue(new Packet(chunkStreamId, message.ContentType, Serialize(message)));
                reset.Set();
            }


            // this method must only be called once
            public async Task RunAsync(int chunkLength)
            {
                if (chunkLength != 0)
                {
                    QueueWrite(new ChunkLength(chunkLength), chunkStreamId: 2, external: false);
                    this.chunkLength = chunkLength;
                }

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await WriteOnceAsync();
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                    catch (Exception e)
                    {
                        Kon.DebugException("rtmpclient::writer encountered an error", e);

                        owner.InternalCloseConnection("writer-exception", e);
                        return;
                    }
                }
            }

            async Task WriteOnceAsync()
            {
                await reset.WaitAsync();

                while (!token.IsCancellationRequested && queue.TryDequeue(out var packet))
                {
                    // quickly estimate the maximum required length for this message. our estimation is as follows:
                    //
                    //     - [payload]
                    //         - take the message length, and add the chunk + message headers.
                    //
                    //     - [chunk headers]
                    //         - all chunk headers begin with a 0-3 byte header, indicating chunk stream id + message header
                    //             format.
                    //         - the one byte variant can encode chunk stream ids up to and including #63. we don't expect to
                    //             encode that many streams right now (unless a user library wants it), so we can assume 1 byte
                    //             chunk headers for now.
                    //~
                    //     - [message headers]
                    //         - the first message header must be a type 0 (new) header, which is 11 bytes large.
                    //         - all further message headers can be a type 3 (continuation) header, which is 0 bytes large.
                    //
                    //     - [total]
                    //         - message_length + chunk_count * 1 + 11
                    //
                    var packetLength = packet.Span.Length;
                    var chunkCount = packetLength / chunkLength + 1;
                    var estimatedMaxLength = packetLength + chunkCount + 11;
                    var writer = new AmfWriter(estimatedMaxLength, context);

                    var previous = streams.TryGetValue(packet.StreamId, out var value) ? value : default(ChunkStream.Snapshot);
                    var next = previous.Clone();

                    next.Ready = true;
                    next.ContentType = packet.Type;
                    next.ChunkStreamId = packet.StreamId;
                    next.MessageStreamId = 0;
                    next.MessageLength = packetLength;
                    next.Timestamp = Ts.CurrentTime;

                    streams[packet.StreamId] = next;
                    ChunkStream.WriteTo(writer, previous, next, chunkLength, packet.Span);

                    await stream.WriteAsync(writer.Span, token);
                    writer.Return();
                }
            }

            Space<byte> Serialize(RtmpMessage message)
            {
                // (this comment must be kept in sync at rtmpclient.reader.cs and rtmpclient.writer.cs)
                //
                // unsupported type summary:
                //
                // - aggregate:      we have never encountered this packet in the wild
                // - shared objects: we have not found a use case for this
                // - data commands:  we have not found a use case for this, though it should be extremely easy to
                //                       support. it's just a one-way equivalent of command (invoke). that is, we don't
                //                       generate an invoke id for it, and it does not contain headers. other than that,
                //                       they're identical. we can use existing logic and add if statements to surround
                //                       writing the invokeid + headers if needed.

                const int WriteInitialBufferLength = 4192;

                var w = new AmfWriter(WriteInitialBufferLength, context);

                switch (message.ContentType)
                {
                    case PacketContentType.SetChunkSize:
                        var a = (ChunkLength)message;
                        w.WriteInt32(a.Length);
                        break;

                    case PacketContentType.AbortMessage:
                        var b = (Abort)message;
                        w.WriteInt32(b.ChunkStreamId);
                        break;

                    case PacketContentType.Acknowledgement:
                        var c = (Acknowledgement)message;
                        w.WriteUInt32(c.TotalRead);
                        break;

                    case PacketContentType.UserControlMessage:
                        var d = (UserControlMessage)message;

                        w.WriteUInt16((ushort)d.EventType);
                        foreach (var value in d.Values)
                            w.WriteUInt32(value);

                        break;

                    case PacketContentType.WindowAcknowledgementSize:
                        var e = (WindowAcknowledgementSize)message;
                        w.WriteInt32(e.Count);
                        break;

                    case PacketContentType.SetPeerBandwith:
                        var f = (PeerBandwidth)message;
                        w.WriteInt32(f.AckWindowSize);
                        w.WriteByte((byte)f.LimitType);
                        break;

                    case PacketContentType.Audio:
                    case PacketContentType.Video:
                        var g = (ByteData)message;
                        w.WriteBytes(g.Data);
                        break;

                    case PacketContentType.DataAmf0:
                        throw NotSupportedException("data-amf0");

                    case PacketContentType.SharedObjectAmf0:
                        throw NotSupportedException("sharedobject-amf0");

                    case PacketContentType.CommandAmf0:
                        WriteCommand(ObjectEncoding.Amf0, w, message);
                        break;

                    case PacketContentType.DataAmf3:
                        throw NotSupportedException("data-amf3");

                    case PacketContentType.SharedObjectAmf3:
                        throw NotSupportedException("sharedobject-amf0");

                    case PacketContentType.CommandAmf3:
                        // first byte is an encoding specifier byte.
                        //     see `writecommand` comment below: specify amf0 object encoding and elevate into amf3.
                        w.WriteByte((byte)ObjectEncoding.Amf0);
                        WriteCommand(ObjectEncoding.Amf3, w, message);
                        break;

                    case PacketContentType.Aggregate:
                        throw NotSupportedException("aggregate");

                    default:
                        throw NotSupportedException($"unknown ({message.ContentType})");
                }

                return w.Span;
            }

            // most rtmp servers we are interested in only support amf3 via an amf0 envelope
            static void WriteCommand(ObjectEncoding encoding, AmfWriter w, RtmpMessage message)
            {
                switch (message)
                {
                    case Notify notify:
                        w.WriteBoxedAmf0Object(encoding, notify.Data);
                        break;

                    case Invoke request:
                        w.WriteBoxedAmf0Object(encoding, request.MethodName);
                        w.WriteBoxedAmf0Object(encoding, request.InvokeId);
                        w.WriteBoxedAmf0Object(encoding, request.Headers);

                        foreach (var arg in request.Arguments ?? EmptyArray<object>.Instance)
                            w.WriteBoxedAmf0Object(encoding, arg);

                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            struct Packet
            {
                // this is the chunk stream id. as above, we only ever use one message stream per chunk stream.
                public int StreamId;
                public Space<byte> Span;
                public PacketContentType Type;

                public Packet(int streamId, PacketContentType type, Space<byte> span)
                {
                    StreamId = streamId;
                    Type = type;
                    Span = span;
                }
            }

            static Exception NotSupportedException(string type) => new NotSupportedException($"packets with the type \"{type}\" aren't supported right now.");
        }

        #endregion
    }

    #endregion
}
