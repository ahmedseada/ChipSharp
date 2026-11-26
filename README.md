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
| `src/ChipSharp.Library` | ChipSharp's own C# for library types (library substitutes, e.g. `System.TimeSpan`), compiled together with the programs that use them |
| `src/ChipSharp.Backend.Llvm` | The `llvm` code generator: TIR in, LLVM IR text out |
| `src/ChipSharp.Toolchain.Clang` | The `clang` toolchain: LLVM IR and runtime sources in, a linked program out |
| `src/ChipSharp.Runtime.Windows` | The `windows-x64` runtime for PC programs: start-up and console output, no C runtime needed |
| `src/ChipSharp.Cli` | The `chipsharp` command |
| `targets/arch`, `chip`, `board` | Target files: board -> chip -> architecture, e.g. `pico` -> `rp2040` -> `armv6m`, `host` -> `x86_64` |
| `samples` | Small C# programs (e.g. `Logit`, `VendingMachine`, `TrafficLights`) with their expected .NET output; `update-expected.ps1` regenerates the outputs |
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

`dotnet test` runs 1005 tests. The PC tests need LLVM (`C:\Program Files\LLVM\bin`, or set `CHIPSHARP_CLANG` /
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
| Static arrays | Arrays in static fields that the field's initializer gives once, with a constant size and constant elements: `static readonly byte[] Digits = { 0x3F, 0x06, ... };` is a constant table when nothing writes an element (in flash on a chip), `static int[] buffer = new int[16];` a fixed array in RAM; read, indexed and passed to methods like any array, with no heap |
| Fixed text | String literals (`"road : 1"`, `string.Empty`) in locals, parameters, return values, static fields (`static readonly` too) and `const`, compared with `null`; printed part by part with `Console.Write` and `Console.WriteLine` (`null` as nothing, like .NET), as UTF-8. Each literal is in flash once on a chip |
| Printed text | Text built and printed straight away, with no string ever made and no heap: `Console.WriteLine($"road : {lane}")`, `Console.Write("road : " + lane)`, `lane.ToString()`; holes of text and of every type `Console.Write` prints, worked out first, in order, like .NET; alignments (`{x,5}`, `{x,-5}`, not of floats) and the formats `D`, `D<n>`, `X<n>`, `x<n>` and `G` of whole numbers |
| Library | `Console.Write` and `Console.WriteLine` of text, a number or a bool; `Math` and `MathF`: `Abs`, `Min`, `Max`, `Sqrt`; `TimeSpan`: its factories in any unit (`FromMilliseconds(500)`, `FromSeconds(1.5)`), parts, totals, arithmetic and comparisons; on a chip, `ChipSharp.Hardware`: pins, timing (`Timing.Delay(TimeSpan)` too), registers and interrupts |

| Not yet | When |
|---|---|
| Classes and objects, interfaces, virtual calls, boxing, generics (`Span<T>`, `List<T>`); keeping built text (in a variable, a field, a return value), `string.Format` and the other methods of string, `==` on text, enum names in text, text in the fields of structs, `char`; arrays that leave their method, whose size is known only when the program runs, that are `null`, or that hold strings or objects | With M3 (the heap) |
| Exceptions (`try`, `catch`, `finally`, `throw`, `using`), `checked` arithmetic, multi-dimensional arrays, unsafe code and `stackalloc`, delegates and lambdas, `typeof` | Later |
| Other library types and methods (`decimal`, `Random`, `Math.Sin`, ...) | Not yet: `chipsharp check` lists the methods there are |

`chipsharp check` (and `build`) say so for each feature a program uses, with what to write for now. The first program
ChipSharp was asked to run printed its result with `Console.WriteLine($"Logit result: {result}")`: printed text, which
compiles and prints what .NET prints (`samples/Logit.cs` prints the parts one after another, as it had to before). Made
in a method that returns it, `static string Report(float result) => $"Logit result: {result}";`, the text is kept,
which needs the heap:

```
error: Logit.dll uses 1 C# feature that ChipSharp does not compile yet (1 place):
  keeping built text in a variable arrives with M3 (the heap): 1 place in local function Report (IL_0025). For now, print it straight away: Console.WriteLine($"...").
For every place, run: chipsharp check Logit.dll --details
```

A call to a library method ChipSharp does not have lists the ones it has:

```
ChipSharp can call these library methods (--details lists each one with its parameters):
  Console.Write and Console.WriteLine of (nothing), text, int, uint, long, ulong, bool, float, double
  Math and MathF: Abs, Min, Max, Sqrt
  TimeSpan: FromTicks, FromDays, FromHours, FromMinutes, FromSeconds, FromMilliseconds, FromMicroseconds, Add, Subtract, Negate, Multiply, Divide, Duration, Compare, CompareTo, Equals, its constructors, properties and operators
  ChipSharp.Hardware: Pin, Timing, Registers (see the README)
```

The other samples show the rest of the table, e.g. `VendingMachine.cs` (structs, enums, an array of structs and static
fields as a state machine), `Projectile.cs` (floats in structs, with operators), `TimeSpans.cs` (TimeSpan arithmetic),
`TrafficLights.cs` (fixed text between numbers), `PrintedText.cs` (text built and printed straight away) and
`LookupTables.cs` (a 7-segment display's tables in flash and a buffer of readings in RAM).

### Fixed text

A string literal is fixed text: it never changes, so it needs no heap. Each distinct literal is one constant record
in read-only data (flash on a chip, once however often the program uses it): its length as .NET counts it (UTF-16
code units), its number of UTF-8 bytes, then those bytes; a string is the record's address, or null
(`src/ChipSharp.Frontend/FixedText.cs`). Text is written as UTF-8 on every target, the bytes .NET writes with a UTF-8
console; a Windows console shows non-ASCII text right when its code page is UTF-8 (`chcp 65001`), and there .NET
itself writes in the console's code page. Text built from it and printed straight away is printed text (below).

Where .NET would throw an exception (an index outside an array, a `TimeSpan` too long), the program stops with the
message .NET prints for it: on standard error with exit code 1 on a PC, over UART0 and at a breakpoint on a chip.

### Printed text

Text built and printed straight away needs no heap either, because no string is made. C# turns `$"..."`, `+` and
`ToString()` into a `DefaultInterpolatedStringHandler` with its `AppendLiteral` and `AppendFormatted` calls, or into
`string.Concat` of two to four strings or of an array of them, with `x.ToString()` for each number; ChipSharp follows
these on IL's evaluation stack (`src/ChipSharp.Frontend/PrintedText.cs`) to the `Console.Write` or `Console.WriteLine`
that prints the text. Where the IL hands a hole's value to the text, the value waits in a slot; the print then writes
the literals and those values one after another, with the functions of `Console.Write`. So, as in .NET, every hole is
worked out first, in order, and a hole that prints something prints before its line:

```csharp
Console.WriteLine($"road : {lane}");
Console.WriteLine($"{Name(road),-6}|{cars,5}|{busy,-6}| {cars * 100f / total} %");
Console.WriteLine("lane " + lane + (cut ? " cut" : "") + ", next " + next.ToString("D2"));
```

An alignment pads with spaces like .NET, to the length .NET counts (UTF-16 code units). The formats are those written
exactly like .NET: `D`, `D<n>`, `X`, `X<n>`, `x<n>` and `G` for whole numbers (hex of the value's own width: an `sbyte`
-1 is `FF`), `G` and `R` for floats; a format of text or of a bool is ignored, as .NET ignores it. Text that is kept
needs the heap: stored in a field or in a variable (but a variable written once and read once, which is what C#'s own
temporaries are, and what Release makes of `string s = $"..."; Console.WriteLine(s);`), returned, passed to another
method, measured (`.Length`) or chosen where ways join (`c ? $"a{x}" : "b"`), it is "keeping built text in a variable",
which arrives with M3. An enum in a hole (.NET prints its name), the other formats (`F2`, `N0`) and an aligned float
are rejected too, each saying what to write for now. `samples/PrintedText.cs` shows each form.

### Arrays in static fields

An array that a static field gets once, when the program starts, and keeps for good needs no heap either: it is a
global, like any static field (`src/ChipSharp.Frontend/StaticArrays.cs`). The field's initializer (or the static
constructor, where it runs exactly once) makes it with a constant size and constant elements (`new int[16]`,
`{ 1, 2, 3 }`, `[]`), nothing else assigns the field, and nothing takes its `ref`. When nothing in the program writes an
element, also not the methods it is passed to nor those they pass it on to, it is a constant table: read-only data, in
flash on a chip, never copied to RAM. Otherwise it is a fixed array in RAM, which starts with its elements. Either way
the code that made it is not compiled (a type initializer that did nothing else is gone), and reading the field gives
the usual array, so it is indexed, measured and passed down like an array on the stack:

```csharp
static readonly byte[] Digits = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };   // flash
static int[] readings = new int[6];                                                              // RAM
static int count;

static void Record(int value) => readings[count++ % readings.Length] = value;
```

Any other array in a static field (assigned again, made of computed elements or of a size known only when the program
runs, never given one) arrives with the heap; `chipsharp check` says which, where, and what to write for now.

### Library substitutes

Some library types are compiled from ChipSharp's own C#, in `src/ChipSharp.Library`: a type there marked
`[Substitute(typeof(System.TimeSpan))]` stands for `System.TimeSpan`. Where a program uses a member of the library type,
ChipSharp compiles the substitute's public member with the same name and signature, together with the program, and only
the members the program reaches; a struct substitute has the library struct's fields (`TimeSpan` is one `long` of
ticks). A member the substitute does not have stays "not available yet". A substitute is written in the C# ChipSharp
compiles, may use other substitutes, and stops the program where .NET throws by calling a method of
`ChipSharp.Library.Runtime`, which becomes a call to the runtime function of that name (each runtime's
`..._failures.c` has one per method). So a new library type is C# in `src/ChipSharp.Library`, not compiler code; its
tests compare it with .NET's, value by value.

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

