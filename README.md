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

`dotnet test` runs 1556 tests. The PC tests need LLVM (`C:\Program Files\LLVM\bin`, or set `CHIPSHARP_CLANG` /
`CHIPSHARP_LLVM_BIN`); the Pico tests also need the simulator (see below), and are skipped, saying why, without it.

## What C# compiles today

| C# | Today |
|---|---|
| Methods | Static methods, top-level statements, local functions (that capture variables too), recursion; the methods, constructors, properties and operators of structs and classes |
| Values | `bool`, `char`, every whole number (`sbyte` to `ulong`, `nint`, `nuint`), `float` and `double` (computed and printed bit for bit like .NET) |
| Code | `if`, `switch`, `for`, `while`, `do`, `foreach` over arrays, `&&`, `\|\|`, `?:`, patterns on numbers; arithmetic wraps around like unchecked C# |
| Types | Structs, enums (`[Flags]` too), `ref`, `out` and `in` parameters, `ref` locals |
| Classes | On a target with a heap: the program's classes, `sealed` or not, nested, derived from each other (and from an `abstract` class): `new`, fields of the types above, of objects (the class itself too: lists, trees) and of structs holding objects, field initializers, constructors (overloads, `this(...)`, `base(...)`), instance and static methods and properties, `this`, a method hidden with `new`; objects in locals, parameters, return values, the fields of objects and of structs, and static fields; `null`, `==`, `!=`, `is null`, `ReferenceEquals`, `?.` and `??` (`??=` too), object initializers (`new Node { Value = 1 }`), `object`. The garbage collector frees what the program no longer reaches |
| Virtual calls | On a target with a heap: `virtual`, `override` and `abstract` methods and properties over any number of levels, `sealed override`, `base.M()`, a `new virtual` method that opens a slot of its own; object's `Equals` and `GetHashCode` (overridden, `base.Equals`, and object's own: the same object, and a number that stays the same for it). A call that only one method can answer, on every object the program makes that the variable can hold, is a plain call (a sealed class, a sealed override, a single subclass made), and a method no object of the program can run is left out |
| Interfaces of classes | Classes that implement interfaces implicitly and explicitly, through a base class, again in a derived class, interfaces that inherit interfaces, default methods, properties, generic interfaces (`IStore<int>`, `IRepository<Lane>`) with variance (`out T`, `in T`: an `IProducer<Dog>` is an `IProducer<Animal>` in calls, casts and `is`), the library's `IComparable`, `IComparable<T>`, `IEquatable<T>` and `IDisposable`, and the collections' `IEnumerable<T>`, `IEnumerator<T>`, `ICollection<T>`, `IList<T>`, `IReadOnlyCollection<T>`, `IReadOnlyList<T>`, `IComparer<T>` and `IEqualityComparer<T>` (and `IEnumerable`, `IEnumerator`); interfaces as the types of locals, parameters, return values, fields and static fields |
| Type tests and casts | `is`, `as` and `(T)x` to classes, interfaces, `object` and arrays (`o is int[]`, covariant arrays of objects too), `x is Square s`, property patterns, `switch` on types, `is null`, `is not null`; a cast that fails stops the program with .NET's `InvalidCastException` message |
| Boxing | On a target with a heap: every value as an object or an interface (`object o = 5;`, `object[] { 1, "a", 2.5, Color.Red, point }`, `IDevice d = counter;`, `IComparable c = 4;`), a box of its own type named as .NET names it (`System.Int32`, `Program+Point`), with the struct's `ToString`, `Equals`, `GetHashCode` and interface methods (a call through the interface changes the box), else .NET's (a number's text and hash, an enum's name, `ValueType.Equals` field by field: NaN equal to NaN, `-0.0` to `0.0`, text by its chars); `IComparable`, `IComparable<T>` and `IEquatable<T>` of numbers, bools, chars and enums; unboxing (`(int)o`, `o is int n`, `o as int?`, `switch` on values) with .NET's rules (an enum and its underlying type unbox to each other, two numbers do not) and messages; `object.Equals(a, b)`; objects, structs and nullables in printed and kept text (`$"{shape}"`, `string.Format("{0} {1}", o, p)`, `Console.WriteLine("{0} {1} {2} {3}", a, b, c, d)` with its `params` span, `"x" + o`); a delegate of a struct's method. With no heap: a struct's `ToString`, `Equals` and `GetHashCode` called on it (`point.ToString()`), a value printed straight away, and generic code's tests of its boxes (`value is IComparable`, `value is int n`), which fold away |
| Nullables | `int?`, `bool?`, nullable enums and structs, with no heap, on every target: `HasValue`, `Value` (.NET's `InvalidOperationException` message with none), `GetValueOrDefault`, `??`, `default`, lifted arithmetic and comparisons, `is int n`; in locals, parameters, return values, fields, arrays and static fields; printed and in text (nothing with no value); boxed as .NET boxes them (null with no value, else the value's box) and unboxed back |
| Generics | Generic methods, structs and static classes over values (numbers, bools, enums, structs, generic structs, text), nested in each other: `static T Max<T>(T a, T b) where T : IComparable<T>`, `static void Swap<T>(ref T a, ref T b)`, `struct Pair<T>`, made once for each type argument, with no heap. On a target with a heap also over classes, interfaces, `object` and arrays, and generic classes (`class Stack<T>` of `Node<T>`, `class Derived<T> : Base<T>`, `class IntBox : Box<int>`, a class nested in one), each instance a class of its own with its own static fields and .NET's name (``Box`1[Program+Lane]``); constraints (`where T : class`, `where T : Animal`, `where T : IShape`, `where T : new()`, `where T : unmanaged`), `default(T)` (null), `new T()`, `new T[n]`, casts, `is` and `as` to `T`; generic virtual methods and generic methods of interfaces and of generic classes; the library's generic code over the program's types (`span.IndexOf(point)`, `Array.Sort` of structs that implement `IComparable<T>`); `EqualityComparer<T>.Default` and `Comparer<T>.Default` with .NET's results, with no boxing; `foreach` over a collection of the program's whose `GetEnumerator()` gives a struct or a sealed class. Interfaces implemented by structs, through a constraint (`static void Run<T>(ref T device) where T : struct, IDevice`, explicit implementations and static abstract members too) or called on the struct itself; `CompareTo`, `Equals`, `GetHashCode` and `ToString` of a number `T`; generic math (`INumber<T>`): the operators, comparisons, `T.Zero`, `T.One`, `MinValue`, `MaxValue`; `default(T)`, `typeof(T) == typeof(int)`, `sizeof(T)`; a `T` printed and in printed text |
| Delegates | On a target with a heap: `Action`, `Func<...>`, `Predicate<T>`, `Comparison<T>`, `Converter<TInput, TOutput>`, `EventHandler<T>` and the program's own delegate types (generic ones too), over values, text and objects; made of static and instance methods, virtual and interface methods (the override of the object they are made of), extension methods, library methods (`Action<int> print = Console.WriteLine;`), generic methods, and lambdas (`static` ones too) that capture nothing, variables, `this`, the variables of outer scopes and loop variables (`foreach` gives each its own, as .NET does), in generic methods too; `d(x)`, `d.Invoke(x)`, `d?.Invoke(x)` (null stops the program with .NET's `NullReferenceException` message); `+`, `-`, `+=` and `-=` with .NET's order and what is left (the last occurrence of a list taken out), every delegate of a multicast one called in order and the last one's result, `GetInvocationList`, `==`, `!=`, `Equals`, `GetHashCode`, `ToString` (the type's name), `is` and `as`; field-like events (`event Action Changed`, static ones too) and events with their own `add` and `remove`; delegates in locals, fields, static fields, arrays and as type arguments. A local function that is only called keeps what it captures in a struct, with no heap, on every target; an interrupt handler may invoke a delegate kept in a static field |
| Memory | The heap only for a program that uses it (none of its RAM or code otherwise); a garbage collector that runs with interrupts on; `GC.Collect()`, `GC.GetTotalMemory(bool)` and `GC.KeepAlive(object)`; a recursion with no end stops with .NET's `Stack overflow.`, on the PC and on a chip; `chipsharp build --verify-heap` checks the whole heap at every collection (see Memory, below) |
| Static fields | Of the types above, `volatile` too, with field initializers and static constructors |
| Arrays | Of numbers, bools, structs and enums, on the stack: a constant size, kept in the method that makes them and passed down to the methods it calls. On a target with a heap also on the heap: returned, kept in static fields, in the fields of objects and in other arrays, of a size known only when the program runs (`new int[n]`), `null` (`a == null`, `a?.Length ?? 0`), arrays of arrays (`int[][]`), of objects and of the program's classes; `[]` and `Array.Empty<T>()`, `foreach`, `.Length`, bounds checks and .NET's messages for null and a negative length. An array that stays in its method with a constant size is still on the stack |
| Spans | `Span<T>` and `ReadOnlySpan<T>` with no heap, of arrays, of `stackalloc` (a constant size, initializers too), of constant data (`static ReadOnlySpan<byte> Digits => new byte[] { 0x3F, ... };`, in flash on a chip) and of variables (`MemoryMarshal.CreateSpan(ref x, 1)`): the indexer (`ref` too), `Length`, `IsEmpty`, `Slice`, ranges and indices (`s[2..5]`, `s[^1]`), `foreach`, `CopyTo`, `TryCopyTo`, `Fill`, `Clear`, `==`; as parameters, locals, return values and fields of a ref struct, of a generic `T` too |
| Static arrays | Arrays in static fields that the field's initializer gives once, with a constant size and constant elements: `static readonly byte[] Digits = { 0x3F, 0x06, ... };` is a constant table when nothing writes an element (in flash on a chip), `static int[] buffer = new int[16];` a fixed array in RAM; read, indexed and passed to methods like any array, with no heap |
| Fixed text | String literals (`"road : 1"`, `string.Empty`) anywhere a string may be, on every target: printed (`null` as nothing, like .NET, as UTF-8), compared, measured, searched (the methods of string that make no new text). Each literal is an object in flash once on a chip: 8 bytes and 1 byte per char on the Pico |
| Printed text | Text built and printed straight away, with no string ever made and no heap: `Console.WriteLine($"road : {lane}")`, `Console.Write("road : " + lane + '!')`, `lane.ToString()`, `Console.WriteLine("{0,4} {1:X2}", a, b)`, `Console.WriteLine(color)`; holes of text, chars, enums (by name, `[Flags]` combined too, or with `D` and `X`), structs (by their `ToString`, run on the value), nullables (their value, or nothing) and of every type `Console.Write` prints, worked out first, in order, like .NET, the `params` span of `Console.WriteLine("{0} {1} {2} {3}", a, b, c, d)` too; alignments (`{x,5}`, `{x,-5}`, not of floats) and the formats `D`, `D<n>`, `X<n>`, `x<n>` and `G` of whole numbers |
| Strings | On a target with a heap: text built and kept (`string s = $"..."`, `+`, `+=`, `ToString()` of numbers, bools, chars and enums with the formats printed text has, `string.Concat`, `string.Join` of an array, `string.Format` of a literal format) in locals, fields of classes and structs, static fields, arrays (`string[]`), returned and passed anywhere; `==`, `!=`, `Equals`, `CompareOrdinal`, `CompareTo`, `IsNullOrEmpty`, `IsNullOrWhiteSpace`, `Length`, `s[i]`, `foreach`, `Substring`, `IndexOf`, `LastIndexOf`, `Contains`, `StartsWith`, `EndsWith`, `Replace`, `Trim`, `TrimStart`, `TrimEnd`, `PadLeft`, `PadRight`, `ToUpper`, `ToLower`, `Insert`, `Remove`, `Split`, `ToCharArray`, `new string(...)`, `GetHashCode`, `switch` on strings; `int.Parse` and `TryParse` (and those of the other whole numbers); `char.IsDigit`, `IsLetter`, `IsWhiteSpace`, `IsUpper`, `IsLower`, `ToUpper`, `ToLower`; spans of chars (`s.AsSpan()`, `stackalloc char[8]`, `span.ToString()`); `ToString()` of the program's classes (overrides, and object's own: the type's name), `Console.WriteLine(shape)`, `is string`, `as string`, `(string)o` |
| Library | `Console.Write` and `Console.WriteLine` of text, a number or a bool; `Math` and `MathF`: `Abs`, `Min`, `Max`, `Sqrt`; `TimeSpan`: its factories in any unit (`FromMilliseconds(500)`, `FromSeconds(1.5)`), parts, totals, arithmetic and comparisons; on a chip, `ChipSharp.Hardware`: pins, timing (`Timing.Delay(TimeSpan)` too), registers and interrupts; for spans, `MemoryExtensions` (`IndexOf`, `LastIndexOf`, `Contains`, `SequenceEqual`, `StartsWith`, `EndsWith`, `Reverse`, `AsSpan`), `BinaryPrimitives` (`Read`, `Write`, `TryRead` and `TryWrite` of the whole numbers, `float` and `double`, big- and little-endian; `ReverseEndianness`) and `MemoryMarshal` (`Read`, `Write`, `TryRead`, `TryWrite`, `AsBytes`, `Cast`, `CreateSpan`), at any offset; `System.Array`: `Copy` (every overload, the `long` ones too, overlapping copies within an array like .NET's), `ConstrainedCopy`, `CopyTo`, `Clear`, `Fill`, `IndexOf`, `LastIndexOf` and `Reverse` of arrays of every element type, `Sort` and `BinarySearch` of numbers, bools and structs that implement `IComparable<T>`, with no heap; on a target with a heap also `Resize` and `Clone`, and those that take a delegate, of arrays of every element type: `Exists`, `Find`, `FindAll`, `FindIndex`, `FindLast`, `FindLastIndex`, `ForEach`, `TrueForAll`, `ConvertAll` and `Sort` with a `Comparison` (.NET's introsort, so elements the comparison calls equal end up where .NET puts them); `EqualityComparer<T>.Default` and `Comparer<T>.Default` (`Equals`, `GetHashCode`, `Compare`); on a target with a heap, `GC.Collect`, `GC.GetTotalMemory` and `GC.KeepAlive` |

| Not yet | When |
|---|---|
| Records, `StringBuilder`, a `params` span of text (`string.Join(", ", a, b, c)`: give an array), the other formats (`F2`, `N0`), a format of an object or a struct (`$"{o:X2}"`) and `string.Format` of a format known only when the program runs or of an `object[]`, culture-aware comparisons and the case of letters past U+00FF, `ref` and `out` of a string; boxing a library struct (`(object)TimeSpan.Zero`), `unbox` to the address in a box of a nullable, type tests against `System.ValueType` and `System.Enum`, `Enum.HasFlag`, `GetType()`, `ref` and `out` of an object, type tests against library classes; an array in the field of a struct, `ToArray` of a span | With M3 (the heap) |
| Exceptions (`try`, `catch`, `finally`, `throw`, `using`), `checked` arithmetic, multi-dimensional arrays, array covariance (`object[] items = new int[2][]`), `ref` and `out` of an array (but `Array.Resize(ref a, n)`), a static array kept outside its field, unsafe code (pointers, `stackalloc` into a pointer, `stackalloc` of a size known only when the program runs), collection expressions as spans (`Span<int> s = [1, 2];`), function pointers (`delegate*`), delegates that take or return arrays or spans (`Action<int[]>`), `BeginInvoke` and `EndInvoke`, `typeof`, finalizers, classes derived from library classes | Later |
| The library's collections (`List<T>`, `Dictionary<TKey, TValue>`, ...), `foreach` over an enumerator that is disposed (one that implements `IDisposable`, as every `IEnumerator<T>` does: a `foreach` over an `IEnumerable<T>`), which C# does in a `try`/`finally`; an array used as an interface (`IEnumerable<int> e = numbers;`) | With M3 (collections) |
| `Array.Copy` between arrays of different element types (`int[]` into `long[]`), `Array.Sort` and `BinarySearch` of enums, structs that do not implement `IComparable<T>` and objects, `Array.IndexOf` of structs, of `object` and of classes with an `Equals` of their own | Later |
| Other library types and methods (`decimal`, `Random`, `Math.Sin`, `array.GetValue(i)`, the overloads of `System.Array` that take an `IComparer`, ...) | Not yet: `chipsharp check` lists the methods there are, and for `System.Array` says to write the loop |

`chipsharp check` (and `build`) say so for each feature a program uses, with what to write for now. The first program
ChipSharp was asked to run printed its result with `Console.WriteLine($"Logit result: {result}")`: printed text, which
compiles and prints what .NET prints (`samples/Logit.cs` prints the parts one after another, as it had to before). Made
in a method that returns it, `static string Report(float result) => $"Logit result: {result}";`, the text is kept: a
string on the heap, which compiles for the PC and the Pico, and on a chip without a heap (here a Pico whose target file
says "heap": false) says so:

```
error: Logit.dll uses 1 C# feature that ChipSharp does not compile yet (1 place):
  keeping built text in a variable needs a heap, and the target 'pico-no-heap' (264 KB RAM) has no heap: 1 place in local function Report (IL_0020). Print it straight away: Console.WriteLine($"...").
For every place, run: chipsharp check Logit.dll --details -t pico-no-heap
```

A call to a library method ChipSharp does not have lists the ones it has:

```
ChipSharp can call these library methods (--details lists each one with its parameters):
  Console.Write and Console.WriteLine of (nothing), text, int, uint, long, ulong, bool, float, double
  Math and MathF: Abs, Min, Max, Sqrt
  GC: Collect, GetTotalMemory, KeepAlive
  Array: Copy, ConstrainedCopy, CopyTo, Clear, Fill, IndexOf, LastIndexOf, Reverse, Resize, Clone, Sort, BinarySearch, Exists, Find, FindAll, FindIndex, FindLast, FindLastIndex, ForEach, TrueForAll, ... (21 in all)
  MemoryExtensions: IndexOf, LastIndexOf, Contains, SequenceEqual, StartsWith, EndsWith, Reverse, AsSpan
  TimeSpan: FromTicks, FromDays, FromHours, FromMinutes, FromSeconds, FromMilliseconds, FromMicroseconds, Add, Subtract, Negate, Multiply, Divide, Duration, Compare, CompareTo, Equals, its constructors, properties and operators
  Span<T>: Slice, CopyTo, TryCopyTo, Fill, Clear, its constructors, properties and operators
  ReadOnlySpan<T>: Slice, CopyTo, TryCopyTo, its constructors, properties and operators
  BinaryPrimitives: ReverseEndianness, ReadInt16BigEndian, ReadInt16LittleEndian, ReadUInt16BigEndian, ReadUInt16LittleEndian, ReadInt32BigEndian, ReadInt32LittleEndian, ReadUInt32BigEndian, ReadUInt32LittleEndian, ReadInt64BigEndian, ReadInt64LittleEndian, ReadUInt64BigEndian, ReadUInt64LittleEndian, ReadIntPtrBigEndian, ReadIntPtrLittleEndian, ReadUIntPtrBigEndian, ReadUIntPtrLittleEndian, ReadSingleBigEndian, ReadSingleLittleEndian, ReadDoubleBigEndian, ... (73 in all)
  Comparer<T>: Compare, its properties
  EqualityComparer<T>: Equals, GetHashCode, its properties
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
make the Pico's garbage collector run again and again), `HeapArrays.cs` (arrays returned, of sizes known when the
program runs, an array of arrays, an array kept in a static field and replaced, null, and many arrays used once),
`ArrayMethods.cs` (a ring buffer of readings moved along with `Array.Copy`, medians with `Array.Sort`, levels with
`Array.Fill` and `IndexOf`, a table searched with `BinarySearch`, a log grown with `Array.Resize` and copied with `Clone`)
`Delegates.cs` (callbacks, a closure counter, lambdas made in a loop that each keep their own variable, an event with two subscribers and an unsubscribe, readings sorted with a comparison and filtered with `Array.FindAll`, and enough short-lived closures to make the Pico's collector run) and `GenericClasses.cs` (a linked stack of readings and of numbers, a repository interface with two implementations,
`new T()`, `EqualityComparer<T>.Default` over a class with its own `Equals` and over `int`, and tens of thousands of
short-lived objects of generic classes) and `Boxing.cs` (a log of readings of mixed kinds in an `object[]` sorted out with
a switch on types, a struct read through an interface, `Equals` of boxes, nullable readings averaged, objects in formatted
text).

### Fixed text

A string literal is fixed text: it never changes, so it needs no heap. Each distinct literal is one constant string
object in read-only data (flash on a chip, once however often the program uses it), laid out as a string on the heap
is (see Strings, below), so code never needs to know which one it has; the collector never marks it, as it is outside
the heap (`src/ChipSharp.Frontend/FixedText.cs`). Text is written as UTF-8 on every target, the bytes .NET writes with
a UTF-8 console; a Windows console shows non-ASCII text right when its code page is UTF-8 (`chcp 65001`), and there
.NET itself writes in the console's code page. Text built from it and printed straight away is printed text (below).

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
-1 is `FF`), `G` and `R` for floats; a format of text, a char or a bool is ignored, as .NET ignores it. An enum is its
name, written by a function of its own made from its members (`[Flags]` ones combined, `Read, Write`, as .NET combines
them; a value without a name as its number), or its number with `D`, its hex digits with `X`. `string.Format`,
`Console.WriteLine("{0} of {1,4}", a, b)` and `Console.WriteLine(x)` of a boxed value are holes too, when the format is
a literal: ChipSharp reads it as .NET does. Text that is kept is a string, made on a target with a heap where the text
is finished (see Strings): stored in a field or in a variable (but a variable written once and read once, which is
what C#'s own temporaries are, and what Release makes of `string s = $"..."; Console.WriteLine(s);`), returned, passed
to another method, measured (`.Length`) or chosen where ways join (`c ? $"a{x}" : "b"`). On a target without a heap it
is "keeping built text in a variable", which needs a heap. The other formats (`F2`, `N0`) and an aligned float are
rejected, saying what to write for now. `samples/PrintedText.cs` shows each form.

### Strings

A string is an object (`src/ChipSharp.Frontend/Strings.cs`): a literal in flash, or text the program builds on the
heap, which the garbage collector frees once nothing uses it. Its `Length` and `s[i]` count UTF-16 code units, as .NET
does. The object is laid out as an array: its header (one of the runtime's two string descriptors), its length, then
its chars, 1 byte each when every char is below U+0100 (ASCII and Latin-1: `"café"` is 4 bytes) and 2 bytes each
(UTF-16) for any other text, always so, so that two equal strings have the same form. On the Pico a literal takes 8
bytes and its chars (`"road : 1"`, 16 bytes), as much flash as UTF-8 for ASCII; a string on the heap as much, rounded up
to 8. A span of chars views 2-byte chars: `s.AsSpan()` of a UTF-16 string is the string's own chars, of a 1-byte one a
UTF-16 copy on the heap.

What reads strings (compare, search, measure, print) needs no heap and works on every target, for literals; what makes
them (kept text, `Substring`, `Replace`, `ToUpper`, `Split`, number to text, ...) is the runtime's C
(`src/ChipSharp.Runtime.Shared/chipsharp_string.c` and `chipsharp_string_heap.c`), and needs a heap. Each method gives
.NET's results and stops the program with .NET's message where .NET throws (`"abc".Substring(4)`, `int.Parse("12x")`).
Comparisons are ordinal, char by char: `==`, `Equals`, `IndexOf`, `StartsWith`, `CompareTo` and `string.Compare` too,
as .NET compares them in its invariant mode (`CompareTo` gives the difference of the first chars that differ, as
`CompareOrdinal` does); with a culture .NET compares the last four by it (the same results for text without the
characters a culture ignores, such as a zero char, but for the order of upper and lower case letters: `"a".CompareTo("B")`
is 31 here and in invariant mode, -1 with a culture). `ToUpper`, `ToLower` and the char methods change case and tell letters as the
invariant culture does for the chars up to U+00FF (Latin-1, `ÿ` to `Ÿ` too); from U+0100 on a char is no letter and
keeps its case (`IsWhiteSpace` knows all of .NET's spaces). `GetHashCode` of a string is the same in every run (FNV-1a
of its UTF-16 code units), where .NET's differs from one process to the next. `int.Parse` and `TryParse` (and those of
`sbyte` to `ulong`) read as .NET's invariant culture does: spaces, a sign, digits. A `switch` on strings compiles as C#
makes it, in Release (by length and char, or by hash) and in Debug (`==` one after another).

`ToString()` of the program's classes is a virtual call like any other: an override gives its string, object's own the
type's .NET name (`Program+Node`); `Console.WriteLine(node)`, `"x" + node`, `$"{node}"` and `string.Format("{0}",
node)` call it (and write nothing for null). A call of object's `Equals`, `GetHashCode` or `ToString` on an `object` that
holds a string runs a string's, and `is string`, `as string` and `(string)o` test the object's header. `StringBuilder`
and a `params` span of text (`string.Join(", ", a, b, c)`, give an array) are not there yet; the `params` span of objects
of `string.Format` and `Console.WriteLine` of four values or more is printed or kept text like the others. `samples/Text.cs` takes commands as text, the way they come over a
serial line; on the Pico it takes 21.8 KB of flash, the runtime's text of floats and strings included.

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

Every empty array is an object of its own type, outside the heap (in flash on a chip): `Array.Empty<T>()`, `[]` and an
empty static array are one empty `int[]` for `int`, as .NET has one for each `T`, so a type test reads its header like
any array's (`(object)Array.Empty<int>() is long[]` is false), and the collector, which marks only what is in the heap,
never touches it.

### The methods of System.Array

`Array.Copy` and the rest are ChipSharp's own (`src/ChipSharp.Frontend/ArrayMethods.cs`): those that take an `Array`
learn the element type from the array on IL's evaluation stack, and work on arrays of every element type arrays hold,
the program's enums, structs and classes too, so they are compiled as TIR (`Lowering/ArrayMethodFunctions.cs`); `Sort`
and `BinarySearch` are a library substitute (`src/ChipSharp.Library/Array.cs`), .NET's own introsort, NaN pre-pass and
binary search, so equal elements that are not the same bits (`-0.0` and `0.0`, NaNs) end up where .NET puts them. Each
checks its arguments in .NET's order and stops the program with .NET's message, the value included where .NET says it
(`length ('-1') must be a non-negative value. (Parameter 'length')` and `Actual value was -1.` on a line of its own):

```csharp
Array.Copy(window, 1, window, 0, window.Length - 1);   // overlapping, as memmove does
Array.Sort(scratch);                                     // numbers and bools
int band = Array.BinarySearch(Thresholds, median);       // a table in flash: only read, so it stays there
Array.Fill(levels, Level.Normal);
int high = Array.IndexOf(levels, Level.High);            // an enum, by value; a NaN equals NaN, as Equals says
Array.Resize(ref log, log.Length * 2);                   // a local, a parameter or a field: needs a heap
int[] kept = (int[])log.Clone();                         // needs a heap
```

`IndexOf` and `LastIndexOf` compare as .NET's `EqualityComparer<T>.Default` does: numbers, bools and enums by value
(a float NaN equals NaN, `-0.0` equals `0.0`), arrays and the program's classes by reference; a struct, `object` and a
class with an `Equals` of its own would need that `Equals`, which they do not call yet. Only the elements a method
writes count as written (a copy's destination; the array of `Clear`, `Fill`, `Reverse`, `Sort`), so a constant table that
`IndexOf` or `BinarySearch` reads stays in flash. Nothing allocates but `Resize` and `Clone`, which make the new array
before they copy into it, so the references of an array are never half copied when a collection comes: the collector
runs only when an allocation needs one.

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
false); `Console.WriteLine(t)` writes the number. A struct turned into an interface (`IDevice d = counter;` boxes it)
needs the heap: on a chip without one `chipsharp check` says where, and to use a constraint instead (see Boxing, below,
for the tests of a box of `T` that fold away). A generic method that calls itself with
an ever bigger type argument (`F<T>` calling `F<Pair<T>>`) stops at 8 generic types deep.

**Generics over classes.** On a target with a heap a class, an interface, `object` or an array is a type argument like
a number (`src/ChipSharp.Frontend/Generics/ReferenceArguments.cs`): `Stack<Reading>` and `Stack<int>` are two instances
of `Stack<T>`, each a class of its own, with its own static fields and descriptor, which names it as .NET does, so a
cast that fails says ``Unable to cast object of type 'Box`1[Program+Lane]' to type 'Box`1[System.Int32]'``. A class
nested in a generic class is nested in its instance (`Holder<Lane>.Cell`), and generic classes derive from each other
(`Derived<T> : Base<T>`, `IntBox : Box<int>`) with their virtual methods. In an instance over a class, what the code
does through `T` is what code written for that class does (`Generics/Specialization.References.cs`): a call through a
constraint (`where T : Animal`, `where T : IShape`, `where T : class`) is a call on the object, virtual where it must
be; `default(T)` is null; `new T()` (`where T : new()`) runs the class's constructor, or a struct's own, or is the
default of any other value; `new T[n]` is an array of that class, which is an array of its base's too; `(T)o`, `o is T`
and `o as T` test against the class. A value is never null, nor ever a class: `value == null` and `value is Node` fold
to false in an instance over a value. On a target without a heap every use of an instance over a reference type needs
one, and `chipsharp check` says so.

One copy for each type argument, not one shared by every reference type, is the choice made here: the code of an
instance over a class depends on the class wherever it makes a `Node<T>` or a `T[]`, casts, tests or makes a `new T()`
(each needs the exact type's descriptor), and where it does not, LLVM inlines the instance's methods into their callers
in the whole program, so a shared copy would not be smaller. Measured on the Pico (`GenericClassCheckTests` compares the
same on every run): a `Box<T>` of six members used with three classes takes 7484 bytes of flash, 204 more than the same `Box<A>` used three times
(the descriptors and names of `Box<B>` and `Box<C>`, about 100 bytes each); a `Chain<T>` of nodes with loops called from
six places, over three classes, 316 bytes more than over one (four descriptors); and a single `Box` over `object`, which
is what sharing would give, 8356 bytes, 872 more than the three instances, as each caller casts what it gets back. The
linker's identical code folding (lld `--icf=all`) saves some 400 bytes on these programs, all of it in the runtime, none
in the instances, which are inlined or differ by their descriptors.

**Generic virtual methods** (`public abstract int Print<T>(T item)`, overridden, and an interface's generic method,
implemented implicitly or explicitly) have a slot for each instance the program calls (`Print<int>`, `Print<Reading>`),
which each class fills with its override for the same type arguments (`src/ChipSharp.Frontend/VirtualMethods.Generic.cs`):
only those are compiled. **Variance** of the program's interfaces (`interface IProducer<out T>`, `IConsumer<in T>`) and
the library's (`IEnumerable<out T>`, `IComparer<in T>`, ...) works in calls, casts and type tests: an object of a class
that implements `IProducer<Dog>` has an entry for `IProducer<Animal>` in its interface map, with the methods of the one
it implements (`VirtualMethods.Variance.cs`).

**The library's generic code over the program's types**, such as `MemoryExtensions.IndexOf<Point>`, is made in the
program, whose IL names the program's types (the library's own by their full names), so `span.IndexOf(point)` compiles,
and so does `Array.Sort` of a struct that implements `IComparable<T>`. A library method's constraints are its own (they
may say more than .NET's): an instance is made only for type arguments that meet them (`Generics/LibraryConstraints.cs`).

**The groundwork of collections**: `EqualityComparer<T>.Default` and `Comparer<T>.Default` are generic library
substitutes (`src/ChipSharp.Library/EqualityComparer.cs`, `Comparer.cs`, `Equality.cs`), objects that implement
`IEqualityComparer<T>` and `IComparer<T>`, with .NET's default comparers' results and no boxing: a number, a bool, an
enum and a type that implements `IEquatable<T>` (`IComparable<T>`) through it (NaN equals NaN), any other class through
its `Equals(object)` (`IComparable`), text as string compares it, null equal to null and before everything else. The
collections' interfaces (`IEnumerable<T>`, `IList<T>`, `IReadOnlyList<T>`, `IEqualityComparer<T>`, ...) are interfaces a
class of the program implements and is called through, and `foreach` over a collection of the program's uses its
`GetEnumerator()`: a struct or a sealed class without `Dispose`, as C# makes no `try`/`finally` for one. A `foreach`
over an enumerator that is disposed (every `IEnumerator<T>`), and an array used as an interface, arrive with the
collections; `chipsharp check` says what to write for now.

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
`ToString` and records arrive with M3's stored strings: `chipsharp check` says so for each place. Generic classes are
classes too (see Generics). On a target without a heap the classes themselves compile (their static members, their virtual
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

### Delegates

A delegate is an object on the heap (`src/ChipSharp.Frontend/Delegates.cs`), of its delegate type, whose descriptor's
base is System.MulticastDelegate's, then System.Delegate's, then object's; every delegate type lays its objects out the
same way, 16 bytes on the Pico (32 on a PC):

```
header | target (ref) | method (ptr) | invocations (ref)
```

- Made of an instance method (a lambda, which C# makes a method of the object it keeps what the lambda captures in, or
  of its one object of the lambdas that capture nothing; `counter.Next`), the target is the object and the method its
  function's address (`funcaddr` in TIR), which takes it as `this`. A virtual method's (`ldvirtftn`) is what its slot
  runs on that object: the one override there can be, or the address read from the object's tables.
- Made of a static method, the target is null and the method that method's thunk (`<delegate> Program.Add(int32, int32)`),
  which takes a target it leaves alone and calls the method, or the runtime function of a library method
  (`Console.WriteLine`). An extension method made into a delegate of its object (`node.Twice`) is called itself, with
  the object as its first argument.
- A multicast delegate, what `+` and `+=` make of two or more, has as its target and its invocations one
  `System.Delegate[]` of the single delegates it calls (never another multicast one), and as its method the invoker of
  its type's signature (`ChipSharp.Delegate.InvokeAll`), which its type's descriptor holds after object's slots: it
  calls each delegate of the list in order and returns what the last one returns.

So every invocation, `d(x)`, `d.Invoke(x)` and `d?.Invoke(x)`, is the same four instructions: the null check (.NET's
`NullReferenceException` message), the loads of the target and of the method, and one `call indirect method(target, x)`.
The collector follows the target and the invocations; the method is an address in the code.

`Combine`, `Remove`, `==`, `Equals`, `GetHashCode` and `GetInvocationList` are .NET's System.MulticastDelegate's
(`Lowering/DelegateFunctions.cs`): two delegates are equal when they are of one type and have the same target and
method, two multicast ones when their lists are equal one by one; `a + null` is `a` itself, `Remove` takes out the last
run of the list it is given and gives what is left (null, the one delegate left itself, or a new delegate of the rest);
two delegates of two types (`Action<object>` as an `Action<string>`) stop the program with .NET's `ArgumentException`.
A list is never shared or changed once made, so a list is always exactly as long as it needs to be. A field-like event's
`add` and `remove` (C#'s loop around `Interlocked.CompareExchange`) compare and store with interrupts off, so that an
interrupt handler that sets the field in between makes the loop go round again.

```csharp
public event Action<int> TooHot;             // add and remove: Combine and Remove in a CompareExchange loop
thermostat.TooHot += t => Console.WriteLine("too hot: " + t);
thermostat.TooHot += fan.Faster;             // an instance method: the fan is the target
TooHot?.Invoke(temperature);                 // both, in order
Array.Sort(readings, (a, b) => b - a);       // .NET's introsort with a comparison: ChipSharp.Library's Array
```

An interrupt handler may invoke a delegate (it makes no object), and the whole-program pass follows what the delegates
of that type may call into what the handler runs; making one there (a lambda C# caches in a static field too) and `+=`
make objects, which `chipsharp check` says is not allowed where it happens. On a target without a heap a delegate, and
the object C# makes for what lambdas capture, need a heap, which `chipsharp check` says in those words; a local function
that C# only calls keeps what it captures in a struct of its method, passed by `ref`, and compiles on every target. Not
yet: function pointers (`delegate*<int, int>`), delegates that take or return arrays or spans (the analysis of arrays
cannot follow an array into the method a delegate calls), and `BeginInvoke`. A delegate of a struct's method is made of
a box of the struct, as in .NET, whose value the method runs on (see Boxing, below). `samples/Delegates.cs` shows a
thermostat's callbacks and events.

### Boxing and nullables

A value used as an object is boxed (`src/ChipSharp.Frontend/Boxing.cs`, `Lowering/BoxingFunctions.cs`): `box T` makes an
object of the class `T (boxed)`, its header and then the value (on the Pico a boxed `int` takes 8 bytes of the heap, a
boxed `long` or `double` 16), one class per boxed type, named as .NET names it (`System.Int32`, `Program+Point`,
``Pair`1[System.Int32]``), whose base is `System.Object`. Like a class's, its descriptor has a virtual method table
and an interface map, for the slots the program calls: a struct's own `ToString`, `Equals`, `GetHashCode` and interface
methods, run on the value in the box (so `IDevice d = counter; d.Tick();` changes the box, not `counter`, as in
.NET), and for the rest .NET's own:

