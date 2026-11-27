using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend;

/// <summary>
/// What ChipSharp compiles today: static methods (and the methods of structs and classes) over whole numbers, floats
/// and bools, the program's structs and enums, references to them and arrays of them on the stack, fixed text (string
/// literals, see <see cref="FixedText"/>), static fields, the program's classes and their objects on the heap (see
/// <see cref="ClassRules"/>), with branches, loops, switch and calls, printing and the hardware through
/// <see cref="KnownExternals"/>. The whole-program pass rejects anything else with a clear message that says when it is
/// planned; the IL-to-TIR translation handles exactly this set. Later milestones grow it.
/// </summary>
public static class SupportedSubset
{
    /// <summary>The IL instructions compiled today. Checked arithmetic (<c>*.ovf</c>), objects, pointers, exceptions
    /// and ckfinite are not in it; the array instructions are there for the arrays of <see cref="StackArrays"/>,
    /// <see cref="StaticArrays"/> and <see cref="HeapArrays"/>, newobj and the field instructions for structs only,
    /// ldstr and ldnull for fixed text.</summary>
    public static FrozenSet<IlOpCode> OpCodes { get; } = new[]
    {
        IlOpCode.Nop,
        IlOpCode.Ldarg_0, IlOpCode.Ldarg_1, IlOpCode.Ldarg_2, IlOpCode.Ldarg_3, IlOpCode.Ldarg_S, IlOpCode.Ldarg,
        IlOpCode.Starg_S, IlOpCode.Starg,
        IlOpCode.Ldloc_0, IlOpCode.Ldloc_1, IlOpCode.Ldloc_2, IlOpCode.Ldloc_3, IlOpCode.Ldloc_S, IlOpCode.Ldloc,
        IlOpCode.Stloc_0, IlOpCode.Stloc_1, IlOpCode.Stloc_2, IlOpCode.Stloc_3, IlOpCode.Stloc_S, IlOpCode.Stloc,
        IlOpCode.Ldsfld, IlOpCode.Stsfld, IlOpCode.Volatile,

        // structs, enums and references (ValueTypes): fields of structs, addresses, and loads and stores through them
        IlOpCode.Ldfld, IlOpCode.Stfld, IlOpCode.Ldflda, IlOpCode.Initobj, IlOpCode.Ldobj, IlOpCode.Stobj, IlOpCode.Cpobj,
        IlOpCode.Newobj, IlOpCode.Ldloca_S, IlOpCode.Ldloca, IlOpCode.Ldarga_S, IlOpCode.Ldarga, IlOpCode.Ldsflda,
        IlOpCode.Ldind_I1, IlOpCode.Ldind_U1, IlOpCode.Ldind_I2, IlOpCode.Ldind_U2, IlOpCode.Ldind_I4, IlOpCode.Ldind_U4,
        IlOpCode.Ldind_I8, IlOpCode.Ldind_I, IlOpCode.Stind_I1, IlOpCode.Stind_I2, IlOpCode.Stind_I4, IlOpCode.Stind_I8,
        IlOpCode.Stind_I,
        IlOpCode.Ldc_I4_M1, IlOpCode.Ldc_I4_0, IlOpCode.Ldc_I4_1, IlOpCode.Ldc_I4_2, IlOpCode.Ldc_I4_3,
        IlOpCode.Ldc_I4_4, IlOpCode.Ldc_I4_5, IlOpCode.Ldc_I4_6, IlOpCode.Ldc_I4_7, IlOpCode.Ldc_I4_8,
        IlOpCode.Ldc_I4_S, IlOpCode.Ldc_I4, IlOpCode.Ldc_I8,
        IlOpCode.Dup, IlOpCode.Pop, IlOpCode.Call, IlOpCode.Ret,
        IlOpCode.Br_S, IlOpCode.Brfalse_S, IlOpCode.Brtrue_S, IlOpCode.Beq_S, IlOpCode.Bge_S, IlOpCode.Bgt_S,
        IlOpCode.Ble_S, IlOpCode.Blt_S, IlOpCode.Bne_Un_S, IlOpCode.Bge_Un_S, IlOpCode.Bgt_Un_S, IlOpCode.Ble_Un_S,
        IlOpCode.Blt_Un_S,
        IlOpCode.Br, IlOpCode.Brfalse, IlOpCode.Brtrue, IlOpCode.Beq, IlOpCode.Bge, IlOpCode.Bgt, IlOpCode.Ble,
        IlOpCode.Blt, IlOpCode.Bne_Un, IlOpCode.Bge_Un, IlOpCode.Bgt_Un, IlOpCode.Ble_Un, IlOpCode.Blt_Un,
        IlOpCode.Switch,
        IlOpCode.Add, IlOpCode.Sub, IlOpCode.Mul, IlOpCode.Div, IlOpCode.Div_Un, IlOpCode.Rem, IlOpCode.Rem_Un,
        IlOpCode.And, IlOpCode.Or, IlOpCode.Xor, IlOpCode.Shl, IlOpCode.Shr, IlOpCode.Shr_Un, IlOpCode.Neg, IlOpCode.Not,
        IlOpCode.Conv_I1, IlOpCode.Conv_I2, IlOpCode.Conv_I4, IlOpCode.Conv_I8, IlOpCode.Conv_I,
        IlOpCode.Conv_U1, IlOpCode.Conv_U2, IlOpCode.Conv_U4, IlOpCode.Conv_U8, IlOpCode.Conv_U,
        IlOpCode.Ceq, IlOpCode.Cgt, IlOpCode.Cgt_Un, IlOpCode.Clt, IlOpCode.Clt_Un,

        // floats and doubles: the arithmetic, comparisons and branches above work on them too, and they are read and
        // written through references and in arrays like the whole numbers
        IlOpCode.Ldc_R4, IlOpCode.Ldc_R8, IlOpCode.Conv_R4, IlOpCode.Conv_R8, IlOpCode.Conv_R_Un,
        IlOpCode.Ldind_R4, IlOpCode.Ldind_R8, IlOpCode.Stind_R4, IlOpCode.Stind_R8,

        // arrays on the stack: see StackArrays, whose rule the whole-program pass checks for each of these (an
        // element's address from ldelema is a reference, which ldind, stind and the field instructions above go through)
        IlOpCode.Newarr, IlOpCode.Ldlen, IlOpCode.Ldelema,
        IlOpCode.Ldelem_I1, IlOpCode.Ldelem_U1, IlOpCode.Ldelem_I2, IlOpCode.Ldelem_U2, IlOpCode.Ldelem_I4,
        IlOpCode.Ldelem_U4, IlOpCode.Ldelem_I8, IlOpCode.Ldelem_I, IlOpCode.Ldelem_R4, IlOpCode.Ldelem_R8,
        IlOpCode.Ldelem,
        IlOpCode.Stelem_I1, IlOpCode.Stelem_I2, IlOpCode.Stelem_I4, IlOpCode.Stelem_I8, IlOpCode.Stelem_I,
        IlOpCode.Stelem_R4, IlOpCode.Stelem_R8, IlOpCode.Stelem,
        IlOpCode.Conv_Ovf_I, IlOpCode.Conv_Ovf_I_Un, IlOpCode.Ldtoken,

        // arrays of references on the heap (see HeapArrays): arrays of arrays and of objects
        IlOpCode.Ldelem_Ref, IlOpCode.Stelem_Ref,

        // fixed text (FixedText): a string literal, and null, which text, objects and arrays on the heap can be (see
        // HeapArrays; the other types it could be are rejected as types)
        IlOpCode.Ldstr, IlOpCode.Ldnull,

        // classes (ClassRules): callvirt, which checks its object for null, then calls the method or what its virtual
        // method table or interface map says (VirtualMethods); newobj and the field instructions above make objects and
        // reach their fields; isinst and castclass test them against classes and interfaces
        IlOpCode.Callvirt, IlOpCode.Isinst, IlOpCode.Castclass,

        // delegates (Delegates): the address of the method a new delegate calls, a virtual one's from the object's tables
        IlOpCode.Ldftn, IlOpCode.Ldvirtftn,

        // throw (Exceptions): stops the program, as an exception nothing catches does; try/finally is plain branches by
        // then (TryFinally)
        IlOpCode.Throw,

        // references to objects and strings (ObjectReferences): what they refer to, loaded and stored
        IlOpCode.Ldind_Ref, IlOpCode.Stind_Ref,
    }.ToFrozenSet();

