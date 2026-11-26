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
| `src/ChipSharp.Frontend` | Reads IL from a compiled C# .dll, finds every method the program uses and checks it against the supported subset, and translates it into TIR |
| `src/ChipSharp.Backend.Llvm` | The `llvm` code generator: TIR in, LLVM IR text out |
| `src/ChipSharp.Toolchain.Clang` | The `clang` toolchain: LLVM IR and runtime sources in, a linked program out |
| `src/ChipSharp.Runtime.Windows` | The `windows-x64` runtime for PC programs: start-up and console output, no C runtime needed |
| `src/ChipSharp.Cli` | The `chipsharp` command |
| `targets/arch`, `chip`, `board` | Target files: board -> chip -> architecture, e.g. `pico` -> `rp2040` -> `armv6m`, `host` -> `x86_64` |
| `samples` | Small C# programs (e.g. `Logit`, `VendingMachine`, `Projectile`) with their expected .NET output; `update-expected.ps1` regenerates the outputs |
| `examples` | Projects for the Raspberry Pi Pico: `pico-blink`, `pico-timer-blink`, `pico-button` |
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
.\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe check path\to\YourProgram.dll
.\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe tir path\to\YourProgram.dll
.\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build path\to\YourProgram.dll -t host --keep
```

`check` says whether a program fits what ChipSharp compiles today; if not, it names each C# feature the program uses
that ChipSharp does not compile yet, where, when it is planned, and what to write for now (`--details`: every place
on a line of its own, and every library method ChipSharp can call). `tir` shows the program in TIR. `build` compiles
it for a target (`host` is your PC) into `chipsharp-output\<target>\`; `--keep` also keeps the `.tir` and `.ll` files.

`dotnet test` runs 832 tests. The PC tests need LLVM (`C:\Program Files\LLVM\bin`, or set `CHIPSHARP_CLANG` /
`CHIPSHARP_LLVM_BIN`); the Pico tests also need the simulator (see below), and are skipped, saying why, without it.

## What C# compiles today

| C# | Today |
|---|---|
| Methods | Static methods, top-level statements, static local functions, recursion; the methods, constructors, properties and operators of structs |
| Values | `bool`, every whole number (`sbyte` to `ulong`, `nint`, `nuint`), `float` and `double` (computed and printed bit for bit like .NET) |
| Code | `if`, `switch`, `for`, `while`, `do`, `foreach` over arrays, `&&`, `\|\|`, `?:`, patterns on numbers; arithmetic wraps around like unchecked C# |
| Types | Structs, enums (`[Flags]` too), `ref`, `out` and `in` parameters, `ref` locals |
| Static fields | Of the types above, `volatile` too, with field initializers and static constructors |
| Arrays | Of numbers, bools, structs and enums, on the stack: a constant size, kept in the method that makes them and passed down to the methods it calls |
| Library | `Console.WriteLine` of a number or a bool; `Math` and `MathF`: `Abs`, `Min`, `Max`, `Sqrt`; on a chip, `ChipSharp.Hardware`: pins, timing, registers and interrupts |

| Not yet | When |
|---|---|
| Classes and objects, `null`, interfaces, virtual calls, boxing, generics (`Span<T>`, `List<T>`), strings and `$"..."`, `char`; arrays that leave their method, whose size is known only when the program runs, or that hold strings or objects | With M3 (the heap) |
| Exceptions (`try`, `catch`, `finally`, `throw`, `using`), `checked` arithmetic, multi-dimensional arrays, unsafe code and `stackalloc`, delegates and lambdas, `typeof` | Later |
| Other library types and methods (`decimal`, `Random`, `Math.Sin`, ...) | Not yet: `chipsharp check` lists the methods there are |

`chipsharp check` (and `build`) say so for each feature a program uses, with what to write for now. The first program
ChipSharp was asked to run printed its result with `Console.WriteLine($"Logit result: {result}")`:

```
error: Logit.dll uses 3 C# features that ChipSharp does not compile yet (11 places):
  string interpolation ($"...") arrives with M3 (the heap): 9 places in the top-level statements (IL_0036, IL_003b, IL_0040 and 6 more). For now, print numbers and bools with Console.WriteLine.
  text strings arrive with M3 (the heap): 1 place in the top-level statements (IL_0042). For now, print numbers and bools with Console.WriteLine.
  the library method System.Console.WriteLine(string) is not available yet: 1 place in the top-level statements (IL_005b).
