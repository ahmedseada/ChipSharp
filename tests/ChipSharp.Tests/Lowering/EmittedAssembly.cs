using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Tests.Lowering;

/// <summary>
/// An assembly written instruction by instruction with System.Reflection.Emit, for IL that C# does not produce:
/// nop, the long forms (ldarg, starg, ldloc, stloc, br, beq, ...), and broken stacks. Its static methods live in a
/// class named Program, and each parameter is named a, b, c, ...
/// </summary>
internal sealed class EmittedAssembly
{
    private readonly PersistedAssemblyBuilder _assembly;
    private readonly TypeBuilder _type;
    private byte[]? _image;

    public EmittedAssembly(string name = "Emitted")
    {
        Name = name;
        _assembly = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        _type = _assembly.DefineDynamicModule(name)
            .DefineType("Program", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
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

    /// <summary>The assembly's bytes; no method can be added after this.</summary>
    public byte[] Save()
    {
        if (_image is null)
        {
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
