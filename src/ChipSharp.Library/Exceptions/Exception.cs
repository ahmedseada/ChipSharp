namespace ChipSharp.Library.Exceptions;

/// <summary>
/// ChipSharp's <see cref="System.Exception"/>: what a program throws (<c>throw new InvalidOperationException("...")</c>,
/// the program's own exceptions derived from it), which stops the program with .NET's output for an exception nothing
/// catches (see <see cref="Thrown"/>). Its <see cref="Message"/> and <see cref="ToString"/> are .NET's: the class's name
/// as .NET writes it (<c>System.InvalidOperationException</c>, <c>Program+Overheated</c>), the message, and the inner
/// exception's after <c> ---&gt; </c>, without a stack trace, which ChipSharp does not keep.
/// </summary>
/// <remarks>
/// These types are in a namespace of their own, so that the library's other code throws .NET's exceptions (which ChipSharp
/// links to these) and so does on .NET too. <c>Data</c>, <c>HResult</c>, <c>Source</c> and <c>TargetSite</c> are not
/// here yet; catching an exception arrives later.
/// </remarks>
[Substitute(typeof(System.Exception))]
public class Exception
{
    private readonly string? _message;
    private readonly Exception? _innerException;

    public Exception()
    {
    }

    public Exception(string? message) => _message = message;

    public Exception(string? message, Exception? innerException)
    {
        _message = message;
        _innerException = innerException;
    }

    /// <summary>The message: the one given, else "Exception of type '...' was thrown.", as .NET's.</summary>
    public virtual string Message => _message ?? "Exception of type '" + ClassName() + "' was thrown.";

    public Exception? InnerException => _innerException;

    /// <summary>Null: ChipSharp keeps no stack trace.</summary>
    public virtual string? StackTrace => null;

    /// <summary>The innermost exception of the chain of inner exceptions (this one when it has none).</summary>
    public virtual Exception GetBaseException()
    {
        var inner = InnerException;
        var back = this;
        while (inner != null)
        {
            back = inner;
            inner = inner.InnerException;
        }

        return back;
    }

    /// <summary>
    /// .NET's: the class's name, then ": " and the message when there is one, then the inner exception's on a line of its
    /// own after " ---> " and "   --- End of inner exception stack trace ---" (in that order: the message first).
    /// </summary>
    public override string ToString()
    {
        var className = ClassName();
        var message = Message;
        var inner = _innerException?.ToString() ?? "";
        var text = string.IsNullOrEmpty(message) ? className : className + ": " + message;
        if (_innerException != null)
        {
            text = text + "\r\n ---> " + inner + "\r\n   --- End of inner exception stack trace ---";
        }

        return text;
    }

    /// <summary>The name .NET gives the object's type (<c>GetType().ToString()</c>): what object's own ToString gives.</summary>
    private string ClassName() => base.ToString() ?? "";
}

[Substitute(typeof(System.SystemException))]
public class SystemException : Exception
{
    public SystemException()
        : base("System error.")
    {
    }

    public SystemException(string? message)
        : base(message)
    {
    }

    public SystemException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

[Substitute(typeof(System.ApplicationException))]
public class ApplicationException : Exception
{
    public ApplicationException()
        : base("Error in the application.")
    {
    }

    public ApplicationException(string? message)
        : base(message)
    {
    }

    public ApplicationException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

[Substitute(typeof(System.InvalidOperationException))]
public class InvalidOperationException : SystemException
{
    private const string Default = "Operation is not valid due to the current state of the object.";

    public InvalidOperationException()
        : base(Default)
    {
    }

    public InvalidOperationException(string? message)
        : base(message ?? Default)
    {
    }

    public InvalidOperationException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.ObjectDisposedException))]
public class ObjectDisposedException : InvalidOperationException
{
    private const string Default = "Cannot access a disposed object.";
    private readonly string? _objectName;

    public ObjectDisposedException(string? objectName)
        : this(objectName, Default)
    {
    }

