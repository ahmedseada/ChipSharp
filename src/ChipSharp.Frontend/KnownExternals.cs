using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>A runtime function a library call becomes, e.g. <c>Console.WriteLine(int)</c> becomes <c>ChipSharp.Runtime.WriteLineInt32</c>.</summary>
/// <param name="DisplayName">The TIR external function's name; the code generator turns it into the runtime's symbol.</param>
/// <param name="ReturnType">What it returns.</param>
/// <param name="ParameterTypes">The TIR type of each parameter. A C# bool is passed as an i32 holding 0 or 1.</param>
/// <param name="LibraryMethod">The library method it stands for, as messages show it, e.g. "System.Console.WriteLine(int)".</param>
public sealed record ExternalFunction(
    string DisplayName,
    TirType ReturnType,
    IReadOnlyList<TirType> ParameterTypes,
    string LibraryMethod);

/// <summary>
/// The library methods a program may call in this milestone, and the runtime function each one becomes.
/// A call to any other method outside the program is not supported yet.
/// </summary>
public static class KnownExternals
{
    private static readonly (string Type, string Name, IlType[] Parameters, ExternalFunction Function)[] Table =
    [
        ("System.Console", "WriteLine", [],
            new("ChipSharp.Runtime.WriteLine", TirType.Void, [], "System.Console.WriteLine()")),
        ("System.Console", "WriteLine", [IlType.Int32],
            new("ChipSharp.Runtime.WriteLineInt32", TirType.Void, [TirType.I32], "System.Console.WriteLine(int)")),
        ("System.Console", "WriteLine", [IlType.UInt32],
            new("ChipSharp.Runtime.WriteLineUInt32", TirType.Void, [TirType.I32], "System.Console.WriteLine(uint)")),
        ("System.Console", "WriteLine", [IlType.Int64],
            new("ChipSharp.Runtime.WriteLineInt64", TirType.Void, [TirType.I64], "System.Console.WriteLine(long)")),
        ("System.Console", "WriteLine", [IlType.UInt64],
            new("ChipSharp.Runtime.WriteLineUInt64", TirType.Void, [TirType.I64], "System.Console.WriteLine(ulong)")),
        ("System.Console", "WriteLine", [IlType.Bool],
            new("ChipSharp.Runtime.WriteLineBool", TirType.Void, [TirType.I32], "System.Console.WriteLine(bool)")),
    ];

    /// <summary>Every runtime function a library call can become.</summary>
    public static IReadOnlyList<ExternalFunction> All { get; } = [.. Table.Select(entry => entry.Function)];

    /// <summary>The runtime function a call to <paramref name="method"/> becomes, if the library method is supported.</summary>
    public static bool TryMap(IlMethodReference method, [NotNullWhen(true)] out ExternalFunction? function)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!method.HasThis && method.GenericArguments.Count == 0 && method.ReturnType == IlType.Void
            && method.DeclaringType is IlNamedType type)
        {
            foreach (var entry in Table)
            {
                if (type.FullName == entry.Type && method.Name == entry.Name
                    && method.ParameterTypes.SequenceEqual(entry.Parameters))
                {
                    function = entry.Function;
                    return true;
                }
            }
        }

        function = null;
        return false;
    }
}
