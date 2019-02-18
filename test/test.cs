//#define SOAK
using Rtmp;
using Rtmp.Net;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

public static class test
{
    static void CheckHandler(string condition, string function, string file, int line)
    {
        Console.Write($"check failed: ( {condition} ), function {function}, file {file}, line {line}\n");
        Debugger.Break();
        Environment.Exit(1);
    }

    [DebuggerStepThrough, Conditional("DEBUG")]
    public static void check(bool condition)
    {
        if (!condition)
        {
            var stackFrame = new StackTrace().GetFrame(1);
            CheckHandler(null, stackFrame.GetMethod().Name, stackFrame.GetFileName(), stackFrame.GetFileLineNumber());
        }
    }

    static void test_endian()
    {
        const ulong value = 0x11223344UL;

        var bytes = BitConverter.GetBytes(value);
        check(bytes[0] == 0x44);
        check(bytes[1] == 0x33);
        check(bytes[2] == 0x22);
        check(bytes[3] == 0x11);
    }

    static async Task test_connect()
    {
        var key = "live_48269693_OhemQ7S4gQy7y27byu47TRERxyzgLl";
        var options = new RtmpClient.Options
        {
            // required parameters:
            Url = $"rtmp://live.twitch.tv/app/{key}",
            Context = new SerializationContext(),

            // optional parameters:
            AppName = "app",                            // optional app name, passed to the remote server during connect.
            //PageUrl = "",                             // optional page url, passed to the remote server during connect.
            //SwfUrl = "",                              // optional swf url, passed to the remote server during connect.
            //FlashVersion = "WIN 21,0,0,174",          // optional flash version, paased to the remote server during connect.

            //ChunkLength = 4192,                       // optional outgoing rtmp chunk length.
            //Validate = (sender, certificate, chain, errors) => true // optional certificate validation callback. used only in tls connections.
        };
        using (var client = await RtmpClient.ConnectAsync(options))
        {
            //var exists = await client.InvokeAsync<bool>("storage", "exists", new { name = "music.pdf" });
            var createStream = await client.Connection.CreateStreamAsync();
            await createStream.PublishAndWaitAsync(key, "live");
        }
    }

    static async Task test_server()
    {
        var context = new SerializationContext();
        var options = new RtmpServer.Options()
        {
            // required parameters:
            Context = context,
        };

        using (var server = await RtmpServer.ConnectAsync(options))
            server.Wait();
    }

    static void RUN_TEST(string name, Action test_function) { Console.Write($"{name}\n"); test_function(); }
    static void RUN_TESTASYNC(string name, Func<Task> test_function) { Console.Write($"{name}\n"); test_function().Wait(); }

#if SOAK
    static volatile bool quit = false;

    static void interrupt_handler(object sender, ConsoleCancelEventArgs e) { quit = true; e.Cancel = true; }
#endif

    static int Main(string[] args)
    {
        Console.Write("\n");

#if SOAK
        Console.CancelKeyPress += interrupt_handler;

        var iter = 0;
        while (true)
#endif
        {
            Console.Write("\n[rtmp]\n\n");

            //RUN_TEST("test_endian", test_endian);
            RUN_TESTASYNC("test_connect", test_connect);
            //RUN_TESTASYNC("test_server", test_server);

#if SOAK
            if (quit)
                break;
            iter++;
            for (var j = 0; j < iter % 10; ++j)
                Console.Write(".");
            Console.Write("\n");
#endif
        }

#if SOAK
        if (quit)
            Console.Write("\n");
#else
        Console.Write("\n*** ALL TESTS PASS ***\n\n");
#endif

        return 0;
    }
}