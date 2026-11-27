using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;

namespace ChipSharp.Toolchain.Clang;

/// <summary>
/// Writes the ld.lld linker script for a chip program from the target's memory regions and "link" settings.
/// In the code region (flash): the "link.head" sections, the vector table (section .vectors), code and read-only
/// data, and the initial values of .data. In the data region (RAM): .data, .bss, and the stack at the end, which the
/// script makes a memory region of its own (<see cref="StackRegion"/>, "link.stack" bytes long), so that the variables
/// cannot take its room. A target with a heap has the heap (<see cref="HeapRegion"/>, its "heap" capability's bytes)
/// between the variables and the stack, a region of its own too.
/// </summary>
/// <remarks>
/// The script defines these symbols for the runtime's start-up code, all word-aligned (on an AVR, which has no
/// alignment, wherever they fall):
/// <list type="table">
/// <item><term>__chipsharp_data_start, __chipsharp_data_end</term><description>.data in RAM</description></item>
/// <item><term>__chipsharp_data_load</term><description>where .data's initial values are in flash</description></item>
/// <item><term>__chipsharp_bss_start, __chipsharp_bss_end</term><description>.bss, which starts at zero</description></item>
/// <item><term>__chipsharp_stack_top</term><description>the end of RAM, where the stack starts</description></item>
/// <item><term>__chipsharp_heap_start, __chipsharp_heap_end</term><description>the heap, for a target with
/// one</description></item>
/// <item><term>__chipsharp_stack_bottom</term><description>the lowest the stack may go: the start of its region
/// above the heap, or, without a heap, the end of the variables (so the stack has all the RAM they leave)</description></item>
/// <item><term>__chipsharp_stack_limit</term><description>the bottom and a margin (<see cref="StackMargin"/>): the
/// functions that check the stack stop the program with "Stack overflow." below it</description></item>
/// </list>
/// The link fails, saying why, when a head section is not exactly its size; when the program does not fit, ld.lld says
/// by how much each region overflowed (see <see cref="MemoryOverflow"/>).
/// <para>
/// An AVR (<see cref="ChipSettings.IsAvr"/>) is laid out as avr-gcc lays it out. Its flash and its RAM are two
/// address spaces that both start at 0: the .elf puts RAM's data addresses at 0x800000 and up ("link.dataOffset").
/// An ordinary pointer reads RAM, so the ordinary constants (.rodata) are in RAM like .data, their starting values in
/// flash; the constants read from flash with LPM (LLVM's address space 1, which puts them in .progmem.data) stay in
/// flash, after the vectors and before the code (section .progmem). For the runtime's start-up code:
/// <list type="bullet">
/// <item>__chipsharp_data_start, _data_end, _bss_start, _bss_end and _stack_top are .elf addresses in RAM, 0x800000
/// and more: lo8() and hi8() in assembly, or a pointer in C (16 bits), drop the 0x800000 and give the data address,
/// e.g. 0x0100.</item>
/// <item>__chipsharp_data_load is a byte address in flash, for LPM's Z.</item>
/// <item>__chipsharp_stack_top is one past the last byte of RAM (0x0900 on the ATmega328P): an AVR's push stores at
/// SP, then decrements it, so the start-up code sets SP to __chipsharp_stack_top - 1 (RAMEND).</item>
/// <item>Clang's AVR code names avr-gcc's start-up routines __do_copy_data and __do_clear_bss in every file with .data
/// or .bss, without calling them: they need not exist, as the runtime copies .data and clears .bss itself.</item>
/// </list>
/// </para>
/// </remarks>
internal static class LinkerScript
{
    public const string DataStart = "__chipsharp_data_start";
    public const string DataEnd = "__chipsharp_data_end";
    public const string DataLoad = "__chipsharp_data_load";
    public const string BssStart = "__chipsharp_bss_start";
    public const string BssEnd = "__chipsharp_bss_end";
    public const string StackTop = "__chipsharp_stack_top";
    public const string HeapStart = "__chipsharp_heap_start";
    public const string HeapEnd = "__chipsharp_heap_end";
    public const string StackBottom = "__chipsharp_stack_bottom";
    public const string StackLimit = "__chipsharp_stack_limit";

