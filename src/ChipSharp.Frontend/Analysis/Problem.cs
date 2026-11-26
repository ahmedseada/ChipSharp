using System.Globalization;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// One place where the program uses C# that <see cref="SupportedSubset"/> does not have yet: the method, where in the
/// method, and the feature. <see cref="Line"/> is the problem as <c>chipsharp check --details</c> lists it.
/// </summary>
public sealed class Problem
{
    private readonly string _where;

    private Problem(IlMethod method, PlaceKind kind, string where, string place, int? offset, Unsupported unsupported)
    {
        Method = method;
        Kind = kind;
        _where = where;
        Place = place;
        Offset = offset;
        Unsupported = unsupported;
        Line = $"{CSharpNames.Method(method)}{where}: {unsupported.Message}";
    }

    /// <summary>The kinds of place, in the order a summary lists the places of one method.</summary>
    internal enum PlaceKind
    {
        Method,
        Signature,
        Code,
        Local,
    }

    /// <summary>The method the place is in.</summary>
    public IlMethod Method { get; }

    /// <summary>The IL offset of the instruction or try block; null for the method itself, its signature and its locals.</summary>
    public int? Offset { get; }

    /// <summary>
    /// The place as a summary lists it: "IL_0001" (an instruction or a try block), "return type", "parameter 'x'",
    /// "local V_0"; empty for the method itself (an instance method, a generic method, ...).
    /// </summary>
    public string Place { get; }

    /// <summary>What is not supported, as C# programmers know it: "arrays", "the library method System.Math.Abs(int)".</summary>
    public string Feature => Unsupported.What;

    /// <summary>True for a call to a library method ChipSharp lacks; every such method is a feature of its own.</summary>
    public bool IsLibraryCall => Unsupported.IsLibraryMethod;

    /// <summary>
    /// The method, the place and the message, e.g.
    /// "Program.Main() at IL_0002 (add.ovf): checked arithmetic arrives later. For now, leave out checked: ChipSharp's
    /// arithmetic wraps around on overflow, like unchecked C#."
    /// </summary>
    public string Line { get; }

    internal PlaceKind Kind { get; }

    internal Unsupported Unsupported { get; }

    public override string ToString() => Line;

    /// <summary>The same place, with another feature: what the target says about it (see <see cref="TargetRules"/>).</summary>
    internal Problem With(Unsupported unsupported) => new(Method, Kind, _where, Place, Offset, unsupported);

    /// <summary>The method itself: "Counter.Next(int): virtual methods (virtual, override, abstract) arrive with M3 ...".</summary>
    internal static Problem InMethod(IlMethod method, Unsupported unsupported) =>
        new(method, PlaceKind.Method, "", "", null, unsupported);

    /// <summary>The return type or a parameter (<paramref name="what"/>): "Program.Half(int), return type (double): ...".</summary>
    internal static Problem InSignature(IlMethod method, string what, IlType type, Unsupported unsupported) =>
        new(method, PlaceKind.Signature, $", {what} ({CSharpNames.Type(type, method)})", what, null, unsupported);

    /// <summary>A local variable: "Program.Main(), local variable V_0 (float[]): ...".</summary>
    internal static Problem InLocal(IlMethod method, IlLocal local, Unsupported unsupported)
    {
        var name = $"V_{local.Index.ToString(CultureInfo.InvariantCulture)}";
        return new(method, PlaceKind.Local, $", local variable {name} ({CSharpNames.Type(local.Type, method)})",
            $"local {name}", null, unsupported);
    }

    /// <summary>A try block: "Program.Main() at IL_0000: ...".</summary>
    internal static Problem AtTry(IlMethod method, int tryOffset, Unsupported unsupported)
    {
        var label = IlPrinter.Label(tryOffset);
        return new(method, PlaceKind.Code, $" at {label}", label, tryOffset, unsupported);
    }

    /// <summary>An instruction: "Program.Main() at IL_0001 (newarr): ...".</summary>
    internal static Problem At(IlMethod method, IlInstruction instruction, Unsupported unsupported)
    {
        var label = IlPrinter.Label(instruction.Offset);
        return new(method, PlaceKind.Code, $" at {label} ({instruction.Name})", label, instruction.Offset, unsupported);
    }
}
