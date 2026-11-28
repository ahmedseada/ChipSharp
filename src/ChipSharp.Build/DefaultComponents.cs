using ChipSharp.Abstractions;
using ChipSharp.Backend.C;
using ChipSharp.Backend.Llvm;
using ChipSharp.Flasher.Avrdude;
using ChipSharp.Flasher.EasyPdkProg;
using ChipSharp.Flasher.Stm8Flash;
using ChipSharp.Flasher.Uf2Copy;
using ChipSharp.Image.Hex;
using ChipSharp.Image.Uf2;
using ChipSharp.Runtime.ArmV6M;
using ChipSharp.Runtime.Atmega328p;
using ChipSharp.Runtime.Avr;
using ChipSharp.Runtime.Esp32c5;
using ChipSharp.Runtime.Pdk14;
using ChipSharp.Runtime.Pfs154;
using ChipSharp.Runtime.Riscv32;
using ChipSharp.Runtime.Rp2040;
using ChipSharp.Runtime.Stm8;
using ChipSharp.Runtime.Stm8s103;
using ChipSharp.Runtime.Windows;
using ChipSharp.Toolchain.Clang;
using ChipSharp.Toolchain.Sdcc;

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
        var sdcc = new SdccToolchainOptions
        {
            OutputFolder = build.WorkFolder,
            ProgramName = build.ProgramName,
            SdccPath = build.Options.SdccPath,
            UseCache = build.Options.UseCache,
        };

        // Register new plug-ins here, one line each, and reference their project from ChipSharp.Build.csproj.
        // A target file names its parts ("backend", "toolchain", "runtime", "image", "flasher"); each part is found by
        // the name it gives itself.
        return RegisterFlashers(new ComponentRegistry()
            .Register(new LlvmCodeGenerator())      // backend "llvm"
            .Register(new CCodeGenerator())         // backend "c": portable C99, for SDCC's chips (and clang's)
            .Register(new ClangToolchain(clang))    // toolchain "clang"
            .Register(new ClangToolchain(clang with { Driver = ClangDriver.Zig })) // toolchain "zig": AVR
            .Register(new SdccToolchain(sdcc))      // toolchain "sdcc": Padauk (pdk14) and STM8 chips, through C
            .Register(new WindowsX64Runtime())      // runtime "windows-x64"
            .Register(new ArmV6MRuntime())          // runtime "armv6m": any Cortex-M0/M0+ chip
            .Register(new Rp2040Runtime())          // runtime "rp2040": Raspberry Pi Pico
            .Register(new AvrRuntime())             // runtime "avr": any AVR chip (ATmega328P: Arduino Uno)
            .Register(new Atmega328pRuntime())      // runtime "atmega328p": Arduino Uno, on the runtime "avr"
            .Register(new Riscv32Runtime())         // runtime "riscv32": any 32-bit RISC-V chip (RV32, machine mode)
            .Register(new Esp32c5Runtime())         // runtime "esp32c5": the ESP32-C5-DevKitC-1, on the runtime "riscv32"
            .Register(new Pdk14Runtime())           // runtime "pdk14": any Padauk PDK14 chip (PFS154), through SDCC
            .Register(new Pfs154Runtime())          // runtime "pfs154": a PFS154 on its own, on the runtime "pdk14"
            .Register(new Stm8Runtime())            // runtime "stm8": any STM8 chip, through SDCC
            .Register(new Stm8s103Runtime())        // runtime "stm8s103f3": the blue STM8S103 board, on the runtime "stm8"
            .Register(new Uf2ImageFormat())         // image "uf2"
            .Register(new IntelHexImageFormat()));  // image "hex": Intel HEX (an AVR's flash; SDCC's .ihx)
    }

    /// <summary>
    /// A registry with the flashers alone, for <c>chipsharp flash</c> of an image already built: they take no per-build
    /// settings (the target they flash is passed to each).
    /// </summary>
    public static ComponentRegistry CreateFlasherRegistry() => RegisterFlashers(new ComponentRegistry());

    private static ComponentRegistry RegisterFlashers(ComponentRegistry registry) => registry
        .Register(new Uf2CopyFlasher())             // flasher "uf2-copy": the .uf2 onto the Pico's RPI-RP2 drive
        .Register(new AvrdudeFlasher())             // flasher "avrdude": the .hex through an Arduino's USB port
        .Register(new EasyPdkProgFlasher())         // flasher "easypdkprog": the .hex into a PFS154 in the Easy PDK programmer
        .Register(new Stm8FlashFlasher());          // flasher "stm8flash": the .hex into an STM8 through an ST-LINK's SWIM
}
