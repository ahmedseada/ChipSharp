namespace ChipSharp.Toolchain.Sdcc;

/// <summary>
/// Rules for SDCC's peephole optimizer that the toolchain adds to its own (<c>sdcc --peep-file</c>), for the ports where
/// SDCC 4.6 makes code that is wrong, each with what it fixes. SDCC applies them after its own rules.
/// </summary>
/// <remarks>
/// On the STM8 SDCC drops the test of A between <c>ld a, xl</c> (or xh, yl, yh) and the conditional jump on it, as if
/// that load set the flags; it sets none (PM0044, and SDCC's own simulator ucsim agree). So the jump goes by the flags
/// of the instruction before: after a 16-bit remainder (<c>call __modsint</c>, <c>divw x, y</c>), the quotient's, and
/// <c>if ((uint8_t)(n % 7) == 0)</c> took every n. The rules put the test back (<c>tnz a</c>, 1 byte and 1 cycle).
/// </remarks>
internal static class SdccPeepholes
{
    /// <summary>The file the rules are written to in the build folder, which sdcc reads with --peep-file.</summary>
    public const string FileName = "chipsharp_peephole.def";

    /// <summary>The rules for <paramref name="port"/> (sdcc -m), or null when it needs none.</summary>
    public static string? For(string port) => port == "stm8" ? Stm8 : null;

    private static readonly string Stm8 = string.Concat(new[] { "jrne", "jreq", "jrmi", "jrpl" }.Select(jump => $$"""
        replace {
        	ld	a, %1
        	{{jump}}	%2
        } by {
        	ld	a, %1
        	tnz	a
        	; ChipSharp: ld a, %1 sets no flags, the {{jump}} tests a
        	{{jump}}	%2
        } if same(%1 'xl' 'xh' 'yl' 'yh')


        """)).ReplaceLineEndings("\n");
}
