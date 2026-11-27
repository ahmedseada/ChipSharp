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

`dotnet test` runs 1273 tests. The PC tests need LLVM (`C:\Program Files\LLVM\bin`, or set `CHIPSHARP_CLANG` /
`CHIPSHARP_LLVM_BIN`); the Pico tests also need the simulator (see below), and are skipped, saying why, without it.

## What C# compiles today

| C# | Today |
|---|---|
| Methods | Static methods, top-level statements, static local functions, recursion; the methods, constructors, properties and operators of structs and classes |
| Values | `bool`, every whole number (`sbyte` to `ulong`, `nint`, `nuint`), `float` and `double` (computed and printed bit for bit like .NET) |
| Code | `if`, `switch`, `for`, `while`, `do`, `foreach` over arrays, `&&`, `\|\|`, `?:`, patterns on numbers; arithmetic wraps around like unchecked C# |
| Types | Structs, enums (`[Flags]` too), `ref`, `out` and `in` parameters, `ref` locals |
| Classes | On a target with a heap: the program's classes, `sealed` or not, nested, derived from each other (and from an `abstract` class): `new`, fields of the types above, of objects (the class itself too: lists, trees) and of structs holding objects, field initializers, constructors (overloads, `this(...)`, `base(...)`), instance and static methods and properties, `this`, a method hidden with `new`; objects in locals, parameters, return values, the fields of objects and of structs, and static fields; `null`, `==`, `!=`, `is null`, `ReferenceEquals`, `?.` and `??` (`??=` too), object initializers (`new Node { Value = 1 }`), `object`. The garbage collector frees what the program no longer reaches |
| Virtual calls | On a target with a heap: `virtual`, `override` and `abstract` methods and properties over any number of levels, `sealed override`, `base.M()`, a `new virtual` method that opens a slot of its own; object's `Equals` and `GetHashCode` (overridden, `base.Equals`, and object's own: the same object, and a number that stays the same for it). A call that only one method can answer, on every object the program makes that the variable can hold, is a plain call (a sealed class, a sealed override, a single subclass made), and a method no object of the program can run is left out |
| Interfaces of classes | Classes that implement interfaces implicitly and explicitly, through a base class, again in a derived class, interfaces that inherit interfaces, default methods, properties, generic interfaces over values (`IStore<int>`) and the library's `IComparable`, `IComparable<T>`, `IEquatable<T>` and `IDisposable` over values; interfaces as the types of locals, parameters, return values, fields and static fields |
| Type tests and casts | `is`, `as` and `(T)x` to classes, interfaces, `object` and arrays (`o is int[]`, covariant arrays of objects too), `x is Square s`, property patterns, `switch` on types, `is null`, `is not null`; a cast that fails stops the program with .NET's `InvalidCastException` message |
| Generics | Generic methods, structs and static classes over values (numbers, bools, enums, structs, generic structs, fixed text), nested in each other: `static T Max<T>(T a, T b) where T : IComparable<T>`, `static void Swap<T>(ref T a, ref T b)`, `struct Pair<T>`, made once for each type argument, with no heap. Interfaces implemented by structs, through a constraint (`static void Run<T>(ref T device) where T : struct, IDevice`, explicit implementations and static abstract members too) or called on the struct itself; `CompareTo`, `Equals`, `GetHashCode` and `ToString` of a number `T`; generic math (`INumber<T>`): the operators, comparisons, `T.Zero`, `T.One`, `MinValue`, `MaxValue`; `default(T)`, `typeof(T) == typeof(int)`, `sizeof(T)`; a `T` printed and in printed text |
| Static fields | Of the types above, `volatile` too, with field initializers and static constructors |
| Arrays | Of numbers, bools, structs and enums, on the stack: a constant size, kept in the method that makes them and passed down to the methods it calls. On a target with a heap also on the heap: returned, kept in static fields, in the fields of objects and in other arrays, of a size known only when the program runs (`new int[n]`), `null` (`a == null`, `a?.Length ?? 0`), arrays of arrays (`int[][]`), of objects and of the program's classes; `[]` and `Array.Empty<T>()`, `foreach`, `.Length`, bounds checks and .NET's messages for null and a negative length. An array that stays in its method with a constant size is still on the stack |
| Spans | `Span<T>` and `ReadOnlySpan<T>` with no heap, of arrays, of `stackalloc` (a constant size, initializers too), of constant data (`static ReadOnlySpan<byte> Digits => new byte[] { 0x3F, ... };`, in flash on a chip) and of variables (`MemoryMarshal.CreateSpan(ref x, 1)`): the indexer (`ref` too), `Length`, `IsEmpty`, `Slice`, ranges and indices (`s[2..5]`, `s[^1]`), `foreach`, `CopyTo`, `TryCopyTo`, `Fill`, `Clear`, `==`; as parameters, locals, return values and fields of a ref struct, of a generic `T` too |
| Static arrays | Arrays in static fields that the field's initializer gives once, with a constant size and constant elements: `static readonly byte[] Digits = { 0x3F, 0x06, ... };` is a constant table when nothing writes an element (in flash on a chip), `static int[] buffer = new int[16];` a fixed array in RAM; read, indexed and passed to methods like any array, with no heap |
| Fixed text | String literals (`"road : 1"`, `string.Empty`) in locals, parameters, return values, static fields (`static readonly` too) and `const`, compared with `null`; printed part by part with `Console.Write` and `Console.WriteLine` (`null` as nothing, like .NET), as UTF-8. Each literal is in flash once on a chip |
| Printed text | Text built and printed straight away, with no string ever made and no heap: `Console.WriteLine($"road : {lane}")`, `Console.Write("road : " + lane)`, `lane.ToString()`; holes of text and of every type `Console.Write` prints, worked out first, in order, like .NET; alignments (`{x,5}`, `{x,-5}`, not of floats) and the formats `D`, `D<n>`, `X<n>`, `x<n>` and `G` of whole numbers |
| Library | `Console.Write` and `Console.WriteLine` of text, a number or a bool; `Math` and `MathF`: `Abs`, `Min`, `Max`, `Sqrt`; `TimeSpan`: its factories in any unit (`FromMilliseconds(500)`, `FromSeconds(1.5)`), parts, totals, arithmetic and comparisons; on a chip, `ChipSharp.Hardware`: pins, timing (`Timing.Delay(TimeSpan)` too), registers and interrupts; for spans, `MemoryExtensions` (`IndexOf`, `LastIndexOf`, `Contains`, `SequenceEqual`, `StartsWith`, `EndsWith`, `Reverse`, `AsSpan`), `BinaryPrimitives` (`Read`, `Write`, `TryRead` and `TryWrite` of the whole numbers, `float` and `double`, big- and little-endian; `ReverseEndianness`) and `MemoryMarshal` (`Read`, `Write`, `TryRead`, `TryWrite`, `AsBytes`, `Cast`, `CreateSpan`), at any offset |

