using ChipSharp.Abstractions;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Delegates (see <see cref="Delegates"/>): the descriptors of System.Delegate, System.MulticastDelegate and of each
/// delegate type the program makes or tests against, the thunks that call static methods as delegates, and the virtual
/// method tables of the delegate types, which <see cref="FillDelegateTables"/> fills in once every method is translated.
/// </summary>
internal sealed partial class ProgramFunctions
{
    private readonly Dictionary<string, (TirTypeDescriptor Descriptor, IlType Type, IlAssembly Assembly)> _delegateTypes =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, TirFunction> _thunks = new(StringComparer.Ordinal);
    private TirTypeDescriptor? _delegate;
    private TirTypeDescriptor? _multicast;
    private DelegateFunctions? _delegateFunctions;

    /// <summary>The functions of delegates: Combine, Remove, Equals, the invokers, ...</summary>
    public DelegateFunctions DelegateFunctions => _delegateFunctions ??= new DelegateFunctions(module, this);

    /// <summary>System.Delegate's descriptor: laid out as every delegate (<see cref="Delegates.ObjectType"/>), based on object's.</summary>
    public TirTypeDescriptor DelegateDescriptor => _delegate ??= NamedDelegateDescriptor(Delegates.DelegateName, ObjectDescriptor);

    /// <summary>System.MulticastDelegate's descriptor, the base of every delegate type's, and what a multicast delegate is made as.</summary>
    public TirTypeDescriptor MulticastDescriptor =>
        _multicast ??= NamedDelegateDescriptor(Delegates.MulticastName, DelegateDescriptor);

    /// <summary>The descriptor of <c>System.Delegate[]</c>, the arrays of a multicast delegate's invocations.</summary>
    public TirTypeDescriptor DelegateArrayDescriptor
    {
        get
        {
            var type = new IlArrayType(Delegates.DelegateType);
            var descriptor = ArrayDescriptor(CSharpNames.Type(type), TirType.Ref, DelegateDescriptor);
            descriptor.RuntimeName ??= VirtualMethods.RuntimeName(type);
            return descriptor;
        }
    }

    /// <summary>
    /// The descriptor of a delegate type (or of System.Delegate and System.MulticastDelegate themselves), named as C#
    /// names it (<c>System.Func&lt;int, int&gt;</c>) and holding .NET's name (<c>System.Func`2[System.Int32,System.Int32]</c>);
    /// the same one for every name of the type.
    /// </summary>
    public TirTypeDescriptor DelegateTypeDescriptor(IlType type, IlAssembly assembly)
    {
        var plain = Delegates.Canonical(type, assembly);
        switch (plain)
        {
            case IlNamedType { Name: "Delegate" } when Delegates.IsBaseType(plain):
                return DelegateDescriptor;

            case IlNamedType { Name: "MulticastDelegate" } when Delegates.IsBaseType(plain):
                return MulticastDescriptor;
        }

        var name = CSharpNames.Type(plain);
        if (!_delegateTypes.TryGetValue(name, out var known))
        {
            var descriptor = module.AddClass(name, Delegates.ObjectType, MulticastDescriptor);
            descriptor.RuntimeName = VirtualMethods.RuntimeName(plain);
            _delegateTypes.Add(name, known = (descriptor, plain, assembly));
        }

        return known.Descriptor;
    }

