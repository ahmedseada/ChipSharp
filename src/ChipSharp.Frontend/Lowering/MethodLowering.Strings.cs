using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The methods of string, char and parsing (see <see cref="StringMethods"/>): a call of one is a call of its runtime
/// function, which takes the string it is called on first; callvirt checks that string for null first, as .NET does
/// (C# calls a literal's methods with call, which needs no check). An array or span of chars (or an array of strings)
/// is passed as its address and length; an array the runtime makes (char[], string[]) comes back as its object and is
/// turned into an array value. <c>s.Length</c> is the string's length field, and a string's ToString the string itself.
/// <c>s.AsSpan()</c> is a span of the string's UTF-16 chars (see <see cref="Strings"/>).
/// </summary>
internal sealed partial class MethodLowering
{
    private readonly Dictionary<IlInstruction, StringMethod?> _stringCalls = [];

    /// <summary>True for a call of a method of strings, chars or parsing that <see cref="LowerStringInstruction"/> translates.</summary>
    private bool IsStringInstruction(IlInstruction instruction) => StringCall(instruction) is not null
        || (instruction.OpCode == IlOpCode.Constrained && StringMethods.IsPrefix(_method, _body.IndexOf(instruction.Offset)));

    private StringMethod? StringCall(IlInstruction instruction)
    {
        if (!_stringCalls.TryGetValue(instruction, out var method))
        {
            method = instruction.OpCode is IlOpCode.Call or IlOpCode.Callvirt or IlOpCode.Newobj
                ? StringMethods.Find(_method, _body.IndexOf(instruction.Offset))
                : null;
            _stringCalls.Add(instruction, method);
        }

        return method;
    }

    private void LowerStringInstruction(IlInstruction instruction)
    {
        // the constrained. prefix of a span's ToString: the call does it all
        if (StringCall(instruction) is not { } method)
        {
            return;
        }

        var reference = ((IlMethodOperand)instruction.Operand!).Method;
        var isNew = instruction.OpCode == IlOpCode.Newobj;
        var values = new StackValue[reference.ParameterTypes.Count + (reference.HasThis && !isNew ? 1 : 0)];
        for (var i = values.Length - 1; i >= 0; i--)
        {
            values[i] = PopValue();
        }

        var arguments = new List<TirValue>();
        var next = 0;
        if (reference.HasThis && !isNew)
        {
            var self = values[next++];
            if (method.Type.StartsWith("System.", StringComparison.Ordinal))
            {
                // a span's own method: this is the span's address
                AddChars(arguments, _ir.Load(StackArrays.ArrayType, Materialize(self)));
            }
            else
            {
                var text = ObjectFromStack(self, $"the string {method.CSharpName} is called on");
                arguments.Add(instruction.OpCode == IlOpCode.Callvirt || method.Kind == StringCallKind.Length
                    ? NullCheck(text)
                    : text);
            }
        }

        switch (method.Kind)
        {
            case StringCallKind.Length:
                Push(_ir.ArrayLength(arguments[0]));
                return;

            case StringCallKind.Self:
                PushObject(arguments[0]);
                return;

            case StringCallKind.Span:
                StringSpan(values);
                return;
        }

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            var key = method.Parameters[i];
            var value = values[next++];
            if (StringMethods.IsArrayValue(key))
            {
                AddChars(arguments, Materialize(value));
            }
            else
            {
                arguments.Add(FromStack(value, StringMethods.RuntimeType(key), reference.ParameterTypes[i],
                    $"argument {i + 1} of {method.CSharpName}"));
            }
        }

        TirTypeDescriptor? made = null;
        if (method.Kind == StringCallKind.ArrayResult)
        {
            made = _program.HeapArrayDescriptor(reference.ReturnType, _method.Assembly);
            arguments.Add(_ir.AddressOf(made));
        }

        var call = _ir.Call(_program.RuntimeFunction(StringMethods.RuntimeFunction(method)), arguments);
        if (made is not null)
        {
            Push(_ir.Call(_program.HeapArrayFunctions.FromObject(made), call));
        }
        else if (isNew)
        {
            PushObject(call);
        }
        else if (call.HasResult)
        {
            Push(ToStack(call, reference.ReturnType));
        }
    }

    /// <summary>An array value or span (of chars, or of strings) as the address of its elements and their number.</summary>
    private void AddChars(List<TirValue> arguments, TirValue view)
    {
        arguments.Add(_ir.Call(_program.Arrays.Data, view));
        arguments.Add(_ir.Call(_program.Arrays.Length, view));
    }

    /// <summary>
    /// <c>MemoryExtensions.AsSpan(string)</c>, with a start and a length: the runtime checks them as .NET does and gives
    /// the address of the chars, then the span's length; null is the empty span.
    /// </summary>
    private void StringSpan(StackValue[] values)
    {
        var text = ObjectFromStack(values[0], "the string AsSpan is called on");
        var start = values.Length > 1 ? FromStack(values[1], TirType.I32, IlType.Int32, "the start") : _ir.Const(TirType.I32, 0);
        var length = values.Length > 2 ? FromStack(values[2], TirType.I32, IlType.Int32, "the length") : _ir.Const(TirType.I32, 0);
        var hasLength = _ir.Const(TirType.I32, values.Length > 2 ? 1 : 0);
        TirType[] parameters = [TirType.Ref, TirType.I32, TirType.I32, TirType.I32];
        var chars = _ir.Call(_program.RuntimeFunction("StringSpanChars", TirType.Ptr, parameters), text, start, length, hasLength);
        var count = _ir.Call(_program.RuntimeFunction("StringSpanLength", TirType.I32, parameters), text, start, length, hasLength);
        Push(_ir.Call(_program.Arrays.Make, chars, count));
    }
}
