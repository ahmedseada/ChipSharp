namespace ChipSharp.Toolchain.Clang;

/// <summary>Which clang and linker a <see cref="ClangToolchain"/> runs; a target picks one with its "toolchain".</summary>
public enum ClangDriver
{
    /// <summary>
    /// The installed LLVM's clang, which also links (through ld.lld or lld-link): the toolchain "clang", for the PC and
    /// the chips whose processor that LLVM has (ARM, RISC-V).
    /// </summary>
    Clang,

    /// <summary>
    /// The clang and ld.lld inside Zig (<c>zig cc</c>, <c>zig ld.lld</c>): the toolchain "zig", for chips whose processor
    /// the installed LLVM lacks (LLVM's release builds for Windows have no AVR; Zig's has every processor LLVM knows).
    /// Chip programs only.
    /// </summary>
    Zig,
}
