using System.Text;
using ChipSharp.Tests.Sdcc;
using ChipSharp.Tests.Stm8;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// The stack checks on the STM8S103 with its runtime: a recursion with no end stops with "Stack overflow." on UART1, as
/// .NET says it, and the failure stop (the break chipsim takes as a failure), rather than writing over the variables;
/// one that fits runs to its end.
/// </summary>
public sealed class SdccStackCheckTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private const string Recursion = """
        using System;

        static class Program
        {
            static int Depth(int n, int limit) => n == limit ? n : Depth(n + 1, limit) + 0;

            static void Main()
            {
                Console.WriteLine(Depth(0, 10));
                Console.WriteLine(Depth(0, 100000));
            }
        }
        """;

    [SdccFact]
    public void A_recursion_with_no_room_left_stops_with_dotnets_words_and_the_failure_stop()
    {
        var program = Stm8sProgram.Build(_scratch, "Recursion", Recursion);

        var run = program.Run(10_000);

        Assert.Equal(3, run.ExitCode);
        Assert.Equal("10\r\nStack overflow.\r\n", Encoding.ASCII.GetString(run.Serial));
    }
}
