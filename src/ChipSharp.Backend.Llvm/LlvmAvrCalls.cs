using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// Calls on an AVR that pass arguments on the stack, which LLVM 22 (the one in Zig 0.17) can get wrong: the AVR passes
/// a call's first 18 bytes of arguments in r25 down to r8, each argument rounded up to an even number of bytes, and the
/// rest on the stack, from the first argument that does not fit on. In a function without a frame pointer, LLVM makes
/// room for those on the stack right before the call by reading the stack pointer into r31:r30 (Z) and writing it back
/// lowered, but its register allocator does not take Z as changed there (the pseudo-instruction that becomes this,
/// ADJCALLSTACKDOWN, says only SP and SREG): a value it keeps in Z across it, and uses to work an argument out, is the
/// stack pointer then (seen: the high half of a double passed on, which printed every double as 2.6E-266). A function
/// with a frame pointer has its calls' stack room set up once, in its prologue, and stores the arguments through the
/// frame pointer, Y; LLVM gives an AVR function one when it has a variable on the stack. So a function that makes such a
/// call gets one of a byte (<see cref="FrameLines"/>), which LLVM keeps for its volatile store; a function it is inlined
/// into gets it too, with the call.
/// </summary>
internal static class LlvmAvrCalls
{
    /// <summary>The bytes of arguments an AVR call passes in registers, r25 down to r8.</summary>
    public const int RegisterBytes = 18;

    /// <summary>A result of more than 8 bytes is written where a hidden first argument, its address, says.</summary>
    private const int ReturnRegisterBytes = 8;

    /// <summary>True when <paramref name="function"/> makes a call that passes arguments on the stack on an AVR.</summary>
    public static bool NeedsFrame(TirFunction function, LlvmTypes types) =>
        function.Blocks.SelectMany(block => block.Instructions).Any(instruction => instruction switch
        {
            TirCall call => PassesOnStack(call.Type, call.Arguments, types),
            TirCallIndirect call => PassesOnStack(call.Type, call.Arguments, types),
            _ => false,
        });

    /// <summary>
    /// True when a call that returns <paramref name="result"/> and takes <paramref name="arguments"/> passes some of them
    /// on the stack: each takes its size rounded up to even, in order, and those past the first 18 bytes go there.
    /// </summary>
    public static bool PassesOnStack(TirType result, IReadOnlyList<TirValue> arguments, LlvmTypes types)
    {
        var bytes = result.Kind != TirTypeKind.Void && types.Layout.SizeOf(result) > ReturnRegisterBytes ? 2L : 0L;
        foreach (var argument in arguments)
        {
            bytes += (types.Layout.SizeOf(argument.Type) + 1) / 2 * 2;
        }

        return bytes > RegisterBytes;
    }

    /// <summary>
    /// The size, in TIR instructions, above which a function with a loop stays a function of its own on an AVR
    /// (<see cref="StaysOutOfLine"/>).
    /// </summary>
    public const int OutOfLineSize = 100;

    /// <summary>
    /// True when <paramref name="function"/>, on an AVR, is never inlined (LLVM's <c>noinline</c>): it has a loop and more
    /// than <see cref="OutOfLineSize"/> instructions. LLVM inlines a function called once wherever it is, so a program of
    /// many such functions called from one loop became one function of thousands of instructions with hundreds of
    /// bytes of spilled values, and LLVM 22's AVR register allocation gets such a function wrong: it copies one byte of
    /// a register pair and spills the other's stale value as if it held nothing (the machine verifier says "Using an
    /// undefined physical register" of the spill), and which passes then show it moves with the code
    /// (stack-slot-coloring, early-machinelicm, machine-sink: NarrowingPrograms.Random(108, 12, ...) and 30 more of
    /// 56 printed wrong numbers). Each of those functions on its own is right, and the program is mostly smaller so.
    /// </summary>
    public static bool StaysOutOfLine(TirFunction function)
    {
        var count = 0;
        var loops = false;
        var index = function.Blocks.Select((block, i) => (block, i)).ToDictionary(pair => pair.block, pair => pair.i);
        foreach (var (block, i) in function.Blocks.Select((block, i) => (block, i)))
        {
            count += block.Instructions.Count;
            loops |= block.Instructions.Any(instruction => instruction switch
            {
                TirJump jump => index[jump.Target] <= i,
                TirBranch branch => index[branch.WhenTrue] <= i || index[branch.WhenFalse] <= i,
                _ => false,
            });
        }

        return loops && count > OutOfLineSize;
    }

    /// <summary>The lines that give a function a variable of one byte on the stack, which LLVM keeps: <paramref name="name"/>.</summary>
    public static IEnumerable<string> FrameLines(string name) =>
        [$"{name} = alloca i8, align 1", $"store volatile i8 0, ptr {name}, align 1"];
}
