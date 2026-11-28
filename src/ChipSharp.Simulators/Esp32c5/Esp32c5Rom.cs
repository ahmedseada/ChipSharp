using System.Globalization;
using System.Text;
using ChipSharp.Simulators.RiscV;

namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The ROM functions a start-up might call, done in C# instead of the ROM's code (the simulator has no copy of it), at
/// their addresses in components/esp_rom/esp32c5/ld/esp32c5.rom.ld and esp32c5.rom.libc.ld: rtc_get_reset_reason
/// (1, power-on), ets_printf (%d %i %u %x %X %c %s %p %%, with widths, zero padding and l; to UART0, as the ROM's console
/// writes), ets_delay_us (the time passes, at the CPU's clock), ets_get_cpu_frequency and ets_update_cpu_frequency (the
/// ROM's ticks a microsecond), uart_tx_one_char (to UART0), uart_tx_flush (waits for UART0 to send what it holds),
/// ets_install_putc1, ets_install_putc2, ets_install_uart_printf and ets_install_usb_printf (nothing to do),
/// software_reset and software_reset_cpu (they stop the run: the chip would start again), memset and strlen. Each
/// returns to ra. A call of any other ROM address stops the run as a failure, saying which.
/// </summary>
internal sealed class Esp32c5Rom(Esp32c5Chip chip)
{
    private static readonly Dictionary<uint, string> Functions = new()
    {
        [0x4000_0018] = "rtc_get_reset_reason", [0x4000_0024] = "ets_printf", [0x4000_0028] = "ets_install_putc1",
        [0x4000_002C] = "ets_install_putc2", [0x4000_0030] = "ets_install_uart_printf",
        [0x4000_0034] = "ets_install_usb_printf", [0x4000_003C] = "ets_delay_us", [0x4000_0040] = "ets_get_cpu_frequency",
        [0x4000_0044] = "ets_update_cpu_frequency", [0x4000_0054] = "uart_tx_one_char", [0x4000_0074] = "uart_tx_flush",
        [0x4000_0094] = "software_reset", [0x4000_0098] = "software_reset_cpu", [0x4000_04B8] = "memset",
        [0x4000_04D8] = "strlen",
    };

    private uint _ticksPerMicrosecond = 48;

    /// <summary>The functions the simulator has, by address.</summary>
    public static IReadOnlyDictionary<uint, string> Names => Functions;

    /// <summary>Runs the ROM function at <paramref name="address"/> and returns to ra; its cycles.</summary>
    public int Call(uint address, RiscVCpu cpu)
    {
        var a0 = cpu.X[10];
        var cycles = 20;
        switch (Functions.GetValueOrDefault(address))
        {
            case "rtc_get_reset_reason":
                cpu.X[10] = 1;
                break;
            case "ets_printf":
                var text = Format(cpu);
                foreach (var b in text)
                {
                    chip.RomPutChar(b);
                }

                cpu.X[10] = (uint)text.Length;
                cycles += 10 * text.Length;
                break;
            case "ets_install_putc1" or "ets_install_putc2" or "ets_install_uart_printf" or "ets_install_usb_printf":
                break;
            case "ets_delay_us":
                cycles = (int)Math.Min(int.MaxValue, (long)a0 * chip.CpuHertz / 1_000_000);
                break;
            case "ets_get_cpu_frequency":
                cpu.X[10] = _ticksPerMicrosecond;
                break;
            case "ets_update_cpu_frequency":
                _ticksPerMicrosecond = a0;
                break;
            case "uart_tx_one_char":
                chip.RomPutChar((byte)a0);
                cpu.X[10] = 0;
                break;
            case "uart_tx_flush":
                cycles += (int)Math.Min(int.MaxValue, chip.UartIdleTicks() * chip.CpuHertz / SimulatedChip.TicksPerSecond);
                break;
            case "software_reset" or "software_reset_cpu":
                throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                    $"the program reset the chip ({Functions[address]} in the ROM, called from 0x{cpu.X[1] - 4:x8}): it would start again"));
            case "memset":
                for (uint i = 0; i < cpu.X[12]; i++)
                {
                    chip.Write(a0 + i, 1, cpu.X[11]);
                }

                cycles += (int)(cpu.X[12] / 4);
                break;
            case "strlen":
                uint length = 0;
                while (chip.Read(a0 + length, 1) != 0)
                {
                    length++;
                }

                cpu.X[10] = length;
                cycles += (int)length;
                break;
            default:
                throw new ProgramFault(string.Create(CultureInfo.InvariantCulture,
                    $"the program called the ROM at 0x{address:x8} (from 0x{cpu.X[1]:x8}), which the simulator does not " +
                    $"have: it has {string.Join(", ", Functions.Values)}{(cpu.LastException is { } trap ? $"; before, it caused {trap}" : "")}"));
        }

        cpu.Pc = cpu.X[1] & ~1u;
        return cycles;
    }

    /// <summary>ets_printf's text: the format at a0, its arguments in a1-a7 and then on the stack.</summary>
    private byte[] Format(RiscVCpu cpu)
    {
        var format = String(cpu.X[10]);
        var argument = 0;
        uint Next() => argument < 7 ? cpu.X[11 + argument++] : chip.Read(cpu.X[2] + 4 * (uint)(argument++ - 7), 4);
        var text = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '%' || i + 1 == format.Length)
            {
                text.Append(format[i]);
                continue;
            }

            i++;
            var zero = format[i] == '0';
            var width = 0;
            while (i < format.Length && char.IsAsciiDigit(format[i]))
            {
                width = width * 10 + format[i++] - '0';
            }

            while (i < format.Length && format[i] == 'l')
            {
                i++;
            }

            if (i == format.Length)
            {
                break;
            }

            var piece = format[i] switch
            {
                'd' or 'i' => ((int)Next()).ToString(CultureInfo.InvariantCulture),
                'u' => Next().ToString(CultureInfo.InvariantCulture),
                'x' => Next().ToString("x", CultureInfo.InvariantCulture),
                'X' => Next().ToString("X", CultureInfo.InvariantCulture),
                'p' => "0x" + Next().ToString("x8", CultureInfo.InvariantCulture),
                'c' => ((char)(byte)Next()).ToString(),
                's' => String(Next()),
                '%' => "%",
                var other => "%" + other,
            };
            text.Append(piece.PadLeft(width, zero && format[i] is not ('s' or 'c') ? '0' : ' '));
        }

        return Encoding.Latin1.GetBytes(text.ToString());
    }

    private string String(uint address)
    {
        var bytes = new List<byte>();
        for (var b = chip.Read(address, 1); b != 0 && bytes.Count < 4096; b = chip.Read(address + (uint)bytes.Count, 1))
        {
            bytes.Add((byte)b);
        }

        return Encoding.Latin1.GetString([.. bytes]);
    }
}
