using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The whole-program pass: finds every method the program uses, starting at its entry point and following
/// <c>call</c> instructions (and the type initializers of the types the methods use), and checks each one against
/// <see cref="SupportedSubset"/>. What it returns, an <see cref="IlProgram"/>, is what the IL-to-TIR translation compiles.
/// </summary>
public static partial class WholeProgramPass
{
    private static readonly IlArrayType StringArray = new(IlType.String);

    /// <summary>
    /// The program in <paramref name="assembly"/>: its entry point, its interrupt handlers (see <see cref="Interrupts"/>)
    /// and every method of the assembly they reach through calls. Methods nothing calls are left out, and not checked.
    /// </summary>
    /// <exception cref="ChipSharpException">
    /// The assembly has no entry point, or its methods use C# that <see cref="SupportedSubset"/> does not have. The
    /// message then is the <see cref="AnalysisResult.Summary"/> of <see cref="Check(IlAssembly)"/>: every feature in C#
    /// words, with how many places use it and where, and a hint.
    /// </exception>
    public static IlProgram Analyze(IlAssembly assembly) => Check(assembly).ToProgram();

    /// <summary>
    /// The methods of the program in <paramref name="assembly"/>, found as <see cref="Analyze(IlAssembly)"/> finds them,
    /// and every place they use C# that <see cref="SupportedSubset"/> does not have, as data: one problem per place (the
    /// method, the IL offset where there is one, what is not supported in C# words, and a hint), and the problems by
    /// feature. Interrupt handlers must name one of ChipSharp.Hardware's events.
    /// </summary>
    /// <exception cref="ChipSharpException">The assembly has no entry point.</exception>
    public static AnalysisResult Check(IlAssembly assembly) => Check(assembly, null);

    /// <summary>
    /// Like <see cref="Check(IlAssembly)"/>, for <paramref name="target"/>: interrupt handlers must name an event the
    /// target has an interrupt for (none on a PC), and the program may use only what the target has (see
    /// <see cref="TargetRules"/>: a heap, a console). Null checks the handlers against ChipSharp.Hardware's events
    /// instead, and the rest for any target.
    /// </summary>
    /// <exception cref="ChipSharpException">The assembly has no entry point, or the target's "interrupts" are not well formed.</exception>
    public static AnalysisResult Check(IlAssembly assembly, TargetDescription? target)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var entryPoint = assembly.EntryPoint ?? throw new ChipSharpException(
            $"{assembly.FilePath} has no entry point: a program needs a static void Main() (or top-level statements) to start from.");
        var interruptProblems = new List<Problem>();
        var handlers = FindHandlers(assembly, target, interruptProblems);
        var methods = FindReachableMethods(entryPoint, handlers.Select(handler => handler.Method));

        var problems = new List<Problem>();
        var statics = StaticArrays.Find(methods);
        var heap = HeapArrays.Find(methods, statics);
        var heapArrays = new HashSet<IlInstruction>(); // the newarr instructions that make an array on the heap
        foreach (var method in methods)
        {
            CheckMethod(method, problems, statics, heap, heapArrays);
        }

