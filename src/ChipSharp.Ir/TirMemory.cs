namespace ChipSharp.Ir;

/// <summary>Which memory an address points into, as far as TIR shows it: see <see cref="TirMemoryOf.Address"/>.</summary>
public enum TirMemory
{
    /// <summary>Not known from the address alone: a parameter, what a call returned or a load read, a number made an address.</summary>
    Unknown,

    /// <summary>
    /// Memory the program writes: a slot, a variable global (<see cref="TirGlobal.IsConstant"/> false), an object on the
    /// heap. RAM on a chip.
    /// </summary>
    Variables,

    /// <summary>
    /// Read-only data: a constant global (<see cref="TirGlobal.IsConstant"/>, also a constant object) or a type
    /// descriptor of the module, which a code generator puts with the code, in flash on a chip.
    /// </summary>
    Constants,
}

/// <summary>
/// Where an address points, from how TIR made it. On most chips this changes nothing: flash and RAM are one address
/// space, and a load reads either. On a chip whose flash is an address space of its own (an AVR, where only the LPM
/// instruction reads flash), a code generator reads <see cref="TirMemory.Constants"/> with flash's instruction,
/// <see cref="TirMemory.Variables"/> with RAM's, and checks an <see cref="TirMemory.Unknown"/> address when the program
/// runs.
/// </summary>
public static class TirMemoryOf
{
    /// <summary>
    /// The memory <paramref name="address"/> (a ptr or a ref) points into: a slot's, a global's or a type descriptor's
    /// address, or a new object, says it; an address made from another one (<see cref="TirFieldAddress"/>,
    /// <see cref="TirElementAddress"/>, <see cref="TirArrayData"/>, <see cref="TirArrayObject"/>) points where that one
    /// does; any other value is <see cref="TirMemory.Unknown"/>.
    /// </summary>
    public static TirMemory Address(TirValue address) => Root(address) switch
    {
        TirSlotAddress or TirNewObject or TirNewArray => TirMemory.Variables,
        TirGlobalAddress global => global.Global.IsConstant ? TirMemory.Constants : TirMemory.Variables,
        TirTypeAddress => TirMemory.Constants,
        _ => TirMemory.Unknown,
    };

    /// <summary>
    /// The value <paramref name="address"/> is made from: itself, or, for an address made from another one
    /// (<see cref="TirFieldAddress"/>, <see cref="TirElementAddress"/>, <see cref="TirArrayData"/>,
    /// <see cref="TirArrayObject"/>), the root of that one, e.g. the <see cref="TirGlobalAddress"/> of a table for the
    /// address of one of its elements.
    /// </summary>
    public static TirValue Root(TirValue address)
    {
        ArgumentNullException.ThrowIfNull(address);
        while (true)
        {
            switch (address)
            {
                case TirFieldAddress field:
                    address = field.Address;
                    break;

                case TirElementAddress element:
                    address = element.Address;
                    break;

                case TirArrayData data:
                    address = data.Array;
                    break;

                case TirArrayObject arrayObject:
                    address = arrayObject.Data;
                    break;

                default:
                    return address;
            }
        }
    }
}