| Not yet | When |
|---|---|
| Object's `ToString` (and printing an object: `Console.WriteLine(node)`, `$"{node}"`) and records (with M3's stored strings), a struct turned into an interface or `object` (boxing) and back (unboxing: `(int)o`, `o is int`), generic virtual methods, generics over classes (`List<T>`, generic classes, a class, `object` or an array as a type argument), `ref` and `out` of an object, type tests against `string` and library classes, `?.` that gives a number (an `int?`); keeping built text (in a variable, a field, a return value), `string.Format` and the other methods of string, `==` on text, enum names in text, text in the fields of structs, `char`; arrays of strings, an array in the field of a struct, `ToArray` of a span | With M3 (the heap) |
| Exceptions (`try`, `catch`, `finally`, `throw`, `using`), `checked` arithmetic, multi-dimensional arrays, array covariance (`object[] items = new int[2][]`), `ref` and `out` of an array, a static array kept outside its field, unsafe code (pointers, `stackalloc` into a pointer, `stackalloc` of a size known only when the program runs), collection expressions as spans (`Span<int> s = [1, 2];`), delegates and lambdas, `typeof`, finalizers, classes derived from library classes | Later |
| The methods of `System.Array` (`Array.Copy`, `Array.Fill`, `Array.Clear`, `Array.IndexOf`, `Array.Reverse`, `array.Clone()`, ...) | Not yet: `chipsharp check` says where, and to write the loop |
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
  MemoryExtensions: IndexOf, LastIndexOf, Contains, SequenceEqual, StartsWith, EndsWith, Reverse, AsSpan
  TimeSpan: FromTicks, FromDays, FromHours, FromMinutes, FromSeconds, FromMilliseconds, FromMicroseconds, Add, Subtract, Negate, Multiply, Divide, Duration, Compare, CompareTo, Equals, its constructors, properties and operators
  Span<T>: Slice, CopyTo, TryCopyTo, Fill, Clear, its constructors, properties and operators
  ReadOnlySpan<T>: Slice, CopyTo, TryCopyTo, its constructors, properties and operators
  BinaryPrimitives: ReverseEndianness, ReadInt16BigEndian, ReadInt16LittleEndian, ReadUInt16BigEndian, ReadUInt16LittleEndian, ReadInt32BigEndian, ReadInt32LittleEndian, ReadUInt32BigEndian, ReadUInt32LittleEndian, ReadInt64BigEndian, ReadInt64LittleEndian, ReadUInt64BigEndian, ReadUInt64LittleEndian, ReadIntPtrBigEndian, ReadIntPtrLittleEndian, ReadUIntPtrBigEndian, ReadUIntPtrLittleEndian, ReadSingleBigEndian, ReadSingleLittleEndian, ReadDoubleBigEndian, ... (73 in all)
  MemoryMarshal: Read, Write, TryRead, TryWrite, AsBytes, Cast, CreateSpan, CreateReadOnlySpan
  ChipSharp.Hardware: Pin, Timing, Registers (see the README)
