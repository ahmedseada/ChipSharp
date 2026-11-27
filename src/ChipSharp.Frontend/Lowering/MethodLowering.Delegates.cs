using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// Delegates (see <see cref="Delegates"/> for their layout, <see cref="DelegateFunctions"/> for what they call):
/// <list type="bullet">
/// <item><c>ldftn</c> of an instance method: its function's address (<c>funcaddr</c>); of a static method, its thunk's,
/// which leaves the target alone, or, closed over its first argument (the target is one more argument than the
/// delegate's Invoke has), the method's own. <c>ldvirtftn</c>: the null check of the object, then the address of what
/// the slot runs on it: devirtualized as a call is (one method can run), or read from the object's tables.</item>
/// <item><c>newobj</c> of a delegate type's constructor: a new delegate of the type's descriptor, with the target and
/// the address IL gave.</item>
/// <item>Invoke: the null check, then <c>call indirect method(target, arguments)</c>.</item>
/// <item>Delegate.Combine, Remove, ==, !=, GetInvocationList, a delegate's Equals, GetHashCode and ToString, and
/// Interlocked.CompareExchange of an object: a call of their functions.</item>
/// <item><c>isinst</c> and <c>castclass</c> to a delegate type, System.Delegate or System.MulticastDelegate test the
/// object's class (and its bases) as against a class; the cast C# puts after Combine and Remove is what they give
/// anyway, so it is left out.</item>
/// </list>
/// </summary>
internal sealed partial class MethodLowering
{
    /// <summary>True for the instructions <see cref="LowerDelegateInstruction"/> translates.</summary>
    private bool IsDelegateInstruction(IlInstruction instruction)
    {
        var assembly = _method.Assembly;
        return instruction switch
        {
            { OpCode: IlOpCode.Ldftn or IlOpCode.Ldvirtftn } => true,
            { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } } =>
                Delegates.IsConstructor(constructor, assembly),
            { OpCode: IlOpCode.Call or IlOpCode.Callvirt, Operand: IlMethodOperand { Method: var callee } } =>
                Delegates.IsInvoke(callee, assembly) || Delegates.Call(callee, assembly) is not null
                || DelegateObjectMethod(instruction) is not null,
            { OpCode: IlOpCode.Isinst or IlOpCode.Castclass, Operand: IlTypeOperand { Type: var type } } =>
                Delegates.IsDelegateType(type, assembly),
            _ => false,
        };
    }

    private void LowerDelegateInstruction(IlInstruction instruction)
    {
        var assembly = _method.Assembly;
        switch (instruction)
        {
            case { OpCode: IlOpCode.Ldftn, Operand: IlMethodOperand { Method: var method } }:
                LoadMethodAddress(method);
                break;

            case { OpCode: IlOpCode.Ldvirtftn, Operand: IlMethodOperand { Method: var method } }:
                LoadVirtualMethodAddress(method);
                break;

            case { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } }:
                NewDelegate(constructor);
                break;

            case { OpCode: IlOpCode.Isinst or IlOpCode.Castclass, Operand: IlTypeOperand { Type: var type } }:
                DelegateTypeTest(type, cast: instruction.OpCode == IlOpCode.Castclass);
                break;

            case { Operand: IlMethodOperand { Method: var callee } } when Delegates.IsInvoke(callee, assembly):
                InvokeDelegate(callee);
                break;

            case { Operand: IlMethodOperand { Method: var callee } } when Delegates.Call(callee, assembly) is { } call:
                CallDelegateFunction(call, callee);
                break;

            default:
                CallDelegateObjectMethod(DelegateObjectMethod(instruction)!);
                break;
        }
    }

    /// <summary>
    /// A callvirt of object's Equals, GetHashCode or ToString on a delegate (as the instruction that put it on the stack
    /// says): the slot's name; null for any other instruction.
    /// </summary>
    private string? DelegateObjectMethod(IlInstruction instruction)
    {
        var index = _body.IndexOf(instruction.Offset);
        return instruction.OpCode == IlOpCode.Callvirt
            && VirtualMethods.CalledSlot(_body.Instructions, index) is { Kind: DispatchSlotKind.Object } slot
            && !StringMethods.IsConsoleWriteOfObject(instruction)
            && VirtualMethods.StackType(_method, index, slot.Signature.ParameterTypes.Count) is { } receiver
            && Delegates.IsDelegateType(receiver, _method.Assembly)
            ? slot.Name
            : null;
    }

    /// <summary>
    /// ldftn: an instance method's own address; a static method's thunk's, or its own when the delegate made next is
    /// closed over its first argument (an extension method as a method group: the target is that argument).
    /// </summary>
    private void LoadMethodAddress(IlMethodReference method)
    {
        if (method.Definition is { IsStatic: false } instance)
        {
            // a struct's method: on the value in the box that is the delegate's target (see BoxingFunctions.OnBox)
            Push(_ir.AddressOf(ValueTypes.IsStruct(instance.DeclaringType)
                && Boxing.OfDefinition(instance.DeclaringType) is { } boxed
                    ? _program.Boxes.OnBox(DispatchTarget.Of(instance) with { Boxed = boxed })
                    : _program.Get(instance).Function));
            return;
        }

        if (method.HasThis)
        {
            throw Error($"ldftn of {method.DisplayName}, an instance method of the library, is not supported");
        }

        // the newobj that comes next says what the delegate's Invoke takes
        var index = _body.IndexOf(_instruction.Offset);
        var invoke = index + 1 < _body.Instructions.Count
            && _body.Instructions[index + 1] is { OpCode: IlOpCode.Newobj, Operand: IlMethodOperand { Method: var constructor } }
            ? Delegates.InvokeSignature(constructor.DeclaringType, _method.Assembly)
            : null;
        if (invoke is null)
        {
            throw Error($"ldftn of {method.DisplayName} is not followed by the newobj of a delegate type: function " +
                "pointers are not supported");
        }

        var closed = method.ParameterTypes.Count == invoke.ParameterTypes.Count + 1;
        if (!closed)
        {
            Push(_ir.AddressOf(_program.DelegateThunk(method, _method.Assembly)));
        }
        else if (method.Definition is { } first)
        {
            Push(_ir.AddressOf(_program.Get(first).Function));
        }
        else
        {
            throw Error($"a delegate of {method.DisplayName} closed over its first argument is not supported: ChipSharp " +
                "makes delegates of the library's static methods that take what the delegate takes");
        }
    }

    /// <summary>
    /// ldvirtftn: the object's null check, then the address of the method its slot runs on it: of the one method that can
    /// run (or none: null, which the null check stopped at), else from the object's tables. Object's slots go through
    /// the function a virtual call of them calls, which knows arrays and strings.
    /// </summary>
    private void LoadVirtualMethodAddress(IlMethodReference method)
    {
        var slot = Delegates.LoadedSlot(_body.Instructions, _body.IndexOf(_instruction.Offset));
        var receiverType = StackType(0) ?? method.DeclaringType;
        var receiver = NullCheck(ObjectFromStack(PopValue(), $"the object whose {method.Name} a delegate calls"));
        if (slot is null)
        {
            var definition = method.Definition
                ?? throw Error($"ldvirtftn of {method.DisplayName}, a method of the library, is not supported");

            // a struct's method (an interface's it implements is virtual): on the value in the box that is the target
            Push(_ir.AddressOf(ValueTypes.IsStruct(definition.DeclaringType)
                && Boxing.OfDefinition(definition.DeclaringType) is { } boxed
                    ? _program.Boxes.OnBox(DispatchTarget.Of(definition) with { Boxed = boxed })
                    : _program.Get(definition).Function));
            return;
        }

        var targets = _program.VirtualTables.Dispatch.Targets(slot, receiverType);
        if (slot.Kind == DispatchSlotKind.Object)
        {
            var own = targets.Count switch
            {
                0 => null,
                1 => _program.Function(targets[0]),
                _ => _program.ObjectFunctions.VirtualObjectCall(slot.Name, SlotIndex(slot)),
            };
            var function = StringMethods.MayBeString(receiverType) ? _program.StringObjects.OrString(slot.Name, own) : own;
            Push(function is null ? _ir.Zero(TirType.Ptr) : _ir.AddressOf(function));
            return;
        }

        Push(targets.Count switch
        {
            0 => _ir.Zero(TirType.Ptr),
            1 => _ir.AddressOf(_program.Function(targets[0])),
            _ => MethodAddress(slot, receiver),
        });
    }

    /// <summary>newobj of a delegate type's constructor: a new delegate of the type, with the target and the method's address.</summary>
    private void NewDelegate(IlMethodReference constructor)
    {
        var method = PopValue();
        if (method.Type != TirType.Ptr || method.IsNull)
        {
            throw Error($"the delegate {CSharpNames.Type(constructor.DeclaringType)} takes the address of a method " +
                "(ldftn or ldvirtftn), but the evaluation stack has " + StackName(method.Type));
        }

        var address = Materialize(method);
        var target = ObjectFromStack(PopValue(), $"the target of the delegate {CSharpNames.Type(constructor.DeclaringType)}");
        var made = NewObject(_program.DelegateTypeDescriptor(constructor.DeclaringType, _method.Assembly));
        _ir.Store(_ir.FieldAddress(made, Delegates.ObjectType, Delegates.TargetField), target);
        _ir.Store(_ir.FieldAddress(made, Delegates.ObjectType, Delegates.MethodField), address);
        PushObject(made);
    }

    /// <summary>A delegate's Invoke: its null check, then one indirect call of its method, with its target first.</summary>
    private void InvokeDelegate(IlMethodReference invoke)
    {
        var type = CSharpNames.Type(invoke.DeclaringType);
        var signature = Delegates.InvokeSignature(invoke.DeclaringType, _method.Assembly)!;
        var values = new StackValue[signature.ParameterTypes.Count];
        for (var i = values.Length - 1; i >= 0; i--)
        {
            values[i] = PopValue();
        }

        var invoked = NullCheck(ObjectFromStack(PopValue(), $"the delegate {type} that is invoked"));

        // the fields are those of System.Delegate's object type, whose descriptor the module then has
        _ = _program.DelegateDescriptor;
        var arguments = new List<TirValue>
        {
            _ir.Load(TirType.Ref, _ir.FieldAddress(invoked, Delegates.ObjectType, Delegates.TargetField)),
        };
        for (var i = 0; i < values.Length; i++)
        {
            var parameter = signature.ParameterTypes[i];
            arguments.Add(FromStack(values[i], MapType(parameter, $"parameter {i + 1} of {type}.Invoke"), parameter,
                $"argument {i + 1} of {type}.Invoke"));
        }

        var method = _ir.Load(TirType.Ptr, _ir.FieldAddress(invoked, Delegates.ObjectType, Delegates.MethodField));
        var result = _ir.CallIndirect(MapType(signature.ReturnType, $"the result of {type}.Invoke"), method, arguments);
        if (result.HasResult)
        {
            Push(ToStack(result, signature.ReturnType));
        }
    }

    /// <summary>A method of System.Delegate (or Interlocked.CompareExchange): a call of its function.</summary>
    private void CallDelegateFunction(DelegateCall call, IlMethodReference callee)
    {
        var functions = _program.DelegateFunctions;
        switch (call)
        {
            case DelegateCall.Combine or DelegateCall.Remove:
                var second = PopObject();
                var first = PopObject();
                PushObject(_ir.Call(call == DelegateCall.Combine ? functions.Combine : functions.Remove, first, second));
                break;

            case DelegateCall.Equality or DelegateCall.Inequality:
                var right = PopObject();
                var left = PopObject();
                Push(StackValue.OfBool(_ir.Call(call == DelegateCall.Equality ? functions.Equality : functions.Inequality,
                    left, right)));
                break;

            case DelegateCall.InvocationList:
                // its type's descriptor holds .NET's name, Delegate[], for a cast of it
                _ = _program.DelegateArrayDescriptor;
                Push(ArrayToStack(_ir.Call(functions.InvocationList, PopObjectNotNull()), callee.ReturnType));
                break;

            default:
                var comparand = PopObject();
                var value = PopObject();
                var (location, _) = PopReference();
                PushObject(_ir.Call(functions.CompareExchange, location, value, comparand));
                break;
        }
    }

    /// <summary>Object's Equals, GetHashCode or ToString on a delegate, after its null check: the delegates' own, object's own ToString.</summary>
    private void CallDelegateObjectMethod(string name)
    {
        var functions = _program.DelegateFunctions;
        switch (name)
        {
            case "Equals":
                var other = PopObject();
                Push(StackValue.OfBool(_ir.Call(functions.SameAs, PopObjectNotNull(), other)));
                break;

            case "GetHashCode":
                Push(_ir.Call(functions.HashCode, PopObjectNotNull()));
                break;

            default:
                PushObject(_ir.Call(_program.ObjectToString, PopObjectNotNull()));
                break;
        }
    }

    /// <summary>
    /// isinst or castclass to a delegate type (or System.Delegate, System.MulticastDelegate): the object itself when its
    /// variable is of that type already, or a cast that follows Combine or Remove; else the test against the type's
    /// descriptor, as against a class's.
    /// </summary>
    private void DelegateTypeTest(IlType target, bool cast)
    {
        var assembly = _method.Assembly;
        var index = _body.IndexOf(_instruction.Offset);
        var known = StackType(0);
        var combined = index > 0 && _body.Instructions[index - 1] is { Operand: IlMethodOperand { Method: var previous } }
            && Delegates.Call(previous, assembly) is DelegateCall.Combine or DelegateCall.Remove;
        var reference = ObjectFromStack(PopValue(), _instruction.Name);
        var already = known is not null && Delegates.IsDelegateType(known, assembly)
            && ((Delegates.IsBaseType(target) && !Delegates.IsBaseType(known))
                || Delegates.Canonical(known, assembly).Equals(Delegates.Canonical(target, assembly)));
        if ((cast && combined) || already || ObjectTypes.IsSystemObject(target))
        {
            PushObject(reference);
            return;
        }

        var descriptor = _program.DelegateTypeDescriptor(target, assembly);
        PushObject(_ir.Call(_program.ObjectFunctions.TypeTest(TirTypeDescriptorKind.Class, cast), reference,
            _ir.AddressOf(descriptor)));
    }
}
