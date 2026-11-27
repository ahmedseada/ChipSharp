namespace ChipSharp.Ir;

/// <summary>The kinds of <see cref="TirTypeDescriptor"/>.</summary>
public enum TirTypeDescriptorKind
{
    /// <summary>A class: its objects are laid out as its <see cref="TirTypeDescriptor.ObjectType"/>.</summary>
    Class,

    /// <summary>An array on the heap: a header, a length and its elements (see <see cref="TirLayout.ArrayDataOffset"/>).</summary>
    Array,

    /// <summary>An interface: no object is ever of it; classes name it in their <see cref="TirTypeDescriptor.Interfaces"/>.</summary>
    Interface,
}

/// <summary>
/// What the program and the garbage collector know about one type of object on the heap, printed at the top of a module
/// (<c>type Program.Node = class Program.Node : System.Object</c>) and named by the instructions that use it
/// (<see cref="TirNewObject"/>, <see cref="TirNewArray"/>, <see cref="TirTypeAddress"/>). Created with
/// <see cref="TirModule.AddClass"/>, <see cref="TirModule.AddArrayType"/> and <see cref="TirModule.AddInterface"/>; one
/// per C# type, as the type of an object is the address of its descriptor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Objects.</b> Every object starts with its header: one pointer-sized word, the address of its type's descriptor.
/// A class's object type is a struct whose first field is that header (<see cref="HeaderField"/>, a ptr), then the
/// fields of its base class in their order, then its own (<see cref="ObjectStruct"/> makes one), so the fields of a
/// base class are where they are in every class derived from it. An array object is the header, its length (i32) at
/// <see cref="TirLayout.ArrayLengthOffset"/> and its elements from <see cref="TirLayout.ArrayDataOffset"/> on. The heap
/// gives every object an address that is a multiple of 8, with every byte zero but the header.
/// </para>
/// <para>
/// <b>The descriptor in memory</b>, as the code generator writes it (read-only data: flash on a chip) and the runtime
/// reads it (<c>chipsharp_type</c> in src/ChipSharp.Runtime.Shared/chipsharp_heap.c), laid out like a C struct:
/// <code>
/// u32  size              a class: the size of its objects, header included; an array: ArrayDataOffset
/// u32  element_size      an array: the size of one element; 0 for a class and an interface
/// u32  flags             1: an array, 2: an interface
/// u32  reference_count   how many offsets references lists
/// ptr  references        the u32 offsets of the refs in an object (a class) or in one element (an array), from
///                        TirLayout.ReferenceOffsets; null when there are none
/// ptr  base              the descriptor of the base class (Base), or of the element type of an array of refs
///                        (ElementDescriptor); null for none
/// ptr  name              the name (RuntimeName, else Name) as UTF-8 ending with a 0 byte, e.g. for an
///                        InvalidCastException's message
/// ptr  interfaces        the interface map: interface_count pairs { ptr interface descriptor, ptr method table },
///                        each method table the addresses of the methods in the order the interface declares them
/// u32  interface_count
/// u32  virtual_count
/// ptr  virtuals[]        the virtual method table: the address of each of VirtualMethods (null for an empty slot),
///                        right after virtual_count; slot i is at 4 * 4 + 4 * pointer size + 2 * 4 + i * pointer size
/// </code>
/// The garbage collector reads size, element_size, flags and the references: in an object it follows exactly the refs
/// the references list, so a ptr field (fixed text, an address in flash) is never followed. The rest is for the
/// program: casts and type tests (base, interfaces, name) and virtual calls (virtuals), which the program fills in with
/// <see cref="VirtualMethods"/> and <see cref="Interfaces"/>.
/// </para>
/// <para>
/// <b>Marks.</b> While the collector runs it marks an object by setting bit 0 of its header (a descriptor's address is
/// a multiple of 4, so the bit is free), and clears it again before it ends: outside a collection a header is exactly
/// the descriptor's address, which the program may load (<see cref="TirTypeOf"/>) and compare.
/// </para>
/// </remarks>
public sealed class TirTypeDescriptor
{
    /// <summary>The name of the header field, the first field of every object type: the address of the descriptor.</summary>
    public const string HeaderField = "<type>";

    /// <summary>The field of <see cref="Layout"/> that holds the flags (an i32): 1 for an array, 2 for an interface.</summary>
    public const int FlagsField = 2;

    /// <summary>The field of <see cref="Layout"/> that holds the base (a ptr).</summary>
    public const int BaseField = 5;

    /// <summary>The field of <see cref="Layout"/> that holds the name (a ptr to UTF-8 ending with a 0 byte).</summary>
    public const int NameField = 6;

    /// <summary>The field of <see cref="Layout"/> that holds the interface map (a ptr to <see cref="InterfaceEntry"/>s).</summary>
    public const int InterfacesField = 7;

    /// <summary>The field of <see cref="Layout"/> that holds how many entries the interface map has (an i32).</summary>
    public const int InterfaceCountField = 8;

    /// <summary>The field of <see cref="Layout"/> where the virtual method table starts: slot i is its element i.</summary>
    public const int VirtualsField = 10;

