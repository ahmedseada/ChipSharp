using ChipSharp.Flasher.Avrdude;
using ChipSharp.Flasher.EasyPdkProg;
using ChipSharp.Flasher.Shared;
using ChipSharp.Flasher.Stm8Flash;
using ChipSharp.Flasher.Uf2Copy;
using ChipSharp.Tests.Avr;
using ChipSharp.Tests.Support;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// Runs the real <c>chipsharp flash</c>: what it needs (a board whose target names a flasher, the target's image or a
/// .dll), a .hex through a stand-in avrdude, easypdkprog and stm8flash (<see cref="FakeTool"/>), a .uf2 onto a folder
/// that stands for the Pico's drive, and a .dll built for the Uno, the PFS154 and the STM8S103, then flashed.
/// </summary>
public sealed class FlashCliTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Scratch.Delete(_scratch);
    }

    [Fact]
    public async Task Flash_needs_a_board_whose_target_names_a_flasher_and_that_boards_image()
    {
        var hex = Write("Blink.hex", ":00000001FF\r\n");
        const string Boards = "the boards with a flasher: pfs154-bare (easypdkprog), pico (uf2-copy), pico-w (uf2-copy), " +
            "stm8s103-board (stm8flash), uno (avrdude).";

        Assert.Equal((1, $"error: chipsharp flash needs the board to flash, with -t (e.g. -t uno): {Boards}"),
            await Error("flash", hex));
        Assert.Equal((1, "error: Target 'host' has no flasher (\"flasher\" in its file names what puts a program onto " +
                $"the board): {Boards}"),
            await Error("flash", hex, "-t", "host"));
        Assert.Equal((1, $"error: Target 'rp2040' is not a board (memory.flash.size is for a board's file to give): " +
                $"flash a board, {Boards}"),
            await Error("flash", hex, "-t", "rp2040"));
        Assert.Equal((1, $"error: '{hex}' is not a .uf2 file, which is what the target 'pico' flashes: give the program's " +
                ".dll (chipsharp flash builds it for the target first) or the .uf2 chipsharp build -t pico wrote."),
            await Error("flash", hex, "-t", "pico"));
        var missing = Path.Combine(_scratch, "Missing.hex");
        Assert.Equal((1, $"error: '{missing}' was not found."), await Error("flash", missing, "-t", "uno", "--port", "COM5"));
        Assert.Equal((1, "error: --port needs a value."), await Error("flash", hex, "-t", "uno", "--port"));
    }

    [Fact]
    public async Task Flash_of_a_hex_runs_avrdude_on_the_port_given_and_prints_each_stage()
    {
        var hex = Write("Blink.hex", ":00000001FF\r\n");
        var avrdude = new FakeTool(Path.Combine(_scratch, "avrdude"), "avrdude");

        var (exitCode, printed, error) = await Run(avrdude, "flash", hex, "-t", "uno", "--port", "COM42");

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        var commandLine = AvrdudeFlasher.CommandLine(
            AvrdudeFlasher.Arguments(new AvrdudeSettings("arduino", "atmega328p", 115200), "COM42", hex));
        Assert.Equal(commandLine, avrdude.CommandLine);
        Assert.Equal([$"COM42: avrdude {commandLine}", "COM42: written and verified; the uno runs it"], Lines(printed));
    }

    [Fact]
    public async Task A_failed_flash_ends_with_avrdudes_error_and_exit_code_1()
    {
        var hex = Write("Blink.hex", ":00000001FF\r\n");
        var avrdude = new FakeTool(Path.Combine(_scratch, "avrdude"), "avrdude", 1,
            "avrdude: stk500_getsync() attempt 10 of 10: not in sync: resp=0x00");

        var (exitCode, printed, error) = await Run(avrdude, "flash", hex, "-t", "uno", "--port", "COM42");

        Assert.Equal(1, exitCode);
        Assert.Single(Lines(printed)); // the command line it ran
        Assert.StartsWith("error: avrdude could not flash 'Blink.hex' to the board on COM42 (exit code 1). The board on " +
            "COM42 did not answer as its bootloader should", error);
        Assert.EndsWith("avrdude said:\n  avrdude: stk500_getsync() attempt 10 of 10: not in sync: resp=0x00",
            error.ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public async Task Flash_of_a_uf2_copies_it_to_the_Picos_drive()
    {
        var uf2 = Write("Blink.uf2", "UF2 blocks");
        var drive = Directory.CreateDirectory(Path.Combine(_scratch, "RPI-RP2")).FullName;
        File.WriteAllText(Path.Combine(drive, Uf2CopyFlasher.InfoFile), "UF2 Bootloader v3.0\r\n");

        var (exitCode, printed, error) = await ChipSharpCli.RunAsync("flash", uf2, "-t", "pico", "--port", drive);

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        Assert.Equal("UF2 blocks", File.ReadAllText(Path.Combine(drive, "Blink.uf2")));
        Assert.Equal(
            [$"{drive}: copying Blink.uf2 (10 bytes)", $"{drive}: copied; the pico writes it into its flash, restarts and runs it"],
            Lines(printed));
    }

    [AvrFact]
    public async Task Flash_of_a_dll_builds_it_for_the_Uno_as_build_does_then_flashes_the_hex()
    {
        var dll = Path.Combine(Directory.CreateDirectory(_scratch).FullName, "UnoBlink.dll");
        File.WriteAllBytes(dll, CSharpCompiler.Compile(["""
            using ChipSharp.Hardware;

            Pin.Output(13);
            while (true)
            {
                Pin.Toggle(13);
                Timing.DelayMilliseconds(500);
            }
            """], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "UnoBlink"));
        var avrdude = new FakeTool(Path.Combine(_scratch, "avrdude"), "avrdude");
        var output = Path.Combine(_scratch, "uno");

        var (exitCode, printed, error) = await Run(avrdude, "flash", dll, "-t", "uno", "--port", "COM42", "-o", output,
            "--no-cache");

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        var hex = Path.Combine(output, "UnoBlink.hex");
        var lines = Lines(printed);
        Assert.Equal(4, lines.Count);
        Assert.Equal(hex, lines[0]);
        Assert.Matches(@"^flash \d\.\d KB of 31\.5 KB \(\d\.\d%\), RAM \d+ bytes of 2 KB \(stack 512 bytes reserved\)$", lines[1]);
        Assert.Equal($"COM42: avrdude {avrdude.CommandLine}", lines[2]);
        Assert.Contains($"flash:w:{hex}:i", avrdude.CommandLine);
        Assert.Equal("COM42: written and verified; the uno runs it", lines[3]);
        Assert.StartsWith(":", File.ReadAllText(hex));
    }

    [Fact]
    public async Task Flash_of_a_hex_for_the_pfs154_bare_runs_easypdkprog_on_the_programmers_port()
    {
        var hex = Write("Blink.hex", ":00000001FF\r\n");
        var easypdkprog = new FakeTool(Path.Combine(_scratch, "easypdkprog"), "easypdkprog", 0,
            "Writing IC (174 words)... done.", "Calibrating IC",
            "* IHRC SYSCLK=2000000Hz @ 5.00V ... calibration result: 1998750Hz (0x84)  done.");

        var (exitCode, printed, error) = await ChipSharpCli.RunWithAsync(_scratch,
            new Dictionary<string, string> { [EasyPdkProgFlasher.EasyPdkProgVariable] = easypdkprog.Path },
            "flash", hex, "-t", "pfs154-bare", "--port", "COM42");

        Assert.Equal("", error);
        Assert.Equal(0, exitCode);
        Assert.Equal($@"-n PFS154 -p \\.\COM42 write {hex}", easypdkprog.CommandLine);
        Assert.Equal(
            [
                $@"COM42: easypdkprog -n PFS154 -p \\.\COM42 write {ChipSharpCli.Quoted(hex)}",
                "COM42: IHRC calibrated: 1998750 Hz for 2000000 Hz at 5.00 V (IHRCR 0x84)",
                @"COM42: written and verified; the pfs154-bare runs it out of the programmer's socket (in it: " +
                    @"easypdkprog -p \\.\COM42 start)",
            ],
            Lines(printed));
    }

    [Fact]
    public async Task Flash_of_a_hex_for_the_stm8s103_board_runs_stm8flash_and_a_locked_chip_gets_the_unlock_command()
    {
        var hex = Write("Blink.hex", ":00000001FF\r\n");
        var stm8flash = new FakeTool(Path.Combine(_scratch, "stm8flash"), "stm8flash", -1, "Determine FLASH area", "Tries exceeded");

        var (exitCode, printed, error) = await ChipSharpCli.RunWithAsync(_scratch,
            new Dictionary<string, string> { [Stm8FlashFlasher.Stm8FlashVariable] = stm8flash.Path },
            "flash", hex, "-t", "stm8s103-board");

        // the board's ST-LINK (stlinkv2), unless this computer has another kind plugged in alone
        var programmer = StLinks.Programmer("stlinkv2",
            UsbDevices.List(StLinks.VendorId) is { } devices ? StLinks.Among(devices) : null);
        Assert.Equal(1, exitCode);
        Assert.Equal($"-c {programmer} -p stm8s103f3 -s flash -w {hex}", stm8flash.CommandLine);
        Assert.Equal(
            [$"{StLinks.Name(programmer)}: stm8flash -c {programmer} -p stm8s103f3 -s flash -w {ChipSharpCli.Quoted(hex)}"],
            Lines(printed));
        Assert.StartsWith($"error: stm8flash could not write 'Blink.hex' into the stm8s103f3 through the " +
            $"{StLinks.Name(programmer)} (exit code -1). The stm8s103f3 is most likely read-out protected", error);
        Assert.Contains($"unlock it, which erases it, with stm8flash -c {programmer} -p stm8s103f3 -u, then flash it again.",
            error);
    }

    [SdccBoardsFact]
    public async Task Flash_of_a_dll_builds_it_for_the_PFS154_and_the_STM8S103_then_flashes_the_hex()
    {
        var dll = Path.Combine(Directory.CreateDirectory(_scratch).FullName, "Count.dll");
        File.WriteAllBytes(dll, CSharpCompiler.Compile(["""
            static class Program
            {
                static int Main()
                {
                    int sum = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        sum += i;
                    }

                    return sum;
                }
            }
            """], [typeof(ChipSharp.Hardware.Pin).Assembly.Location], "Count"));
        var easypdkprog = new FakeTool(Path.Combine(_scratch, "easypdkprog"), "easypdkprog");
        var stm8flash = new FakeTool(Path.Combine(_scratch, "stm8flash"), "stm8flash");
        var tools = new Dictionary<string, string>
        {
            [EasyPdkProgFlasher.EasyPdkProgVariable] = easypdkprog.Path,
            [Stm8FlashFlasher.Stm8FlashVariable] = stm8flash.Path,
        };

        foreach (var (board, tool, port, memory) in new (string, FakeTool, string[], string)[]
        {
            ("pfs154-bare", easypdkprog, ["--port", "COM42"], @"^program \d+ words of 2048 words \(\d+\.\d%\), RAM \d+ bytes of 128 bytes"),
            ("stm8s103-board", stm8flash, [], @"^flash [\d.]+ (bytes|KB) of 8 KB \(\d+\.\d%\), RAM \d+ bytes of 1 KB"),
        })
        {
            var output = Path.Combine(_scratch, board);
            var (exitCode, printed, error) = await ChipSharpCli.RunWithAsync(_scratch, tools,
                ["flash", dll, "-t", board, .. port, "-o", output, "--no-cache"]);

            Assert.True(exitCode == 0, error);
            var hex = Path.Combine(output, "Count.hex");
            var lines = Lines(printed);
            Assert.Equal(hex, lines[0]);
            Assert.Matches(memory, lines[1]);
            Assert.EndsWith(hex, tool.CommandLine);
            Assert.EndsWith($": {tool.Name} {tool.CommandLine}", lines[2]);
            Assert.Contains($"the {board} runs it", lines[^1]);
            Assert.StartsWith(":", File.ReadAllText(hex));
        }
    }

    [Fact]
    public async Task The_usage_shows_flash_and_its_port()
    {
        var (exitCode, printed, _) = await ChipSharpCli.RunAsync("flash");

        Assert.Equal(2, exitCode);
        Assert.Contains("chipsharp flash <assembly.dll | image.hex | image.uf2> -t|--target <board> [--port <port>]", printed);
        Assert.Contains("--port <port>           flash: the board's serial port (avrdude: COM5) or boot drive", printed);
    }

    /// <summary>chipsharp with <paramref name="avrdude"/> as its avrdude (CHIPSHARP_AVRDUDE).</summary>
    private Task<(int ExitCode, string Output, string Error)> Run(FakeTool avrdude, params string[] arguments) =>
        ChipSharpCli.RunWithAsync(_scratch, new Dictionary<string, string> { [AvrdudeFlasher.AvrdudeVariable] = avrdude.Path },
            arguments);

    /// <summary>The exit code and standard error of chipsharp, which must print nothing else.</summary>
    private async Task<(int ExitCode, string Error)> Error(params string[] arguments)
    {
        var (exitCode, printed, error) = await ChipSharpCli.RunAsync(arguments);
        Assert.Equal("", printed);
        return (exitCode, error.TrimEnd());
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(_scratch);
        var path = Path.Combine(_scratch, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static List<string> Lines(string text) => [.. text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)];
}
