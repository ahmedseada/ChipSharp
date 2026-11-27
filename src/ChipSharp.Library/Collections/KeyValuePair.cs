using ChipSharp.Library.Collections;

// a namespace of its own: in the collections' code, KeyValuePair is .NET's (which ChipSharp links to this)
namespace ChipSharp.Library.Pairs;

/// <summary>ChipSharp's <see cref="System.Collections.Generic.KeyValuePair{TKey, TValue}"/>: a key and its value, as .NET's.</summary>
[Substitute(typeof(System.Collections.Generic.KeyValuePair<,>))]
public readonly struct KeyValuePair<TKey, TValue>
{
    private readonly TKey key;
    private readonly TValue value;

    public KeyValuePair(TKey key, TValue value)
    {
        this.key = key;
        this.value = value;
    }

    public TKey Key => key;

    public TValue Value => value;

    /// <summary>.NET's: "[key, value]", nothing for a null one.</summary>
    public override string ToString() => "[" + ThrowHelper.Text(key) + ", " + ThrowHelper.Text(value) + "]";

    public void Deconstruct(out TKey key, out TValue value)
    {
        key = Key;
        value = Value;
    }
}

/// <summary>ChipSharp's <see cref="System.Collections.Generic.KeyValuePair"/>: <see cref="Create"/>.</summary>
[Substitute(typeof(System.Collections.Generic.KeyValuePair))]
public static class KeyValuePair
{
    public static KeyValuePair<TKey, TValue> Create<TKey, TValue>(TKey key, TValue value) => new(key, value);
}