    /// <summary>
    /// The thunk that calls the static method <paramref name="target"/> (of the program, or of the library a
    /// <see cref="KnownExternals"/> runtime function is) as a delegate: it takes the delegate's target, a ref it leaves
    /// alone, then the method's parameters, and returns what the method returns; one per method.
    /// </summary>
    public TirFunction DelegateThunk(IlMethodReference target, IlAssembly assembly)
    {
        var key = target.Definition?.ToString() ?? $"{target.DisplayName}({string.Join(", ", target.ParameterTypes)})";
        if (_thunks.TryGetValue(key, out var thunk))
        {
            return thunk;
        }

        TirFunction callee;
        IReadOnlyList<TirType> parameters;
        if (target.Definition is { } method)
        {
            var lowered = _methods.TryGetValue(method, out var found) && found.Arguments.All(argument => argument.Parameter is not null)
                ? found
                : throw new ChipSharpException($"{method} is made into a delegate, but it is not among the program's methods.");
            callee = lowered.Function;
            parameters = [.. callee.Parameters.Select(parameter => parameter.Type)];
        }
        else if (KnownExternals.TryMap(target, out var external))
        {
            callee = External(external);
            parameters = [.. target.ParameterTypes.Select(type => SupportedSubset.TryMapType(type, assembly, out var mapped)
                ? mapped
                : throw new ChipSharpException($"{target.DisplayName} has a parameter of type {CSharpNames.Type(type)}."))];
        }
        else
        {
            throw new ChipSharpException($"{target.DisplayName} is made into a delegate, and it is no method ChipSharp can call.");
        }

        var returns = target.Definition is not null ? callee.ReturnType
            : SupportedSubset.TryMapType(target.ReturnType, assembly, out var result) ? result : callee.ReturnType;
        thunk = module.AddFunction($"<delegate> {key}", returns,
            [("<target>", TirType.Ref), .. parameters.Select((type, i) => (callee.Parameters[i].Name, type))]);
        var ir = new TirBuilder(thunk.AddBlock("entry"));
        var arguments = thunk.Parameters.Skip(1).Select((parameter, i) =>
            Fit(ir, parameter, callee.Parameters[i].Type, target.Definition is null ? target.ParameterTypes[i] : null));
        var call = ir.Call(callee, [.. arguments]);
        ir.Return(returns == TirType.Void ? null
            : call.Type == returns ? call
            : returns == TirType.Bool ? ir.Compare(TirCompareOp.Ne, call, ir.Const(call.Type, 0))
            : ir.Convert(call, returns));
        _thunks.Add(key, thunk);
        return thunk;
    }

    /// <summary>
    /// Once every method is translated: each delegate type's virtual method table, object's slots first (the delegates'
    /// Equals and GetHashCode, object's own ToString), then, when the program makes multicast delegates, its invoker.
    /// </summary>
    public void FillDelegateTables()
    {
        if (_delegateTypes.Count == 0)
        {
            return;
        }

        var objectSlots = VirtualTables.ObjectTable;
        var multicast = _delegateFunctions?.UsesMulticast == true;
        foreach (var (descriptor, type, assembly) in _delegateTypes.Values)
        {
            foreach (var slot in objectSlots)
            {
                descriptor.VirtualMethods.Add(Function(VirtualDispatch.DelegateTarget(slot)));
            }

            if (multicast)
            {
                descriptor.VirtualMethods.Add(Invoker(type, assembly));
            }
        }
    }

    /// <summary>The invoker of a delegate type's multicast delegates, from its Invoke's signature; null when it has none.</summary>
    private TirFunction? Invoker(IlType type, IlAssembly assembly)
    {
        if (Delegates.InvokeSignature(type, assembly) is not { } signature)
        {
            return null;
        }

        var types = new List<TirType>();
        foreach (var parameter in signature.ParameterTypes.Prepend(signature.ReturnType))
        {
            if (!SupportedSubset.TryMapType(parameter, assembly, out var mapped))
            {
                return null;
            }

            types.Add(mapped);
        }

        return DelegateFunctions.Invoker(types[0], types[1..]);
    }

    private TirTypeDescriptor NamedDelegateDescriptor(string name, TirTypeDescriptor baseType)
    {
        var descriptor = module.AddClass(name, Delegates.ObjectType, baseType);
        descriptor.RuntimeName = name;
        return descriptor;
    }

    /// <summary>
    /// A thunk's parameter as the method it calls takes it: as it is, or for a runtime function, a bool as an i32 (0 or 1)
    /// and a char or another small number widened as its IL type says (<paramref name="ilType"/>).
    /// </summary>
    private static TirValue Fit(TirBuilder ir, TirValue value, TirType wanted, IlType? ilType)
    {
        if (value.Type == wanted)
        {
            return value;
        }

        var unsigned = ilType is IlPrimitiveType { Kind: IlPrimitiveKind.Bool or IlPrimitiveKind.Char or IlPrimitiveKind.UInt8
            or IlPrimitiveKind.UInt16 or IlPrimitiveKind.UInt32 or IlPrimitiveKind.UInt64 or IlPrimitiveKind.NativeUInt };
        return ir.Convert(value, wanted, unsigned);
    }
}
