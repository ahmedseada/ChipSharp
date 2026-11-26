using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace ChipSharp.Tests.Lowering;

/// <summary>The array instructions of the subset (see StackArrays), in methods that make their own arrays.</summary>
public sealed partial class IlToTirOpCodeTests
{
    private sealed partial class OpCodeProgram
    {
        private static readonly MethodInfo InitializeArray = typeof(RuntimeHelpers).GetMethod(
            nameof(RuntimeHelpers.InitializeArray), [typeof(Array), typeof(RuntimeFieldHandle)])!;

        private void AddArrays()
        {
            // each element instruction: the element stored with stelem, read back with the signed and unsigned ldelem
            Elements("Bytes", typeof(sbyte), typeof(int), OpCodes.Stelem_I1, [OpCodes.Ldelem_I1, OpCodes.Ldelem_U1],
                [200], [-1], [127]);
            Elements("Shorts", typeof(short), typeof(int), OpCodes.Stelem_I2, [OpCodes.Ldelem_I2, OpCodes.Ldelem_U2],
                [40_000], [-2]);
            Elements("Ints", typeof(int), typeof(int), OpCodes.Stelem_I4, [OpCodes.Ldelem_I4, OpCodes.Ldelem_U4],
                [5], [-7]);
            Elements("Longs", typeof(long), typeof(long), OpCodes.Stelem_I8, [OpCodes.Ldelem_I8], [3_000_000_000L]);
            Elements("Floats", typeof(float), typeof(float), OpCodes.Stelem_R4, [OpCodes.Ldelem_R4], [1.5f], [-0f]);
            Elements("Doubles", typeof(double), typeof(double), OpCodes.Stelem_R8, [OpCodes.Ldelem_R8], [0.1],
                [double.NaN]);
            Add("NativeInts", typeof(long), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Newarr, typeof(nint));
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stelem_I);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ldelem_I);
                il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Ret);
            }, [-5]);
            Add("Typed", typeof(int), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldc_I4_3);
                il.Emit(OpCodes.Newarr, typeof(int));
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Stelem, typeof(int));
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Ldelem, typeof(int));
                il.Emit(OpCodes.Ret);
            }, [9]);
            Add("Bools", typeof(bool), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Newarr, typeof(bool));
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_5);
                il.Emit(OpCodes.Cgt);
                il.Emit(OpCodes.Stelem_I1);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldelem_U1);
                il.Emit(OpCodes.Ret);
            }, [7], [3]);
            Add("Length", typeof(int), [], il =>
            {
                il.Emit(OpCodes.Ldc_I4_7);
                il.Emit(OpCodes.Newarr, typeof(int));
                il.Emit(OpCodes.Ldlen);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Ret);
            }, [[]]);

            // an element through its address (ldelema), as C# writes a[i] += x
            ThroughAddress("AddressOfByte", typeof(sbyte), typeof(int), OpCodes.Stind_I1, OpCodes.Ldind_I1, [300]);
            ThroughAddress("AddressOfUByte", typeof(byte), typeof(int), OpCodes.Stind_I1, OpCodes.Ldind_U1, [300]);
            ThroughAddress("AddressOfShort", typeof(short), typeof(int), OpCodes.Stind_I2, OpCodes.Ldind_I2, [40_000]);
            ThroughAddress("AddressOfUShort", typeof(ushort), typeof(int), OpCodes.Stind_I2, OpCodes.Ldind_U2, [-1]);
            ThroughAddress("AddressOfInt", typeof(int), typeof(int), OpCodes.Stind_I4, OpCodes.Ldind_I4, [-8]);
            ThroughAddress("AddressOfUInt", typeof(uint), typeof(uint), OpCodes.Stind_I4, OpCodes.Ldind_U4, [-8]);
            ThroughAddress("AddressOfLong", typeof(long), typeof(long), OpCodes.Stind_I8, OpCodes.Ldind_I8,
                [-5_000_000_000L]);
            ThroughAddress("AddressOfFloat", typeof(float), typeof(float), OpCodes.Stind_R4, OpCodes.Ldind_R4, [2.5f]);
            ThroughAddress("AddressOfDouble", typeof(double), typeof(double), OpCodes.Stind_R8, OpCodes.Ldind_R8,
                [1e300]);
            Add("AddressOfNative", typeof(long), [typeof(int)], il =>
            {
                il.DeclareLocal(typeof(nint[]));
                il.Emit(OpCodes.Ldc_I4_3);
                il.Emit(OpCodes.Newarr, typeof(nint));
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Ldelema, typeof(nint));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stind_I);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Ldelema, typeof(nint));
                il.Emit(OpCodes.Ldind_I);
                il.Emit(OpCodes.Conv_I8);
                il.Emit(OpCodes.Ret);
            }, [-12]);

            // a long index, converted the way C# converts it (conv.ovf.i, or conv.ovf.i.un for a ulong)
            Index("LongIndex", typeof(long), OpCodes.Conv_Ovf_I, [3L], [0L]);
            Index("ULongIndex", typeof(ulong), OpCodes.Conv_Ovf_I_Un, [2UL]);

            // an array initializer: the data of a field, copied by RuntimeHelpers.InitializeArray
            var data = Assembly.Data("Primes", [.. new[] { 2, 3, 5, 7 }.SelectMany(BitConverter.GetBytes)]);
            Add("Initializer", typeof(int), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldc_I4_4);
                il.Emit(OpCodes.Newarr, typeof(int));
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldtoken, data);
                il.Emit(OpCodes.Call, InitializeArray);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldelem_I4);
                il.Emit(OpCodes.Ret);
            }, [0], [3]);
        }

        /// <summary>An array of two elements of <paramref name="element"/>, both set to the argument, read back by each load.</summary>
        private void Elements(string name, Type element, Type type, OpCode store, OpCode[] loads, params object[][] calls) =>
            Add(name, type, [type], il =>
            {
                il.DeclareLocal(element.MakeArrayType());
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Newarr, element);
                il.Emit(OpCodes.Stloc_0);
                for (var i = 0; i < 2; i++)
                {
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(store);
                }

                // the loads, each a digit apart in the result: first * 1000 + second
                for (var i = 0; i < loads.Length; i++)
                {
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(loads[i]);
                    if (i > 0)
                    {
                        il.Emit(OpCodes.Add);
                    }

                    if (i + 1 < loads.Length)
                    {
                        il.Emit(OpCodes.Ldc_I4, 1000);
                        il.Emit(OpCodes.Mul);
                    }
                }

                il.Emit(OpCodes.Ret);
            }, calls);

        /// <summary>
        /// a[2] = argument through ldelema and stind, then a[2] read through that address and through a new ldelema,
        /// added: twice the element as the store cut it down.
        /// </summary>
        private void ThroughAddress(string name, Type element, Type type, OpCode store, OpCode load, params object[][] calls)
        {
            var parameter = type == typeof(long) || type == typeof(float) || type == typeof(double) ? type : typeof(int);
            Add(name, type, [parameter], il =>
            {
                il.DeclareLocal(element.MakeArrayType());
                il.Emit(OpCodes.Ldc_I4_3);
                il.Emit(OpCodes.Newarr, element);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Ldelema, element);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(store);
                il.Emit(load);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Ldelema, element);
                il.Emit(load);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ret);
            }, calls);
        }

        /// <summary>{ 10, 20, 30, 40 }[argument], the argument converted to native int by <paramref name="convert"/>.</summary>
        private void Index(string name, Type index, OpCode convert, params object[][] calls) =>
            Add(name, typeof(int), [index], il =>
            {
                il.DeclareLocal(typeof(int[]));
                il.Emit(OpCodes.Ldc_I4_4);
                il.Emit(OpCodes.Newarr, typeof(int));
                il.Emit(OpCodes.Stloc_0);
                for (var i = 0; i < 4; i++)
                {
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldc_I4, 10 * (i + 1));
                    il.Emit(OpCodes.Stelem_I4);
                }

                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(convert);
                il.Emit(OpCodes.Ldelem_I4);
                il.Emit(OpCodes.Ret);
            }, calls);
    }
}
