using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>The functions of the program's classes (see <see cref="ClassRules"/>): their instance methods and constructors.</summary>
internal sealed partial class ProgramFunctions
{
    /// <summary>
    /// The parameter a class's instance method or constructor takes first: <c>this</c>, the object (a ref), which is IL
    /// argument 0. Null for any other method. A virtual method is an error: it arrives with virtual calls, which give it
    /// its place in the virtual method table.
    /// </summary>
    private static (string Name, TirType Type)? ClassThisParameter(IlMethod method)
    {
        if (method.IsStatic || !ObjectTypes.IsClass(method.DeclaringType))
        {
            return null;
        }

        if (ClassRules.IsOverridable(method))
        {
            throw LoweringErrors.In(method, "virtual methods (virtual, override, abstract) are not supported yet (they " +
                "arrive with M3, virtual calls)");
        }

        // C# lets a parameter be called @this
        return (method.Parameters.Any(parameter => parameter.Name == "this") ? "<this>" : "this", TirType.Ref);
    }

    /// <summary>The IL type of an instance method's <c>this</c>: the class itself for a class, a reference to the struct for a struct.</summary>
    private static IlType ThisType(IlMethod method) =>
        ObjectTypes.IsClass(method.DeclaringType) ? method.DeclaringType.Type : ValueTypes.ThisType(method);
}
