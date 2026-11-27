using System.Reflection;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Generics;

/// <summary>
/// The constraints of ChipSharp.Library's generic methods, which may say more than .NET's own (Array.Sort&lt;T&gt; is
/// <c>where T : struct, IComparable&lt;T&gt;</c> there, for the numbers it is written for): an instance of one is made
/// only for type arguments that meet them, so a call with others stays a library method that the whole-program pass
/// judges where it is called (Array.Sort of an array of enums), and the library's code is never compiled for a type it
/// was not written for.
/// </summary>
internal static class LibraryConstraints
{
    /// <summary>
    /// True when <paramref name="methodArguments"/>, named in <paramref name="owner"/>'s IL, meet the constraints of
    /// <paramref name="definition"/>'s generic parameters: <c>struct</c> a value, <c>class</c> a reference, and each
    /// interface (<c>IComparable&lt;T&gt;</c>) one the type implements (a number or a bool those of itself). Always true
    /// for a method of the program's.
    /// </summary>
    public static bool Met(IlMethod definition, IReadOnlyList<IlType> typeArguments, IReadOnlyList<IlType> methodArguments,
        IlAssembly owner)
    {
        if (definition.GenericParameters.Count == 0 || !LibrarySubstitutes.Defines(definition)
            || methodArguments.Count != definition.GenericParameters.Count)
        {
            return true;
        }

        var substitution = new Substitution(typeArguments, methodArguments);
        var constraints = definition.Assembly.Constraints(definition);
        for (var i = 0; i < constraints.Count; i++)
        {
            var argument = StackArrays.Primitive(ValueTypes.WithoutModifiers(methodArguments[i]));
            var isValue = ValueTypes.TryMapType(argument, owner, out _) && !FixedText.IsText(argument);
            var (attributes, types) = constraints[i];
            if (((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0 && !isValue)
                || ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0 && isValue))
            {
                return false;
            }

            foreach (var type in types.Select(substitution.Apply))
            {
                if (type is IlGenericInstanceType { Definition: IlNamedType { IsDefinedInThisAssembly: false } } wanted
                    && !(argument is IlPrimitiveType { Kind: >= IlPrimitiveKind.Bool and <= IlPrimitiveKind.Float64 }
                        && wanted.Arguments.SequenceEqual([argument]))
                    && !Specialization.Implements(argument, wanted, owner))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
