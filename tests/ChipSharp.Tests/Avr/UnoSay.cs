namespace ChipSharp.Tests.Avr;

/// <summary>
/// The C# a test program for the Uno says what it found with: chars, numbers, hex and bools as bytes on USART0 through
/// ChipSharp.Hardware.Uart (port 0, the console's, at 115200 baud), one at a time, with no string and no heap. A program
/// calls <c>Say.Open()</c> first; the source to append is <see cref="Source"/>.
/// </summary>
internal static class UnoSay
{
    public const string Source = """

        static class Say
        {
            public static void Open() => Uart.Open(0, 1, 0, 115200);

            public static void Char(char c) => Uart.WriteByte(0, (byte)c);

            public static void Space() => Char(' ');

            public static void Line() => Char('\n');

            /// <summary>T or F.</summary>
            public static void Bool(bool value) => Char(value ? 'T' : 'F');

            public static void Number(int value)
            {
                if (value < 0)
                {
                    Char('-');
                    value = -value;
                }

                Span<byte> digits = stackalloc byte[10];
                var count = 0;
                do
                {
                    digits[count++] = (byte)('0' + value % 10);
                    value /= 10;
                }
                while (value != 0);

                while (count > 0)
                {
                    Uart.WriteByte(0, digits[--count]);
                }
            }

            /// <summary>Two hex digits.</summary>
            public static void Hex(int value)
            {
                Char(Digit((value >> 4) & 15));
                Char(Digit(value & 15));
            }

            private static char Digit(int digit) => (char)(digit < 10 ? '0' + digit : 'A' + digit - 10);
        }
        """;
}
