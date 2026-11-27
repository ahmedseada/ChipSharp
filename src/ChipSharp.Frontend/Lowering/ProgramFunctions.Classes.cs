using ChipSharp.Frontend.Il;
using ChipSharp.Ir;

namespace ChipSharp.Frontend.Lowering;

/// <summary>
/// The functions of the program's classes (see <see cref="ClassRules"/>): their instance methods (virtual ones too) and
/// constructors, and the default methods of interfaces.
/// </summary>
internal sealed partial class ProgramFunctions
{
    /// <summary>
    /// The parameter a class's instance method or constructor (or an interface's default method) takes first:
    /// <c>this</c>, the object (a ref), which is IL argument 0. Null for any other method. A virtual method is a
    /// function like any other: the virtual method tables of the classes that run it hold its address.
    /// </summary>
    private static (string Name, TirType Type)? ClassThisParameter(IlMethod method)
    {
        if (method.IsStatic || !(ObjectTypes.IsClass(method.DeclaringType) || method.DeclaringType.IsInterface))
        {
            return null;
        }

        // C# lets a parameter be called @this
        return (method.Parameters.Any(parameter => parameter.Name == "this") ? "<this>" : "this", TirType.Ref);
    }

    /// <summary>
    /// The IL type of an instance method's <c>this</c>: the class (or interface) itself for a class (or an interface), a
    /// reference to the struct for a struct.
    /// </summary>
    private static IlType ThisType(IlMethod method) =>
        ObjectTypes.IsClass(method.DeclaringType) || method.DeclaringType.IsInterface
            ? method.DeclaringType.Type
            : ValueTypes.ThisType(method);
}
