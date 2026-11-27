using ChipSharp.Ir;

namespace ChipSharp.Backend.Llvm;

/// <summary>
/// Where the addresses of flash data go in a program for a chip whose flash is an address space of its own (see
/// <see cref="LlvmFlash"/>): which values may hold one, found over the whole module, so that only a load through an
/// address that may be flash's checks bit 15 when it runs; one that can only be RAM's is an ordinary load. A program
/// whose structs are reached through <c>ref</c> parameters and <c>this</c>, say, and that never stores a string literal or
/// a table's address where that memory is, reads them all with plain loads.
/// <para>
/// A value may hold an address of flash data (it "may be flash") when it is one (<see cref="TirMemory.Constants"/>), is
/// made from one (a field's or an element's address), is a parameter some call passes one to, is what a function that
/// may return one returns (an external function's, when the program passed it one), or is read from memory that may
/// hold one. Memory is followed as slots and variable globals whose address never leaves the code that reads and writes
/// through it, each on its own; all other memory (a slot whose address is passed to a call, what a pointer parameter
/// points to) is one memory together. An address of flash data turned into a number makes every address made from a
/// number one that may be flash. A call through an address may reach every function whose address the program takes.
/// </para>
/// </summary>
internal sealed class LlvmFlashFlow
{
    private readonly Func<TirValue, TirMemory> _memory;
    private readonly HashSet<TirValue> _values = [];
    private readonly HashSet<TirSlot> _slots = [];
    private readonly HashSet<TirGlobal> _globals = [];
    private readonly HashSet<TirFunction> _returns = [];
    private readonly HashSet<TirSlot> _escapedSlots = [];
    private readonly HashSet<TirGlobal> _escapedGlobals = [];
    private readonly List<TirFunction> _addressed;
    private bool _otherMemory;
    private bool _numbers;
    private bool _changed;

    private LlvmFlashFlow(TirModule module, Func<TirValue, TirMemory> memory)
    {
        _memory = memory;
        var defined = module.Functions.Where(function => function.Kind == TirFunctionKind.Defined).ToList();
        var addressed = new HashSet<TirFunction>(Instructions(defined).OfType<TirFunctionAddress>()
            .Select(address => address.Function));
        foreach (var type in module.TypeDescriptors)
        {
            addressed.UnionWith(type.VirtualMethods.OfType<TirFunction>());
            addressed.UnionWith(type.Interfaces.SelectMany(map => map.Methods).OfType<TirFunction>());
        }

        _addressed = [.. addressed];
        FindEscapes(defined);
        do
        {
            _changed = false;
            foreach (var instruction in Instructions(defined))
            {
                Visit(instruction);
            }
        } while (_changed);
    }

    /// <summary>
    /// Follows the addresses of flash data through <paramref name="module"/>; <paramref name="memory"/> says where an
    /// address points (<see cref="TirMemory.Constants"/>: flash data).
    /// </summary>
    public static LlvmFlashFlow Analyze(TirModule module, Func<TirValue, TirMemory> memory) => new(module, memory);

    /// <summary>True when <paramref name="value"/> may hold (or, a struct, contain) an address of flash data.</summary>
    public bool MayBeFlash(TirValue value) =>
        _values.Contains(value) || _memory(value) == TirMemory.Constants;

    private static IEnumerable<TirInstruction> Instructions(IEnumerable<TirFunction> functions) =>
        functions.SelectMany(function => function.Blocks).SelectMany(block => block.Instructions);

    /// <summary>
    /// The slots and variable globals whose address leaves the code that reads and writes through it: used as anything
    /// but the address of a load or store, the start of another address, or a side of a comparison.
    /// </summary>
    private void FindEscapes(IEnumerable<TirFunction> functions)
    {
        foreach (var instruction in Instructions(functions))
        {
            foreach (var operand in instruction.Operands)
            {
                if (IsAddressUse(instruction, operand))
                {
                    continue;
                }

                switch (TirMemoryOf.Root(operand))
                {
                    case TirSlotAddress slot:
                        _escapedSlots.Add(slot.Slot);
                        break;

                    case TirGlobalAddress { Global.IsConstant: false } global:
                        _escapedGlobals.Add(global.Global);
                        break;
                }
            }
        }
    }

    /// <summary>True when <paramref name="user"/> takes <paramref name="operand"/> as an address it reads, writes or compares.</summary>
    private static bool IsAddressUse(TirInstruction user, TirValue operand) => user switch
    {
        TirPointerStore store => !ReferenceEquals(store.Value, operand),
        TirPointerLoad or TirFieldAddress or TirElementAddress or TirArrayData or TirArrayObject or TirArrayLength
            or TirTypeOf or TirNullCheck or TirCompare => true,
        _ => false,
    };

