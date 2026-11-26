using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>A runtime function a library call becomes, e.g. <c>Console.WriteLine(int)</c> becomes <c>ChipSharp.Runtime.WriteLineInt32</c>.</summary>
/// <param name="DisplayName">The TIR external function's name; the code generator turns it into the runtime's symbol.</param>
/// <param name="ReturnType">What it returns.</param>
/// <param name="ParameterTypes">The TIR type of each parameter. A C# bool is passed (and returned) as an i32 holding 0 or 1.</param>
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
    private const string Pin = "ChipSharp.Hardware.Pin";
    private const string Timing = "ChipSharp.Hardware.Timing";
    private const string Registers = "ChipSharp.Hardware.Registers";

    private static readonly (string Type, string Name, IlType[] Parameters, IlType Returns, ExternalFunction Function)[] Table =
    [
        ("System.Console", "WriteLine", [], IlType.Void,
            new("ChipSharp.Runtime.WriteLine", TirType.Void, [], "System.Console.WriteLine()")),
        ("System.Console", "WriteLine", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.WriteLineInt32", TirType.Void, [TirType.I32], "System.Console.WriteLine(int)")),
        ("System.Console", "WriteLine", [IlType.UInt32], IlType.Void,
            new("ChipSharp.Runtime.WriteLineUInt32", TirType.Void, [TirType.I32], "System.Console.WriteLine(uint)")),
        ("System.Console", "WriteLine", [IlType.Int64], IlType.Void,
            new("ChipSharp.Runtime.WriteLineInt64", TirType.Void, [TirType.I64], "System.Console.WriteLine(long)")),
        ("System.Console", "WriteLine", [IlType.UInt64], IlType.Void,
            new("ChipSharp.Runtime.WriteLineUInt64", TirType.Void, [TirType.I64], "System.Console.WriteLine(ulong)")),
        ("System.Console", "WriteLine", [IlType.Bool], IlType.Void,
            new("ChipSharp.Runtime.WriteLineBool", TirType.Void, [TirType.I32], "System.Console.WriteLine(bool)")),
        ("System.Console", "WriteLine", [IlType.Float32], IlType.Void,
            new("ChipSharp.Runtime.WriteLineFloat32", TirType.Void, [TirType.F32], "System.Console.WriteLine(float)")),
        ("System.Console", "WriteLine", [IlType.Float64], IlType.Void,
            new("ChipSharp.Runtime.WriteLineFloat64", TirType.Void, [TirType.F64], "System.Console.WriteLine(double)")),

        // ChipSharp.Hardware: the chip's pins, timing and registers; only a chip's runtime implements these
        (Pin, "Output", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.PinOutput", TirType.Void, [TirType.I32], $"{Pin}.Output(int)")),
        (Pin, "Input", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.PinInput", TirType.Void, [TirType.I32], $"{Pin}.Input(int)")),
        (Pin, "Write", [IlType.Int32, IlType.Bool], IlType.Void,
            new("ChipSharp.Runtime.PinWrite", TirType.Void, [TirType.I32, TirType.I32], $"{Pin}.Write(int, bool)")),
        (Pin, "Toggle", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.PinToggle", TirType.Void, [TirType.I32], $"{Pin}.Toggle(int)")),
        (Pin, "Read", [IlType.Int32], IlType.Bool,
            new("ChipSharp.Runtime.PinRead", TirType.I32, [TirType.I32], $"{Pin}.Read(int)")),
        (Timing, "DelayMilliseconds", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.DelayMilliseconds", TirType.Void, [TirType.I32],
                $"{Timing}.DelayMilliseconds(int)")),
        (Timing, "DelayMicroseconds", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.DelayMicroseconds", TirType.Void, [TirType.I32],
                $"{Timing}.DelayMicroseconds(int)")),
        (Registers, "Read", [IlType.UInt32], IlType.UInt32,
            new("ChipSharp.Runtime.RegisterRead", TirType.I32, [TirType.I32], $"{Registers}.Read(uint)")),
        (Registers, "Write", [IlType.UInt32, IlType.UInt32], IlType.Void,
            new("ChipSharp.Runtime.RegisterWrite", TirType.Void, [TirType.I32, TirType.I32],
                $"{Registers}.Write(uint, uint)")),
    ];

    /// <summary>Every runtime function a library call can become.</summary>
    public static IReadOnlyList<ExternalFunction> All { get; } = [.. Table.Select(entry => entry.Function)];

    /// <summary>The runtime function a call to <paramref name="method"/> becomes, if the library method is supported.</summary>
    public static bool TryMap(IlMethodReference method, [NotNullWhen(true)] out ExternalFunction? function)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!method.HasThis && method.GenericArguments.Count == 0 && method.DeclaringType is IlNamedType type)
        {
            foreach (var entry in Table)
            {
                if (type.FullName == entry.Type && method.Name == entry.Name && method.ReturnType == entry.Returns
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
