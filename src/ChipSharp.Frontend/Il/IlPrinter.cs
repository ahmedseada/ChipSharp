using System.Globalization;
using System.Text;

namespace ChipSharp.Frontend.Il;

/// <summary>
/// Writes IL as an ildasm-like listing, for <c>chipsharp il</c>, tests and dumps. Lines end with "\n" on every platform.
/// <code>
/// .method static int32 Program::Square(int32 n)
/// {
///   .maxstack 2
///   IL_0000:  ldarg.0
///   IL_0001:  ldarg.0
///   IL_0002:  mul
///   IL_0003:  ret
/// }
/// </code>
/// Branch targets print as labels (<c>IL_000c</c>), methods and fields as ildasm writes them
/// (<c>void [System.Console]System.Console::WriteLine(int32)</c>), strings quoted, locals as V_0, V_1, ...,
/// and arguments by parameter name (<c>this</c> for argument 0 of an instance method).
/// </summary>
public static class IlPrinter
{
    /// <summary>Every method of the assembly, in metadata order, with an empty line between methods.</summary>
    public static string Print(IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Print(assembly.Methods);
    }

    /// <summary>The given methods, with an empty line between them.</summary>
    public static string Print(IEnumerable<IlMethod> methods)
    {
        ArgumentNullException.ThrowIfNull(methods);

        var text = new StringBuilder();
        foreach (var method in methods)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            Write(text, method);
        }

        return text.ToString();
    }

    /// <summary>One method: its header, then its body between braces.</summary>
    public static string Print(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);

        var text = new StringBuilder();
        Write(text, method);
        return text.ToString();
    }

    /// <summary>One instruction without indentation, e.g. "IL_0002:  call       int32 Program::Square(int32)". Arguments print by number.</summary>
    public static string Print(IlInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        return Line(instruction, null);
    }

    /// <summary>The label of an offset, e.g. "IL_000c".</summary>
    public static string Label(int offset) => string.Create(CultureInfo.InvariantCulture, $"IL_{offset:x4}");

    /// <summary>A string in double quotes, with quotes, backslashes and control characters escaped as in C#.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case '\0':
                    text.Append("\\0");
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        text.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        return text.Append('"').ToString();
    }

    private static void Write(StringBuilder text, IlMethod method)
    {
        var generics = method.GenericParameters.Count > 0 ? $"<{string.Join(", ", method.GenericParameters)}>" : "";
        text.Append(".method ")
            .Append(IlMethodSignature.ConventionText(method.Signature.CallingConvention))
            .Append(method.IsStatic ? "static " : "instance ")
            .Append(method.ReturnType).Append(' ')
            .Append(method.DeclaringType.Type).Append("::").Append(method.Name).Append(generics)
            .Append('(').Append(string.Join(", ", method.Parameters)).Append(")\n");
        text.Append("{\n");

        if (method.BodyAsRead is { } body)
        {
            if (method.IsEntryPoint)
            {
                text.Append("  .entrypoint\n");
            }

            text.Append(CultureInfo.InvariantCulture, $"  .maxstack {body.MaxStack}\n");
            if (body.Locals.Count > 0)
            {
                text.Append("  .locals ").Append(body.InitLocals ? "init " : "")
                    .Append('(').Append(string.Join(", ", body.Locals)).Append(")\n");
            }

            foreach (var region in body.ExceptionRegions)
            {
                text.Append("  .try ").Append(Label(region.TryOffset)).Append(" to ").Append(Label(region.TryEnd));
                switch (region.Kind)
                {
                    case IlExceptionRegionKind.Catch:
                        text.Append(" catch ").Append(region.CatchType);
                        break;
                    case IlExceptionRegionKind.Filter:
                        text.Append(" filter ").Append(Label(region.FilterOffset ?? 0));
                        break;
                    case IlExceptionRegionKind.Finally:
                        text.Append(" finally");
                        break;
                    case IlExceptionRegionKind.Fault:
                        text.Append(" fault");
                        break;
                }

                text.Append(" handler ").Append(Label(region.HandlerOffset)).Append(" to ").Append(Label(region.HandlerEnd))
                    .Append('\n');
            }

            foreach (var instruction in body.Instructions)
            {
                text.Append("  ").Append(Line(instruction, method)).Append('\n');
            }
        }

        text.Append("}\n");
    }

    private static string Line(IlInstruction instruction, IlMethod? method)
    {
        var line = $"{Label(instruction.Offset)}:  {instruction.Name}";
        return instruction.Operand is null ? line : $"{line.PadRight(20)} {OperandText(instruction, method)}";
    }

    private static string OperandText(IlInstruction instruction, IlMethod? method) => instruction.Operand switch
    {
        IlArgumentOperand argument when method is not null => ArgumentName(method, argument.Index),
        IlMethodOperand m when instruction.OpCode == IlOpCode.Ldtoken => $"method {m.Method}",
        IlFieldOperand f when instruction.OpCode == IlOpCode.Ldtoken => $"field {f.Field}",
        var operand => operand!.ToString(),
    };

    private static string ArgumentName(IlMethod method, int index)
    {
        var parameter = method.IsStatic ? index : index - 1;
        return !method.IsStatic && index == 0 ? "this"
            : parameter < method.Parameters.Count ? method.Parameters[parameter].Name
            : index.ToString(CultureInfo.InvariantCulture);
    }
}
