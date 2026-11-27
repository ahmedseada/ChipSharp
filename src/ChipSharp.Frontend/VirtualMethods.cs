using System.Reflection;
using System.Runtime.CompilerServices;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Generics;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend;

/// <summary>The kinds of <see cref="DispatchSlot"/>.</summary>
internal enum DispatchSlotKind
{
    /// <summary>A virtual method of a class of the program: the one that opens the slot (virtual, abstract, new virtual).</summary>
    Class,

    /// <summary>One of System.Object's virtual methods: Equals(object) and GetHashCode() (and ToString(), with stored strings).</summary>
    Object,

    /// <summary>A method of an interface: of the program's (an instance of a generic one too) or of the library's.</summary>
    Interface,
}

/// <summary>
/// A slot a virtual call goes through: what a <c>callvirt</c> names, whose method each class fills in for itself. A
/// virtual method and every override of it share one slot; a <c>new virtual</c> method opens a slot of its own.
/// </summary>
/// <param name="Kind">What opens the slot.</param>
/// <param name="Owner">The class that opens it (its type), System.Object (<c>object</c>), or the interface.</param>
/// <param name="Name">The method's name, e.g. "Area" or "get_Sides".</param>
/// <param name="Signature">Its signature, with an interface's type arguments in place (<c>int32(int32)</c> for IComparable&lt;int&gt;).</param>
/// <param name="Opener">For a class's slot, the method that opens it; null for the others.</param>
internal sealed record DispatchSlot(DispatchSlotKind Kind, IlType Owner, string Name, IlMethodSignature Signature,
    IlMethod? Opener = null)
{
    /// <summary>The slot as C# names its method, e.g. "Shape.Area" or "System.IComparable&lt;int&gt;.CompareTo".</summary>
    public override string ToString() => $"{CSharpNames.Type(Owner)}.{Name}";
}

/// <summary>
/// What a slot runs on the objects of one class: a method of the program (<paramref name="Method"/>), or System.Object's
/// own Equals or GetHashCode (<paramref name="ObjectMethod"/>, its name), which ChipSharp gives itself.
/// </summary>
internal sealed record DispatchTarget(IlMethod? Method, string? ObjectMethod)
{
    public static DispatchTarget Of(IlMethod method) => new(method, null);

    public override string ToString() => Method?.ToString() ?? $"System.Object.{ObjectMethod}";
}

/// <summary>
/// Virtual methods and interfaces of the program's classes, as IL says them, for the whole-program pass and the
/// translation alike: which <c>callvirt</c> goes through a slot (<see cref="CalledSlot"/>), the slot of a virtual method
/// (<see cref="SlotOf"/>), and what a slot runs on an object of a class (<see cref="Resolve"/>), as .NET resolves it:
/// <list type="bullet">
/// <item>A class's slot: the most derived override in the class or its bases. An override is a virtual method that is
/// not <c>newslot</c> (C#'s <c>override</c>, <c>sealed override</c>) with the name and signature of a base class's
/// virtual method, or one a MethodImpl names (C#'s covariant return type).</item>
/// <item>System.Object's Equals and GetHashCode: an override of the class or a base, else object's own.</item>
/// <item>An interface's method: in the most derived class that lists the interface (C# lists every interface a class
/// implements, those the interface inherits too), its explicit implementation (<c>int IShape.Area()</c>), else its (or a
/// base class's) public method of that name and signature; then, when that method is virtual, what its slot runs on the
/// class (an override in a derived class). A class that implements none: the interface's default method, or an
/// interface's explicit override of it.</item>
/// </list>
/// </summary>
internal static partial class VirtualMethods
{
    private static readonly ConditionalWeakTable<IlMethod, DispatchSlot> Slots = new();

    private static readonly ConditionalWeakTable<IlTypeDefinition, IReadOnlyList<IlType>> InterfaceLists = new();

    private static readonly ConditionalWeakTable<IlMethodBody, bool[]> BlockStarts = new();

    private static readonly IlMethodSignature EqualsSignature = new(true, IlType.Bool, [IlType.Object]);

    private static readonly IlMethodSignature GetHashCodeSignature = new(true, IlType.Int32, []);

    private static readonly IlMethodSignature ToStringSignature = new(true, IlType.String, []);