ChipSharp can call these library methods (--details lists each one with its parameters):
  Console.WriteLine of (nothing), int, uint, long, ulong, bool, float, double
  Math and MathF: Abs, Min, Max, Sqrt
  ChipSharp.Hardware: Pin, Timing, Registers (see the README)
For every place, run: chipsharp check Logit.dll --details
```

With `Console.WriteLine(result)` it compiles, and prints what .NET prints: that is `samples/Logit.cs`. The other
samples show the rest of the table, e.g. `VendingMachine.cs` (structs, enums, an array of structs and static fields as
a state machine) and `Projectile.cs` (floats in structs, with operators).

## Raspberry Pi Pico

A program for the Pico references `src/ChipSharp.Hardware` (pins, timing, registers):

```csharp
using ChipSharp.Hardware;

Pin.Output(25);                 // the Pico's LED
while (true)
{
    Pin.Toggle(25);
    Timing.DelayMilliseconds(500);
}
```

```powershell
cd examples\pico-blink
dotnet build -c Release
..\..\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe build bin\Release\net10.0\PicoBlink.dll -t pico
```

That makes `chipsharp-output\pico\PicoBlink.uf2`. To run it:
- **Simulator**: `node tools\rp2040-sim\run.mjs <file.uf2> --ms 3000` prints every pin change and every line written
  to UART0 (one-time setup: `cd tools\rp2040-sim; npm ci`). The Pico tests use it and skip when it isn't set up.
- **Wokwi** (wokwi.com): start a Raspberry Pi Pico project, press F1, "Upload Firmware and Start Simulation".
- **The real board**: hold BOOTSEL while plugging it in, then copy the `.uf2` onto the drive that appears.
  `Console.WriteLine` goes to UART0 (GPIO 0, 115200 baud).

The three examples blink the LED in three ways: `examples/pico-blink` with a delay loop, `examples/pico-timer-blink`
from a timer interrupt, and `examples/pico-button` from a pin interrupt, at each press of a button on GPIO 15. They are
built the same way, and a test builds each project with `chipsharp build -t pico` and runs it in the simulator. Every
sample in `samples` also runs on the simulated Pico, printing over UART0 exactly what .NET prints on the PC.

### Interrupts

A `static void` method without parameters marked `[Interrupt(...)]` runs when its event happens, whatever `Main` is
doing; nothing in the program calls it. Plain calls start the events (`examples/pico-timer-blink`, `examples/pico-button`):

```csharp
using ChipSharp.Hardware;

Pin.Output(25);
Timing.EveryMilliseconds(500);   // or EveryMicroseconds, AfterMilliseconds (once), AfterMicroseconds; Stop
while (true)
{
}

partial class Program
{
    [Interrupt(Event.Timer)]
    static void Toggle() => Pin.Toggle(25);
}
```

`Pin.Watch`, `Pin.WatchRising` and `Pin.WatchFalling` run the `[Interrupt(Event.Pin)]` method when a pin changes
(`Pin.Unwatch` stops). A static field that a handler and the rest of the program share must be `volatile`, and a handler
cannot wait (`Timing.Delay...`): `chipsharp check` says where. The target file says which interrupt each event uses
(`"interrupts"` in `rp2040.json`); a PC has none, so such a program does not build for `host`. The simulator changes an
input pin at a set time with `--input 15=high@100ms`.

## Roadmap

| Milestone | Goal |
|---|---|
| Skeleton (done) | Contracts, registry, target files with inheritance |
| M0 wave 0–1 (done) | TIR; reading IL; the LLVM code generator; the PC target, clang toolchain, runtime and samples |
| M0 wave 2 (done) | IL to TIR; the whole-program pass and `chipsharp check`; every sample runs end to end and matches .NET |
| M0 (done) | Compile simple C# to a PC program through LLVM; every sample matches real .NET |
| M1 (done in the simulator) | Blink the Raspberry Pi Pico's LED; every sample also runs on the simulated RP2040 and matches .NET over UART0 |
| M2 (done) | The Restricted tier: static fields and constructors; float and double, bit for bit like .NET; structs, enums, `ref`/`out`/`in`; arrays on the stack; interrupts (timer and pin). `Timing.Delay(TimeSpan)` and library substitutes: in progress |
| M3 | Classes, heap, garbage collector, strings, interfaces, generics (and with them `Span<T>`): the Full tier |
| M4 | ESP32-C5 (RISC-V) |
| M5 | CH32V003: C# on a 10-cent chip |
| M6 | C code generator for 8-bit chips |
| M7 | Interpreter mode |