```

The other samples show the rest of the table, e.g. `VendingMachine.cs` (structs, enums, an array of structs and static
fields as a state machine), `Projectile.cs` (floats in structs, with operators), `TimeSpans.cs` (TimeSpan arithmetic),
`TrafficLights.cs` (fixed text between numbers), `PrintedText.cs` (text built and printed straight away),
`LookupTables.cs` (a 7-segment display's tables in flash and a buffer of readings in RAM), `Generics.cs` (generic
methods and structs, devices used through a constraint, generic math), `Spans.cs` (one `Sum` over an array, a slice,
`stackalloc` and a table in flash; a sensor's packet read with `BinaryPrimitives` and `MemoryMarshal`, a struct from an odd
offset, a checksum over its bytes), `Shapes.cs` (an abstract shape and its overrides, interfaces called through a list
and a static field, `is`, `as`, casts and a switch on types, `Equals` and `GetHashCode`), `Classes.cs` (a linked list and a tree of
objects, constructors calling each other, a derived class, objects in static fields, and enough short-lived objects to
make the Pico's garbage collector run again and again) and `HeapArrays.cs` (arrays returned, of sizes known when the
program runs, an array of arrays, an array kept in a static field and replaced, null, and many arrays used once).

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
runs, never given one) is assigned while the program runs, on the heap (below); on a chip without a heap,
`chipsharp check` says which, where, and what to write instead.

### Arrays on the heap

On a target with a heap, an array that leaves the method that makes it, whose size is known only when the program runs,
or that a loop makes again while the one from before is still in use, is an object on the heap
(`src/ChipSharp.Frontend/HeapArrays.cs`), which the garbage collector frees once nothing uses it:

```csharp
static int[] latest;                                  // null until the first window

static int[] Read(int count)                          // a size known when the program runs, returned
{
    int[] readings = new int[count];
    ...
    return readings;
}

