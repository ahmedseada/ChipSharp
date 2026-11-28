using System.Globalization;
using System.Text;
using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// What the functions of a module need declared or defined before them, gathered while they are written, each once: the
/// C library's functions (fmod for C#'s % on a double, memset and friends), the runtime's functions for objects, the
/// typedefs of the function pointers that calls through an address cast to, and the small static helpers (a float made
/// an integer as .NET does, a copy for a value at an address that may not be aligned).
/// </summary>
internal sealed class CNeeds(CTypes types, ISet<string> names, bool reentrant)
{
    private readonly SortedDictionary<string, string> _library = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> _runtime = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _functionTypes = new(StringComparer.Ordinal);
    private readonly List<string> _functionTypeDefinitions = [];
    private readonly Dictionary<string, string> _helpers = new(StringComparer.Ordinal);
    private readonly List<string> _helperDefinitions = [];

    /// <summary>The standard declarations of the C library's functions the program calls, by name.</summary>
    private static readonly Dictionary<string, string> LibraryDeclarations = new(StringComparer.Ordinal)
    {
        ["memset"] = "void *memset(void *destination, int value, size_t count);",
        ["memcpy"] = "void *memcpy(void *destination, const void *source, size_t count);",
        ["memmove"] = "void *memmove(void *destination, const void *source, size_t count);",
        ["fmod"] = "double fmod(double x, double y);",
        ["fmodf"] = "float fmodf(float x, float y);",
    };

    /// <summary>True when a call through an address cast to a function pointer typedef.</summary>
    public bool HasFunctionTypes => _functionTypes.Count > 0;

    /// <summary>Declares the C library's function <paramref name="name"/> (one of memset, memcpy, memmove, fmod, fmodf).</summary>
    public string Library(string name)
    {
        _library[name] = LibraryDeclarations[name];
        return name;
    }

    /// <summary>True for a C library function the program may call as an external function, declared as C declares it.</summary>
    public static bool IsLibrary(string symbol) => symbol is "memset" or "memcpy" or "memmove";

    /// <summary>Declares one of the runtime's functions for objects (see <see cref="CHeap.RuntimeSymbols"/>).</summary>
    public string Runtime(string symbol)
    {
        _runtime[symbol] = CHeap.Declaration(symbol, types);
        return symbol;
    }

    /// <summary>
    /// The typedef of a pointer to a function that returns <paramref name="returns"/> and takes
    /// <paramref name="parameters"/> (C types), <c>cs_function_0</c>, ..., which a call through an address casts to.
    /// </summary>
    public string FunctionType(string returns, IReadOnlyList<string> parameters)
    {
        var list = parameters.Count == 0 ? "void" : string.Join(", ", parameters);
        var key = returns + "|" + list;
        if (!_functionTypes.TryGetValue(key, out var name))
        {
            name = CTypes.Unique(names, "cs_function_" + _functionTypes.Count.ToString(CultureInfo.InvariantCulture));
            _functionTypes[key] = name;
            var mark = reentrant ? " " + CCodeGenerator.ReentrantMacro : "";
            _functionTypeDefinitions.Add($"typedef {CTypes.Declare(returns, $"(*{name})({list})")}{mark};");
        }

        return name;
    }

    /// <summary>
    /// The helper that makes a float or double an integer as TIR (and .NET) does: rounded toward zero, saturated to
    /// the integer's range, NaN to 0; e.g. <c>cs_f64_to_i32</c>. C leaves a value out of the range undefined.
    /// </summary>
    public string FloatToInteger(TirType from, TirType to, bool unsigned)
    {
        var bits = types.Bits(to);
        var fromName = from == TirType.F32 ? "f32" : "f64";
        var wanted = $"cs_{fromName}_to_{(unsigned ? "u" : "i")}{bits.ToString(CultureInfo.InvariantCulture)}";
        if (_helpers.TryGetValue(wanted, out var name))
        {
            return name;
        }

        name = CTypes.Unique(names, wanted);
        _helpers[wanted] = name;
        var floatType = types.Of(from);
        var result = $"uint{bits.ToString(CultureInfo.InvariantCulture)}_t";
        var suffix = from == TirType.F32 ? "f" : "";
        var text = new StringBuilder();
        if (unsigned)
        {
            text.Append($"/* ({IntegerName(bits, unsigned: true)})value as .NET converts it: rounded toward zero, NaN and below 0 give 0, " +
                $"2^{bits} and above the largest */\n");
            text.Append($"static {result} {name}({floatType} value)\n{{\n");
            text.Append("    if (!(value >= 0.0").Append(suffix).Append("))\n    {\n        return 0;\n    }\n\n");
            text.Append($"    if (value >= 0x1p+{bits}{suffix})\n    {{\n        return {Maximum(bits, unsigned: true)};\n    }}\n\n");
            text.Append($"    return ({result})value;\n}}\n");
        }
        else
        {
            text.Append($"/* ({IntegerName(bits, unsigned: false)})value as .NET converts it: rounded toward zero, NaN gives 0, " +
                $"-2^{bits - 1} and below the smallest, 2^{bits - 1} and above the largest */\n");
            text.Append($"static {result} {name}({floatType} value)\n{{\n");
            text.Append("    if (value != value)\n    {\n        return 0;\n    }\n\n");
            text.Append($"    if (value <= -0x1p+{bits - 1}{suffix})\n    {{\n        return {Minimum(bits)};\n    }}\n\n");
            text.Append($"    if (value >= 0x1p+{bits - 1}{suffix})\n    {{\n        return {Maximum(bits, unsigned: false)};\n    }}\n\n");
            text.Append($"    return ({result})(int{bits.ToString(CultureInfo.InvariantCulture)}_t)value;\n}}\n");
        }

        _helperDefinitions.Add(text.ToString());
        return name;
    }

