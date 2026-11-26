using System.Reflection.Emit;

namespace ChipSharp.Tests.Lowering;

/// <summary>The fixed-text instructions of the subset (see FixedText): string literals and null, tested and compared.</summary>
public sealed partial class IlToTirOpCodeTests
{
    private sealed partial class OpCodeProgram
    {
        private void AddText()
        {
            // ldstr and ldnull, returned through a branch on a bool; Main prints what comes back (null as nothing)
            Add("Pick", typeof(string), [typeof(bool)], il =>
            {
                var empty = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Brfalse_S, empty);
                il.Emit(OpCodes.Ldstr, "road : 1");
                il.Emit(OpCodes.Ret);
                il.MarkLabel(empty);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
            }, [true], [false]);

            // text compared with null as C# compiles s == null and s != null, and brtrue on it (s ?? ...)
            Add("IsNull", typeof(bool), [typeof(string)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Ret);
            }, ["x"], [null!]);
            Add("IsNotNull", typeof(bool), [typeof(string)], il =>
            {
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Cgt_Un);
                il.Emit(OpCodes.Ret);
            }, ["x"], [null!]);
            Add("OrNone", typeof(string), [typeof(string)], il =>
            {
                var done = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Brtrue_S, done);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldstr, "none");
                il.MarkLabel(done);
                il.Emit(OpCodes.Ret);
            }, ["some"], [null!]);

            // equal literals are one reference, as .NET interns them; beq and bne.un compare references too
            Add("SameLiteral", typeof(bool), [], il =>
            {
                il.Emit(OpCodes.Ldstr, "same");
                il.Emit(OpCodes.Ldstr, "same");
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Ret);
            }, Array.Empty<object>());
            Add("OtherLiteral", typeof(int), [typeof(string)], il =>
            {
                var other = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldstr, "same");
                il.Emit(OpCodes.Bne_Un_S, other);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
                il.MarkLabel(other);
                il.Emit(OpCodes.Ldc_I4_2);
                il.Emit(OpCodes.Ret);
            }, ["same"], ["other"], [null!]);
        }
    }
}
