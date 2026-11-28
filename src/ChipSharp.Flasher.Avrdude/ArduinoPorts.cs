using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.Avrdude;

/// <summary>Which of this computer's serial ports (<see cref="SerialPorts.List"/>) an Arduino is on.</summary>
public static class ArduinoPorts
{
    /// <summary>The USB vendor ids of Arduino's own boards: Arduino LLC's and Arduino SRL's.</summary>
    private static readonly int[] ArduinoVendors = [0x2341, 0x2a03];

    /// <summary>
    /// The USB serial chips of the Uno's (and Nano's) clones, by vendor and product: WCH's CH340, CH341 and CH9102,
    /// FTDI's FT232R, Silicon Labs' CP210x.
    /// </summary>
    private static readonly (int Vendor, int Product)[] CloneChips =
        [(0x1a86, 0x7523), (0x1a86, 0x5523), (0x1a86, 0x55d4), (0x0403, 0x6001), (0x10c4, 0xea60)];

    /// <summary>The names Linux and macOS give USB serial ports, whose ids are not known here.</summary>
    private static readonly string[] UsbPortNames = ["ttyACM", "ttyUSB", "cu.usbmodem", "cu.usbserial", "cu.wchusbserial"];

    /// <summary>
    /// True when the port is an Arduino's, or a clone's: its USB ids are Arduino's or a clone's USB serial chip's, or
    /// (with no ids) the system calls it an Arduino, or names it as a USB serial port.
    /// </summary>
    public static bool LooksLikeArduino(SerialPortInfo port)
    {
        ArgumentNullException.ThrowIfNull(port);

        return port.VendorId is { } vendor
            ? ArduinoVendors.Contains(vendor) || CloneChips.Contains((vendor, port.ProductId ?? -1))
            : port.Description?.Contains("Arduino", StringComparison.OrdinalIgnoreCase) == true
                || UsbPortNames.Any(name => Path.GetFileName(port.Name).StartsWith(name, StringComparison.Ordinal));
    }

    /// <summary>
    /// The one port of <paramref name="ports"/> that looks like an Arduino's (<see cref="LooksLikeArduino"/>);
    /// otherwise an error that lists the ports and says what to do: plug the board in, or name its port with --port.
    /// </summary>
    public static string Choose(IReadOnlyList<SerialPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        var arduinos = ports.Where(LooksLikeArduino).ToList();
        if (arduinos.Count == 1)
        {
            return arduinos[0].Name;
        }

        static string Listed(IEnumerable<SerialPortInfo> list) => string.Join(", ", list);
        throw new ChipSharpException(arduinos.Count > 1
            ? string.Create(CultureInfo.InvariantCulture,
                $"{arduinos.Count} serial ports look like an Arduino's: {Listed(arduinos)}. Say which is the board's " +
                $"with --port, e.g. --port {arduinos[0].Name}.")
            : ports.Count == 0
                ? $"No serial port was found: plug the board in by USB, or give its port with --port (e.g. --port " +
                  $"{SerialPorts.Example}). On Windows its port shows in Device Manager, under \"Ports (COM & LPT)\"; a " +
                  "clone with a CH340 USB chip needs that chip's driver first."
                : $"None of the serial ports looks like an Arduino's: {Listed(ports)}. Plug the board in by USB, or give " +
                  $"its port with --port, e.g. --port {ports[^1].Name}.");
    }
}
