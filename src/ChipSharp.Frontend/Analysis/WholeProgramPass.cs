using System.Globalization;
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

    private static readonly string SupportedLibraryCalls =
        string.Join(", ", KnownExternals.All.Select(function => function.LibraryMethod));

    /// <summary>
    /// The program in <paramref name="assembly"/>: its entry point and every method of the assembly the entry point
    /// reaches through calls. Methods nothing calls are left out, and not checked.
    /// </summary>
    /// <exception cref="ChipSharpException">
    /// The assembly has no entry point, or its methods use C# that <see cref="SupportedSubset"/> does not have. The
    /// message then lists every problem, one per line: the method, the IL offset where there is one, what is not
    /// supported in C# words, and a hint.
    /// </exception>
    public static IlProgram Analyze(IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var entryPoint = assembly.EntryPoint ?? throw new ChipSharpException(
            $"{assembly.FilePath} has no entry point: a program needs a static void Main() (or top-level statements) to start from.");
        var methods = FindReachableMethods(entryPoint);

        var problems = new List<string>();
        foreach (var method in methods)
        {
            Check(method, problems);
        }

        if (problems.Count > 0)
        {
            var count = problems.Count == 1
                ? "1 problem"
                : string.Create(CultureInfo.InvariantCulture, $"{problems.Count} problems");
            throw new ChipSharpException(
                $"{assembly.FilePath} uses C# that {SupportedSubset.Milestone} does not compile yet ({count}):\n" +
                string.Join("\n", problems.Select(problem => $"  {problem}")));
        }

        return new IlProgram(assembly, methods);
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

    /// <summary>Adds a line to <paramref name="problems"/> for everything in <paramref name="method"/> that M0 cannot compile.</summary>
    private static void Check(IlMethod method, List<string> problems)
    {
        var name = CSharpNames.Method(method);

        void Add(string where, string message) => problems.Add($"{where}: {message}");

        void AddAt(IlInstruction instruction, string message) =>
            Add($"{name} at {IlPrinter.Label(instruction.Offset)} ({instruction.Name})", message);

        void CheckType(IlType type, string what)
        {
            if (!SupportedSubset.TryMapType(type, out _) && Unsupported.ForType(type, method.Assembly) is { } unsupported)
            {
                Add($"{name}, {what} ({CSharpNames.Type(type, method)})", unsupported.Message);
            }
        }

        if (!method.IsStatic)
        {
            Add(name, Unsupported.InstanceMethods.Message);
        }

        if (method.GenericParameters.Count > 0)
        {
            Add(name, Unsupported.GenericMethods.Message);
        }

        if (method.DeclaringType.GenericParameters.Count > 0)
        {
            Add(name, Unsupported.GenericTypes.Message);
        }

        if (!method.HasBody)
        {
            Add(name, Unsupported.MethodsWithoutBody.Message);
        }

        CheckType(method.ReturnType, "return type");

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
                CheckType(parameter.Type, $"parameter '{parameter.Name}'");
            }
        }

        if (method.Body is not { } body)
        {
            return;
        }

        foreach (var local in body.Locals)
        {
            CheckType(local.Type, $"local variable V_{local.Index.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (var tryOffset in body.ExceptionRegions.Select(region => region.TryOffset).Distinct().Order())
        {
            Add($"{name} at {IlPrinter.Label(tryOffset)}", Unsupported.TryCatchFinally.Message);
        }

        foreach (var instruction in body.Instructions)
        {
            if (argsArgument is not null && instruction.ArgumentIndex == argsArgument)
            {
                AddAt(instruction, Unsupported.Arguments.Message);
            }
            else if (!SupportedSubset.OpCodes.Contains(instruction.OpCode))
            {
                AddAt(instruction, Unsupported.ForInstruction(instruction.OpCode).Message);
            }
            else if (instruction is { OpCode: IlOpCode.Call, Operand: IlMethodOperand { Method: { Definition: null } callee } }
                && !KnownExternals.TryMap(callee, out _))
            {
                AddAt(instruction, $"the library method {CSharpNames.Method(callee)} is not available in " +
                    $"{SupportedSubset.Milestone}. {SupportedSubset.Milestone} supports these library calls: {SupportedLibraryCalls}.");
            }
        }
    }
}