static void Keep(int[] window) => latest = window;    // kept in a static field: its callers' arrays are on the heap
```

Every array, on the stack, in flash, in RAM or on the heap, is the same value where the program has one (a local, a
parameter, a return value): the address of its elements and its length, so one method takes them all, and an array
that stays in its method with a constant size is on the stack as before, on every target. Null is the value of zeros;
every other array has an address of its elements, the empty ones too (`[]` is one empty array of the program's, outside
the heap). Where the collector reads references exactly (a static field the program assigns, a field of an object, an
element of an array of arrays or of objects) the array object itself is kept, which is the address of its elements less
the length and header in front of them; on the stack the address of the elements keeps the object alive. The analysis
(`ArrayEscapeAnalysis`) follows each array through the whole program, the parameters of every method it is passed to
included, so that only an array on the heap is ever kept that way; a constant table in flash, which has no object
around it, is not (keeping one outside its field is rejected). An array that may be null (read from a field, given null
by a caller) is measured with a null test, and its bounds check tells null from an index outside the array only when the
index is not in range. On a chip without a heap, each of these needs a heap, and `chipsharp check` says so, with the
same hints as before.

### Spans

A `Span<T>` or `ReadOnlySpan<T>` is a view of elements that live somewhere else, so it needs no heap
(`src/ChipSharp.Frontend/Spans.cs`). It is the same value as an array, the address of its first element and how many
there are, so an array becomes a span as it is, and a slice is a new address and length. What a span can view: an
array (on the stack or in a static field), a `stackalloc` (a constant size: memory of the method, like an array on the
stack, zeroed where it runs), C#'s constant data and a variable:

```csharp
static ReadOnlySpan<byte> Digits => new byte[] { 0x3F, 0x06, 0x5B, 0x4F };   // a constant table, in flash on a chip

static int Sum(ReadOnlySpan<int> values) { ... }

Sum(samples);                       // an array
Sum(samples.AsSpan(1, 3));          // a slice of it, or samples.AsSpan()[1..4]
Span<int> buffer = stackalloc int[4];
Sum(buffer);
```

Span's own members, the `AsSpan` of arrays and the safe part of `MemoryMarshal` are compiled by ChipSharp itself, with
.NET's checks: an index outside a span stops the program with IndexOutOfRangeException's message, a slice outside it with
ArgumentOutOfRangeException's, a `CopyTo` a shorter span with ArgumentException's ("Destination is too short."), as
.NET says them. `MemoryExtensions` and `BinaryPrimitives` are library substitutes, plain C# over spans
(`src/ChipSharp.Library`). `MemoryMarshal.Read` and `Write` (and `BinaryPrimitives` with them) read and write a value
at any offset, unaligned, so a Cortex-M0+, which faults on a word that is not at a multiple of its size, reads a struct
from the middle of a packet as a PC does. `MemoryMarshal.Cast` gives a span of other elements of the same memory; on a
chip without unaligned access (the target's `"unalignedAccess": false`, as `armv6m` says) it checks that the span starts
where those elements may be, and stops the program with DataMisalignedException's message when it does not (read them
with `MemoryMarshal.Read` instead); a PC reads them anywhere. Only values without references, laid out as C# lays them
out by default, are read and written as bytes.

C# keeps a span of `stackalloc` or of a variable from outliving it, but takes an array to be on the heap: `Span<int> s =
new int[4]; return s;` is fine C#. Here an array that stays in its method is on the stack, so a span of it keeps to the
array's rule (`src/ChipSharp.Frontend/Analysis/SpanEscapeAnalysis.cs`): it may be kept in locals, sliced and passed to
methods, which may hand it back (`static Span<int> Tail(Span<int> s) => s.Slice(1);`); on a target with a heap, an
array whose span leaves the method that made it (returned, or stored where its caller sees it), or is still in use when
a loop makes the array again, is on the heap instead (see "Arrays on the heap"), and a span of an array on the heap
goes wherever a span may. A `stackalloc` stays in its method, and must not be in use through a span from the time
before when a loop makes it again. `ToArray` makes an array, which needs the heap; a collection expression as a span
(`Span<int> s = [1, 2];`) and `stackalloc` into a pointer arrive later. `samples/Spans.cs` reads a sensor's packet.

### Generics

Generics over values need no heap: like C++ templates (and .NET itself for structs), ChipSharp makes a copy of a generic
method or type for each list of type arguments the program uses it with (`src/ChipSharp.Frontend/Generics`). Where the
IL calls `Max<int>`, or names a method or field of `Pair<int>`, the reference is linked to that instance, made the first
time: a method of its own, whose signature, locals and IL have `int` in place of `T`, so everything after the IL reader
takes it for code written for `int`. The program has exactly the instances it reaches, a generic method calling another
with its own `T` included; the generic code itself is never compiled. Each is named as C# writes it:
`Program.Max<int>`, `Pair<int>.Swap`, whose symbols are `Program_Max_int_` and `Pair_int__Swap`. An instance costs what
the same code written for its type does: on the Pico a generic `Sum<T>` and `Pair<T>` used with `int` take exactly the
flash of the hand-written `int` versions.

In an instance, what the code does through `T` is what it does on that type, with no boxing: a constrained call
`device.Tick()` on a struct `T` calls the struct's own method (an explicit implementation, `void IDevice.Tick()`, too);
`a.CompareTo(b)`, `Equals` and `GetHashCode` of a number are ChipSharp.Library's `Numbers`, which give .NET's results
(a `byte`'s CompareTo is a difference, NaN equals NaN); `a + b`, `a <= b`, `T.Zero` and the rest of generic math on a
number are the operation itself, as .NET computes it (a `byte` sum wraps to a byte, a float comparison with NaN is
false); `Console.WriteLine(t)` writes the number. A class, an interface, `object` or an array as a type argument needs
the heap (generics over classes arrive with M3), and so does a struct turned into an interface (`IDevice d = counter;`
boxes it): `chipsharp check` says where, and to use a constraint instead. A generic method that calls itself with an
ever bigger type argument (`F<T>` calling `F<Pair<T>>`) stops at 8 generic types deep.

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

### Classes

The objects of the program's classes live on the heap (`src/ChipSharp.Frontend/ClassRules.cs`, `ObjectTypes.cs`).
`new Node(1, list)` makes a zeroed object of the class, whose header is the address of the class's descriptor, and runs
the constructor on it; an object has the fields of its base classes first, then its own, so a field is at the same place
in the objects of every class derived from its own. A call of an instance method on an object is C#'s `callvirt`:
ChipSharp checks the object for null, as .NET does, then calls the method like any other (`this` is never null, so its
fields need no check). A field of null, or a method called on null, stops the program with .NET's
NullReferenceException message.

```csharp
class Node
{
    public int Value;
    public Node Next;