    /// <summary>
    /// A descriptor in memory as a struct, for code that reads descriptors through their address (<see cref="TirTypeOf"/>,
    /// <see cref="TirTypeAddress"/>): <c>fieldaddr &lt;type descriptor&gt; %type, 5</c> is the address of the base
    /// (<see cref="BaseField"/>), and <c>elementaddr ptr</c> on the address of <see cref="VirtualsField"/> that of a slot
    /// of the virtual method table (the struct gives the table one slot; it has as many as the class has).
    /// </summary>
    public static TirStructType Layout { get; } = new("<type descriptor>",
        ("size", TirType.I32), ("element_size", TirType.I32), ("flags", TirType.I32), ("reference_count", TirType.I32),
        ("references", TirType.Ptr), ("base", TirType.Ptr), ("name", TirType.Ptr), ("interfaces", TirType.Ptr),
        ("interface_count", TirType.I32), ("virtual_count", TirType.I32), ("virtuals", new TirArrayType(TirType.Ptr, 1)));

    /// <summary>An entry of an interface map in memory: the interface's descriptor (field 0), then its method table (field 1).</summary>
    public static TirStructType InterfaceEntry { get; } = new("<interface entry>",
        ("interface", TirType.Ptr), ("methods", TirType.Ptr));

    internal TirTypeDescriptor(TirModule module, int index, string name, TirTypeDescriptorKind kind,
        TirStructType? objectType, TirType? elementType, TirTypeDescriptor? baseType)
    {
        Module = module;
        Index = index;
        Name = name;
        Kind = kind;
        ObjectType = objectType;
        ElementType = elementType;
        Base = baseType;
    }

    public TirModule Module { get; }

    /// <summary>Position in <see cref="TirModule.TypeDescriptors"/>, from 0.</summary>
    public int Index { get; }

    /// <summary>The C# name, e.g. "Program.Node", "int[]" or "Program.IShape"; unique among the module's descriptors.</summary>
    public string Name { get; }

    /// <summary>
    /// The name .NET gives the type (<c>Type.ToString()</c>), e.g. "Program+Node" for a class nested in Program, or
    /// "IGen`1[System.Int32]": what the descriptor's name holds in memory, for an InvalidCastException's message and for
    /// an object printed as .NET prints it. Null (the default) for <see cref="Name"/>.
    /// </summary>
    public string? RuntimeName { get; set; }

    public TirTypeDescriptorKind Kind { get; }

    /// <summary>A class's object type: its header (<see cref="HeaderField"/>), then its fields. Null for an array and an interface.</summary>
    public TirStructType? ObjectType { get; }

    /// <summary>An array's element type, e.g. i32, ref, or a struct. Null for a class and an interface.</summary>
    public TirType? ElementType { get; }

    /// <summary>A class's base class, whose object type's fields are the first of <see cref="ObjectType"/>; null for none.</summary>
    public TirTypeDescriptor? Base { get; }

    /// <summary>
    /// For an array whose elements are refs: the descriptor of the element type (a class, an interface or an array), for
    /// casts and type tests of arrays; null when not known or not needed.
    /// </summary>
    public TirTypeDescriptor? ElementDescriptor { get; set; }

    /// <summary>
    /// The symbol of a descriptor that the runtime defines, laid out as above (e.g. <c>chipsharp_string_type</c>, a
    /// string's, see src/ChipSharp.Runtime.Shared/chipsharp_string.c): the code generator then only names it, so that the
    /// runtime's C and the program share one descriptor; null (the default) for one the code generator writes.
    /// </summary>
    public string? ExternalSymbol { get; set; }

    /// <summary>
    /// A class's virtual method table, slot by slot: the function each slot calls (null for a slot nothing can call,
    /// such as an abstract method's in an abstract class). Empty until the program fills it in.
    /// </summary>
    public IList<TirFunction?> VirtualMethods { get; } = new List<TirFunction?>();

    /// <summary>A class's interface map: for each interface it implements, the function of each of the interface's methods.</summary>
    public IList<TirInterfaceMethods> Interfaces { get; } = new List<TirInterfaceMethods>();

    /// <summary>True for an array: its objects have a length and elements (<see cref="ElementType"/>).</summary>
    public bool IsArray => Kind == TirTypeDescriptorKind.Array;

    /// <summary>
    /// The object type of a class: a struct named <paramref name="name"/> with the header first, then
    /// <paramref name="fields"/> (the base class's fields, then the class's own).
    /// </summary>
    public static TirStructType ObjectStruct(string name, params IEnumerable<(string Name, TirType Type)> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new TirStructType(name, [(HeaderField, TirType.Ptr), .. fields]);
    }

    public override string ToString() => Name;
}

/// <summary>
/// One entry of a class's interface map (<see cref="TirTypeDescriptor.Interfaces"/>): an interface, and the function
/// that each of its methods calls on objects of the class, in the order the interface declares them.
/// </summary>
public sealed record TirInterfaceMethods(TirTypeDescriptor Interface, IReadOnlyList<TirFunction?> Methods);
