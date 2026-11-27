using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The notes of a program (see <see cref="Note"/>): the places where it calls a library method whose result in .NET
/// depends on the culture, the PC's language settings, where ChipSharp's is the same everywhere: it compares and
/// searches text char by char (as .NET does in its invariant mode, or with <c>StringComparison.Ordinal</c>), changes
/// case as the invariant culture does, and reads and writes numbers as the invariant culture does. Each note says what
/// to write to get ChipSharp's result on .NET too, where there is something to write.
/// </summary>
/// <remarks>
/// The program's own methods are looked at, generic ones too, not the library substitutes (ChipSharp's own C#).
/// <list type="bullet">
/// <item><c>CompareTo</c>, <c>string.Compare(a, b)</c>, and <c>StartsWith</c>, <c>EndsWith</c>, <c>IndexOf</c> and
/// <c>LastIndexOf</c> of a string (not of a char: those are ordinal in .NET too); their overloads with a
/// <c>StringComparison</c> unless it is the constant <c>Ordinal</c> or <c>OrdinalIgnoreCase</c>;</item>
/// <item><c>ToUpper()</c> and <c>ToLower()</c> of a string and of a char (not their Invariant forms);</item>
/// <item><c>Parse</c> and <c>TryParse</c> of the whole numbers;</item>
/// <item>a float or double written as text: <c>Console.Write(x)</c>, <c>$"{x}"</c>, <c>"a" + x</c>,
/// <c>x.ToString()</c>, <c>string.Format("{0}", x)</c>.</item>
/// </list>
/// </remarks>
internal static class CultureNotes
{
    /// <summary>What ChipSharp does where .NET compares by a culture.</summary>
    private const string ComparesCodes = "ChipSharp compares character codes (like .NET's invariant mode)";

    private const string SameEverywhere = "to get the same result everywhere.";

    /// <summary>The values of System.StringComparison.</summary>
    private static readonly string[] Comparisons =
    [
        "CurrentCulture", "CurrentCultureIgnoreCase", "InvariantCulture", "InvariantCultureIgnoreCase", "Ordinal",
        "OrdinalIgnoreCase",
    ];

    /// <summary>A float or double written as text.</summary>
    private static readonly Difference FloatText = new("a float or double as text",
        "a float or double written as text (Console.Write(x), $\"{x}\", \"a\" + x, x.ToString()) depends on the PC's " +
        "language settings in .NET (1,5 with German settings); ChipSharp writes it as .NET's invariant culture does (1.5)",
        "");

    /// <summary>The notes of the program in <paramref name="assembly"/>, whose methods are <paramref name="methods"/>.</summary>
    public static IReadOnlyList<Note> Find(IlAssembly assembly, IReadOnlyList<IlMethod> methods)
    {
        var notes = new List<Note>();
        foreach (var method in ProgramMethods(assembly, methods))
        {
            var instructions = method.Body!.Instructions;
            HashSet<IlInstruction> noted = [];
            for (var i = 0; i < instructions.Count; i++)
            {
                if (ForCall(method, i) is { } difference && noted.Add(instructions[i]))
                {
                    notes.Add(new Note(method, instructions[i], difference));
                }
            }

            if (MayWriteFloats(instructions))
            {
                var printed = PrintedText.Of(method);
                foreach (var piece in printed.Prints.Values.Concat(printed.Builds.Values).SelectMany(site => site.Pieces)
                    .Where(piece => piece is { Literal: null, Type.Kind: IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64 }))
                {
                    if (noted.Add(piece.Source))
                    {
                        notes.Add(new Note(method, piece.Source, FloatText));
                    }
                }
            }
        }

        return [.. notes.OrderBy(note => Order(methods, note.Method)).ThenBy(note => note.Offset)];
    }

    /// <summary>
    /// The methods of <paramref name="methods"/> that are the program's own (not the library substitutes'), then the
    /// generic methods of the program they reach, which the program uses as their instances.
    /// </summary>
    private static List<IlMethod> ProgramMethods(IlAssembly assembly, IReadOnlyList<IlMethod> methods)
    {
        var found = new List<IlMethod>();
        var seen = new HashSet<IlMethod>();
        void Add(IlMethod method)
        {
            if (method.Body is not null && ReferenceEquals(method.DeclaringType.Assembly, assembly) && seen.Add(method))
            {
                found.Add(method);
            }
        }

        foreach (var method in methods)
        {
            Add(method);
        }

        for (var i = 0; i < found.Count; i++)
        {
            foreach (var instruction in found[i].Body!.Instructions)
            {
                if (instruction.Operand is IlMethodOperand { Method.Definition: { } callee }
                    && Generics.Instantiations.IsGeneric(callee))
                {
                    Add(callee);
                }
            }
        }

        return found;
    }

    /// <summary>Where <paramref name="method"/> is among <paramref name="methods"/>; a generic one after them.</summary>
    private static int Order(IReadOnlyList<IlMethod> methods, IlMethod method)
    {
        for (var i = 0; i < methods.Count; i++)
        {
            if (methods[i] == method)
            {
                return i;
            }
        }

        return methods.Count;
    }

    /// <summary>The difference of the library method the instruction at <paramref name="index"/> calls, or null.</summary>
    private static Difference? ForCall(IlMethod method, int index)
    {
        var instruction = method.Body!.Instructions[index];
        if (instruction is not { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } })
        {
            return null;
        }

        // a StringBuilder (a library substitute) appends a float as its ToString writes it, a hole of sb.Append($"...") too
        if (callee.DeclaringType is IlNamedType { Name: "StringBuilder" or "AppendInterpolatedStringHandler" } builder
            && builder.FullName.StartsWith("System.Text.StringBuilder", StringComparison.Ordinal)
            && (callee.Name is "Append" or "Insert" && callee.ParameterTypes.Any(IsFloat)
                || callee.Name == "AppendFormatted" && callee.GenericArguments.Any(IsFloat)))
        {
            return FloatText;
        }

