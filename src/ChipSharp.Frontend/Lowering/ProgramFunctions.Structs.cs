using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

internal sealed partial class ProgramFunctions
{
    /// <summary>
    /// The parameter a struct's instance method or constructor takes first: <c>this</c>, a reference to the struct
    /// (a ptr), which is IL argument 0. Null for a static method.
    /// </summary>
    private static (string Name, TirType Type)? ThisParameter(IlMethod method)
    {
        if (!ValueTypes.IsStructInstanceMethod(method))
        {
            return null;
        }

        if (ValueTypes.ProblemOf(method.DeclaringType) is { } problem)
        {
            throw LoweringErrors.In(method, $"{problem.Text}, which is not supported yet");
        }

        // C# lets a parameter be called @this
        return (method.Parameters.Any(parameter => parameter.Name == "this") ? "<this>" : "this", TirType.Ptr);
    }
}
