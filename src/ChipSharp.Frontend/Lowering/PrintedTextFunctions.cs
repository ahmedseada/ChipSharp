using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The TIR functions that write a part of printed text with an alignment or a format (see <see cref="PrintedText"/>),
/// each added to the module when first needed. They write with the runtime's Console.Write functions and fixed text
/// only (a space, a zero, a minus sign, the hex digits), never building a string:
/// <code>
/// function void ChipSharp.Text.Repeat(ref %text, i32 %count)                  the text count times; none when count &lt;= 0
/// function i32 ChipSharp.Text.Length(ref %text)                               its length as .NET counts it; 0 for null
/// function void ChipSharp.Text.WriteAligned(ref %text, i32 %alignment)
/// function void ChipSharp.Text.WriteChar(i32 %value, i32 %alignment)
/// function void ChipSharp.Text.WriteBool(i32 %value, i32 %alignment)
/// function void ChipSharp.Text.WriteSigned(i64 %value, i32 %precision, i32 %alignment)
/// function void ChipSharp.Text.WriteDecimal(i64 %magnitude, i32 %negative, i32 %precision, i32 %alignment)
/// function void ChipSharp.Text.WriteHex(i64 %bits, i32 %upper, i32 %precision, i32 %alignment)
/// function i32 ChipSharp.Text.Digits(i64 %magnitude)                          its decimal digits, read as unsigned
/// function i32 ChipSharp.Text.HexDigits(i64 %bits)                            its hex digits, read as unsigned
/// </code>
/// An alignment pads like .NET: with spaces before the part to <c>alignment</c> characters when it is positive, after it
/// to <c>-alignment</c> when it is negative. The spaces before are <c>alignment - width</c> of them and those after
/// <c>-alignment - width</c>, so one of the two is never more than none. A precision is the least number of digits,
/// made up with zeros after the minus sign; the hex digits of a value are those of its bits, as unsigned, so the caller
/// passes a negative number's bits of its own width (FF for an sbyte -1).
/// </summary>
internal sealed class PrintedTextFunctions(TirModule module, ProgramFunctions program)
{
    private TirFunction? _repeat;
    private TirFunction? _length;
    private TirFunction? _writeAligned;
    private TirFunction? _writeBool;
    private TirFunction? _writeSigned;
    private TirFunction? _writeDecimal;
    private TirFunction? _writeHex;
    private TirFunction? _digits;
    private TirFunction? _hexDigits;
    private TirFunction? _writeChar;

    /// <summary>Writes text (or nothing, for null) aligned.</summary>
    public TirFunction WriteAligned => _writeAligned ??= DefineWriteAligned();

    /// <summary>Writes a char (an i32) aligned.</summary>
    public TirFunction WriteChar => _writeChar ??= DefineWriteChar();

    /// <summary>Writes a bool (an i32, 0 or not) aligned: True or False.</summary>
    public TirFunction WriteBool => _writeBool ??= DefineWriteBool();

    /// <summary>Writes a signed number (sign-extended to i64) in decimal, with at least precision digits, aligned.</summary>
    public TirFunction WriteSigned => _writeSigned ??= DefineWriteSigned();

    /// <summary>Writes a magnitude in decimal after a minus sign when negative is 1, with at least precision digits, aligned.</summary>
    public TirFunction WriteDecimal => _writeDecimal ??= DefineWriteDecimal();

    /// <summary>Writes bits in hex, upper case when upper is 1, at least precision digits, aligned.</summary>
    public TirFunction WriteHex => _writeHex ??= DefineWriteHex();

    private TirFunction Repeat => _repeat ??= DefineRepeat();

    private TirFunction Length => _length ??= DefineLength();

    private TirFunction Digits => _digits ??= DefineDigits("ChipSharp.Text.Digits", TirBinaryOp.DivUnsigned, 10);

    private TirFunction HexDigits => _hexDigits ??= DefineDigits("ChipSharp.Text.HexDigits", TirBinaryOp.ShrUnsigned, 4);

    private TirFunction DefineRepeat()
    {
        var function = module.AddFunction("ChipSharp.Text.Repeat", TirType.Void, ("text", TirType.Ref), ("count", TirType.I32));
        var (text, count) = (function.Parameters[0], function.Parameters[1]);
        var left = function.AddSlot("left", TirType.I32);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var loop = function.AddBlock("loop");
        var write = function.AddBlock("write");
        var done = function.AddBlock("done");
        ir.Store(left, count);
        ir.Jump(loop);

        ir.Block = loop;
        ir.Branch(ir.Compare(TirCompareOp.Gt, ir.Load(left), I32(ir, 0)), write, done);

        ir.Block = write;
        ir.Call(program.Runtime("WriteString"), text);
        ir.Store(left, ir.Binary(TirBinaryOp.Sub, ir.Load(left), I32(ir, 1)));
        ir.Jump(loop);

        ir.Block = done;
        ir.Return();
        return function;
    }