    public Node(int value, Node next)
    {
        Value = value;
        Next = next;
    }
}

static Node list;                     // a static field: a root of the garbage collector

for (int i = 0; i < 5; i++)
{
    list = new Node(i, list);
}
```

On the Pico a `Node` takes 16 bytes of the heap (its header, an int and a reference, 12 bytes, rounded up to 8). Object's
`ToString` and records arrive with M3's stored strings, generic classes with generics over classes: `chipsharp check`
says so for each place. On a target without a heap the classes themselves compile (their static members, their virtual
calls and casts, a reference that stays null); each place that makes an object needs a heap. `samples/Classes.cs`
shows the rest.

### Virtual calls and interfaces

The whole program is known, so ChipSharp compiles only what can run (`src/ChipSharp.Frontend/VirtualDispatch.cs`,
rapid type analysis): a virtual call reaches the method its slot runs on each class whose objects the program makes
(`new`), and a class made reaches the methods of the slots called so far, until nothing new is reached. An override of
a class no one makes, or of a method no one calls, is not in the program. A call with one possible target, on every
object made that its variable (a local, a parameter, a field, a call's result, a cast) can hold, is a plain call, after
the object's null check, as .NET checks it; one with none is the null check alone. On the Shapes sample that makes the
Pico's flash 11.2 KB instead of 11.8 KB.

The other calls go through the object's descriptor, the address in its header (`src/ChipSharp.Frontend/Lowering`,
`VirtualTables.cs` and `MethodLowering.Virtual.cs`):

- **Virtual methods**: each class the program makes has a virtual method table in its descriptor (in flash on a chip):
  object's `Equals` and `GetHashCode` first, then its base class's slots, then those its own methods open (`virtual`,
  `abstract`, `new virtual`); an override fills in the slot of what it overrides. A slot is in the tables only when a
  call through it can run two methods or more. A call loads the object's header, the slot's address from the table and
  calls it: `call indirect`, two loads and a call, the same on every target.
- **Interfaces**: each class made has an interface map, one entry for each interface it implements (through a base
  class too) that a call or a type test needs: the interface's descriptor and a method table, the methods the class runs
  for the interface's slots, in the order the program first calls them. A call finds the entry with
  `ChipSharp.Object.FindInterface`, a scan of a map that holds a few entries, then calls through its table. Explicit
  implementations, a method of a base class, an override of it in a derived class, re-implementation in a derived class
  and default interface methods are resolved when compiling, as .NET resolves them.
- **Type tests and casts**: `is` and `as` against a class walk the object's class and its bases to the one tested;
  against an interface look it up in the interface map; against an array type compare the array's descriptor (an array
  of a class's objects is an array of its base's too, as .NET's arrays are covariant). A test the variable passes by its
  type is the object itself; one no object the program makes can pass is null. A cast that fails stops the program as
  .NET does: `Unhandled exception. System.InvalidCastException: Unable to cast object of type 'Circle' to type 'IShape'.`
  Each descriptor holds .NET's name of its type (`Program+Circle`); like .NET, the message names a nested type by its
  own name and an instance of a generic type with its type arguments (``IStore`1[System.Int32]``).
