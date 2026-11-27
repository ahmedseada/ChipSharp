using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// What generic code does through a T that is a reference type (generics over classes, see
/// <see cref="ReferenceArguments"/>), made what the same code written for that type does, as .NET runs it:
/// <list type="bullet">
/// <item><c>box T</c> is nothing: an object is one already (C# writes it before a call through a <c>where T : class</c>
/// or <c>where T : Base</c> constraint, a test against null and an <c>is</c>); <c>unbox.any T</c> (<c>(T)o</c>) is a
/// cast, <c>castclass</c>, and after an <c>isinst</c> of T (<c>o is T t</c>) nothing;</item>
/// <item><c>constrained. T callvirt M</c> is <c>callvirt M</c> on the object the address points to: the instruction
/// that put the address there (<c>ldarga</c>, <c>ldloca</c>, <c>ldflda</c>, <c>ldsflda</c>, <c>ldelema</c>) loads the
/// object instead;</item>
/// <item><c>initobj T</c> (C#'s <c>default</c>) stores null where its address came from;</item>
/// <item><c>new T()</c> (<c>Activator.CreateInstance&lt;T&gt;()</c>) is <c>newobj</c> of T's constructor without
/// parameters, on a struct that has one too; on any other value it is <c>default(T)</c>.</item>
/// </list>
/// And on a value T, a test against null (<c>box T</c>, <c>ldnull</c>, <c>ceq</c> or <c>cgt.un</c>) is false or true:
/// a value is never null. Each is replaced in the bytes it took, like the rest of <see cref="Specialization"/>.
/// </summary>
internal static partial class Specialization
{
    /// <summary>ChipSharp.Library's generic helpers that generic code over a type calls (Defaults.Of&lt;T&gt;: default(T)).</summary>
    private const string DefaultsType = "ChipSharp.Library.Defaults";

    /// <summary>ChipSharp.Library's comparisons of the default comparers (EqualityComparer&lt;T&gt;.Default, Comparer&lt;T&gt;.Default).</summary>
    private const string EqualityType = "ChipSharp.Library.Equality";

