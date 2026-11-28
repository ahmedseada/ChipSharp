using System.Globalization;
using System.Text;
using ChipSharp.Abstractions;
using ChipSharp.Ir;

namespace ChipSharp.Backend.C;

/// <summary>
/// The C type of each TIR type for one target, in fixed-width types only, so that nothing depends on the width of the
/// compiler's <c>int</c> (16 bits with SDCC): every integer, signless in TIR, is the unsigned type of its width
/// (<c>uint8_t</c> to <c>uint64_t</c>; nint the one as wide as a pointer), which wraps around as C#'s arithmetic does;
/// an operation that reads its operands as signed casts them to the signed type of the width (see
/// <see cref="CFunctionWriter"/>). bool is a <c>uint8_t</c> holding 0 or 1, f32 a <c>float</c>, f64 a <c>double</c>, and
/// ptr and ref are byte addresses, <c>uint8_t *</c>, which every load and store casts to the type it reads or writes.
/// <para>
/// A struct is a C struct of its fields in order, <c>struct Program_Point</c>; an array, which C cannot pass, return or
/// assign as a value, is a struct around the C array, <c>struct cs_a4_i32 { uint32_t e[4]; }</c>, so that TIR's array
/// values (a slot of a fixed buffer, an array loaded and stored whole) are C values too. The layout is
/// <see cref="TirLayout"/>'s, which is how C lays these out on the targets ChipSharp supports (natural alignment, up to
/// the target's most alignment); the file checks that it is, for every struct and array, when it is compiled
/// (<see cref="Checks"/>), so a compiler that lays one out otherwise stops with the struct's name rather than running a
/// program that reads the wrong bytes.
/// </para>
/// </summary>
internal sealed class CTypes
{
    private readonly Dictionary<TirType, string> _names = [];
    private readonly Dictionary<(TirStructType, int), string> _fields = [];
    private readonly List<TirType> _aggregates = [];

    public CTypes(TirLayout layout)
    {
        Layout = layout;
    }

    /// <summary>Sizes, alignments and field offsets on the target.</summary>
    public TirLayout Layout { get; }

    /// <summary>The width of a pointer (and nint) in bits: 16, 32 or 64.</summary>
    public int PointerBits => Layout.PointerSize * 8;

    /// <summary>The C integer as wide as a pointer, unsigned: what nint is, and what addresses compare as.</summary>
    public string PointerInteger => $"uint{PointerBits.ToString(CultureInfo.InvariantCulture)}_t";

    /// <summary>
    /// Names every struct and array type the module uses (and the arrays inside them), the structs first, each taking
    /// its C# name made a C name (<c>Program_Point</c>), the arrays <c>cs_a</c>, their count and their element's name
    /// (<c>cs_a4_i32</c>, <c>cs_a2_Program_Point</c>), with _2, _3, ... after a name that is taken; the names are struct
    /// tags, which do not clash with the names of functions and variables.
    /// </summary>
    public void Name(TirModule module, ISet<string> tags)
    {
        foreach (var type in Collect(module))
        {
            _aggregates.Add(type);
        }

        foreach (var structType in _aggregates.OfType<TirStructType>())
        {
            _names[structType] = Unique(tags, CText.Identifier(structType.Name));
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in structType.Fields)
            {
                var wanted = CText.Identifier(field.Name, "f");
                _fields[(structType, field.Index)] = Unique(members, wanted);
            }
        }

