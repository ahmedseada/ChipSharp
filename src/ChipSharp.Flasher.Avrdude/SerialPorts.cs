using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ChipSharp.Abstractions;
using Microsoft.Win32;

namespace ChipSharp.Flasher.Avrdude;

/// <summary>
/// A serial port of this computer: its name (COM5, /dev/ttyACM0), what the system calls its device ("Arduino Uno",
/// "USB-SERIAL CH340"), and the device's USB vendor and product ids when it is on USB.
/// </summary>
public sealed record SerialPortInfo(string Name, string? Description = null, int? VendorId = null, int? ProductId = null)
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
    public bool LooksLikeArduino => VendorId is { } vendor
        ? ArduinoVendors.Contains(vendor) || CloneChips.Contains((vendor, ProductId ?? -1))
        : Description?.Contains("Arduino", StringComparison.OrdinalIgnoreCase) == true
            || UsbPortNames.Any(name => Path.GetFileName(Name).StartsWith(name, StringComparison.Ordinal));

    /// <summary>"COM5 (Arduino Uno)", or the name alone.</summary>
    public override string ToString() => Description is null ? Name : $"{Name} ({Description})";
}

/// <summary>This computer's serial ports, and which of them a board is on.</summary>
internal static partial class SerialPorts
{
    /// <summary>
    /// The serial ports there are now, by name. On Windows: those the registry's SERIALCOMM lists, each with the
    /// friendly name and the USB ids of its device from the USB (and FTDI) devices' keys; on Linux and macOS the USB
    /// serial ports in /dev, with the ids Linux gives in /sys.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> List()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsPorts();
            }

            return UnixPorts();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// The one port of <paramref name="ports"/> that looks like an Arduino's (<see cref="SerialPortInfo.LooksLikeArduino"/>);
    /// otherwise an error that lists the ports and says what to do: plug the board in, or name its port with --port.
    /// </summary>
    public static string Choose(IReadOnlyList<SerialPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        var arduinos = ports.Where(port => port.LooksLikeArduino).ToList();
        if (arduinos.Count == 1)
        {
            return arduinos[0].Name;
        }

        static string Listed(IEnumerable<SerialPortInfo> list) => string.Join(", ", list);
        var example = OperatingSystem.IsWindows() ? "COM5" : "/dev/ttyACM0";
        throw new ChipSharpException(arduinos.Count > 1
            ? string.Create(CultureInfo.InvariantCulture,
                $"{arduinos.Count} serial ports look like an Arduino's: {Listed(arduinos)}. Say which is the board's " +
                $"with --port, e.g. --port {arduinos[0].Name}.")
            : ports.Count == 0
                ? $"No serial port was found: plug the board in by USB, or give its port with --port (e.g. --port " +
                  $"{example}). On Windows its port shows in Device Manager, under \"Ports (COM & LPT)\"; a clone with a " +
                  "CH340 USB chip needs that chip's driver first."
                : $"None of the serial ports looks like an Arduino's: {Listed(ports)}. Plug the board in by USB, or give " +
                  $"its port with --port, e.g. --port {ports[^1].Name}.");
    }

    /// <summary>"Arduino Uno (COM5)" without its port: "Arduino Uno".</summary>
    internal static string? Description(string? friendlyName) =>
        string.IsNullOrWhiteSpace(friendlyName) ? null : PortSuffix().Replace(friendlyName, "").Trim();

    /// <summary>The USB ids in a device's name: VID_2341&amp;PID_0043 (a USB device), VID_0403+PID_6001+... (FTDI's).</summary>
    internal static (int Vendor, int Product)? UsbIds(string deviceName)
    {
        var match = UsbIdPattern().Match(deviceName);
        return match.Success
            ? (int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            : null;
    }

    [SupportedOSPlatform("windows")]
    private static List<SerialPortInfo> WindowsPorts()
    {
        using var map = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (map is null)
        {
            return [];
        }

        // SERIALCOMM: the kernel's device (\Device\USBSER000, \Device\BthModem0, ...) and the port it has, for each port
        // there is now; the devices' keys below say what each is, a Bluetooth link's from its own bus
        var usb = WindowsDevices("USB", "FTDIBUS");
        var bluetooth = WindowsDevices("BTHENUM");
        return
        [
            .. map.GetValueNames()
                .Select(device => (Device: device, Port: map.GetValue(device) as string))
                .Where(entry => !string.IsNullOrEmpty(entry.Port)).DistinctBy(entry => entry.Port, StringComparer.OrdinalIgnoreCase)
                .OrderBy(entry => entry.Port, StringComparer.OrdinalIgnoreCase)
                .Select(entry =>
                {
                    var (first, second) = entry.Device.Contains("Bth", StringComparison.OrdinalIgnoreCase)
                        ? (bluetooth, usb)
                        : (usb, bluetooth);
                    return first.GetValueOrDefault(entry.Port!) ?? second.GetValueOrDefault(entry.Port!)
                        ?? new SerialPortInfo(entry.Port!);
                }),
        ];
    }

    /// <summary>
    /// The devices of these buses (USB, FTDI's, Bluetooth's) that Windows knows to have a serial port (Device
    /// Parameters\PortName), by port: their friendly name, and their ids when they are USB ones. Devices unplugged are
    /// among them; SERIALCOMM says which ports are there now.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Dictionary<string, SerialPortInfo> WindowsDevices(params string[] buses)
    {
        var devices = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var bus in buses)
        {
            using var root = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{bus}");
            foreach (var deviceName in root?.GetSubKeyNames() ?? [])
            {
                var ids = UsbIds(deviceName);
                using var device = root!.OpenSubKey(deviceName);
                foreach (var instanceName in device?.GetSubKeyNames() ?? [])
                {
                    using var instance = device!.OpenSubKey(instanceName);
                    using var parameters = instance?.OpenSubKey("Device Parameters");
                    if (parameters?.GetValue("PortName") is string { Length: > 0 } port)
                    {
                        var name = instance!.GetValue("FriendlyName") as string
                            ?? (instance.GetValue("DeviceDesc") as string)?.Split(';')[^1];
                        devices[port] = new SerialPortInfo(port, Description(name), ids?.Vendor, ids?.Product);
                    }
                }
            }
        }

        return devices;
    }

    private static List<SerialPortInfo> UnixPorts()
    {
        if (!Directory.Exists("/dev"))
        {
            return [];
        }

        string? Read(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        int? Hex(string? text) =>
            int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;

        var ports = new List<SerialPortInfo>();
        foreach (var pattern in new[] { "ttyACM*", "ttyUSB*", "cu.usbmodem*", "cu.usbserial*", "cu.wchusbserial*" })
        {
            foreach (var path in Directory.GetFiles("/dev", pattern).Order(StringComparer.Ordinal))
            {
                // Linux: the tty's device is a USB interface (ACM) or a serial converter below one; the ids are above
                var device = Path.Combine("/sys/class/tty", Path.GetFileName(path), "device");
                var usb = new[] { "..", Path.Combine("..", "..") }.Select(up => Path.Combine(device, up))
                    .FirstOrDefault(folder => File.Exists(Path.Combine(folder, "idVendor")));
                ports.Add(usb is null
                    ? new SerialPortInfo(path)
                    : new SerialPortInfo(path, Read(Path.Combine(usb, "product")), Hex(Read(Path.Combine(usb, "idVendor"))),
                        Hex(Read(Path.Combine(usb, "idProduct")))));
            }
        }

        return ports;
    }

    [GeneratedRegex(@"\s*\((COM\d+|LPT\d+)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PortSuffix();

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})[&+]PID_([0-9A-Fa-f]{4})")]
    private static partial Regex UsbIdPattern();
}