    /// <summary>
    /// The helper that copies <c>count</c> bytes one by one, <c>cs_copy</c>: a load or store at an address that may not be
    /// aligned for its type, which a chip that cannot read a misaligned word would fault on.
    /// </summary>
    public string Copy()
    {
        const string Wanted = "cs_copy";
        if (_helpers.TryGetValue(Wanted, out var name))
        {
            return name;
        }

        name = CTypes.Unique(names, Wanted);
        _helpers[Wanted] = name;
        _helperDefinitions.Add(
            "/* copies count bytes one by one: a value at an address that may not be aligned for its type */\n" +
            $"static void {name}(uint8_t *to, const uint8_t *from, size_t count)\n{{\n" +
            "    while (count > 0)\n    {\n        *to++ = *from++;\n        count--;\n    }\n}\n");
        return name;
    }

    /// <summary>
    /// The helper that does <c>a &amp; b</c>, <c>a | b</c> or <c>a ^ b</c> (<paramref name="op"/>) of two 64-bit values,
    /// <c>cs_or64</c>: SDCC 4.6 works such an operation out in 32 bits when it knows both values while it compiles, and
    /// drops the high half (<c>(uint64_t)-1099511627776 | 255</c> is 255), so with SDCC a function does it when the
    /// program runs, whose arguments it does not see into; with any other compiler it is the operator.
    /// </summary>
    public string Bitwise64(char op)
    {
        var wanted = "cs_" + (op switch { '&' => "and", '|' => "or", _ => "xor" }) + "64";
        if (_helpers.TryGetValue(wanted, out var name))
        {
            return name;
        }

        name = CTypes.Unique(names, wanted);
        _helpers[wanted] = name;
        _helperDefinitions.Add(
            $"/* a {op} b of 64 bits: SDCC 4.6 works it out in 32 bits when it knows a and b, so it is done as the " +
            "program runs */\n" +
            $"#ifdef __SDCC\nstatic uint64_t {name}(uint64_t a, uint64_t b)\n{{\n    return a {op} b;\n}}\n" +
            $"#else\n#define {name}(a, b) ((a) {op} (b))\n#endif\n");
        return name;
    }

    /// <summary>Writes the declarations: the C library's, then the runtime's, then the function pointer typedefs.</summary>
    public void WriteDeclarations(StringBuilder text, ISet<string> declaredByModule)
    {
        var library = _library.Values.ToList();
        var runtime = _runtime.Where(pair => !declaredByModule.Contains(pair.Key)).Select(pair => pair.Value).ToList();
        if (library.Count > 0)
        {
            text.Append("/* the C library's */\n");
            foreach (var declaration in library)
            {
                text.Append(declaration).Append('\n');
            }

            text.Append('\n');
        }

        if (runtime.Count > 0)
        {
            text.Append("/* the runtime's, for objects */\n");
            foreach (var declaration in runtime)
            {
                text.Append(declaration).Append('\n');
            }

            text.Append('\n');
        }

        if (_functionTypeDefinitions.Count > 0)
        {
            text.Append("/* the functions called through an address */\n");
            foreach (var definition in _functionTypeDefinitions)
            {
                text.Append(definition).Append('\n');
            }

            text.Append('\n');
        }
    }

    /// <summary>Writes the helpers the functions use, in the order they were first needed.</summary>
    public void WriteHelpers(StringBuilder text)
    {
        foreach (var helper in _helperDefinitions)
        {
            text.Append(helper).Append('\n');
        }
    }

    /// <summary>C#'s name of the integer type of <paramref name="bits"/> bits: sbyte, byte, short, ushort, ...</summary>
    private static string IntegerName(int bits, bool unsigned) => (bits, unsigned) switch
    {
        (8, false) => "sbyte",
        (8, true) => "byte",
        (16, false) => "short",
        (16, true) => "ushort",
        (32, false) => "int",
        (32, true) => "uint",
        (_, false) => "long",
        _ => "ulong",
    };

    private static string Minimum(int bits) => bits == 64 ? "0x8000000000000000u" : $"0x{(1UL << (bits - 1)).ToString("X", CultureInfo.InvariantCulture)}u";

    private static string Maximum(int bits, bool unsigned)
    {
        var value = unsigned ? (bits == 64 ? ulong.MaxValue : (1UL << bits) - 1) : (1UL << (bits - 1)) - 1;
        return $"0x{value.ToString("X", CultureInfo.InvariantCulture)}u";
    }
}
