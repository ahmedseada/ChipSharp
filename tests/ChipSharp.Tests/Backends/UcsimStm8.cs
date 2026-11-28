using System.Diagnostics;
using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Backends;

/// <summary>
/// SDCC's own STM8 simulator, uCsim (ucsim_stm8.exe, installed with SDCC, next to sdcc.exe; SDCC's regression tests
/// run on it): a second, independent STM8 core for the programs the tests run on chipsim's, so that a mistake chipsim
/// shares with the runtime or with what a test expects shows up as the two disagreeing. A program built with the tests'
/// startup and console (<see cref="ChipConsole"/>) and <see cref="ReportFile"/> sends each report (a kind and 32 bits)
/// as ten hex digits and a newline on UART1, which uCsim writes to a file (as text: Windows' uCsim writes each LF byte
/// as CR LF, so the bytes go as digits). uCsim's STM8S103 has the STM8S208's memory (6 KB of RAM, the stack from
/// 0x17FF, 128 KB of flash), so only the core is checked, not the chip's sizes.
/// </summary>
internal static class UcsimStm8
{
    /// <summary>ucsim_stm8.exe next to SDCC's sdcc.exe; null when there is none.</summary>
    public static string? Path => Found.Value;

    private static readonly Lazy<string?> Found = new(() =>
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(new SdccToolchain().FindSdcc())!, "ucsim_stm8.exe");
            return File.Exists(path) ? path : null;
        }
        catch (ChipSharpException)
        {
            return null;
        }
    });

    /// <summary>report() for uCsim: the kind and the value as ten hex digits and a newline on UART1.</summary>
    public static GeneratedFile ReportFile { get; } = new("report.c", """
        /* The tests' report() for uCsim: each report on UART1 as ten hex digits (kind, value) and a newline. */
        #include <stdint.h>

        extern volatile uint8_t cs_kind;
        extern volatile uint32_t cs_value;

        #define UART1_SR (*(volatile uint8_t *)0x5230)
        #define UART1_DR (*(volatile uint8_t *)0x5231)
        #define UART1_BRR1 (*(volatile uint8_t *)0x5232)
        #define UART1_BRR2 (*(volatile uint8_t *)0x5233)
        #define UART1_CR2 (*(volatile uint8_t *)0x5235)

        static void put(uint8_t value)
        {
            while (!(UART1_SR & 0x80))
            {
            }

            UART1_DR = value;
        }

        static void hex(uint8_t value)
        {
            put("0123456789abcdef"[value >> 4]);
            put("0123456789abcdef"[value & 15]);
        }

        void report(void)
        {
            uint32_t value = cs_value;
            uint8_t kind = cs_kind;
            if (!(UART1_CR2 & 0x08))
            {
                UART1_BRR2 = 0x00;
                UART1_BRR1 = 0x01;
                UART1_CR2 = 0x08;
            }

            hex(kind);
            hex((uint8_t)(value >> 24));
            hex((uint8_t)(value >> 16));
            hex((uint8_t)(value >> 8));
            hex((uint8_t)value);
            put(10);
            if (kind == 12 || kind == 13)
            {
                while (!(UART1_SR & 0x40))
                {
                }
            }
        }

        """.ReplaceLineEndings("\n"));

    /// <summary>
    /// Runs <paramref name="ihx"/> (built with <see cref="ReportFile"/>) in uCsim until the program stops the core, or
    /// <paramref name="timeout"/>: the text it wrote, as <see cref="ChipConsole.Text"/> makes it of the reports.
    /// </summary>
    public static (byte[] Output, string? Failure, bool Ended) Run(string ihx, TirModule module, TimeSpan timeout)
    {
        var reports = new List<(byte Kind, uint Value)>();
        foreach (var line in Uart1(ihx, timeout).Split('\n'))
        {
            var digits = line.Trim();
            if (digits.Length == 10
                && uint.TryParse(digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
                && byte.TryParse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var kind))
            {
                reports.Add((kind, value));
            }
        }

        return ChipConsole.Text(reports, module);
    }

    /// <summary>
    /// Runs an image (an .ihx or .hex) in uCsim until the program stops the core, or <paramref name="timeout"/>: what it
    /// sent on UART1 (the console of ChipSharp's stm8s103f3 runtime), as text, each line end as the program sent it.
    /// </summary>
    public static string Uart1(string image, TimeSpan timeout)
    {
        var simulator = Path ?? throw new InvalidOperationException("uCsim (ucsim_stm8.exe) is not installed with SDCC.");
        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(image))!;
        var output = System.IO.Path.Combine(folder, "ucsim-uart1.txt");
        File.Delete(output);
        // through cmd, its console's input NUL: uCsim, which also reads commands there, quits at once at the end of a
        // pipe, and runs slowly while it polls one that stays open; the output file by its name alone, in the image's
        // folder, as uCsim takes the colon of a drive for its own
        var start = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Arguments = $"/c \"\"{simulator}\" -t STM8S103 -q -S uart=1,out={System.IO.Path.GetFileName(output)} -G " +
            $"\"{System.IO.Path.GetFileName(image)}\" < NUL\"";

        using (var process = Process.Start(start) ?? throw new InvalidOperationException("uCsim did not start."))
        {
            var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            drain.Wait();
        }

        // uCsim writes its file as text: each LF byte the program sent became CR LF
        return File.Exists(output)
            ? Encoding.Latin1.GetString(File.ReadAllBytes(output)).Replace("\r\n", "\n", StringComparison.Ordinal)
            : "";
    }
}
