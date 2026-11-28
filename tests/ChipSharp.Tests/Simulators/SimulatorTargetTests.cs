using System.Globalization;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Simulators;
using ChipSharp.Simulators.Pdk14;
using ChipSharp.Simulators.Stm8;
using ChipSharp.Tests.Sdcc;

namespace ChipSharp.Tests.Simulators;

/// <summary>
/// The simulators and the chips' target files (targets/chip/pfs154.json, stm8s103f3.json) say the same: the memories,
/// the ports and their pins, the I/O registers and peripherals' addresses, the interrupts and the vectors, so a change to
/// one that the other does not follow fails here.
/// </summary>
public sealed class SimulatorTargetTests
{
    private static readonly IntelHexImage Blank = IntelHexImage.FromBytes(0, new byte[4096], new bool[4096], 0);

    [Fact]
    public void The_PFS154_simulator_has_the_chip_files_memories_ports_registers_and_interrupts()
    {
        var target = SdccPrograms.Shipped.Load("pfs154");
        var chip = new Pfs154(Blank);

        Assert.Equal(Pfs154.RomWords * 2, Size(target, "program"));
        Assert.Equal(Pfs154.RamBytes, Size(target, "ram"));
        var ports = target.Settings["ports"]!.AsObject();
        Assert.Equal(ports.Select(port => port.Key), chip.Ports.Select(port => port.Letter.ToString()));
        foreach (var port in chip.Ports)
        {
            var pins = ports[port.Letter.ToString()]!["pins"]!.AsArray().Select(pin => (int)pin!);
            Assert.Equal(pins, Enumerable.Range(0, 8).Where(bit => (port.Pins & (1 << bit)) != 0));
            Assert.Equal(Number(ports[port.Letter.ToString()]!["data"]!), Pfs154.IoRegisters[$"p{char.ToLowerInvariant(port.Letter)}"]);
        }

        var registers = target.Settings["ioRegisters"]!.AsObject();
        foreach (var (name, address) in Pfs154.IoRegisters)
        {
            Assert.True(Number(registers[name]!) == address, $"{name}: the simulator has 0x{address:x2}, the chip file {registers[name]}");
        }

        var sources = target.Settings["interruptSources"]!.AsObject();
        foreach (var (name, bit) in Pfs154.InterruptBits)
        {
            Assert.Equal((int)sources[name]!, bit);
        }

        Assert.Equal(Pdk14Cpu.InterruptVector, Number(target.Settings["vectorAddresses"]!["interrupt"]!));
        var reserved = target.Settings["programReserved"]!.AsObject().Select(word => Number(JsonValue.Create(word.Key)!)).ToList();
        Assert.Contains(Pfs154.IhrcTrimWord, reserved);
        Assert.Contains(Pfs154.BandgapTrimWord, reserved);
        Assert.Equal(0x0200 | Pfs154.FactoryTrim, chip.Cpu.Rom[Pfs154.IhrcTrimWord]); // ret k: a program calls it to read the trim
        var pinNames = target.Settings["pinNames"]!.AsArray().Select(name => (string)name!).ToList();
        foreach (var port in chip.Ports)
        {
            foreach (var bit in Enumerable.Range(0, 8).Where(bit => (port.Pins & (1 << bit)) != 0))
            {
                Assert.Equal(pinNames[(port.Letter - 'A') * 8 + bit], port.PinName(bit));
            }
        }

        var clocks = target.Settings["clocks"]!;
        Assert.Equal(SimulatedChip.TicksPerSecond, (long)clocks["ihrcHz"]!);
        Assert.InRange(SimulatedChip.TicksPerSecond / (double)Pfs154.TicksPerIlrc, 0.99 * (double)clocks["ilrcHz"]!,
            1.01 * (double)clocks["ilrcHz"]!);
    }

    [Fact]
    public void The_STM8S103_simulator_has_the_chip_files_memories_ports_peripherals_and_interrupts()
    {
        var target = SdccPrograms.Shipped.Load("stm8s103f3");
        var chip = new Stm8s103(IntelHexImage.FromBytes(Stm8s103.FlashStart, new byte[Stm8s103.FlashSize], new bool[Stm8s103.FlashSize], 0));

        Assert.Equal((Number(target.Settings["memory"]!["flash"]!["origin"]!), Size(target, "flash")), (Stm8s103.FlashStart, Stm8s103.FlashSize));
        Assert.Equal(Stm8s103.RamSize, Size(target, "ram"));
        Assert.Equal((Number(target.Settings["memory"]!["eeprom"]!["origin"]!), Size(target, "eeprom")), (Stm8s103.EepromStart, Stm8s103.EepromSize));
        var ports = target.Settings["ports"]!.AsObject();
        foreach (var (letter, port) in ports)
        {
            var simulated = chip.Port(letter[0])!;
            Assert.Equal(port!["pins"]!.AsArray().Select(pin => (int)pin!), Enumerable.Range(0, 8).Where(bit => (simulated.Pins & (1 << bit)) != 0));
            Assert.Equal(Number(port["address"]!), Stm8s103.PortAddress(letter[0]));
        }

        Assert.All(chip.Ports.Where(port => port.Pins != 0), port => Assert.True(ports.ContainsKey(port.Letter.ToString())));
        var irqs = target.Settings["irqs"]!.AsObject();
        foreach (var (name, irq) in Stm8s103.Interrupts)
        {
            Assert.True((int)irqs[name]! == irq, $"{name}: the simulator has {irq}, the chip file {irqs[name]}");
        }

        var peripherals = target.Settings["peripherals"]!.AsObject();
        foreach (var (name, address) in Stm8s103.Peripherals)
        {
            Assert.True(Number(peripherals[name]!) == address, $"{name}: the simulator has 0x{address:x4}, the chip file {peripherals[name]}");
        }

        // the vectors: reset, trap, then interrupt n at 0x8000 + 4 * (2 + n)
        Assert.Equal(Stm8s103.FlashStart + 4 * 2, Stm8s103.FirstVector);
        Assert.Equal(32, (int)target.Settings["vectors"]!);
    }

    private static int Number(JsonNode node) => node.GetValueKind() == System.Text.Json.JsonValueKind.Number
        ? (int)node
        : int.Parse(((string)node!)[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static int Size(TargetDescription target, string memory) =>
        (int)ChipSharp.Targets.SizeParser.Parse((string)target.Settings["memory"]![memory]!["size"]!);
}