        foreach (var array in _aggregates.OfType<TirArrayType>())
        {
            _names[array] = Unique(tags, "cs_" + ArrayPart(array));
        }
    }

    /// <summary>The C type of a value of <paramref name="type"/>, e.g. <c>uint32_t</c>, <c>uint8_t *</c>, <c>struct Program_Point</c>.</summary>
    public string Of(TirType type) => type switch
    {
        TirStructType or TirArrayType => _names.TryGetValue(type, out var name)
            ? "struct " + name
            : throw new ChipSharpException($"The type {type} was not named for this module."),
        _ => type.Kind switch
        {
            TirTypeKind.Void => "void",
            TirTypeKind.Bool or TirTypeKind.I8 => "uint8_t",
            TirTypeKind.F32 => "float",
            TirTypeKind.F64 => "double",
            TirTypeKind.Ptr or TirTypeKind.Ref => "uint8_t *",
            _ => $"uint{Bits(type).ToString(CultureInfo.InvariantCulture)}_t",
        },
    };

    /// <summary>The signed C type of an integer's width, e.g. <c>int32_t</c> for i32, which signed operations cast to.</summary>
    public string Signed(TirType type) => $"int{Bits(type).ToString(CultureInfo.InvariantCulture)}_t";

    /// <summary>The width in bits of a scalar: 8 for bool, 32 for i32 and f32, the pointer width for nint, ptr and ref.</summary>
    public int Bits(TirType type) => type.Kind switch
    {
        TirTypeKind.Bool or TirTypeKind.I8 => 8,
        TirTypeKind.I16 => 16,
        TirTypeKind.I32 or TirTypeKind.F32 => 32,
        TirTypeKind.I64 or TirTypeKind.F64 => 64,
        TirTypeKind.NativeInt or TirTypeKind.Ptr or TirTypeKind.Ref => PointerBits,
        _ => throw new ChipSharpException($"The c code generator has no bit width for the TIR type {type}."),
    };

    /// <summary>A declaration of <paramref name="name"/> with the C type of <paramref name="type"/>: <c>uint8_t *p</c>, <c>uint32_t n</c>.</summary>
    public string Declare(TirType type, string name) => Declare(Of(type), name);

    /// <summary>A declaration of <paramref name="name"/> as <paramref name="cType"/>, with no space after a <c>*</c>.</summary>
    public static string Declare(string cType, string name) => cType.EndsWith('*') ? cType + name : cType + " " + name;

    /// <summary>The C name of field <paramref name="index"/> of <paramref name="structType"/>, e.g. <c>X</c>, <c>f_type_</c>.</summary>
    public string Field(TirStructType structType, int index) => _fields[(structType, index)];

    /// <summary>The member of an array's struct that is the C array: <c>e</c>, as in <c>s_digits.e[3]</c>.</summary>
    public const string ElementsMember = "e";

    /// <summary>The definitions of the module's structs and arrays, those a type holds before it.</summary>
    public void WriteDefinitions(StringBuilder text)
    {
        foreach (var type in _aggregates)
        {
            text.Append(Of(type)).Append('\n').Append("{\n");
            if (type is TirStructType structType)
            {
                foreach (var field in structType.Fields)
                {
                    text.Append("    ").Append(Declare(field.Type, Field(structType, field.Index))).Append(";\n");
                }
            }
            else
            {
                var array = (TirArrayType)type;
                text.Append("    ").Append(Declare(array.Element, ElementsMember))
                    .Append(CultureInfo.InvariantCulture, $"[{array.Count}];\n");
            }

            text.Append("};\n\n");
        }
    }

    /// <summary>
    /// The checks that the compiler lays every struct and array out as <see cref="Layout"/> does, one typedef each, which
    /// is an array of size -1 (an error that names it) when the compiler does not: e.g.
    /// <c>typedef char cs_layout_Program_Point[sizeof(struct Program_Point) == 8 &amp;&amp; offsetof(struct Program_Point, Y) == 4 ? 1 : -1];</c>
    /// </summary>
    public IEnumerable<string> Checks(ISet<string> names)
    {
        foreach (var type in _aggregates)
        {
            var cType = Of(type);
            var conditions = new List<string> { $"sizeof({cType}) == {Layout.SizeOf(type).ToString(CultureInfo.InvariantCulture)}" };
            if (type is TirStructType structType)
            {
                var offsets = Layout.OffsetsOf(structType);
                conditions.AddRange(structType.Fields.Skip(1).Select(field =>
                    $"offsetof({cType}, {Field(structType, field.Index)}) == " +
                    offsets[field.Index].ToString(CultureInfo.InvariantCulture)));
            }

            yield return Check(names, "cs_layout_" + _names[type], conditions);
        }
    }

    /// <summary>
    /// A check of <paramref name="conditions"/> when the file is compiled, as C99 allows one: a typedef of an array whose
    /// size is -1, an error that names <paramref name="name"/>, when one is false.
    /// </summary>
    public static string Check(ISet<string> names, string name, IReadOnlyList<string> conditions)
    {
        var condition = conditions.Count == 1 ? conditions[0] : string.Join("\n    && ", conditions);
        return $"typedef char {Unique(names, name)}[{condition} ? 1 : -1];";
    }

    /// <summary><paramref name="wanted"/>, or with _2, _3, ... after it when that is taken; the name returned is taken.</summary>
    public static string Unique(ISet<string> taken, string wanted)
    {
        var name = wanted;
        for (var n = 2; !taken.Add(name); n++)
        {
            name = wanted + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    /// <summary>The part of an array's name after cs_: a4_i32, a2_Program_Point, a2_a3_i8.</summary>
    private string ArrayPart(TirArrayType array) =>
        $"a{array.Count.ToString(CultureInfo.InvariantCulture)}_" + array.Element switch
        {
            TirArrayType inner => ArrayPart(inner),
            TirStructType structType => _names[structType],
            var scalar => scalar.Name,
        };

    /// <summary>
    /// Every struct and array type the module uses, each once, those a type holds first: what
    /// <see cref="TirModule.FindStructTypes"/> finds, with the arrays too.
    /// </summary>
    private static List<TirType> Collect(TirModule module)
    {
        var found = new List<TirType>();
        var seen = new HashSet<TirType>();

        void Visit(TirType type)
        {
            switch (type)
            {
                case TirArrayType array when seen.Add(array):
                    Visit(array.Element);
                    found.Add(array);
                    break;

                case TirStructType structType when seen.Add(structType):
                    foreach (var field in structType.Fields)
                    {
                        Visit(field.Type);
                    }

                    found.Add(structType);
                    break;
            }
        }

        foreach (var structType in module.FindStructTypes())
        {
            Visit(structType);
        }

        foreach (var global in module.Globals)
        {
            Visit(global.Type);
        }

        foreach (var function in module.Functions)
        {
            Visit(function.ReturnType);
            foreach (var parameter in function.Parameters)
            {
                Visit(parameter.Type);
            }

            foreach (var slot in function.Slots)
            {
                Visit(slot.Type);
            }

            foreach (var instruction in function.Blocks.SelectMany(block => block.Instructions))
            {
                Visit(instruction.Type);
                switch (instruction)
                {
                    case TirElementAddress element:
                        Visit(element.ElementType);
                        break;

                    case TirArrayData data:
                        Visit(data.ElementType);
                        break;

                    case TirPointerLoad load:
                        Visit(load.Type);
                        break;
                }

                foreach (var operand in instruction.Operands)
                {
                    Visit(operand.Type);
                }
            }
        }

        return found;
    }
}
