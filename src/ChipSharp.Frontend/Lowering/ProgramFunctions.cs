using ChipSharp.Abstractions;
using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>A method of the program with the TIR function it becomes.</summary>
/// <param name="Method">The IL method.</param>
/// <param name="Body">Its IL.</param>
/// <param name="Function">Its TIR function, still without blocks until its body is translated.</param>
/// <param name="Arguments">One per IL argument, by argument number.</param>
internal sealed record LoweredMethod(
    IlMethod Method,
    IlMethodBody Body,
    TirFunction Function,
    IReadOnlyList<LoweredArgument> Arguments);

/// <summary>An IL argument: its name, its IL type and its TIR parameter, which is null when it was left out
/// (the entry point's unused <c>string[] args</c>).</summary>
internal sealed record LoweredArgument(string Name, IlType Type, TirParameter? Parameter);

/// <summary>What a <c>call</c> calls: a TIR function, and the IL types of its parameters and result.</summary>
/// <param name="Function">The TIR function, defined or external.</param>
/// <param name="ParameterTypes">The IL type of each parameter, which says how a stack value is narrowed for it.</param>
/// <param name="ReturnType">The IL return type, which says how the result is widened onto the stack.</param>
/// <param name="Name">The method as messages show it, e.g. "Program.Square(int32)".</param>
internal sealed record CallTarget(
    TirFunction Function,
    IReadOnlyList<IlType> ParameterTypes,
    IlType ReturnType,
    string Name);

/// <summary>
/// The TIR functions of one program: one per method, made from its signature before any body is translated, and
/// one per runtime function a library call becomes, added when a call first needs it and shared after that.
/// </summary>
internal sealed partial class ProgramFunctions(TirModule module)
{
    private static readonly IlArrayType StringArray = new(IlType.String);

    private readonly Dictionary<IlMethod, LoweredMethod> _methods = [];
    private readonly Dictionary<string, TirFunction> _externals = new(StringComparer.Ordinal);

    /// <summary>Adds the TIR function of <paramref name="method"/>, checking that M0 can compile its signature.</summary>
    public void Declare(IlMethod method, bool isEntryPoint)
    {
        if (_methods.ContainsKey(method))
        {
            throw new ArgumentException($"{method} appears twice among the program's methods.", nameof(method));
        }

        if (!method.IsStatic && !ValueTypes.IsStructInstanceMethod(method))
        {
            throw LoweringErrors.In(method, $"instance methods are not supported yet ({SupportedSubset.Milestone} " +
                "compiles static methods and the methods of structs)");
        }

        if (method.GenericParameters.Count > 0 || method.DeclaringType.GenericParameters.Count > 0)
        {
            throw LoweringErrors.In(method, "generic methods and the methods of generic types are not supported yet " +
                $"(they arrive after {SupportedSubset.Milestone})");
        }

        if (method.Signature.CallingConvention != IlCallingConvention.Default)
        {
            throw LoweringErrors.In(method,
                $"the {method.Signature.CallingConvention} calling convention is not supported");
        }

        var body = method.Body ?? throw LoweringErrors.In(method,
            "it has no IL to compile (it is abstract, extern or implemented by the runtime)");
        var returnType = LoweringErrors.MapType(method, method.ReturnType, "it returns");

        var parameters = new List<(string Name, TirType Type)>();
        var self = ThisParameter(method);
        if (self is { } thisParameter)
        {
            parameters.Add(thisParameter);
        }

        var leftOut = new HashSet<int>();
        foreach (var parameter in method.Parameters)
        {
            if (isEntryPoint && parameter.Type == StringArray)
            {
                // C#'s top-level statements give Main a string[] args; M0 has no arrays, so an unused one is left out
                if (body.Instructions.FirstOrDefault(i => i.ArgumentIndex == parameter.Index) is { } use)
                {
                    throw LoweringErrors.At(method, use.Offset,
                        $"the entry point uses its string[] {parameter.Name}, and arrays are not supported yet " +
                        $"({SupportedSubset.Milestone} leaves an unused args out)");
                }

                leftOut.Add(parameter.Index);
                continue;
            }

            var type = LoweringErrors.MapType(method, parameter.Type, $"parameter '{parameter.Name}' has type");
            parameters.Add((parameter.Name, type));
        }

        var function = module.AddFunction(method.DisplayName, returnType, parameters);
        var arguments = new List<LoweredArgument>();
        var next = 0;
        if (self is { } thisArgument)
        {
            arguments.Add(new LoweredArgument(thisArgument.Name, ValueTypes.ThisType(method), function.Parameters[next++]));
        }

        foreach (var parameter in method.Parameters)
        {
            var tir = leftOut.Contains(parameter.Index) ? null : function.Parameters[next++];
            arguments.Add(new LoweredArgument(parameter.Name, parameter.Type, tir));
        }

        _methods.Add(method, new LoweredMethod(method, body, function, arguments));
    }

    /// <summary>The method as <see cref="Declare"/> made it.</summary>
    public LoweredMethod Get(IlMethod method) => _methods[method];

    /// <summary>
    /// What the <c>call</c> at <paramref name="call"/> in <paramref name="caller"/> calls: a method of the program,
    /// or the runtime function of a supported library method.
    /// </summary>
    public CallTarget Callee(IlMethod caller, IlInstruction call)
    {
        var reference = ((IlMethodOperand)call.Operand!).Method;
        if (reference.Definition is { } definition)
        {
            if (!_methods.TryGetValue(definition, out var callee))
            {
                throw LoweringErrors.At(caller, call.Offset,
                    $"the call to {definition} is not supported: that method is not among the program's methods");
            }

            if (callee.Arguments.Any(a => a.Parameter is null))
            {
                throw LoweringErrors.At(caller, call.Offset,
                    $"the call to {definition} is not supported: it passes the string[] the entry point leaves out");
            }

            // a struct method's this comes first
            return new CallTarget(callee.Function, [.. callee.Arguments.Select(argument => argument.Type)],
                definition.ReturnType, definition.ToString());
        }

        if (KnownExternals.TryMap(reference, out var external))
        {
            return new CallTarget(External(external), reference.ParameterTypes, reference.ReturnType,
                external.LibraryMethod);
        }

        var known = string.Join(", ", KnownExternals.All.Select(f => f.LibraryMethod));
        throw LoweringErrors.At(caller, call.Offset,
            $"the call to {reference.DisplayName}({string.Join(", ", reference.ParameterTypes)}) is not supported: " +
            $"of the library, {SupportedSubset.Milestone} can call {known}");
    }

    private TirFunction External(ExternalFunction external)
    {
        if (!_externals.TryGetValue(external.DisplayName, out var function))
        {
            var count = external.ParameterTypes.Count;
            var parameters = external.ParameterTypes
                .Select((type, i) => (count == 1 ? "value" : $"value{i + 1}", type));
            function = module.AddExternalFunction(external.DisplayName, external.ReturnType, parameters);
            _externals.Add(external.DisplayName, function);
        }

        return function;
    }
}
