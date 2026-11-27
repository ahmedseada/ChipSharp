namespace ChipSharp.Library.Exceptions;

/// <summary>
/// ChipSharp's <see cref="System.ArgumentException"/>: its message ends with " (Parameter 'name')" when it has a parameter's
/// name, as .NET's does.
/// </summary>
[Substitute(typeof(System.ArgumentException))]
public class ArgumentException : SystemException
{
    private const string Default = "Value does not fall within the expected range.";
    private readonly string? _paramName;

    public ArgumentException()
        : base(Default)
    {
    }

    public ArgumentException(string? message)
        : base(message ?? Default)
    {
    }

    public ArgumentException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }

    public ArgumentException(string? message, string? paramName, Exception? innerException)
        : base(message ?? Default, innerException) => _paramName = paramName;

    public ArgumentException(string? message, string? paramName)
        : base(message ?? Default) => _paramName = paramName;

    public override string Message => string.IsNullOrEmpty(_paramName)
        ? base.Message
        : base.Message + " (Parameter '" + _paramName + "')";

    public virtual string? ParamName => _paramName;

    /// <summary>ArgumentNullException for null, ArgumentException for "".</summary>
    public static void ThrowIfNullOrEmpty(string? argument, string? paramName = null)
    {
        if (string.IsNullOrEmpty(argument))
        {
            ThrowNullOrEmpty(argument, paramName);
        }
    }

    /// <summary>ArgumentNullException for null, ArgumentException for text of spaces only (or "").</summary>
    public static void ThrowIfNullOrWhiteSpace(string? argument, string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            if (argument is null)
            {
                throw new System.ArgumentNullException(paramName);
            }

            throw new System.ArgumentException(
                "The value cannot be an empty string or composed entirely of whitespace.", paramName);
        }
    }

    private static void ThrowNullOrEmpty(string? argument, string? paramName)
    {
        if (argument is null)
        {
            throw new System.ArgumentNullException(paramName);
        }

        throw new System.ArgumentException("The value cannot be an empty string.", paramName);
    }
}

[Substitute(typeof(System.ArgumentNullException))]
public class ArgumentNullException : ArgumentException
{
    private const string Default = "Value cannot be null.";

    public ArgumentNullException()
        : base(Default)
    {
    }

    public ArgumentNullException(string? paramName)
        : base(Default, paramName)
    {
    }

    public ArgumentNullException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }

    public ArgumentNullException(string? paramName, string? message)
        : base(message ?? Default, paramName)
    {
    }

    /// <summary>ArgumentNullException when <paramref name="argument"/> is null (C# passes its expression as the name).</summary>
    public static void ThrowIfNull(object? argument, string? paramName = null)
    {
        if (argument is null)
        {
            throw new System.ArgumentNullException(paramName);
        }
    }
}

/// <summary>
/// ChipSharp's <see cref="System.ArgumentOutOfRangeException"/>: the value it was given is on a line of its own after the
/// message ("Actual value was 7."), as .NET writes it. The value is kept as its text.
/// </summary>
[Substitute(typeof(System.ArgumentOutOfRangeException))]
public class ArgumentOutOfRangeException : ArgumentException
{
    private const string Default = "Specified argument was out of the range of valid values.";
    private readonly object? _actualValue;

    public ArgumentOutOfRangeException()
        : base(Default)
    {
    }

    public ArgumentOutOfRangeException(string? paramName)
        : base(Default, paramName)
    {
    }

    public ArgumentOutOfRangeException(string? paramName, string? message)
        : base(message ?? Default, paramName)
    {
    }

    public ArgumentOutOfRangeException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }

    public ArgumentOutOfRangeException(string? paramName, object? actualValue, string? message)
        : base(message ?? Default, paramName) => _actualValue = actualValue;

    public virtual object? ActualValue => _actualValue;

    public override string Message
    {
        get
        {
            var message = base.Message;
            if (_actualValue == null)
            {
                return message;
            }

            var value = "Actual value was " + _actualValue.ToString() + ".";
            return message + "\r\n" + value;
        }
    }
}
