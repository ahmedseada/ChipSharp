namespace ChipSharp.Library;

/// <summary>
/// ChipSharp's input of <see cref="System.Console"/>: <see cref="Read"/> and <see cref="ReadLine"/>, on a chip whose
/// console has input (on the Pico, UART0's RX, GPIO 1, at 115200 baud). Console.Write and Console.WriteLine are the
/// runtime's own functions (see ChipSharp.Frontend's KnownExternals), not members of this type.
/// </summary>
/// <remarks>
/// The input is the bytes the console receives, UTF-8 decoded as .NET decodes it (the runtime's ConsoleRead). Nothing is
/// echoed: what the other end types is not sent back, as .NET does not echo input that comes through a pipe or a
/// serial line. A serial line has no end, so <see cref="Read"/> never returns -1 and <see cref="ReadLine"/> never null:
/// both wait for input.
/// </remarks>
[Substitute(typeof(System.Console))]
internal static class Console
{
    /// <summary>
    /// True after <see cref="ReadLine"/> took a '\r' as the end of a line: a '\n' right after it is part of that end
    /// ("\r\n"), and the next read skips it. (.NET looks at the char after '\r' at once, waiting for it; this waits only
    /// when the next read does.)
    /// </summary>
    private static bool _afterReturn;

    /// <summary>The next char of the input, waiting for it: a UTF-16 code unit, '\r' and '\n' too.</summary>
    public static int Read()
    {
        var value = Runtime.ConsoleRead();
        if (_afterReturn)
        {
            _afterReturn = false;
            if (value == '\n')
            {
                value = Runtime.ConsoleRead();
            }
        }

        return value;
    }

    /// <summary>
    /// The next line of the input, without its end, waiting for it: a line ends with "\r\n", "\r" or "\n", as .NET's
    /// ReadLine ends it. It makes a string, so it needs a heap.
    /// </summary>
    public static string? ReadLine()
    {
        var line = new System.Text.StringBuilder();
        while (true)
        {
            var value = Read();
            if (value == '\r')
            {
                _afterReturn = true;
                return line.ToString();
            }

            if (value == '\n')
            {
                return line.ToString();
            }

            line.Append((char)value);
        }
    }
}
