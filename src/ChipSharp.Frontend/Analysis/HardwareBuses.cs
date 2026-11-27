using System.Globalization;
using System.Text.Json.Nodes;
using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// ChipSharp.Hardware's buses, I2c and Spi, and Lcd, a display on an I2C bus, on a target (see
/// <see cref="TargetRules"/>): a program that uses one needs a target with the bus, the capability "i2c" or "spi"; and
/// the constant arguments of its Open must be ones the target has,
/// as its "i2c" and "spi" settings say: the buses there are, the pins of each (<c>"buses": [{ "sda": [0, 4, ...],
/// ... }, ...]</c>) and the lowest and highest clock (<c>"frequency": [2000, 1000000]</c>). An argument known only when
/// the program runs is the chip's runtime's to check, and it stops the program on a mistake.
/// </summary>
/// <remarks>
/// An argument is a constant when the instructions right before the call push it with ldc.i4, with no branch into
/// them: the last arguments of <c>I2c.Open(0, sda: 4, scl: 5, frequency: 100_000)</c> as C# compiles it, all of them
/// there.
/// </remarks>
internal static class HardwareBuses
{
    private static readonly Bus[] Buses =
    [
        new("I2c", "I2C", "i2c", ["bus", "sda", "scl", "frequency"], ["sda", "scl"], []),
        new("Spi", "SPI", "spi", ["bus", "sck", "tx", "rx", "frequency", "mode"], ["sck", "tx", "rx"], ["tx", "rx"]),

        // a display on an I2C bus (ChipSharp.Library's Lcd writes to it with I2c.Write): its Open's bus
        new("Lcd", "I2C", "i2c", ["bus", "address", "columns", "rows"], [], []),
    ];

