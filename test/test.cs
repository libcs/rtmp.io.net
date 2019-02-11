//#define SOAK
using Rtmp;
using System;
using System.Diagnostics;

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

            RUN_TEST("test_endian", test_endian);

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