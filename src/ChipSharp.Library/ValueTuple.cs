// a namespace of its own: in the rest of the library, a tuple is .NET's (which ChipSharp links to these)
namespace ChipSharp.Library.Tuples;

/// <summary>
/// ChipSharp's <see cref="System.ValueTuple"/>: what C# makes of <c>(1, "a")</c>, a struct of its elements in public
/// fields <c>Item1</c> to <c>Item7</c> and, past seven, a tuple of the rest in <c>Rest</c>, laid out as .NET's, so a tuple
/// needs no heap: C# reads and writes the fields itself (named elements, deconstruction, <c>==</c> element by element).
/// The members are .NET's (System.Private.CoreLib's ValueTuple.cs): <c>Equals</c> compares the elements as
/// <c>EqualityComparer&lt;T&gt;.Default</c> does and <c>CompareTo</c> as <c>Comparer&lt;T&gt;.Default</c> does (with no
/// comparer object, see <see cref="Equality"/>), <c>ToString</c> writes <c>(1, a)</c> (nothing for null), which needs a
/// heap, as text kept does.
/// </summary>
/// <remarks>
/// .NET's <c>GetHashCode</c> combines the elements' hash codes with <c>HashCode.Combine</c>, which mixes in a number that
/// changes from one run to the next; ChipSharp's combines them the same way from a seed of 0 (<see cref="TupleHash"/>),
/// so equal tuples have equal hash codes, the same in every run and on every target. A tuple of one element has its
/// element's, as in .NET. <c>ITuple</c>, <c>IStructuralEquatable</c>, <c>IStructuralComparable</c> and the
/// non-generic <c>IComparable</c> are not here yet.
/// </remarks>
[Substitute(typeof(System.ValueTuple))]
public readonly struct ValueTuple
{
    public static ValueTuple<T1> Create<T1>(T1 item1) => new(item1);

    public static ValueTuple<T1, T2> Create<T1, T2>(T1 item1, T2 item2) => new(item1, item2);

    public static ValueTuple<T1, T2, T3> Create<T1, T2, T3>(T1 item1, T2 item2, T3 item3) => new(item1, item2, item3);

    public static ValueTuple<T1, T2, T3, T4> Create<T1, T2, T3, T4>(T1 item1, T2 item2, T3 item3, T4 item4) =>
        new(item1, item2, item3, item4);

    public static ValueTuple<T1, T2, T3, T4, T5> Create<T1, T2, T3, T4, T5>(T1 item1, T2 item2, T3 item3, T4 item4,
        T5 item5) => new(item1, item2, item3, item4, item5);

    public static ValueTuple<T1, T2, T3, T4, T5, T6> Create<T1, T2, T3, T4, T5, T6>(T1 item1, T2 item2, T3 item3,
        T4 item4, T5 item5, T6 item6) => new(item1, item2, item3, item4, item5, item6);

    public static ValueTuple<T1, T2, T3, T4, T5, T6, T7> Create<T1, T2, T3, T4, T5, T6, T7>(T1 item1, T2 item2,
        T3 item3, T4 item4, T5 item5, T6 item6, T7 item7) => new(item1, item2, item3, item4, item5, item6, item7);

    public static ValueTuple<T1, T2, T3, T4, T5, T6, T7, ValueTuple<T8>> Create<T1, T2, T3, T4, T5, T6, T7, T8>(T1 item1,
        T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7, T8 item8) =>
        new(item1, item2, item3, item4, item5, item6, item7, new ValueTuple<T8>(item8));
}

/// <summary>What a tuple is to the tuple that holds it as its <c>Rest</c>: .NET's IValueTupleInternal, for its text.</summary>
internal interface ITupleRest
{
}

/// <summary>ChipSharp's <see cref="System.ValueTuple{T1}"/> (see <see cref="ValueTuple"/>).</summary>
[Substitute(typeof(System.ValueTuple<>))]
public struct ValueTuple<T1> : IEquatable<ValueTuple<T1>>, IComparable<ValueTuple<T1>>, ITupleRest
{
    public T1 Item1;

    public ValueTuple(T1 item1) => Item1 = item1;

    public override bool Equals(object? obj) => obj is ValueTuple<T1> tuple && Equals(tuple);

    public bool Equals(ValueTuple<T1> other) => Equality.AreEqual(Item1, other.Item1);

    public int CompareTo(ValueTuple<T1> other) => Equality.Compare(Item1, other.Item1);

    public override int GetHashCode() => Equality.HashOf(Item1);

