using System.Reflection;
using System.Reflection.Emit;

namespace ChipSharp.Tests.Lowering;

public sealed partial class IlToTirOpCodeTests
{
    private sealed partial class OpCodeProgram
    {
        /// <summary>
        /// A class Counter { int Count; } with a constructor that calls object's and an instance method, made with newobj
        /// and called with callvirt, as C# calls every instance method of a class.
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
        }
    }
}