        if (callee.Definition is not null)
        {
            return null;
        }

        if (callee is { Name: "Write" or "WriteLine", HasThis: false, ParameterTypes: [IlPrimitiveType { Kind: var kind }] }
            && callee.DeclaringType is IlNamedType { FullName: "System.Console" }
            && kind is IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64)
        {
            return FloatText;
        }

        if (StringMethods.Find(callee) is not { } called)
        {
            return null;
        }

        return (called.Type, called.Name, called.Parameters) switch
        {
            ("String", "CompareTo", ["String"]) => Compares("string.CompareTo", "Use string.CompareOrdinal " + SameEverywhere),
            ("String", "Compare", ["String", "String"]) => Compares("string.Compare",
                "Use string.CompareOrdinal or StringComparison.Ordinal " + SameEverywhere),
            ("String", "StartsWith" or "EndsWith" or "IndexOf" or "LastIndexOf", ["String", ..] parameters)
                when parameters[^1] != "System.StringComparison" =>
                Compares($"string.{called.Name}", "Pass StringComparison.Ordinal " + SameEverywhere),
            ("String", var name, [.., "System.StringComparison"]) => WithComparison($"string.{name}",
                Comparison(method, index)),
            ("String", "ToUpper" or "ToLower", []) => ChangesCase($"string.{called.Name}", $"{called.Name}Invariant()"),
            ("Char", "ToUpper" or "ToLower", ["Char"]) => ChangesCase($"char.{called.Name}",
                $"char.{called.Name}Invariant"),
            (_, "Parse" or "TryParse", ["String", ..]) => Reads($"{StringMethods.CSharpType(called.Type)}.{called.Name}"),
            _ => null,
        };
    }

    private static Difference Compares(string api, string hint) =>
        new(api, $"{api} compares by the PC's language settings in .NET; {ComparesCodes}", hint);

    /// <summary>
    /// An overload with a StringComparison: no difference for Ordinal and OrdinalIgnoreCase (<paramref name="comparison"/>
    /// is the constant the program passes, null when it is not one).
    /// </summary>
    private static Difference? WithComparison(string api, int? comparison)
    {
        if (comparison is 4 or 5)
        {
            return null;
        }

        var how = comparison switch
        {
            0 or 1 => $"with StringComparison.{Comparisons[comparison.Value]} compares by the PC's language settings",
            2 or 3 => $"with StringComparison.{Comparisons[comparison.Value]} compares by the invariant culture's rules",
            _ => "with a StringComparison that is not Ordinal or OrdinalIgnoreCase compares by a culture's rules",
        };
        var ignoringCase = comparison is 1 or 3 ? ", ignoring case," : "";
        return new Difference(api,
            $"{api} {how} in .NET; ChipSharp compares character codes{ignoringCase} (like .NET's invariant mode)",
            "Pass StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase " + SameEverywhere);
    }

    private static Difference ChangesCase(string api, string invariant) =>
        new(api, $"{api} changes case by the PC's language settings in .NET (with Turkish settings \"i\" becomes " +
            "\"İ\"); ChipSharp changes it as .NET's invariant culture does", $"Use {invariant} {SameEverywhere}");

    private static Difference Reads(string api) =>
        new(api, $"{api} reads the number by the PC's language settings in .NET (its minus and plus signs); ChipSharp " +
            "reads it as .NET's invariant culture does (- and +)", "");

    /// <summary>
    /// The StringComparison the call at <paramref name="index"/> is given, when it is a constant: the instruction before
    /// it pushes it, and no branch comes to the call. Null when it is not a constant.
    /// </summary>
    private static int? Comparison(IlMethod method, int index)
    {
        var instructions = method.Body!.Instructions;
        var call = instructions[index].Offset;
        if (index == 0 || instructions.Any(instruction => instruction.Operand switch
            {
                IlBranchOperand branch => branch.Target == call,
                IlSwitchOperand table => table.Targets.Contains(call),
                _ => false,
            }))
        {
            return null;
        }

        return instructions[index - 1] switch
        {
            { OpCode: IlOpCode.Ldc_I4_0 } => 0,
            { OpCode: IlOpCode.Ldc_I4_1 } => 1,
            { OpCode: IlOpCode.Ldc_I4_2 } => 2,
            { OpCode: IlOpCode.Ldc_I4_3 } => 3,
            { OpCode: IlOpCode.Ldc_I4_4 } => 4,
            { OpCode: IlOpCode.Ldc_I4_5 } => 5,
            { OpCode: IlOpCode.Ldc_I4 or IlOpCode.Ldc_I4_S, Operand: IlInt32Operand constant } => constant.Value,
            _ => null,
        };
    }

    /// <summary>
    /// True when the method may write a float or double as text: it calls a method of float or double, a generic method
    /// of a float or double (<c>AppendFormatted&lt;float&gt;</c>), or boxes one. Only then is its printed text looked at.
    /// </summary>
    private static bool MayWriteFloats(IReadOnlyList<IlInstruction> instructions) =>
        instructions.Any(instruction => instruction.Operand switch
        {
            IlMethodOperand { Method: var callee } => IsFloat(callee.DeclaringType) || callee.GenericArguments.Any(IsFloat),
            IlTypeOperand { Type: var type } => IsFloat(type),
            _ => false,
        });

    private static bool IsFloat(IlType type) => type is IlPrimitiveType { Kind: IlPrimitiveKind.Float32 or IlPrimitiveKind.Float64 }
        || type is IlNamedType { Namespace: "System", Name: "Single" or "Double" };
}
