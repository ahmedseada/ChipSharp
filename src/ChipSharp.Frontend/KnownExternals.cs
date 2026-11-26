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
    private const string Math = "System.Math";
    private const string MathF = "System.MathF";

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

        // System.Math and System.MathF on doubles and floats, with .NET's results bit for bit (a square root is
        // rounded to nearest; Min and Max give NaN when either value is NaN, and take -0 as less than +0)
        FloatMath(Math, "Abs", IlType.Float64, 1),
        FloatMath(Math, "Abs", IlType.Float32, 1),
        FloatMath(Math, "Min", IlType.Float64, 2),
        FloatMath(Math, "Min", IlType.Float32, 2),
        FloatMath(Math, "Max", IlType.Float64, 2),
        FloatMath(Math, "Max", IlType.Float32, 2),
        FloatMath(Math, "Sqrt", IlType.Float64, 1),
        FloatMath(MathF, "Abs", IlType.Float32, 1),
        FloatMath(MathF, "Min", IlType.Float32, 2),
        FloatMath(MathF, "Max", IlType.Float32, 2),
        FloatMath(MathF, "Sqrt", IlType.Float32, 1),

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

        // the events an [Interrupt] method handles (see Interrupts): starting and stopping them
        (Timing, "EveryMilliseconds", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.TimerEveryMilliseconds", TirType.Void, [TirType.I32],
                $"{Timing}.EveryMilliseconds(int)")),
        (Timing, "EveryMicroseconds", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.TimerEveryMicroseconds", TirType.Void, [TirType.I32],
                $"{Timing}.EveryMicroseconds(int)")),
        (Timing, "AfterMilliseconds", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.TimerAfterMilliseconds", TirType.Void, [TirType.I32],
                $"{Timing}.AfterMilliseconds(int)")),
        (Timing, "AfterMicroseconds", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.TimerAfterMicroseconds", TirType.Void, [TirType.I32],
                $"{Timing}.AfterMicroseconds(int)")),
        (Timing, "Stop", [], IlType.Void,
            new("ChipSharp.Runtime.TimerStop", TirType.Void, [], $"{Timing}.Stop()")),
        (Pin, "Watch", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.PinWatch", TirType.Void, [TirType.I32], $"{Pin}.Watch(int)")),
        (Pin, "WatchRising", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.PinWatchRising", TirType.Void, [TirType.I32], $"{Pin}.WatchRising(int)")),
        (Pin, "WatchFalling", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.PinWatchFalling", TirType.Void, [TirType.I32], $"{Pin}.WatchFalling(int)")),
        (Pin, "Unwatch", [IlType.Int32], IlType.Void,
            new("ChipSharp.Runtime.PinUnwatch", TirType.Void, [TirType.I32], $"{Pin}.Unwatch(int)")),
    ];

    /// <summary>Every runtime function a library call can become.</summary>
    public static IReadOnlyList<ExternalFunction> All { get; } = [.. Table.Select(entry => entry.Function)];

    /// <summary>
    /// The runtime function a call to <paramref name="method"/> becomes, if the library method is supported; a call to
    /// a runtime function of ChipSharp's library is one too (<see cref="LibrarySubstitutes.TryMapRuntimeFunction"/>).
    /// </summary>
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

        return LibrarySubstitutes.TryMapRuntimeFunction(method, out function);
    }

    /// <summary>
    /// The entry of <paramref name="type"/>.<paramref name="name"/> taking <paramref name="count"/> values of
    /// <paramref name="floatType"/> and returning one: the runtime's &lt;name&gt;Float32 or &lt;name&gt;Float64,
    /// e.g. System.Math.Sqrt(double) becomes ChipSharp.Runtime.SqrtFloat64.
    /// </summary>
    private static (string Type, string Name, IlType[] Parameters, IlType Returns, ExternalFunction Function) FloatMath(
        string type, string name, IlType floatType, int count)
    {
        var (tirType, bits, csharp) = floatType == IlType.Float32
            ? (TirType.F32, 32, "float")
            : (TirType.F64, 64, "double");
        return (type, name, [.. Enumerable.Repeat(floatType, count)], floatType,
            new($"ChipSharp.Runtime.{name}Float{bits}", tirType, [.. Enumerable.Repeat(tirType, count)],
                $"{type}.{name}({string.Join(", ", Enumerable.Repeat(csharp, count))})"));
    }
}