        var forTarget = TargetRules.Apply(target, methods, problems);
        CheckHandlers(entryPoint, methods, handlers, interruptProblems, heapArrays);
        problems.AddRange(interruptProblems);
        return new AnalysisResult(assembly, methods, problems)
        {
            InterruptHandlers = handlers,
            DetailsTarget = forTarget ? target!.Name : null,
            UnalignedAccess = target?.HasCapability("unalignedAccess") ?? true,
        };
    }

    /// <summary>
    /// The entry point, then the interrupt handlers (<paramref name="handlers"/>), then every method of the assembly
    /// they reach through <c>call</c> and <c>callvirt</c> (and through <c>newobj</c> of a struct or a class, which calls
    /// its constructor; see <see cref="ValueTypes.CalledMethod"/>), each once, in the order they are first called when
    /// the methods are scanned in this same order, instruction by instruction. The type initializers each method makes
    /// run (see <see cref="StaticInitialization"/>) come before the methods it calls. A virtual call reaches what its slot
    /// runs on the objects the program makes (<see cref="VirtualDispatch"/>), not the method it names.
    /// </summary>
    private static IlMethod[] FindReachableMethods(IlMethod entryPoint, IEnumerable<IlMethod> handlers)
    {
        var methods = new List<IlMethod> { entryPoint };
        var found = new HashSet<IlMethod> { entryPoint };
        var dispatch = new VirtualDispatch();
        methods.AddRange(handlers.Where(found.Add));
        for (var i = 0; i < methods.Count; i++)
        {
            foreach (var initializer in StaticInitialization.InitializersRunBy(methods[i], isStart: i == 0))
            {
                if (found.Add(initializer))
                {
                    methods.Add(initializer);
                }
            }

            // generic code is reached as the instances calls are linked to (see Generics.Instantiations), never itself
            var instructions = methods[i].Body?.Instructions ?? [];
            for (var j = 0; j < instructions.Count; j++)
            {
                if (ValueTypes.CalledMethod(instructions[j]) is { } callee && !Generics.Instantiations.IsGeneric(callee)
                    && VirtualMethods.CalledSlot(instructions, j) is null && found.Add(callee))
                {
                    methods.Add(callee);
                }

                methods.AddRange(dispatch.Follow(instructions, j, methods[i].Assembly).Where(found.Add));
            }
        }

        return [.. methods];
    }

    /// <summary>
    /// Adds a problem to <paramref name="problems"/> for every place in <paramref name="method"/> that ChipSharp cannot
    /// compile. <paramref name="statics"/> are the program's arrays in static fields, <paramref name="heap"/> what it
    /// keeps of the arrays its methods are given (see <see cref="HeapArrays"/>); the newarr instructions that make an
    /// array on the heap are added to <paramref name="heapArrays"/>.
    /// </summary>
    private static void CheckMethod(IlMethod method, List<Problem> problems, StaticArrays statics, HeapArrays heap,
        HashSet<IlInstruction> heapArrays)
    {
        // an array of references compiles only with a heap: a heap use
        Unsupported? ForType(IlType type) => SupportedSubset.TryMapType(type, method.Assembly, out _)
            ? HeapArrays.ForType(type, method.Assembly)
            : ForAnyType(type, method.Assembly, method);

        void CheckSignature(IlType type, string what)
        {
            if (ForType(type) is { } unsupported)
            {
                problems.Add(Problem.InSignature(method, what, type, unsupported));
            }
        }

        if (!method.IsStatic && ForInstanceMethod(method) is { } instance)
        {
            problems.Add(Problem.InMethod(method, instance));
        }

        if (method.GenericParameters.Count > 0)
        {
            problems.Add(Problem.InMethod(method, Unsupported.GenericMethods));
        }

        if (method.DeclaringType.GenericParameters.Count > 0)
        {
            problems.Add(Problem.InMethod(method, Unsupported.GenericTypes));
        }

        // an abstract method has no body: only its overrides run (it is a virtual method, reported above)
        if (!method.HasBody && !ClassRules.IsAbstract(method))
        {
            problems.Add(Problem.InMethod(method, Unsupported.MethodsWithoutBody));
        }

        CheckSignature(method.ReturnType, "return type");
        ArrayEscapeAnalysis.CheckReturnType(method, problems);

        // the entry point may take a string[] args it never uses: C#'s top-level statements always declare one
        int? argsArgument = null;
        foreach (var parameter in method.Parameters)
        {
            if (method.IsEntryPoint && parameter.Type == StringArray)
            {
                argsArgument = parameter.Index + (method.IsStatic ? 0 : 1);
            }
            else
            {
                CheckSignature(parameter.Type, $"parameter '{parameter.Name}'");
            }
        }

        if (method.Body is not { } body)
        {
            return;
        }

        // the locals C# builds interpolated strings in: taking their address is part of building the text, which
        // printed text (PrintedText) judges where it is used, not as a local (so are the params spans of formats)
        var interpolations = new HashSet<int>();
        var printed = PrintedText.Of(method);
        foreach (var local in body.Locals)
        {
            if (ForType(local.Type) is { } unsupported && !Unsupported.IsInterpolatedStringHandler(local.Type)
                && !printed.InlineArrayLocals.Contains(local.Index))
            {
                problems.Add(Problem.InLocal(method, local, unsupported));
            }

            if (Unsupported.IsInterpolatedStringHandler(local.Type))
            {
                interpolations.Add(local.Index);
            }
        }

        foreach (var tryOffset in body.ExceptionRegions.Select(region => region.TryOffset).Distinct().Order())
        {
            problems.Add(Problem.AtTry(method, tryOffset, ForTry(method, tryOffset)));
        }

        // the verdict of printed text comes first for the instructions it is about, then the arrays' rule's (a problem;
        // then spans' problem; then a heap use or a fine), then generics', then virtual calls', then classes'
        var analyzed = ArrayEscapeAnalysis.Analyze(method, statics, heap);
        var arrays = analyzed.Verdicts;
        heapArrays.UnionWith(body.Instructions.Where(i => i.OpCode == IlOpCode.Newarr && analyzed.HeapArrays.Contains(i.Offset)));
        var spans = ForSpans(method);
        var generics = ForGenerics(method);
        var virtuals = ForVirtualCalls(method);
        var classes = ForClasses(method);
        var strings = ForStrings(method);
        var boxing = ForBoxing(method);
        // after the arrays' and spans' verdicts: boxing's, strings', generics', virtual calls', classes', building text,
        // the struct/reference rules, the rest
        Unsupported? Others(IlInstruction instruction) =>
            boxing.TryGetValue(instruction, out var forBoxing) ? forBoxing
            : strings.TryGetValue(instruction, out var forStrings) ? forStrings
            : generics.TryGetValue(instruction, out var generic) ? generic
            : virtuals.TryGetValue(instruction, out var dispatched) ? dispatched
            : classes.TryGetValue(instruction, out var forClasses) ? forClasses
            : ForText(method, instruction) ?? ForStructOrReference(method, instruction)
                ?? ForInstruction(instruction, argsArgument, interpolations);

        foreach (var instruction in body.Instructions)
        {
            // what printed text, the array escape analysis, spans, generics, virtual calls or classes found for this
            // instruction, else building text, else the struct/reference rules, else the rest; a heap use (fine where
            // there is a heap) does not hide a problem the rules after it find
            var unsupported = printed.Verdicts.TryGetValue(instruction, out var text) ? text
                : arrays.TryGetValue(instruction, out var array)
                    && (array is { IsHeapUse: false } || (spans.GetValueOrDefault(instruction) is not { IsHeapUse: false }
                        && (array is not null || spans.GetValueOrDefault(instruction) is null)))
                    ? array
                : spans.TryGetValue(instruction, out var span) ? span
                : Others(instruction);
            // (but printed text's own, built text that is kept, is the whole verdict on what printed text is about)
            if (unsupported is { IsHeapUse: true } && !printed.Owns(instruction)
                && Others(instruction) is { IsHeapUse: false } other)
            {
                unsupported = other;
            }

            // a use of an instance over a reference type (generics over classes) needs a heap, but hides no problem
            // (an InlineArray of objects for a format's params span is printed text's, which makes no instance)
            unsupported ??= printed.Owns(instruction) ? null : Generics.ReferenceArguments.Use(method, instruction);
            if (unsupported is not null)
            {
                problems.Add(Problem.At(method, instruction, unsupported));
            }
        }
    }

    /// <summary>
    /// What <paramref name="instruction"/> does that ChipSharp cannot compile, or null. <paramref name="argsArgument"/> is
    /// the entry point's unused args; <paramref name="interpolations"/> the locals of interpolated strings.
    /// </summary>
    private static Unsupported? ForInstruction(IlInstruction instruction, int? argsArgument, HashSet<int> interpolations)
    {
        if (argsArgument is not null && instruction.ArgumentIndex == argsArgument)
        {
            return Unsupported.Arguments;
        }

        if (instruction is { OpCode: IlOpCode.Ldloca or IlOpCode.Ldloca_S, LocalIndex: { } local }
            && interpolations.Contains(local))
        {
            return Unsupported.BuildingText;
        }

        if (!SupportedSubset.OpCodes.Contains(instruction.OpCode))
        {
            var unsupported = Unsupported.ForInstruction(instruction.OpCode);
            return unsupported == Unsupported.OtherInstruction ? Unsupported.InstructionNamed(instruction.Name) : unsupported;
        }

        if (ForStaticField(instruction) is { } field)
        {
            return field;
        }

        if (instruction is
            {
                OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: { Definition: null } callee },
            } && !KnownExternals.TryMap(callee, out _) && ArrayMethods.Call(callee) is null)
        {
            return Unsupported.LibraryMethod(CSharpNames.Method(callee));
        }

        return null;
    }

    /// <summary>
    /// What <paramref name="instruction"/> does with text that needs the heap, or null: a call, callvirt or newobj of a
    /// library method that builds or reads text (<see cref="FixedText.IsBuildingText"/>: string's methods, ToString,
    /// the calls C# makes for <c>$"..."</c>, which are the interpolation, not calls the program wrote), or the
    /// <c>constrained.</c> prefix of one (C#'s <c>value.ToString()</c>). Text itself, a literal, is fine.
    /// </summary>
    private static Unsupported? ForText(IlMethod method, IlInstruction instruction)
    {
        var operand = instruction.OpCode == IlOpCode.Constrained ? Next(method, instruction)?.Operand : instruction.Operand;
        return operand is IlMethodOperand { Method: var callee } && FixedText.IsBuildingText(callee)
            ? Unsupported.BuildingText
            : null;
    }

    private static IlInstruction? Next(IlMethod method, IlInstruction instruction) =>
        method.Body!.IndexOf(instruction.Offset) + 1 is var index && index < method.Body.Instructions.Count
            ? method.Body.Instructions[index]
            : null;
}
