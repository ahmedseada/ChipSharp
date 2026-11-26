# ChipSharp

C# for microcontrollers: C# compiled ahead of time, through LLVM, into native code that runs on the chip with
no operating system and no interpreter.

```
C# --Roslyn--> IL --ChipSharp--> TIR --code generator--> LLVM IR / C --toolchain--> .elf --image--> .uf2 --> chip
```

## Layout

| Folder | What it holds |
|---|---|
| `src/ChipSharp.Ir` | TIR, the intermediate representation every code generator reads |
| `src/ChipSharp.Abstractions` | The contracts (code generator, toolchain, image format, flasher, architecture runtime), the registry, the target model |
| `src/ChipSharp.Targets` | Reads the target files and merges each inheritance chain |
| `src/ChipSharp.Cli` | The `chipsharp` command |
| `targets/arch`, `chip`, `board` | Target files: board -> chip -> architecture, e.g. `pico` -> `rp2040` -> `armv6m` |
| `tests/ChipSharp.Tests` | Tests |

A new board is one small JSON file; a new chip of a known architecture is a JSON file plus its SVD; only a CPU
no compiler supports needs a new code generator.

## Build, test, try

```powershell
dotnet build -c Release
dotnet test -c Release --no-build
.\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe targets list
.\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe targets show pico
```

## Roadmap

| Milestone | Goal |
|---|---|
| Skeleton (done) | Contracts, registry, target files with inheritance |
| M0 | Compile simple C# to a PC program through LLVM; compare every result with real .NET |
| M1 | Blink an LED on the Raspberry Pi Pico (RP2040) |
| M2 | Structs, `Span<T>`, interrupts, UART: the Restricted tier |
| M3 | Classes, heap, garbage collector, strings, interfaces, generics: the Full tier |
| M4 | ESP32-C5 (RISC-V) |
| M5 | CH32V003: C# on a 10-cent chip |
| M6 | C code generator for 8-bit chips |
| M7 | Interpreter mode |