    public override string ToString() => "(" + TupleText.Of(Item1) + ")";
}

/// <summary>ChipSharp's <see cref="System.ValueTuple{T1, T2}"/> (see <see cref="ValueTuple"/>).</summary>
[Substitute(typeof(System.ValueTuple<,>))]
public struct ValueTuple<T1, T2> : IEquatable<ValueTuple<T1, T2>>, IComparable<ValueTuple<T1, T2>>, ITupleRest
{
    public T1 Item1;
    public T2 Item2;

    public ValueTuple(T1 item1, T2 item2)
    {
        Item1 = item1;
        Item2 = item2;
    }

    public override bool Equals(object? obj) => obj is ValueTuple<T1, T2> tuple && Equals(tuple);

    public bool Equals(ValueTuple<T1, T2> other) =>
        Equality.AreEqual(Item1, other.Item1) && Equality.AreEqual(Item2, other.Item2);

    public int CompareTo(ValueTuple<T1, T2> other)
    {
        var c = Equality.Compare(Item1, other.Item1);
        return c != 0 ? c : Equality.Compare(Item2, other.Item2);
    }

    public override int GetHashCode() => TupleHash.Combine(Equality.HashOf(Item1), Equality.HashOf(Item2));

    public override string ToString() => "(" + TupleText.Of(Item1) + ", " + TupleText.Of(Item2) + ")";
}

/// <summary>ChipSharp's <see cref="System.ValueTuple{T1, T2, T3}"/> (see <see cref="ValueTuple"/>).</summary>
[Substitute(typeof(System.ValueTuple<,,>))]
public struct ValueTuple<T1, T2, T3> : IEquatable<ValueTuple<T1, T2, T3>>, IComparable<ValueTuple<T1, T2, T3>>, ITupleRest
{
    public T1 Item1;
    public T2 Item2;
    public T3 Item3;

    public ValueTuple(T1 item1, T2 item2, T3 item3)
    {
        Item1 = item1;
        Item2 = item2;
        Item3 = item3;
    }

    public override bool Equals(object? obj) => obj is ValueTuple<T1, T2, T3> tuple && Equals(tuple);

    public bool Equals(ValueTuple<T1, T2, T3> other) =>
        Equality.AreEqual(Item1, other.Item1) && Equality.AreEqual(Item2, other.Item2)
        && Equality.AreEqual(Item3, other.Item3);

    public int CompareTo(ValueTuple<T1, T2, T3> other)
    {
        var c = Equality.Compare(Item1, other.Item1);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item2, other.Item2);
        return c != 0 ? c : Equality.Compare(Item3, other.Item3);
    }

    public override int GetHashCode() =>
        TupleHash.Combine(Equality.HashOf(Item1), Equality.HashOf(Item2), Equality.HashOf(Item3));

    public override string ToString() =>
        "(" + TupleText.Of(Item1) + ", " + TupleText.Of(Item2) + ", " + TupleText.Of(Item3) + ")";
}

/// <summary>ChipSharp's <see cref="System.ValueTuple{T1, T2, T3, T4}"/> (see <see cref="ValueTuple"/>).</summary>
[Substitute(typeof(System.ValueTuple<,,,>))]
public struct ValueTuple<T1, T2, T3, T4>
    : IEquatable<ValueTuple<T1, T2, T3, T4>>, IComparable<ValueTuple<T1, T2, T3, T4>>, ITupleRest
{
    public T1 Item1;
    public T2 Item2;
    public T3 Item3;
    public T4 Item4;

    public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4)
    {
        Item1 = item1;
        Item2 = item2;
        Item3 = item3;
        Item4 = item4;
    }

    public override bool Equals(object? obj) => obj is ValueTuple<T1, T2, T3, T4> tuple && Equals(tuple);

    public bool Equals(ValueTuple<T1, T2, T3, T4> other) =>
        Equality.AreEqual(Item1, other.Item1) && Equality.AreEqual(Item2, other.Item2)
        && Equality.AreEqual(Item3, other.Item3) && Equality.AreEqual(Item4, other.Item4);

    public int CompareTo(ValueTuple<T1, T2, T3, T4> other)
    {
        var c = Equality.Compare(Item1, other.Item1);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item2, other.Item2);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item3, other.Item3);
        return c != 0 ? c : Equality.Compare(Item4, other.Item4);
    }

    public override int GetHashCode() => TupleHash.Combine(Equality.HashOf(Item1), Equality.HashOf(Item2),
        Equality.HashOf(Item3), Equality.HashOf(Item4));

    public override string ToString() => "(" + TupleText.Of(Item1) + ", " + TupleText.Of(Item2) + ", "
        + TupleText.Of(Item3) + ", " + TupleText.Of(Item4) + ")";
}

