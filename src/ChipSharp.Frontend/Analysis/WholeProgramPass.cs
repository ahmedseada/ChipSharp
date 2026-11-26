using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The whole-program pass: finds every method the program uses, starting at its entry point and following
/// <c>call</c> instructions, and checks each one against <see cref="SupportedSubset"/>. What it returns, an
/// <see cref="IlProgram"/>, is what the IL-to-TIR translation compiles.
/// </summary>
public static class WholeProgramPass
{
    private static readonly IlArrayType StringArray = new(IlType.String);

    /// <summary>
    /// The program in <paramref name="assembly"/>: its entry point and every method of the assembly the entry point
    /// reaches through calls. Methods nothing calls are left out, and not checked.
    /// </summary>
    /// <exception cref="ChipSharpException">
    /// The assembly has no entry point, or its methods use C# that <see cref="SupportedSubset"/> does not have. The
    /// message then is the <see cref="AnalysisResult.Summary"/> of <see cref="Check"/>: every feature in C# words, with
    /// how many places use it and where, and a hint.
    /// </exception>
    public static IlProgram Analyze(IlAssembly assembly) => Check(assembly).ToProgram();

    /// <summary>
    /// The methods of the program in <paramref name="assembly"/>, found as <see cref="Analyze"/> finds them, and every
    /// place they use C# that <see cref="SupportedSubset"/> does not have, as data: one problem per place (the method,
    /// the IL offset where there is one, what is not supported in C# words, and a hint), and the problems by feature.
    /// </summary>
    /// <exception cref="ChipSharpException">The assembly has no entry point.</exception>
    public static AnalysisResult Check(IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var entryPoint = assembly.EntryPoint ?? throw new ChipSharpException(
            $"{assembly.FilePath} has no entry point: a program needs a static void Main() (or top-level statements) to start from.");
        var methods = FindReachableMethods(entryPoint);

        var problems = new List<Problem>();
        foreach (var method in methods)
        {
            CheckMethod(method, problems);
        }

        return new AnalysisResult(assembly, methods, problems);
    }

    /// <summary>
    /// The entry point, then every method of the assembly it reaches through <c>call</c>, each once, in the order
    /// they are first called when the methods are scanned in this same order, instruction by instruction.
    /// </summary>
    private static IlMethod[] FindReachableMethods(IlMethod entryPoint)
    {
        var methods = new List<IlMethod> { entryPoint };
        var found = new HashSet<IlMethod> { entryPoint };
        for (var i = 0; i < methods.Count; i++)
        {
            foreach (var instruction in methods[i].Body?.Instructions ?? [])
            {
                if (instruction is { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method.Definition: { } callee } }
                    && found.Add(callee))
                {
                    methods.Add(callee);
                }
            }
        }

        return [.. methods];
    }

    /// <summary>Adds a problem to <paramref name="problems"/> for every place in <paramref name="method"/> that M0 cannot compile.</summary>
    private static void CheckMethod(IlMethod method, List<Problem> problems)
    {
        Unsupported? ForType(IlType type)
        {
            if (SupportedSubset.TryMapType(type, out _) || StackArrays.TryMapType(type, out _))
            {
                return null;
            }

            var unsupported = Unsupported.ForType(type, method.Assembly);
            return unsupported == Unsupported.OtherType ? Unsupported.TypeNamed(CSharpNames.Type(type, method)) : unsupported;
        }

        void CheckSignature(IlType type, string what)
        {
            if (ForType(type) is { } unsupported)
            {
                problems.Add(Problem.InSignature(method, what, type, unsupported));
            }
        }

        if (!method.IsStatic)
        {
            problems.Add(Problem.InMethod(method, Unsupported.InstanceMethods));
        }

        if (method.GenericParameters.Count > 0)
        {
            problems.Add(Problem.InMethod(method, Unsupported.GenericMethods));
        }

        if (method.DeclaringType.GenericParameters.Count > 0)
        {
            problems.Add(Problem.InMethod(method, Unsupported.GenericTypes));
        }

        if (!method.HasBody)
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

        // the locals C# builds interpolated strings in: taking their address is part of the string interpolation
        var interpolations = new HashSet<int>();
        foreach (var local in body.Locals)
        {
            if (ForType(local.Type) is { } unsupported)
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
            problems.Add(Problem.AtTry(method, tryOffset, Unsupported.TryCatchFinally));
        }

        // the verdict of the arrays' rule comes first for the instructions it is about
        var arrays = ArrayEscapeAnalysis.Check(method);
        foreach (var instruction in body.Instructions)
        {
            var unsupported = arrays.TryGetValue(instruction, out var array)
                ? array
                : ForInstruction(instruction, argsArgument, interpolations);
            if (unsupported is not null)
            {
                problems.Add(Problem.At(method, instruction, unsupported));
            }
        }
    }

    /// <summary>
    /// What <paramref name="instruction"/> does that M0 cannot compile, or null. <paramref name="argsArgument"/> is the
    /// entry point's unused args; <paramref name="interpolations"/> the locals of interpolated strings.
    /// </summary>
    private static Unsupported? ForInstruction(IlInstruction instruction, int? argsArgument, HashSet<int> interpolations)
    {
        if (argsArgument is not null && instruction.ArgumentIndex == argsArgument)
        {
            return Unsupported.Arguments;
        }

        if (!SupportedSubset.OpCodes.Contains(instruction.OpCode))
        {
            if (instruction is { OpCode: IlOpCode.Ldloca or IlOpCode.Ldloca_S, LocalIndex: { } local }
                && interpolations.Contains(local))
            {
                return Unsupported.StringInterpolation;
            }

            var unsupported = Unsupported.ForInstruction(instruction.OpCode);
            return unsupported == Unsupported.OtherInstruction ? Unsupported.InstructionNamed(instruction.Name) : unsupported;
        }

        if (instruction is { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Definition: null } callee } }
            && !KnownExternals.TryMap(callee, out _))
        {
            // the calls C# makes to build an interpolated string are the interpolation, not calls the program wrote
            return Unsupported.IsInterpolatedStringHandler(callee.DeclaringType)
                ? Unsupported.StringInterpolation
                : Unsupported.LibraryMethod(CSharpNames.Method(callee));
        }

        return null;
    }
}
