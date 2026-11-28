using System.Globalization;
using System.Text;
using ChipSharp.Ir;

namespace ChipSharp.Tests.CBackend;

/// <summary>
/// TIR programs for the tests of the c code generator, each with what it must print, worked out here by C#'s own
/// arithmetic (a reference of TIR's meaning): the arithmetic, comparisons and conversions of every integer width and
/// both floats, on the values where C and C# part ways (overflow, the smallest value, negative division, shifts by the
/// width less one, NaN, saturation, rounding), each once as a call (the operands are parameters there) and once on
/// constants; and memory (structs, rows of elements, unaligned and volatile access, addresses as numbers).
/// </summary>
internal static class CPrograms
{
    private static readonly TirType[] Integers = [TirType.I8, TirType.I16, TirType.I32, TirType.I64];

    /// <summary>A program with a void Main that prints through the runtime's console functions, and what it must print.</summary>
    public sealed class Printer
    {
        private readonly Dictionary<string, TirFunction> _prints = [];
        private readonly StringBuilder _expected = new();
        private readonly List<(string Line, string What)> _lines = [];

        public Printer(string name)
        {
            Module = new TirModule(name);
            Main = Module.AddFunction("Program.Main", TirType.Void);
            Module.EntryPoint = Main;
            Ir = new TirBuilder(Main.AddBlock("entry"));
        }

        public TirModule Module { get; }

        public TirFunction Main { get; }

        public TirBuilder Ir { get; }

        /// <summary>What the program must print, line by line as .NET ends them.</summary>
        public string Expected => _expected.ToString();

        /// <summary>What the next lines print, for a message that names the line that differs.</summary>
        public string What { get; set; } = "";

        /// <summary>
        /// Null when <paramref name="printed"/> is what the program must print; else the first line that differs, what it
        /// prints and what it should.
        /// </summary>
        public string? Mismatch(string printed)
        {
            var lines = printed.Split("\r\n");
            for (var i = 0; i < _lines.Count; i++)
            {
                var line = i < lines.Length ? lines[i] : "(nothing)";
                if (line != _lines[i].Line)
                {
                    return $"line {i + 1}, {_lines[i].What}: printed {line}, should be {_lines[i].Line}";
                }
            }

            return printed == Expected ? null : $"printed more than the {_lines.Count} lines it should";
        }

