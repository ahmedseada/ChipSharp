using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Chips;

namespace ChipSharp.Tests.Chips;

/// <summary>
/// The two readers of a chip's description: ChipSharp's JSON (fields as a bit, "msb:lsb" or an object with values,
/// derived peripherals, the Padauk's I/O space, pins numbered port * 8 + bit, the checks of a description that is wrong)
/// and CMSIS-SVD (derived peripherals, arrays, clusters, the three ways to give a field's bits, enumerated values, the
/// defaults), on small descriptions and on the RP2040's official SVD.
/// </summary>
public sealed class ChipReaderTests
{
    private const string Small = """
        {
          // a chip of two peripherals
          "name": "tiny", "vendor": "Nobody", "cpu": "stm8", "endian": "big", "width": 8,
          "memory": { "flash": { "origin": "0x8000", "size": "8K", "access": "rx" } },
          "peripherals": {
            "PA": { "base": "0x5000", "registers": { "ODR": { "offset": 0 }, "DDR": { "offset": 2, "reset": "0x00" } } },
            "PB": { "base": "0x5005", "derivedFrom": "PA" },
            "CLK": {
              "base": "0x50c0",
              "registers": {
                "CKDIVR": { "offset": 6, "reset": 24, "fields": {
                  "HSIDIV": { "bits": "4:3", "values": { "DIV1": 0, "DIV8": { "value": 3, "description": "2 MHz" } } },
                  "CPUDIV": "2:0" } },
                "ICKR": { "offset": 0, "access": "read-only", "fields": { "HSIRDY": { "bits": 1 }, "HSIEN": { "bits": 0, "access": "read-write" } } }
              }
            },
            "SYSTEM": { "space": "io", "prefix": "", "registers": { "INTRQ": { "address": "0x05" } } }
          },
          "interrupts": { "TIM4": { "number": 23 }, "T16": { "number": 0, "request": { "register": "INTRQ", "enable": "INTRQ", "bit": 2 } } },
          "ports": { "B": { "registers": { "out": "PB_ODR" } } },
          "pins": { "PB5": { "functions": ["I2C_SDA"] }, "PD6": { }, "X": { "number": 40 } },
          "constants": { "SWR_HSI": "0xe1" }
        }
        """;

    [Fact]
    public void ChipSharps_description_has_registers_by_C_name_fields_values_derived_peripherals_pins_and_ports()
    {
        var chip = ChipJson.Parse(Small, "tiny.json");

        Assert.Equal(("tiny", "Nobody", "stm8", "big", 8), (chip.Name, chip.Vendor, chip.Cpu, chip.Endian, chip.Width));
        Assert.Equal(new ChipMemory("flash", 0x8000, 8192, "rx", null), Assert.Single(chip.Memory));
        Assert.Equal(["PA_ODR", "PA_DDR", "PB_ODR", "PB_DDR", "CLK_CKDIVR", "CLK_ICKR", "INTRQ"], chip.Registers.Select(r => r.FullName));
        Assert.Equal(0x5007UL, chip.Register("PB_DDR")!.Address);
        Assert.Equal(AddressSpace.Io, chip.PeripheralOf(chip.Register("INTRQ")!).Space);
        var divider = chip.Register("CLK_CKDIVR")!;
        Assert.Equal((0x50c6UL, 8, 24UL), (divider.Address, divider.Size, divider.Reset!.Value));
        var hsidiv = divider.Fields[0];
        Assert.Equal(("HSIDIV", 3, 2, 0x18UL), (hsidiv.Name, hsidiv.Offset, hsidiv.Width, hsidiv.Mask));
        Assert.Equal([new ChipFieldValue("DIV1", 0, null), new ChipFieldValue("DIV8", 3, "2 MHz")], hsidiv.Values);
        Assert.Equal(("CPUDIV", 0, 3), (divider.Fields[1].Name, divider.Fields[1].Offset, divider.Fields[1].Width));
        var ready = chip.Register("CLK_ICKR")!;
        Assert.Equal([RegisterAccess.ReadOnly, RegisterAccess.ReadWrite], ready.Fields.Select(f => f.Access));
        Assert.Equal([(23, null), (0, new ChipInterruptRequest("INTRQ", "INTRQ", 2))],
            chip.Interrupts.Select(i => (i.Number, i.Request)));
        Assert.Equal([("PB5", 13, "B", 5), ("PD6", 30, "D", 6), ("X", 40, null, null)],
            chip.Pins.Select(p => (p.Name, p.Number, p.Port, p.Bit)));
        Assert.Equal([5], Assert.Single(chip.Ports).Bits);
        Assert.Equal(new ChipConstant("SWR_HSI", 0xe1, null), Assert.Single(chip.Constants));
    }

