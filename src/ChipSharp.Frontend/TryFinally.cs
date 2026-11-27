using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>
/// try/finally, C#'s <c>using</c> (statement and declaration) and the <c>foreach</c> that disposes its enumerator, without
/// exceptions. ChipSharp has none: where .NET throws, the program stops, as it does in .NET when nothing catches the
/// exception, and .NET then runs no finally block either. So a finally runs only when its try block is left normally,
/// by a <c>leave</c> (C#'s end of the block, <c>return</c>, <c>break</c>, <c>continue</c> and <c>goto</c> out of it), and
/// IL's try/finally is plain control flow, which <see cref="Fold"/> makes of a method's IL before anything else reads it
/// (<see cref="IlMethod.Body"/>), so that the whole-program pass and the translation see branches only:
/// <list type="bullet">
/// <item>A <c>leave</c> becomes a <c>br</c> to the first finally it leaves (the innermost), whose <c>endfinally</c> goes on
/// to the next finally the leave leaves, and after the last one to the leave's target: nested regions, and a
/// <c>return</c> out of two <c>using</c> blocks, run each finally once, innermost first.</item>
/// <item>A finally body is kept once, where it is. When control can go on to one place only after it (a try block left one
/// way: the end of the block, or one <c>return</c>), its <c>endfinally</c> is a <c>br</c> there. When it can go on to
/// several (a <c>return</c> or <c>break</c> out of a <c>using</c> block, besides its end), each way in stores its number
/// in an int local of its own (its continuation) before it goes to the finally, and the <c>endfinally</c> goes to a
/// <c>switch</c> on that number. These stores and switches are added after the method's IL, so every instruction of the
/// IL keeps its offset; the finally is not copied, so a big one (a <c>using</c> around much code) costs its size once.
/// LLVM turns the local into a value in each way and folds the switch where it is known.</item>
/// <item><c>constrained. S callvirt IDisposable::Dispose()</c> on a struct S (C#'s <c>using</c> of a struct and its
/// <c>foreach</c> over a struct enumerator, e.g. <c>List&lt;T&gt;.Enumerator</c>) is a call of S's own Dispose, as in
/// generic code (see <see cref="Generics.ConstrainedCalls"/>).</item>
/// </list>
/// A fault handler (C#'s iterators have one around their body, which disposes the iterator when an exception leaves it)
/// runs only when an exception leaves its try block, which stops the program in ChipSharp: no leave goes through it, and
/// its end goes back to its start, where nothing leads. A method with a catch or a filter keeps its IL as it is: catching
/// exceptions arrives later, and the whole-program pass says so.
/// </summary>
internal static class TryFinally
{
    /// <summary>
    /// The IL of <paramref name="body"/> with its finally regions made plain control flow (see the summary), or
    /// <paramref name="body"/> itself when it has none, or has a handler other than a finally.
    /// </summary>
    public static IlMethodBody Fold(IlMethodBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var regions = body.ExceptionRegions;
        if (regions.Count == 0
            || regions.Any(region => region.Kind is not (IlExceptionRegionKind.Finally or IlExceptionRegionKind.Fault)))
        {
            return body;
        }

        return new Folding(body).Run();
    }

    /// <summary>Where control goes after a leave or a finally: an offset, or through a finally first, then on.</summary>
    private abstract record Continuation;

    /// <summary>The offset the leave goes to, once every finally it leaves has run.</summary>
    private sealed record ToOffset(int Offset) : Continuation;

    /// <summary>The finally of region <paramref name="Region"/>, then <paramref name="Then"/>.</summary>
    private sealed record Through(int Region, Continuation Then) : Continuation;

    private sealed class Folding(IlMethodBody body)
    {
        private const int LdcI4Size = 5;
        private const int StlocSize = 4;
        private const int LdlocSize = 4;
        private const int BrSize = 5;

        private readonly IReadOnlyList<IlExceptionRegion> _regions = body.ExceptionRegions;

        /// <summary>For each region, where control can go after its finally, in the order the IL first leaves that way.</summary>
        private readonly List<Continuation>[] _exits =
            [.. body.ExceptionRegions.Select(_ => new List<Continuation>())];

        /// <summary>The continuation of each leave, by instruction index.</summary>
        private readonly Dictionary<int, Continuation> _leaves = [];