    /// <summary>The memory region of the stack: the last "link.stack" bytes of the data region.</summary>
    public const string StackRegion = "stack";

    /// <summary>The memory region of the heap: the "heap" capability's bytes of the data region, right before the stack.</summary>
    public const string HeapRegion = "heap";

    /// <summary>The section an AVR's constants in flash go in (address space 1: .progmem.data and the like).</summary>
    public const string AvrFlashConstants = ".progmem";

    public static string Write(TargetDescription target, ChipSettings settings)
    {
        var (code, data) = (settings.Code.Name, settings.Data.Name);
        var (stack, heap) = (settings.StackSize, settings.HeapSize);
        var variables = settings.Data.Region.Size - stack - heap; // ChipSettings checks that the stack and the heap fit
        var dataOrigin = settings.Data.Region.Origin + settings.DataOffset;
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');

        Line($"/* Linker script for target '{target.Name}' ({string.Join(" -> ", target.InheritanceChain)}), written by");
        Line("   ChipSharp's clang toolchain from the target's memory regions and link settings. */");
        Line();
        Line($"ENTRY({settings.Entry})");
        Line();
        Line("MEMORY");
        Line("{");
        if (settings.DataOffset > 0)
        {
            Line($"  /* {data} is in an address space of its own: the .elf has its data addresses + " +
                $"{Hex(settings.DataOffset)} (link.dataOffset) */");
        }

        var codeLength = data == code ? variables : settings.Code.Region.Size;
        Line($"  {code} (rx) : ORIGIN = {Hex(settings.Code.Region.Origin)}, LENGTH = {Hex(codeLength)}");
        if (data != code)
        {
            Line($"  {data} (rwx) : ORIGIN = {Hex(dataOrigin)}, LENGTH = {Hex(variables)}");
        }

        if (heap > 0)
        {
            Line($"  /* the heap (capabilities.heap), between the variables and the stack */");
            Line($"  {HeapRegion} (rw) : ORIGIN = {Hex(dataOrigin + variables)}, LENGTH = {Hex(heap)}");
        }

        if (stack > 0)
        {
            Line($"  /* the end of {data}, kept for the stack (link.stack) */");
            Line($"  {StackRegion} (rw) : ORIGIN = {Hex(dataOrigin + variables + heap)}, LENGTH = {Hex(stack)}");
        }

        Line("}");
        Line();
        Line("SECTIONS");
        Line("{");
        foreach (var head in settings.Head)
        {
            Line($"  /* first in {code}, exactly {head.Size} bytes (link.head) */");
            Line($"  {head.Name} :");
            Line("  {");
            Line($"    KEEP(*({head.Name}))");
            Line($"  }} > {code}");
            Line($"  ASSERT(SIZEOF({head.Name}) == {Hex(head.Size)}, \"Target {target.Name}: section {head.Name} must " +
                $"be exactly {head.Size} bytes (link.head), and it is not.\")");
            Line();
        }

        if (settings.IsAvr)
        {
            AvrSections(Line, code, data, settings);
        }
        else
        {
            Sections(Line, code, data, settings);
        }

        if (heap > 0)
        {
            Line($"  /* the heap: the objects the program makes, and the garbage collector's marks */");
            Line($"  {HeapStart} = ORIGIN({HeapRegion});");
            Line($"  {HeapEnd} = ORIGIN({HeapRegion}) + LENGTH({HeapRegion});");
            Line();
        }

        Line($"  /* the stack grows down from the end of {data} */");
        var top = stack > 0 ? StackRegion : data;
        Line($"  {StackTop} = ORIGIN({top}) + LENGTH({top});");
        Line(heap > 0 && stack > 0
            ? $"  /* down to its own region at most: the heap is below it */\n  {StackBottom} = ORIGIN({StackRegion});"
            : $"  /* down to the variables at most: the stack has all the RAM they leave */\n  {StackBottom} = {BssEnd};");
        Line($"  /* a function that checks the stack stops the program below this (see LlvmStackChecks) */");
        Line($"  {StackLimit} = {StackBottom} + {Hex(StackMargin(stack))};");
        Line("}");
        return text.ToString();
    }

