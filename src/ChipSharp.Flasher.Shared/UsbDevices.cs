using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ChipSharp.Flasher.Shared;

/// <summary>
/// A USB device plugged into this computer: its vendor and product ids, what the system calls it ("STM32 STLink"), and
/// on Windows the driver it has (its service: "WinUSB", "libusbK"; null when it has none).
/// </summary>
public sealed record UsbDeviceInfo(int VendorId, int ProductId, string? Description = null, string? Driver = null)
{
    /// <summary>"STM32 STLink (USB 0483:3748)".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"{Description ?? "a USB device"} (USB {VendorId:x4}:{ProductId:x4})");
}

/// <summary>
/// The USB devices plugged in now, for a flasher whose programmer is a USB device of its own rather than a serial port
/// (an ST-LINK, which stm8flash finds itself): to say which kind is there, or that none is.
/// </summary>
public static class UsbDevices
{
    /// <summary>CM_Locate_DevNode's answer for a device that is plugged in.</summary>
    private const int Found = 0;

    /// <summary>
    /// The devices of the vendor <paramref name="vendorId"/> there are now, one for each (not one for each of its
    /// interfaces); null when this computer cannot say (macOS; a registry or folder that may not be read). On Windows:
    /// the registry's USB devices (Enum\USB, every device ever plugged in), those plugged in now (CM_Locate_DevNode);
    /// on Linux /sys/bus/usb/devices.
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

    [SupportedOSPlatform("windows")]
    private static List<UsbDeviceInfo> WindowsDevices(int vendorId)
    {
        var devices = new List<UsbDeviceInfo>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        foreach (var deviceName in root?.GetSubKeyNames() ?? [])
        {
            // VID_0483&PID_374B is the device; VID_0483&PID_374B&MI_00 one of its interfaces
            if (SerialPorts.UsbIds(deviceName) is not { } ids || ids.Vendor != vendorId
                || deviceName.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
                devices.Add(new UsbDeviceInfo(ids.Vendor, ids.Product, string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                    string.IsNullOrWhiteSpace(driver) ? null : driver));
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
}