    /// <summary>
    /// Adds a problem for each place in <paramref name="methods"/> (the program's, not the library's) that uses a bus
    /// <paramref name="target"/> does not have, and for each constant argument of an Open that the target's bus does
    /// not have. True when there is one.
    /// </summary>
    public static bool Apply(TargetDescription target, IReadOnlyList<IlMethod> methods, List<Problem> problems)
    {
        ArgumentNullException.ThrowIfNull(target);
        var found = false;
        foreach (var method in methods.Where(method => !LibrarySubstitutes.Defines(method)))
        {
            var instructions = method.Body?.Instructions ?? [];
            HashSet<int>? targets = null;
            for (var i = 0; i < instructions.Count; i++)
            {
                if (instructions[i] is not
                    {
                        OpCode: IlOpCode.Call,
                        Operand: IlMethodOperand
                        {
                            Method: { DeclaringType: IlNamedType { Namespace: "ChipSharp.Hardware", DeclaringType: null } type } callee,
                        },
                    }
                    || Buses.FirstOrDefault(bus => bus.Type == type.Name) is not { } bus)
                {
                    continue;
                }

                if (!target.HasCapability(bus.Capability))
                {
                    problems.Add(Problem.At(method, instructions[i],
                        Unsupported.NoBus(bus.Type, bus.Name, bus.Capability, target.Name)));
                    found = true;
                }
                else if (callee.Name == "Open" && callee.ParameterTypes.Count == bus.Parameters.Length)
                {
                    targets ??= BranchTargets(instructions);
                    var arguments = Constants(instructions, i, bus.Parameters.Length, targets);
                    foreach (var unsupported in Check(bus, target, arguments))
                    {
                        problems.Add(Problem.At(method, instructions[i], unsupported));
                        found = true;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// What is wrong with the constant arguments of a call of <paramref name="bus"/>'s Open (null where an argument is
    /// not a constant) for <paramref name="target"/>; nothing when its settings give no bus table.
    /// </summary>
    private static IEnumerable<Unsupported> Check(Bus bus, TargetDescription target, int?[] arguments)
    {
        if (target.Settings[bus.Capability] is not JsonObject settings)
        {
            yield break;
        }

        var open = $"{bus.Type}.Open";
        var where = $"on target '{target.Name}'";
        var buses = settings["buses"] as JsonArray ?? [];
        if (arguments[0] is { } number && (number < 0 || number >= buses.Count))
        {
            yield return Unsupported.BusArgument($"{bus.Name} bus {Text(number)} ({open}'s bus) does not exist {where}",
                $"Its {bus.Name} buses are {Numbers([.. Enumerable.Range(0, buses.Count)], "and")}.");
        }
        else if (arguments[0] is { } index && buses[index] is JsonObject pins)
        {
            foreach (var name in bus.Pins)
            {
                var role = name.ToUpperInvariant();
                var allowed = Integers(pins[name]);
                var optional = bus.Optional.Contains(name);
                if (arguments[Array.IndexOf(bus.Parameters, name)] is { } pin && allowed.Count > 0 && !allowed.Contains(pin)
                    && !(optional && pin == -1))
                {
                    yield return Unsupported.BusArgument(
                        $"GPIO {Text(pin)} is not {(role == "TX" ? "a" : "an")} {role} pin of {bus.Name} bus {Text(index)} ({open}'s " +
                        $"{name}) {where}",
                        $"Its {role} pins are {Pins(allowed)}{(optional ? " (-1 for none)" : "")}.");
                }
            }
        }

        var frequency = Array.IndexOf(bus.Parameters, "frequency");
        if (frequency >= 0 && arguments[frequency] is { } hertz && Integers(settings["frequency"]) is [var lowest, var highest]
            && (hertz < lowest || hertz > highest))
        {
            yield return Unsupported.BusArgument(
                $"an {bus.Name} clock of {Text(hertz)} Hz ({open}'s frequency) is not one target '{target.Name}' makes",
                $"It makes {Text(lowest)} to {Text(highest)} Hz.");
        }

        var mode = Array.IndexOf(bus.Parameters, "mode");
        if (mode >= 0 && arguments[mode] is { } spiMode && spiMode is < 0 or > 3)
        {
            yield return Unsupported.BusArgument($"SPI mode {Text(spiMode)} ({open}'s mode) does not exist",
                "The mode is 0, 1, 2 or 3; mode 0 suits most devices.");
        }

        // Lcd.Open's size: what an HD44780's 80 characters of memory show (ChipSharp.Library's Lcd.Fits)
        var columns = Array.IndexOf(bus.Parameters, "columns");
        if (columns >= 0 && arguments[columns] is { } across && arguments[columns + 1] is { } down
            && !(across >= 1 && down >= 1 && down <= 4 && across <= (down > 2 ? 20 : 40)))
        {
            yield return Unsupported.BusArgument(
                $"an LCD of {Text(across)} columns and {Text(down)} rows ({open}'s columns and rows) is not one an " +
                "HD44780 shows",
                "It shows 1 to 4 rows of 1 to 40 columns, 20 on more than two rows: e.g. 16 columns and 2 rows, or 20 and 4.");
        }
    }

    /// <summary>
    /// The arguments of the call at <paramref name="call"/> that the instructions right before it push as constants,
    /// with no branch into them, as many as it takes; null for the others.
    /// </summary>
    private static int?[] Constants(IReadOnlyList<IlInstruction> instructions, int call, int count, HashSet<int> targets)
    {
        var arguments = new int?[count];
        for (int argument = count - 1, at = call - 1; argument >= 0 && at >= 0; argument--, at--)
        {
            // a branch to the instruction after this one would bring another value for the arguments after it
            if (Spans.Integer(instructions[at]) is not { } value || targets.Contains(instructions[at + 1].Offset))
            {
                break;
            }

            arguments[argument] = value;
        }

        return arguments;
    }

    /// <summary>Where the method's branches and switches go.</summary>
    private static HashSet<int> BranchTargets(IReadOnlyList<IlInstruction> instructions) =>
    [
        .. instructions.SelectMany(instruction => instruction.Operand switch
        {
            IlBranchOperand branch => [branch.Target],
            IlSwitchOperand table => table.Targets,
            _ => Array.Empty<int>(),
        }),
    ];

    private static List<int> Integers(JsonNode? node) =>
        node is JsonArray array
            ? [.. array.Select(item => item is JsonValue value && value.TryGetValue(out int number) ? number : int.MinValue)]
            : [];

    /// <summary>"GPIO 0, 4, 8 and 12".</summary>
    private static string Pins(List<int> pins) => "GPIO " + Numbers(pins, "and");

    /// <summary>"0, 4, 8 and 12" (with "or" or "and").</summary>
    private static string Numbers(List<int> numbers, string last) => numbers.Count switch
    {
        0 => "none",
        1 => Text(numbers[0]),
        _ => $"{string.Join(", ", numbers.Take(numbers.Count - 1).Select(Text))} {last} {Text(numbers[^1])}",
    };

    private static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// One of ChipSharp.Hardware's buses (or Lcd, a device on one, which has no pins of its own): its class, its name in messages, the capability (and the target setting) that
    /// gives it, the parameters of its Open, those of them that are pins (as the target's bus table names them), and the
    /// pins that may be -1 for none (Spi's tx and rx).
    /// </summary>
    private sealed record Bus(string Type, string Name, string Capability, string[] Parameters, string[] Pins,
        string[] Optional);
}
