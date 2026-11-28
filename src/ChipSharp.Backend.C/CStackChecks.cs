using System.Globalization;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// The stack checks of a target whose capability "stackCheck" is true (the STM8's and the PDK14's): a chip has no
/// operating system to stop a program whose stack runs into its variables, so the functions through which the stack
/// can grow without end check it themselves, as the llvm code generator's do (its LlvmStackChecks): every function that
/// can call itself again (<see cref="CCallGraph.Recursive"/>), and on a compiler that keeps every frame on the stack every
/// function whose slots and parameters take <see cref="LargeFrame"/> bytes or more, which could pass the margin at once;
/// the others go as deep as the program's code, which the margin is for. When one starts, it compares where its frame
/// is, the address of a byte of it (portable C, which SDCC makes 4 instructions of on the STM8, 5 on the PDK14), with
/// <see cref="LimitMacro"/>, the end of the stack's room less a margin, less what one more call of it takes
/// (<see cref="FrameBytes"/>), and past it calls <see cref="OverflowSymbol"/> (the runtime's: "Stack overflow.", as .NET
/// says it, then the failure stop).
/// </summary>
/// <remarks>
/// The limit comes from the target file: the region of the variables and the stack ("link.data", RAM by default), the
/// room the variables must leave for the stack ("link.stack"), which the toolchain sdcc checks at link time, and which
/// way it grows ("c.stack"). Growing up (the PDK14: from the variables to the end of RAM), the stack's room ends at the
/// end of RAM, less the margin. Growing down (the STM8: from the end of RAM toward the variables at its start), it ends
/// where the variables do, which only the link knows: the program's own variables, which the file has, and as much again
/// as the margin for the runtime's, and the margin. The margin is a quarter of "link.stack" (at least 4 bytes, at most
/// 1 KB): what the stack may still need after a check passes, for the calls of functions that do not check, an
/// interrupt, the call that stops the program.
/// </remarks>
internal static class CStackChecks
{
    /// <summary>The target capability that asks for the checks.</summary>
    public const string Capability = "stackCheck";

    /// <summary><c>void chipsharp_stack_overflow(void)</c>, the runtime's: says so and stops; it does not return.</summary>
    public const string OverflowSymbol = "chipsharp_stack_overflow";

    /// <summary>The macro of the limit, an address: the file defines it from the target.</summary>
    public const string LimitMacro = "CS_STACK_LIMIT";

    /// <summary>The bytes of slots and parameters from which a function checks the stack, recursive or not.</summary>
    public const long LargeFrame = 256;

    /// <summary>The name of the byte whose address a checking function compares.</summary>
    public const string FrameName = "cs_frame";

    /// <summary>
    /// The functions of <paramref name="module"/> that check the stack and the limit they check against, or none when
    /// <paramref name="target"/> does not ask for checks.
    /// </summary>
    public static (HashSet<TirFunction> Functions, Limit? Limit) Find(TirModule module, TargetDescription target,
        CReachable reachable, CTypes types, CSettings settings)
    {
        if (!target.HasCapability(Capability))
        {
            return ([], null);
        }

        var variables = module.Globals.Where(global => reachable.Globals.Contains(global) && !global.IsConstant)
            .Sum(global => global.ObjectType is null ? types.Layout.SizeOf(global.Type) : 0);
        var limit = LimitOf(target, settings, (ulong)variables);
        var checks = CCallGraph.Recursive(module);
        if (settings.Reentrant is null)
        {
            checks.UnionWith(module.Functions.Where(function => function.Kind == TirFunctionKind.Defined
                && function.Slots.Sum(slot => types.Layout.SizeOf(slot.Type))
                    + function.Parameters.Sum(parameter => types.Layout.SizeOf(parameter.Type)) >= LargeFrame));
        }

        checks.IntersectWith(reachable.Functions);
        return (checks, limit);
    }

    /// <summary>
    /// The lines a checking function starts with, after its slots: the byte, and the call that stops the program when
    /// the frame is past the limit, less what one more call of the function takes.
    /// </summary>
    public static IEnumerable<string> Check(TirFunction function, CTypes types, Limit limit, string frame)
    {
        var bytes = FrameBytes(function, types, limit.Up).ToString(CultureInfo.InvariantCulture);
        var bound = limit.Up ? $"{LimitMacro} - {bytes}u" : $"{LimitMacro} + {bytes}u";
        yield return $"uint8_t {frame};";
        yield return $"if ((uintptr_t)&{frame} {(limit.Up ? ">" : "<")} {bound})";
        yield return $"    {OverflowSymbol}();";
    }

    /// <summary>
    /// What one more call of <paramref name="function"/> takes of the stack: its arguments (two bytes each byte where the
    /// stack grows up: SDCC's PDK14 pushes A and the flags), the return address, its slots, the byte the check reads, and
    /// what the compiler keeps of the values it computes there, a byte for each byte of them up to
    /// <see cref="Temporaries"/> (most stay in registers; the margin takes the rest).
    /// </summary>
    public static long FrameBytes(TirFunction function, CTypes types, bool up)
    {
        var arguments = function.Parameters.Sum(parameter => types.Layout.SizeOf(parameter.Type)) * (up ? 2 : 1);
        var values = CLiveness.Find(function).Live.Where(instruction => instruction.HasResult && instruction is not TirConst)
            .Sum(instruction => types.Layout.SizeOf(instruction.Type));
        return arguments + 2 + function.Slots.Sum(slot => types.Layout.SizeOf(slot.Type)) + 1 + Math.Min(values, Temporaries);
    }

    /// <summary>The most bytes of a frame's values <see cref="FrameBytes"/> counts.</summary>
    private const long Temporaries = 8;

    /// <summary>Where the stack's room ends, and which way it grows.</summary>
    internal sealed record Limit(ulong Address, bool Up)
    {
        /// <summary>The definition of <see cref="LimitMacro"/>: <c>#define CS_STACK_LIMIT 0x0340u</c>.</summary>
        public string Definition =>
            $"#define {LimitMacro} 0x{Address.ToString("X4", CultureInfo.InvariantCulture)}u";
    }

    /// <summary>
    /// The limit: growing down, the start of the region, the program's <paramref name="variables"/> bytes, the runtime's
    /// (a margin's worth) and the margin; growing up, the end of the region less the margin (see the remarks).
    /// </summary>
    private static Limit LimitOf(TargetDescription target, CSettings settings, ulong variables)
    {
        ChipSharpException Missing(string what) => new($"Target '{target.Name}' asks for stack checks (\"stackCheck\": " +
            $"true), which the c code generator writes from the region of the stack and its size: {what}.");

        var link = target.Settings["link"];
        var regionName = link?["data"]?.GetValue<string>() ?? "ram";
        if (!target.Memory.TryGetValue(regionName, out var region))
        {
            throw Missing($"the target has no memory region '{regionName}' (\"link.data\")");
        }

        var stackText = link?["stack"]?.GetValue<string>()
            ?? throw Missing("say how much of it the stack has, in \"link.stack\" (e.g. \"256\")");
        if (!ulong.TryParse(stackText, NumberStyles.None, CultureInfo.InvariantCulture, out var stack) || stack == 0
            || stack >= region.Size)
        {
            throw Missing($"\"link.stack\" must be a number of bytes below the region's {region.Size}, not '{stackText}'");
        }

        var margin = Math.Max(4UL, Math.Min(1024UL, stack / 4));
        var end = region.Origin + region.Size;
        return settings.StackGrowsUp
            ? new Limit(end - margin, Up: true)
            : new Limit(Math.Min(region.Origin + variables + 2 * margin, end - margin), Up: false);
    }
}
