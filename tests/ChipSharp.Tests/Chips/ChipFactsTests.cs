using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChipSharp.Chips;
using ChipSharp.Targets;
using ChipSharp.Tests.Pico;

namespace ChipSharp.Tests.Chips;

/// <summary>
/// One chip, one set of facts: what a target file says of its chip (the PFS154's I/O registers, interrupt sources and
/// ports; the STM8S103's peripherals, interrupts, ports and pin functions; the RP2040's interrupts) is what its
/// description says; and the addresses the RP2040's hand-written runtime defines are its official SVD's.
/// </summary>
public sealed partial class ChipFactsTests
{
    private static ulong Number(JsonNode? node) => SizeParser.Parse(node!.GetValue<string>());

    private static IEnumerable<int> Numbers(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<int>());

    [Fact]
    public void The_PFS154s_target_file_has_the_descriptions_IO_registers_interrupt_bits_and_ports()
    {
        var target = ChipFiles.Targets.Load("pfs154-bare");
        var chip = ChipFiles.Read("pfs154-bare");

        Assert.Equal("pfs154", chip.Name);
        var io = target.Settings["ioRegisters"]!.AsObject();
        Assert.Equal(io.Count, chip.Registers.Count());
        Assert.All(io, pair => Assert.True(chip.Register(pair.Key.ToUpperInvariant())?.Address == Number(pair.Value),
            $"{pair.Key}: the target says {pair.Value}"));
        Assert.All(chip.Peripherals, p => Assert.Equal(AddressSpace.Io, p.Space));
        var sources = target.Settings["interruptSources"]!.AsObject();
        Assert.Equal(sources.Select(pair => (pair.Key.ToUpperInvariant(), pair.Value!.GetValue<int>())),
            chip.Interrupts.Select(i => (i.Name, i.Request!.Bit)));
        Assert.All(chip.Interrupts, i => Assert.Equal(0, i.Number));
        // INTEN and INTRQ have a bit of each source's name
        Assert.All(chip.Interrupts, i => Assert.Contains(chip.Register("INTEN")!.Fields, f => f.Name == i.Name && f.Offset == i.Request!.Bit));
        foreach (var (name, node) in target.Settings["ports"]!.AsObject())
        {
            var port = chip.Ports.Single(p => p.Name == name);
            Assert.Equal(Number(node!["data"]), chip.Register(port.Registers["data"])!.Address);
            Assert.Equal(Number(node["control"]), chip.Register(port.Registers["control"])!.Address);
            Assert.Equal(Number(node["pullHigh"]), chip.Register(port.Registers["pullHigh"])!.Address);
            Assert.Equal(Number(node["digitalInput"]), chip.Register(port.Registers["digitalInput"])!.Address);
            Assert.Equal(Numbers(node["pins"]), port.Bits);
        }

        AssertPinNames(target, chip);
    }

    [Fact]
    public void The_STM8S103s_target_file_has_the_descriptions_peripherals_interrupts_ports_and_pin_functions()
    {
        var target = ChipFiles.Targets.Load("stm8s103-board");
        var chip = ChipFiles.Read("stm8s103-board");

        // each peripheral the description has starts where the target file says
        var peripherals = target.Settings["peripherals"]!.AsObject();
        var described = peripherals.Where(pair => chip.Peripheral(pair.Key.ToUpperInvariant()) is not null).ToList();
        Assert.Equal(["exti", "clk", "spi", "i2c", "uart1", "tim1", "tim2", "tim4", "adc1", "cfg", "itc"], described.Select(p => p.Key));
        Assert.All(described, pair => Assert.Equal(Number(pair.Value), chip.Peripheral(pair.Key.ToUpperInvariant())!.Base));

        // the same interrupt numbers, by the target's names
        var irqs = target.Settings["irqs"]!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<int>());
        Assert.Equal(irqs.Values.Order(), chip.Interrupts.Select(i => i.Number).Order());
        Assert.Equal(irqs["uart1Rx"], chip.Interrupts.Single(i => i.Name == "UART1_RX").Number);
        Assert.Equal(irqs["tim4Update"], chip.Interrupts.Single(i => i.Name == "TIM4_UPD_OVF").Number);
        Assert.Equal(irqs["extiC"], chip.Interrupts.Single(i => i.Name == "EXTI_PORTC").Number);

