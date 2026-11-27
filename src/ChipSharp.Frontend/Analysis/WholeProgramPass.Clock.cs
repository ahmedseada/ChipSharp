using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The target's clock in the whole-program pass: DateTime.Now, UtcNow and Today, Stopwatch, Environment.TickCount and
/// ChipSharp.Hardware's Clock.Start read or set a clock, the runtime's Clock functions (ChipSharp.Library's
/// Runtime.Clock.cs), which only a target whose file says <c>"clock": true</c> has: a timer that counts microseconds (the
/// RP2040's, a PC's performance counter).
/// </summary>
public static partial class WholeProgramPass
{
    private const string ClockFunctions = "ChipSharp.Runtime.Clock";

    /// <summary>
    /// On a target without a clock, a problem for each place in the program's own <paramref name="methods"/> that
    /// calls something that reads or sets it, itself or through the library methods it calls. True when there is one.
    /// </summary>
    private static bool ApplyClock(TargetDescription? target, IReadOnlyList<IlMethod> methods, List<Problem> problems)
    {
        if (target is null || target.HasCapability("clock"))
        {
            return false;
        }

        var found = false;
        foreach (var method in methods.Where(method => !LibrarySubstitutes.Defines(method)))
        {
            foreach (var instruction in method.Body?.Instructions ?? [])
            {
                if (ClockUser(instruction) is { } user)
                {
                    problems.Add(Problem.At(method, instruction, Unsupported.NoClock(user, target.Name)));
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The library member <paramref name="instruction"/> uses that reads or sets the clock, as C# names it (e.g.
    /// "System.DateTime.Now"): a call that runs a Clock function, itself or through what it calls, or a static field of
    /// Stopwatch (its Frequency is the clock's). Null for any other instruction.
    /// </summary>
    private static string? ClockUser(IlInstruction instruction) => instruction.Operand switch
    {
        IlMethodOperand { Method: var callee } when instruction.OpCode is IlOpCode.Call or IlOpCode.Callvirt or IlOpCode.Newobj
            && RuntimeCalls(instruction).Any(function => function.DisplayName.StartsWith(ClockFunctions, StringComparison.Ordinal)) =>
            CSharpNames.Method(callee),
        IlFieldOperand { Field: { Definition: { } field } reference } when instruction.OpCode is IlOpCode.Ldsfld or IlOpCode.Ldsflda
            && LibrarySubstitutes.Substituted.TryGetValue(field.DeclaringType, out var type)
            && type == "System.Diagnostics.Stopwatch" => $"{type}.{reference.Name}",
        _ => null,
    };
}
