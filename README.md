# ChipSharp

C# for microcontrollers. You write an ordinary C# program and build it with `dotnet build` as usual; ChipSharp then
compiles it ahead of time, through LLVM, into native code: a small `.exe` for your PC, or a `.uf2` file for a
Raspberry Pi Pico. On the chip there is no operating system, no .NET runtime and no interpreter: only your program, a
small runtime written in C, and, when the program needs one, a heap with a garbage collector.

```
C# --Roslyn--> IL --ChipSharp--> TIR --LLVM--> .exe (PC)   or   .elf --> .uf2 (Pico)
```

Where it stands: milestones M0 to M3 are done. Numbers, structs, arrays, spans, generics, text, interrupts, `Math` and
`TimeSpan` work on every chip, even one without a heap; on a chip with a heap (the Pico, the PC) so do classes,
records, strings, interfaces, delegates and lambdas, boxing, exceptions you throw (not yet catch) and the collections
(`List<T>`, `Dictionary<TKey, TValue>`, ...). Every sample prints exactly what .NET prints, on the PC and on a simulated
Pico, and the tests compare programs, and thousands of library calls, with .NET's own results.

**Contents:** [Quick start](#quick-start) · [Targets](#targets-what-a-chip-has) ·
[What C# compiles today](#what-c-compiles-today) · [Known differences and gaps](#known-differences-from-net-and-gaps) ·
[Memory](#memory) · [Interrupts](#interrupts) · [The chipsharp command](#the-chipsharp-command) ·
[Samples and examples](#samples-and-examples) · [Tests](#tests) · [How it works](#how-it-works) · [Roadmap](#roadmap)

## Quick start

### What you need

| Tool | What for |
|---|---|
| [.NET 10 SDK](https://dot.net) | Building ChipSharp and your programs |
| [LLVM](https://github.com/llvm/llvm-project/releases) (clang and ld.lld; `winget install LLVM.LLVM`) | Turning the program into native code. ChipSharp finds it in `C:\Program Files\LLVM\bin` or on the PATH, or set `CHIPSHARP_LLVM_BIN` to its `bin` folder |
| [Node.js](https://nodejs.org) 18 or later | Only for the Pico simulator (`tools/rp2040-sim`), once: `cd tools\rp2040-sim; npm ci` |
| A Raspberry Pi Pico | Optional: the simulator and [Wokwi](https://wokwi.com) run the same `.uf2` |

ChipSharp is developed and tested on Windows; the PC target (`host`) makes Windows x64 programs.

### Build ChipSharp

```powershell
git clone https://github.com/ahmedseada/ChipSharp
cd ChipSharp
dotnet build -c Release
```

The command is then `src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe`; below it is written `chipsharp`
(in PowerShell: `Set-Alias chipsharp "$PWD\src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe"`).

### Your first program, on the PC

```powershell
dotnet new console -n Hello
cd Hello
dotnet build -c Release
chipsharp build bin\Release\net10.0\Hello.dll
.\chipsharp-output\host\Hello.exe
```

It prints `Hello, World!`. `Hello.exe` is 4.5 KB and needs no .NET on the PC that runs it.

### The same program, on a Pico

```powershell
chipsharp build bin\Release\net10.0\Hello.dll -t pico
```

```
...\chipsharp-output\pico\Hello.uf2
flash 2.0 KB of 2048 KB (0.1%), RAM 16 bytes of 264 KB (stack 8 KB reserved)
```

On the Pico, `Console.Write` and `Console.WriteLine` go to UART0: GPIO 0 (TX), 115200 baud. To use the pins, a
project references `src/ChipSharp.Hardware`, as the projects in `examples` do:

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
chipsharp build bin\Release\net10.0\PicoBlink.dll -t pico      # chipsharp-output\pico\PicoBlink.uf2
```

### Run it

- **In the simulator** (rp2040js, in Node.js): `node tools\rp2040-sim\run.mjs chipsharp-output\pico\PicoBlink.uf2 --ms 3000`
  prints every pin change and every line written to UART0, with the simulated time. `--input 22=high@2500ms` drives
  an input pin from outside (a button pressed at 2.5 s), `--uart0 file.bin` saves what UART0 sent.
- **In Wokwi** ([wokwi.com](https://wokwi.com), in the browser): start a new Raspberry Pi Pico project, replace its
  `diagram.json` with the example's (the traffic lights have one: the Pico, LEDs, displays, the button, and UART0 on the
  serial monitor), press F1 and pick "Upload Firmware and Start Simulation...", then choose the `.uf2`.
- **On a real Pico**: hold the BOOTSEL button while plugging it into USB, and copy the `.uf2` onto the `RPI-RP2` drive
  that appears; the Pico restarts and runs it. To see `Console` output, connect a 3.3 V USB-to-serial adapter (its RX to
  GPIO 0, GND to GND) at 115200 baud: the Pico's own USB port does not carry it.

## Targets: what a chip has

A target file says what a chip or board is and has. Files inherit from each other, board → chip → architecture, so a
new board is a few lines of JSON:

| Target | What it is |
|---|---|
| `host` | Your PC: a Windows x64 console program, with no C runtime (inherits `x86_64`) |
| `pico` | Raspberry Pi Pico: RP2040, 2 MB flash, LED on GPIO 25 (inherits `rp2040`, which inherits `armv6m`) |
| `pico-w` | Pico W: like the Pico, but its LED is wired to the Wi-Fi chip |
| `rp2040`, `armv6m`, `x86_64` | A chip and two architectures, for target files to inherit; not built for directly |

```
> chipsharp targets show pico
Target pico   (pico -> rp2040 -> armv6m)
  backend      llvm
  toolchain    clang
  runtime      rp2040
  triple       thumbv6m-none-eabi
  cpu          cortex-m0plus
  pointer size 4 bytes, little endian
  image        uf2
  flasher      uf2-copy
  memory
    flash      0x10000000  2 MB
    ram        0x20000000  264 KB
  capabilities hwMultiply=true, hwDivide=false, fpu=false, atomics=none, unalignedAccess=false, stackCheck=true, heap=224K, console=true, romFloat=true
  pins         LED=25
  interrupts   timer=IRQ 1, pin=IRQ 13
```

The `"capabilities"` decide what a program may use on the chip:

| Capability | Meaning | `pico` | `host` | `armv6m` (a new chip, until its file says) |
|---|---|---|---|---|
| `heap` | The most RAM the heap may take, or `false` for none. Objects, kept strings, boxes, delegates and collections need one (see [Memory](#memory)) | 224 KB | 256 MB | none |
| `console` | Whether `Console.Write` has somewhere to go | UART0 | standard output | none |
| `unalignedAccess` | Whether a word may be read at any address; without it `MemoryMarshal.Read` reads byte by byte and `MemoryMarshal.Cast` checks alignment | no | yes | no |
| `stackCheck` | The functions that can recurse check the stack, so a stack overflow says `Stack overflow.` instead of locking the core up | yes | (Windows' guard page) | yes |
| `hwMultiply`, `hwDivide`, `fpu`, `atomics`, `romFloat` | Describe the chip; the code itself follows the file's `triple`, `cpu` and `clang` flags (soft float on `armv6m`) | | | |

The file also gives the memory regions, the link layout (`"link.stack"`: 8 KB on the Pico) and the interrupts each
event uses (`"interrupts"` in `rp2040.json`). `chipsharp check -t <target>` and `chipsharp build -t <target>` judge the
program for that chip: on one without a heap or without a console, they name each place that needs it (see
[below](#when-chipsharp-check-says-no)). A new chip of a known architecture is a JSON file plus its runtime; a CPU no
code generator supports yet (the ESP32-C5's RISC-V, next) also needs a runtime for its architecture.

## What C# compiles today

The rule of thumb: what is a **value** (numbers, structs, enums, spans, fixed text, arrays that stay in their method)
works on every chip, with no heap; what **makes objects while the program runs** (classes, strings that are kept,
boxes, delegates, collections, exceptions) needs a heap, which the Pico and the PC have. A program that makes no object
gets no heap at all, even on the Pico. `chipsharp check` tells you for your program, place by place.

### On every chip, with no heap

| C# | What works |
|---|---|
| Program structure | Static methods, top-level statements, local functions (also ones that capture variables: those are kept in a struct), recursion; static fields (`volatile` too) with field initializers and static constructors |
| Values | `bool`, `char` (with `char.IsDigit`, `IsLetter`, `IsWhiteSpace`, `ToUpper`, ...), every whole number (`sbyte` to `ulong`, `nint`, `nuint`), `float` and `double`, computed and printed bit for bit like .NET (also on the Pico, which does float math in software); arithmetic wraps around like unchecked C# |
| Code | `if`, `switch` (patterns, and on strings), `for`, `while`, `do`, `foreach` over arrays and spans, `&&`, `\|\|`, `?:`, `goto`; `try`/`finally` (with `return`, `break` and `continue` out of it), `using` of a struct |
| Structs and enums | Fields, constructors, methods, properties and operators of structs; `readonly struct`; enums (`[Flags]` too); `ref`, `out` and `in` parameters and `ref` locals; primary constructors; `record struct` and `readonly record struct` (`==`, `with`, `Deconstruct`, `Equals`, `GetHashCode`; only their `ToString` needs a heap) |
| Nullables | `int?`, `bool?`, nullable enums and structs: `HasValue`, `Value`, `GetValueOrDefault`, `??`, lifted arithmetic and comparisons, `is int n` |
| Tuples | `(1, 2.5)`, `(int Min, int Max)`: `System.ValueTuple` of any number of elements, a struct of its elements as .NET lays it out. Named elements and `Item1`..., deconstruction into new and existing variables (`var (min, max) = Range(a);`, `(a, b) = (b, a)`, nested, with `_`), tuples as locals, parameters (`ref` too), return values, fields, static fields and array elements; `==` and `!=`, `Equals`, `CompareTo` and `GetHashCode` (element by element, as the default comparers compare them), `switch` on a tuple (`(light, waiting) switch { (Light.Red, > 3) => ... }`), `ValueTuple.Create`. Only `ToString()` (`(1, a)`) needs a heap |
| Generics over values | Generic methods, structs and static classes over numbers, bools, enums and structs, with constraints; interfaces implemented by structs and called through a constraint (`static void Run<T>(ref T device) where T : struct, IDevice`, static abstract members too); generic math (`INumber<T>`: operators, `T.Zero`, `T.One`, `MinValue`, `MaxValue`). Each instance is compiled for its type, like a C++ template, so `Max<int>` costs what a hand-written `int` version costs |
| Arrays | On the stack: a constant size, made in a method and passed down to the methods it calls. In static fields: `static readonly byte[] Digits = { 0x3F, ... };` is a constant table in flash when nothing writes it; `static int[] buffer = new int[16];` a fixed array in RAM. `foreach`, `Length`, bounds checks |
| Spans | `Span<T>` and `ReadOnlySpan<T>` of arrays, of `stackalloc` (a constant size), of constant data (`static ReadOnlySpan<byte> Table => new byte[] { ... };`, in flash) and of variables; indexer, `Slice`, ranges and indices (`s[2..5]`, `s[^1]`), `CopyTo`, `Fill`, `Clear`; `MemoryExtensions` (`IndexOf`, `SequenceEqual`, `StartsWith`, ...), `BinaryPrimitives` and `MemoryMarshal` (`Read`, `Write`, `Cast`, `AsBytes`), at any offset, also on a chip that faults on unaligned words |
| Fixed text | String literals anywhere a string may be: printed, compared, measured and searched (`Length`, `s[i]`, `==`, `IndexOf`, `StartsWith`, `switch`); each is a constant object in flash (8 bytes and 1 byte per char on the Pico) |
| Printed text | Text built and printed straight away, with no string ever made (also in the body of a `foreach` over a collection or a `using`, where C# makes it otherwise): `Console.WriteLine($"road : {lane}")`, `Console.Write("lane " + lane + '!')`, `x.ToString()` printed, `Console.WriteLine("{0,4} {1:X2}", a, b)`. Holes of numbers, bools, chars, text, enums (by name, `[Flags]` combined), structs (their `ToString`) and nullables; alignments (`{x,5}`, `{x,-5}`); the formats `D`, `D<n>`, `X<n>`, `x<n>` and `G` of whole numbers, `G` and `R` of floats |
| Type objects | `typeof(X)`, `typeof(T)`: `Name`, `FullName`, `Namespace`, `ToString()` as .NET 10 writes them, `==`, `IsValueType`, `IsArray`, `IsEnum`, `IsClass`, `IsInterface`, `IsPrimitive`, `IsGenericType` (constant objects in flash) |
| Math | `Math` and `MathF`: trigonometry (`Sin`, `Cos`, `Tan`, `SinCos`, `Asin`, `Acos`, `Atan`, `Atan2`), hyperbolic functions and their inverses, `Exp`, `Log` (in any base), `Log2`, `Log10`, `Pow`, `Sqrt`, `Cbrt`; `Floor`, `Ceiling`, `Truncate`, `Round` (to digits, by every `MidpointRounding`), `Clamp`, `Sign`, `Abs`, `Min`, `Max`, `MinMagnitude`, `MaxMagnitude`, `CopySign`, `FusedMultiplyAdd`, `IEEERemainder`, `ScaleB`, `ILogB`, `BitIncrement`, `BitDecrement`, the reciprocal estimates, `E`, `PI`, `Tau`; for every whole-number type `Abs`, `Min`, `Max`, `Clamp`, `Sign`, `DivRem`, and `BigMul`; `double.Sin(x)` and the like, and generic math over float types (`T.Sqrt`, `T.Sin`, ...). The exact ones give .NET's bits; the transcendental ones the same bits on the PC and the Pico, and .NET's but for the last bit of a few results (see [differences](#where-a-program-behaves-differently)). A program links only the functions it calls (`Math.Sin`: 9 KB on the Pico) |
| Library | `Console.Write` and `WriteLine`; `TimeSpan` (factories in any unit, parts, totals, arithmetic, comparisons); `System.Array`: `Copy`, `ConstrainedCopy`, `CopyTo`, `Clear`, `Fill`, `IndexOf`, `LastIndexOf`, `Reverse`, and `Sort` and `BinarySearch` of numbers, bools and structs that implement `IComparable<T>` |
| Hardware | `ChipSharp.Hardware`: `Pin` (`Output`, `Input`, `Write`, `Read`, `Toggle`, `Watch`, `WatchRising`, `WatchFalling`, `Unwatch`), `Timing` (`Delay(TimeSpan)`, `DelayMilliseconds`, `DelayMicroseconds`, `EveryMilliseconds`, `EveryMicroseconds`, `AfterMilliseconds`, `AfterMicroseconds`, `Stop`), `Registers.Read` and `Write`, and `[Interrupt]` methods (see [Interrupts](#interrupts)) |
| Failures | Where .NET throws (an index outside an array, a whole number divided by zero, `int.MinValue / -1`, a `TimeSpan` too long, a field of null, a cast that fails, ...), the program stops with the message .NET prints: on standard error with exit code 1 on the PC, over UART0 and then at a breakpoint on a chip |

### On a chip with a heap (the Pico and the PC)

| C# | What works |
|---|---|
| Classes | The program's classes, `sealed`, `abstract`, nested and derived from each other: `new`, fields (of objects too: lists, trees), field initializers, constructors (`this(...)`, `base(...)`), instance and static members, object initializers, `null`, `?.`, `??`, `??=`, `ReferenceEquals`; objects in locals, parameters, fields, arrays and static fields. The garbage collector frees what the program no longer reaches |
| Records | `record` / `record class` (positional, with members of their own, derived, generic, `sealed`, `abstract`): `init` properties, `required` members, `with`, `Deconstruct`, `Equals`, `==`, `GetHashCode` and `ToString` as C# makes them (`Point { X = 1, Y = 2 }`) |
| Virtual calls and interfaces | `virtual`, `override`, `abstract`, `sealed override`, `base.M()`, `new virtual`; interfaces (explicit implementations, default methods, properties, generic interfaces with `in`/`out` variance); `object`'s `Equals`, `GetHashCode` and `ToString`. A call only one method can answer is a plain call, and a method no object of the program can run is left out |
| Type tests and casts | `is`, `as`, `(T)x`, `x is Square s`, property patterns, `switch` on types, against classes, interfaces, `object` and arrays; a failed cast stops with .NET's `InvalidCastException` message; `x.GetType()` |
| Strings | Text built and kept (`string s = $"..."`, `+`, `+=`, `ToString()` with the formats printed text has, `string.Concat`, `string.Join` of an array, `string.Format` of a literal format), in fields, arrays and collections; `Substring`, `IndexOf`, `Replace`, `Trim`, `PadLeft`, `ToUpper`, `Split`, `Insert`, `Remove`, `ToCharArray`, `new string(...)`, `CompareOrdinal`, `CompareTo`, `IsNullOrEmpty`, `GetHashCode`, ...; `int.Parse` and `TryParse` (and those of the other whole numbers); spans of chars |
| StringBuilder | Its constructors, `Append` of every kind (also `Append($"...")` part by part), `AppendLine`, `AppendFormat` of a literal format, `AppendJoin` of an array, `Insert`, `Remove`, `Replace`, `Clear`, `Length`, `Capacity`, the indexer, `ToString`, with .NET's results and messages |
| Boxing | Any value as an object or an interface (`object o = 5;`, `object[] { 1, "a", 2.5, Color.Red, point }`, `IComparable c = 4;`), named and compared as .NET does; unboxing (`(int)o`, `o is int n`, `switch` on values) with .NET's rules; objects in formatted text (`$"{shape}"`, `string.Format("{0}", o)`) |
| Generics over classes | Generic classes (`class Stack<T>`, `class Derived<T> : Base<T>`), classes, interfaces, `object` and arrays as type arguments, constraints (`class`, a base class, an interface, `new()`), `default(T)`, `new T()`, generic virtual methods; `EqualityComparer<T>.Default` and `Comparer<T>.Default` |
| Delegates, lambdas, events | `Action`, `Func<...>`, `Predicate<T>`, `Comparison<T>`, `EventHandler<T>` and your own delegate types; lambdas that capture variables, `this` and loop variables; static, instance, virtual, extension and library methods as delegates; `+=`, `-=`, multicast with .NET's order; field-like events and events with `add`/`remove`; `d?.Invoke(x)` |
| Exceptions | `throw` of .NET's exceptions (`InvalidOperationException`, `ArgumentException` with its parameter name, `KeyNotFoundException`, ...) and of your own derived ones; `ArgumentNullException.ThrowIfNull` and the like. The program stops with what .NET prints for an exception nothing catches (catching comes later, see [gaps](#known-differences-from-net-and-gaps)); an exception as a value (`Message`, `ToString()`, `InnerException`) |
| Collections | `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>`, `Stack<T>`, `KeyValuePair<TKey, TValue>`: .NET 10's own code, so the same growth, order, checks and messages; `foreach`; collection initializers (`new List<int> { 1, 2 }`, `{ ["a"] = 1 }`) and collection expressions of values, text and objects into a list, an array, `IEnumerable<T>`, `IReadOnlyList<T>`, `IList<T>`, ... (`List<string> names = ["a", b];`, `Lane[] all = [north, .. others];`, `[.. names, "c"]`, `[.. Words()]`, `[]`); tuples as elements and as dictionary keys (`foreach (var ((row, column), value) in grid)`) and their text, `(3, three)`, boxed too; `List<T>`'s methods that take a delegate (`FindAll`, `ConvertAll`, `Exists`, `RemoveAll`, `Sort(Comparison)`, ...); arrays as `IEnumerable<T>`, `IList<T>`, `IReadOnlyList<T>`, ... |
| Arrays on the heap | Arrays returned, kept in fields, of a size known only when the program runs (`new int[n]`), arrays of arrays, of strings and of objects (and spans of them: `Span<string> s = names;`), `null`; `Array.Resize`, `Clone` and the `Array` methods that take a delegate (`Find`, `FindAll`, `Exists`, `ForEach`, `ConvertAll`, `Sort` with a `Comparison`) |
| Iterators | `yield return` and `yield break`, with `foreach`, `try`/`finally` and `using` inside (a `finally` runs when the `foreach` over it ends early), in generic methods and as a collection's `GetEnumerator()` |
| Memory | `GC.Collect()`, `GC.GetTotalMemory(bool)`, `GC.KeepAlive(object)` |

### When chipsharp check says no

`chipsharp check` (and `build`) name each C# feature the program uses that ChipSharp does not compile, where, and what
to write for now. On a chip without a heap, everything that needs one says so; here the M3 traffic light, checked for
a Pico whose target file says `"heap": false` (first lines):

```
error: PicoTrafficLightM3.dll uses 10 C# features that ChipSharp does not compile yet (60 places):
  generics over classes (a class, an interface, object or an array as a type argument) need a heap, and the target 'pico-no-heap' (264 KB RAM) has no heap: 31 places in the top-level statements (IL_0000, IL_0012 and IL_0024), ... Use numbers, bools, enums and structs as type arguments: ChipSharp makes the code once for each.
  creating objects (new ...) needs a heap, and the target 'pico-no-heap' (264 KB RAM) has no heap: 5 places in the top-level statements (IL_000d, IL_001f, IL_002a and 2 more). Use structs and static methods.
  ...
For every place, run: chipsharp check PicoTrafficLightM3.dll -t pico-no-heap --details
```

A feature that is not there yet "arrives later" (`multi-dimensional arrays (int[,]) arrive later: ... For now, use a
one-dimensional array and compute the index: row * width + column.`); a library method ChipSharp does not have "is not
available yet", followed by the list of the library methods it can call (`--details` gives each with its parameters).

## Known differences from .NET and gaps

### Where a program behaves differently

| What | .NET | ChipSharp | What to write |
|---|---|---|---|
| A failure | An exception, which a `catch` may handle | `try`/`catch` does not compile yet; a `throw`, or anything .NET would throw for, stops the program as an exception nothing catches does | Report failures through return values (`bool TryRead(out int value)`) |
| `finally` after a failure | .NET 10 prints the unhandled exception, then runs the `finally` blocks around the throw (a `using`'s `Dispose` too) | Stops right after printing it; a `finally` runs only when its `try` block is left normally (its end, `return`, `break`, `continue`) | Do not count on clean-up after a failure |
| What a failure prints | The exception and a stack trace. A stack overflow lists the methods that were running | The exception's first lines (`Unhandled exception. System...: message`) or `Stack overflow.`, with no stack trace; exit code 1 (stack overflow: .NET's 0xC00000FD). On a chip: over UART0, then the core stops at a breakpoint | |
| Comparing text | `CompareTo`, `string.Compare`, `StartsWith(string)`, `EndsWith(string)` and `IndexOf(string)` follow the PC's culture: `"a".CompareTo("B")` is -1 | Ordinal, char by char, as .NET's invariant mode compares: 31 (`==`, `Equals` and `Contains` are ordinal in both) | `string.CompareOrdinal(a, b)`, which gives the same in both |
| Upper and lower case | Every Unicode letter, and the culture's rules (Turkish `i`) | `ToUpper`, `ToLower`, `char.IsLetter`, `IsUpper`, ... know the letters up to U+00FF (ASCII and Latin-1, `ÿ` to `Ÿ` too); from U+0100 on a char is no letter and keeps its case (`IsWhiteSpace` knows all of .NET's spaces) | Keep text that changes case ASCII or Latin-1 |
| Numbers as text | `ToString`, `$"{x}"` and `Parse` follow the PC's culture (`1,5` in German) | Always as the invariant culture: `1.5` | To compare with .NET, run it with `InvariantGlobalization` (the samples' expected files are made so) |
| Hash codes | A string's, a struct's (boxed or through `ValueType`), a record's, a tuple's and a `Type`'s `GetHashCode` differ from run to run | The same in every run (a string's is FNV-1a of its chars, a tuple's its elements' combined as `HashCode.Combine` combines them from a seed of 0); object's own is its address divided by 8 | Never store hash codes or count on their order (true for .NET too) |
| `StringBuilder.Capacity` | Grows by chunks, and drops or splits them on `Clear`, a shorter `Length` or an `Insert` | The same capacity for a builder that only appends; after those three it may differ. The text never does | Do not test `Capacity` |
| Memory numbers | `GC.GetTotalMemory` counts .NET's heap | ChipSharp's heap: each object's block, its size rounded up to 8 | |
| Console | Writes in the console's code page | Always UTF-8; a Windows console shows non-ASCII text right after `chcp 65001` | |
| The last bit of `Sin`, `Exp`, `Pow`, ... | Calls the C runtime: on Windows the UCRT, whose x64 functions are AMD's math library (some in C, some in assembly that uses the fused multiply-add of processors with FMA3), on Linux glibc; not always correctly rounded, so the last bit depends on the PC | One implementation for every target, the same bits on the PC and the Pico: .NET's result for 88 to 100 % of values (above 97 % for most functions), 1 ulp away for the rest (see [Math](#math)); `Cbrt`: 70 %, up to 2 ulps | Compare transcendental results with a tolerance (as with .NET on two operating systems), or round them for display |
| `MathF.ReciprocalEstimate`, `MathF.ReciprocalSqrtEstimate` | On x64, the processor's estimate instructions (`rcpss`, `rsqrtss`: about 12 bits, different on Intel and AMD) | `1f / x` and `1f / MathF.Sqrt(x)`, what .NET computes without them (and for doubles everywhere) | |

### What does not compile yet

| C# | What to write for now |
|---|---|
| `try`/`catch`, exception filters | Return values: `bool TryParse(string text, out int value)` |
| `checked` arithmetic | Leave out `checked` (arithmetic wraps around, as unchecked C# does), or compare with the limits yourself |
| Multi-dimensional arrays (`int[,]`) | A one-dimensional array: `cells[row * width + column]` (or an array of arrays, `int[][]`, on a chip with a heap) |
| `async`/`await`, `Task`, `Thread`, `lock` | One core runs `Main` and the interrupt handlers: a timer interrupt and `volatile` static fields, `Timing.Delay` |
| LINQ (`Where`, `Select`, `OrderBy`, ...; but `ToList` and `ToArray`) | Loops, or `List<T>.FindAll`, `ConvertAll`, `Exists`, `Sort(Comparison<T>)` and their `Array` twins |
| Collection expressions as spans (`Span<int> s = [1, 2];`) | `stackalloc int[] { 1, 2 }` or `new int[] { 1, 2 }` |
| A `params` span of text: `string.Join(", ", a, b, c)`, `sb.AppendJoin(", ", a, b)` | Pass an array: `string.Join(", ", new[] { a, b, c })` |
| The formats `F2`, `N0`, `0.00` and the like; a format or alignment on an object or struct hole (`$"{obj:X}"`); an aligned float (`$"{x,8}"`) | Whole numbers with `D`, `D<n>`, `X<n>`, e.g. hundredths: `$"{cents / 100}.{cents % 100:D2}"`; print floats on their own |
| `string.Format` of a format known only when the program runs, or of an `object[]` | A literal format: `string.Format("{0} {1}", a, b)` |
| `decimal` | `long` counting the smallest unit (cents, millivolts) |
| `Random` | A small generator of your own, e.g. a xorshift struct |
| `Math.BigMul` into `Int128` and `UInt128`; the methods of `double` and `float` that `Math` has not (`double.Exp10`, `double.IsNaN`, `Lerp`, ...) | `Math.BigMul(a, b, out low)`; `x != x` for NaN, `Math.Pow(10, x)` |
| `double.Parse`, `float.Parse`, `Enum.Parse` | `int.Parse` and a scale; a `switch` on the text |
| `Enum.HasFlag` | `(access & Access.Read) != 0` |
| `DateTime`, `Stopwatch`, `Environment.TickCount`, `Console.ReadLine` | Count time with `Timing` or a timer interrupt; read input from pins (or UART registers) |
| Reflection beyond a type's names and `Is...` flags (`GetMethods`, attributes, `BaseType`, `Activator`) | A virtual method that says it, or a `switch` on the object's type |
| `typeof(List<>)` (an open generic type) | `typeof(List<int>)` |
| Boxing a library struct (`object o = TimeSpan.Zero;`, its `GetType()`), `TimeSpan.ToString()` and `Parse` | Keep it in its own type; print `span.TotalMilliseconds` |
| Type tests against `System.ValueType`, `System.Enum` or library classes | Test against your own classes and interfaces |
| Delegates that take or return arrays or spans (`Action<int[]>`), function pointers (`delegate*`), `BeginInvoke` | Keep the array in a field the method reads, or call the method directly |
| Non-generic `IList` and `ICollection`, `List<T>.AsReadOnly`, `SortedDictionary`, `SortedSet`, `LinkedList` and the other collections | `List<T>` (sorted with `Sort`), `Dictionary<TKey, TValue>`, `IReadOnlyList<T>` |
| `Array.Sort` and `BinarySearch` of enums, strings, objects and structs without `IComparable<T>`; `Array.IndexOf` of structs and of classes with their own `Equals`; `Array.Copy` between element types (`int[]` into `long[]`) | `Array.Sort(items, (a, b) => ...)` with a `Comparison`; a loop |
| An array in a field of a struct; `ref` or `out` of an array (but `Array.Resize(ref a, n)`); `span.ToArray()`; `IndexOf`, `Contains` and the other `MemoryExtensions` of a span of strings or objects | A class, or a static field; return the array; `new T[span.Length]` and `CopyTo`; a loop |
| A nullable as a type argument: a tuple of one (`(int?, int)`), `Pair<int?>` | A struct with a `bool` and a value, or `-1` for none |
| `unsafe` code (pointers, `&x`, `fixed`), `stackalloc` into a pointer or of a size known only when the program runs, `[StructLayout(LayoutKind.Explicit)]`, `ref` fields | `Span<T>`, `ref` parameters, `MemoryMarshal` and `BinaryPrimitives`; `stackalloc` of a constant size |
| Finalizers (`~Sensor()`); classes derived from library classes ChipSharp has no C# for | A `Dispose` or `Close` method you call; derive from your own classes |
| `extern` and `[DllImport]` methods; using `Main`'s `args` | C#; leave `args` unused |
| A `nint` or `nuint` in text | Convert it: `$"{(long)size}"` |
| A generic method that calls itself with an ever bigger type argument (`F<T>` calling `F<Pair<T>>`) | Stops at 8 generic types deep |

## Memory

**A heap only when the program uses one.** A program that makes no object, keeps no string and calls nothing of
`System.GC` (the whole program: the library code it calls too) is built as for a chip without a heap: on the Pico no
heap region and none of the collector's code, on the PC no memory taken from Windows. `chipsharp build` says which:

```
flash 5.7 KB of 2048 KB (0.3%), RAM 20 bytes of 264 KB (stack 8 KB reserved)                    examples/pico-traffic-light
flash 17.3 KB of 2048 KB (0.8%), RAM 1.1 KB + heap 224 KB of 264 KB (stack 8 KB reserved)        examples/pico-traffic-light-m3
```

Flash holds the code, the constants (string literals, constant tables, type descriptors and type objects) and the
starting values of the variables; RAM the static fields, then the heap (the target's `"heap"`, when the program uses
one), and the stack (`"link.stack"`) at its end. For the PC, `build` gives the size of the `.exe`. A program that does
not fit says so with both sizes: `PicoBlink does not fit in target 'tiny-flash': its code, constants and the starting
values of its variables need 1.1 KB of flash, 664 bytes more than the target's 512 bytes.`

**What things take on the Pico:** a string literal 8 bytes and 1 byte per char (in flash); an object its header (4
bytes) and its fields, rounded up to 8 (a list node with an `int` and a reference: 16 bytes); a boxed `int` 8 bytes, a
boxed `double` 16; a delegate 16; a string on the heap 8 bytes and its chars (1 byte each when all are below U+0100,
else 2), rounded up to 8; a type object 24 bytes in flash.

**The garbage collector** is a mark and sweep collector (`src/ChipSharp.Runtime.Shared/chipsharp_heap.c`). `new` takes
zeroed memory from the heap; when there is none left, the collector keeps every object the static fields, the stack,
the registers and other kept objects reach, and frees the rest, joining neighbouring free blocks. It never moves an
object. When there is still no room, the program stops with .NET's `OutOfMemoryException` message. A workload that
keeps text, arrays and chains of objects of many sizes, half the heap alive, runs in a 32 KB heap; with more and more
alive it runs out at around 60% of the heap.

**Interrupts wait for no collection.** The collector runs with interrupts on: on the simulated Pico a timer interrupt
every 200 µs came at most 201 µs after the one before while a heap 188 KB full was collected again and again. That is
safe because of two rules for interrupt handlers (see [Interrupts](#interrupts)): they make no object and store none.

**Stack overflow.** A recursion with no end stops with `Stack overflow.`, as .NET does, on the PC and on a chip. A
Cortex-M0+ cannot catch it by itself, so on a chip (`"stackCheck": true`) the functions through which the stack can
grow without end check the stack pointer when they start (four instructions); the others cost nothing.

**`chipsharp build --verify-heap`** checks the whole heap before and after every collection (block sizes, headers,
every reference, the marks, the free lists) and stops a program whose heap is corrupted, saying what is wrong and where:
`Fatal error. The heap is corrupted: a reference into the middle of a block at 0x20008a44 (found before collection 1).`
A collection then takes 2 to 3 times as long. The GC tests run with it on.

**`System.GC`:** `GC.Collect()` collects now; `GC.GetTotalMemory(bool)` gives the bytes of the objects not known to be
garbage (after a collection when asked); `GC.KeepAlive(object)` keeps an object until there.

## Interrupts

A `static void` method with no parameters marked `[Interrupt(...)]` runs when its event happens, whatever `Main` is
doing; nothing in the program calls it. Plain calls start the events:

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

`Pin.Watch`, `WatchRising` and `WatchFalling` run the `[Interrupt(Event.Pin)]` method when a pin changes (`Pin.Unwatch`
stops it). The target file says which interrupt each event uses; a PC has none, so such a program builds for a chip
only. `chipsharp check` enforces the rules, naming the place that breaks one:

| Rule | Why |
|---|---|
| One `static void` method without parameters per event, and one for every event the program starts | The chip calls it with nothing to pass, and an event without a handler would stop the chip |
| A static field shared with `Main` is `volatile`, and an `int`, `uint`, `bool`, ... (not a `long`, a `double` or a struct) | Otherwise `Main` may keep a copy and never see the change, or see half of it |
| A handler does not wait (`Timing.Delay...`) | The interrupt that ends the wait cannot come while a handler runs |
| A handler makes no object (`new`, a box, text, a lambda, `+=` on an event) | The heap may be in the middle of an allocation or a collection |
| A handler stores no object: no reference but `null` into a static field, an object, an array or through a `ref` | The collector may be half way through: an object moved to where it has already looked would be freed while in use. Read objects, store numbers and bools, keep references in locals |

The rules hold for everything a handler runs: the methods it calls, virtual calls and the delegates it invokes.
`examples/pico-traffic-light-m3` shows the usual shape: the handler sets a `volatile bool`, and `Main` does the rest.

## The chipsharp command

| Command | What it does |
|---|---|
| `chipsharp build <program.dll> [-t <target>] [-o <folder>] [--keep] [--verify-heap] [--no-cache] [--quiet-notes]` | Compiles the program for a target (default `host`) into `chipsharp-output\<target>\` (or `-o`): prints the file (`.exe`, `.uf2`) and what it takes of the chip. `--keep` also writes the TIR (`.tir`) and LLVM IR (`.ll`) there, and for a chip the `.elf` |
| `chipsharp check <program.dll> [-t <target>] [--details] [--quiet-notes]` | Whether ChipSharp compiles the program (for that target), and the methods it uses; if not, each feature it does not compile yet, where, and what to write instead. `--details`: every place on a line of its own, and every library method it can call |
| `chipsharp tir <program.dll> [--method Type.Method] [-t <target>]` | The program in TIR, after the whole-program pass |
| `chipsharp il <program.dll> [--method Type.Method]` | The IL of each method, ildasm style |
| `chipsharp targets list` / `chipsharp targets show <name>` | The targets, and one target with everything it inherits merged in |

| Environment variable | Meaning |
|---|---|
| `CHIPSHARP_LLVM_BIN` | The folder with clang, ld.lld and llvm-dlltool (default: `C:\Program Files\LLVM\bin`, then the PATH) |
| `CHIPSHARP_CLANG` | clang itself |
| `CHIPSHARP_TARGETS` | Another folder of target files (or `--targets <folder>`) |
| `CHIPSHARP_CACHE` | Where the build cache lives (default: `%LOCALAPPDATA%\ChipSharp\cache`) |

**Notes.** `build` and `check` write a note on standard error (`note: ...`) where a program uses one of .NET's
culture-sensitive text methods (`CompareTo`, `string.Compare(a, b)`, `StartsWith(string)`, `ToUpper()`, `Parse`,
floats as text, ...), whose result in .NET depends on the PC's language settings while ChipSharp's does not (see
Known differences); each note says what to write to get the same result everywhere. `--quiet-notes` leaves them out.

**Build cache.** `build` compiles each source file (the runtime's C, the program's `.ll`) once for a given clang, its
flags and its text, and keeps the object in `%LOCALAPPDATA%\ChipSharp\cache` (or `CHIPSHARP_CACHE`): the runtime is
compiled once, and later builds only link it (a Pico blink: about 1.5 s before, 0.5 s with a warm cache). Parallel
builds share it safely, and it trims itself past 512 MB. `--no-cache` compiles everything; deleting the folder is
always safe.

## Samples and examples

**`samples/`** holds small programs, one per feature, each with the output .NET gives (`<Name>.expected.txt`, made by
`samples\update-expected.ps1`). The tests run each one on the PC and on the simulated Pico and compare. Flash and RAM on
the Pico:

| Sample | Shows | Pico |
|---|---|---|
| `Square`, `Arithmetic`, `BitOps`, `Comparisons`, `Unsigned`, `IfElse`, `ForLoops`, `WhileSum`, `ShortCircuit`, `Primes`, `Gcd`, `Fibonacci`, `FactorialLong` | The basics: arithmetic, loops, recursion | 1.6 to 3.6 KB, 16 bytes of RAM |
| `FloatMath`, `Logit`, `Projectile` | `float` and `double` bit for bit like .NET; structs of floats with operators | 3.5 to 8.7 KB |
| `MathFunctions` | `Math` and `MathF`: a projectile (trigonometry, `Pow`), a pendulum (`Sqrt`, `Exp`, `Log`), a thermistor (`Log`, `Pow`, `Clamp`), decibels (`Log10`) and a compass (`MathF.Atan2`), rounded for a display by every `MidpointRounding` | 43.6 KB, no heap |
| `Structs`, `VendingMachine`, `StaticFields` | Structs, enums, `ref` and `out`; a state machine; static fields and constructors | 2.9 to 3.2 KB |
| `Arrays`, `LookupTables`, `Spans` | Arrays on the stack; a 7-segment table in flash and a buffer in RAM; a sensor's packet read with spans, `BinaryPrimitives` and `MemoryMarshal` | 3.5 to 7.7 KB |
| `TrafficLights`, `PrintedText`, `TimeSpans`, `Generics` | Fixed text; text built and printed with no string made; `TimeSpan`; generics over values and generic math | 4.6 to 8.5 KB, no heap |
| `Classes`, `HeapArrays`, `Shapes` | Objects, a list and a tree; arrays on the heap; virtual methods, interfaces and type tests | 11.6 to 13.4 KB + heap |
| `Text`, `Records`, `Boxing` | Commands read as text from a serial line; records and a `StringBuilder` report; values as objects | 17.5 to 23.4 KB + heap |
| `Tuples` | A crossroads' readings as tuples: the busiest lane and a summary returned together, a light switched on `(light, waiting)`, a grid keyed by `(row, column)`, collection expressions of lanes and of their names | 28.1 KB + heap |
| `GenericClasses`, `Delegates`, `ArrayMethods`, `Collections` | Generic classes; callbacks, closures and events; `Array.Copy`, `Sort`, `Resize`; `List`, `Dictionary`, `Queue`, `Stack` | 13.8 to 36.6 KB + heap |

**`examples/`** holds projects for the Pico, each a `.csproj` that references `ChipSharp.Hardware`, built with
`dotnet build -c Release` and then `chipsharp build bin\Release\net10.0\<Name>.dll -t pico` in its folder:

| Example | What it does | Pico |
|---|---|---|
| `pico-blink` | Blinks the LED with a delay loop | 1.1 KB, 8 bytes of RAM |
| `pico-timer-blink` | Blinks the LED from a timer interrupt, while `Main` does nothing | |
| `pico-button` | A button on GPIO 15 toggles the LED through a pin interrupt | |
| `pico-traffic-light` | Two lanes of traffic lights (GPIO 16 to 21), a step countdown on a 7-segment display (GPIO 2 to 8), the lane's turn on another (GPIO 9 to 15), and a button on GPIO 22 that cuts a green short; the countdown over UART0. No heap: static methods, arrays passed down, a `ref` parameter, a `volatile` flag set by the interrupt | 5.7 KB, 20 bytes of RAM |
| `pico-traffic-light-m3` | The same lights, displays, button and output, written with classes (`Lane`, `SevenSegment`, `Crossing`), a `List<Lane>`, an enum, a table of steps and an event | 17.3 KB, 1.1 KB of RAM + heap |

The traffic lights have a `diagram.json` for Wokwi with the whole circuit (220 Ω resistors, common-cathode displays, the
button between GPIO 22 and 3V3). With the button pressed at 2.5 s, both print:

```
road : 1
    green 4
    green 3
    green 2
lane : 1 interrupted, move to lane 2
    yellow 1
    red 2
    red 1
road : 2
    green 4
    ...
```

## Tests

```powershell
dotnet build -c Release
dotnet test -c Release --no-build
```

The 1,940 or so tests (about 2 minutes once the build cache is warm) check each stage on its own (reading IL, the whole-program pass, TIR, the LLVM code, linking, the `.uf2`),
then compile C# programs and run them: on the PC, and on the simulated Pico, comparing what they print, byte for byte,
with what .NET prints for the same program; the library code (collections, `StringBuilder`, sorting, `TimeSpan`)
runs next to .NET's own on thousands of random operations, and the math functions on hundreds of thousands of values,
computed both with the PC's float hardware and in software as the Pico computes them. The PC tests need LLVM; the Pico tests also need Node.js
and `npm ci` in `tools\rp2040-sim`, and are skipped, saying why, without them. The example projects are built with
`dotnet build` and `chipsharp build` and run in the simulator, the traffic lights with the button pressed.

The simulator, rp2040js 1.4.1, has one known fault: it sets the overflow flag of `SBCS` wrong when the borrow takes the
subtrahend to 0x80000000, so a comparison of extreme `long` values, such as `long.MaxValue` with `long.MinValue`, gives
the wrong answer on the simulated Pico only (a real RP2040 and the PC are right). The Pico tests leave those values to
the PC tests.

## How it works

### Layout

| Folder | What it holds |
|---|---|
| `src/ChipSharp.Frontend` | Reads the IL of a compiled C# `.dll`, finds every method the program uses (the whole-program pass), checks it against what ChipSharp compiles (`chipsharp check`), and translates it into TIR |
| `src/ChipSharp.Ir` | TIR (Typed Intermediate Representation), which every code generator reads: its verifier, printer and builder |
| `src/ChipSharp.Library` | ChipSharp's own C# for library types (`TimeSpan`, `List<T>`, `StringBuilder`, the exceptions, ...), compiled together with the programs that use them |
| `src/ChipSharp.Backend.Llvm` | The `llvm` code generator: TIR in, LLVM IR text out |
| `src/ChipSharp.Toolchain.Clang` | The `clang` toolchain: LLVM IR and runtime sources in, a linked program out |
| `src/ChipSharp.Runtime.*` | The runtimes, in C: `Shared` (console, text, floats, heap and collector), `Windows` (the PC), `ArmV6M` and `Rp2040` (start-up, UART0, timers, pins, interrupts) |
| `src/ChipSharp.Image.Uf2`, `src/ChipSharp.Build`, `src/ChipSharp.Cli` | The `.uf2` image, the build pipeline, and the `chipsharp` command |
| `src/ChipSharp.Hardware` | The library programs reference for pins, timing, registers and interrupts |
| `src/ChipSharp.Abstractions`, `src/ChipSharp.Targets` | The contracts (code generator, toolchain, image, runtime) and the target files' reader |
| `targets/arch`, `chip`, `board` | Target files |
| `samples`, `examples`, `tests`, `tools/rp2040-sim` | See above |

### The whole program

ChipSharp sees the whole program at once: `Main` and every method it reaches, through calls, virtual calls (rapid type
analysis: only the classes the program makes count), delegates and interrupt handlers. Only those methods are
compiled. A virtual call that one method can answer on every object the variable can hold becomes a plain call, and LLVM
inlines across the whole program. The same pass decides, for each array, string and object, where it can live: an
array that stays in its method is on the stack, one in a static field that never changes is a table in flash, one that
escapes is on the heap; text printed straight away is never made into a string. That is why so much works without a
heap.

**Library substitutes.** Library types are compiled from C# in `src/ChipSharp.Library`: a type marked
`[Substitute(typeof(System.TimeSpan))]` stands for `System.TimeSpan`, and only the members the program reaches are
compiled. The collections, `StringBuilder`, `Array.Sort` and the exceptions are .NET 10's own code ported this way, so
they grow, order, check and fail as .NET does. A new library type is C# there, not compiler code, and its tests compare
it with .NET value by value.

### Text

A string literal is **fixed text**: one constant string object in flash (`src/ChipSharp.Frontend/FixedText.cs`), laid
out as a string on the heap is, so code never needs to know which it has. **Printed text** (`PrintedText.cs`) follows
what C# makes of `$"..."`, `+` and `ToString()` (a `DefaultInterpolatedStringHandler`, or `string.Concat`) on IL's
evaluation stack to the `Console.Write` that prints it, and writes the parts one after another: every hole is worked out
first, in order, like .NET, and no string is made. Text that is kept (stored, returned, measured, passed on) is a
**string** (`Strings.cs`): its length, then its chars, 1 byte each when every char is below U+0100 and 2 bytes (UTF-16)
otherwise, always so, so two equal strings have the same form. Reading strings (compare, search, measure, print) needs
no heap; making them is the runtime's C (`chipsharp_string.c`, `chipsharp_string_heap.c`), with .NET's results and
messages. Text is written as UTF-8 on every target.

### Arrays and spans

Every array is the same value wherever the program holds one: the address of its elements and its length, so one method
takes arrays on the stack, in flash, in RAM and on the heap alike. `ArrayEscapeAnalysis` follows each array through
the whole program to decide where it lives (an array on the heap has a header and its length in front of its
elements); an empty array of each type is one constant object. A span is the same pair, a view of elements that live
elsewhere, so it needs no heap; `SpanEscapeAnalysis` keeps a span of a `stackalloc` or of an array on the stack from
outliving it. `Array.Copy` and the other `System.Array` methods are compiled for each element type
(`ArrayMethods.cs`); `Sort` and `BinarySearch` are .NET's own introsort and binary search, so equal elements end up
where .NET puts them.

### Math

`Math` and `MathF` are runtime functions in C (`src/ChipSharp.Runtime.Shared/chipsharp_float_math.c` and
`chipsharp_math*.c`) and, for what is more than one function (`Round` to digits, `Clamp`, `Sign`, `DivRem`, ...),
ChipSharp.Library's `Math` and `MathF`, .NET's own C# with its checks and messages. `double.Sin(x)` and generic math
over float types (`T.Sqrt`, `T.Sin`) are the same calls (`src/ChipSharp.Frontend/Generics/FloatMath.cs`).

**Exact functions give .NET's bits.** `Floor`, `Ceiling`, `Truncate`, `Round`, `CopySign`, `BitIncrement`,
`MinMagnitude`, `ILogB`, ... work on the bits with integer operations; `ScaleB`, `IEEERemainder` and the estimates
are .NET's arithmetic step for step; `FusedMultiplyAdd` is musl's exact one, in integers.

**Transcendental functions: one implementation, as near .NET's as measured.** .NET does not compute `Sin` or `Pow`
itself: it calls the C runtime, on Windows the UCRT, on Linux glibc, which are not correctly rounded, so their last bit
differs from each other's. Measured on this project's PC (.NET 10, Windows x64, a processor with FMA3) against musl,
CORE-MATH (correctly rounded) and AMD's library, the UCRT turns out to be AMD's: its reference C for `Asin` ...
`Tanh` gives .NET's bits, `Asinh`, `Acosh` and `Atanh` are formulas over `Log1p` and `Log`, and the rest is assembly
that uses the fused multiply-add, for which musl's functions are the nearest (CORE-MATH's correctly rounded ones are
further: .NET is 1 to 2 ulps from the exact result for a fifth of `Asinh`, `Acosh` and `Cbrt`, and they need 128-bit
integers and a fused multiply-add at every step, slow without an FPU). The share of values whose result has .NET's
bits, over 40,000 to 80,000 values each (the others are 1 ulp away; `Cbrt` and `Math.Cosh`: 2):

| Function | From | `Math` | `MathF` |
|---|---|---|---|
| `Sin`, `Cos`, `Tan` | musl (`MathF.Tan`: AMD) | 98.8, 98.8, 97.1 % | 99.9, 99.9, 99.999 % |
| `Asin`, `Acos`, `Atan`, `Atan2` | AMD | 99.6, 99.8, 99.97, 99.995 % | 99.7, 99.9, 100, 100 % |
| `Sinh`, `Cosh`, `Tanh` | AMD | 99.3, 97.9, 99.8 % | 100, 100, 99.9 % |
| `Asinh`, `Acosh`, `Atanh` | the UCRT's formulas over musl's `Log1p` | 94.4, 88.8, 93.7 % | 93.1, 87.6, 92.3 % |
| `Exp`, `Log`, `Log2`, `Log10`, `Pow` | musl | 99.6, 99.9, 99.7, 99.3, 99.9 % | 99.97, 99.6, 100, 96.9, 99.7 % |
| `Cbrt` | musl | 69.4 % | 69.3 % |

Every function computes with `+ - * /` and integers only, each step rounded as IEEE 754 says and none fused, so the PC
(SSE2) and the Pico (compiler-rt's soft float) compute the same bits: the tests run every function both ways on every
value they try, and the same program prints the same bytes on both. The `MathF` functions use float algorithms
(musl's evaluate a short polynomial in doubles, so that a float result is rounded once). A program links only the
functions it calls; on the Pico, beyond the soft-float routines, and the cycles of a call at 125 MHz:

| Family | Flash | Cycles |
|---|---|---|
| `Floor`, `Round` to digits by a `MidpointRounding` | 0.2 KB, 0.4 KB | 107 (`Floor`) |
| `Math.Sin` / `MathF.Sin` (with the reduction of huge arguments) | 8.6 KB / 8.0 KB | 11,500 / 7,000 |
| `Math.Exp` / `MathF.Exp` | 3.0 KB / 1.4 KB | 7,100 / 4,500 |
| `Math.Log`, `Math.Atan2`, `Math.Sinh`, `Math.Cbrt` | 5.5, 5.9, 3.1, 0.9 KB | 8,200 (`Log`), 11,600 (`Atan2`) |
| `Math.Pow` / `MathF.Pow` | 8.7 KB / 2.3 KB | 23,200 / 10,000 |
| `Math.Sqrt`, `Math.FusedMultiplyAdd` | in the float math, 2.0 KB | 1,700 (`Sqrt`) |

Licenses: musl's functions are MIT (with the notices of Sun's fdlibm, Arm's optimized routines and, for `fmaf`,
David Schultz's BSD license), AMD's are BSD-3-Clause; each file has its notices.

### Generics

Like C++ templates (and like .NET itself for structs), ChipSharp makes a copy of a generic method or type for each list
of type arguments the program uses (`src/ChipSharp.Frontend/Generics`): `Program.Max<int>` is a method of its own, and
the generic code itself is never compiled. Through `T`, an instance does what code written for its type does, with no
boxing: a constrained call on a struct calls the struct's own method, generic math is the operation itself. Over
classes too each instance is a class of its own, with its own descriptor and .NET's name (``Box`1[Program+Lane]``):
measured on the Pico, one shared copy over `object`, as .NET shares, would be larger (each caller casts what it gets
back), and LLVM inlines the instances' methods into their callers anyway.

### Objects, virtual calls and interfaces

An object starts with its header, the address of its type's **descriptor** (in flash: size, where its references are,
its base, its .NET name, its virtual method table and interface map), then its fields, its base classes' first
(`src/ChipSharp.Ir/TirTypeDescriptor.cs`). A virtual call loads the header and calls through the table: two loads and
an indirect call. An interface call finds the interface in the object's interface map (a short scan) and calls through
its table. `is`, `as` and casts walk the class's bases or look in the interface map. Each class the program makes has
tables only for the slots that can run two methods or more.

A **delegate** is an object of 16 bytes on the Pico: header, target, method address, and the list of a multicast
delegate. Every invocation is the same four instructions: a null check, two loads, one indirect call. A **box** is an
object of the class `T (boxed)`, its header and the value, with .NET's `ToString`, `Equals` and `GetHashCode` for the
value; in generic code over values, a box whose test the type argument decides is folded away, as .NET's JIT does. A
`Nullable<T>` is a struct, so it needs no heap. A **record** is a class or struct like any other; what C# generates for
it compiles as the program's own code, with ChipSharp's `StringBuilder` for its `ToString` and **type objects**
(`typeof(X)`: one constant object per type in flash, 24 bytes on the Pico) for its `EqualityContract`.

### try/finally, throw and collections

ChipSharp has no exceptions to catch yet: before anything reads a method's IL, its `finally` regions become plain
branches (`src/ChipSharp.Frontend/TryFinally.cs`), so a `finally` runs when its `try` block is left normally. A `throw`
calls the library's `Exceptions.Thrown.Unhandled`, which prints `Unhandled exception. ` and the exception's
`ToString()` and stops the program. The exceptions are library substitutes with .NET 10's messages, and so are `List<T>`,
`Dictionary<TKey, TValue>`, `HashSet<T>`, `Queue<T>` and `Stack<T>` (`src/ChipSharp.Library/Collections`), which
enumerate in .NET's order whatever the hash codes are. A `T[]` is an `IList<T>` and the rest through the library's
`SzArray<T>`, .NET's SZArrayHelper, with the array as `this`. `Comparer<T>.Default` of a class that is not comparable
is .NET's ObjectComparer: two such objects stop the program with its `ArgumentException`, which a sort wraps in .NET's
`InvalidOperationException`, as .NET does. A fault handler (C# puts one around an iterator's body, to dispose it when an
exception leaves it) never runs, as an exception stops the program: no `leave` goes through it.

A **tuple** is a `System.ValueTuple<...>`, whose library substitute (`src/ChipSharp.Library/ValueTuple.cs`) is a generic
struct laid out as .NET's: its elements are public fields, which C# reads and writes itself, so naming, deconstructing,
`==` and `switch` need no heap, and `Equals`, `CompareTo` and `GetHashCode` compare the elements as the default comparers
do, with no comparer object and no box, so a tuple is a dictionary key at no cost of its own. A **collection
expression** into a list is C#'s `CollectionsMarshal.SetCount` and `AsSpan`, and a spread copies through spans: a span
of strings or objects is a span like any other, whose pointer into the middle of the array's elements keeps the array
alive through the conservative scan of the stack; a spread of a sequence is `Enumerable.ToList` or `ToArray`.

### The heap and the collector

The collector (`chipsharp_heap.c`) marks in a bitmap of its own, so an interrupt handler that reads an object during a
collection sees it as it was, and only garbage, which no handler reaches, becomes free. The stack and the registers are
read conservatively; so that a stale word does not keep garbage alive, the collector zeroes the stack its own frames are
about to use, the code generator starts every stack slot at zero, and `GC.Collect()` clears the callee-saved registers
first. Small objects are carved one after another from a free block; one of 256 bytes or more takes the smallest free
block that holds it. On the PC the heap is 256 MB taken from Windows at the start (its pages only when touched). On a
chip, a function in a cycle of calls, or with 256 bytes of stack or more, compares the stack pointer with
`__chipsharp_stack_limit` (1 KB above the stack's bottom) when it starts, and below it says `Stack overflow.` from a stack
of its own (`src/ChipSharp.Backend.Llvm/LlvmStackChecks.cs`).

## Roadmap

| Milestone | Goal |
|---|---|
| M0 (done) | Simple C# compiled to a PC program through LLVM; every sample matches .NET |
| M1 (done, in the simulator) | Blink the Raspberry Pi Pico's LED; every sample also runs on the simulated RP2040 and matches .NET over UART0 |
| M2 (done) | Code for chips without a heap: static fields, `float` and `double` bit for bit, structs, enums, `ref`/`out`/`in`, arrays on the stack, interrupts (timer and pin), `TimeSpan` and the first library substitutes |
| M3 (done) | The heap: classes, garbage collector, strings, interfaces, generics, `Span<T>`, delegates, boxing, records, exceptions you throw, collections |
| M4 | ESP32-C5 (RISC-V) |
| M5 | CH32V003, C# on a 10-cent chip with 2 KB of RAM: M2-style code and the M3 pieces that need no heap (generics over values, spans, fixed and printed text, record structs) |
| Later | Catching exceptions (`try`/`catch`); more chips, including 8-bit ones through a C code generator, and an interpreter mode for the smallest |