        /// <summary>The local of each region whose finally can go on to several places.</summary>
        private readonly Dictionary<int, int> _continuationLocals = [];

        /// <summary>Where the stores of each way into a finally (region, exit) start, after the method's IL.</summary>
        private readonly Dictionary<(int Region, int Exit), int> _entries = [];

        /// <summary>Where the switch after each finally with several exits starts.</summary>
        private readonly Dictionary<int, int> _dispatches = [];

        public IlMethodBody Run()
        {
            var instructions = body.Instructions;
            for (var i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is { OpCode: IlOpCode.Leave or IlOpCode.Leave_S, Operand: IlBranchOperand { Target: var target } } leave)
                {
                    var continuation = ContinuationOf(leave.Offset, target);
                    _leaves.Add(i, continuation);
                    for (var through = continuation as Through; through is not null; through = through.Then as Through)
                    {
                        if (!_exits[through.Region].Contains(through.Then))
                        {
                            _exits[through.Region].Add(through.Then);
                        }
                    }
                }
            }

            // the stores and switches come after the method's IL, in order of region
            var locals = body.Locals.ToList();
            var end = body.CodeSize;
            for (var r = 0; r < _regions.Count; r++)
            {
                if (_exits[r].Count < 2)
                {
                    continue;
                }

                _continuationLocals.Add(r, locals.Count);
                locals.Add(new IlLocal(locals.Count, IlType.Int32));
                for (var e = 0; e < _exits[r].Count; e++)
                {
                    _entries.Add((r, e), end);
                    end += LdcI4Size + StlocSize + BrSize;
                }

                _dispatches.Add(r, end);
                end += LdlocSize + SwitchSize(_exits[r].Count - 1) + BrSize;
            }

            var folded = new List<IlInstruction>(instructions.Count);
            for (var i = 0; i < instructions.Count; i++)
            {
                folded.Add(Folded(instructions, i));
            }

            for (var r = 0; r < _regions.Count; r++)
            {
                if (_continuationLocals.TryGetValue(r, out var local))
                {
                    AddDispatch(folded, r, local);
                }
            }

            return new IlMethodBody(body.Method, Math.Max(body.MaxStack, 1), body.InitLocals, end, locals, folded, []);
        }

        /// <summary>
        /// Where a leave at <paramref name="offset"/> to <paramref name="target"/> goes: through the finally of each try
        /// region it is in and its target is not, the innermost first, then to the target (never through a fault).
        /// </summary>
        private Continuation ContinuationOf(int offset, int target)
        {
            var left = Enumerable.Range(0, _regions.Count)
                .Where(r => _regions[r].Kind == IlExceptionRegionKind.Finally && InTry(r, offset) && !InTry(r, target))
                .OrderBy(r => _regions[r].TryLength)
                .ToList();
            Continuation continuation = new ToOffset(target);
            for (var k = left.Count - 1; k >= 0; k--)
            {
                continuation = new Through(left[k], continuation);
            }

            return continuation;
        }

        private bool InTry(int region, int offset) =>
            offset >= _regions[region].TryOffset && offset < _regions[region].TryEnd;

        private bool InHandler(int region, int offset) =>
            offset >= _regions[region].HandlerOffset && offset < _regions[region].HandlerEnd;

        /// <summary>The offset control goes to for <paramref name="continuation"/>.</summary>
        private int OffsetOf(Continuation continuation) => continuation switch
        {
            ToOffset to => to.Offset,
            Through through when _exits[through.Region].Count > 1 =>
                _entries[(through.Region, _exits[through.Region].IndexOf(through.Then))],
            Through through => _regions[through.Region].HandlerOffset,
            _ => throw new InvalidOperationException($"Unknown continuation {continuation}."),
        };

