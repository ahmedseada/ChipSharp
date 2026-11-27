namespace ChipSharp.Library;

/// <summary>
/// What generic code over a value gets where it makes a T without a constructor to run: in an instance of generic code,
/// C#'s <c>new T()</c> (<c>Activator.CreateInstance&lt;T&gt;()</c>) on a class, or a struct with a constructor without
/// parameters, runs that constructor; on any other value it is <see cref="Of{T}"/>, T's default, as .NET makes it.
/// </summary>
internal static class Defaults
{
    public static T Of<T>() => default!;
}