    /// <summary>
    /// Rewrites the instruction at <paramref name="index"/> (and those it goes with) when it does something through a
    /// T that is a reference type, or tests a value against null; true when it did.
    /// </summary>
    private static bool RewriteReference(IlAssembly owner, List<IlInstruction> instructions, int index, IlMethod instance,
        HashSet<int> targets)
    {
        var instruction = instructions[index];
        var next = index + 1 < instructions.Count ? instructions[index + 1] : null;
        var previous = index > 0 ? instructions[index - 1] : null;
        switch (instruction)
        {
            // (box of text is the generics' own: before Console.WriteLine(object) it prints text)
            case { OpCode: IlOpCode.Box, Operand: IlTypeOperand { Type: var boxed } }
                when IsReference(boxed, owner) && !FixedText.IsText(StackArrays.Primitive(boxed)):
                return Replaced(instructions, index, 1, [], targets);

            case { OpCode: IlOpCode.Box, Operand: IlTypeOperand { Type: var boxed } }
                when next is { OpCode: IlOpCode.Ldnull } && index + 2 < instructions.Count
                    && instructions[index + 2].OpCode is IlOpCode.Ceq or IlOpCode.Cgt_Un
                    && ValueTypes.TryMapType(StackArrays.Primitive(boxed), owner, out _) && !Nullables.IsNullable(boxed):
                // value == null is false, value != null true: a value is never null
                var notNull = instructions[index + 2].OpCode == IlOpCode.Cgt_Un;
                return Replaced(instructions, index, 3, [(IlOpCode.Pop, null), (notNull ? IlOpCode.Ldc_I4_1 : IlOpCode.Ldc_I4_0, null)],
                    targets);

            case { OpCode: IlOpCode.Box, Operand: IlTypeOperand { Type: var boxed } }
                when next is { OpCode: IlOpCode.Isinst, Operand: IlTypeOperand { Type: var testedAgainst } }
                    && NeverHolds(testedAgainst, boxed, owner):
                // value is Node: a value is never one; value is Node == false, compared with null C#'s way
                return index + 3 < instructions.Count && instructions[index + 2].OpCode == IlOpCode.Ldnull
                    && instructions[index + 3].OpCode == IlOpCode.Cgt_Un
                    ? Replaced(instructions, index, 4, [(IlOpCode.Pop, null), (IlOpCode.Ldc_I4_0, null)], targets)
                    : Replaced(instructions, index, 2, [(IlOpCode.Pop, null), (IlOpCode.Ldnull, null)], targets);

            case { OpCode: IlOpCode.Unbox_Any, Operand: IlTypeOperand { Type: var cast } } when IsReference(cast, owner):
                return previous is { OpCode: IlOpCode.Isinst, Operand: IlTypeOperand { Type: var tested } } && tested == cast
                    ? Replaced(instructions, index, 1, [], targets)
                    : Replaced(instructions, index, 1, [(IlOpCode.Castclass, new IlTypeOperand(cast))], targets);

            case { OpCode: IlOpCode.Initobj, Operand: IlTypeOperand { Type: var zeroed } }
                when IsReference(zeroed, owner) && previous is not null && StoreOfNull(previous) is { } store:
                return Replaced(instructions, index - 1, 2, [(IlOpCode.Ldnull, null), store], targets);

            // through a reference to an object (an out T, ObjectReferences): what ldind.ref and stind.ref do
            case { OpCode: IlOpCode.Initobj, Operand: IlTypeOperand { Type: var zeroed } } when IsReference(zeroed, owner):
                return Replaced(instructions, index, 1, [(IlOpCode.Ldnull, null), (IlOpCode.Stind_Ref, null)], targets);

            case { OpCode: IlOpCode.Ldobj, Operand: IlTypeOperand { Type: var loaded } } when IsReference(loaded, owner):
                return Replaced(instructions, index, 1, [(IlOpCode.Ldind_Ref, null)], targets);

            case { OpCode: IlOpCode.Stobj, Operand: IlTypeOperand { Type: var stored } } when IsReference(stored, owner):
                return Replaced(instructions, index, 1, [(IlOpCode.Stind_Ref, null)], targets);

            case { OpCode: IlOpCode.Constrained, Operand: IlTypeOperand { Type: var type } }
                when IsReference(type, owner) && next is { OpCode: IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } }:
                return CallOnObject(instructions, index, callee, instance, targets);

            case
            {
                OpCode: IlOpCode.Call,
                Operand: IlMethodOperand
                {
                    Method:
                    {
                        Name: "CreateInstance", HasThis: false, ParameterTypes.Count: 0, GenericArguments: [var made],
                        DeclaringType: IlNamedType { FullName: "System.Activator" },
                    },
                },
            } when !Substitution.IsOpen(made) && New(made, owner) is { } creation:
                return Replaced(instructions, index, 1, [creation], targets);

            case
            {
                OpCode: IlOpCode.Call,
                Operand: IlMethodOperand
                {
                    Method.Definition:
                    {
                        GenericDefinition: { DeclaringType.FullName: EqualityType } comparison, MethodArguments: [var compared],
                    },
                },
            } when DefaultComparison(comparison.Name, compared, owner) is { } chosen:
                return Replaced(instructions, index, 1, [(IlOpCode.Call, new IlMethodOperand(chosen))], targets);

            default:
                return false;
        }
    }

    /// <summary>True for a reference type argument (<see cref="ReferenceArguments"/>), text too: what an object is.</summary>
    private static bool IsReference(IlType type, IlAssembly owner) =>
        !Substitution.IsOpen(type)
        && (StackArrays.Primitive(ValueTypes.WithoutModifiers(type)) is IlPrimitiveType { Kind: IlPrimitiveKind.String }
            || ReferenceArguments.IsReference(StackArrays.Primitive(type), owner, out _));

    /// <summary>
    /// True when a value of <paramref name="value"/> (a number, bool, enum or struct), boxed, is never a
    /// <paramref name="tested"/>: a class of the program or an array (not object), or an interface the struct does not
    /// implement.
    /// </summary>
    private static bool NeverHolds(IlType tested, IlType value, IlAssembly owner)
    {
        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(value));
        if (Substitution.IsOpen(plain) || Substitution.IsOpen(tested) || IsReference(plain, owner)
            || !ValueTypes.TryMapType(plain, owner, out _))
        {
            return false;
        }

        var target = StackArrays.Primitive(ValueTypes.WithoutModifiers(tested));
        if (ObjectTypes.Class(target, owner) is not null || target is IlArrayType)
        {
            return true;
        }

        return VirtualMethods.IsInterface(target, owner) && ValueTypes.Definition(plain, owner) is { } definition
            && ValueTypes.IsStruct(definition)
            && !(definition.GenericDefinition ?? definition).Assembly.Interfaces(definition.GenericDefinition ?? definition)
                .Select(i => new Substitution(definition.TypeArguments, []).Apply(i)).Contains(target);
    }

    /// <summary>The store of null that takes the place of <paramref name="address"/> and an <c>initobj</c> of a reference type there.</summary>
    private static (IlOpCode, IlOperand?)? StoreOfNull(IlInstruction address) => address switch
    {
        { OpCode: IlOpCode.Ldloca_S, Operand: var local } => (IlOpCode.Stloc_S, local),
        { OpCode: IlOpCode.Ldloca, Operand: var local } => (IlOpCode.Stloc, local),
        { OpCode: IlOpCode.Ldarga_S, Operand: var argument } => (IlOpCode.Starg_S, argument),
        { OpCode: IlOpCode.Ldarga, Operand: var argument } => (IlOpCode.Starg, argument),
        { OpCode: IlOpCode.Ldflda, Operand: var field } => (IlOpCode.Stfld, field),
        { OpCode: IlOpCode.Ldsflda, Operand: var field } => (IlOpCode.Stsfld, field),
        { OpCode: IlOpCode.Ldelema } => (IlOpCode.Stelem_Ref, null),
        _ => null,
    };

    /// <summary>The load that takes the place of <paramref name="address"/>, the address of a variable that holds an object.</summary>
    private static (IlOpCode, IlOperand?)? LoadOf(IlInstruction address) => address switch
    {
        { OpCode: IlOpCode.Ldloca_S, Operand: var local } => (IlOpCode.Ldloc_S, local),
        { OpCode: IlOpCode.Ldloca, Operand: var local } => (IlOpCode.Ldloc, local),
        { OpCode: IlOpCode.Ldarga_S, Operand: var argument } => (IlOpCode.Ldarg_S, argument),
        { OpCode: IlOpCode.Ldarga, Operand: var argument } => (IlOpCode.Ldarg, argument),
        { OpCode: IlOpCode.Ldflda, Operand: var field } => (IlOpCode.Ldfld, field),
        { OpCode: IlOpCode.Ldsflda, Operand: var field } => (IlOpCode.Ldsfld, field),
        { OpCode: IlOpCode.Ldelema } => (IlOpCode.Ldelem_Ref, null),
        _ => null,
    };

    /// <summary>
    /// <c>constrained. T callvirt <paramref name="callee"/></c> at <paramref name="index"/>, T a reference type: the
    /// instruction of the same block that put the address of the object there (below the call's arguments) loads the
    /// object instead, and the prefix goes; the callvirt is then one like any other. True when it could.
    /// </summary>
    private static bool CallOnObject(List<IlInstruction> instructions, int index, IlMethodReference callee, IlMethod instance,
        HashSet<int> targets)
    {
        var depth = callee.ParameterTypes.Count;
        for (var i = index - 1; i >= 0; i--)
        {
            // the values of another block, or of a copy, are not known here
            var instruction = instructions[i];
            if (instruction.OpCode == IlOpCode.Dup || targets.Contains(instructions[i + 1].Offset)
                || Lowering.IlBlock.EndsBlock(instruction))
            {
                return false;
            }

            var (pops, pushes) = IlStackEffects.Of(instruction, instance);
            if (depth < pushes)
            {
                if (pushes != 1 || LoadOf(instruction) is not { } load)
                {
                    return false;
                }

                Replace(instructions, i, 1, [load], targets);
                return Replaced(instructions, index, 1, [], targets);
            }

            depth += pops - pushes;
        }

        return false;
    }

    /// <summary>
    /// What <c>new T()</c> is for <paramref name="type"/>: <c>newobj</c> of the constructor without parameters of a class
    /// or a struct, or a call of ChipSharp.Library's Defaults.Of&lt;T&gt;() (C#'s default) for any other value; null
    /// when it is neither (an abstract class, ...).
    /// </summary>
    private static (IlOpCode, IlOperand?)? New(IlType type, IlAssembly owner)
    {
        var plain = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        var definition = ObjectTypes.Class(plain, owner) ?? ValueTypes.Definition(plain, owner);
        if (definition?.Methods.FirstOrDefault(method => method is { Name: ".ctor", IsStatic: false, Parameters.Count: 0 })
            is { } constructor && (definition.Attributes & System.Reflection.TypeAttributes.Abstract) == 0)
        {
            var reference = new IlMethodReference(plain, ".ctor", constructor.Signature, [], constructor);
            return (IlOpCode.Newobj, new IlMethodOperand(reference));
        }

        if (definition is not null && ObjectTypes.IsClass(definition) || IsReference(plain, owner)
            || LibrarySubstitutes.Assembly.FindType(DefaultsType)?.Methods.FirstOrDefault(method => method.Name == "Of")
                is not { } of)
        {
            return null;
        }

        var defaults = new IlNamedType("ChipSharp.Library", "Defaults", null, "ChipSharp.Library");
        var call = new IlMethodReference(defaults, of.Name, of.Signature, [plain], of);
        return (IlOpCode.Call, new IlMethodOperand(Instantiations.Link(call, owner)));
    }

    /// <summary>
    /// The comparison of ChipSharp.Library's Equality that .NET's default comparer of <paramref name="type"/> makes, for
    /// <c>Equality.AreEqual&lt;T&gt;</c> or <c>Equality.Compare&lt;T&gt;</c> (<paramref name="name"/>): through
    /// <c>IEquatable&lt;T&gt;</c> (<c>IComparable&lt;T&gt;</c>) for a number, a bool and a type that implements it (an enum
    /// as its number), else through object's Equals (IComparable) for a class; null when it is neither (a struct that
    /// does not implement it, which the default comparer boxes). For <c>Equality.HashOf&lt;T&gt;</c> of an enum, that of
    /// its number; null for any other T, whose own GetHashCode it calls.
    /// </summary>
    internal static IlMethodReference? DefaultComparison(string name, IlType type, IlAssembly owner)
    {
        var own = StackArrays.Primitive(ValueTypes.WithoutModifiers(type));
        var plain = ValueTypes.Underlying(own, owner);
        // (CompareInSort is Compare in a sort, whose own body fails as a sort does where neither object is IComparable)
        name = name == "CompareInSort" ? "Compare" : name;
        var generic = name == "AreEqual" ? "System.IEquatable`1" : name == "Compare" ? "System.IComparable`1" : null;
        if (Substitution.IsOpen(plain) || (generic is null && (name != "HashOf" || plain == own)))
        {
            return null;
        }

        // text is compared as string compares it (its Equals(object) is string's own)
        if (name == "Compare" && FixedText.IsText(plain)
            && LibrarySubstitutes.Assembly.FindType(EqualityType)?.Methods.FirstOrDefault(method => method.Name == "CompareText")
                is { } text)
        {
            return new IlMethodReference(new IlNamedType("ChipSharp.Library", "Equality", null, "ChipSharp.Library"),
                text.Name, text.Signature, [], text);
        }

        // an enum's hash code is its number's
        var through = generic is null ? "HashOf"
            : plain is IlPrimitiveType { Kind: >= IlPrimitiveKind.Bool and <= IlPrimitiveKind.NativeUInt }
            || Implements(plain, new IlGenericInstanceType(new IlNamedType("System", generic[7..], null, "System.Runtime"),
                [plain]), owner)
            ? name == "AreEqual" ? "OfEquatable" : "OfComparable"
            : !IsReference(plain, owner) ? null
            : name == "AreEqual" ? "OfObject"
            : Implements(plain, new IlNamedType("System", "IComparable", null, "System.Runtime"), owner) ? "OfObjectComparable"
            : null;
        if (through is null || LibrarySubstitutes.Assembly.FindType(EqualityType)?.Methods
                .FirstOrDefault(method => method.Name == through) is not { } helper)
        {
            return null;
        }

        var equality = new IlNamedType("ChipSharp.Library", "Equality", null, "ChipSharp.Library");
        return Instantiations.Link(new IlMethodReference(equality, helper.Name, helper.Signature, [plain], helper), owner);
    }

    /// <summary>
    /// True when <paramref name="type"/>, a struct or a class (or a base class of it), says it implements
    /// <paramref name="interfaceType"/>, a library interface, by its full name (whichever library assembly names it).
    /// </summary>
    internal static bool Implements(IlType type, IlType interfaceType, IlAssembly owner)
    {
        var wanted = IlTypeNames.DisplayName(interfaceType);
        IReadOnlyList<IlType> arguments = interfaceType is IlGenericInstanceType generic ? generic.Arguments : [];
        for (var current = ObjectTypes.Class(type, owner) ?? ValueTypes.Definition(type, owner); current is not null;
             current = ObjectTypes.IsClass(current) ? VirtualMethods.BaseOf(current) : null)
        {
            var definition = current.GenericDefinition ?? current;
            var ofType = new Substitution(current.TypeArguments, []);
            if (definition.Assembly.Interfaces(definition)
                .Select(listed => ValueTypes.WithoutModifiers(ofType.Apply(listed)))
                .Any(listed => IlTypeNames.DisplayName(listed) == wanted
                    && (listed is IlGenericInstanceType instance ? instance.Arguments : []).Select(VirtualMethods.Canonical)
                        .SequenceEqual(arguments.Select(VirtualMethods.Canonical))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Replaces <paramref name="count"/> instructions from <paramref name="index"/> with <paramref name="replacement"/>,
    /// as <see cref="Replace"/> does; true when it did (no branch goes into the middle of them, and they fit).
    /// </summary>
    private static bool Replaced(List<IlInstruction> instructions, int index, int count,
        IReadOnlyList<(IlOpCode OpCode, IlOperand? Operand)> replacement, HashSet<int> targets)
    {
        var before = instructions[index];
        Replace(instructions, index, count, replacement, targets);
        return !ReferenceEquals(before, instructions[index]);
    }
}