- a number's text as `Console.Write` writes it, a bool's `True`, an enum's name, a struct's type name;
- `Equals(object)` true only for a box of exactly the same type with an equal value (`((object)5).Equals(5L)` is false):
  numbers as their `Equals` compares them (NaN equals NaN, `-0.0` equals `0.0`), a struct as `ValueType.Equals` does,
  field by field (objects with their own `Equals`, through its slot, text by its chars, a nested struct with its own
  `Equals` given the other value boxed, as .NET does), which gives what .NET's bitwise or field-by-field comparison gives;
- `GetHashCode` of a number, a bool, a char or an enum as .NET's (an `int` its value, a `double` from its bits, NaNs and
  both zeros alike); of a struct, which .NET mixes with a number that changes from one run to the next, the fields' hash
  codes combined: the same for equal values, and in every run;
- `IComparable` of numbers, bools, chars and enums (`CompareTo(null)` is 1, another type stops the program with .NET's
  `ArgumentException`), `IComparable<T>` and `IEquatable<T>` of numbers.

Unboxing (`unbox.any`, `unbox`) gives the value of a box of the same type, an enum and its underlying type counting as
one (`(int)(object)Color.Green`, `(Color)(object)1`), and stops the program as .NET does otherwise: null with
NullReferenceException's message, any other object with `Unable to cast object of type 'System.UInt32' to type
'System.Int32'.` A type test wants exactly the type (`(object)Color.Green is int` is false). The methods of a value take
its address, as a struct's own do, so a constrained call (`point.ToString()`, `point.GetHashCode()` on a struct without
its own, generic code over values) runs them with no box, on a target without a heap too; so does printed text, which
writes a struct by its `ToString` and a nullable by its value. In generic code over values, a box whose test the type
argument decides is folded away as .NET's JIT folds it: `box T` with `isinst X` and a null test (`value is IComparable`,
the first half of `value is int n`) is true or false, `box T` with `unbox.any T` (the second half, after `isinst T` too) is
the value. A box that is kept or called through (`if (value is IDevice d) d.Tick();`) is a real box, as in .NET. On a target
without a heap each box is "boxing", or "converting a struct to an interface", which needs a heap: `chipsharp check` says
where, and to give the method a type parameter (`where T : struct, IDevice`) instead.

A `Nullable<T>` (`src/ChipSharp.Frontend/Nullables.cs`) is a struct of its own, `hasValue` and `value` as .NET lays them
out, so it needs no heap; ChipSharp compiles its members where C# calls them (C# makes `x + y`, `x == 5`, `x ?? 0` and
`x is int n` of `HasValue`, `GetValueOrDefault` and its constructor). Boxed it is null with no value and a box of its value
otherwise, and unboxing to `T?` takes null or exactly a box of `T`. `samples/Boxing.cs` shows each of these.

### Memory

The heap is where objects live: what classes, strings, boxes, delegates and collections need.

- An object starts with its header, the address of its type's descriptor, then its fields, those of its base classes
  first; an array has its length and then its elements. A descriptor (in flash on a chip) says the object's size,
  where its references are, its base, its name, and has room for the virtual methods and interfaces of its class
  (`src/ChipSharp.Ir/TirTypeDescriptor.cs`). In TIR an object is a `ref`, which the garbage collector follows, and an
  address a `ptr`, which it never follows.
- `new` takes zeroed memory from the heap; when there is none left, the garbage collector runs (mark and sweep, in
  `src/ChipSharp.Runtime.Shared/chipsharp_heap.c`): every object the static fields, the stack (the registers too) and
  other live objects reach stays, the rest is free again, each run of free blocks joined into one. Small objects are
  carved one after another from a free block, filling the holes small objects left; one of 256 bytes or more takes the
  smallest free block that holds it. When there is still not enough, the program stops with .NET's
  OutOfMemoryException message; a field of `null` stops it with NullReferenceException's.
- **Only a program that uses it has a heap.** A program that makes no object, no string and calls nothing of
  `System.GC` (the whole program: its library substitutes and the runtime functions it calls too) is built as for the
  target without a heap: on a chip no heap region (the stack may go down to the variables) and none of the collector's
  code, on the PC no memory taken from Windows. Its report says so: `samples/TrafficLights.cs` takes `RAM 16 bytes of
  264 KB` on the Pico, where it took `RAM 20 bytes + heap 224 KB of 264 KB`. A program that makes objects has the
  target's `"heap"` bytes of RAM between the variables and the stack (the Pico's 224 KB), or, on the PC, its 256 MB,
  taken from Windows at the start (its pages only when touched).
- **Interrupts wait for no collection.** The collector runs with interrupts on, so a handler runs when its interrupt
  comes, also in the middle of a collection: on the simulated Pico a timer interrupt every 200 µs, while a heap with
  188 KB of its 224 KB alive is collected again and again, came at most 201 µs after the one before (19.9 ms before,
  when the collector kept interrupts off). That is sound because a handler changes nothing the collector reads: the
  marks are in a bitmap of their own, so every object a handler reads (its type, its virtual methods, a string's
  chars) is as it was; only garbage, which no handler reaches, becomes free blocks; and two rules of `[Interrupt]`
  methods, and of everything they run (the methods they call, virtual calls, the delegates they invoke, library
  substitutes), which `chipsharp check` says where they are broken: a handler makes no object (`new`, a box, text; the
  runtime would stop the program there, saying so), and it stores no object: no reference but `null` in a static
  field, an object, an array or through a `ref`, as one moved from where the collector has not looked yet to where it
  has looked already would be freed while in use (`src/ChipSharp.Frontend/HandlerStores.cs`). Reading objects,
  storing numbers and bools in them and keeping references in its own locals are fine:

  ```
  Program.Tick() at IL_0009 (stsfld): an interrupt handler cannot store an object (a reference other than null) in a static field, an object or an array. The garbage collector may be in the middle of a collection when the interrupt comes, and runs on after it: an object moved from where it has not looked yet to where it has looked already would be freed while still in use. Read objects in the handler, store numbers and bools (in volatile static fields, or in the objects' fields), set references to null; let Main store the objects.
  ```
- **Stack overflow.** A recursion with no end stops the program as .NET does, with `Stack overflow.` on a line of its
  own. On the PC, Windows' guard page ends the stack, and the runtime's vectored exception handler, which
  `SetThreadStackGuarantee` leaves a stack for, writes it on standard error and exits with .NET's exit code
  (0xC00000FD); .NET goes on to list the methods that were running, ChipSharp does not. A Cortex-M0+ cannot catch it
  by itself: its MPU could put a guard region below the stack, but the HardFault that raises needs the very stack that
  ran out to enter its handler, so the core locks up instead, saying nothing. So on a chip (`"stackCheck": true` in
  `armv6m.json`) the functions through which the stack can grow without end check it when they start: every function
  in a cycle of the program's calls (through other methods, virtual and interface calls and delegates), and every one
  whose slots take 256 bytes or more, compares the stack pointer with `__chipsharp_stack_limit`, 1 KB above the
  stack's bottom (the linker script's; room for what runs below the last check), in four instructions and a literal
  (`src/ChipSharp.Backend.Llvm/LlvmStackChecks.cs`). Below it the program goes on a stack of its own, with interrupts
  off, and says `Stack overflow.` over UART0 before it stops at a breakpoint, like the other failures. The stack's
  bottom is the start of its region (`"link.stack"`, 8 KB on the Pico) above a heap, or, without one, the end of the
  variables. The functions that cannot recurse check nothing: they go only as deep as the program's code does.
- **Garbage the conservative scan could keep.** The stack and the registers are read conservatively, so a word that
  still holds a reference the program no longer uses would keep its object, and what it reaches, alive. Before a
  collection the collector zeroes the stack its own frames are about to take (what earlier, deeper calls left there),
  the code generator starts every slot of a function at zero, and right before `GC.Collect()` and
  `GC.GetTotalMemory(true)` it clears the callee-saved registers (`LlvmCollectCalls.cs`; LLVM first puts what it still
  needs on the stack). A method that builds a list of 2000 objects and returns its sum, then `GC.Collect()`: before,
  the whole list (172 KB on the Pico, 203 KB on the PC) was still in use after the collection, held by a register of
  the inlined method; now nothing is.
- **Fragmentation.** The collector never moves an object, so the free memory can be in pieces. A workload that keeps
  slots of text (10 to 200 chars), arrays (4 to 256 ints), arrays grown by `Array.Resize` and chains of small objects,
  each replaced in a pseudo-random order (`tests/ChipSharp.Tests/Memory/FragmentationTests.cs`), runs in a 32 KB heap
  with half of it alive, on the PC and the Pico, printing what .NET prints. Measured on the PC with more and more
  slots, it first runs out of memory with some 60% of the heap alive (56% measured, every 100 steps, at the last size
  that ran; taking big objects first-fit too runs out at the same size).
- **System.GC**: `GC.Collect()` collects now; `GC.GetTotalMemory(bool)` is the bytes of the objects not known to be
  garbage, each object's block (its size rounded up to 8), after a collection when asked (every collection is a full
  one, so one is enough); `GC.KeepAlive(object)` is a call the object is passed to, so the program holds it until
  there. .NET's numbers are those of its own heap; on a target without a heap these need one, as `chipsharp check`
  says.
- **`chipsharp build --verify-heap`** checks the whole heap before and after every collection: that its blocks cover
  it exactly, that every header is a type descriptor and every reference in an object null, outside the heap or the
  start of an object, that no mark is left and the bytes in use add up, and that each free list holds free blocks of
  its size. A corrupted heap stops the program with what is wrong and where, the way .NET says a fatal error (exit code
  0x80131506 on the PC): `Fatal error. The heap is corrupted: a reference into the middle of a block at 0x20008a44
  (found before collection 1).` The GC tests run with it on; a collection then takes some 2 to 3 times as long.

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
(`Pin.Unwatch` stops). A static field that a handler and the rest of the program share must be `volatile`, a handler
cannot wait (`Timing.Delay...`), and it makes no object and stores none (see Memory): `chipsharp check` says where. The target file says which interrupt each event uses
(`"interrupts"` in `rp2040.json`); a PC has none, so such a program does not build for `host`. The simulator changes an
input pin at a set time with `--input 15=high@100ms`.

### What a target allows, and what a program takes

A target file's `"capabilities"` say what a program may use on it: `"heap"`, the most RAM the heap may take (objects,
text kept while the program runs, collections: M3), or `false` for none (printed text needs none); and `"console"`, whether `Console.Write` has
somewhere to go (and `"unalignedAccess"`, whether a word may be read at any address: see Spans). The PC has a 256 MB heap and its console; the Pico a 224 KB heap (for a program that uses one, see Memory; 264 KB of RAM less 8 KB of stack and
32 KB for static fields) and UART0; a new chip inherits `armv6m`'s: neither, until its file says so.
`chipsharp check -t <target>` and `build` judge the program for that chip, so on one without a heap an object is out
of reach (a class whose objects are never made still compiles):

```
creating objects (new ...) needs a heap, and the target 'tiny' (2 KB RAM) has no heap: 1 place in Program.Main() (IL_0000). Use structs and static methods.
Console.Write and Console.WriteLine need a console, and the target 'tiny' has none: 1 place in Program.Main() (IL_000a). Show what the program does another way, e.g. on a pin with ChipSharp.Hardware.Pin.
```

`chipsharp build` ends with what the program takes of the chip, read from the linked `.elf`: flash for its code,
constants and the starting values of its variables, RAM for its variables, with the heap (`"heap"`) after them when the
program uses one and the stack (`"link.stack"`) kept at the end of the RAM. For `examples/pico-blink`, which makes no
object (`samples/Classes.cs`, which makes objects, `RAM 188 bytes + heap 224 KB of 264 KB`):

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
| M3 | Classes, heap, garbage collector, strings, interfaces, generics, `Span<T>` (done, with no heap): the Full tier |
| M4 | ESP32-C5 (RISC-V) |
| M5 | CH32V003: C# on a 10-cent chip |
| M6 | C code generator for 8-bit chips |
| M7 | Interpreter mode |