        /// <summary>The instruction at <paramref name="index"/>, folded: a leave or an endfinally becomes a br.</summary>
        private IlInstruction Folded(IReadOnlyList<IlInstruction> instructions, int index)
        {
            var instruction = instructions[index];
            switch (instruction.OpCode)
            {
                case IlOpCode.Leave or IlOpCode.Leave_S:
                    return new IlInstruction(instruction.Offset, instruction.OpCode == IlOpCode.Leave ? IlOpCode.Br : IlOpCode.Br_S,
                        new IlBranchOperand(OffsetOf(_leaves[index])), instruction.Size);

                case IlOpCode.Endfinally:
                    // the innermost finally (or fault) it ends; one no leave reaches never runs, and goes back to its start
                    var region = Enumerable.Range(0, _regions.Count).Where(r => InHandler(r, instruction.Offset))
                        .OrderBy(r => _regions[r].HandlerLength).First();
                    var target = _exits[region].Count switch
                    {
                        0 => _regions[region].HandlerOffset,
                        1 => OffsetOf(_exits[region][0]),
                        _ => _dispatches[region],
                    };
                    return new IlInstruction(instruction.Offset, IlOpCode.Br, new IlBranchOperand(target), instruction.Size);

                case IlOpCode.Callvirt when index > 0 && instructions[index - 1] is
                    {
                        OpCode: IlOpCode.Constrained, Operand: IlTypeOperand { Type: var type },
                    } && IsDispose(instruction) && StructDispose(type, instruction) is { } dispose:
                    return new IlInstruction(instruction.Offset, IlOpCode.Call, new IlMethodOperand(dispose), instruction.Size);

                case IlOpCode.Constrained when index + 1 < instructions.Count && IsDispose(instructions[index + 1])
                    && StructDispose(((IlTypeOperand)instruction.Operand!).Type, instructions[index + 1]) is not null:
                    return new IlInstruction(instruction.Offset, IlOpCode.Nop, null, instruction.Size);

                default:
                    return instruction;
            }
        }

        /// <summary>The ways into the finally of <paramref name="region"/>, each storing its number, and the switch after it.</summary>
        private void AddDispatch(List<IlInstruction> folded, int region, int local)
        {
            var exits = _exits[region];
            for (var e = 0; e < exits.Count; e++)
            {
                var at = _entries[(region, e)];
                folded.Add(new IlInstruction(at, IlOpCode.Ldc_I4, new IlInt32Operand(e), LdcI4Size));
                folded.Add(new IlInstruction(at + LdcI4Size, IlOpCode.Stloc, new IlLocalOperand(local), StlocSize));
                folded.Add(new IlInstruction(at + LdcI4Size + StlocSize, IlOpCode.Br,
                    new IlBranchOperand(_regions[region].HandlerOffset), BrSize));
            }

            // exit i for i below the last, by the switch; the last one after it
            var dispatch = _dispatches[region];
            var cases = exits.Take(exits.Count - 1).Select(OffsetOf).ToList();
            folded.Add(new IlInstruction(dispatch, IlOpCode.Ldloc, new IlLocalOperand(local), LdlocSize));
            folded.Add(new IlInstruction(dispatch + LdlocSize, IlOpCode.Switch, new IlSwitchOperand(cases),
                SwitchSize(cases.Count)));
            folded.Add(new IlInstruction(dispatch + LdlocSize + SwitchSize(cases.Count), IlOpCode.Br,
                new IlBranchOperand(OffsetOf(exits[^1])), BrSize));
        }

        private static int SwitchSize(int cases) => 1 + 4 + (4 * cases);

        private static bool IsDispose(IlInstruction instruction) => instruction is
        {
            OpCode: IlOpCode.Callvirt,
            Operand: IlMethodOperand
            {
                Method:
                {
                    Name: "Dispose", HasThis: true, ParameterTypes.Count: 0, GenericArguments.Count: 0,
                    DeclaringType: IlNamedType { FullName: "System.IDisposable", IsDefinedInThisAssembly: false },
                },
            },
        };

        /// <summary>
        /// The Dispose of the struct <paramref name="type"/> (closed: not a T) that <paramref name="call"/>, a callvirt of
        /// IDisposable.Dispose, runs on it, as a reference; null when it is none.
        /// </summary>
        private IlMethodReference? StructDispose(IlType type, IlInstruction call)
        {
            var owner = body.Method.Assembly;
            if (Generics.Substitution.IsOpen(type)
                || ValueTypes.Definition(ValueTypes.WithoutModifiers(type), owner) is not { } definition
                || !ValueTypes.IsStruct(definition))
            {
                return null;
            }

            var dispose = ((IlMethodOperand)call.Operand!).Method;
            return Generics.ConstrainedCalls.Implementation(definition, dispose, owner) is { } implementation
                ? new IlMethodReference(ValueTypes.WithoutModifiers(type), implementation.Name, implementation.Signature, [],
                    implementation)
                : null;
        }
    }
}
