using System.Reflection;
using System.Reflection.Emit;

namespace ChipSharp.Tests.Lowering;

public sealed partial class IlToTirOpCodeTests
{
    private sealed partial class OpCodeProgram
    {
        /// <summary>
        /// A class Counter { int Count; } with a constructor that calls object's and an instance method, made with newobj
        /// and called with callvirt, as C# calls every instance method of a class; tested against its class with isinst
        /// and castclass; its methods made into delegates with ldftn and (a virtual one) ldvirtftn.
        /// </summary>
        private void AddClasses()
        {
            var counter = Assembly.Class("Counter");
            var count = counter.DefineField("Count", typeof(int), FieldAttributes.Public);
            var constructor = counter.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(int)]);
            var body = constructor.GetILGenerator();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Call, typeof(object).GetConstructor([])!);
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldarg_1);
            body.Emit(OpCodes.Stfld, count);
            body.Emit(OpCodes.Ret);
            var add = counter.DefineMethod("Add", MethodAttributes.Public, typeof(int), [typeof(int)]);
            body = add.GetILGenerator();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldfld, count);
            body.Emit(OpCodes.Ldarg_1);
            body.Emit(OpCodes.Add);
            body.Emit(OpCodes.Stfld, count);
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldfld, count);
            body.Emit(OpCodes.Ret);

            // new Counter(a), then Add(b) twice through callvirt, the second on the object kept in a local
            Add("Objects", typeof(int), [typeof(int), typeof(int)], il =>
            {
                il.DeclareLocal(counter);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, add);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, add);
                il.Emit(OpCodes.Ret);
            }, [5, 3], [-1, 10]);

            // ((Counter)(object)new Counter(a) as Counter).Count
            Add("TypeTests", typeof(int), [typeof(int)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Castclass, counter);
                il.Emit(OpCodes.Isinst, counter);
                il.Emit(OpCodes.Ldfld, count);
                il.Emit(OpCodes.Ret);
            }, [7]);

            // a virtual Get, then Func<int, int> of Add (ldftn) and Func<int> of Get (ldvirtftn), each invoked
            var get = counter.DefineMethod("Get", MethodAttributes.Public | MethodAttributes.Virtual |
                MethodAttributes.NewSlot | MethodAttributes.HideBySig, typeof(int), []);
            body = get.GetILGenerator();
            body.Emit(OpCodes.Ldarg_0);
            body.Emit(OpCodes.Ldfld, count);
            body.Emit(OpCodes.Ret);
            Add("Delegates", typeof(int), [typeof(int), typeof(int)], il =>
            {
                il.DeclareLocal(counter);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Ldftn, add);
                il.Emit(OpCodes.Newobj, typeof(Func<int, int>).GetConstructors()[0]);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Callvirt, typeof(Func<int, int>).GetMethod("Invoke")!);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldvirtftn, get);
                il.Emit(OpCodes.Newobj, typeof(Func<int>).GetConstructors()[0]);
                il.Emit(OpCodes.Callvirt, typeof(Func<int>).GetMethod("Invoke")!);
                il.Emit(OpCodes.Ret);
            }, [5, 3], [-2, 7]);

            // a reference to an object: Counter kept = new Counter(a); ref Counter r = ref copy; r = kept; r.Count
            Add("ObjectReferences", typeof(int), [typeof(int)], il =>
            {
                il.DeclareLocal(counter);
                il.DeclareLocal(counter);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Newobj, constructor);
                il.Emit(OpCodes.Stloc_0);
                il.Emit(OpCodes.Ldloca_S, (byte)1);
                il.Emit(OpCodes.Ldloc_0);
                il.Emit(OpCodes.Stind_Ref);
                il.Emit(OpCodes.Ldloca_S, (byte)1);
                il.Emit(OpCodes.Ldind_Ref);
                il.Emit(OpCodes.Ldfld, count);
                il.Emit(OpCodes.Ret);
            }, [9]);

            // throw: if (a < 0) throw new InvalidOperationException(); return a (run with a number that does not throw)
            Add("Throws", typeof(int), [typeof(int)], il =>
            {
                var fine = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Bge_S, fine);
                il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([])!);
                il.Emit(OpCodes.Throw);
                il.MarkLabel(fine);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ret);
            }, [4]);
        }
    }
}