/// <summary>ChipSharp's <see cref="System.ValueTuple{T1, T2, T3, T4, T5}"/> (see <see cref="ValueTuple"/>).</summary>
[Substitute(typeof(System.ValueTuple<,,,,>))]
public struct ValueTuple<T1, T2, T3, T4, T5>
    : IEquatable<ValueTuple<T1, T2, T3, T4, T5>>, IComparable<ValueTuple<T1, T2, T3, T4, T5>>, ITupleRest
{
    public T1 Item1;
    public T2 Item2;
    public T3 Item3;
    public T4 Item4;
    public T5 Item5;

    public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5)
    {
        Item1 = item1;
        Item2 = item2;
        Item3 = item3;
        Item4 = item4;
        Item5 = item5;
    }

    public override bool Equals(object? obj) => obj is ValueTuple<T1, T2, T3, T4, T5> tuple && Equals(tuple);

    public bool Equals(ValueTuple<T1, T2, T3, T4, T5> other) =>
        Equality.AreEqual(Item1, other.Item1) && Equality.AreEqual(Item2, other.Item2)
        && Equality.AreEqual(Item3, other.Item3) && Equality.AreEqual(Item4, other.Item4)
        && Equality.AreEqual(Item5, other.Item5);

    public int CompareTo(ValueTuple<T1, T2, T3, T4, T5> other)
    {
        var c = Equality.Compare(Item1, other.Item1);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item2, other.Item2);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item3, other.Item3);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item4, other.Item4);
        return c != 0 ? c : Equality.Compare(Item5, other.Item5);
    }

    public override int GetHashCode() => TupleHash.Combine(Equality.HashOf(Item1), Equality.HashOf(Item2),
        Equality.HashOf(Item3), Equality.HashOf(Item4), Equality.HashOf(Item5));

    public override string ToString() => "(" + TupleText.Of(Item1) + ", " + TupleText.Of(Item2) + ", "
        + TupleText.Of(Item3) + ", " + TupleText.Of(Item4) + ", " + TupleText.Of(Item5) + ")";
}

/// <summary>ChipSharp's <see cref="System.ValueTuple{T1, T2, T3, T4, T5, T6}"/> (see <see cref="ValueTuple"/>).</summary>
[Substitute(typeof(System.ValueTuple<,,,,,>))]
public struct ValueTuple<T1, T2, T3, T4, T5, T6>
    : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6>>, IComparable<ValueTuple<T1, T2, T3, T4, T5, T6>>, ITupleRest
{
    public T1 Item1;
    public T2 Item2;
    public T3 Item3;
    public T4 Item4;
    public T5 Item5;
    public T6 Item6;

    public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6)
    {
        Item1 = item1;
        Item2 = item2;
        Item3 = item3;
        Item4 = item4;
        Item5 = item5;
        Item6 = item6;
    }

    public override bool Equals(object? obj) => obj is ValueTuple<T1, T2, T3, T4, T5, T6> tuple && Equals(tuple);

    public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6> other) =>
        Equality.AreEqual(Item1, other.Item1) && Equality.AreEqual(Item2, other.Item2)
        && Equality.AreEqual(Item3, other.Item3) && Equality.AreEqual(Item4, other.Item4)
        && Equality.AreEqual(Item5, other.Item5) && Equality.AreEqual(Item6, other.Item6);

    public int CompareTo(ValueTuple<T1, T2, T3, T4, T5, T6> other)
    {
        var c = Equality.Compare(Item1, other.Item1);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item2, other.Item2);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item3, other.Item3);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item4, other.Item4);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item5, other.Item5);
        return c != 0 ? c : Equality.Compare(Item6, other.Item6);
    }

    public override int GetHashCode() => TupleHash.Combine(Equality.HashOf(Item1), Equality.HashOf(Item2),
        Equality.HashOf(Item3), Equality.HashOf(Item4), Equality.HashOf(Item5), Equality.HashOf(Item6));

    public override string ToString() => "(" + TupleText.Of(Item1) + ", " + TupleText.Of(Item2) + ", "
        + TupleText.Of(Item3) + ", " + TupleText.Of(Item4) + ", " + TupleText.Of(Item5) + ", " + TupleText.Of(Item6)
        + ")";
}

