using ChipSharp.Abstractions;
using ChipSharp.Flasher.Shared;
using ChipSharp.Targets;
using ChipSharp.Tests.Build;

namespace ChipSharp.Tests.Flash;

/// <summary>
/// A CH32V003 board for the tests of the flashers that write through a WCH-LinkE (wlink, minichlink), on the shipped
/// riscv32 target: 16 KB of flash at 0x08000000, 2 KB of SRAM, the image "bin", the flasher "wlink" for the chip
/// CH32V003; "ch32-test-console" has a console too, "ch32-test-minichlink" names minichlink instead. The USB devices and
/// serial ports a WCH-LinkE has, as Windows lists them.
/// </summary>
internal static class WchLinkTestBoard
{
    public const string Name = "ch32-test-board";

    /// <summary>A WCH-LinkE in its RISC-V mode, WCH's driver on its interface 0 (the probe), usbser on 2 (its serial port).</summary>
    public static readonly UsbDeviceInfo Riscv = new(0x1a86, 0x8010, "WCH-Link", "usbccgp")
    {
        InterfaceDrivers = new Dictionary<int, string?> { [0] = "WCHLinkDrv", [1] = "usbser" },
    };

    /// <summary>The same in its ARM mode (CMSIS-DAP).</summary>
    public static readonly UsbDeviceInfo Dap = new(0x1a86, 0x8012, "WCH-LinkDAP", "usbccgp");

    /// <summary>A USB-to-serial converter of WCH's (a CH340): WCH's vendor id, no WCH-Link.</summary>
    public static readonly UsbDeviceInfo Ch340 = new(0x1a86, 0x7523, "USB-SERIAL CH340", "CH341SER_A64");

    /// <summary>The WCH-LinkE's own serial port, and a CH340's.</summary>
    public static readonly SerialPortInfo LinkPort = new("COM7", "WCH-Link SERIAL", 0x1a86, 0x8010);

    public static readonly SerialPortInfo Ch340Port = new("COM3", "USB-SERIAL CH340", 0x1a86, 0x7523);

    /// <summary>Writes the test boards, next to a copy of the shipped targets, into <paramref name="folder"/>; returns it.</summary>
    public static string Write(string folder) => TargetFiles.WithShipped(folder,
        (Name, """
            {
              "name": "ch32-test-board",
              "inherits": "riscv32",
              "memory": {
                "flash": { "origin": "0x08000000", "size": "16K" },
                "ram":   { "origin": "0x20000000", "size": "2K" }
              },
              "image": "bin",
              "flasher": "wlink",
              "wlink": { "chip": "CH32V003" }
            }
            """),
        ("ch32-test-console", """{ "inherits": "ch32-test-board", "capabilities": { "console": true } }"""),
        ("ch32-test-minichlink", """{ "inherits": "ch32-test-board", "flasher": "minichlink" }"""));

    /// <summary>The test board <paramref name="name"/>, from the targets written into <paramref name="folder"/>.</summary>
    public static TargetDescription Load(string folder, string name = Name) => new TargetLoader(Write(folder)).Load(name);
}
