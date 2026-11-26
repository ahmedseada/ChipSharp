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
/// The script defines these symbols for the runtime's start-up code, all word-aligned:
/// <list type="table">
/// <item><term>__chipsharp_data_start, __chipsharp_data_end</term><description>.data in RAM</description></item>
/// <item><term>__chipsharp_data_load</term><description>where .data's initial values are in flash</description></item>
/// <item><term>__chipsharp_bss_start, __chipsharp_bss_end</term><description>.bss, which starts at zero</description></item>
/// <item><term>__chipsharp_stack_top</term><description>the end of RAM, where the stack starts</description></item>
/// <item><term>__chipsharp_heap_start, __chipsharp_heap_end</term><description>the heap, for a target with
/// one</description></item>
/// </list>
/// The link fails, saying why, when a head section is not exactly its size; when the program does not fit, ld.lld says
/// by how much each region overflowed (see <see cref="MemoryOverflow"/>).
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

    /// <summary>The memory region of the stack: the last "link.stack" bytes of the data region.</summary>
    public const string StackRegion = "stack";

    /// <summary>The memory region of the heap: the "heap" capability's bytes of the data region, right before the stack.</summary>
    public const string HeapRegion = "heap";

    public static string Write(TargetDescription target, ChipSettings settings)
    {
        var (code, data) = (settings.Code.Name, settings.Data.Name);
        var (stack, heap) = (settings.StackSize, settings.HeapSize);
        var variables = settings.Data.Region.Size - stack - heap; // ChipSettings checks that the stack and the heap fit
        var text = new StringBuilder();
        void Line(string line = "") => text.Append(line).Append('\n');

        Line($"/* Linker script for target '{target.Name}' ({string.Join(" -> ", target.InheritanceChain)}), written by");
        Line("   ChipSharp's clang toolchain from the target's memory regions and link settings. */");
        Line();
        Line($"ENTRY({settings.Entry})");
        Line();
        Line("MEMORY");
        Line("{");
        var codeLength = data == code ? variables : settings.Code.Region.Size;
        Line($"  {code} (rx) : ORIGIN = {Hex(settings.Code.Region.Origin)}, LENGTH = {Hex(codeLength)}");
        if (data != code)
        {
            Line($"  {data} (rwx) : ORIGIN = {Hex(settings.Data.Region.Origin)}, LENGTH = {Hex(variables)}");
        }

        if (heap > 0)
        {
            Line($"  /* the heap (capabilities.heap), between the variables and the stack */");
            Line($"  {HeapRegion} (rw) : ORIGIN = {Hex(settings.Data.Region.Origin + variables)}, LENGTH = {Hex(heap)}");
        }

        if (stack > 0)
        {
            Line($"  /* the end of {data}, kept for the stack (link.stack) */");
            Line($"  {StackRegion} (rw) : ORIGIN = {Hex(settings.Data.Region.Origin + variables + heap)}, " +
                $"LENGTH = {Hex(stack)}");
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

        Line("  /* the vector table: the initial stack pointer, the reset handler, then the other exception handlers */");
        Line($"  .vectors : ALIGN({Hex(settings.VectorAlign)})");
        Line("  {");
        Line("    KEEP(*(.vectors))");
        Line($"  }} > {code}");
        Line();
        Line("  .text :");
        Line("  {");
        Line("    *(.text .text.*)");
        Line("    *(.rodata .rodata.*)");
        Line("    . = ALIGN(4);");
        Line($"  }} > {code}");
        Line();
        Line("  .ARM.exidx :");
        Line("  {");
        Line("    *(.ARM.exidx .ARM.exidx.*)");
        Line($"  }} > {code}");
        Line();
        Line($"  /* variables with a starting value: in {data}, their starting values in {code}, copied at start-up */");
        Line("  .data : ALIGN(4)");
        Line("  {");
        Line($"    {DataStart} = .;");
        Line("    *(.data .data.* .sdata .sdata.*)");
        Line("    . = ALIGN(4);");
        Line($"    {DataEnd} = .;");
        Line($"  }} > {data} AT > {code}");
        Line($"  {DataLoad} = LOADADDR(.data);");
        Line();
        Line("  /* variables that start at zero, zeroed at start-up */");
        Line("  .bss (NOLOAD) : ALIGN(4)");
        Line("  {");
        Line($"    {BssStart} = .;");
        Line("    *(.bss .bss.* .sbss .sbss.* COMMON)");
        Line("    . = ALIGN(4);");
        Line($"    {BssEnd} = .;");
        Line($"  }} > {data}");
        Line();
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
        Line("}");
        return text.ToString();
    }

    private static string Hex(ulong value) => "0x" + value.ToString("x", CultureInfo.InvariantCulture);
}
