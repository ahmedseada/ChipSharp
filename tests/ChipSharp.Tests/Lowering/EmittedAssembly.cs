using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// An assembly written instruction by instruction with System.Reflection.Emit, for IL that C# does not produce:
/// nop, the long forms (ldarg, starg, ldloc, stloc, br, beq, ...), and broken stacks. Its static methods live in a
/// class named Program, and each parameter is named a, b, c, ...; it may have structs of its own too.
/// </summary>
internal sealed class EmittedAssembly
{
    private readonly PersistedAssemblyBuilder _assembly;
    private readonly ModuleBuilder _module;
    private readonly TypeBuilder _type;
    private readonly List<TypeBuilder> _structs = [];
    private byte[]? _image;

    public EmittedAssembly(string name = "Emitted")
    {
        Name = name;
        _assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        _module = _assembly.DefineDynamicModule(name);
        _type = _module.DefineType("Program", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
    }

    public string Name { get; }

    /// <summary>Adds <c>static <paramref name="returnType"/> Program.<paramref name="name"/>(...)</c> with the IL that <paramref name="body"/> writes.</summary>
    public MethodBuilder Method(string name, Type returnType, Type[] parameters, Action<ILGenerator> body)
    {
        var method = _type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, returnType, parameters);
        for (var i = 0; i < parameters.Length; i++)
        {
            method.DefineParameter(i + 1, ParameterAttributes.None, ((char)('a' + i)).ToString());
        }

        body(method.GetILGenerator());
        return method;
    }

    /// <summary>
    /// Adds <c>static <paramref name="type"/> Program.<paramref name="name"/></c>; with <paramref name="isVolatile"/>,
    /// its type carries the modifier C# gives a volatile field, <c>modreq(IsVolatile)</c>.
    /// </summary>
    public FieldBuilder Field(string name, Type type, bool isVolatile = false) =>
        _type.DefineField(name, type, isVolatile ? [typeof(System.Runtime.CompilerServices.IsVolatile)] : null, null,
            FieldAttributes.Public | FieldAttributes.Static);

    /// <summary>
    /// Adds a static field of Program whose bytes are <paramref name="data"/> in the image (at its RVA), as C# keeps
    /// the constants of an array initializer; <c>ldtoken</c> it for <c>RuntimeHelpers.InitializeArray</c>.
    /// </summary>
    public FieldBuilder Data(string name, byte[] data) =>
        _type.DefineInitializedData(name, data, FieldAttributes.Private | FieldAttributes.Static);

    /// <summary>
    /// Adds a struct named <paramref name="name"/>, laid out as C# lays out structs (sequential); its fields, constructors
    /// and methods are defined on what this returns.
    /// </summary>
    public TypeBuilder Struct(string name)
    {
        var type = _module.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            typeof(ValueType));
        _structs.Add(type);
        return type;
    }

    /// <summary>
    /// Adds a class named <paramref name="name"/>, derived from object; its fields, constructors and methods are defined
    /// on what this returns.
    /// </summary>
    public TypeBuilder Class(string name)
    {
        var type = _module.DefineType(name, TypeAttributes.Public | TypeAttributes.Class, typeof(object));
        _structs.Add(type);
        return type;
    }

    /// <summary>The assembly's bytes; no method can be added after this.</summary>
    public byte[] Save()
    {
        if (_image is null)
        {
            foreach (var type in _structs)
            {
                type.CreateType();
            }

            _type.CreateType();
            using var stream = new MemoryStream();
            _assembly.Save(stream);
            _image = stream.ToArray();
        }

        return _image;
    }

    /// <summary>The assembly as ChipSharp reads it, from the file name "&lt;Name&gt;.dll".</summary>
    public IlAssembly Read() => IlAssembly.Load(Save(), Name + ".dll");

    /// <summary>Loads the assembly into .NET itself (in a context of its own, unloaded with it) to run its methods.</summary>
    public Assembly LoadIntoDotnet() =>
        new AssemblyLoadContext(Name, isCollectible: true).LoadFromStream(new MemoryStream(Save()));
}
