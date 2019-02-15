//#define SOAK
using Rtmp;
using Rtmp.Net;
using System;
using System.Diagnostics;
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
        var key = "";
        var context = new SerializationContext();
        var options = new RtmpClient.Options()
        {
            // required parameters:
            //Url = "rtmp://ingress.winky.com:1234",
            Url = $"rtmp://live.twitch.tv/app/{key}",
            Context = context,

            // optional parameters:
            AppName = "demo-app",                                  // optional app name, passed to the remote server during connect.
            PageUrl = "https://example.com/rtmpsharp/demo.html",   // optional page url, passed to the remote server during connect.
            //SwfUrl = "",                                          // optional swf url, passed to the remote server during connect.
            //FlashVersion = "WIN 21,0,0,174",                            // optional flash version, paased to the remote server during connect.

            //ChunkLength = 4192,                                        // optional outgoing rtmp chunk length.
            //Validate = (sender, certificate, chain, errors) => true // optional certificate validation callback. used only in tls connections.
        };

        using (var client = await RtmpClient.ConnectAsync(options))
        {
            //var exists = await client.InvokeAsync<bool>("storage", "exists", new { name = "music.pdf" });
        }
    }

    static async Task test_connect2()
    {
        // route ADD 52.223.225.248 MASK 255.255.255.248 127.0.0.1
        var context = new SerializationContext();
        var options = new RtmpServer.Options()
        {
            // required parameters:
            //Url = "rtmp://live-sea.twitch.tv/app/key",
            //Url = "rtmp://anyv4",
            Context = context,
        };

        using (var server = await RtmpServer.ConnectAsync(options))
            server.Wait();
    }

    static void RUN_TEST(string name, Action test_function)
    {
        Console.Write($"{name}\n");
        //if (!InitializeRtmp())
        //{
        //    Console.Write("error: failed to initialize rtmp\n");
        //    Environment.Exit(1);
        //}
        test_function();
        //ShutdownRtmp();
    }

    static void RUN_TESTASYNC(string name, Func<Task> test_function)
    {
        Console.Write($"{name}\n");
        //if (!InitializeRtmp())
        //{
        //    Console.Write("error: failed to initialize rtmp\n");
        //    Environment.Exit(1);
        //}
        test_function().Wait();
        //ShutdownRtmp();
    }

#if SOAK
    static volatile bool quit = false;

    static void interrupt_handler(object sender, ConsoleCancelEventArgs e) { quit = true; e.Cancel = true; }
#endif

    static int Main(string[] args)
    {
        Console.Write("\n");

        //log_level(LOG_LEVEL_INFO);

#if SOAK
        Console.CancelKeyPress += interrupt_handler;

        var iter = 0;
        while (true)
#endif
        {
            Console.Write("\n[rtmp]\n\n");

            //RUN_TEST("test_endian", test_endian);
            //RUN_TESTASYNC("test_connect", test_connect);
            RUN_TESTASYNC("test_connect2", test_connect2);

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