        // the ports' registers from their addresses on, as the target's "portRegisters" orders them
        var order = target.Settings["portRegisters"]!.AsObject();
        foreach (var (name, node) in target.Settings["ports"]!.AsObject())
        {
            var port = chip.Ports.Single(p => p.Name == name);
            var address = Number(node!["address"]);
            Assert.Equal(address + (ulong)order["odr"]!.GetValue<int>(), chip.Register(port.Registers["out"])!.Address);
            Assert.Equal(address + (ulong)order["idr"]!.GetValue<int>(), chip.Register(port.Registers["in"])!.Address);
            Assert.Equal(address + (ulong)order["ddr"]!.GetValue<int>(), chip.Register(port.Registers["direction"])!.Address);
            Assert.Equal(address + (ulong)order["cr1"]!.GetValue<int>(), chip.Register(port.Registers["control1"])!.Address);
            Assert.Equal(address + (ulong)order["cr2"]!.GetValue<int>(), chip.Register(port.Registers["control2"])!.Address);
            Assert.Equal(Numbers(node["pins"]), port.Bits);
        }

        // the pins the target gives each function have it in the description
        int Pin(string function) => chip.Pins.Single(p => p.Functions.Contains(function)).Number;
        var functions = target.Settings["pinFunctions"]!;
        Assert.Equal(Numbers(functions["uart0.tx"]), [Pin("UART1_TX")]);
        Assert.Equal(Numbers(functions["uart0.rx"]), [Pin("UART1_RX")]);
        Assert.Equal(Numbers(functions["adc"]).Order(),
            chip.Pins.Where(p => p.Functions.Any(f => f.StartsWith("AIN", StringComparison.Ordinal))).Select(p => p.Number).Order());
        // the PWM outputs are TIM2's channels (TIM1 is the runtime's clock)
        Assert.Equal(Numbers(functions["pwm"]).Order(),
            chip.Pins.Where(p => p.Functions.Any(f => TimerChannel().IsMatch(f))).Select(p => p.Number).Order());
        var i2c = target.Settings["i2c"]!["buses"]![0]!;
        Assert.Equal((Numbers(i2c["sda"]).Single(), Numbers(i2c["scl"]).Single()), (Pin("I2C_SDA"), Pin("I2C_SCL")));
        var spi = target.Settings["spi"]!["buses"]![0]!;
        Assert.Equal((Numbers(spi["sck"]).Single(), Numbers(spi["tx"]).Single(), Numbers(spi["rx"]).Single()),
            (Pin("SPI_SCK"), Pin("SPI_MOSI"), Pin("SPI_MISO")));
        AssertPinNames(target, chip);
    }

    [GeneratedRegex(@"^TIM2_CH\d$")]
    private static partial Regex TimerChannel();

    /// <summary>The target's "pinNames" are the description's pins, by number.</summary>
    private static void AssertPinNames(ChipSharp.Abstractions.TargetDescription target, ChipDescription chip)
    {
        var names = target.Settings["pinNames"]!.AsArray().Select((name, number) => (Name: name!.GetValue<string>(), number))
            .Where(pin => pin.Name.Length > 0);
        Assert.Equal(names.Select(pin => (pin.Name, pin.number)), chip.Pins.OrderBy(p => p.Number).Select(p => (p.Name, p.Number)));
    }

    [Fact]
    public void The_RP2040s_events_are_its_SVDs_interrupts_and_its_target_finds_its_SVD()
    {
        var pico = ChipFiles.Targets.Load("pico");
        var rp2040 = ChipFiles.Read("pico");

        Assert.Equal("RP2040", rp2040.Name);
        Assert.Equal(rp2040.Interrupts.Single(i => i.Name == "TIMER_IRQ_1").Number, pico.Settings["interrupts"]!["timer"]!["irq"]!.GetValue<int>());
        Assert.Equal(rp2040.Interrupts.Single(i => i.Name == "IO_IRQ_BANK0").Number, pico.Settings["interrupts"]!["pin"]!["irq"]!.GetValue<int>());
        // the uart event's IRQ 26 is past the chip's 26 interrupts: a "user IRQ" of the NVIC, which no peripheral raises
        Assert.Equal(26, rp2040.Interrupts.Max(i => i.Number) + 1);
        Assert.Equal(26, pico.Settings["interrupts"]!["uart"]!["irq"]!.GetValue<int>());
    }

    [GeneratedRegex(@"^#define (?<name>[A-Z][A-Z0-9_]*) 0x(?<value>[0-9a-f]+)u\r?$", RegexOptions.Multiline)]
    private static partial Regex Address();

    [Fact]
    public void The_register_addresses_the_RP2040s_runtime_defines_by_hand_are_its_SVDs()
    {
        var rp2040 = Svd.Read(ChipFiles.Svd);
        var folder = Path.Combine(Repository.Root, "src", "ChipSharp.Runtime.Rp2040");
        var checkedNames = new List<string>();

        foreach (var file in Directory.GetFiles(folder, "rp2040_*.c"))
        {
            foreach (Match match in Address().Matches(File.ReadAllText(file)))
            {
                if (rp2040.Register(match.Groups["name"].Value) is { } register)
                {
                    Assert.True(register.Address == ulong.Parse(match.Groups["value"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        $"{Path.GetFileName(file)}: {match.Value.Trim()}, but the SVD says 0x{register.Address:x}");
                    checkedNames.Add(register.FullName);
                }
            }
        }

        Assert.Contains("TIMER_TIMERAWL", checkedNames);
        Assert.Contains("RESETS_RESET", checkedNames);
        Assert.Contains("XOSC_CTRL", checkedNames);
        Assert.True(checkedNames.Distinct().Count() >= 15, string.Join(", ", checkedNames.Distinct()));
    }

    /// <summary>
    /// The ESP32-C5's target file and its description agree: the program's SRAM is the chip's, up to where the ROM's stack
    /// and data start; the trap dispatch has the CLIC's ids, and the events' are CPU interrupt lines (16 and up); the
    /// pins are GPIO 0-28; the description is found from the target, and takes its registers from Espressif's SVD.
    /// </summary>
    [Fact]
    public void The_ESP32_C5s_target_file_has_the_descriptions_SRAM_interrupt_ids_and_pins()
    {
        var target = ChipFiles.Targets.Load("esp32c5-devkitc");
        var chip = ChipFiles.Read("esp32c5-devkitc");
        ulong Constant(string name) => chip.Constants.Single(c => c.Name == name).Value;

        Assert.Equal(Path.Combine(ChipFiles.Folder, "esp32c5.json"), ChipCatalog.FindFile(target, ChipFiles.Folder));
        var sram = chip.Memory.Single(m => m.Name == "sram");
        Assert.Equal(sram.Origin, target.Memory["sram"].Origin);
        Assert.Equal(Constant("ROM_RESERVED_START"), target.Memory["sram"].End);
        Assert.InRange(target.Memory["sram"].End, sram.Origin, sram.Origin + sram.Size);
        Assert.Equal(Constant("CLIC_INTERRUPTS").ToString(CultureInfo.InvariantCulture), target.Setting("vectors"));
        Assert.Equal(48, chip.Peripheral("CLIC")!.Registers.Count(r => r.Name.StartsWith("INT_CTRL", StringComparison.Ordinal)));
        foreach (var (_, node) in target.Settings["interrupts"]!.AsObject())
        {
            Assert.InRange(node!["irq"]!.GetValue<int>(), (int)Constant("CLIC_EXT_INTR_NUM_OFFSET"), (int)Constant("CLIC_INTERRUPTS") - 1);
        }

        Assert.Equal(Enumerable.Range(0, 29), chip.Pins.Select(p => p.Number));
        Assert.Equal("GPIO27", chip.Pins.Single(p => p.Number == int.Parse(target.Pins["RGB_LED"], CultureInfo.InvariantCulture)).Name);
        Assert.Equal("rv32imac_zicsr_zifencei", target.Setting("march"));
        Assert.StartsWith("RV32IMAC", Svd.Read(ChipFiles.Esp32c5Svd).Cpu, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CH32V003's target file and its description agree: the flash and the SRAM are the chip's (the program linked
    /// at the flash, 0x08000000, the core starting at its alias, 0x00000000); the trap dispatch and the vector table at
    /// the start of flash have the PFIC's 39 numbers, the highest interrupt's and one; the pin event is the chip's
    /// EXTI7_0, and the timer's and the uart's, which the runtime's handlers of SysTick and USART1 call, are past the
    /// PFIC's numbers; the pins are the F4P6's 18, and those a program may use all but PD1 (SWIO) and PD7 (NRST); the
    /// description is found from the target, and takes its registers from WCH's SVD.
    /// </summary>
    [Fact]
    public void The_CH32V003s_target_file_has_the_descriptions_memory_vector_numbers_events_and_pins()
    {
        var target = ChipFiles.Targets.Load("ch32v003-evt");
        var chip = ChipFiles.Read("ch32v003-evt");
        ulong Constant(string name) => chip.Constants.Single(c => c.Name == name).Value;

        Assert.Equal(Path.Combine(ChipFiles.Folder, "ch32v003.json"), ChipCatalog.FindFile(target, ChipFiles.Folder));
        var flash = chip.Memory.Single(m => m.Name == "flash");
        Assert.Equal((0x08000000UL, flash.Size), (target.Memory["flash"].Origin, target.Memory["flash"].Size));
        Assert.Equal(flash.Origin, target.Memory["flash"].Origin);
        var sram = chip.Memory.Single(m => m.Name == "sram");
        Assert.Equal((sram.Origin, sram.Size), (target.Memory["sram"].Origin, target.Memory["sram"].Size));

        var vectors = (int)Constant("VECTORS");
        Assert.Equal(vectors.ToString(CultureInfo.InvariantCulture), target.Setting("vectors"));
        Assert.Equal(vectors, chip.Interrupts.Max(i => i.Number) + 1);
        var head = Assert.Single(target.Settings["link"]!["head"]!.AsArray());
        Assert.Equal((".ch32v003_vectors", 4 * vectors), (head!["section"]!.GetValue<string>(), head["size"]!.GetValue<int>()));
        Assert.Equal(0UL, target.Memory["flash"].Origin % Constant("MTVEC_ALIGN"));
        int Irq(string name) => target.Settings["interrupts"]![name]!["irq"]!.GetValue<int>();
        Assert.Equal("EXTI7_0", chip.Interrupts.Single(i => i.Number == Irq("pin")).Name);
        Assert.All((string[])["timer", "uart"], name => Assert.InRange(Irq(name), vectors, vectors + 7));
        Assert.Equal((12, 32), (chip.Interrupts.Single(i => i.Name == "SysTick").Number,
            chip.Interrupts.Single(i => i.Name == "USART1").Number));

        Assert.Equal([1, 2, .. Enumerable.Range(16, 16)], chip.Pins.Select(p => p.Number));
        Assert.Equal(["A", "C", "D"], chip.Ports.Select(p => p.Name));
        Assert.Equal("PD1", chip.Pins.Single(p => p.Functions.Contains("SWIO")).Name);
        Assert.Equal("PD7", chip.Pins.Single(p => p.Functions.Contains("NRST")).Name);
        var usable = target.Settings["pinFunctions"]!["pin"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        Assert.Equal(chip.Pins.Select(p => p.Number).Where(n => n is not (1 or 2 or 25 or 31)), usable); // the board's
        var chipFile = ChipFiles.Targets.Load("ch32v003");
        Assert.Equal(chip.Pins.Select(p => p.Number).Where(n => n is not (25 or 31)),
            chipFile.Settings["pinFunctions"]!["pin"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal((chip.Pins.Single(p => p.Functions.Contains("USART1_TX")).Number,
            chip.Pins.Single(p => p.Functions.Contains("USART1_RX")).Number),
            (target.Settings["pinFunctions"]!["uart0.tx"]![0]!.GetValue<int>(), target.Settings["pinFunctions"]!["uart0.rx"]![0]!.GetValue<int>()));
        Assert.Equal(("rv32ec", "rv32ec_zicsr"), (chip.Cpu, target.Setting("march")));
        Assert.Equal((Constant("PLL_HZ"), 2 * Constant("HSI_HZ")),
            (ulong.Parse(target.Setting("clockHz")!, CultureInfo.InvariantCulture), Constant("PLL_HZ")));
        Assert.Equal("WCH Ltd.", chip.Vendor); // from the SVD
        Assert.Equal(0x40021000UL, chip.Peripheral("RCC")!.Base);
    }
}