    [Theory]
    [InlineData("""{ "name": "x", "peripherals": { "A": { "registers": { "R": { "offset": 0, "fields": { "F": "9:8" } } } } } }""",
        "A_R.F (bits 9:8) is outside the register's 8 bits")]
    [InlineData("""{ "name": "x", "peripherals": { "A": { "registers": { "R": { "offset": 0, "fields": { "F": "3:1", "G": 2 } } } } } }""",
        "overlaps another field of the register")]
    [InlineData("""{ "name": "x", "peripherals": { "A": { "registers": { "R": { "offset": 0 } } }, "B": { "prefix": "A", "registers": { "R": { "offset": 1 } } } } }""",
        "two registers are named A_R")]
    [InlineData("""{ "name": "x", "ports": { "A": { "registers": { "out": "PA_ODR" } } } }""",
        "port A's out register PA_ODR is not one of the chip's registers")]
    [InlineData("""{ "name": "x", "peripherals": { "B": { "derivedFrom": "A" } } }""", "derived from A, which is not described before it")]
    [InlineData("""{ "name": "x", "peripherals": { "A": { "registers": { "R": { "offset": 0, "fields": { "F": { "bits": "1:0", "values": { "BIG": 4 } } } } } } } }""",
        "the value BIG (4) does not fit in its 2 bits")]
    [InlineData("""{ "name": "x", "pins": { "PA1": {}, "Q": { "number": 1 } } }""", "pins PA1 and Q have the same number, 1")]
    [InlineData("""{ "peripherals": {} }""", "\"name\" is missing")]
    public void A_description_that_is_wrong_says_where(string json, string message)
    {
        var error = Assert.Throws<ChipSharpException>(() => ChipJson.Parse(json, "x.json"));

        Assert.StartsWith("x.json: ", error.Message);
        Assert.Contains(message, error.Message);
    }

    private const string SmallSvd = """
        <?xml version="1.0" encoding="utf-8"?>
        <device schemaVersion="1.3">
          <vendor>Somebody</vendor>
          <name>CHIP5</name>
          <description>A   chip
             of two lines</description>
          <width>32</width>
          <size>32</size>
          <access>read-write</access>
          <resetValue>0x00000000</resetValue>
          <cpu><name>RV32IMAC</name><endian>little</endian></cpu>
          <peripherals>
            <peripheral>
              <name>GPIO</name>
              <baseAddress>0x60004000</baseAddress>
              <interrupt><name>GPIO</name><value>16</value></interrupt>
              <registers>
                <register>
                  <name>OUT</name><addressOffset>0x4</addressOffset><resetValue>0x1</resetValue>
                  <fields>
                    <field><name>DATA</name><bitOffset>0</bitOffset><bitWidth>8</bitWidth>
                      <enumeratedValues><enumeratedValue><name>ALL</name><value>0xff</value></enumeratedValue>
                        <enumeratedValue><name>ODD</name><value>#01010101</value></enumeratedValue>
                        <enumeratedValue><name>ANY</name><value>#xxxx0000</value></enumeratedValue></enumeratedValues></field>
                    <field><name>EN</name><lsb>8</lsb><msb>9</msb><access>write-only</access></field>
                    <field><name>MODE</name><bitRange>[15:12]</bitRange></field>
                  </fields>
                </register>
                <register><name>PIN%s</name><dim>3</dim><dimIncrement>4</dimIncrement><addressOffset>0x10</addressOffset><size>16</size></register>
                <register derivedFrom="OUT"><name>OUT_SET</name><addressOffset>0x8</addressOffset></register>
                <cluster>
                  <name>CH[%s]</name><dim>2</dim><dimIncrement>0x10</dimIncrement><dimIndex>A,B</dimIndex><addressOffset>0x100</addressOffset>
                  <register><name>CONF</name><addressOffset>0x4</addressOffset><access>read-only</access></register>
                </cluster>
              </registers>
            </peripheral>
            <peripheral derivedFrom="GPIO">
              <name>GPIO1</name>
              <baseAddress>0x60005000</baseAddress>
              <interrupt><name>GPIO1</name><value>17</value></interrupt>
            </peripheral>
          </peripherals>
        </device>
        """;

