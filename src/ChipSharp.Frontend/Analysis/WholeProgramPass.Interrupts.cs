using System.Reflection;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Interrupt handlers in the whole-program pass (see <see cref="Interrupts"/>): the methods marked
/// <c>[Interrupt(...)]</c> are part of the program though nothing calls them, and the pass checks their rules.
/// </summary>
public static partial class WholeProgramPass
{
    /// <summary>
    /// Like <see cref="Analyze(IlAssembly)"/>, for <paramref name="target"/>: each interrupt handler must name an event
    /// the target has an interrupt for (its "interrupts" table), and the program it returns knows that interrupt, so that
    /// the translation can put the handler where the chip looks for it.
    /// </summary>
    /// <exception cref="ChipSharpException">As for <see cref="Analyze(IlAssembly)"/>; also when the target's table is not well formed.</exception>
    public static IlProgram Analyze(IlAssembly assembly, TargetDescription target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Check(assembly, target).ToProgram();
    }

    /// <summary>
    /// The methods marked <c>[Interrupt(...)]</c> that may handle their event: those of the right shape (static, void,
    /// no parameters) that name an event of <paramref name="target"/> (of ChipSharp.Hardware when it is null). A
    /// problem for each that may not, and for each of two or more handlers of one event.
    /// </summary>
    private static List<InterruptHandler> FindHandlers(IlAssembly assembly, TargetDescription? target, List<Problem> problems)
    {
        var table = target is null ? null : Interrupts.ForTarget(target);
        var handlers = new List<InterruptHandler>();
        foreach (var (method, name) in Interrupts.FindMarked(assembly))
        {
            if (!method.IsStatic || method.ReturnType != IlType.Void || method.Parameters.Count > 0)
            {
                problems.Add(Problem.InMethod(method, Unsupported.HandlerShape));
            }
            else if (table is { Count: 0 })
            {
                problems.Add(Problem.InMethod(method, Unsupported.NoInterrupts(target!.Name)));
            }
            else if (name is not null && table is not null && table.TryGetValue(name, out var interrupt))
            {
                handlers.Add(new InterruptHandler(method, name, interrupt));
            }
            else if (name is not null && table is null && Interrupts.LibraryEvents.Contains(name, StringComparer.Ordinal))
            {
                handlers.Add(new InterruptHandler(method, name, null));
            }
            else
            {
                var known = table?.Keys ?? Interrupts.LibraryEvents;
                problems.Add(Problem.InMethod(method, Unsupported.UnknownEvent(name, known, target?.Name)));
            }
        }

        foreach (var sameEvent in handlers.GroupBy(handler => handler.Event).Where(group => group.Count() > 1))
        {
            problems.AddRange(sameEvent.Select(handler => Problem.InMethod(handler.Method, Unsupported.TwoHandlers(sameEvent.Key))));
        }

        return handlers;
    }

