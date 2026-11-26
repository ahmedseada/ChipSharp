using ChipSharp.Frontend;
using ChipSharp.Frontend.Analysis;
using ChipSharp.Frontend.Il;

namespace ChipSharp.Tests.Analysis;

/// <summary>The C# words for every IL instruction ChipSharp does not compile, and when each is planned.</summary>
public sealed class UnsupportedTests
{
    [Theory]
    [InlineData(IlOpCode.Newobj, "creating objects (new ...)")]
    [InlineData(IlOpCode.Ldstr, "text strings")]
    [InlineData(IlOpCode.Newarr, "arrays")]
    [InlineData(IlOpCode.Ldelem_I4, "arrays")]
    [InlineData(IlOpCode.Ldelem, "arrays")]
    [InlineData(IlOpCode.Ldelema, "arrays")]
    [InlineData(IlOpCode.Stelem_Ref, "arrays")]
    [InlineData(IlOpCode.Ldlen, "arrays")]
    [InlineData(IlOpCode.Ldfld, "instance fields of classes (fields without static)")]
    [InlineData(IlOpCode.Stfld, "instance fields of classes (fields without static)")]
    [InlineData(IlOpCode.Ldflda, "instance fields of classes (fields without static)")]
    [InlineData(IlOpCode.Ldsflda, "taking addresses (ref, out, struct methods)")]
    [InlineData(IlOpCode.Add_Ovf, "checked arithmetic")]
    [InlineData(IlOpCode.Sub_Ovf_Un, "checked arithmetic")]
    [InlineData(IlOpCode.Conv_Ovf_I4, "checked arithmetic")]
    [InlineData(IlOpCode.Conv_Ovf_U_Un, "checked arithmetic")]
    [InlineData(IlOpCode.Ckfinite, "checking that a float is finite (ckfinite)")]
    [InlineData(IlOpCode.Throw, "exceptions")]
    [InlineData(IlOpCode.Leave, "exceptions")]
    [InlineData(IlOpCode.Endfinally, "exceptions")]
    [InlineData(IlOpCode.Callvirt, "instance or virtual method calls")]
    [InlineData(IlOpCode.Box, "boxing (using a value as an object)")]
    [InlineData(IlOpCode.Unbox_Any, "boxing (using a value as an object)")]
    [InlineData(IlOpCode.Ldloca_S, "taking addresses (ref, out, struct methods)")]
    [InlineData(IlOpCode.Ldarga, "taking addresses (ref, out, struct methods)")]
    [InlineData(IlOpCode.Ldind_I4, "references and pointers (ref, out, in, *)")]
    [InlineData(IlOpCode.Stind_I8, "references and pointers (ref, out, in, *)")]
    [InlineData(IlOpCode.Ldnull, "null")]
    [InlineData(IlOpCode.Isinst, "type tests and casts on objects (is, as, (T)x)")]
    [InlineData(IlOpCode.Initobj, "structs")]
    [InlineData(IlOpCode.Localloc, "stackalloc")]
    [InlineData(IlOpCode.Ldftn, "delegates, lambdas and function pointers")]
    [InlineData(IlOpCode.Calli, "delegates, lambdas and function pointers")]
    [InlineData(IlOpCode.Ldtoken, "typeof and type handles")]
    public void An_instruction_is_described_in_CSharp_words(IlOpCode opCode, string what)
    {
        Assert.Equal(what, Unsupported.ForInstruction(opCode).What);
    }

    [Fact]
    public void Every_instruction_outside_the_subset_says_when_it_arrives_and_what_to_do_for_now()
    {
        Assert.All(IlOpCodes.All.Where(op => !SupportedSubset.OpCodes.Contains(op)), op =>
        {
            var message = Unsupported.ForInstruction(op).Message;
            Assert.Matches(@"^.+ arrives? (later|with M3 \(the heap\))\. For now, .+\.$", message);
        });
    }

    [Fact]
    public void Only_instructions_CSharp_rarely_writes_have_no_words_of_their_own()
    {
        var unnamed = IlOpCodes.All
            .Where(op => !SupportedSubset.OpCodes.Contains(op) && Unsupported.ForInstruction(op) == Unsupported.OtherInstruction)
            .Order();

        Assert.Equal(
        [
            IlOpCode.Break, IlOpCode.Jmp, IlOpCode.Refanyval, IlOpCode.Mkrefany, IlOpCode.Arglist, IlOpCode.Unaligned,
            IlOpCode.Tail, IlOpCode.Cpblk, IlOpCode.Initblk, IlOpCode.No, IlOpCode.Refanytype,
            IlOpCode.Readonly,
        ], unnamed);
    }
}