        /// <summary>Prints an integer of any width as a long, sign-extended, and expects <paramref name="expected"/>.</summary>
        public void Integer(TirValue value, long expected)
        {
            var wide = value.Type == TirType.I64 ? value : Ir.Convert(value, TirType.I64);
            Print("WriteLineInt64", wide, expected.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Prints a bool (as the runtime takes it: an int, 0 or 1) and expects <paramref name="expected"/>.</summary>
        public void Bool(TirValue value, bool expected) =>
            Print("WriteLineBool", Ir.Convert(value, TirType.I32), expected ? "True" : "False");

        /// <summary>
        /// Prints a float or double as its bits (read back from memory as an integer of its width), or NaN as -1 (its
        /// sign and payload are the processor's), and expects the bits of <paramref name="expected"/>.
        /// </summary>
        public void Float(TirValue value, double expected)
        {
            var isFloat = value.Type == TirType.F32;
            var slot = Main.AddSlot($"bits{Main.Slots.Count}", value.Type);
            Ir.Store(slot, value);
            var bits = Ir.Load(isFloat ? TirType.I32 : TirType.I64, Opaque(Ir, Ir.AddressOf(slot)));
            var isNaN = Ir.Compare(TirCompareOp.Ne, value, value);
            var shown = Ir.Binary(TirBinaryOp.Or, Ir.Convert(bits, TirType.I64, unsigned: true),
                Ir.Binary(TirBinaryOp.Sub, Ir.Const(TirType.I64, 0), Ir.Convert(isNaN, TirType.I64)));
            var text = double.IsNaN(expected) ? "-1"
                : isFloat ? BitConverter.SingleToUInt32Bits((float)expected).ToString(CultureInfo.InvariantCulture)
                : BitConverter.DoubleToInt64Bits(expected).ToString(CultureInfo.InvariantCulture);
            Print("WriteLineInt64", shown, text);
        }

        /// <summary>Ends Main where the builder is and returns the module.</summary>
        public TirModule Finish()
        {
            Ir.Return();
            return Module;
        }

        private void Print(string name, TirValue value, string expected)
        {
            if (!_prints.TryGetValue(name, out var function))
            {
                function = Module.AddExternalFunction($"ChipSharp.Runtime.{name}", TirType.Void, ("value", value.Type));
                _prints[name] = function;
            }

            Ir.Call(function, value);
            _expected.Append(expected).Append("\r\n");
            _lines.Add((expected, What));
        }
    }

    /// <summary>Every integer operation of every width on values where C and C# part ways, as a call and on constants.</summary>
    public static Printer IntegerArithmetic()
    {
        var program = new Printer("IntegerArithmetic");
        foreach (var type in Integers)
        {
            var bits = Bits(type);
            var (min, max) = (-(1L << (bits - 1)), bits == 64 ? long.MaxValue : (1L << (bits - 1)) - 1);
            (long, long)[] pairs =
            [
                (max, 1), (min, 1), (min, -1), (max, max), (min, min), (-7, 2), (7, -2), (-7, -2),
                (-1, 1), (min, 3), (max / 3 + 5, 7), (0x5A, 0x3C), (-100, 3),
            ];
            foreach (var op in Enum.GetValues<TirBinaryOp>())
            {
                var function = Operation(program.Module, op, type);
                foreach (var (left, right) in pairs)
                {
                    var amount = op is TirBinaryOp.Shl or TirBinaryOp.Shr or TirBinaryOp.ShrUnsigned
                        ? (long)((ulong)right & (ulong)(bits - 1))
                        : right;
                    if (IsDivision(op) && (Wrap(amount, bits) == 0 || (op is TirBinaryOp.Div or TirBinaryOp.Rem
                        && Wrap(left, bits) == min && Wrap(amount, bits) == -1)))
                    {
                        continue; // the TIR checks these before it divides
                    }

                    var expected = Evaluate(op, bits, left, amount);
                    var ir = program.Ir;
                    program.What = $"{op} {type} {left}, {amount}";
                    program.Integer(ir.Call(function, Constant(ir, type, left), Constant(ir, type, amount)), expected);
                    program.What += " (constants)";
                    program.Integer(ir.Binary(op, Constant(ir, type, left), Constant(ir, type, amount)), expected);
                }
            }
        }

        return program;
    }

    /// <summary>Every comparison of every integer width, signed and unsigned, as a call and on constants.</summary>
    public static Printer IntegerComparisons()
    {
        var program = new Printer("IntegerComparisons");
        foreach (var type in Integers)
        {
            var bits = Bits(type);
            var min = -(1L << (bits - 1));
            (long, long)[] pairs = [(-1, 1), (1, -1), (min, 0), (min, -min - 1), (5, 5), (0, 0x7F)];
            foreach (var op in Enum.GetValues<TirCompareOp>())
            {
                var function = Comparison(program.Module, op, type);
                foreach (var (left, right) in pairs)
                {
                    var expected = Compare(op, bits, left, right);
                    var ir = program.Ir;
                    program.What = $"{op} {type} {left}, {right}";
                    program.Bool(ir.Call(function, Constant(ir, type, left), Constant(ir, type, right)), expected);
                    program.What += " (constants)";
                    program.Bool(ir.Compare(op, Constant(ir, type, left), Constant(ir, type, right)), expected);
                }
            }
        }

        return program;
    }

    /// <summary>Every integer conversion: narrowing, sign- and zero-extension, from bool.</summary>
    public static Printer IntegerConversions()
    {
        var program = new Printer("IntegerConversions");
        long[] values = [-1, -128, 127, 255, 0x1234, -0x8000, 0x7FFF_FFFF, unchecked((long)0xFFFF_FFFF_8000_0000), 0x1_2345_6789];
        foreach (var from in Integers)
        {
            foreach (var to in Integers)
            {
                foreach (var unsigned in new[] { false, true })
                {
                    foreach (var value in values)
                    {
                        var ir = program.Ir;
                        var constant = Constant(ir, from, value);
                        var source = Wrap(value, Bits(from));
                        var widened = unsigned ? (long)((ulong)source & Mask(Bits(from))) : source;
                        program.What = $"convert {from} {value} to {to}{(unsigned ? " unsigned" : "")}";
                        program.Integer(ir.Convert(constant, to, unsigned), Wrap(widened, Bits(to)));
                    }
                }
            }
        }

        var ir2 = program.Ir;
        program.Integer(ir2.Convert(ir2.Const(true), TirType.I32), 1);
        program.Integer(ir2.Convert(ir2.Compare(TirCompareOp.Lt, ir2.Const(TirType.I8, 1), ir2.Const(TirType.I8, 2)),
            TirType.I64), 1);
        return program;
    }

    /// <summary>Floats and doubles: arithmetic, % (fmod), comparisons with NaN (ordered and not), and conversions.</summary>
    public static Printer Floats()
    {
        var program = new Printer("Floats");
        double[] values = [1.5, -0.0, 0.1, 3.0, -2.5, double.PositiveInfinity, double.NaN, 1e-310, 1e300, 16777217];
        foreach (var type in new[] { TirType.F32, TirType.F64 })
        {
            var isFloat = type == TirType.F32;
            double Round(double x) => isFloat ? (float)x : x;
            foreach (var op in new[] { TirBinaryOp.Add, TirBinaryOp.Sub, TirBinaryOp.Mul, TirBinaryOp.Div, TirBinaryOp.Rem })
            {
                var function = Operation(program.Module, op, type);
                foreach (var left in values)
                {
                    foreach (var right in new[] { 2.0, -0.75, 0.1, double.NaN, 0.0 })
                    {
                        var (a, b) = (Round(left), Round(right));
                        var expected = isFloat ? FloatOp(op, (float)a, (float)b) : DoubleOp(op, a, b);
                        var ir = program.Ir;
                        program.What = $"{op} {type} {a}, {b}";
                        program.Float(ir.Call(function, ir.Const(type, a), ir.Const(type, b)), expected);
                    }
                }
            }

            foreach (var op in Enum.GetValues<TirCompareOp>())
            {
                var function = Comparison(program.Module, op, type);
                foreach (var (left, right) in new[] { (1.0, 2.0), (2.0, 2.0), (double.NaN, 1.0), (1.0, double.NaN), (-0.0, 0.0) })
                {
                    var ir = program.Ir;
                    program.What = $"{op} {type} {left}, {right}";
                    program.Bool(ir.Call(function, ir.Const(type, left), ir.Const(type, right)), FloatCompare(op, left, right));
                }
            }
        }

        // a float to every integer, as .NET converts: rounded toward zero, saturated, NaN to 0
        double[] big = [300.75, -200.5, 1e10, -1e10, 4.2e9, 1.9e19, -1.0, -0.5, double.NaN, double.NegativeInfinity, 65535.9];
        foreach (var from in new[] { TirType.F32, TirType.F64 })
        {
            foreach (var to in Integers)
            {
                foreach (var unsigned in new[] { false, true })
                {
                    var function = Conversion(program.Module, from, to, unsigned);
                    foreach (var value in big)
                    {
                        var source = from == TirType.F32 ? (float)value : value;
                        var expected = Saturate(source, Bits(to), unsigned);
                        var ir = program.Ir;
                        program.What = $"convert {from} {source} to {to}{(unsigned ? " unsigned" : "")}";
                        program.Integer(ir.Call(function, ir.Const(from, source)), expected);
                    }
                }
            }
        }

        // an integer to a float, rounded to nearest (ties to even); and a double to a float and back
        long[] whole = [16777217, -16777217, 0x7FFF_FFFF, -1, long.MaxValue, unchecked((long)0x8000_0000_0000_0401)];
        foreach (var from in new[] { TirType.I32, TirType.I64 })
        {
            foreach (var unsigned in new[] { false, true })
            {
                foreach (var value in whole)
                {
                    var source = Wrap(value, Bits(from));
                    var ir = program.Ir;
                    program.What = $"convert {from} {value} to a float{(unsigned ? ", unsigned" : "")}";
                    var asUnsigned = (ulong)source & Mask(Bits(from));
                    program.Float(ir.Convert(Constant(ir, from, value), TirType.F32, unsigned),
                        unsigned ? (float)asUnsigned : (float)source);
                    program.Float(ir.Convert(Constant(ir, from, value), TirType.F64, unsigned),
                        unsigned ? (double)asUnsigned : (double)source);
                }
            }
        }

        var ir3 = program.Ir;
        program.Float(ir3.Convert(ir3.Const(TirType.F64, 0.1), TirType.F32), 0.1f);
        program.Float(ir3.Convert(ir3.Const(TirType.F32, 0.1), TirType.F64), 0.1f);
        program.Float(ir3.Convert(ir3.Const(TirType.F64, 1e300), TirType.F32), float.PositiveInfinity);
        return program;
    }

    /// <summary>
    /// Memory: a struct's fields through its slot's address and through a pointer, a row of elements indexed from its
    /// middle backwards, an int read and written at an odd address, volatile access, an address as a number and back,
    /// addresses compared, a table of constants, a global with a float's starting value, sizeof.
    /// </summary>
    public static Printer Memory()
    {
        var program = new Printer("Memory");
        var module = program.Module;
        var ir = program.Ir;
        var point = new TirStructType("Program.Point", ("X", TirType.I8), ("Y", TirType.I32), ("Z", TirType.I64),
            ("Where", TirType.Ptr), ("Row", new TirArrayType(TirType.I16, 3)));
        var p = program.Main.AddSlot("p", point);
        var bytes = program.Main.AddSlot("bytes", new TirArrayType(TirType.I8, 16));
        var table = module.AddArray("Program.Table", new TirArrayType(TirType.I16, 4),
            [new TirConst(TirType.I16, -2), new TirConst(TirType.I16, 300), new TirConst(TirType.I16, 0x7FFF),
                new TirConst(TirType.I16, -32768)], isConstant: true);
        var floats = module.AddArray("Program.Floats", new TirArrayType(TirType.F64, 3),
            [new TirConst(TirType.F64, -0.0), new TirConst(TirType.F64, double.NegativeInfinity), new TirConst(TirType.F64, 0.1)],
            isConstant: false);
        var start = module.AddGlobal("Program.Start", TirType.F32, new TirConst(TirType.F32, 2.5));

        // p.X = -1; p.Y = int.MinValue; p.Z = p.Y * 3L; p.Row[2] = -5, through the slot's place
        var pAddress = ir.AddressOf(p);
        ir.Store(ir.FieldAddress(pAddress, point, 0), ir.Const(TirType.I8, -1));
        ir.Store(ir.FieldAddress(pAddress, point, 1), ir.Const(TirType.I32, int.MinValue));
        var y = ir.Load(TirType.I32, ir.FieldAddress(pAddress, point, 1));
        ir.Store(ir.FieldAddress(pAddress, point, 2), ir.Binary(TirBinaryOp.Mul, ir.Convert(y, TirType.I64), ir.Const(TirType.I64, 3)));
        var row = ir.FieldAddress(pAddress, point, 4);
        ir.Store(ir.ElementAddress(row, TirType.I16, ir.Const(TirType.I32, 2)), ir.Const(TirType.I16, -5));
        ir.Store(ir.FieldAddress(pAddress, point, 3), pAddress);

        // through a pointer that went through memory (the struct's own Where): read them back
        var where = ir.Load(TirType.Ptr, ir.FieldAddress(pAddress, point, 3));
        program.Integer(ir.Load(TirType.I8, ir.FieldAddress(where, point, 0)), -1);
        program.Integer(ir.Load(TirType.I64, ir.FieldAddress(where, point, 2)), 3L * int.MinValue);
        var middle = ir.ElementAddress(ir.FieldAddress(where, point, 4), TirType.I16, ir.Const(TirType.I64, 2));
        program.Integer(ir.Load(TirType.I16, ir.ElementAddress(middle, TirType.I16, ir.Const(TirType.I32, 0))), -5);
        program.Integer(ir.Load(TirType.I16, ir.ElementAddress(middle, TirType.I16, ir.Const(TirType.I32, -2))), 0);

        // an int at an odd address: written and read back byte by byte where that matters
        var bytesAddress = ir.AddressOf(bytes);
        var odd = Opaque(ir, ir.ElementAddress(bytesAddress, TirType.I8, ir.Const(TirType.I32, 3)));
        ir.StoreUnaligned(odd, ir.Const(TirType.I32, 0x11223344));
        program.Integer(ir.LoadUnaligned(TirType.I32, odd), 0x11223344);
        program.Integer(ir.Load(TirType.I8, ir.ElementAddress(bytesAddress, TirType.I8, ir.Const(TirType.I32, 3))), 0x44);

        // volatile: written and read exactly as said
        var volatileAddress = ir.ElementAddress(bytesAddress, TirType.I8, ir.Const(TirType.I32, 8));
        ir.Store(volatileAddress, ir.Const(TirType.I8, 0x5A), isVolatile: true);
        program.Integer(ir.Load(TirType.I8, volatileAddress, isVolatile: true), 0x5A);

        // an address as a number and back; addresses compared as unsigned numbers
        var number = ir.PtrToInt(odd, TirType.NativeInt);
        var back = ir.IntToPtr(ir.Binary(TirBinaryOp.Add, number, ir.Const(TirType.NativeInt, 1)));
        program.Integer(ir.Load(TirType.I8, back), 0x33);
        program.Bool(ir.Compare(TirCompareOp.LtUnsigned, odd, back), true);
        program.Bool(ir.Compare(TirCompareOp.Eq, ir.Zero(TirType.Ptr), ir.Zero(TirType.Ptr)), true);

        // the constant table, the global array of doubles, the float global
        var tableAddress = ir.AddressOf(table);
        foreach (var (index, value) in new[] { (0, -2L), (1, 300L), (2, 0x7FFFL), (3, -32768L) })
        {
            program.Integer(ir.Load(TirType.I16, ir.ElementAddress(tableAddress, TirType.I16, ir.Const(TirType.I32, index))), value);
        }

        var floatsAddress = ir.AddressOf(floats);
        foreach (var (index, value) in new[] { (0, -0.0), (1, double.NegativeInfinity), (2, 0.1) })
        {
            program.Float(ir.Load(TirType.F64, ir.ElementAddress(floatsAddress, TirType.F64, ir.Const(TirType.I32, index))), value);
        }

        program.Float(ir.Load(TirType.F32, ir.AddressOf(start)), 2.5f);
        program.Integer(ir.SizeOf(point), 32); // X at 0, Y at 4, Z at 8, Where at 16, Row at 24, then padding to 8

        // a whole struct copied through a slot, and a zero one stored over it
        var copy = program.Main.AddSlot("copy", point);
        ir.Store(copy, ir.Load(p));
        ir.Store(p, ir.Zero(point));
        program.Integer(ir.Load(TirType.I32, ir.FieldAddress(ir.AddressOf(copy), point, 1)), int.MinValue);
        program.Integer(ir.Load(TirType.I32, ir.FieldAddress(ir.AddressOf(p), point, 1)), 0);
        return program;
    }

    /// <summary>
    /// A program with a call through an address and an exported function (an interrupt handler's entry) that calls a
    /// function Main calls too: Main returns Shared(Twice(21)) through the address of Twice.
    /// </summary>
    public static TirModule IndirectAndInterrupt()
    {
        var module = new TirModule("Indirect");
        var twice = module.AddFunction("Program.Twice", TirType.I32, ("x", TirType.I32));
        var ir = new TirBuilder(twice.AddBlock("entry"));
        ir.Return(ir.Binary(TirBinaryOp.Mul, twice.Parameters[0], ir.Const(TirType.I32, 2)));

        var shared = module.AddFunction("Program.Shared", TirType.I32, ("x", TirType.I32));
        ir = new TirBuilder(shared.AddBlock("entry"));
        ir.Return(ir.Binary(TirBinaryOp.Add, shared.Parameters[0], ir.Const(TirType.I32, 1)));

        var ticks = module.AddGlobal("Program.ticks", TirType.I32);
        var handler = module.AddFunction("<Interrupt timer>", TirType.Void);
        handler.ExportName = "chipsharp_isr_irq1";
        ir = new TirBuilder(handler.AddBlock("entry"));
        ir.Store(ir.AddressOf(ticks), ir.Call(shared, ir.Load(TirType.I32, ir.AddressOf(ticks))));
        ir.Return();

        var main = module.AddFunction("Program.Main", TirType.I32);
        module.EntryPoint = main;
        ir = new TirBuilder(main.AddBlock("entry"));
        var doubled = ir.CallIndirect(TirType.I32, ir.AddressOf(twice), ir.Const(TirType.I32, 21));
        ir.Return(ir.Call(shared, doubled));
        return module;
    }

    /// <summary><c>function T Program.Add_i32(T %a, T %b)</c>: the operation on its parameters.</summary>
    private static TirFunction Operation(TirModule module, TirBinaryOp op, TirType type)
    {
        var function = module.AddFunction($"Program.{op}_{type}", type, ("a", type), ("b", type));
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.Return(ir.Binary(op, function.Parameters[0], function.Parameters[1]));
        return function;
    }

    /// <summary><c>function bool Program.Lt_i32(T %a, T %b)</c>: the comparison of its parameters.</summary>
    private static TirFunction Comparison(TirModule module, TirCompareOp op, TirType type)
    {
        var function = module.AddFunction($"Program.{op}_{type}", TirType.Bool, ("a", type), ("b", type));
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.Return(ir.Compare(op, function.Parameters[0], function.Parameters[1]));
        return function;
    }

    /// <summary><c>function i32 Program.Convert_f64_i32(f64 %value)</c>: the conversion of its parameter.</summary>
    private static TirFunction Conversion(TirModule module, TirType from, TirType to, bool unsigned)
    {
        var function = module.AddFunction($"Program.Convert_{from}_{(unsigned ? "u" : "")}{to}", to, ("value", from));
        var ir = new TirBuilder(function.AddBlock("entry"));
        ir.Return(ir.Convert(function.Parameters[0], to, unsigned));
        return function;
    }

    private static TirConst Constant(TirBuilder ir, TirType type, long value) => ir.Const(type, Wrap(value, Bits(type)));

    /// <summary>
    /// <paramref name="address"/> through a number, so that the verifier does not see what it points to: a load of
    /// another type than is there (the bits of a float, an int among bytes), as unsafe C# makes.
    /// </summary>
    private static TirValue Opaque(TirBuilder ir, TirValue address) => ir.IntToPtr(ir.PtrToInt(address, TirType.NativeInt));

    private static int Bits(TirType type) => type.Kind switch
    {
        TirTypeKind.I8 => 8,
        TirTypeKind.I16 => 16,
        TirTypeKind.I32 => 32,
        _ => 64,
    };

    private static ulong Mask(int bits) => bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;

    /// <summary><paramref name="value"/>'s low <paramref name="bits"/> bits, read as signed.</summary>
    private static long Wrap(long value, int bits) => bits == 64 ? value : (value << (64 - bits)) >> (64 - bits);

    private static bool IsDivision(TirBinaryOp op) =>
        op is TirBinaryOp.Div or TirBinaryOp.DivUnsigned or TirBinaryOp.Rem or TirBinaryOp.RemUnsigned;

    /// <summary>TIR's meaning of <paramref name="op"/> in <paramref name="bits"/> bits, by C#'s 64-bit arithmetic.</summary>
    private static long Evaluate(TirBinaryOp op, int bits, long left, long right)
    {
        var (sa, sb) = (Wrap(left, bits), Wrap(right, bits));
        var (ua, ub) = ((ulong)sa & Mask(bits), (ulong)sb & Mask(bits));
        var result = op switch
        {
            TirBinaryOp.Add => ua + ub,
            TirBinaryOp.Sub => ua - ub,
            TirBinaryOp.Mul => ua * ub,
            TirBinaryOp.Div => (ulong)(sa / sb),
            TirBinaryOp.DivUnsigned => ua / ub,
            TirBinaryOp.Rem => (ulong)(sa % sb),
            TirBinaryOp.RemUnsigned => ua % ub,
            TirBinaryOp.And => ua & ub,
            TirBinaryOp.Or => ua | ub,
            TirBinaryOp.Xor => ua ^ ub,
            TirBinaryOp.Shl => ua << (int)ub,
            TirBinaryOp.Shr => (ulong)(sa >> (int)ub),
            _ => ua >> (int)ub,
        };

        return Wrap((long)result, bits);
    }

    private static bool Compare(TirCompareOp op, int bits, long left, long right)
    {
        var (sa, sb) = (Wrap(left, bits), Wrap(right, bits));
        var (ua, ub) = ((ulong)sa & Mask(bits), (ulong)sb & Mask(bits));
        return op switch
        {
            TirCompareOp.Eq => sa == sb,
            TirCompareOp.Ne => sa != sb,
            TirCompareOp.Lt => sa < sb,
            TirCompareOp.Le => sa <= sb,
            TirCompareOp.Gt => sa > sb,
            TirCompareOp.Ge => sa >= sb,
            TirCompareOp.LtUnsigned => ua < ub,
            TirCompareOp.LeUnsigned => ua <= ub,
            TirCompareOp.GtUnsigned => ua > ub,
            _ => ua >= ub,
        };
    }

    /// <summary>IL's comparison of floats: ordered, but != and the unsigned (unordered) ones, true when either is NaN.</summary>
    private static bool FloatCompare(TirCompareOp op, double left, double right)
    {
        var unordered = double.IsNaN(left) || double.IsNaN(right);
        return op switch
        {
            TirCompareOp.Eq => left == right,
            TirCompareOp.Ne => left != right,
            TirCompareOp.Lt => left < right,
            TirCompareOp.Le => left <= right,
            TirCompareOp.Gt => left > right,
            TirCompareOp.Ge => left >= right,
            TirCompareOp.LtUnsigned => unordered || left < right,
            TirCompareOp.LeUnsigned => unordered || left <= right,
            TirCompareOp.GtUnsigned => unordered || left > right,
            _ => unordered || left >= right,
        };
    }

    private static double FloatOp(TirBinaryOp op, float a, float b) => op switch
    {
        TirBinaryOp.Add => a + b,
        TirBinaryOp.Sub => a - b,
        TirBinaryOp.Mul => a * b,
        TirBinaryOp.Div => a / b,
        _ => a % b,
    };

    private static double DoubleOp(TirBinaryOp op, double a, double b) => op switch
    {
        TirBinaryOp.Add => a + b,
        TirBinaryOp.Sub => a - b,
        TirBinaryOp.Mul => a * b,
        TirBinaryOp.Div => a / b,
        _ => a % b,
    };

    /// <summary>.NET's conversion of a float to an integer: rounded toward zero, saturated, NaN to 0; sign-extended to a long.</summary>
    private static long Saturate(double value, int bits, bool unsigned)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        var whole = Math.Truncate(value);
        if (unsigned)
        {
            var top = Math.Pow(2, bits);
            var result = whole <= 0 ? 0UL : whole >= top ? Mask(bits) : (ulong)whole;
            return Wrap((long)result, bits);
        }

        var (min, limit) = (-Math.Pow(2, bits - 1), Math.Pow(2, bits - 1));
        return whole <= min ? -(1L << (bits - 1)) : whole >= limit ? (bits == 64 ? long.MaxValue : (1L << (bits - 1)) - 1)
            : (long)whole;
    }
}