    public ObjectDisposedException(string? objectName, string? message)
        : base(message ?? Default) => _objectName = objectName;

    public ObjectDisposedException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }

    public string ObjectName => _objectName ?? "";

    public override string Message => string.IsNullOrEmpty(ObjectName)
        ? base.Message
        : base.Message + "\r\nObject name: '" + ObjectName + "'.";
}

[Substitute(typeof(System.NotSupportedException))]
public class NotSupportedException : SystemException
{
    private const string Default = "Specified method is not supported.";

    public NotSupportedException()
        : base(Default)
    {
    }

    public NotSupportedException(string? message)
        : base(message ?? Default)
    {
    }

    public NotSupportedException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.NotImplementedException))]
public class NotImplementedException : SystemException
{
    private const string Default = "The method or operation is not implemented.";

    public NotImplementedException()
        : base(Default)
    {
    }

    public NotImplementedException(string? message)
        : base(message ?? Default)
    {
    }

    public NotImplementedException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.IndexOutOfRangeException))]
public sealed class IndexOutOfRangeException : SystemException
{
    private const string Default = "Index was outside the bounds of the array.";

    public IndexOutOfRangeException()
        : base(Default)
    {
    }

    public IndexOutOfRangeException(string? message)
        : base(message ?? Default)
    {
    }

    public IndexOutOfRangeException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.Collections.Generic.KeyNotFoundException))]
public class KeyNotFoundException : SystemException
{
    private const string Default = "The given key was not present in the dictionary.";

    public KeyNotFoundException()
        : base(Default)
    {
    }

    public KeyNotFoundException(string? message)
        : base(message ?? Default)
    {
    }

    public KeyNotFoundException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.FormatException))]
public class FormatException : SystemException
{
    private const string Default = "One of the identified items was in an invalid format.";

    public FormatException()
        : base(Default)
    {
    }

    public FormatException(string? message)
        : base(message ?? Default)
    {
    }

    public FormatException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.NullReferenceException))]
public class NullReferenceException : SystemException
{
    private const string Default = "Object reference not set to an instance of an object.";

    public NullReferenceException()
        : base(Default)
    {
    }

    public NullReferenceException(string? message)
        : base(message ?? Default)
    {
    }

    public NullReferenceException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.InvalidCastException))]
public class InvalidCastException : SystemException
{
    private const string Default = "Specified cast is not valid.";

    public InvalidCastException()
        : base(Default)
    {
    }

    public InvalidCastException(string? message)
        : base(message ?? Default)
    {
    }

    public InvalidCastException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.TimeoutException))]
public class TimeoutException : SystemException
{
    private const string Default = "The operation has timed out.";

    public TimeoutException()
        : base(Default)
    {
    }

    public TimeoutException(string? message)
        : base(message ?? Default)
    {
    }

    public TimeoutException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.ArithmeticException))]
public class ArithmeticException : SystemException
{
    private const string Default = "Overflow or underflow in the arithmetic operation.";

    public ArithmeticException()
        : base(Default)
    {
    }

    public ArithmeticException(string? message)
        : base(message ?? Default)
    {
    }

    public ArithmeticException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.OverflowException))]
public class OverflowException : ArithmeticException
{
    private const string Default = "Arithmetic operation resulted in an overflow.";

    public OverflowException()
        : base(Default)
    {
    }

    public OverflowException(string? message)
        : base(message ?? Default)
    {
    }

    public OverflowException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}

[Substitute(typeof(System.DivideByZeroException))]
public class DivideByZeroException : ArithmeticException
{
    private const string Default = "Attempted to divide by zero.";

    public DivideByZeroException()
        : base(Default)
    {
    }

    public DivideByZeroException(string? message)
        : base(message ?? Default)
    {
    }

    public DivideByZeroException(string? message, Exception? innerException)
        : base(message ?? Default, innerException)
    {
    }
}