    /// <summary>
    /// The library interfaces a class of the program may implement: those of values, and those of collections (generics
    /// over classes), over a type that can be a type argument.
    /// </summary>
    private static readonly HashSet<string> LibraryInterfaces =
        new(["System.IComparable", "System.IComparable`1", "System.IEquatable`1", "System.IDisposable",
            .. Generics.GenericSubstitutes.CollectionInterfaces], StringComparer.Ordinal);

    /// <summary>
    /// The slot the <c>callvirt</c> at <paramref name="index"/> of <paramref name="instructions"/> goes through: a
    /// virtual method of a class that is not sealed (<see cref="ClassRules.IsOverridable"/>), object's Equals or
    /// GetHashCode, or a virtual method of an interface. Null for any other instruction, a callvirt of a method that is
    /// not virtual (a plain call after the null check), one after <c>constrained.</c> (a call on a value) and a call of
    /// a generic virtual method.
    /// </summary>
    public static DispatchSlot? CalledSlot(IReadOnlyList<IlInstruction> instructions, int index) =>
        instructions[index] is { OpCode: IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } }
            && (index == 0 || instructions[index - 1].OpCode != IlOpCode.Constrained)
            ? SlotCalled(callee)
            : StringMethods.IsConsoleWriteOfObject(instructions[index]) ? ObjectSlot("ToString", ToStringSignature)
            : null;

    /// <summary>The slot a callvirt of <paramref name="callee"/> goes through (see <see cref="CalledSlot"/>), or null.</summary>
    public static DispatchSlot? SlotCalled(IlMethodReference callee)
    {
        ArgumentNullException.ThrowIfNull(callee);
        if (!callee.HasThis)
        {
            return null;
        }

        if (IsGenericVirtual(callee))
        {
            // a slot of its own for each instance the program calls (VirtualMethods.Generic.cs)
            return GenericSlotCalled(callee);
        }

        if (callee.Definition is { } method)
        {
            var type = method.DeclaringType;
            return type.IsInterface
                ? (method.Attributes & MethodAttributes.Virtual) != 0 ? InterfaceSlot(callee) : null
                : ObjectTypes.IsClass(type) && ClassRules.IsOverridable(method) ? SlotOf(method) : null;
        }

        return ObjectTypes.IsSystemObject(callee.DeclaringType) ? Callable(ObjectSlot(callee.Name, callee.Signature))
            : IsLibraryInterface(callee.DeclaringType) ? InterfaceSlot(callee)
            : null;
    }

    /// <summary>
    /// The slot of System.Object's Equals(object) or GetHashCode() when <paramref name="callee"/> is one of them (a
    /// <c>call</c> of it is C#'s <c>base.Equals(other)</c>, which runs object's own); null for any other method.
    /// </summary>
    public static DispatchSlot? ObjectSlotOf(IlMethodReference callee)
    {
        ArgumentNullException.ThrowIfNull(callee);
        return callee is { Definition: null, HasThis: true } && ObjectTypes.IsSystemObject(callee.DeclaringType)
            ? Callable(ObjectSlot(callee.Name, callee.Signature))
            : null;
    }

    /// <summary>
    /// True for a call of a generic virtual method (<c>virtual void Run&lt;T&gt;()</c>) or of a generic method of an
    /// interface: each type argument would need a slot of its own, which ChipSharp does not make.
    /// </summary>
    public static bool IsGenericVirtual(IlMethodReference callee)
    {
        ArgumentNullException.ThrowIfNull(callee);
        var method = callee.Definition;
        var generic = callee.GenericArguments.Count > 0 || method?.GenericParameters.Count > 0
            || method?.GenericDefinition?.GenericParameters.Count > 0;
        return generic && (method is null || (method.Attributes & MethodAttributes.Virtual) != 0);
    }

    /// <summary>
    /// The slot of a virtual method of a class: the slot of the method it overrides (of a base class, or object's Equals
    /// or GetHashCode), or its own when it opens one (<c>newslot</c>: C#'s virtual, abstract, new virtual).
    /// </summary>
    public static DispatchSlot SlotOf(IlMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return Slots.GetValue(method, FindSlot);
    }

    /// <summary>
    /// What <paramref name="slot"/> runs on an object of <paramref name="type"/>, a class of the program: null when the
    /// class has nothing for it (an abstract method, or a slot of another class or of an interface it does not implement).
    /// </summary>
    public static DispatchTarget? Resolve(IlTypeDefinition type, DispatchSlot slot)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Opener is { MethodArguments.Count: > 0 })
        {
            return ResolveGeneric(type, slot);
        }

        if (slot.Kind == DispatchSlotKind.Interface)
        {
            return ResolveInterface(type, slot);
        }

        for (IlTypeDefinition? current = type; current is not null; current = BaseOf(current))
        {
            foreach (var method in current.Methods)
            {
                if (method is { IsStatic: false } && method.Name == slot.Name
                    && (method.Attributes & MethodAttributes.Virtual) != 0 && SlotOf(method) == slot)
                {
                    return method.HasBody ? DispatchTarget.Of(method) : null;
                }
            }
        }

        return slot.Kind == DispatchSlotKind.Object ? new DispatchTarget(null, slot.Name) : null;
    }

    /// <summary>True when <paramref name="slot"/> is one an object of <paramref name="type"/> has.</summary>
    public static bool Has(IlTypeDefinition type, DispatchSlot slot) => slot.Kind switch
    {
        DispatchSlotKind.Class => IsDerived(type, slot.Opener!.DeclaringType),
        DispatchSlotKind.Interface => Implements(type, slot.Owner),
        _ => true,
    };

    /// <summary>True when <paramref name="type"/> is <paramref name="ancestor"/> or derives from it.</summary>
    public static bool IsDerived(IlTypeDefinition type, IlTypeDefinition ancestor)
    {
        for (IlTypeDefinition? current = type; current is not null; current = BaseOf(current))
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the class <paramref name="type"/> (or a base class of it) implements <paramref name="interfaceType"/>.</summary>
    public static bool Implements(IlTypeDefinition type, IlType interfaceType)
    {
        var plain = Canonical(interfaceType);
        for (IlTypeDefinition? current = type; current is not null; current = BaseOf(current))
        {
            if (InterfacesOf(current).Contains(plain))
            {
                return true;
            }
        }

        // or one it is by variance (VirtualMethods.Variance.cs): an IProducer<Dog> is an IProducer<Animal>
        return VariantInterface(type, plain) is not null;
    }

    /// <summary>
    /// True when an object whose type is <paramref name="type"/> may be in a variable of <paramref name="variable"/>: a
    /// class it derives from, an interface it implements, object (or a type not known here).
    /// </summary>
    public static bool Fits(IlTypeDefinition type, IlType? variable)
    {
        if (variable is null || ObjectTypes.IsSystemObject(variable))
        {
            return true;
        }

        return ObjectTypes.Class(variable, type.Assembly) is { } ancestor ? IsDerived(type, ancestor)
            : !IsInterface(variable, type.Assembly) || Implements(type, variable);
    }

    /// <summary>
    /// True for an interface a variable of the program may have, and an object implement: one of the program's that is
    /// not generic, an instance of a generic one over values (<c>IHandler&lt;int&gt;</c>), or one of the library's
    /// that values implement (IComparable, IComparable&lt;T&gt;, IEquatable&lt;T&gt; over values, IDisposable).
    /// </summary>
    public static bool IsInterface(IlType type, IlAssembly assembly)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        return ValueTypes.WithoutModifiers(type) switch
        {
            IlNamedType { IsDefinedInThisAssembly: true } named =>
                assembly.FindType(named.FullName) is { IsInterface: true, GenericParameters.Count: 0 },
            IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: true } } instance =>
                Instantiations.Type(instance, assembly) is { IsInterface: true },
            IlGenericInstanceType { Definition: IlNamedType library, Arguments.Count: > 0 } instance =>
                LibraryInterfaces.Contains(library.FullName) && Instantiations.Refusal(assembly, instance.Arguments) is null,
            IlNamedType library => LibraryInterfaces.Contains(library.FullName),
            _ => false,
        };
    }

    /// <summary>
    /// The name .NET gives a type, <c>Type.ToString()</c>: "Program+Node" for a class nested in Program, "Shapes.Circle",
    /// "IHandler`1[System.Int32]", "System.Int32[]"; what a descriptor holds as its name.
    /// </summary>
    public static string RuntimeName(IlType type) => ValueTypes.WithoutModifiers(type) switch
    {
        IlPrimitiveType { Kind: var kind } => "System." + kind switch
        {
            IlPrimitiveKind.Bool => "Boolean",
            IlPrimitiveKind.Int8 => "SByte",
            IlPrimitiveKind.UInt8 => "Byte",
            IlPrimitiveKind.Float32 => "Single",
            IlPrimitiveKind.Float64 => "Double",
            IlPrimitiveKind.NativeInt => "IntPtr",
            IlPrimitiveKind.NativeUInt => "UIntPtr",
            _ => kind.ToString(),
        },
        IlNamedType named => LibrarySubstitutes.SubstitutedName(named) ?? named.FullName,
        IlGenericInstanceType instance =>
            $"{RuntimeName(instance.Definition)}[{string.Join(",", instance.Arguments.Select(RuntimeName))}]",
        IlArrayType array => RuntimeName(array.ElementType) + "[]",
        var other => CSharpNames.Type(other),
    };

    /// <summary>
    /// The IL type of the value <paramref name="depth"/> deep (0: the top) on the evaluation stack before instruction
    /// <paramref name="index"/> of <paramref name="method"/>, as the instruction of the same block that put it there
    /// says: a local's, a parameter's (<c>this</c>: the method's type), a field's, a call's result, the class of a
    /// <c>new</c>, the type of a cast or <c>as</c>; null when it is not known (it came from another block, or from
    /// another instruction). What a virtual call is made on, for the whole-program pass and the translation alike: an
    /// object of a class that a variable of this type cannot hold is never there.
    /// </summary>
    public static IlType? StackType(IlMethod method, int index, int depth)
    {
        ArgumentNullException.ThrowIfNull(method);
        var instructions = method.Body!.Instructions;
        var starts = BlockStarts.GetValue(method.Body, FindBlockStarts);
        for (var i = index - 1; i >= 0 && !starts[i + 1]; i--)
        {
            var instruction = instructions[i];
            if (instruction.OpCode == IlOpCode.Dup)
            {
                // either copy is the value under it
                depth = Math.Max(depth - 1, 0);
                continue;
            }

            var (pops, pushes) = IlStackEffects.Of(instruction, method);
            if (depth < pushes)
            {
                return pushes == 1 ? PushedType(method, instruction) : null;
            }

            depth += pops - pushes;
        }

        return null;
    }

    /// <summary>The base class of a class of the program, or null for System.Object (or a library class, which is rejected apart).</summary>
    public static IlTypeDefinition? BaseOf(IlTypeDefinition type) =>
        type.BaseType is { } baseType && !ObjectTypes.IsSystemObject(baseType) ? ObjectTypes.Class(baseType, type.Assembly) : null;

    /// <summary>The IL type of what <paramref name="instruction"/> pushes, when it says; null otherwise.</summary>
    private static IlType? PushedType(IlMethod method, IlInstruction instruction) => instruction switch
    {
        { OpCode: >= IlOpCode.Ldloc_0 and <= IlOpCode.Ldloc_3 or IlOpCode.Ldloc_S or IlOpCode.Ldloc, LocalIndex: { } local }
            when local < method.Body!.Locals.Count => method.Body.Locals[local].Type,
        { OpCode: >= IlOpCode.Ldarg_0 and <= IlOpCode.Ldarg_3 or IlOpCode.Ldarg_S or IlOpCode.Ldarg, ArgumentIndex: { } argument } =>
            method.IsStatic ? Parameter(method, argument)
            : argument == 0 ? method.DeclaringType.Type
            : Parameter(method, argument - 1),
        { OpCode: IlOpCode.Ldfld or IlOpCode.Ldsfld, Operand: IlFieldOperand { Field: var field } } => field.FieldType,
        // (a library generic type's method names its result in terms of !0: IEnumerator<object>.Current is an object)
        { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } } =>
            Arguments(callee.DeclaringType).Apply(callee.ReturnType),
        { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } } => constructor.DeclaringType,
        { OpCode: IlOpCode.Isinst or IlOpCode.Castclass, Operand: IlTypeOperand { Type: var type } } => type,
        { OpCode: IlOpCode.Newarr, Operand: IlTypeOperand { Type: var element } } => new IlArrayType(StackArrays.Primitive(element)),
        _ => null,
    };

    private static IlType? Parameter(IlMethod method, int index) =>
        index < method.Parameters.Count ? method.Parameters[index].Type : null;

    /// <summary>For each instruction, true when a block starts there, as the translation cuts the body (see <see cref="Lowering.IlBlock"/>).</summary>
    private static bool[] FindBlockStarts(IlMethodBody body)
    {
        var instructions = body.Instructions;
        var starts = new bool[instructions.Count + 1];
        starts[0] = true;
        for (var i = 0; i < instructions.Count; i++)
        {
            foreach (var target in Lowering.IlBlock.Targets(instructions[i]))
            {
                starts[body.IndexOf(target)] = true;
            }

            starts[i + 1] |= Lowering.IlBlock.EndsBlock(instructions[i]);
        }

        return starts;
    }

    /// <summary>The interfaces the metadata says <paramref name="type"/> implements, without modifiers.</summary>
    private static IReadOnlyList<IlType> InterfacesOf(IlTypeDefinition type) => InterfaceLists.GetValue(type, own =>
        own.GenericDefinition is null
            ? [.. own.Assembly.Interfaces(own).Select(Canonical)]
            : [.. own.Assembly.Interfaces(own.GenericDefinition)
                .Select(i => Canonical(new Substitution(own.TypeArguments, []).Apply(i)))]);

    private static DispatchSlot FindSlot(IlMethod method)
    {
        if (method.MethodArguments.Count > 0)
        {
            return GenericSlot(method);
        }

        var type = method.DeclaringType;

        // C#'s covariant return type: a newslot method that names the method it overrides in a MethodImpl
        foreach (var (body, declaration) in type.Assembly.MethodImplementations(type.GenericDefinition ?? type))
        {
            if ((body == method || body == method.GenericDefinition) && declaration.Definition is { } overridden
                && ObjectTypes.IsClass(overridden.DeclaringType))
            {
                // of a generic class, the method of the base class's instance (a declaration in terms of !0 names its own)
                var baseClass = ObjectTypes.Class(new Substitution(type.TypeArguments, []).Apply(declaration.DeclaringType),
                    type.Assembly);
                return SlotOf(baseClass is not null && overridden.GenericDefinition is null && Instantiations.IsGeneric(overridden)
                    ? Instantiations.MethodOf(baseClass, overridden)
                    : overridden);
            }
        }

        if ((method.Attributes & MethodAttributes.NewSlot) == 0)
        {
            for (var baseClass = BaseOf(type); baseClass is not null; baseClass = BaseOf(baseClass))
            {
                var overridden = baseClass.Methods.FirstOrDefault(candidate => candidate is { IsStatic: false }
                    && (candidate.Attributes & MethodAttributes.Virtual) != 0 && candidate.Name == method.Name
                    && candidate.Signature == method.Signature);
                if (overridden is not null)
                {
                    return SlotOf(overridden);
                }
            }

            if (ObjectSlot(method.Name, method.Signature) is { } objects)
            {
                return objects;
            }
        }

        return new DispatchSlot(DispatchSlotKind.Class, type.Type, method.Name, method.Signature, method);
    }

    /// <summary>
    /// The slot of System.Object's Equals(object), GetHashCode() or ToString(), which the classes' overrides of them
    /// fill in; null for its other methods.
    /// </summary>
    private static DispatchSlot? ObjectSlot(string name, IlMethodSignature signature) => (name, signature) switch
    {
        ("Equals", var equals) when equals == EqualsSignature =>
            new DispatchSlot(DispatchSlotKind.Object, IlType.Object, name, EqualsSignature),
        ("GetHashCode", var hash) when hash == GetHashCodeSignature =>
            new DispatchSlot(DispatchSlotKind.Object, IlType.Object, name, GetHashCodeSignature),
        ("ToString", var text) when text == ToStringSignature =>
            new DispatchSlot(DispatchSlotKind.Object, IlType.Object, name, ToStringSignature),
        _ => null,
    };

    /// <summary>
    /// <paramref name="slot"/>: ToString's too, now that strings are objects (object's own gives the type's name, see
    /// ProgramFunctions.Strings.cs).
    /// </summary>
    private static DispatchSlot? Callable(DispatchSlot? slot) => slot;

    /// <summary>The slot of a method of an interface, with the interface's type arguments in its signature.</summary>
    private static DispatchSlot InterfaceSlot(IlMethodReference callee)
    {
        var owner = Canonical(callee.DeclaringType);
        return new DispatchSlot(DispatchSlotKind.Interface, owner, callee.Name, Arguments(owner).Apply(callee.Signature));
    }

    /// <summary>The type arguments of an instance of a generic interface, for its <c>!0</c>, <c>!1</c>, ...</summary>
    private static Substitution Arguments(IlType type) =>
        new(type is IlGenericInstanceType { Arguments: var arguments } ? arguments : [], []);

    /// <summary>True for a library interface that values implement (see <see cref="IsInterface"/>).</summary>
    private static bool IsLibraryInterface(IlType type) => ValueTypes.WithoutModifiers(type) switch
    {
        IlNamedType { IsDefinedInThisAssembly: false } named => LibraryInterfaces.Contains(named.FullName),
        IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: false } named } =>
            LibraryInterfaces.Contains(named.FullName),
        _ => false,
    };

    private static DispatchTarget? ResolveInterface(IlTypeDefinition type, DispatchSlot slot)
    {
        for (IlTypeDefinition? lister = type; lister is not null; lister = BaseOf(lister))
        {
            if (!InterfacesOf(lister).Contains(slot.Owner))
            {
                continue;
            }

            foreach (var (body, declaration) in lister.Assembly.MethodImplementations(lister.GenericDefinition ?? lister))
            {
                if (Implements(declaration, slot, lister))
                {
                    return Final(type, Instantiations.MethodOf(lister, body));
                }
            }

            for (IlTypeDefinition? current = lister; current is not null; current = BaseOf(current))
            {
                var implicitly = current.Methods.FirstOrDefault(method => method is { IsStatic: false }
                    && (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
                    && method.Name == slot.Name && method.Signature == slot.Signature);
                if (implicitly is not null)
                {
                    return Final(type, implicitly);
                }
            }
        }

        return VariantInterface(type, slot.Owner) is { } implemented
            ? ResolveVariant(type, slot, implemented)
            : DefaultMethod(type, slot);
    }

    /// <summary>
    /// A default interface method for <paramref name="slot"/> on <paramref name="type"/>, which implements the interface
    /// without a method of its own for it: an interface's explicit override of it (<c>void IShape.Draw() { }</c> in an
    /// interface derived from IShape), else the interface's own body; null when there is neither.
    /// </summary>
    private static DispatchTarget? DefaultMethod(IlTypeDefinition type, DispatchSlot slot)
    {
        var assembly = type.Assembly;
        var interfaces = new List<IlType>();
        for (IlTypeDefinition? current = type; current is not null; current = BaseOf(current))
        {
            interfaces.AddRange(InterfacesOf(current).Where(i => !interfaces.Contains(i)));
        }

        foreach (var other in interfaces.Where(i => i != slot.Owner))
        {
            if (InterfaceDefinition(other, assembly) is { } definition)
            {
                foreach (var (body, declaration) in assembly.MethodImplementations(definition.GenericDefinition ?? definition))
                {
                    if (Implements(declaration, slot, definition) && body.HasBody)
                    {
                        return DispatchTarget.Of(Instantiations.MethodOf(definition, body));
                    }
                }
            }
        }

        return InterfaceDefinition(slot.Owner, assembly)?.Methods.FirstOrDefault(method => method is { IsStatic: false, HasBody: true }
            && method.Name == slot.Name && method.Signature == slot.Signature) is { } own
            ? DispatchTarget.Of(own)
            : null;
    }

    /// <summary>The program's interface (or the instance of a generic one) that <paramref name="type"/> names; null for a library one.</summary>
    private static IlTypeDefinition? InterfaceDefinition(IlType type, IlAssembly assembly) => type switch
    {
        IlNamedType { IsDefinedInThisAssembly: true } named => assembly.FindType(named.FullName),
        IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: true } } instance =>
            Instantiations.Type(instance, assembly),
        _ => null,
    };

    /// <summary>
    /// True when the interface method a MethodImpl of <paramref name="implementer"/> names is <paramref name="slot"/>'s:
    /// of a generic type's, in terms of its type parameters, which the instance's type arguments take the place of.
    /// </summary>
    private static bool Implements(IlMethodReference declaration, DispatchSlot slot, IlTypeDefinition implementer)
    {
        var owner = Canonical(new Substitution(implementer.TypeArguments, []).Apply(declaration.DeclaringType));
        return owner == slot.Owner && declaration.Name == slot.Name
            && Arguments(owner).Apply(declaration.Signature) == slot.Signature;
    }

    /// <summary>What an implementation runs on an object of <paramref name="type"/>: itself, or when it is virtual, its slot's method there.</summary>
    private static DispatchTarget? Final(IlTypeDefinition type, IlMethod implementation) =>
        ClassRules.IsOverridable(implementation) ? Resolve(type, SlotOf(implementation))
        : implementation.HasBody ? DispatchTarget.Of(implementation)
        : null;
}
