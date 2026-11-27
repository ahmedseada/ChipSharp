using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Translates a program's IL into TIR: one TIR function per method of the <see cref="IlProgram"/>, with the same
/// display name, and one external function per runtime function its library calls become
/// (<see cref="KnownExternals"/>). It handles exactly <see cref="SupportedSubset"/>; anything else is a
/// <see cref="ChipSharpException"/> that names the method and, for an instruction, its IL offset.
/// </summary>
/// <remarks>
/// <para>
/// IL is a stack machine and TIR is not, so each method is translated like this. The IL is cut into blocks at
/// every branch and switch target and after every branch, switch and ret; each becomes a TIR block named after
/// its offset, e.g. <c>IL_000c</c>. Locals become slots. Parameters are used as they are, except one the method
/// assigns to (<c>starg</c>): that one is copied into a slot first.
/// </para>
/// <para>
/// Within a block the evaluation stack holds TIR values of IL's stack types: i32 for int32, i64 for int64 and nint
/// for native int. IL has no bool or small integer on the stack, so a bool, int8, uint8, int16 or uint16 is widened
/// to i32 when it is loaded (bool to 0 or 1, the others by sign or zero) and narrowed again when it is stored,
/// passed or returned (bool by comparing with zero, the others by keeping the low bits).
/// </para>
/// <para>
/// A value still on the stack where control goes to another block (C#'s <c>?:</c>, <c>&amp;&amp;</c> and
/// <c>||</c> leave one there) goes through a "stack slot", one per stack depth and type, e.g. <c>$stack0_i32</c>:
/// every way into the block stores it there, and the block loads it back at its start. Every way in must agree on
/// what the stack holds. Like IL itself, the translation reads each method once, front to back: a block that only a
/// later instruction jumps back to starts with an empty stack.
/// </para>
/// <para>
/// Constants, bools widened to int32 and values waiting in a stack slot are written as TIR only where they are used,
/// in the form the use needs (see <see cref="StackValue"/>): <c>const i64 5</c> rather than an i32 constant and a
/// conversion, a branch on the comparison itself rather than on its 0 or 1, no load of a value that is only passed on.
/// </para>
/// <para>
/// A static field is a global named like it, e.g. <c>@Program.counter</c>: <c>ldsfld</c> and <c>stsfld</c> take its
/// address and load or store through it. The type initializers (<see cref="IlProgram.TypeInitializers"/>) run before
/// the entry point: when there are any, the module's entry point is <c>&lt;Start&gt;</c>, which calls them in order and
/// then the entry point. An initializer that only sets its type's fields to constants is not compiled at all: the
/// fields' globals start with those values. An array in a static field is a global array that starts with its
/// elements, constant when nothing writes them (see <see cref="StaticArrays"/>).
/// </para>
/// <para>
/// An interrupt handler (<see cref="IlProgram.InterruptHandlers"/>) is a function like any other. When the target gave
/// its interrupt, an entry function such as <c>&lt;Interrupt timer&gt;</c>, exported under the interrupt's symbol
/// (<c>chipsharp_isr_irq1</c>), calls the runtime function that clears the event, then the handler.
/// </para>
/// </remarks>
public static class IlToTir
{
    /// <summary>
    /// The TIR module of <paramref name="program"/>: named after its assembly, with its entry point set. The entry
    /// point's unused <c>string[] args</c> (see <see cref="IlProgram"/>) is left out.
    /// </summary>
    public static TirModule Lower(IlProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        var module = new TirModule(program.Assembly.Name);
        var functions = new ProgramFunctions(module)
        {
            StaticArrays = program.StaticArrays,
            HeapArrays = program.HeapArrays,
            UnalignedAccess = program.UnalignedAccess,
        };

        // a type initializer that only sets fields to constants becomes their globals' initial values, not code
        var methods = program.Methods.Where(method => !functions.TryFoldInitializer(method)).ToList();

        // every function exists before any body is built, so calls (recursion included) find their callee
        foreach (var method in methods)
        {
            functions.Declare(method, isEntryPoint: ReferenceEquals(method, program.EntryPoint));
        }

        foreach (var method in methods)
        {
            new MethodLowering(functions, functions.Get(method)).Lower();
        }

        functions.AddInterruptEntries(program);
        module.EntryPoint = functions.Start(program);
        return module;
    }
}