`Timing.Delay(TimeSpan.FromMilliseconds(500))` waits the same, in any unit (`TimeSpan.FromSeconds(2)`,
`TimeSpan.FromMicroseconds(250)`), also longer than an `int` of microseconds.

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
  `Console.Write` and `Console.WriteLine` go to UART0 (GPIO 0, 115200 baud).

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

### What a target allows, and what a program takes

A target file's `"capabilities"` say what a program may use on it: `"heap"`, the most RAM the heap may take (objects,
text kept while the program runs, collections: M3), or `false` for none (printed text needs none); and `"console"`, whether `Console.Write` has
somewhere to go. The PC has a 256 MB heap and its console; the Pico a 224 KB heap (264 KB of RAM less 8 KB of stack and
32 KB for static fields) and UART0; a new chip inherits `armv6m`'s: neither, until its file says so.
`chipsharp check -t <target>` and `build` judge the program for that chip, so on one without a heap a class is not
"for M3" but out of reach:

```
creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) has no heap: 1 place in Program.Main() (IL_0000). Use structs and static methods.
Console.Write and Console.WriteLine need a console, and the target 'tiny' has none: 1 place in Program.Main() (IL_000a). Show what the program does another way, e.g. on a pin with ChipSharp.Hardware.Pin.
```