    private void Visit(TirInstruction instruction)
    {
        switch (instruction)
        {
            case TirStore store when MayBeFlash(store.Value):
                MarkSlot(store.Slot);
                break;

            case TirPointerStore store when MayBeFlash(store.Value):
                MarkMemory(store.Address);
                break;

            case TirLoad load when SlotMayBeFlash(load.Slot):
                Mark(load);
                break;

            case TirPointerLoad load when ReadMayBeFlash(load.Address):
                Mark(load);
                break;

            case TirArrayLength length when ReadMayBeFlash(length.Array):
                Mark(length);
                break;

            case TirTypeOf typeOf when ReadMayBeFlash(typeOf.Reference):
                Mark(typeOf);
                break;

            case TirFieldAddress field when MayBeFlash(field.Address):
                Mark(field);
                break;

            case TirElementAddress element when MayBeFlash(element.Address):
                Mark(element);
                break;

            case TirArrayData data when MayBeFlash(data.Array):
                Mark(data);
                break;

            case TirArrayObject arrayObject when MayBeFlash(arrayObject.Data):
                Mark(arrayObject);
                break;

            case TirPtrToInt toNumber when MayBeFlash(toNumber.Address) && !_numbers:
                _numbers = _otherMemory = _changed = true;
                break;

            case TirIntToPtr toAddress when _numbers:
                Mark(toAddress);
                break;

            case TirReturn { Value: { } value } when MayBeFlash(value):
                MarkReturn(instruction.Block!.Function);
                break;

            case TirCall call:
                for (var i = 0; i < call.Arguments.Count; i++)
                {
                    if (!MayBeFlash(call.Arguments[i]))
                    {
                        continue;
                    }

                    if (call.Callee.Kind == TirFunctionKind.External)
                    {
                        // the runtime may give back what it was given
                        MarkReturn(call.Callee);
                    }
                    else
                    {
                        Mark(call.Callee.Parameters[i]);
                    }
                }

                if (_returns.Contains(call.Callee))
                {
                    Mark(call);
                }

                break;

            case TirCallIndirect call:
                for (var i = 0; i < call.Arguments.Count; i++)
                {
                    if (MayBeFlash(call.Arguments[i]))
                    {
                        foreach (var function in _addressed.Where(function => function.Parameters.Count > i))
                        {
                            Mark(function.Parameters[i]);
                        }
                    }
                }

                if (_addressed.Any(_returns.Contains))
                {
                    Mark(call);
                }

                break;
        }
    }

    /// <summary>True when what a load through <paramref name="address"/> reads may be (or contain) an address of flash data.</summary>
    private bool ReadMayBeFlash(TirValue address)
    {
        if (_memory(address) == TirMemory.Constants)
        {
            // flash data holds addresses of flash data: a descriptor's, a literal's in a constant object
            return true;
        }

        return TirMemoryOf.Root(address) switch
        {
            TirSlotAddress slot => SlotMayBeFlash(slot.Slot),
            TirGlobalAddress { Global.IsConstant: false } global when !_escapedGlobals.Contains(global.Global) =>
                _globals.Contains(global.Global),
            _ => _otherMemory || MayBeFlash(address),
        };
    }

    private bool SlotMayBeFlash(TirSlot slot) => _escapedSlots.Contains(slot) ? _otherMemory : _slots.Contains(slot);

    private void MarkSlot(TirSlot slot)
    {
        if (_escapedSlots.Contains(slot))
        {
            MarkOtherMemory();
        }
        else if (_slots.Add(slot))
        {
            _changed = true;
        }
    }

    /// <summary>Notes that the memory a store through <paramref name="address"/> writes may now hold an address of flash data.</summary>
    private void MarkMemory(TirValue address)
    {
        switch (TirMemoryOf.Root(address))
        {
            case TirSlotAddress slot:
                MarkSlot(slot.Slot);
                break;

            case TirGlobalAddress { Global.IsConstant: false } global when !_escapedGlobals.Contains(global.Global):
                if (_globals.Add(global.Global))
                {
                    _changed = true;
                }

                break;

            default:
                MarkOtherMemory();
                break;
        }
    }

    private void MarkOtherMemory()
    {
        if (!_otherMemory)
        {
            _otherMemory = _changed = true;
        }
    }

    private void Mark(TirValue value)
    {
        if (_values.Add(value))
        {
            _changed = true;
        }
    }

    private void MarkReturn(TirFunction function)
    {
        if (_returns.Add(function))
        {
            _changed = true;
        }
    }
}
