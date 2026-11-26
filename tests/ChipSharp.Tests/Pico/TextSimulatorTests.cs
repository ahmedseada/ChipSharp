using System.Text;
using ChipSharp.Image.Uf2;
using ChipSharp.Tests.Lowering;
using ChipSharp.Tests.Support;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Tests.Pico;

/// <summary>
/// The fixed-text programs of <see cref="TextPrograms"/> built for the Pico and run in the simulator: they print over
/// UART0 what .NET prints, text as UTF-8. Each literal is in flash, once, however often the program uses it.
/// </summary>
public sealed class TextSimulatorTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "chipsharp-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    public static TheoryData<string> ProgramNames => [.. TextPrograms.All.Select(program => program.Name)];

    [SimulatorTheory]
    [MemberData(nameof(ProgramNames))]
    public async Task Each_program_prints_over_UART0_what_dotnet_prints(string name)
    {
        var source = TextPrograms.All.Single(program => program.Name == name).Source;
        var dotnet = await HostRun.RunOnDotnetAsync(CSharpCompiler.Compile(source, name), name,
            Path.Combine(_scratch, "dotnet"));
        var program = PicoBuild.Build(source, name, Path.Combine(_scratch, "pico"));

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 10_000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Contains("stopped: the program has finished", run.Stopped);
        Assert.Equal(dotnet.Text, Encoding.ASCII.GetString(run.Uart0));
        Assert.Equal(dotnet.Output, run.Uart0);
    }

    [SimulatorFact]
    public async Task Text_that_is_not_ASCII_goes_over_UART0_as_UTF8()
    {
        var program = PicoBuild.Build(TextPrograms.NotAscii, "NotAscii", _scratch);

        var run = await Rp2040Simulator.RunAsync(program.Uf2, 1000);

        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(Encoding.UTF8.GetBytes(TextPrograms.NotAsciiOutput), run.Uart0);
    }

    [PicoFact]
    public void Each_literal_is_in_flash_once_and_none_is_copied_to_RAM()
    {
        // "lane : " three times is one record; the static field that holds text is a pointer in .bss
        var program = PicoBuild.Build("""
            using System;

            class Program
            {
                static readonly string Lane = "lane : ";

                static void Main()
                {
                    Console.Write("lane : ");
                    Console.Write(1);
                    Console.Write(" interrupted, move to lane ");
                    Console.WriteLine(2);
                    Console.Write("lane : ");
                    Console.WriteLine(Lane);
                }
            }
            """, "Lanes", _scratch);
        var elf = ElfFile.Read(program.Elf);
        var flash = PicoBuild.FlashImage(elf);

        Assert.Equal(1, Count(flash, [7, 0, 0, 0, 7, 0, 0, 0, .. "lane : "u8]));
        Assert.Equal(1, Count(flash, [.. " interrupted, move to lane "u8]));
        Assert.Equal(elf.Symbols[LinkerScript.DataStart], elf.Symbols[LinkerScript.DataEnd]);
    }

    /// <summary>How many times <paramref name="part"/> is in <paramref name="data"/>.</summary>
    private static int Count(byte[] data, byte[] part)
    {
        var count = 0;
        for (var start = 0; data.AsSpan(start).IndexOf(part) is var at and >= 0; start += at + 1)
        {
            count++;
        }

        return count;
    }
}
