# ChipSharp

C# for microcontrollers: C# compiled ahead of time, through LLVM, into native code that runs on the chip with
no operating system and no interpreter.

```
C# --Roslyn--> IL --ChipSharp--> TIR --code generator--> LLVM IR / C --toolchain--> .elf --image--> .uf2 --> chip
```

## Layout

| Folder | What it holds |
|---|---|
| `src/ChipSharp.Ir` | TIR (Typed Intermediate Representation): every code generator reads it; with its verifier, printer and builder |
| `src/ChipSharp.Abstractions` | The contracts (code generator, toolchain, image format, flasher, architecture runtime), the registry, the target model |
| `src/ChipSharp.Targets` | Reads the target files and merges each inheritance chain |
| `src/ChipSharp.Frontend` | Reads IL from a compiled C# .dll (and, next, turns it into TIR) |
| `src/ChipSharp.Backend.Llvm` | The `llvm` code generator: TIR in, LLVM IR text out |
| `src/ChipSharp.Toolchain.Clang` | The `clang` toolchain: LLVM IR and runtime sources in, a linked program out |
| `src/ChipSharp.Runtime.Windows` | The `windows-x64` runtime for PC programs: start-up and console output, no C runtime needed |
| `src/ChipSharp.Cli` | The `chipsharp` command |
| `targets/arch`, `chip`, `board` | Target files: board -> chip -> architecture, e.g. `pico` -> `rp2040` -> `armv6m`, `host` -> `x86_64` |
| `samples` | Small C# programs with their expected .NET output; `update-expected.ps1` regenerates the outputs |
| `tests/ChipSharp.Tests` | Tests |

A new board is one small JSON file; a new chip of a known architecture is a JSON file plus its SVD; only a CPU
no compiler supports needs a new code generator.

## Build, test, try

```powershell
dotnet build -c Release
dotnet test -c Release --no-build
.\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe targets list
.\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe targets show pico
.\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe il path\to\YourProgram.dll --method Program.Main
```

The PC tests need LLVM (`C:\Program Files\LLVM\bin`, or set `CHIPSHARP_CLANG` / `CHIPSHARP_LLVM_BIN`).

## Roadmap

| Milestone | Goal |
|---|---|
| Skeleton (done) | Contracts, registry, target files with inheritance |
| M0 wave 0–1 (done) | TIR; reading IL; the LLVM code generator; the PC target, clang toolchain, runtime and samples |
| M0 | Compile simple C# to a PC program through LLVM; compare every result with real .NET |
| M1 | Blink an LED on the Raspberry Pi Pico (RP2040) |
| M2 | Structs, `Span<T>`, interrupts, UART: the Restricted tier |
| M3 | Classes, heap, garbage collector, strings, interfaces, generics: the Full tier |
| M4 | ESP32-C5 (RISC-V) |
| M5 | CH32V003: C# on a 10-cent chip |
| M6 | C code generator for 8-bit chips |
| M7 | Interpreter mode |
