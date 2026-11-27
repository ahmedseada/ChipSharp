using ChipSharp.Abstractions;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// The calls that make the garbage collector run now, System.GC's (<c>GC.Collect()</c>, <c>GC.GetTotalMemory(true)</c>):
/// right before one, the callee-saved registers are cleared (an inline asm that zeroes them and says it does, so that
/// LLVM first moves what it still needs from them to the stack, where the collector reads it). The collector reads the
/// registers conservatively, and a register keeps a reference the program no longer uses until something else is put
/// there: a list a method made and returned from, still in a register of its inlined body, would live on through the
/// collection the program asked for to free it. The registers a frame pointer could be in (rbp on a PC, r7 on Thumb) are
/// among them: a function of ChipSharp's never needs one, as its stack never grows by an amount known only when it runs.
/// </summary>
internal static class LlvmCollectCalls
{
    /// <summary>The runtime functions that collect (chipsharp_heap.c).</summary>
    private static readonly HashSet<string> Symbols = new(StringComparer.Ordinal)
    {
        "ChipSharp_Runtime_GcCollect", "ChipSharp_Runtime_GcGetTotalMemory",
    };

    /// <summary>True for a call of a function with <paramref name="symbol"/> that may collect now.</summary>
    public static bool Collects(string symbol) => Symbols.Contains(symbol);

    /// <summary>The line that clears the callee-saved registers on <paramref name="target"/>; null for one it does not know.</summary>
    public static string? ForgetRegisters(TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var triple = target.Triple ?? "";
        if (triple.StartsWith("x86_64", StringComparison.Ordinal))
        {
            return "call void asm sideeffect \"xorl %ebx, %ebx\\0Axorl %ebp, %ebp\\0Axorl %esi, %esi\\0Axorl %edi, %edi\\0A" +
                "xorl %r12d, %r12d\\0Axorl %r13d, %r13d\\0Axorl %r14d, %r14d\\0Axorl %r15d, %r15d\", " +
                "\"~{rbx},~{rbp},~{rsi},~{rdi},~{r12},~{r13},~{r14},~{r15},~{eflags}\"()";
        }

        if (triple.StartsWith("thumbv6m", StringComparison.Ordinal))
        {
            return "call void asm sideeffect \"movs r4, #0\\0Amovs r5, #0\\0Amovs r6, #0\\0Amovs r7, #0\\0Amov r8, r4\\0A" +
                "mov r9, r4\\0Amov r10, r4\\0Amov r11, r4\",\"~{r4},~{r5},~{r6},~{r7},~{r8},~{r9},~{r10},~{r11},~{cpsr}\"()";
        }

        return null;
    }
}
