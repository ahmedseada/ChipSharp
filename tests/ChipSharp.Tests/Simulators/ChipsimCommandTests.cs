using System.Diagnostics;
using System.Text;
using ChipSharp.Simulators;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The chipsim command: what it prints (the lines of tools/avr-sim, with the simulated time), its options, and its exit
/// codes: 0 when the program ran, 1 for an image that is not valid, 2 for wrong arguments, 3 when the program went
/// wrong. The images are written here, a few hand-assembled instructions.
/// </summary>
public sealed class ChipsimCommandTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public ChipsimCommandTests() => Directory.CreateDirectory(_scratch);

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    /// <summary>
    /// An STM8S103 program: the reset vector to 0x8004; PB5 an output (low); UART1 at 2 MHz / 208 sending "hi\n" (each
    /// byte once TXE is set), until TC; then sim, halt.
    /// </summary>
    private string Stm8Hello() => Image("hello.ihx",
        (0x8000, [0x82, 0x00, 0x80, 0x04]),
        (0x8004,
        [
            0x35, 0x20, 0x50, 0x07,                     // mov 0x5007, #0x20     PB_DDR
            0x35, 0x0D, 0x52, 0x32,                     // mov 0x5232, #0x0d     UART1_BRR1
            0x35, 0x08, 0x52, 0x35,                     // mov 0x5235, #0x08     UART1_CR2: TEN
            0x35, (byte)'h', 0x52, 0x31,                // mov 0x5231, #'h'
            0x72, 0x0F, 0x52, 0x30, 0xFB,               // btjf 0x5230, #7, .    TXE
            0x35, (byte)'i', 0x52, 0x31,
            0x72, 0x0F, 0x52, 0x30, 0xFB,
            0x35, 0x0A, 0x52, 0x31,
            0x72, 0x0D, 0x52, 0x30, 0xFB,               // btjf 0x5230, #6, .    TC
            0x9B, 0x8E,                                 // sim; halt
        ]));

    /// <summary>A PFS154 program (byte address 2w for word w): IHRC / 8, PA4 an output, high; stopsys; then nothing.</summary>
    private string PfsStop() => Image("stop.ihx", (0, Words(0x2F3C, 0x0183, 0x2F10, 0x0191, 0x0190, 0x0076)));

    [Fact]
    public void The_command_prints_pin_changes_and_serial_lines_with_their_time_and_writes_the_bytes_sent()
    {
        var bytes = Path.Combine(_scratch, "uart1.bin");

        var (exitCode, output, errors) = Command("stm8s103", Stm8Hello(), "--ms", "100", "--uart1", bytes);

        Assert.True(exitCode == 0, output + errors);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal($"booting {Path.Combine(_scratch, "hello.ihx")} on the STM8S103 (45 bytes of program memory), running 100 ms " +
            "of simulated time", lines[0]);
        Assert.Matches(@"^\[\s+0\.\d{3} ms\] PB5 low$", lines[1]);
        Assert.Matches(@"^\[\s+0\.\d{3} ms\] UART1 baud 9615$", lines[2]);
        Assert.Matches(@"^\[\s+3\.\d{3} ms\] UART1: hi$", lines[3]); // 3 bytes of 10 bits at 9615 baud
        Assert.Matches(@"^\[\s+3\.\d{3} ms\] stopped: the program has finished: the CPU halted \(halt\)", lines[4]);
        Assert.Equal(5, lines.Length);
        Assert.Equal("hi\n", Encoding.ASCII.GetString(File.ReadAllBytes(bytes)));
    }

    [Fact]
    public void An_input_wakes_the_PFS154_from_stopsys_and_memory_the_image_does_not_fill_stops_the_run()
    {
        var (exitCode, output, _) = Command("pfs154", PfsStop(), "--input", "PA3=high@2.5ms");

        Assert.Equal(3, exitCode);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith("] PA4 low", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("] PA4 high", lines[2], StringComparison.Ordinal);
        Assert.Equal("[     2.500 ms] input: PA3 high", lines[3]);
        Assert.Equal("[     2.500 ms] stopped: the program ran into program memory the image does not fill, at pc 0x000c (word 0x006)",
            lines[4]);
    }

    [Fact]
    public void A_run_ends_after_the_time_given()
    {
        var spin = Image("spin.ihx", (0x8000, [0x82, 0x00, 0x80, 0x04]), (0x8004, [0x20, 0xFE])); // jra .

        var (exitCode, output, _) = Command("stm8s103", spin, "--ms", "12.5");

        Assert.Equal(0, exitCode);
        Assert.EndsWith("[    12.500 ms] stopped: 12.5 ms of simulated time have passed" + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "Usage: chipsim")]
    [InlineData(new[] { "z80", "x.ihx" }, "there is no chip 'z80'")]
    [InlineData(new[] { "stm8s103" }, "which .ihx image?")]
    [InlineData(new[] { "stm8s103", "IMAGE", "--fast" }, "unknown option '--fast'")]
    [InlineData(new[] { "stm8s103", "IMAGE", "--ms", "0" }, "--ms must be a positive number")]
    [InlineData(new[] { "stm8s103", "IMAGE", "--ms" }, "--ms needs a value")]
    [InlineData(new[] { "stm8s103", "IMAGE", "--input", "PB5=on" }, "--input takes a pin, a level and maybe a time")]
    [InlineData(new[] { "stm8s103", "IMAGE", "--input", "PA0=high" }, "the STM8S103 has no pin PA0; it has PA1, PA2, PA3, PB4, PB5")]
    [InlineData(new[] { "stm8s103", "IMAGE", "--send", "\\q" }, "--send: '\\q' is not one of")]
    [InlineData(new[] { "pfs154", "PDK", "--send", "hi" }, "the PFS154 has no UART for --send or --uart1")]
    public void Wrong_arguments_exit_with_2(string[] arguments, string message)
    {
        var (stm8, pdk) = (Stm8Hello(), PfsStop());
        var (exitCode, output, errors) = Command([.. arguments.Select(a => a == "IMAGE" ? stm8 : a == "PDK" ? pdk : a)]);

        Assert.Equal(2, exitCode);
        Assert.Contains(message, output + errors, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_prints_the_usage_and_exits_with_0()
    {
        var (exitCode, output, _) = Command("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Exit code: 0 when the program ran", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "cannot read")]
    [InlineData(":0400000082008004F7\n:00000001FF\n", "has a wrong checksum")]
    [InlineData(":0200000082007C\n", "has no end-of-file record")]
    [InlineData("hello\n", "is not an Intel HEX record")]
    [InlineData(":01010000FFFF\n:00000001FF\n", "outside the STM8S103's flash")]
    public void An_image_that_is_not_valid_exits_with_1(string text, string message)
    {
        var path = Path.Combine(_scratch, "bad.ihx");
        if (text != "missing")
        {
            File.WriteAllText(path, text);
        }

        var (exitCode, output, errors) = Command("stm8s103", path);

        Assert.Equal(1, exitCode);
        Assert.Contains(message, errors, StringComparison.Ordinal);
        Assert.Equal("", output);
    }

    [Fact]
    public void The_chipsim_program_exits_with_the_runs_exit_code()
    {
        var breaks = Image("break.ihx", (0x8000, [0x82, 0x00, 0x80, 0x04]), (0x8004, [0x8B]));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "chipsim.dll"), "stm8s103", breaks })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(3, process.ExitCode);
        Assert.Contains("stopped: the program hit a breakpoint (break) at pc 0x8004", output, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output, string Errors) Command(params string[] arguments)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exitCode = Simulation.Command(arguments, output, errors);
        return (exitCode, output.ToString(), errors.ToString());
    }

    /// <summary>The bytes of PDK14 words: each low byte first.</summary>
    private static byte[] Words(params ushort[] words) => [.. words.SelectMany(word => new[] { (byte)word, (byte)(word >> 8) })];

    /// <summary>Writes <paramref name="blocks"/> as an Intel HEX file named <paramref name="name"/>.</summary>
    private string Image(string name, params (int Address, byte[] Data)[] blocks)
    {
        var text = new StringBuilder();
        foreach (var (address, data) in blocks)
        {
            for (var at = 0; at < data.Length; at += 16)
            {
                var chunk = data.AsSpan(at, Math.Min(16, data.Length - at));
                var record = new List<byte> { (byte)chunk.Length, (byte)((address + at) >> 8), (byte)(address + at), 0 };
                record.AddRange(chunk.ToArray());
                record.Add((byte)-record.Sum(b => b));
                text.Append(':').Append(Convert.ToHexString([.. record])).Append('\n');
            }
        }

        text.Append(":00000001FF\n");
        var path = Path.Combine(_scratch, name);
        File.WriteAllText(path, text.ToString());
        return path;
    }
}
