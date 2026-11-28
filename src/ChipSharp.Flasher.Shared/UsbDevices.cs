using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ChipSharp.Flasher.Shared;

/// <summary>
/// A USB device plugged into this computer: its vendor and product ids, what the system calls it ("STM32 STLink"), and
/// on Windows the driver it has (its service: "WinUSB", "libusbK"; null when it has none).
/// </summary>
public sealed record UsbDeviceInfo(int VendorId, int ProductId, string? Description = null, string? Driver = null)
{
    private static readonly IReadOnlyDictionary<int, string?> NoInterfaces = new Dictionary<int, string?>();

    /// <summary>
    /// On Windows, the drivers of a composite device's interfaces, by number (its key VID_1A86&amp;PID_8010&amp;MI_00 is
    /// interface 0): "WinUSB", "WCHLinkDrv", "usbser"; null for one Windows has no driver for. Empty when not known (Linux;
    /// a device that is not composite, whose <see cref="Driver"/> is its own).
    /// </summary>
    public IReadOnlyDictionary<int, string?> InterfaceDrivers { get; init; } = NoInterfaces;

    /// <summary>"STM32 STLink (USB 0483:3748)".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Description ?? "a USB device"} (USB {VendorId:x4}:{ProductId:x4})");
}

/// <summary>
/// The USB devices plugged in now, for a flasher whose programmer is a USB device of its own rather than a serial port
/// (an ST-LINK, which stm8flash finds itself): to say which kind is there, or that none is.
/// </summary>
public static partial class UsbDevices
{
    /// <summary>CM_Locate_DevNode's answer for a device that is plugged in.</summary>
    private const int Found = 0;

    /// <summary>
    /// The devices of the vendor <paramref name="vendorId"/> there are now, one for each (not one for each of its
    /// interfaces); null when this computer cannot say (macOS; a registry or folder that may not be read). On Windows:
    /// the registry's USB devices (Enum\USB, every device ever plugged in), those plugged in now (CM_Locate_DevNode),
    /// each with the drivers of its interfaces plugged in now (<see cref="UsbDeviceInfo.InterfaceDrivers"/>); on Linux
    /// /sys/bus/usb/devices.
    /// </summary>
    public static IReadOnlyList<UsbDeviceInfo>? List(int vendorId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsDevices(vendorId);
            }

            return OperatingSystem.IsLinux() ? LinuxDevices(vendorId) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                      or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The interface number in a device's name: 0 for VID_1A86&amp;PID_8010&amp;MI_00; null for a device's own name.</summary>
    internal static int? InterfaceNumber(string deviceName)
    {
        var match = InterfacePattern().Match(deviceName);
        return match.Success ? int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : null;
    }

    [SupportedOSPlatform("windows")]
    private static List<UsbDeviceInfo> WindowsDevices(int vendorId)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        var names = root?.GetSubKeyNames() ?? [];

        // the instances plugged in now of a device or interface key, each with its name and driver (its service)
        IEnumerable<(string? Name, string? Driver)> Present(string deviceName)
        {
            using var device = root!.OpenSubKey(deviceName);
            foreach (var instanceName in device?.GetSubKeyNames() ?? [])
            {
                if (CM_Locate_DevNodeW(out _, $@"USB\{deviceName}\{instanceName}", 0) != Found)
                {
                    continue;
                }

                using var instance = device!.OpenSubKey(instanceName);
                var name = instance?.GetValue("FriendlyName") as string
                    ?? (instance?.GetValue("DeviceDesc") as string)?.Split(';')[^1];
                var driver = instance?.GetValue("Service") as string;
                yield return (string.IsNullOrWhiteSpace(name) ? null : name.Trim(), string.IsNullOrWhiteSpace(driver) ? null : driver);
            }
        }

        // VID_1A86&PID_8010 is the device; VID_1A86&PID_8010&MI_00 its interface 0, with a driver of its own
        var interfaces = new Dictionary<(int Vendor, int Product), Dictionary<int, string?>>();
        foreach (var deviceName in names)
        {
            if (SerialPorts.UsbIds(deviceName) is { } ids && ids.Vendor == vendorId && InterfaceNumber(deviceName) is { } number)
            {
                foreach (var (_, driver) in Present(deviceName))
                {
                    if (!interfaces.TryGetValue(ids, out var drivers))
                    {
                        drivers = new Dictionary<int, string?>();
                        interfaces[ids] = drivers;
                    }

                    drivers[number] = driver;
                }
            }
        }

        var devices = new List<UsbDeviceInfo>();
        foreach (var deviceName in names)
        {
            if (SerialPorts.UsbIds(deviceName) is not { } ids || ids.Vendor != vendorId || InterfaceNumber(deviceName) is not null)
            {
                continue;
            }

            foreach (var (name, driver) in Present(deviceName))
            {
                devices.Add(new UsbDeviceInfo(ids.Vendor, ids.Product, name, driver)
                {
                    InterfaceDrivers = interfaces.TryGetValue(ids, out var drivers) ? drivers : new Dictionary<int, string?>(),
                });
            }
        }

        return devices;
    }

    private static List<UsbDeviceInfo> LinuxDevices(int vendorId)
    {
        const string Devices = "/sys/bus/usb/devices";
        if (!Directory.Exists(Devices))
        {
            return [];
        }

        string? Read(string folder, string file) =>
            File.Exists(Path.Combine(folder, file)) ? File.ReadAllText(Path.Combine(folder, file)).Trim() : null;
        int? Hex(string? text) =>
            int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;

        // 1-1.2 is a device, 1-1.2:1.0 one of its interfaces (which has no idVendor)
        return
        [
            .. Directory.GetDirectories(Devices).Order(StringComparer.Ordinal)
                .Select(folder => (Folder: folder, Vendor: Hex(Read(folder, "idVendor")), Product: Hex(Read(folder, "idProduct"))))
                .Where(device => device.Vendor == vendorId && device.Product is not null)
                .Select(device => new UsbDeviceInfo(device.Vendor!.Value, device.Product!.Value, Read(device.Folder, "product"))),
        ];
    }

    /// <summary>Finds a device by its instance id (USB\VID_0483&amp;PID_3748\...); flags 0: only one plugged in now.</summary>
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [GeneratedRegex(@"&MI_([0-9A-Fa-f]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex InterfacePattern();
}