    /// <summary>
    /// The TIR type of a parameter, local or return type of a method of <paramref name="assembly"/>: a value
    /// (<see cref="ValueTypes.TryMapType"/>: a primitive below, or a struct, enum or reference of the program), an
    /// array on the stack (<see cref="StackArrays.TryMapType"/>), fixed text (<see cref="FixedText.TryMapType"/>) or an
    /// object (<see cref="ObjectTypes.TryMapType"/>: a class of the program, or <c>object</c>).
    /// This is the one test of whether such a type is supported: the whole-program pass and the IL-to-TIR translation
    /// both ask it. (Whether an array may be returned is the rule of <see cref="StackArrays"/>, which they check
    /// apart; what keeps a class from compiling, that of <see cref="ClassRules"/>.)
    /// </summary>
    public static bool TryMapType(IlType type, IlAssembly assembly, [NotNullWhen(true)] out TirType? tirType)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(assembly);
        return ValueTypes.TryMapType(type, assembly, out tirType) || StackArrays.TryMapType(type, assembly, out tirType)
            || FixedText.TryMapType(type, out tirType) || ObjectTypes.TryMapType(type, assembly, out tirType)
            || ObjectReferences.TryMapType(type, assembly, out tirType);
    }

    /// <summary>
    /// The TIR type of a primitive type ChipSharp has: bool, char, the whole numbers of every width (TIR types carry no sign;
    /// signed and unsigned share one), nint/nuint, float, double and void. The other types a program may use build on
    /// these: see <see cref="TryMapType(IlType, IlAssembly, out TirType?)"/>.
    /// </summary>
    public static bool TryMapType(IlType type, [NotNullWhen(true)] out TirType? tirType)
    {
        ArgumentNullException.ThrowIfNull(type);
        tirType = type is IlPrimitiveType primitive
            ? primitive.Kind switch
            {
                IlPrimitiveKind.Void => TirType.Void,
                IlPrimitiveKind.Bool => TirType.Bool,
                IlPrimitiveKind.Int8 or IlPrimitiveKind.UInt8 => TirType.I8,
                // a char is its UTF-16 code unit, unsigned, like a ushort
                IlPrimitiveKind.Int16 or IlPrimitiveKind.UInt16 or IlPrimitiveKind.Char => TirType.I16,
                IlPrimitiveKind.Int32 or IlPrimitiveKind.UInt32 => TirType.I32,
                IlPrimitiveKind.Int64 or IlPrimitiveKind.UInt64 => TirType.I64,
                IlPrimitiveKind.NativeInt or IlPrimitiveKind.NativeUInt => TirType.NativeInt,
                IlPrimitiveKind.Float32 => TirType.F32,
                IlPrimitiveKind.Float64 => TirType.F64,
                _ => null,
            }
            : null;
        return tirType is not null;
    }
}
