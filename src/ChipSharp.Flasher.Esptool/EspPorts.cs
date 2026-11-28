using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.Esptool;

/// <summary>Which of this computer's serial ports (<see cref="SerialPorts.List"/>) an ESP32-C5 board is on.</summary>
public static class EspPorts
{
    /// <summary>
    /// The USB ids of the ports a board has, the likeliest first: the chip's own USB Serial/JTAG (Espressif 303A:1001,
    /// the ESP32-C5-DevKitC-1's port marked "USB", which is also what the ROM's download mode shows), then the
    /// devkit's USB-to-UART bridge (the port marked "UART": Silicon Labs' CP2102N 10C4:EA60) and the bridges other
    /// ESP32 boards have (WCH's CH343 1A86:55D3, CH340 1A86:7523, CH9102 1A86:55D4).
    /// </summary>
    private static readonly (string Name, (int Vendor, int Product)[] Ids)[] Kinds =
    [
        ("USB Serial/JTAG", [(0x303a, 0x1001)]),
        ("USB-to-UART bridge", [(0x10c4, 0xea60), (0x1a86, 0x55d3), (0x1a86, 0x7523), (0x1a86, 0x55d4)]),
    ];

    /// <summary>What to plug in, for messages.</summary>
    public const string PlugIn =
        "Plug the board in by USB: either USB-C port of the ESP32-C5-DevKitC-1 works, the one marked \"USB\" (the chip's " +
        "own USB Serial/JTAG) or the one marked \"UART\" (a USB-to-UART bridge, which needs its driver: Silicon Labs' " +
        "CP210x)";

    /// <summary>
    /// The port to flash: the one port with the chip's USB Serial/JTAG ids, else the one with a UART bridge's. More than
    /// one of a kind is an error that lists them (say which with --port); none, an error that says what to plug in.
    /// </summary>
    public static string Choose(IReadOnlyList<SerialPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        foreach (var (_, ids) in Kinds)
        {
            var found = ports.Where(port => port.VendorId is { } v && ids.Contains((v, port.ProductId ?? -1))).ToList();
            if (found.Count == 1)
            {
                return found[0].Name;
            }

            if (found.Count > 1)
            {
                throw new ChipSharpException(string.Create(CultureInfo.InvariantCulture,
                    $"{found.Count} serial ports look like an ESP32 board's: {string.Join(", ", found)}. Say which is the " +
                    $"board's with --port, e.g. --port {found[0].Name}."));
            }
        }

        throw new ChipSharpException(ports.Count == 0
            ? $"No serial port was found. {PlugIn}, or give its port with --port (e.g. --port {SerialPorts.Example})."
            : $"None of the serial ports looks like an ESP32-C5 board's: {string.Join(", ", ports)}. {PlugIn}, or give " +
              $"its port with --port, e.g. --port {ports[^1].Name}.");
    }
}
