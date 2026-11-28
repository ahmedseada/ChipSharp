using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Toolchain.Sdcc;

namespace ChipSharp.Tests.Sdcc;

/// <summary>
/// The target files of the SDCC chips: the Padauk PFS154 (pdk14 -> pfs154 -> pfs154-bare) and the STM8S103
/// (stm8 -> stm8s103f3 -> stm8s103-board): what they inherit, their memory (the PFS154's in 14-bit words of two bytes),
/// their capabilities, the chips' facts as data (pins, ports, registers, interrupts), and the SDCC settings read from
/// them.
/// </summary>
public sealed class SdccTargetTests
{
    private static readonly IReadOnlyList<string> SdccChips = ["pfs154-bare", "stm8s103-board"];

    [Fact]
    public void The_PFS154_is_a_pdk14_chip_built_through_C_with_SDCC_into_a_hex_file()
    {
        var pfs = SdccPrograms.Pfs154;

        Assert.Equal(["pfs154-bare", "pfs154", "pdk14"], pfs.InheritanceChain);
        Assert.True(pfs.IsComplete);
        // the chip's runtime, on the pdk14 one (src/ChipSharp.Runtime.Pfs154, src/ChipSharp.Runtime.Pdk14)
        Assert.Equal(("c", "sdcc", "pfs154", "hex"), (pfs.Backend, pfs.Toolchain, pfs.Runtime, pfs.Image));
        Assert.Null(pfs.Triple);
        Assert.Equal((2, Endianness.Little), (pfs.PointerSize, pfs.Endian));
        // 2K words of program memory, two bytes of the .ihx each; 128 bytes of RAM
        Assert.Equal(new MemoryRegion(0, 4096), pfs.Memory["program"]);
        Assert.Equal("2", pfs.Setting("memory.program.wordSize"));
        Assert.Equal(new MemoryRegion(0, 128), pfs.Memory["ram"]);
        Assert.Equal(("2000000", "ihrc", "8"), (pfs.Setting("clockHz"), pfs.Setting("clock.source"), pfs.Setting("clock.divider")));
        Assert.Equal("4", pfs.Pins["LED"]);
    }

    [Fact]
    public void The_PFS154_has_no_heap_no_console_no_UART_and_no_64_bit_double_and_recurses_only_through_reentrant()
    {
        var pfs = SdccPrograms.Pfs154;

        Assert.False(pfs.HasHeap);
        Assert.False(pfs.HasConsole);
        Assert.Equal(("0", "0", "3"), (pfs.Capabilities["uart"], pfs.Capabilities["adc"], pfs.Capabilities["timers"]));
        Assert.False(pfs.HasCapability("double64"));
        Assert.Equal("false", pfs.Capabilities["double64"]);
        Assert.Equal("reentrant", pfs.Capabilities["recursion"]);
        Assert.False(pfs.HasCapability("hwMultiply"));
        // SDCC's C on the pdk14: int 16 bits, double a float, a pointer to RAM alone 1 byte
        Assert.Equal(("2", "4", "1", "2"), (pfs.Setting("c.int"), pfs.Setting("c.double"), pfs.Setting("c.nearPointer"),
            pfs.Setting("c.pointer")));
    }

    [Fact]
    public void The_PFS154s_pins_ports_registers_and_interrupts_are_data()
    {
        var pfs = SdccPrograms.Pfs154;

        // 14 I/O pins, numbered port * 8 + bit: PA0, PA3-PA7, PB0-PB7
        Assert.Equal(["PA0", "PA3", "PA4", "PA5", "PA6", "PA7", "PB0", "PB1", "PB2", "PB3", "PB4", "PB5", "PB6", "PB7"],
            PinNames(pfs).Where(name => name.Length > 0));
        Assert.Equal([0, 3, 4, 5, 6, 7], Numbers(pfs.Settings["ports"]!["A"]!["pins"]));
        Assert.Equal(("0x10", "0x11", "0x12"), (pfs.Setting("ports.A.data"), pfs.Setting("ports.A.control"),
            pfs.Setting("ports.A.pullHigh")));
        Assert.Equal(("0x04", "0x05", "0x14"), (pfs.Setting("ioRegisters.inten"), pfs.Setting("ioRegisters.intrq"),
            pfs.Setting("ioRegisters.pb")));
        // one interrupt vector (word 0x10); its sources are bits of INTEN and INTRQ
        Assert.Equal(("2", "0x010"), (pfs.Setting("vectors"), pfs.Setting("vectorAddresses.interrupt")));
        Assert.Equal(("0", "2", "6", "7"), (pfs.Setting("interruptSources.pa0"), pfs.Setting("interruptSources.t16"),
            pfs.Setting("interruptSources.tm2"), pfs.Setting("interruptSources.tm3")));
        Assert.Contains("fuse", pfs.Setting("programReserved.0x7ff"));
    }

