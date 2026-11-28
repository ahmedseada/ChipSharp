namespace ChipSharp.Chips;

/// <summary>What a program may do with a register or a field, in CMSIS-SVD's words.</summary>
public enum RegisterAccess
{
    ReadWrite,
    ReadOnly,
    WriteOnly,
    WriteOnce,
    ReadWriteOnce,
}

/// <summary>
/// Where a peripheral's registers are: in memory, at addresses loads and stores reach (every chip's but the Padauk's), or
/// in an I/O space of their own, which other instructions reach (the Padauk's mov.io, set0, set1, t0sn).
/// </summary>
public enum AddressSpace
{
    Memory,
    Io,
}

/// <summary>A named value of a field, e.g. CLK_CKDIVR's HSIDIV "DIV8" = 3.</summary>
public sealed record ChipFieldValue(string Name, ulong Value, string? Description);

/// <summary>
/// A field of a register: <paramref name="Width"/> bits from bit <paramref name="Offset"/> on, e.g. UART1_SR's TXE, bit 7.
/// </summary>
public sealed record ChipField(
    string Name,
    int Offset,
    int Width,
    RegisterAccess Access,
    string? Description,
    IReadOnlyList<ChipFieldValue> Values)
{
    /// <summary>The field's bits in the register.</summary>
    public ulong Mask => (Width >= 64 ? ulong.MaxValue : (1UL << Width) - 1) << Offset;
}

/// <summary>
/// A register: its name (<paramref name="Name"/>, as its peripheral names it) and the name C code uses
/// (<paramref name="FullName"/>: <c>UART1_SR</c>, <c>TIMER_TIMERAWL</c>, the Padauk's own <c>PA</c>), its address in its
/// peripheral's <see cref="AddressSpace"/>, its size in bits, what a program may do with it, its value after reset and
/// its fields.
/// </summary>
public sealed record ChipRegister(
    string Name,
    string FullName,
    ulong Address,
    int Size,
    RegisterAccess Access,
    ulong? Reset,
    string? Description,
    IReadOnlyList<ChipField> Fields);

/// <summary>A peripheral: its registers, from its base address on, in one address space.</summary>
public sealed record ChipPeripheral(
    string Name,
    ulong Base,
    AddressSpace Space,
    string? Description,
    IReadOnlyList<ChipRegister> Registers);

/// <summary>
/// What asked for an interrupt on a chip whose interrupts share a vector (the Padauk's): the bit of the request register
/// (INTRQ) the source sets, and of the enable register (INTEN) that lets it interrupt.
/// </summary>
public sealed record ChipInterruptRequest(string Register, string Enable, int Bit);

/// <summary>
/// An interrupt: its name and number (the IRQ: which entry of the vector table, after the processor's own; on SDCC's
/// STM8 port, <c>__interrupt(n)</c>'s n), the peripheral that raises it, and on a chip with one vector what asked for it.
/// </summary>
public sealed record ChipInterrupt(
    string Name,
    int Number,
    string? Peripheral,
    string? Description,
    ChipInterruptRequest? Request = null);

/// <summary>A memory region: flash, RAM, EEPROM, program memory.</summary>
public sealed record ChipMemory(string Name, ulong Origin, ulong Size, string? Access, string? Description);

/// <summary>
/// A port of pins: its name (A, B, ...) and its registers by role (<c>out</c>, <c>in</c>, <c>direction</c>, <c>pull</c>, ...,
/// the registers' C names), and the bits that are pins of this package.
/// </summary>
public sealed record ChipPort(string Name, IReadOnlyDictionary<string, string> Registers, IReadOnlyList<int> Bits);

/// <summary>
/// A pin: its name (PA3), its number in ChipSharp's numbering of the chip (port * 8 + bit for the 8-bit chips), its port
/// and bit, and the functions it can take besides a plain input or output (<c>UART1_TX</c>, <c>AIN4</c>, <c>TIM2_CH1</c>).
/// </summary>
public sealed record ChipPin(string Name, int Number, string? Port, int? Bit, IReadOnlyList<string> Functions);

/// <summary>A named value a runtime needs that is not one field's, e.g. the Padauk's CLKMD setting for IHRC / 8.</summary>
public sealed record ChipConstant(string Name, ulong Value, string? Description);

/// <summary>
/// A chip as data: its registers (address, size, fields, access), interrupts, pins and their functions, and memory,
/// from ChipSharp's own JSON description (<see cref="ChipJson"/>, in the repository's chips folder) or a vendor's
/// CMSIS-SVD file (<see cref="Svd"/>). <see cref="ChipHeaders"/> writes the C headers a runtime uses from it.
/// </summary>
public sealed class ChipDescription
{
    /// <summary>The chip's name, e.g. "stm8s103f3", "RP2040": what the headers' names start with.</summary>
    public required string Name { get; init; }

    public string? Vendor { get; init; }

    public string? Description { get; init; }

    /// <summary>The processor, e.g. "stm8", "pdk14", "CM0PLUS".</summary>
    public string? Cpu { get; init; }

    /// <summary>"little" or "big".</summary>
    public string Endian { get; init; } = "little";

    /// <summary>The size of a register when it does not say, in bits.</summary>
    public int Width { get; init; } = 32;

    public IReadOnlyList<ChipMemory> Memory { get; init; } = [];

    public IReadOnlyList<ChipPeripheral> Peripherals { get; init; } = [];

    public IReadOnlyList<ChipInterrupt> Interrupts { get; init; } = [];

    /// <summary>
    /// True when <see cref="Interrupts"/> are sources an interrupt matrix routes to the processor's own interrupts
    /// (Espressif's chips: each source's <c>INTERRUPT_CORE0_&lt;SOURCE&gt;_INTR_MAP</c> register names one), not entries
    /// of the processor's vector table.
    /// </summary>
    public bool InterruptMatrix { get; init; }

    public IReadOnlyList<ChipPort> Ports { get; init; } = [];

    public IReadOnlyList<ChipPin> Pins { get; init; } = [];

    public IReadOnlyList<ChipConstant> Constants { get; init; } = [];

    /// <summary>Every register, peripheral by peripheral.</summary>
    public IEnumerable<ChipRegister> Registers => Peripherals.SelectMany(peripheral => peripheral.Registers);

    /// <summary>The register by its C name (<c>PB_ODR</c>); null when the chip has none.</summary>
    public ChipRegister? Register(string fullName) => Registers.FirstOrDefault(register => register.FullName == fullName);

    /// <summary>The peripheral by name; null when the chip has none.</summary>
    public ChipPeripheral? Peripheral(string name) => Peripherals.FirstOrDefault(peripheral => peripheral.Name == name);

    /// <summary>The peripheral a register is in.</summary>
    public ChipPeripheral PeripheralOf(ChipRegister register) =>
        Peripherals.First(peripheral => peripheral.Registers.Contains(register));
}