    /// <summary>The UTF-16 length of a string (see <see cref="Strings"/>): its length field.</summary>
    private TirFunction DefineLength()
    {
        var function = module.AddFunction("ChipSharp.Text.Length", TirType.I32, ("text", TirType.Ref));
        var text = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var none = function.AddBlock("null");
        var length = function.AddBlock("length");
        ir.Branch(ir.Compare(TirCompareOp.Eq, text, ir.Zero(TirType.Ref)), none, length);

        ir.Block = none;
        ir.Return(I32(ir, 0));

        ir.Block = length;
        ir.Return(ir.ArrayLength(text));
        return function;
    }

    private TirFunction DefineWriteAligned()
    {
        var function = module.AddFunction("ChipSharp.Text.WriteAligned", TirType.Void,
            ("text", TirType.Ref), ("alignment", TirType.I32));
        var (text, alignment) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var width = ir.Call(Length, text);
        PadBefore(ir, alignment, width);
        ir.Call(program.Runtime("WriteString"), text);
        PadAfter(ir, alignment, width);
        ir.Return();
        return function;
    }

    private TirFunction DefineWriteBool()
    {
        var function = module.AddFunction("ChipSharp.Text.WriteBool", TirType.Void,
            ("value", TirType.I32), ("alignment", TirType.I32));
        var (value, alignment) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));

        // "True" is 4 characters, "False" 5
        var isTrue = ir.Convert(ir.Compare(TirCompareOp.Ne, value, I32(ir, 0)), TirType.I32);
        var width = ir.Binary(TirBinaryOp.Sub, I32(ir, 5), isTrue);
        PadBefore(ir, alignment, width);
        ir.Call(program.Runtime("WriteBool"), value);
        PadAfter(ir, alignment, width);
        ir.Return();
        return function;
    }

    /// <summary>The magnitude of a signed value, in unsigned arithmetic, so that long.MinValue has one too.</summary>
    private TirFunction DefineWriteSigned()
    {
        var function = module.AddFunction("ChipSharp.Text.WriteSigned", TirType.Void,
            ("value", TirType.I64), ("precision", TirType.I32), ("alignment", TirType.I32));
        var value = function.Parameters[0];
        var ir = new TirBuilder(function.AddBlock("entry"));
        var sign = ir.Binary(TirBinaryOp.Shr, value, ir.Const(TirType.I64, 63));
        var magnitude = ir.Binary(TirBinaryOp.Sub, ir.Binary(TirBinaryOp.Xor, value, sign), sign);
        var negative = ir.Convert(ir.Compare(TirCompareOp.Lt, value, ir.Const(TirType.I64, 0)), TirType.I32);
        ir.Call(WriteDecimal, magnitude, negative, function.Parameters[1], function.Parameters[2]);
        ir.Return();
        return function;
    }

    private TirFunction DefineWriteDecimal()
    {
        var function = module.AddFunction("ChipSharp.Text.WriteDecimal", TirType.Void, ("magnitude", TirType.I64),
            ("negative", TirType.I32), ("precision", TirType.I32), ("alignment", TirType.I32));
        var (magnitude, negative, precision, alignment) =
            (function.Parameters[0], function.Parameters[1], function.Parameters[2], function.Parameters[3]);
        var digits = function.AddSlot("digits", TirType.I32);
        var width = function.AddSlot("width", TirType.I32);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var pad = function.AddBlock("pad");
        var minus = function.AddBlock("minus");
        var number = function.AddBlock("number");

        // the width: the digits (at least precision of them), and the minus sign
        WidthOf(ir, ir.Call(Digits, magnitude), precision, digits, width, pad);
        ir.Block = pad;
        PadBefore(ir, alignment, ir.Binary(TirBinaryOp.Add, ir.Load(width), negative));
        ir.Branch(ir.Compare(TirCompareOp.Ne, negative, I32(ir, 0)), minus, number);

        ir.Block = minus;
        ir.Call(program.Runtime("WriteString"), ir.AddressOf(program.Text("-")));
        ir.Jump(number);

        ir.Block = number;
        ir.Call(Repeat, ir.AddressOf(program.Text("0")), ir.Binary(TirBinaryOp.Sub, precision, ir.Load(digits)));
        ir.Call(program.Runtime("WriteUInt64"), magnitude);
        PadAfter(ir, alignment, ir.Binary(TirBinaryOp.Add, ir.Load(width), negative));
        ir.Return();
        return function;
    }

    /// <summary>The digits from the highest one down, written by the runtime (ChipSharp.Runtime.WriteHexDigits).</summary>
    private TirFunction DefineWriteHex()
    {
        var function = module.AddFunction("ChipSharp.Text.WriteHex", TirType.Void, ("bits", TirType.I64),
            ("upper", TirType.I32), ("precision", TirType.I32), ("alignment", TirType.I32));
        var (bits, upper, precision, alignment) =
            (function.Parameters[0], function.Parameters[1], function.Parameters[2], function.Parameters[3]);
        var digits = function.AddSlot("digits", TirType.I32);
        var width = function.AddSlot("width", TirType.I32);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var pad = function.AddBlock("pad");

        WidthOf(ir, ir.Call(HexDigits, bits), precision, digits, width, pad);
        ir.Block = pad;
        PadBefore(ir, alignment, ir.Load(width));
        ir.Call(Repeat, ir.AddressOf(program.Text("0")), ir.Binary(TirBinaryOp.Sub, precision, ir.Load(digits)));
        ir.Call(program.RuntimeFunction("WriteHexDigits", TirType.Void, TirType.I64, TirType.I32, TirType.I32), bits,
            ir.Load(digits), upper);
        PadAfter(ir, alignment, ir.Load(width));
        ir.Return();
        return function;
    }

    /// <summary>A char (an i32 holding its UTF-16 code unit), one character wide, aligned.</summary>
    private TirFunction DefineWriteChar()
    {
        var function = module.AddFunction("ChipSharp.Text.WriteChar", TirType.Void,
            ("value", TirType.I32), ("alignment", TirType.I32));
        var (value, alignment) = (function.Parameters[0], function.Parameters[1]);
        var ir = new TirBuilder(function.AddBlock("entry"));
        PadBefore(ir, alignment, I32(ir, 1));
        ir.Call(program.Runtime("WriteChar"), value);
        PadAfter(ir, alignment, I32(ir, 1));
        ir.Return();
        return function;
    }

    /// <summary>
    /// How many digits a number has, read as unsigned: one, and one more for each time <paramref name="op"/> by
    /// <paramref name="by"/> (a division by 10, a shift by 4) leaves something.
    /// </summary>
    private TirFunction DefineDigits(string name, TirBinaryOp op, long by)
    {
        var function = module.AddFunction(name, TirType.I32, ("value", TirType.I64));
        var value = function.Parameters[0];
        var count = function.AddSlot("count", TirType.I32);
        var rest = function.AddSlot("rest", TirType.I64);
        var ir = new TirBuilder(function.AddBlock("entry"));
        var loop = function.AddBlock("loop");
        var more = function.AddBlock("more");
        var done = function.AddBlock("done");
        ir.Store(count, I32(ir, 1));
        ir.Store(rest, ir.Binary(op, value, ir.Const(TirType.I64, by)));
        ir.Jump(loop);

        ir.Block = loop;
        ir.Branch(ir.Compare(TirCompareOp.Ne, ir.Load(rest), ir.Const(TirType.I64, 0)), more, done);

        ir.Block = more;
        ir.Store(count, ir.Binary(TirBinaryOp.Add, ir.Load(count), I32(ir, 1)));
        ir.Store(rest, ir.Binary(op, ir.Load(rest), ir.Const(TirType.I64, by)));
        ir.Jump(loop);

        ir.Block = done;
        ir.Return(ir.Load(count));
        return function;
    }

    /// <summary>
    /// Ends the current block with <paramref name="digits"/> in its slot and the width, the larger of it and
    /// <paramref name="precision"/>, in <paramref name="width"/>; continues at <paramref name="then"/>.
    /// </summary>
    private static void WidthOf(TirBuilder ir, TirValue count, TirValue precision, TirSlot digits, TirSlot width,
        TirBlock then)
    {
        var wider = ir.Function.AddBlock("wider");
        ir.Store(digits, count);
        ir.Store(width, count);
        ir.Branch(ir.Compare(TirCompareOp.Gt, precision, count), wider, then);

        ir.Block = wider;
        ir.Store(width, precision);
        ir.Jump(then);
    }

    /// <summary>The spaces before a part <paramref name="width"/> characters wide: alignment - width of them.</summary>
    private void PadBefore(TirBuilder ir, TirValue alignment, TirValue width) =>
        ir.Call(Repeat, ir.AddressOf(program.Text(" ")), ir.Binary(TirBinaryOp.Sub, alignment, width));

    /// <summary>The spaces after a part <paramref name="width"/> characters wide: -alignment - width of them.</summary>
    private void PadAfter(TirBuilder ir, TirValue alignment, TirValue width) =>
        ir.Call(Repeat, ir.AddressOf(program.Text(" ")),
            ir.Binary(TirBinaryOp.Sub, ir.Binary(TirBinaryOp.Sub, I32(ir, 0), alignment), width));

    private static TirConst I32(TirBuilder ir, long value) => ir.Const(TirType.I32, value);
}