    [Fact]
    public void The_STM8S103_board_is_an_STM8S103F3_at_16_MHz_with_its_LED_on_PB5()
    {
        var board = SdccPrograms.Stm8s103;

        Assert.Equal(["stm8s103-board", "stm8s103f3", "stm8"], board.InheritanceChain);
        Assert.True(board.IsComplete);
        Assert.Equal(("c", "sdcc", "stm8", "hex"), (board.Backend, board.Toolchain, board.Runtime, board.Image));
        Assert.Equal((2, Endianness.Big), (board.PointerSize, board.Endian));
        Assert.Equal(new MemoryRegion(0x8000, 8 << 10), board.Memory["flash"]);
        Assert.Equal(new MemoryRegion(0, 1 << 10), board.Memory["ram"]);
        Assert.Equal(new MemoryRegion(0x4000, 640), board.Memory["eeprom"]);
        Assert.Equal("16000000", board.Setting("clockHz"));
        Assert.Equal("13", board.Pins["LED"]);
        Assert.Equal("PB5", PinNames(board)[13]);
        Assert.Equal("true", board.Setting("ledActiveLow"));
    }

    [Fact]
    public void The_STM8S103s_peripherals_interrupts_and_capabilities_are_data()
    {
        var board = SdccPrograms.Stm8s103;

        Assert.False(board.HasHeap);
        Assert.False(board.HasConsole);
        Assert.False(board.HasCapability("double64"));
        Assert.True(board.HasCapability("recursion"));
        Assert.Equal(("1", "5", "5"), (board.Capabilities["uart"], board.Capabilities["adc"], board.Capabilities["pwm"]));
        Assert.True(board.HasCapability("i2c") && board.HasCapability("spi"));
        Assert.Equal(("32", "18", "23"), (board.Setting("vectors"), board.Setting("irqs.uart1Rx"), board.Setting("irqs.tim4Update")));
        Assert.Equal(("0x5230", "0x5340", "0x50c0"), (board.Setting("peripherals.uart1"), board.Setting("peripherals.tim4"),
            board.Setting("peripherals.clk")));
        Assert.Equal("0x5005", board.Setting("ports.B.address"));
        // UART1's TX on PD5, RX on PD6
        Assert.Equal(("PD5", "PD6"), (PinNames(board)[29], PinNames(board)[30]));
        Assert.Equal([29], Numbers(board.Settings["pinFunctions"]!["uart0.tx"]));
    }

    [Theory]
    [InlineData("pfs154-bare")]
    [InlineData("stm8s103-board")]
    public void Every_pin_the_chips_file_gives_a_port_or_peripheral_is_one_of_its_named_pins(string name)
    {
        var target = SdccPrograms.Shipped.Load(name);
        var names = PinNames(target);

        var portPins = ((JsonObject)target.Settings["ports"]!)
            .SelectMany(port => Numbers(port.Value!["pins"]).Select(bit => (port.Key, Pin: ("ABCD".IndexOf(port.Key[0]) * 8) + bit)))
            .ToList();
        var functionPins = target.Settings["pinFunctions"] is JsonObject functions
            ? functions.SelectMany(function => Numbers(function.Value))
            : [];
        var busPins = new[] { "i2c", "spi" }
            .SelectMany(bus => target.Settings[bus]?["buses"] is JsonArray buses ? buses : [])
            .SelectMany(bus => ((JsonObject)bus!).SelectMany(pins => Numbers(pins.Value)));

        Assert.All(portPins, pin => Assert.Equal($"P{pin.Key}{pin.Pin % 8}", names[pin.Pin]));
        Assert.Equal(portPins.Count, names.Count(n => n.Length > 0));
        Assert.All(functionPins.Concat(busPins).Append(int.Parse(target.Pins["LED"])), pin => Assert.NotEqual("", names[pin]));
    }

    [Fact]
    public void The_SDCC_settings_are_the_port_its_flags_and_the_link_regions()
    {
        var pfs = SdccSettings.Read(SdccPrograms.Pfs154);
        var stm = SdccSettings.Read(SdccPrograms.Stm8s103);

        Assert.Equal(("pdk14", "program", "ram", 16UL, 2UL), (pfs.Port, pfs.Code.Name, pfs.Data.Name, pfs.StackSize, pfs.CodeWordSize));
        Assert.Equal(["--opt-code-size"], pfs.Flags);
        // the Padauk's code goes where SDCC puts it, after the vectors at words 0 and 0x10
        Assert.Empty(pfs.PlacementFlags);
        Assert.Equal(("stm8", "flash", "ram", 256UL, 1UL), (stm.Port, stm.Code.Name, stm.Data.Name, stm.StackSize, stm.CodeWordSize));
        Assert.Equal(["--opt-code-size"], stm.Flags);
        Assert.Equal(["--code-loc", "0x8000"], stm.PlacementFlags);
        Assert.All(SdccChips, name => Assert.True(SdccSettings.Applies(SdccPrograms.Shipped.Load(name))));
        Assert.False(SdccSettings.Applies(SdccPrograms.Shipped.Load("uno")));
    }

    [Fact]
    public void An_architecture_alone_has_no_memory_to_build_for()
    {
        var error = Assert.Throws<ChipSharpException>(() => SdccSettings.Read(SdccPrograms.Shipped.Load("pdk14")));

        Assert.Equal("Target 'pdk14': 'link.code' is \"program\", but the target has no memory region of that name; " +
            "build for a chip or board that gives them.", error.Message);
    }

    /// <summary>The target's "pinNames", by pin number.</summary>
    private static List<string> PinNames(TargetDescription target) =>
        [.. ((JsonArray)target.Settings["pinNames"]!).Select(name => name!.GetValue<string>())];

    private static List<int> Numbers(JsonNode? list) => [.. ((JsonArray)list!).Select(item => item!.GetValue<int>())];
}