    /// <summary>The sections of a chip with one address space (ARM, RISC-V): the constants in flash with the code.</summary>
    private static void Sections(Action<string> line, string code, string data, ChipSettings settings)
    {
        line("  /* the vector table: the initial stack pointer, the reset handler, then the other exception handlers */");
        line($"  .vectors : ALIGN({Hex(settings.VectorAlign)})");
        line("  {");
        line("    KEEP(*(.vectors))");
        line($"  }} > {code}");
        line("");
        line("  .text :");
        line("  {");
        line("    *(.text .text.*)");
        line("    *(.rodata .rodata.*)");
        line("    . = ALIGN(4);");
        line($"  }} > {code}");
        line("");
        line("  .ARM.exidx :");
        line("  {");
        line("    *(.ARM.exidx .ARM.exidx.*)");
        line($"  }} > {code}");
        line("");
        line($"  /* variables with a starting value: in {data}, their starting values in {code}, copied at start-up */");
        line("  .data : ALIGN(4)");
        line("  {");
        line($"    {DataStart} = .;");
        line("    *(.data .data.* .sdata .sdata.*)");
        line("    . = ALIGN(4);");
        line($"    {DataEnd} = .;");
        line($"  }} > {data} AT > {code}");
        line($"  {DataLoad} = LOADADDR(.data);");
        line("");
        line("  /* variables that start at zero, zeroed at start-up */");
        line("  .bss (NOLOAD) : ALIGN(4)");
        line("  {");
        line($"    {BssStart} = .;");
        line("    *(.bss .bss.* .sbss .sbss.* COMMON)");
        line("    . = ALIGN(4);");
        line($"    {BssEnd} = .;");
        line($"  }} > {data}");
        line("");
    }

    /// <summary>
    /// The sections of an AVR, as avr-gcc's linker script has them: the vectors at the start of flash, the constants read
    /// from flash, the code, then the starting values of .data, with the ordinary constants (.rodata) in RAM.
    /// </summary>
    private static void AvrSections(Action<string> line, string code, string data, ChipSettings settings)
    {
        line($"  /* the interrupt vectors, at the start of {code}: a jump to the reset handler, then one for each interrupt */");
        line($"  .vectors : ALIGN({Hex(settings.VectorAlign)})");
        line("  {");
        line("    KEEP(*(.vectors))");
        line($"  }} > {code}");
        line("");
        line($"  /* the constants read from {code} with LPM (address space 1: .progmem.data), low in {code} as avr-gcc has them */");
        line($"  {AvrFlashConstants} :");
        line("  {");
        line($"    *({AvrFlashConstants} {AvrFlashConstants}.* {AvrFlashConstants}?.*)");
        line($"  }} > {code}");
        line("");
        line("  .text : ALIGN(2)");
        line("  {");
        line("    *(.text .text.*)");
        line("    . = ALIGN(2);");
        line($"  }} > {code}");
        line("");
        line($"  /* variables with a starting value, and the constants an ordinary pointer reads (.rodata): in {data}, " +
            $"their starting values in {code}, copied at start-up */");
        line("  .data :");
        line("  {");
        line($"    {DataStart} = .;");
        line("    *(.data .data.*)");
        line("    *(.rodata .rodata.*)");
        line($"    {DataEnd} = .;");
        line($"  }} > {data} AT > {code}");
        line($"  {DataLoad} = LOADADDR(.data);");
        line("");
        line("  /* variables that start at zero, zeroed at start-up */");
        line("  .bss (NOLOAD) :");
        line("  {");
        line($"    {BssStart} = .;");
        line("    *(.bss .bss.* COMMON)");
        line($"    {BssEnd} = .;");
        line($"  }} > {data}");
        line("");
    }

    /// <summary>
    /// How far above the stack's bottom the program stops with "Stack overflow.": room for what may run below the
    /// last function that checks the stack (the functions it calls that check nothing, the runtime's, an interrupt's
    /// handler): a quarter of "link.stack", at most 1 KB.
    /// </summary>
    public static ulong StackMargin(ulong stack) => Math.Min(1024UL, stack / 4) & ~7UL;

    private static string Hex(ulong value) =>"0x" + value.ToString("x", CultureInfo.InvariantCulture);
}