`chipsharp build` ends with what the program takes of the chip, read from the linked `.elf`: flash for its code,
constants and the starting values of its variables, RAM for its variables, with the stack (`"link.stack"`) kept at the
end of the RAM. For `examples/pico-blink`:

```
flash 1.1 KB of 2048 KB (0.1%), RAM 8 bytes of 264 KB (stack 8 KB reserved)
```

A program that does not fit stops with both sizes instead of the linker's words: `PicoBlink does not fit in target
'tiny-flash': its code, constants and the starting values of its variables need 1.1 KB of flash, 664 bytes more than the
target's 512 bytes.` For `host`, `build` gives the size of the `.exe`.

## Roadmap

| Milestone | Goal |
|---|---|
| Skeleton (done) | Contracts, registry, target files with inheritance |
| M0 wave 0–1 (done) | TIR; reading IL; the LLVM code generator; the PC target, clang toolchain, runtime and samples |
| M0 wave 2 (done) | IL to TIR; the whole-program pass and `chipsharp check`; every sample runs end to end and matches .NET |
| M0 (done) | Compile simple C# to a PC program through LLVM; every sample matches real .NET |
| M1 (done in the simulator) | Blink the Raspberry Pi Pico's LED; every sample also runs on the simulated RP2040 and matches .NET over UART0 |
| M2 (done) | The Restricted tier: static fields and constructors; float and double, bit for bit like .NET; structs, enums, `ref`/`out`/`in`; arrays on the stack; interrupts (timer and pin); `TimeSpan` and `Timing.Delay(TimeSpan)`, from library substitutes (ChipSharp's own C# for library types) |
| M3 | Classes, heap, garbage collector, strings, interfaces, generics (and with them `Span<T>`): the Full tier |
| M4 | ESP32-C5 (RISC-V) |
| M5 | CH32V003: C# on a 10-cent chip |
| M6 | C code generator for 8-bit chips |
| M7 | Interpreter mode |