/// <summary>ChipSharp's <see cref="System.ValueTuple{T1, T2, T3, T4, T5, T6, T7}"/> (see <see cref="ValueTuple"/>).</summary>
[Substitute(typeof(System.ValueTuple<,,,,,,>))]
public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7>
    : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>, IComparable<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>, ITupleRest
{
    public T1 Item1;
    public T2 Item2;
    public T3 Item3;
    public T4 Item4;
    public T5 Item5;
    public T6 Item6;
    public T7 Item7;

    public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7)
    {
        Item1 = item1;
        Item2 = item2;
        Item3 = item3;
        Item4 = item4;
        Item5 = item5;
        Item6 = item6;
        Item7 = item7;
    }

    public override bool Equals(object? obj) => obj is ValueTuple<T1, T2, T3, T4, T5, T6, T7> tuple && Equals(tuple);

    public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6, T7> other) =>
        Equality.AreEqual(Item1, other.Item1) && Equality.AreEqual(Item2, other.Item2)
        && Equality.AreEqual(Item3, other.Item3) && Equality.AreEqual(Item4, other.Item4)
        && Equality.AreEqual(Item5, other.Item5) && Equality.AreEqual(Item6, other.Item6)
        && Equality.AreEqual(Item7, other.Item7);

    public int CompareTo(ValueTuple<T1, T2, T3, T4, T5, T6, T7> other)
    {
        var c = Equality.Compare(Item1, other.Item1);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item2, other.Item2);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item3, other.Item3);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item4, other.Item4);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item5, other.Item5);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item6, other.Item6);
        return c != 0 ? c : Equality.Compare(Item7, other.Item7);
    }

    public override int GetHashCode() => TupleHash.Combine(Equality.HashOf(Item1), Equality.HashOf(Item2),
        Equality.HashOf(Item3), Equality.HashOf(Item4), Equality.HashOf(Item5), Equality.HashOf(Item6),
        Equality.HashOf(Item7));

    public override string ToString() => "(" + TupleText.Of(Item1) + ", " + TupleText.Of(Item2) + ", "
        + TupleText.Of(Item3) + ", " + TupleText.Of(Item4) + ", " + TupleText.Of(Item5) + ", " + TupleText.Of(Item6)
        + ", " + TupleText.Of(Item7) + ")";
}

/// <summary>
/// ChipSharp's <see cref="System.ValueTuple{T1, T2, T3, T4, T5, T6, T7, TRest}"/>: seven elements and a tuple of the
/// rest, what C# makes of a tuple of eight or more (see <see cref="ValueTuple"/>). Its text goes on with the rest's
/// elements, as .NET's: <c>(1, 2, 3, 4, 5, 6, 7, 8)</c>.
/// </summary>
[Substitute(typeof(System.ValueTuple<,,,,,,,>))]
public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>
    : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>>,
        IComparable<ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>>, ITupleRest
    where TRest : struct
{
    public T1 Item1;
    public T2 Item2;
    public T3 Item3;
    public T4 Item4;
    public T5 Item5;
    public T6 Item6;
    public T7 Item7;
    public TRest Rest;

    public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7, TRest rest)
    {
        Item1 = item1;
        Item2 = item2;
        Item3 = item3;
        Item4 = item4;
        Item5 = item5;
        Item6 = item6;
        Item7 = item7;
        Rest = rest;
    }

    public override bool Equals(object? obj) =>
        obj is ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> tuple && Equals(tuple);

    public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> other) =>
        Equality.AreEqual(Item1, other.Item1) && Equality.AreEqual(Item2, other.Item2)
        && Equality.AreEqual(Item3, other.Item3) && Equality.AreEqual(Item4, other.Item4)
        && Equality.AreEqual(Item5, other.Item5) && Equality.AreEqual(Item6, other.Item6)
        && Equality.AreEqual(Item7, other.Item7) && Equality.AreEqual(Rest, other.Rest);

    public int CompareTo(ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> other)
    {
        var c = Equality.Compare(Item1, other.Item1);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item2, other.Item2);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item3, other.Item3);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item4, other.Item4);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item5, other.Item5);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item6, other.Item6);
        if (c != 0)
        {
            return c;
        }

        c = Equality.Compare(Item7, other.Item7);
        return c != 0 ? c : Equality.Compare(Rest, other.Rest);
    }

    public override int GetHashCode() => TupleHash.Combine(Equality.HashOf(Item1), Equality.HashOf(Item2),
        Equality.HashOf(Item3), Equality.HashOf(Item4), Equality.HashOf(Item5), Equality.HashOf(Item6),
        Equality.HashOf(Item7), Equality.HashOf(Rest));

    /// <summary>The seven elements, then the rest's: its text without its "(" (another struct's text, and ")").</summary>
    public override string ToString()
    {
        var start = "(" + TupleText.Of(Item1) + ", " + TupleText.Of(Item2) + ", " + TupleText.Of(Item3) + ", "
            + TupleText.Of(Item4) + ", " + TupleText.Of(Item5) + ", " + TupleText.Of(Item6) + ", " + TupleText.Of(Item7)
            + ", ";
        var rest = TupleText.Of(Rest);
        return Rest is ITupleRest ? start + rest.Substring(1) : start + rest + ")";
    }
}

