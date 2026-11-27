using ChipSharp.Abstractions;
using ChipSharp.Backend.Llvm;
using ChipSharp.Image.Hex;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Atmega328p;
using ChipSharp.Runtime.Avr;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Windows;
using ChipSharp.Toolchain.Clang;

namespace ChipSharp.Build;

/// <summary>
/// Every plug-in ChipSharp builds with: its code generators, toolchains, architecture runtimes, image formats and
/// flashers, all registered in one place.
/// </summary>
public static class DefaultComponents
{
    /// <summary>
    /// A registry with every plug-in, made for one build: some plug-ins take per-build settings (the clang toolchain
    /// works in <see cref="BuildContext.WorkFolder"/> and names the program), so each build gets its own registry.
    /// </summary>
    public static ComponentRegistry CreateRegistry(BuildContext build)
    {
        ArgumentNullException.ThrowIfNull(build);

        var clang = new ClangToolchainOptions
        {
            OutputFolder = build.WorkFolder,
            ProgramName = build.ProgramName,
            ClangPath = build.Options.ClangPath,
            LlvmBinFolder = build.Options.LlvmBinFolder,
            ExtraFlags = build.Options.VerifyHeap ? [ClangToolchainOptions.VerifyHeapFlag] : [],
            UseCache = build.Options.UseCache,
        };

        // Register new plug-ins here, one line each, and reference their project from ChipSharp.Build.csproj.
        // A target file names its parts ("backend", "toolchain", "runtime", "image"); each part is found by the
        // name it gives itself.
        return new ComponentRegistry()
            .Register(new LlvmCodeGenerator())      // backend "llvm"
            .Register(new ClangToolchain(clang))    // toolchain "clang"
            .Register(new ClangToolchain(clang with { Driver = ClangDriver.Zig })) // toolchain "zig": AVR
            .Register(new WindowsX64Runtime())      // runtime "windows-x64"
            .Register(new ArmV6MRuntime())          // runtime "armv6m": any Cortex-M0/M0+ chip
            .Register(new Rp2040Runtime())          // runtime "rp2040": Raspberry Pi Pico
            .Register(new AvrRuntime())             // runtime "avr": any AVR chip (ATmega328P: Arduino Uno)
            .Register(new Atmega328pRuntime())      // runtime "atmega328p": Arduino Uno, on the runtime "avr"
            .Register(new Uf2ImageFormat())         // image "uf2"
            .Register(new IntelHexImageFormat());   // image "hex": Intel HEX (an AVR's flash)
    }
}