- **Object's `Equals` and `GetHashCode`**: an override where there is one; else object's own: the same object, and the
  object's address divided by 8, which stays the same for as long as the object lives (the collector never moves an
  object) and differs from every other live object's. .NET's own numbers are those of another heap, so the tests compare
  only what they do. An array, which can be an object, has object's own.

`samples/Shapes.cs` shows each of these.

### The heap

The heap is where objects live: what classes, strings, boxes and collections need (classes make them today; M3 brings
the rest). Its parts are in place for every target with a heap:

- An object starts with its header, the address of its type's descriptor, then its fields, those of its base classes
  first; an array has its length and then its elements. A descriptor (in flash on a chip) says the object's size,
  where its references are, its base, its name, and has room for the virtual methods and interfaces of its class
  (`src/ChipSharp.Ir/TirTypeDescriptor.cs`). In TIR an object is a `ref`, which the garbage collector follows, and an
  address a `ptr`, which it never follows.
- `new` takes zeroed memory from the heap; when there is none left, the garbage collector runs (mark and sweep, in
  `src/ChipSharp.Runtime.Shared/chipsharp_heap.c`): every object the static fields, the stack (the registers too) and
  other live objects reach stays, the rest is free again. When there is still not enough, the program stops with .NET's
  OutOfMemoryException message; a field of `null` stops it with NullReferenceException's.
- On a chip the heap is the target's `"heap"` bytes of RAM between the variables and the stack, and the collector runs
  with interrupts off; an `[Interrupt]` method, and every method it calls, never makes an object (`chipsharp check`
  says where; the program would stop there, saying so). On the PC the heap is the target's 256 MB, taken from Windows at
  the start.

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
somewhere to go (and `"unalignedAccess"`, whether a word may be read at any address: see Spans). The PC has a 256 MB heap and its console; the Pico a 224 KB heap (264 KB of RAM less 8 KB of stack and
32 KB for static fields) and UART0; a new chip inherits `armv6m`'s: neither, until its file says so.
`chipsharp check -t <target>` and `build` judge the program for that chip, so on one without a heap an object is out
of reach (a class whose objects are never made still compiles):

```
creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) has no heap: 1 place in Program.Main() (IL_0000). Use structs and static methods.
Console.Write and Console.WriteLine need a console, and the target 'tiny' has none: 1 place in Program.Main() (IL_000a). Show what the program does another way, e.g. on a pin with ChipSharp.Hardware.Pin.
```

`chipsharp build` ends with what the program takes of the chip, read from the linked `.elf`: flash for its code,
constants and the starting values of its variables, RAM for its variables, with the heap (`"heap"`) after them and the
stack (`"link.stack"`) kept at the end of the RAM. For `examples/pico-blink`:

```
flash 1.1 KB of 2048 KB (0.1%), RAM 8 bytes + heap 224 KB of 264 KB (stack 8 KB reserved)
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
| M3 | Classes, heap, garbage collector, strings, interfaces, generics, `Span<T>` (done, with no heap): the Full tier |
| M4 | ESP32-C5 (RISC-V) |
| M5 | CH32V003: C# on a 10-cent chip |
| M6 | C code generator for 8-bit chips |
| M7 | Interpreter mode |