/// <summary>An element of a tuple in the tuple's text: <c>Item1?.ToString()</c>, nothing for null.</summary>
internal static class TupleText
{
    public static string Of<T>(T value) => value == null ? "" : value.ToString() ?? "";
}

/// <summary>
/// .NET's HashCode.Combine (xxHash32 of the hash codes, System.Private.CoreLib's HashCode.cs) from a seed of 0, where
/// .NET's seed is a number of the process: a tuple's hash code, the same in every run.
/// </summary>
internal static class TupleHash
{
    private const uint Prime1 = 2654435761U;
    private const uint Prime2 = 2246822519U;
    private const uint Prime3 = 3266489917U;
    private const uint Prime4 = 668265263U;
    private const uint Prime5 = 374761393U;

    // the state .NET's Initialize starts from, from a seed of 0
    private const uint Start1 = unchecked(Prime1 + Prime2);
    private const uint Start2 = Prime2;
    private const uint Start3 = 0;
    private const uint Start4 = unchecked(0 - Prime1);

    public static int Combine(int h1, int h2) =>
        (int)MixFinal(QueueRound(QueueRound(unchecked(Prime5 + 8), (uint)h1), (uint)h2));

    public static int Combine(int h1, int h2, int h3) =>
        (int)MixFinal(QueueRound(QueueRound(QueueRound(unchecked(Prime5 + 12), (uint)h1), (uint)h2), (uint)h3));

    public static int Combine(int h1, int h2, int h3, int h4) =>
        (int)MixFinal(State(h1, h2, h3, h4) + 16);

    public static int Combine(int h1, int h2, int h3, int h4, int h5) =>
        (int)MixFinal(QueueRound(State(h1, h2, h3, h4) + 20, (uint)h5));

    public static int Combine(int h1, int h2, int h3, int h4, int h5, int h6) =>
        (int)MixFinal(QueueRound(QueueRound(State(h1, h2, h3, h4) + 24, (uint)h5), (uint)h6));

    public static int Combine(int h1, int h2, int h3, int h4, int h5, int h6, int h7) =>
        (int)MixFinal(QueueRound(QueueRound(QueueRound(State(h1, h2, h3, h4) + 28, (uint)h5), (uint)h6), (uint)h7));

    public static int Combine(int h1, int h2, int h3, int h4, int h5, int h6, int h7, int h8)
    {
        var v1 = Round(Round(Start1, (uint)h1), (uint)h5);
        var v2 = Round(Round(Start2, (uint)h2), (uint)h6);
        var v3 = Round(Round(Start3, (uint)h3), (uint)h7);
        var v4 = Round(Round(Start4, (uint)h4), (uint)h8);
        return (int)MixFinal(Mix(v1, v2, v3, v4) + 32);
    }

    /// <summary>The state after four hash codes, mixed.</summary>
    private static uint State(int h1, int h2, int h3, int h4) =>
        Mix(Round(Start1, (uint)h1), Round(Start2, (uint)h2), Round(Start3, (uint)h3), Round(Start4, (uint)h4));

    private static uint Mix(uint v1, uint v2, uint v3, uint v4) =>
        RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);

    private static uint Round(uint hash, uint input) => RotateLeft(hash + input * Prime2, 13) * Prime1;

    private static uint QueueRound(uint hash, uint queued) => RotateLeft(hash + queued * Prime3, 17) * Prime4;

    private static uint MixFinal(uint hash)
    {
        hash ^= hash >> 15;
        hash *= Prime2;
        hash ^= hash >> 13;
        hash *= Prime3;
        hash ^= hash >> 16;
        return hash;
    }

    private static uint RotateLeft(uint value, int offset) => (value << offset) | (value >> (32 - offset));
}
