using ChipSharp.Frontend.Il;

namespace ChipSharp.Frontend.Analysis;

/// <summary>
/// The methods of System.Array in the analysis (see <see cref="ArrayMethods"/>): which calls compile, and with what. An
/// array passed to one is neither kept nor null-tested here: the method itself says what null is (.NET's
/// ArgumentNullException), and only Copy's destination and the arrays Clear, Fill, Reverse and Resize change have their
/// elements written.
/// </summary>
internal static partial class ArrayEscapeAnalysis
{
    private sealed partial class Flow
    {
        /// <summary>
        /// A call of a method of System.Array (but <c>Array.Empty</c>, which has its own case): one ChipSharp compiles
        /// itself is fine (Resize and Clone make an array, which needs a heap), with the element types and arguments it
        /// takes; one that takes a delegate arrives with delegates; any other is not available yet, saying what to write
        /// for now. <paramref name="stack"/> holds the arguments, the last on top.
        /// </summary>
        private void ArrayMethod(IlInstruction call, IlMethodReference? callee, List<Value> stack)
        {
            if (callee is null)
            {
                return;
            }

            if (ArrayMethods.IsSortOrSearch(callee, out var sorted))
            {
                if (!ArrayMethods.IsSortable(sorted))
                {
                    Verdict(call, Unsupported.ArraySortElements);
                }

                return;
            }

            if (callee is not { Definition: null, DeclaringType: IlNamedType { Namespace: "System", Name: "Array" } type }
                || type.IsDefinedInThisAssembly)
            {
                return;
            }

            if (ArrayMethods.Call(callee) is not { } known)
            {
                Verdict(call, Unsupported.ArrayMethod(CSharpNames.Method(callee)));
                return;
            }

            Verdict(call, known.Operation switch
            {
                ArrayOperation.WithDelegate => Unsupported.ArrayMethodsWithDelegates,
                ArrayOperation.IndexOf or ArrayOperation.IndexOfFrom or ArrayOperation.IndexOfRange
                    or ArrayOperation.LastIndexOf or ArrayOperation.LastIndexOfFrom or ArrayOperation.LastIndexOfRange =>
                    ArrayMethods.ForIndexOf(known.Element!, _method.Assembly),
                ArrayOperation.Copy or ArrayOperation.CopyLong or ArrayOperation.CopyTo or ArrayOperation.CopyToLong =>
                    SameElements(stack, 3, 2),
                ArrayOperation.CopyRange or ArrayOperation.CopyRangeLong => SameElements(stack, 5, 3),
                ArrayOperation.Resize => IsResizedVariable(call) ? HeapUse(Unsupported.ArrayResize)
                    : Unsupported.ResizeReference,
                ArrayOperation.Clone => ClonedAndCast(call, stack) ? HeapUse(Unsupported.ArrayClone)
                    : Unsupported.CloneWithoutCast,
                _ => null,
            });
        }

        /// <summary>
        /// The castclass right after <c>array.Clone()</c>, to the array's own type: the clone as an array of that type,
        /// one that was kept (it is on the heap).
        /// </summary>
        private void CloneCast(IlInstruction castclass, Func<Value> pop, List<Value> stack)
        {
            Verdict(castclass, null);
            pop();
            stack.Add(Loaded(ArrayMethods.CastType(castclass)));
        }

        /// <summary>
        /// The verdict on an <c>ldflda</c> or <c>ldsflda</c> of a field that holds arrays: fine when only Array.Resize
        /// takes the reference (it assigns the field a new array), else <paramref name="otherwise"/>.
        /// </summary>
        private Unsupported? FieldReference(IlInstruction instruction, Unsupported? otherwise) =>
            ArrayMethods.IsResizeReference(_method, _body.IndexOf(instruction.Offset)) ? null : otherwise;

        /// <summary>
        /// Null when the arrays a copy takes, <paramref name="source"/> and <paramref name="destination"/> deep on the
        /// stack (1 is the top), have the same element type, or one of them is not known here (the translation checks
        /// it then); else the copy between element types, which .NET converts and ChipSharp does not yet.
        /// </summary>
        private Unsupported? SameElements(List<Value> stack, int source, int destination)
        {
            if (stack.Count < source)
            {
                return null;
            }

            var (from, to) = (stack[^source].Type, stack[^destination].Type);
            return from is not null && to is not null && HeapArrays.ElementType(from) is { } a
                && HeapArrays.ElementType(to) is { } b && !ValueTypes.WithoutModifiers(a).Equals(ValueTypes.WithoutModifiers(b))
                ? Unsupported.ArrayCopyTypes
                : null;
        }

        /// <summary>True when the reference Array.Resize takes is the address of a local, a parameter or a field, made for it.</summary>
        private bool IsResizedVariable(IlInstruction call)
        {
            var index = _body.IndexOf(call.Offset);
            for (var i = index - 1; i >= 0 && _blockOf[i] == _blockOf[index]; i--)
            {
                if (ArrayMethods.IsResizeReference(_method, i) && ArrayMethods.ConsumerOf(_method, i)?.Consumer == call)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when <c>array.Clone()</c> is cast at once to the array's own type (when that is known here).</summary>
        private bool ClonedAndCast(IlInstruction call, List<Value> stack)
        {
            var index = _body.IndexOf(call.Offset);
            if (index + 1 >= _body.Instructions.Count || !ArrayMethods.IsCloneCast(_body, index + 1)
                || ArrayMethods.CastType(_body.Instructions[index + 1]) is not { } cast)
            {
                return false;
            }

            return stack.Count == 0 || stack[^1].Type is not { } type || HeapArrays.ElementType(type) is not { } element
                || ValueTypes.WithoutModifiers(element).Equals(ValueTypes.WithoutModifiers(cast.ElementType));
        }
    }
}
