using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Text built into a StringBuilder (see <see cref="StringBuilders"/>): the call that appends it (<c>sb.Append($"...")</c>,
/// <c>sb.AppendLine($"...")</c>, <c>sb.AppendFormat(...)</c>) checks the builder for null, then appends each part as a
/// string, a literal as the constant it is and a value as its text (see MethodLowering.KeptText.cs), with the
/// substitute's <c>Append(string)</c>, and ends an AppendLine with its <c>AppendLine()</c>; it gives the builder back,
/// as .NET's does.
/// </summary>
internal sealed partial class MethodLowering
{
    private void AppendTo(PrintSite site, StackValue builder)
    {
        var target = NullCheck(ObjectFromStack(builder, "the StringBuilder the text is appended to"));
        var append = _program.Get(StringBuilders.AppendText).Function;
        foreach (var piece in site.Pieces)
        {
            _ir.Call(append, target, PartText(piece));
        }

        if (site.Line)
        {
            _ir.Call(_program.Get(StringBuilders.AppendLine).Function, target);
        }

        PushObject(target);
    }
}
