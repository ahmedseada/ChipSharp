using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// Spans in the array escape analysis (see <see cref="Spans"/>; whether a span outlives its array is
/// <see cref="SpanEscapeAnalysis"/>'s): the field token of <c>RuntimeHelpers.CreateSpan</c> is used where it may be, and
/// a ReadOnlySpan of an array writes none of its elements, so a static array viewed only that way stays a constant
/// table.
/// </summary>
internal static partial class ArrayEscapeAnalysis
{
    private sealed partial class Flow
    {
        /// <summary>True for a call of <c>RuntimeHelpers.CreateSpan&lt;T&gt;(field)</c>, which takes a field token.</summary>
        private static bool IsDataSpan(IlMethodReference callee) => Spans.Call(callee)?.Operation == SpanOperation.Data;

        /// <summary>RuntimeHelpers.CreateSpan: the token it takes is used where it may be.</summary>
        private void DataSpan(Value token)
        {
            if (_record && token.Kind == Kind.Token)
            {
                _good.UnionWith(token.Set);
            }
        }

        /// <summary>True for a call that makes a ReadOnlySpan of an array: nothing writes the array's elements through it.</summary>
        private static bool IsReadOnlyView(IlMethodReference callee) =>
            Spans.Call(callee) is { Operation: SpanOperation.FromArray or SpanOperation.FromArrayRange }
            && Spans.IsSpan(callee.DeclaringType, out _, out var isReadOnly) && isReadOnly;
    }
}
