using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;

namespace ChipSharp.Flasher.WchLink;

/// <summary>The mode a WCH-Link is in: RISC-V (what wlink and minichlink write a CH32V003 in) or ARM (CMSIS-DAP).</summary>
public enum WchLinkMode
{
    Riscv,
    Dap,
}

/// <summary>
/// The WCH-Links plugged in (the WCH-LinkE, WCH's own debug probe, about $5), by their USB ids, and what the flashers that
/// write through one say about it. A WCH-LinkE is a USB device of its own, not a serial port, which wlink and minichlink
/// find themselves: 1a86:8010 in its RISC-V mode (its interface 0 the probe, which on Windows needs a driver of its own;
/// interfaces 1 and 2 a USB serial port, on its TX and RX pins), 1a86:8012 in its ARM mode (CMSIS-DAP), in which they
/// cannot write a RISC-V chip. Its serial port is where the chip's console is (<see cref="ConsolePorts"/>).
/// </summary>
public static class WchLinks
{
    /// <summary>WCH's USB vendor id (Nanjing Qinheng), the WCH-Links' (and the CH340's).</summary>
    public const int VendorId = 0x1a86;

    /// <summary>A WCH-Link's product id in its RISC-V mode, and in its ARM (CMSIS-DAP) mode.</summary>
    public const int RiscvProductId = 0x8010, DapProductId = 0x8012;

    /// <summary>How the WCH-LinkE is wired to the board, for messages.</summary>
    public const string Wiring =
        "its SWDIO pin to the chip's SWIO pin (the CH32V003's PD1), GND to GND, and its 3V3 (or 5V) to the board's VCC " +
        "unless the board has power of its own";

    /// <summary>The WCH-Links among <paramref name="devices"/>, with the mode each is in.</summary>
    public static IReadOnlyList<(UsbDeviceInfo Device, WchLinkMode Mode)> Among(IEnumerable<UsbDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        return
        [
            .. devices.Where(device => device.VendorId == VendorId && device.ProductId is RiscvProductId or DapProductId)
                .Select(device => (device, device.ProductId == RiscvProductId ? WchLinkMode.Riscv : WchLinkMode.Dap)),
        ];
    }

    /// <summary>
    /// The WCH-Links' own serial ports among <paramref name="ports"/> (their USB serial interface, in either mode): the
    /// chip's console, when the WCH-LinkE's RX pin is wired to the chip's TX (USART1's, PD5 on the CH32V003).
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> ConsolePorts(IEnumerable<SerialPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(ports);

        return [.. ports.Where(port => port.IsUsb(VendorId, RiscvProductId) || port.IsUsb(VendorId, DapProductId))];
    }

    /// <summary>
    /// What a flash says of the console, for a target that has one: on which of <paramref name="ports"/> it is (the
    /// WCH-LinkE's serial port, or its ports when more than one is plugged in); null when the target has no console or no
    /// WCH-LinkE's port is there.
    /// </summary>
    public static string? ConsoleNote(TargetDescription target, IReadOnlyList<SerialPortInfo> ports)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ports);

        var found = ConsolePorts(ports);
        if (!target.HasConsole || found.Count == 0)
        {
            return null;
        }

        var where = found.Count == 1
            ? $"{found[0].Name}, the WCH-LinkE's serial port"
            : $"the serial port of one of the WCH-LinkEs ({string.Join(", ", found)})";
        return $"the console is on {where}: wire its RX pin to the chip's TX (USART1's, PD5 on the CH32V003)";
    }

    /// <summary>
    /// Where the target's flash starts, as the flashers give it: the origin of the memory region "link.code" names, else
    /// of its region "flash" ("0x08000000").
    /// </summary>
    public static string FlashOrigin(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var region = target.Setting("link.code") is { } code && target.Memory.TryGetValue(code, out var named)
            ? named
            : target.Memory.TryGetValue("flash", out var flash)
                ? flash
                : throw new ChipSharpException(
                    $"Target '{target.Name}' does not say where its flash is: its \"link.code\" must name the memory region " +
                    "the program is written into (e.g. \"flash\").");
        return string.Create(CultureInfo.InvariantCulture, $"0x{region.Origin:x8}");
    }

    /// <summary>
    /// Why <paramref name="tool"/> found no WCH-LinkE it could open, from the WCH-Links plugged in (null: this computer
    /// cannot say): none, one in its ARM mode (<paramref name="modeSwitch"/> says how to switch it), one Windows has no
    /// driver for (<paramref name="driver"/> says which to install), or, with <paramref name="needsWinUsb"/>, one whose
    /// driver the tool cannot use; else one that something else may have.
    /// </summary>
    internal static string UsbHint(string tool, IReadOnlyList<(UsbDeviceInfo Device, WchLinkMode Mode)>? links, string driver,
        string modeSwitch, bool needsWinUsb = false)
    {
        if (links is null)
        {
            return $"{tool} found no WCH-LinkE it could open: plug it in by USB, in its RISC-V mode; on Windows it needs " +
                $"{driver}.";
        }

        if (links.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"No WCH-LinkE is plugged in (no USB device has its ids, {VendorId:x4}:{RiscvProductId:x4} in its RISC-V mode " +
                $"or {VendorId:x4}:{DapProductId:x4} in its ARM mode): plug it in by USB, and wire it to the board: {Wiring}.");
        }

        if (!links.Any(link => link.Mode == WchLinkMode.Riscv))
        {
            return $"The {links[0].Device} is in its ARM mode (CMSIS-DAP), in which {tool} cannot write a RISC-V chip: " +
                $"switch it to its RISC-V mode ({modeSwitch}), then flash again.";
        }

        var device = links.First(link => link.Mode == WchLinkMode.Riscv).Device;
        if (OperatingSystem.IsWindows() && device.InterfaceDrivers.TryGetValue(0, out var probeDriver))
        {
            if (probeDriver is null)
            {
                return $"The {device} is plugged in, but Windows has no driver for its interface 0, which {tool} talks to: " +
                    $"install {driver}.";
            }

            if (needsWinUsb && !IsLibusbDriver(probeDriver))
            {
                return $"The {device} is plugged in, but its interface 0 has the driver {probeDriver}, which {tool} (through " +
                    $"libusb) cannot open it with: install {driver}.";
            }
        }

        return $"The {device} is plugged in, but {tool} could not open it: close what else may have it (WCH-LinkUtility, " +
            $"MounRiver Studio, another wlink or minichlink), unplug it and plug it in again; on Windows it needs {driver}.";
    }

    /// <summary>True for the drivers libusb opens a device with on Windows: WinUSB, libusbK, libusb0.</summary>
    private static bool IsLibusbDriver(string driver) =>
        driver.Equals("WinUSB", StringComparison.OrdinalIgnoreCase) || driver.Equals("libusbK", StringComparison.OrdinalIgnoreCase)
        || driver.Equals("libusb0", StringComparison.OrdinalIgnoreCase);
}