    [Fact]
    public void An_SVD_file_has_derived_peripherals_arrays_clusters_the_three_kinds_of_bits_values_and_defaults()
    {
        var chip = Svd.Read(new MemoryStream(Encoding.UTF8.GetBytes(SmallSvd)), "chip5.svd");

        Assert.Equal(("CHIP5", "Somebody", "RV32IMAC", "little", "A chip of two lines"),
            (chip.Name, chip.Vendor, chip.Cpu, chip.Endian, chip.Description));
        Assert.Equal(
            [
                ("GPIO_OUT", 0x60004004UL, 32), ("GPIO_PIN0", 0x60004010UL, 16), ("GPIO_PIN1", 0x60004014UL, 16),
                ("GPIO_PIN2", 0x60004018UL, 16), ("GPIO_OUT_SET", 0x60004008UL, 32), ("GPIO_CHA_CONF", 0x60004104UL, 32),
                ("GPIO_CHB_CONF", 0x60004114UL, 32),
            ],
            chip.Peripheral("GPIO")!.Registers.Select(r => (r.FullName, r.Address, r.Size)));
        Assert.Equal(0x60005004UL, chip.Register("GPIO1_OUT")!.Address);
        var output = chip.Register("GPIO_OUT")!;
        Assert.Equal(1UL, output.Reset);
        Assert.Equal([("DATA", 0, 8), ("EN", 8, 2), ("MODE", 12, 4)], output.Fields.Select(f => (f.Name, f.Offset, f.Width)));
        Assert.Equal([("ALL", 0xffUL), ("ODD", 0x55UL)], output.Fields[0].Values.Select(v => (v.Name, v.Value)));
        Assert.Equal(RegisterAccess.WriteOnly, output.Fields[1].Access);
        Assert.Equal(output.Fields.Select(f => (f.Name, f.Mask)), chip.Register("GPIO_OUT_SET")!.Fields.Select(f => (f.Name, f.Mask)));
        Assert.Equal(RegisterAccess.ReadOnly, chip.Register("GPIO_CHB_CONF")!.Access);
        Assert.Equal([("GPIO", 16), ("GPIO1", 17)], chip.Interrupts.Select(i => (i.Name, i.Number)));
    }

    [Fact]
    public void The_RP2040s_official_SVD_reads_with_its_peripherals_registers_and_interrupts()
    {
        var rp2040 = Svd.Read(ChipFiles.Svd);

        Assert.Equal(("RP2040", "Raspberry Pi", "CM0PLUS"), (rp2040.Name, rp2040.Vendor, rp2040.Cpu));
        Assert.Equal(36, rp2040.Peripherals.Count);
        Assert.Equal(1015, rp2040.Registers.Count() - DerivedRegisters(rp2040));
        Assert.Equal(0x40054028UL, rp2040.Register("TIMER_TIMERAWL")!.Address);
        Assert.Equal(32, rp2040.Register("TIMER_TIMERAWL")!.Size);
        Assert.Equal(0x40038000UL, rp2040.Register("UART1_UARTDR")!.Address); // UART1 is derived from UART0
        Assert.Equal(rp2040.Register("UART0_UARTLCR_H")!.Fields.Select(f => (f.Name, f.Mask)),
            rp2040.Register("UART1_UARTLCR_H")!.Fields.Select(f => (f.Name, f.Mask)));
        var wlen = rp2040.Register("UART0_UARTLCR_H")!.Fields.Single(f => f.Name == "WLEN");
        Assert.Equal((5, 2), (wlen.Offset, wlen.Width));
        Assert.Equal(26, rp2040.Interrupts.Count);
        Assert.Equal([("TIMER_IRQ_0", 0), ("TIMER_IRQ_1", 1), ("IO_IRQ_BANK0", 13), ("UART0_IRQ", 20), ("UART1_IRQ", 21)],
            rp2040.Interrupts.Where(i => i.Number is 0 or 1 or 13 or 20 or 21).Select(i => (i.Name, i.Number)));
    }

    /// <summary>The registers the peripherals derived from others have: the SVD file writes them once.</summary>
    private static int DerivedRegisters(ChipDescription chip) => chip.Peripherals
        .Where(p => p.Name is "PLL_USB" or "UART1" or "I2C1" or "SPI1" or "PIO1").Sum(p => p.Registers.Count);
}