    /// <summary>
    /// Adds a problem for every place in <paramref name="methods"/> (the program, with <paramref name="entryPoint"/>
    /// first) that breaks a rule of the <paramref name="handlers"/>: a call that starts an event no method is marked to
    /// handle, a wait or a new object (or an array on the heap, <paramref name="heapArrays"/>) in what a handler runs,
    /// and every use of a static field that a handler and the rest of the program share (one of them writing it) when it
    /// is not volatile. The type initializers run before Main, before any interrupt can come, so what they alone do
    /// does not count.
    /// </summary>
    private static void CheckHandlers(IlMethod entryPoint, IReadOnlyList<IlMethod> methods,
        IReadOnlyList<InterruptHandler> handlers, List<Problem> problems, IReadOnlySet<IlInstruction> heapArrays)
    {
        var program = methods.ToHashSet();
        var main = Reach([entryPoint], program);
        var inHandlers = Reach(handlers.Select(handler => handler.Method), program);

        // a method marked for the event that breaks a rule has a problem of its own already
        var handled = Interrupts.FindMarked(entryPoint.Assembly).Select(marked => marked.Event).OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        // the fields each side uses, and whether either writes them
        var mainFields = new HashSet<IlField>();
        var handlerFields = new HashSet<IlField>();
        var written = new HashSet<IlField>();
        foreach (var method in methods.Where(method => main.Contains(method) || inHandlers.Contains(method)))
        {
            foreach (var (instruction, field) in FieldUses(method))
            {
                if (main.Contains(method))
                {
                    mainFields.Add(field);
                }

                if (inHandlers.Contains(method))
                {
                    handlerFields.Add(field);
                }

                // through its address (ref field, a field of a struct field) it may be written too, unless it is
                // readonly: then only its type's initializer writes it, before Main
                if (instruction.OpCode is IlOpCode.Stsfld or IlOpCode.Ldsflda
                    && (field.Attributes & FieldAttributes.InitOnly) == 0)
                {
                    written.Add(field);
                }
            }
        }

        var shared = mainFields.Where(field => handlerFields.Contains(field) && written.Contains(field) && !IsVolatile(field))
            .ToHashSet();

        // the places are in the program's own methods: a library substitute's calls count where the program calls it
        foreach (var method in methods.Where(method => !LibrarySubstitutes.Defines(method)))
        {
            foreach (var instruction in method.Body?.Instructions ?? [])
            {
                foreach (var external in RuntimeCalls(instruction))
                {
                    if (Interrupts.Starters.TryGetValue(external.DisplayName, out var started) && !handled.Contains(started))
                    {
                        problems.Add(Problem.At(method, instruction, Unsupported.Unhandled(started)));
                    }

                    if (inHandlers.Contains(method) && Interrupts.Waits.Contains(external.DisplayName))
                    {
                        problems.Add(Problem.At(method, instruction, Unsupported.WaitInHandler));
                    }
                }

                // the runtime stops a program that makes an object (an array on the heap too) in a handler: this finds
                // it before it runs
                if (inHandlers.Contains(method) && (ClassRules.Allocates(instruction) || heapArrays.Contains(instruction)
                    || LibraryAllocates(instruction, program, heapArrays))) // WholeProgramPass.Collections.cs
                {
                    problems.Add(Problem.At(method, instruction, Unsupported.AllocationInHandler));
                }
            }

            if (main.Contains(method) || inHandlers.Contains(method))
            {
                foreach (var (instruction, field) in FieldUses(method).Where(use => shared.Contains(use.Field)))
                {
                    problems.Add(Problem.At(method, instruction, Unsupported.NotVolatile(field)));
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="roots"/> and every method of <paramref name="program"/> they reach through calls (constructors
    /// too, see <see cref="ValueTypes.CalledMethod"/>), and through virtual calls what they can run on the objects the
    /// program makes (<see cref="VirtualDispatch"/>).
    /// </summary>
    private static HashSet<IlMethod> Reach(IEnumerable<IlMethod> roots, HashSet<IlMethod> program)
    {
        var reached = new HashSet<IlMethod>();
        var pending = new Stack<IlMethod>(roots);
        VirtualDispatch? dispatch = null;
        Dictionary<string, HashSet<IlMethod>>? delegateTargets = null;
        while (pending.TryPop(out var method))
        {
            if (!reached.Add(method))
            {
                continue;
            }

            var instructions = method.Body?.Instructions ?? [];
            for (var i = 0; i < instructions.Count; i++)
            {
                if (ValueTypes.CalledMethod(instructions[i]) is { } callee && program.Contains(callee))
                {
                    pending.Push(callee);
                }

                if (VirtualMethods.CalledSlot(instructions, i) is { } slot)
                {
                    dispatch ??= VirtualDispatch.Of(program);
                    foreach (var target in dispatch.Targets(slot, null))
                    {
                        if (target.Method is { } overrider && program.Contains(overrider))
                        {
                            pending.Push(overrider);
                        }
                    }
                }

                // a delegate invoked runs what a delegate of its type may call (see Delegates)
                if (instructions[i] is { Operand: IlMethodOperand { Method: var invoked } }
                    && Delegates.IsInvoke(invoked, method.Assembly))
                {
                    dispatch ??= VirtualDispatch.Of(program);
                    delegateTargets ??= Delegates.TargetsByType(program, dispatch);
                    foreach (var target in delegateTargets.GetValueOrDefault(Delegates.Family(invoked.DeclaringType,
                        method.Assembly)) ?? [])
                    {
                        if (program.Contains(target))
                        {
                            pending.Push(target);
                        }
                    }
                }
            }
        }

        return reached;
    }

    /// <summary>
    /// The static fields of the program's own types that <paramref name="method"/> reads (ldsfld), writes (stsfld) or
    /// takes the address of (ldsflda: <c>ref field</c>, or a field of a struct in it).
    /// </summary>
    private static IEnumerable<(IlInstruction Instruction, IlField Field)> FieldUses(IlMethod method)
    {
        foreach (var instruction in method.Body?.Instructions ?? [])
        {
            if (instruction is
                {
                    OpCode: IlOpCode.Ldsfld or IlOpCode.Stsfld or IlOpCode.Ldsflda,
                    Operand: IlFieldOperand { Field.Definition: { IsStatic: true } field },
                })
            {
                yield return (instruction, field);
            }
        }
    }

    /// <summary>True for a C# <c>volatile</c> field: its type is required to be modified by IsVolatile.</summary>
    private static bool IsVolatile(IlField field)
    {
        for (var type = field.Type; type is IlModifiedType modified; type = modified.UnmodifiedType)
        {
            if (modified is
                {
                    IsRequired: true,
                    Modifier: IlNamedType { Namespace: "System.Runtime.CompilerServices", Name: "IsVolatile" },
                })
            {
                return true;
            }
        }

        return false;
    }
}
