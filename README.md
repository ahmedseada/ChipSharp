# ChipSharp

C# for microcontrollers. You write an ordinary C# program and build it with `dotnet build` as usual; ChipSharp then
compiles it ahead of time, through LLVM, into native code: a small `.exe` for your PC, or a `.uf2` file for a
Raspberry Pi Pico. On the chip there is no operating system, no .NET runtime and no interpreter: only your program, a
small runtime written in C, and, when the program needs one, a heap with a garbage collector.

```
C# --Roslyn--> IL --ChipSharp--> TIR --LLVM--> .exe (PC)   or   .elf --> .uf2 (Pico)
```

Where it stands: milestones M0 to M3 are done. Numbers, structs, arrays, spans, generics, text, interrupts, `Math`,
`TimeSpan` and `DateTime` (with its text and a clock) work on every chip, even one without a heap; on a chip with a heap (the Pico, the PC) so do classes,
records, strings, interfaces, delegates and lambdas, boxing, exceptions you throw (not yet catch) and the collections
(`List<T>`, `Dictionary<TKey, TValue>`, ...). Every sample prints exactly what .NET prints, on the PC and on a simulated
Pico, and the tests compare programs, and thousands of library calls, with .NET's own results.

**Contents:** [Quick start](#quick-start) · [Targets](#targets-what-a-chip-has) ·
[What C# compiles today](#what-c-compiles-today) · [Known differences and gaps](#known-differences-from-net-and-gaps) ·
[Memory](#memory) · [Interrupts](#interrupts) · [Date and time](#date-and-time) · [Hardware](#hardware) ·
[The chipsharp command](#the-chipsharp-command) ·
[Samples and examples](#samples-and-examples) · [Tests](#tests) · [How it works](#how-it-works) · [Roadmap](#roadmap)

## Quick start

### What you need

| Tool | What for |
|---|---|
| [.NET 10 SDK](https://dot.net) | Building ChipSharp and your programs |
| [LLVM](https://github.com/llvm/llvm-project/releases) (clang and ld.lld; `winget install LLVM.LLVM`) | Turning the program into native code. ChipSharp finds it in `C:\Program Files\LLVM\bin` or on the PATH, or set `CHIPSHARP_LLVM_BIN` to its `bin` folder |
| [Node.js](https://nodejs.org) 18 or later | Only for the Pico simulator (`tools/rp2040-sim`), once: `cd tools\rp2040-sim; npm ci` |
| [Zig](https://ziglang.org/download/) 0.17 or later | Only for the Arduino Uno ([in progress](#arduino-uno-in-progress)) |
| avrdude (it comes with the [Arduino IDE](https://www.arduino.cc/en/software)) | Only for flashing a real Uno (`chipsharp flash -t uno`, [below](#on-an-arduino-uno)) |
| A Raspberry Pi Pico or an Arduino Uno | Optional: the simulators and [Wokwi](https://wokwi.com) run the same `.uf2` and `.hex` |

ChipSharp is developed and tested on Windows; the PC target (`host`) makes Windows x64 programs.

### Build ChipSharp

```powershell
git clone https://github.com/ahmedseada/ChipSharp
cd ChipSharp
dotnet build -c Release
```

Then install the command as a .NET tool, so `chipsharp` works in any folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\install-tool.ps1
chipsharp targets list
```

The script packs `src\ChipSharp.Cli` (`artifacts\nupkg\ChipSharp.<version>.nupkg`, with the target files and the
library inside) and installs it with `dotnet tool install --global`; run it again after changing ChipSharp to replace
the installed one (`dotnet tool uninstall --global ChipSharp` removes it). A new terminal may be needed the first time,
for the .NET tools folder to be on the PATH. Without installing, the command is
`src\ChipSharp.Cli\bin\Release\net10.0\chipsharp.exe`.

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
  an input pin from outside (a button pressed at 2.5 s), `--uart0 file.bin` saves what UART0 sent; `--send` and `--adc`
  type into a serial port and turn a knob (see [Hardware](#serial-ports-analog-inputs-and-pwm-in-the-simulator)).
- **In Wokwi** ([wokwi.com](https://wokwi.com), in the browser): start a new Raspberry Pi Pico project, replace its
  `diagram.json` with the example's (the traffic lights have one: the Pico, LEDs, displays, the button, and UART0 on the
  serial monitor), press F1 and pick "Upload Firmware and Start Simulation...", then choose the `.uf2`.
- **On a real Pico**: hold the BOOTSEL button while plugging it into USB, and copy the `.uf2` onto the `RPI-RP2` drive
  that appears; the Pico restarts and runs it. `chipsharp flash bin\Release\net10.0\PicoBlink.dll -t pico` does both
  steps: it builds the program, then copies the `.uf2` to the drive named `RPI-RP2` (or the one `--port E:\` gives). To
  see `Console` output, connect a 3.3 V USB-to-serial adapter (its RX to GPIO 0, GND to GND) at 115200 baud: the Pico's
  own USB port does not carry it.

### On an Arduino Uno

The Uno ([in progress](#arduino-uno-in-progress)) needs Zig, and its programs are `.hex` files. The examples
`examples\uno-blink`, `uno-timer-blink` and `uno-button` are built as the Pico's are:

```powershell
cd examples\uno-blink
dotnet build -c Release
chipsharp build bin\Release\net10.0\UnoBlink.dll -t uno
```

```
...\chipsharp-output\uno\UnoBlink.hex
flash 1.2 KB of 31.5 KB (3.7%), RAM 15 bytes of 2 KB (stack 512 bytes reserved)
```

- **In the simulator** (avr8js, what Wokwi runs; once: `cd tools\avr-sim; npm ci`):
  `node tools\avr-sim\run.mjs chipsharp-output\uno\UnoBlink.hex --ms 3000` prints each pin change by its Uno name
  (`D13 high`); `--input D2=low@500ms` presses a button between D2 and GND at 0.5 s.
- **In Wokwi** ([wokwi.com](https://wokwi.com)): start a new Arduino Uno project, replace its `diagram.json` with the
  example's (the Uno with an LED on D13 and GND, or a button between D2 and GND, wired directly), press F1 in the code
  editor, pick "Upload Firmware and Start Simulation...", and choose the `.hex`.
- **On a real Uno**: plug it in by USB and run `chipsharp flash`, with the `.dll` (it builds the program first) or the
  `.hex`. It uploads the program through the Uno's USB port to its bootloader with avrdude, as the Arduino IDE does:

  ```
  > chipsharp flash bin\Release\net10.0\UnoBlink.dll -t uno --port COM5
  ...\chipsharp-output\uno\UnoBlink.hex
  flash 1.2 KB of 31.5 KB (3.7%), RAM 15 bytes of 2 KB (stack 512 bytes reserved)
  COM5: avrdude -c arduino -p atmega328p -P COM5 -b 115200 -D -U flash:w:...\chipsharp-output\uno\UnoBlink.hex:i
  COM5: written and verified; the uno runs it
  ```

  Without `--port` it takes the one serial port that looks like an Arduino's (Arduino's USB ids, or a clone's CH340,
  FT232R or CP210x chip), and otherwise lists the ports there are; Windows' Device Manager shows the Uno's under "Ports
  (COM & LPT)". avrdude comes with the [Arduino IDE](https://www.arduino.cc/en/software), whose copy ChipSharp finds
  (`%LOCALAPPDATA%\Arduino15\packages\arduino\tools\avrdude`), or [on its own](https://github.com/avrdudes/avrdude/releases):
  put its folder on the PATH, or set `CHIPSHARP_AVRDUDE` to `avrdude.exe`. Close the serial monitor first: avrdude needs
  the port.

## Targets: what a chip has

A target file says what a chip or board is and has. Files inherit from each other, board → chip → architecture, so a
new board is a few lines of JSON:

| Target | What it is |
|---|---|
| `host` | Your PC: a Windows x64 console program, with no C runtime (inherits `x86_64`) |
| `pico` | Raspberry Pi Pico: RP2040, 2 MB flash, LED on GPIO 25 (inherits `rp2040`, which inherits `armv6m`) |
| `pico-w` | Pico W: like the Pico, but its LED is wired to the Wi-Fi chip |
| `uno` | Arduino Uno: ATmega328P at 16 MHz, 32 KB flash, 2 KB RAM ([in progress](#arduino-uno-in-progress); inherits `atmega328p`, which inherits `avr`) |
| `rp2040`, `atmega328p`, `armv6m`, `avr`, `x86_64` | Chips and architectures, for target files to inherit; not built for directly |

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
  capabilities hwMultiply=true, hwDivide=false, fpu=false, atomics=none, unalignedAccess=false, stackCheck=true, heap=224K, console=true, clock=true, romFloat=true, consoleInput=true, uart=2, adc=4, pwm=16, i2c=true, spi=true
  pins         LED=25
  interrupts   timer=IRQ 1, pin=IRQ 13, uart=IRQ 26
```

The `"capabilities"` decide what a program may use on the chip:

| Capability | Meaning | `pico` | `host` | `armv6m` (a new chip, until its file says) |
|---|---|---|---|---|
| `heap` | The most RAM the heap may take, or `false` for none. Objects, kept strings, boxes, delegates and collections need one (see [Memory](#memory)) | 224 KB | 256 MB | none |
| `console` | Whether `Console.Write` has somewhere to go | UART0 | standard output | none |
| `consoleInput` | Whether `Console.Read` and `Console.ReadLine` have something to read | UART0 | not yet | none |
| `clock` | Whether a timer counts time for `DateTime.Now`, `Stopwatch`, `Environment.TickCount` and `Clock.Start` (see [Date and time](#date-and-time)) | the timer's microseconds | Windows' clock | none |
| `uart`, `adc`, `pwm` | How many serial ports, analog inputs and PWM outputs `ChipSharp.Hardware` can use (see [Hardware](#hardware)); `"pinFunctions"` lists their pins | 2, 4, 16 | none | none |
| `unalignedAccess` | Whether a word may be read at any address; without it `MemoryMarshal.Read` reads byte by byte and `MemoryMarshal.Cast` checks alignment | no | yes | no |
| `stackCheck` | The functions that can recurse check the stack, so a stack overflow says `Stack overflow.` instead of locking the core up | yes | (Windows' guard page) | yes |
| `narrowing` | Arithmetic in 8 or 16 bits where the values provably fit and the result is C#'s (see [Narrowing](#narrowing)); when not given, on for a chip with 2-byte pointers (the AVR) only | no | no | no |
| `i2c`, `spi` | Whether `I2c` and `Spi` have buses (see [Hardware](#hardware)); the file's `"i2c"` and `"spi"` give each bus's pins and clocks | 2 each | none | none |
| `hwMultiply`, `hwDivide`, `fpu`, `atomics`, `romFloat` | Describe the chip; the code itself follows the file's `triple`, `cpu` and `clang` flags (soft float on `armv6m`) | | | |

The file also gives the memory regions, the link layout (`"link.stack"`: 8 KB on the Pico) and the interrupts each
event uses (`"interrupts"` in `rp2040.json`). `chipsharp check -t <target>` and `chipsharp build -t <target>` judge the
program for that chip: on one without a heap or without a console, they name each place that needs it (see
[below](#when-chipsharp-check-says-no)). A new chip of a known architecture is a JSON file plus its runtime; a CPU no
code generator supports yet (the ESP32-C5's RISC-V, next) also needs a runtime for its architecture.

### Arduino Uno (in progress)

Milestone M7 adds the Uno's ATmega328P, an 8-bit AVR: `chipsharp build -t uno` builds a `.hex`. The runtime is two, as
the Pico's: `avr` for any AVR chip (start-up, the vector table, 8-bit math helpers, float in software, the failure
stop) and `atmega328p` for the chip at 16 MHz (pins, the clock, the events, USART0, where Console.Write and WriteLine
go).

- **Pins** go by the Uno's (Arduino's) numbers: 0-13 are D0-D13, 14-19 are A0-A5; the LED "L" is 13. `Pin.Input` is
  high impedance, as on the Pico, without the AVR's pull-up: a button between a pin and GND (the usual Arduino wiring,
  `INPUT_PULLUP`) needs the pull-up turned on after `Pin.Input`, through `Registers.Write` of PORTB, PORTC or PORTD
  (0x25, 0x28, 0x2B: one byte; `Registers` reads and writes bytes on the Uno), or a resistor to 5 V; a button to 5 V
  needs one to GND. D0 and D1 are the console's while it runs.
- **Interrupts**: `Pin.Watch...` on D2 and D3 uses INT0 and INT1, whose edges the chip keeps; on every other pin the
  pin change interrupt of its port, whose handler compares the levels and runs the `[Interrupt(Event.Pin)]` method for
  the edges watched (a pulse shorter than a few microseconds may pass unseen). Each event's interrupt comes through the
  runtime, which checks that the event happened and then calls the method, within some 50 microseconds
  (`"interrupts"` in `atmega328p.json`: numbers past the chip's vectors, as the Pico's uart event).
- **Timers**: Timer1 counts half microseconds (16 MHz / 8) for the clock, the delays and the timer event (its compare
  B), so the three keep the same time: `Timing.EveryMilliseconds(40)` comes every 40 ms to the microsecond, and a delay
  takes what it is given and a few microseconds more for the call. Timer0 and Timer2 are left free.
- **The clock**: Timer1 and the count of its rounds make 48 bits of half microseconds since start-up (they go round
  after 4.4 years): `Stopwatch` (1 MHz, as on the Pico), `Environment.TickCount` and `DateTime.Now` (from 1 January 2000,
  or the date `Clock.Start` gives) read it.
- **The console** is USART0 on D1 (TX) and D0 (RX) at 115200 baud: 117647 really, 2.1% fast, with U2X as the Arduino
  core's `Serial.begin(115200)` has it, which the Arduino IDE's serial monitor and the USB chip take. A byte goes out
  when the one before has (no send buffer, no RAM taken); `Console.Read` and `Uart.Open(0, 1, 0, baud)` (489 to 2000000
  baud) receive into a buffer of 64 bytes, filled by the receive interrupt, as the Arduino core's. `Console.ReadLine`
  makes a string, so it needs a heap, which the Uno has none of. A failure prints .NET's message there, then stops.
- **What it takes**: a blink (`Pin.Output`, `Pin.Toggle`, `Timing.DelayMilliseconds`) is 1.2 KB of flash and 15 bytes
  of RAM; one that writes a few chars to the console 1.4 KB and 30 bytes; a serial echo (`Uart.Open`, `Console.Read`,
  `Uart.WriteByte`) 2.9 KB and 90 bytes, the receive buffer's 64 among them. `chipsharp check -t uno` refuses what the
  Uno has not yet (analog inputs, PWM, I2C, SPI) and a second serial port.

- **Zig**: LLVM's release builds for Windows have no AVR, so the target's toolchain `zig` runs the clang and ld.lld
  inside [Zig](https://ziglang.org/download/) (0.17 or later: unpack the zip anywhere). ChipSharp finds it through
  `CHIPSHARP_ZIG` (`zig.exe` or its folder), then `D:\Tools\zig\zig.exe`, then the PATH. The tests that need it are
  skipped, saying why, without it.
- **The code**: the `llvm` code generator writes the AVR's data layout (2-byte pointers, nothing aligned; C#'s `int`
  stays 32 bits, `long` and `double` 64), and keeps every constant in flash, none of it copied to the 2 KB of RAM: the
  literals, tables, spans of constant data, enum and date names. The AVR reads flash only with its own instruction
  (LPM), so an address of flash data has bit 15 set (0x8000 plus its flash address: RAM's addresses never have it); a
  read whose address the compiler cannot place tests the bit, the others read flash or RAM straight away. The runtime
  reads what the program hands it so too. The capability `"separateFlash": false` keeps constants as ordinary data
  instead, which start-up copies to RAM, as avr-gcc does without `PROGMEM`. Two things LLVM 22 gets wrong on an AVR
  are kept from it: a copy of LLVM's own from flash (a loop over a table made into `memcpy`, which would read RAM), so
  `memcpy` and `memmove` are no builtins in the program; and a call that passes arguments on the stack from a function
  without a frame pointer, which sets the stack up through r31:r30 while it may hold a value, so such a function gets
  one (a byte of stack).
- **What runs**: on the simulated Uno, every sample and every program of the test corpora that makes no object prints
  exactly what .NET prints (numbers, floats and their text, structs, enums, `ref`, arrays on the stack and in static
  fields, constant tables, spans, generics over values, tuples, record structs, nullables, fixed and printed text,
  `TimeSpan`, `DateTime`, `Math`), and where .NET throws it says .NET's message. Those that make objects are refused,
  place by place, as the Uno has no heap; those larger than its 31.5 KB of flash by the memory report: doubles are in
  software, and compiler-rt's add, multiply and divide take 3 to 4 KB each on an AVR, so `MathFunctions`, a program
  of every member of `DateTime` and `Math.Sin`, `Cos`, `Tan`, `Atan2`, `Asinh`, `Acosh`, `Log`, `Log2` and `Pow` with
  the text of doubles do not fit (each gives the PC's bits all the same, on an AVR core of 64 KB). The runtime's C
  takes C#'s `int`, `char` and `bool` as 32 bits (C's `int` has 16 on an AVR) and keeps its own text and tables in
  flash, `Math`'s tables (22 KB) too. Flash and RAM: `Square` 1.3 KB and 6 bytes, `Structs` 2.0 KB and 18 bytes,
  `Spans` 4.8 KB, `TrafficLights` 7.8 KB and 52 bytes, `PrintedText` 14.6 KB, `FloatMath` 16.7 KB, `TimeSpans` 21.6 KB.
- **8-bit arithmetic**: an `int` takes four of the AVR's registers and four instructions for every addition, and C#
  makes nearly everything an `int`, loop counters and bytes on IL's stack included; so on the AVR ChipSharp does the
  arithmetic in 8 or 16 bits wherever it proves the values fit and the result is C#'s ([narrowing](#narrowing)). The
  samples' code takes 5% less flash, loops of counters and small numbers up to half less (ForLoops: 310 bytes instead
  of 588).
- **The `.hex`** is Intel HEX, what `llvm-objcopy -O ihex` writes: the flash's contents, the starting values of the
  variables included. The memory report reads like `flash 417 bytes of 32 KB (1.3%), RAM 12 bytes of 2 KB (stack 512
  bytes reserved)`. Uploading through the Uno's USB port (avrdude, the Arduino IDE) goes through its bootloader, which
  keeps the last 512 bytes of the flash.
- **Flashing**: `chipsharp flash -t uno` uses the target's flasher, `avrdude` (`uno.json`: `"flasher"`, and
  `"avrdude"`: the programmer `arduino`, optiboot's STK500 protocol, at 115200 baud; the chip is the `"mmcu"`), with
  `-D -U flash:w:<file>.hex:i`; a board with another bootloader sets its own `"avrdude"` (an old Nano's: `"baud":
  57600`). When avrdude fails, the error says what is likely wrong (the port would not open, the bootloader did not
  answer, the chip is another) and shows avrdude's last lines. See [On an Arduino Uno](#on-an-arduino-uno).
- **In the simulator** (avr8js, what Wokwi runs; once: `cd tools\avr-sim; npm ci`):
  `node tools\avr-sim\run.mjs Blink.hex --ms 1000` prints every pin change by its Uno name (`[   20.822 ms] D13 high`)
  and every line written to USART0 (`USART0: Hello`); `--input D2=high@100ms` drives a pin from outside, `--send
  "text\n@50ms"` types into USART0, `--usart0 file.bin` saves what it sent. A run stops when the program sleeps with
  interrupts off (it has finished) or reaches a `break` (a fatal error, exit code 3).
- **In Wokwi** ([wokwi.com](https://wokwi.com)): start a new Arduino Uno project, press F1 in the code editor, pick
  "Upload Firmware and Start Simulation...", and choose the `.hex`. The examples' `diagram.json` name the Uno's pins as
  Wokwi's `wokwi-arduino-uno` does: `uno:13`, `uno:2`, `uno:GND.1` (the GND beside 13; `GND.2` and `GND.3` are by
  `5V`), `uno:A0`.

## What C# compiles today

The rule of thumb: what is a **value** (numbers, structs, enums, spans, fixed text, arrays that stay in their method)
works on every chip, with no heap; what **makes objects while the program runs** (classes, strings that are kept,
boxes, delegates, collections, exceptions) needs a heap, which the Pico and the PC have. A program that makes no object
gets no heap at all, even on the Pico. `chipsharp check` tells you for your program, place by place.

### On every chip, with no heap

| C# | What works |
|---|---|
| Program structure | Static methods, top-level statements, local functions (also ones that capture variables: those are kept in a struct), recursion; static fields (`volatile` too) with field initializers and static constructors; a program split into class libraries of its own (project references, which `dotnet build` puts next to the program's `.dll`, and theirs): ChipSharp reads them with it and compiles their C# as the program's, only what it calls, by the same rules |
| Values | `bool`, `char` (with `char.IsDigit`, `IsLetter`, `IsWhiteSpace`, `ToUpper`, ...), every whole number (`sbyte` to `ulong`, `nint`, `nuint`), `float` and `double`, computed and printed bit for bit like .NET (also on the Pico, which does float math in software); arithmetic wraps around like unchecked C# |
| Code | `if`, `switch` (patterns, and on strings), `for`, `while`, `do`, `foreach` over arrays and spans, `&&`, `\|\|`, `?:`, `goto`; `try`/`finally` (with `return`, `break` and `continue` out of it), `using` of a struct |
| Structs and enums | Fields, constructors, methods, properties and operators of structs; `readonly struct`; enums (`[Flags]` too); `ref`, `out` and `in` parameters and `ref` locals; primary constructors; `record struct` and `readonly record struct` (`==`, `with`, `Deconstruct`, `Equals`, `GetHashCode`; only their `ToString` needs a heap) |
| Nullables | `int?`, `bool?`, nullable enums and structs: `HasValue`, `Value`, `GetValueOrDefault`, `??`, lifted arithmetic and comparisons, `is int n` |
| Tuples | `(1, 2.5)`, `(int Min, int Max)`: `System.ValueTuple` of any number of elements, a struct of its elements as .NET lays it out. Named elements and `Item1`..., deconstruction into new and existing variables (`var (min, max) = Range(a);`, `(a, b) = (b, a)`, nested, with `_`), tuples as locals, parameters (`ref` too), return values, fields, static fields and array elements; `==` and `!=`, `Equals`, `CompareTo` and `GetHashCode` (element by element, as the default comparers compare them), `switch` on a tuple (`(light, waiting) switch { (Light.Red, > 3) => ... }`), `ValueTuple.Create`. Only `ToString()` (`(1, a)`) needs a heap |
| Generics over values | Generic methods, structs and static classes over numbers, bools, enums and structs, with constraints; interfaces implemented by structs and called through a constraint (`static void Run<T>(ref T device) where T : struct, IDevice`, static abstract members too); generic math (`INumber<T>`: operators, `T.Zero`, `T.One`, `MinValue`, `MaxValue`). Each instance is compiled for its type, like a C++ template, so `Max<int>` costs what a hand-written `int` version costs |
| Arrays | On the stack: a constant size, made in a method and passed down to the methods it calls. In static fields: `static readonly byte[] Digits = { 0x3F, ... };` is a constant table in flash when nothing writes it; `static int[] buffer = new int[16];` a fixed array in RAM. `foreach`, `Length`, bounds checks |
| Spans | `Span<T>` and `ReadOnlySpan<T>` of arrays, of `stackalloc` (a constant size), of constant data (`static ReadOnlySpan<byte> Table => new byte[] { ... };`, in flash) and of variables; indexer, `Slice`, ranges and indices (`s[2..5]`, `s[^1]`), `CopyTo`, `Fill`, `Clear`; `MemoryExtensions` (`IndexOf`, `SequenceEqual`, `StartsWith`, ...), `BinaryPrimitives` and `MemoryMarshal` (`Read`, `Write`, `Cast`, `AsBytes`), at any offset, also on a chip that faults on unaligned words |
| Fixed text | String literals anywhere a string may be: printed, compared, measured and searched (`Length`, `s[i]`, `==`, `IndexOf`, `StartsWith`, `switch`); each is a constant object in flash (8 bytes and 1 byte per char on the Pico) |
| Printed text | Text built and printed straight away, with no string ever made (also in the body of a `foreach` over a collection or a `using`, where C# makes it otherwise): `Console.WriteLine($"road : {lane}")`, `Console.Write("lane " + lane + '!')`, `x.ToString()` printed, `Console.WriteLine("{0,4} {1:X2}", a, b)`. Holes of numbers, bools, chars, text, enums (by name, `[Flags]` combined), structs (their `ToString`) and nullables; alignments (`{x,5}`, `{x,-5}`); the formats `D`, `D<n>`, `X<n>`, `x<n>` and `G` of whole numbers, `G` and `R` of floats; dates with every format of theirs (`$"{now:HH:mm:ss}"`, `now.ToString("d")`, `Console.WriteLine(now)`, see [Date and time](#date-and-time)) |
| Type objects | `typeof(X)`, `typeof(T)`: `Name`, `FullName`, `Namespace`, `ToString()` as .NET 10 writes them, `==`, `IsValueType`, `IsArray`, `IsEnum`, `IsClass`, `IsInterface`, `IsPrimitive`, `IsGenericType` (constant objects in flash) |
| Math | `Math` and `MathF`: trigonometry (`Sin`, `Cos`, `Tan`, `SinCos`, `Asin`, `Acos`, `Atan`, `Atan2`), hyperbolic functions and their inverses, `Exp`, `Log` (in any base), `Log2`, `Log10`, `Pow`, `Sqrt`, `Cbrt`; `Floor`, `Ceiling`, `Truncate`, `Round` (to digits, by every `MidpointRounding`), `Clamp`, `Sign`, `Abs`, `Min`, `Max`, `MinMagnitude`, `MaxMagnitude`, `CopySign`, `FusedMultiplyAdd`, `IEEERemainder`, `ScaleB`, `ILogB`, `BitIncrement`, `BitDecrement`, the reciprocal estimates, `E`, `PI`, `Tau`; for every whole-number type `Abs`, `Min`, `Max`, `Clamp`, `Sign`, `DivRem`, and `BigMul`; `double.Sin(x)` and the like, and generic math over float types (`T.Sqrt`, `T.Sin`, ...). The exact ones give .NET's bits; the transcendental ones the same bits on the PC and the Pico, and .NET's but for the last bit of a few results (see [differences](#where-a-program-behaves-differently)). A program links only the functions it calls (`Math.Sin`: 9 KB on the Pico) |
| Library | `Console.Write` and `WriteLine`; `TimeSpan` (factories in any unit, parts, totals, arithmetic, comparisons); `DateTime`, `DateOnly` and `TimeOnly` (parts, arithmetic, comparisons, `TryFormat` into a span), `DateTime.Now`, `Stopwatch.GetTimestamp` and `GetElapsedTime`, `Environment.TickCount` (see [Date and time](#date-and-time)); `System.Array`: `Copy`, `ConstrainedCopy`, `CopyTo`, `Clear`, `Fill`, `IndexOf`, `LastIndexOf`, `Reverse`, and `Sort` and `BinarySearch` of numbers, bools and structs that implement `IComparable<T>` |
| Hardware | `ChipSharp.Hardware`: `Pin` (`Output`, `Input`, `Write`, `Read`, `Toggle`, `Watch`, `WatchRising`, `WatchFalling`, `Unwatch`), `Timing` (`Delay(TimeSpan)`, `DelayMilliseconds`, `DelayMicroseconds`, `EveryMilliseconds`, `EveryMicroseconds`, `AfterMilliseconds`, `AfterMicroseconds`, `Stop`), `Registers.Read` and `Write`, `Clock.Start`, and `[Interrupt]` methods (see [Interrupts](#interrupts)); the serial ports and the console's input, the analog inputs, the PWM outputs and the I2C and SPI buses: `Uart` (and `Console.Read`), `Adc`, `Pwm`, `I2c` and `Spi`; and `ChipSharp.Components`, the parts connected to the chip: `Lcd`, `SevenSegment` and `Ds1307` (see [Hardware](#hardware)) |
| Failures | Where .NET throws (an index outside an array, a whole number divided by zero, `int.MinValue / -1`, a `TimeSpan` too long, a field of null, a cast that fails, ...), the program stops with the message .NET prints: on standard error with exit code 1 on the PC, over UART0 and then at a breakpoint on a chip |

### On a chip with a heap (the Pico and the PC)

| C# | What works |
|---|---|
| Classes | The program's classes, `sealed`, `abstract`, nested and derived from each other: `new`, fields (of objects too: lists, trees), field initializers, constructors (`this(...)`, `base(...)`), instance and static members, object initializers, `null`, `?.`, `??`, `??=`, `ReferenceEquals`; objects in locals, parameters, fields, arrays and static fields. The garbage collector frees what the program no longer reaches |
| Records | `record` / `record class` (positional, with members of their own, derived, generic, `sealed`, `abstract`): `init` properties, `required` members, `with`, `Deconstruct`, `Equals`, `==`, `GetHashCode` and `ToString` as C# makes them (`Point { X = 1, Y = 2 }`) |
| Virtual calls and interfaces | `virtual`, `override`, `abstract`, `sealed override`, `base.M()`, `new virtual`; interfaces (explicit implementations, default methods, properties, generic interfaces with `in`/`out` variance); `object`'s `Equals`, `GetHashCode` and `ToString`. A call only one method can answer is a plain call, and a method no object of the program can run is left out |
| Type tests and casts | `is`, `as`, `(T)x`, `x is Square s`, property patterns, `switch` on types, against classes, interfaces, `object` and arrays; a failed cast stops with .NET's `InvalidCastException` message; `x.GetType()` |
| Strings | Text built and kept (`string s = $"..."`, `+`, `+=`, `ToString()` with the formats printed text has, `string.Concat`, `string.Join` of an array, `string.Format` of a literal format), in fields, arrays and collections; `Substring`, `IndexOf`, `Replace`, `Trim`, `PadLeft`, `ToUpper`, `Split`, `Insert`, `Remove`, `ToCharArray`, `new string(...)`, `CompareOrdinal`, `CompareTo`, `IsNullOrEmpty`, `GetHashCode`, ...; `int.Parse` and `TryParse` (and those of the other whole numbers); spans of chars; the text of dates kept (`string s = now.ToString("D")`), and `Stopwatch` objects (`Stopwatch.StartNew()`, `Elapsed`, `Restart()`) |
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
| Dates as text | `ToString`, `$"{now}"` and `TryFormat` follow the PC's culture: its patterns, names of months and days, AM/PM and separators (`27.11.2026 18:05:09` in German) | Always as the invariant culture: `11/27/2026 18:05:09`, `Friday`, `November`, `PM` | A custom format with numbers and quoted separators (`yyyy'-'MM'-'dd HH':'mm`) gives the same text with every culture; `chipsharp build` notes each place |
| Time zones | `DateTime.Now` is UTC moved by the time zone's rules for that moment; the `z` and `K` formats (and `o` and `U`) of a local time take the offset at the date written | A chip has no time zones: `Now` is `UtcNow` (Kind Local), the offset `+00:00`. On the PC `Now` has the time zone's offset now, and `z`, `K`, `o` and `U` take the offset now for every date; once the program calls `Clock.Start`, as on a chip | Keep times in UTC; write `z` only of UTC times |
| Hash codes | A string's, a struct's (boxed or through `ValueType`), a record's, a tuple's and a `Type`'s `GetHashCode` differ from run to run | The same in every run (a string's is FNV-1a of its chars, a tuple's its elements' combined as `HashCode.Combine` combines them from a seed of 0); object's own is its address divided by 8 | Never store hash codes or count on their order (true for .NET too) |
| `StringBuilder.Capacity` | Grows by chunks, and drops or splits them on `Clear`, a shorter `Length` or an `Insert` | The same capacity for a builder that only appends; after those three it may differ. The text never does | Do not test `Capacity` |
| Memory numbers | `GC.GetTotalMemory` counts .NET's heap | ChipSharp's heap: each object's block, its size rounded up to 8 | |
| Console | Writes in the console's code page | Always UTF-8; a Windows console shows non-ASCII text right after `chcp 65001` | |
| The last bit of `Sin`, `Exp`, `Pow`, ... | Calls the C runtime: on Windows the UCRT, whose x64 functions are AMD's math library (some in C, some in assembly that uses the fused multiply-add of processors with FMA3), on Linux glibc; not always correctly rounded, so the last bit depends on the PC | One implementation for every target, the same bits on the PC and the Pico: .NET's result for 88 to 100 % of values (above 97 % for most functions), 1 ulp away for the rest (see [Math](#math)); `Cbrt`: 70 %, up to 2 ulps | Compare transcendental results with a tolerance (as with .NET on two operating systems), or round them for display |
| `MathF.ReciprocalEstimate`, `MathF.ReciprocalSqrtEstimate` | On x64, the processor's estimate instructions (`rcpss`, `rsqrtss`: about 12 bits, different on Intel and AMD) | `1f / x` and `1f / MathF.Sqrt(x)`, what .NET computes without them (and for doubles everywhere) | |
| `nint` and `nuint` | 64 bits on a 64-bit PC | The chip's address size: 32 bits on the Pico, 16 on the Uno, where -123456 does not fit in an `nint` | `int` or `long` for numbers |
| A struct's bytes (`MemoryMarshal.Read` and `Write`, `sizeof`) | Fields aligned to their size, with padding between: `struct { byte; ushort; int; }` takes 8 bytes | The same on the Pico; on the Uno nothing is aligned, so no padding: 7 bytes | Write and read the fields one by one with `BinaryPrimitives`, or order them from the largest down |

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
| `DateTime.Parse`, `TryParse` and `ParseExact` (and DateOnly's and TimeOnly's), `ToLocalTime`, `ToUniversalTime`, `DateTimeOffset`, `TimeZoneInfo`, the overloads of dates with a `Calendar` or an `IFormatProvider` (but `TryFormat`'s, which takes the invariant culture) | Read the numbers with `int.Parse` and `new DateTime(year, month, day, ...)`; keep times in UTC |
| Reflection beyond a type's names and `Is...` flags (`GetMethods`, attributes, `BaseType`, `Activator`) | A virtual method that says it, or a `switch` on the object's type |
| `typeof(List<>)` (an open generic type) | `typeof(List<int>)` |
| Boxing a library struct (`object o = TimeSpan.Zero;`, its `GetType()`; a date boxed for a format, `Console.WriteLine(now)`, is no box), `TimeSpan.ToString()` and `Parse` | Keep it in its own type; print `span.TotalMilliseconds` |
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
stops it), `Uart.Watch` the `[Interrupt(Event.Uart)]` method when bytes arrive on a serial port. The target file says which interrupt each event uses; a PC has none, so such a program builds for a chip
only. `chipsharp check` enforces the rules, naming the place that breaks one:

| Rule | Why |
|---|---|
| One `static void` method without parameters per event, and one for every event the program starts | The chip calls it with nothing to pass, and an event without a handler would stop the chip |
| A static field shared with `Main` is `volatile`, and an `int`, `uint`, `bool`, ... (not a `long`, a `double` or a struct) | Otherwise `Main` may keep a copy and never see the change, or see half of it |
| A handler does not wait (`Timing.Delay...`, `Console.Read`, `Console.ReadLine`) | The interrupt that ends the wait cannot come while a handler runs |
| A handler makes no object (`new`, a box, text, a lambda, `+=` on an event) | The heap may be in the middle of an allocation or a collection |
| A handler stores no object: no reference but `null` into a static field, an object, an array or through a `ref` | The collector may be half way through: an object moved to where it has already looked would be freed while in use. Read objects, store numbers and bools, keep references in locals |

The rules hold for everything a handler runs: the methods it calls, virtual calls and the delegates it invokes.
`examples/pico-traffic-light-m3` shows the usual shape: the handler sets a `volatile bool`, and `Main` does the rest.

## Date and time

`DateTime`, `DateOnly`, `TimeOnly`, `DayOfWeek` and `DateTimeKind` are .NET 10's own code, ported to
ChipSharp.Library: structs of 8 and 4 bytes, with no heap on any chip, and .NET's results bit for bit (its date
arithmetic, its text, its exceptions' messages). A clock that `DateTime.Now` reads, and a counter of uptime for
measuring, come from the chip's timer:

```csharp
Clock.Start(new DateTime(2026, 11, 27, 18, 0, 0));      // ChipSharp.Hardware: DateTime.Now counts from this date
Console.WriteLine($"{DateTime.Now:HH:mm:ss}");            // 18:00:00, printed straight away: no string made
Span<char> text = stackalloc char[16];
if (DateTime.Now.TryFormat(text, out int n, "ddd dd MMM")) Lcd.Line(0, text[..n]);   // "Fri 27 Nov" on an LCD
long start = Stopwatch.GetTimestamp();                    // the timer: microseconds on a chip
TimeSpan took = Stopwatch.GetElapsedTime(start);          // since then
```

**The types.** The constructors (to the microsecond, with a Kind; of a DateOnly and a TimeOnly), `Year`, `Month`,
`Day`, `Hour`, `Minute`, `Second`, `Millisecond`, `Microsecond`, `Nanosecond`, `DayOfWeek`, `DayOfYear`, `Date`,
`TimeOfDay`, `Ticks`, `Kind`; `Add`, `AddYears`, `AddMonths`, `AddDays` ... `AddMicroseconds`, `AddTicks`, `Subtract`,
`-` of two dates (a `TimeSpan`), `+` and `-` of a `TimeSpan`, `==`, `<`, ..., `Compare`, `CompareTo`, `Equals`,
`GetHashCode` (.NET's, the same in every run), `DaysInMonth`, `IsLeapYear`, `SpecifyKind`, `Deconstruct`, `MinValue`,
`MaxValue`, `UnixEpoch`; DateOnly's and TimeOnly's own (`FromDateTime`, `FromDayNumber`, `DayNumber`, `ToDateTime`,
`AddDays`, `Add(span, out wrappedDays)`, `IsBetween`, `-`, ...). 31 February, a date past 9999, a bad format: the
program stops with .NET's message, `Unhandled exception. System.ArgumentOutOfRangeException: Year, Month, and Day
parameters describe an un-representable DateTime.`, also on a chip without a heap.

**Text.** As .NET's invariant culture writes it (ChipSharp has no other: see [differences](#where-a-program-behaves-differently)),
every standard and custom format; three ways, of which only the last needs a heap:

| Way | Example | Heap |
|---|---|---|
| Printed straight away | `$"{now:HH:mm:ss}"`, `$"{today,12:d}"`, `Console.WriteLine(now.ToString("yyyy-MM-dd"))`, `Console.WriteLine(now)`, `Console.WriteLine("{0:t}", now)`, `"at " + now.ToShortTimeString()` | none: the text goes to the console 32 chars at a time |
| Into a span | `now.TryFormat(buffer, out int written, "HH:mm")`, `date.TryFormat(...)`: false (and 0) when it does not fit | none |
| Kept | `string s = now.ToString("D");`, `string t = $"{now:t}";` | a string |

| Format | 2026-11-27 18:05:09.123 (UTC) | Format | |
|---|---|---|---|
| (none), `G` | `11/27/2026 18:05:09` | `o` | `2026-11-27T18:05:09.1230000Z` |
| `d`, `D` | `11/27/2026`, `Friday, 27 November 2026` | `r` | `Fri, 27 Nov 2026 18:05:09 GMT` |
| `f`, `F` | `Friday, 27 November 2026 18:05`, `... 18:05:09` | `s`, `u` | `2026-11-27T18:05:09`, `2026-11-27 18:05:09Z` |
| `g` | `11/27/2026 18:05` | `t`, `T` | `18:05`, `18:05:09` |
| `m`, `y` | `November 27`, `2026 November` | `U` | `Friday, 27 November 2026 18:05:09` |

Custom formats: `yyyy`, `yy`, `y`, `MMMM`, `MMM`, `MM`, `M`, `dddd`, `ddd`, `dd`, `d`, `HH`, `H`, `hh`, `h`, `mm`, `ss`,
`f` to `fffffff`, `F` to `FFFFFFF` (which take a `.` before them back when they write nothing), `tt`, `t`, `z`, `zz`,
`zzz`, `K`, `g`, `:` and `/`, text in quotes, `\` before a char, `%` before a lone one. A DateOnly takes `d D m o r y`
and custom formats without a time, a TimeOnly `t T o r` and ones without a date. The formatter costs about 6.7 KB of
flash on the Pico (a program that prints `$"{now:HH:mm:ss}"` is 9.1 KB, one that prints a number 2.4 KB), and nothing
in a program that writes no date as text; dates themselves, their parts and arithmetic, add next to nothing.

**The clock.** `DateTime.Now`, `UtcNow` and `Today` read the target's clock (`"clock": true` in its file):

- On a chip, a software clock: a date, and the microseconds the timer has counted since (on the RP2040 its 64-bit
  timer, which counts from start-up and will not wrap for 584,000 years). `Clock.Start(date)` sets it, at any time,
  with interrupts off for the moment it takes; until then it starts at 1 January 2000 0:00. A chip has no time zones:
  `Now` is the same time as `UtcNow` (Kind Local and Kind Utc), its offset `+00:00`. It is as exact as the chip's
  crystal (the Pico's: some 30 ppm, 2.6 s a day); a real-time clock chip keeps the time while the power is off: read
  it at start-up, `Clock.Start(Ds1307.Read())` (see [below](#real-time-clock-ds1307)), and again now and then.
- On the PC, Windows' clock (`GetSystemTimePreciseAsFileTime`, as .NET reads it), `Now` with the time zone's offset
  now (`GetTimeZoneInformation`); after `Clock.Start`, a software clock as on a chip, so the same program shows the
  same time on both.

An interrupt handler may read the clock (it does not wait). On a target without a clock, `chipsharp check` names each
place: `System.DateTime.get_Now() needs a clock, and the target 'tiny' has none: ...`.

**Measuring.** `Stopwatch.GetTimestamp()`, `Stopwatch.Frequency` (1,000,000 on a chip; the performance counter's on
a PC, 10 MHz), `Stopwatch.IsHighResolution`, `Stopwatch.GetElapsedTime(start)` and `GetElapsedTime(start, end)` (a
`TimeSpan`), `Environment.TickCount` and `TickCount64` (milliseconds since start-up; on a PC since Windows started, as
in .NET) need no heap; a `Stopwatch` object (`Stopwatch.StartNew()`, `Elapsed`, `ElapsedMilliseconds`, `Restart()`)
is a class, which needs one. Timing a pulse on a pin, such as an HC-SR04's echo:

```csharp
while (!Pin.Read(15)) { }                                 // the echo starts
long rise = Stopwatch.GetTimestamp();
while (Pin.Read(15)) { }                                  // and ends
double centimetres = Stopwatch.GetElapsedTime(rise).TotalMicroseconds / 58;
```

## Hardware

Two libraries, as .NET IoT has System.Device.Gpio and Iot.Device.Bindings: **`ChipSharp.Hardware`** is what the chip
has (pins, timing, the clock, registers, interrupts, serial ports, analog inputs, PWM, the I2C and SPI buses), which
the chip's runtime does; **`ChipSharp.Components`** is the parts you connect to it (a character LCD, a 7-segment
display, a real-time clock, below),
drivers in plain C# over `ChipSharp.Hardware`, which ChipSharp compiles with the program like any class library it
references (only what the program calls). A program references either or both.

`ChipSharp.Hardware` is what a program references to use the chip: `Pin`, `Timing`, `Registers` and `[Interrupt]`
(above), `Clock` (see [Date and time](#date-and-time)), and the serial ports, analog inputs, PWM outputs and buses below. Every method is static and takes numbers and
spans, so nothing allocates and a chip without a heap has them all; those that do not wait can be called from an
interrupt handler. Each is set up before it is used, as a pin is with `Pin.Output`: `Uart.Open`, `Adc.Input`,
`Pwm.Output`, a bus's `Open`. A port, pin, clock or mode the chip does not have is a mistake in the program:
`chipsharp check -t <target>` names it where the value is a constant (the target file's `"pinFunctions"`, `"i2c"` and
`"spi"` list the pins and clocks), and on the chip the program stops with a message that says what is wrong
(`GPIO 6 is not an SDA pin of I2C0: its SDA pins are GPIO 0, 4, 8, 12, 16, 20, 24 and 28.`):

```
GPIO 6 cannot be UART1's TX on the target 'pico': 1 place in the top-level statements (IL_0009). UART1's TX can be GPIO 4, 8, 20 or 24.
```

A target without the capability (`"uart"`, `"adc"`, `"pwm"`, `"consoleInput"`, `"i2c"`, `"spi"`; the PC has none of
them) refuses the class altogether: `ChipSharp.Hardware.Adc needs analog inputs (an ADC), and the target 'host' has
none`.

### Serial ports (UART)

```csharp
Uart.Open(1, tx: 4, rx: 5, baud: 9600);    // UART1 on GPIO 4 and 5, 8N1
Uart.Write(1, "hello\r\n"u8);
Span<byte> buffer = stackalloc byte[32];
var count = Uart.Read(1, buffer);           // the bytes that have arrived, at most 32; 0 when none
var next = Uart.ReadByte(1);                // the next byte, or -1 when none has arrived
```

| Method | What it does |
|---|---|
| `Uart.Open(port, tx, rx, baud)` | Sets up UART0 or UART1. On the Pico UART0's TX is GPIO 0, 12, 16 or 28 and its RX GPIO 1, 13, 17 or 29; UART1's TX is GPIO 4, 8, 20 or 24 and its RX 5, 9, 21 or 25; 120 to 7,812,500 baud |
| `Uart.Write(port, ReadOnlySpan<byte>)`, `Uart.WriteByte(port, byte)` | Sends, waiting while the 32-byte send FIFO is full |
| `Uart.Read(port, Span<byte>)`, `Uart.ReadByte(port)`, `Uart.Available(port)` | Take what has arrived; they never wait |
| `Uart.Watch(port)`, `Uart.Unwatch(port)` | Run the `[Interrupt(Event.Uart)]` method each time bytes arrive on the port |
| `Console.Read()`, `Console.ReadLine()` | The console's input: UART0, GPIO 1 at 115200 baud (or where `Uart.Open(0, ...)` put it) |

From `Open` on, the chip receives on its own: the UART's interrupt moves each byte into a buffer of 256 bytes per
port, so nothing is lost while the program is busy; once the buffer is full, the bytes that come after are dropped.
The runtime puts its receive handlers in a copy of the vector table in RAM, so a program that never opens a port has
neither them nor their buffers (one that does takes about 1 KB of RAM). `Console.ReadLine` waits for a line and returns
it without its end, which is `"\r"`, `"\n"` or `"\r\n"` as for .NET; `Console.Read` returns each char, `'\r'` and `'\n'`
too. The input is UTF-8, decoded as .NET decodes it (U+FFFD for a byte that is not part of a character). Nothing is
echoed: the other end shows what it sends itself (a terminal's local echo). A serial line has no end, so `ReadLine`
never returns `null` and `Read` never `-1`. `ReadLine` makes a string, so it needs a heap; on a chip without one, read
the line char by char with `Console.Read`. Both wait, so an interrupt handler cannot call them; it reads with
`Uart.Read`:

```csharp
Uart.Open(1, 4, 5, 9600);
Uart.Watch(1);

partial class Program
{
    static volatile int received;

    [Interrupt(Event.Uart)]
    static void Received()
    {
        while (Uart.ReadByte(1) >= 0)
        {
            received++;
        }
    }
}
```

### Analog inputs (ADC)

```csharp
Adc.Input(26);                      // GPIO 26, the Pico's ADC0
var reading = Adc.Read(26);         // 0 (0 V) to 4095 (3.3 V)
var volts = Adc.Volts(26);          // reading * 3.3 / 4096
var celsius = Adc.Temperature();    // the chip's own sensor: 27 - (volts - 0.706) / 0.001721
```

The Pico's analog inputs are GPIO 26, 27 and 28, and 29, which measures a third of VSYS. A reading takes 2
microseconds, with interrupts off; the temperature sensor needs no `Input` and is not calibrated (a few degrees off).

### PWM outputs

```csharp
Pwm.Output(15, 1000);               // GPIO 15 at 1 kHz, low
Pwm.Duty(15, 0.25);                 // high a quarter of each period, as .NET IoT's PwmChannel.DutyCycle (0 to 1)
Pwm.Output(16, 50);                 // a servo's 50 Hz
Pwm.PulseMicroseconds(16, 1500);    // high 1.5 ms each period: the servo's middle (1 ms and 2 ms: its ends)
Pwm.Stop(15);                       // the pin no longer driven
```

Every GPIO of the Pico can be a PWM output, from 8 Hz to 62.5 MHz, with the duty cycle in up to 65,535 steps (fewer
above 1.9 kHz) and changing at the end of a period. Its 8 slices have two outputs each, which share the slice's
frequency: GPIO 2n and 2n + 1 are slice n's (GPIO 0 and 1 slice 0, ..., GPIO 14 and 15 slice 7), and from GPIO 16 on the
slices come round again, so GPIO n and n + 16 are the very same output. A second frequency on a slice whose other
output is in use, or a pin whose twin is a PWM output already, stops the program saying so:

```
Unhandled exception. System.InvalidOperationException: GPIO 17 shares PWM slice 0 with GPIO 0, which runs at 1000 Hz, and a slice runs at one frequency. Give both the same frequency, stop GPIO 0 first, or use a pin of another slice.
```

### I2C

```csharp
I2c.Open(0, sda: 4, scl: 5, frequency: 400_000);    // bus 0 (I2C0) on GPIO 4 and 5, 400 kHz
Span<byte> register = stackalloc byte[1];
Span<byte> id = stackalloc byte[1];
register[0] = 0x75;                                  // the MPU-6050's WHO_AM_I
if (I2c.WriteRead(0, 0x68, register, id)) Console.WriteLine($"0x{id[0]:X2}");   // 0x68
```

| Method | What it does |
|---|---|
| `I2c.Open(bus, sda, scl, frequency)` | Sets the bus up on its pins, with the chip's pull-ups on, its clock from 2 kHz to 1 MHz (100 kHz standard mode, 400 kHz fast mode, 1 MHz fast mode plus). The Pico's bus 0 (I2C0) has SDA on GPIO 0, 4, 8, ..., 28 and SCL on the pin after each; bus 1 (I2C1) SDA on GPIO 2, 6, 10, ..., 26 and SCL on the pin after |
| `I2c.Write(bus, address, data)` | Sends the bytes of a `ReadOnlySpan<byte>` to the device at a 7-bit address (0 to 127) |
| `I2c.Read(bus, address, data)` | Fills a `Span<byte>` from the device |
| `I2c.WriteRead(bus, address, write, read)` | Both in one transfer, a repeated start between and no stop: how a device's register is read (its number, then its value) |
| `I2c.Probe(bus, address)` | Whether a device answers at the address (it reads one byte) |
| `I2c.Scan(bus, addresses)` | Probes 0x08 to 0x77 and puts each address that answers into the span; returns how many |

Write, Read and WriteRead say whether it worked: `true` when the device answered its address and took every byte,
`false` when nothing answered, when the device refused a byte, or when the bus did not move for a byte's time limit (1 ms
and 50 clock periods), as when a device holds the clock low: a missing or stuck device never hangs the program.

### SPI

```csharp
Spi.Open(0, sck: 18, tx: 19, rx: 16, frequency: 1_000_000, mode: 0);   // bus 0 (SPI0), 1 MHz, mode 0
Pin.Output(17);                      // the device's chip select, a pin of the program's: high when idle
Pin.Write(17, true);
Pin.Write(17, false);                // select the device, then send a command and read the answer at once
Spi.Transfer(0, command, answer);    // command and answer: spans as long as each other
Pin.Write(17, true);
```

| Method | What it does |
|---|---|
| `Spi.Open(bus, sck, tx, rx, frequency, mode)` | Sets the bus up as its controller: 8 bits, most significant first, the clock the nearest the chip makes at or below `frequency` (2 kHz to 62.5 MHz), SPI `mode` 0 to 3; -1 for no `rx` (a device that only listens) or no `tx`. The Pico's bus 0 (SPI0) has SCK on GPIO 2, 6, 18 or 22, TX on 3, 7, 19 or 23, RX on 0, 4, 16 or 20; bus 1 (SPI1) SCK on 10, 14 or 26, TX on 11, 15 or 27, RX on 8, 12, 24 or 28 |
| `Spi.Write(bus, data)` | Sends the bytes, and drops what comes back |
| `Spi.Read(bus, data)` | Fills the span with what the device sends, sending zeros |
| `Spi.Transfer(bus, write, read)` | Both at once, a byte back for each byte sent (the two spans may be one) |

SPI has no answer to wait for, so these return nothing. Chip select is a `Pin` the program drives, so one bus serves
several devices: low before the transfer, high after it; the methods return once the last bit has left. A 74HC595
shift register takes its latch the same way (`examples/pico-spi-shift-register`).

### LCD

A character LCD, an HD44780 (a 16x2 "1602", a 20x4 "2004") behind a PCF8574 I2C backpack, usually at address 0x27
(0x3F for a PCF8574A), as Wokwi's `wokwi-lcd1602` with `"pins": "i2c"` is too: `Lcd` in `ChipSharp.Components`
(`using ChipSharp.Components;`, and a reference to `src/ChipSharp.Components`, as `examples/pico-i2c-lcd1602` has):

```csharp
I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);   // the backpack's bus first
if (Lcd.Open(1, 0x27, columns: 16, rows: 2))         // false when nothing answers at 0x27
{
    Lcd.Line(0, "Hello, ChipSharp");                 // the whole row: what was there after the text goes
    Lcd.Write(0, 1, "count");                        // from column 0 of row 1 on; the rest of the row stays
    Lcd.Write(6, 1, 42);                             // a number, digit by digit: no string made
}
```

| Method | What it does |
|---|---|
| `Lcd.Open(bus, address, columns, rows)` | Waits for the display to power up (40 ms), sets it up in 4-bit mode as the HD44780's datasheet does, clears it, turns it and the backlight on (no cursor). `true` when the backpack answered; `false` at once when nothing answers or the size is not one an HD44780 has (1 to 4 rows, at most 40 columns, 20 on more than two rows) |
| `Lcd.Clear()` | Every character a space (it waits the 2 ms that takes) |
| `Lcd.Write(column, row, text)` | Writes a string, a `ReadOnlySpan<char>`, a `char` or an `int` from the column on, columns and rows counting from 0 at the top left |
| `Lcd.Line(row, text)` | Writes a string or a span as the whole row: from column 0, then spaces to its end, so nothing of what was there is left (a status line) |
| `Lcd.Backlight(on)` | The backlight on or off; the text stays |

Text that does not fit is cut off at the end of its row (it never runs on into another row), and a row or column
outside the display shows nothing of it. The display shows ASCII from `' '` to `'~'` as its ROM (A00, the usual one)
has it, but for `'\'` as `¥` and `'~'` as `→`; any other char (`é`, `°`, a line end) shows as `?`. The driver is
plain C# over `I2c.Write` and `Timing` (`src/ChipSharp.Components/Lcd.cs`): the backpack's outputs are P0 RS, P1 RW,
P2 E, P3 the backlight, P4 to P7 D4 to D7 (the common modules' and Wokwi's layout), each character is one transfer of
five bytes followed by the HD44780's 50 us, so any bus clock works (a row of 16 takes about 10 ms at 100 kHz, 3 ms at
400 kHz). Nothing allocates and nothing stops the program: before `Open` (or after one that said `false`) the other
methods do nothing; on a target without I2C, `chipsharp check` names its `I2c` calls. It costs about 1.6 KB of flash
and 20 bytes of RAM, and nothing in a program that does not call it; `Open` and `Clear` wait, so an interrupt handler
cannot call them. A real 1602 is a 5 V part: on VBUS its text has more contrast, but the backpack's pull-ups then take SDA and SCL
to 5 V, more than the RP2040's pins stand (take them off, or use a 3.3 V display); `examples/pico-i2c-lcd1602` runs it
on 3V3.

### 7-segment display

A 7-segment display with a pin for each segment (A to G, and the decimal point, DP, when it has a pin of its own),
common cathode or common anode: `SevenSegment` in `ChipSharp.Components`, a struct, so a program has as many as it has
displays, with no heap (`examples/pico-seven-segment`):

```csharp
var count = new SevenSegment(firstPin: 2);           // A to G on GPIO 2 to 8, common cathode: the pins outputs, blank
var other = new SevenSegment(a: 10, b: 11, c: 12, d: 13, e: 14, f: 15, g: 16, dp: 17, commonAnode: true);
count.Show(7);                                       // a digit, 0 to 9
other.Show('F');                                     // '0' to '9', A b C d E F, '-', '_', ' '
other.Point(true);                                   // the decimal point
byte bits = SevenSegment.Pattern(7);                 // 0x07, A, B and C: e.g. for a 74HC595 over SPI
```

| Member | What it does |
|---|---|
| `new SevenSegment(firstPin, commonAnode = false)` | A display with A to G on `firstPin` to `firstPin + 6` and no DP: makes those pins outputs and the display blank |
| `new SevenSegment(a, b, c, d, e, f, g, dp = -1, commonAnode = false)` | The same on any pins, with DP on `dp` (-1: none) |
| `Show(digit)`, `Show(character)` | A digit, 0 to 9, or a char: `'0'` to `'9'`, `'A'` to `'F'` (as A b C d E F; `'a'` to `'f'` too), `'-'`, `'_'`, `' '`; on A to G, DP stays as it is |
| `Write(segments)` | Lights the segments whose bits are set, bit 0 for A to bit 6 for G and bit 7 for DP (`0b0111111`, 0x3F, is a 0), the others off |
| `Point(on)` | The decimal point on or off; nothing without a DP |
| `Blank()` | Every segment off, DP too |
| `SevenSegment.Pattern(digit)`, `Pattern(character)` | The bits alone: 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F for 0 to 9, the byte a 74HC595 takes when its Q0 to Q7 drive A to DP |

A digit outside 0 to 9, or a char it has no shape for, shows as `-`: as with the LCD's `?`, nothing stops the program.
A common-anode display (its COM on 3V3) lights a segment whose pin is low, and the struct turns the levels round. A
display made with `default` rather than `new` has no pins and drives nothing, nor does a pin number outside 0 to 254.
The driver is plain C# over `Pin.Output` and `Pin.Write` (`src/ChipSharp.Components/SevenSegment.cs`), a struct of 9
bytes; the shapes are a table of 16 bytes in flash. Nothing waits, so an interrupt handler can call it. It costs about
150 bytes of flash with its table where a program only shows digits (the example), about 420 with every method, and
nothing in a program that does not use it. A real display needs a resistor for each segment (220 ohm on 3.3 V);
Wokwi's needs none.

### Real-time clock (DS1307)

A DS1307, a clock chip at I2C address 0x68 with a battery of its own (a CR2032 on the common modules), so the date and
time it keeps go on while the board has no power: `Ds1307` in `ChipSharp.Components`. Read it once at start-up and give
the time to the chip's clock (see [Date and time](#date-and-time)), which keeps it from then on
(`examples/pico-clock`):

```csharp
I2c.Open(1, sda: 26, scl: 27, frequency: 100_000);
if (Ds1307.Open(1) && Ds1307.Running)               // it answers at 0x68, and its clock runs
{
    Clock.Start(Ds1307.Read());                       // DateTime.Now counts from its time
}
else
{
    Ds1307.Write(new DateTime(2026, 11, 27, 18, 0, 0));   // set it, and start it
}
```

| Member | What it does |
|---|---|
| `Ds1307.Open(bus, address = 0x68)` | Chooses the clock on a bus `I2c.Open` has set up: `true` when it answers |
| `Ds1307.Read()` | The date and time it keeps, to the second, Kind Unspecified: its seven registers (BCD, 24- or 12-hour form) in one transfer, which the DS1307 copies when the transfer starts, so a read never mixes two seconds |
| `Ds1307.Write(time)` | Sets it (24-hour form; the years it keeps are 2000 to 2099) and starts it: `true` when it took the bytes |
| `Ds1307.Running` | Whether its clock runs: a new DS1307, or one whose battery ran out, is halted at 1 January 2000 |

Nothing allocates and nothing stops the program: before `Open` (or after one that said `false`), or when it does not
answer, `Read` gives `default(DateTime)` (1 January 0001), as it does for registers that hold no date, and `Write`
`false`. The driver is plain C# over `I2c.WriteRead` and `I2c.Write` (`src/ChipSharp.Components/Ds1307.cs`), about
2.5 KB of flash with the DateTime code it calls and 8 bytes of RAM; a transfer takes about 1 ms at 100 kHz. A real
DS1307 is a 5 V part: it keeps time from its supply only at 4.5 V or more (VBUS), and its module's pull-ups then take
SDA and SCL to 5 V: take them off, use a level shifter, or a DS3231, which runs on 3.3 V and has the same registers.

### Buses in the simulator

`tools/rp2040-sim/run.mjs` puts simulated devices on the buses (`tools/rp2040-sim/devices`), and prints what the buses
do with the pin changes:

| Option | Device |
|---|---|
| `--i2c 0:0x68=mpu6050` | An MPU-6050 at 0x68 on I2C0: `WHO_AM_I` 0x68, asleep until woken, a still board's measurements |
| `--i2c 0:0x76=bmp280` | A BMP280 with the datasheet's example calibration and readings (25.08 °C) |
| `--i2c 1:0x50=stuck` | A device that holds the clock low once addressed: the transfer gives up |
| `--spi 0=74hc595@17` | A 74HC595 on SPI0, its latch on GPIO 17: its outputs reported like pins (`SPI0 74HC595 Q3 high`) |
| `--spi 0=loopback`, `--spi 1=loopback@13` | RX wired to TX: every byte comes back (with a pin: only while it is low) |
| `--i2c 1:0x27=lcd1602`, `--i2c 0:0x3f=lcd2004` | A 16x2 (20x4) character LCD on a PCF8574 backpack: an HD44780 in 4- and 8-bit mode with its instructions, its DDRAM and its busy times, printing what it shows (below) |
| `--i2c 1:0x68=ds1307`, `ds1307@2026-11-27T18:00:00`, `ds1307@halted` | A DS1307 real-time clock: its registers, RAM and clock-halt bit, counting with the simulated time from the PC's local time, from a date, or halted as a new one is; a write of the time prints `DS1307 0x68 set to 2026-11-27 18:00:00 (Friday, day 6), running` |

An address without a device does not answer. Each I2C transfer is a line, `I2C0 0x68 write 3b, read 20 00 f0 ...`
(a comma for a repeated start) or `I2C0 0x50 write: no answer`; each new clock a line too, `I2C0 100000 Hz`,
`SPI0 mode 0, 992063 Hz`.

A simulated LCD prints its screen once it settles (5 ms after the last write to it, at most 50 ms after the first)
and only when it changed: a line per row, between bars, with the backlight and the display's on or off when they
change (the example, `--i2c 1:0x27=lcd1602`):

```
[    5.034 ms] LCD 0x27 backlight on
[    5.034 ms] LCD 0x27 display off
[   64.050 ms] LCD 0x27: |Hello, ChipSharp|
[   64.050 ms] LCD 0x27: |count 0         |
[ 1064.160 ms] LCD 0x27: |Hello, ChipSharp|
[ 1064.160 ms] LCD 0x27: |count 1         |
```

A write that comes while the HD44780 is busy (the datasheet's 37 us, 1.52 ms for a clear, 40 ms after power-up) is
lost, as on a real one, and is a line too: `LCD 0x27 busy: lost a write 0.912 ms into power-up (40.000 ms)`.

### Serial ports, analog inputs and PWM in the simulator

`tools/rp2040-sim/run.mjs` plays the other end. `--send "0=led on\r@100ms"` sends text to UART0's RX (or UART1's, with
`1=`), byte after byte at the port's speed, with `\r`, `\n`, `\t`, `\\` and `\xHH` for those bytes, and prints UART1's
output as it does UART0's. `--adc 26=1.65V` sets an analog input (or `27=2048`, a reading; `temp=30C`, the temperature,
27 C unless given), from the start or `@` a time. A PWM output is printed as it is measured from its edges, when a
period differs from the one before, rather than edge by edge:

```
[    2.033 ms] GPIO15 PWM 1000.00 Hz, duty 25.00% (250.00 us high)
[  302.532 ms] GPIO15 PWM steady low
```

## The chipsharp command

| Command | What it does |
|---|---|
| `chipsharp build <program.dll> [-t <target>] [-o <folder>] [--keep] [--verify-heap] [--no-cache] [--quiet-notes]` | Compiles the program for a target (default `host`) into `chipsharp-output\<target>\` (or `-o`): prints the file (`.exe`, `.uf2`) and what it takes of the chip. `--keep` also writes the TIR (`.tir`) and LLVM IR (`.ll`) there, and for a chip the `.elf` |
| `chipsharp flash <program.dll \| image> -t <board> [--port <port>]` | Puts the program onto a board with its target's flasher: a `.dll` is built first (as `build` does, with its options), an image (`.hex`, `.uf2`) is flashed as it is. The Uno: avrdude through its USB port (`--port COM5`, or the one serial port that looks like an Arduino's); the Pico: a copy to its `RPI-RP2` drive (or `--port E:\`) |
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
| `CHIPSHARP_NARROWING` | `on` or `off`: [narrowing](#narrowing) for every target, whatever its file says (to compare a program's results with and without it) |
| `CHIPSHARP_ZIG` | Zig (`zig.exe` or its folder), for the Uno (default: `D:\Tools\zig\zig.exe`, then the PATH) |
| `CHIPSHARP_AVRDUDE` | avrdude (`avrdude.exe` or its folder), for `flash -t uno` (default: the PATH, then the Arduino IDE's copy) |

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
| `pico-i2c-mpu6050` | Reads an MPU-6050 accelerometer and gyroscope over I2C (GPIO 4 and 5): its `WHO_AM_I`, then its acceleration, rotation and temperature twice a second, taken apart with `BinaryPrimitives`, over UART0. No heap | 8.8 KB, 40 bytes of RAM |
| `pico-spi-shift-register` | Counts on a 7-segment digit through a 74HC595 shift register over SPI: three pins (GPIO 17, 18, 19) for eight segments. No heap | 6.0 KB, 24 bytes of RAM |
| `pico-i2c-lcd1602` | A title and a count, once a second, on a 16x2 character LCD on an I2C backpack (I2C1, GPIO 26 and 27) with `ChipSharp.Components`' `Lcd`. No heap | 7.6 KB, 60 bytes of RAM |
| `pico-seven-segment` | Counts from 0 to 9, once a second, on a 7-segment display on GPIO 2 to 8 with `ChipSharp.Components`' `SevenSegment`, and over UART0. No heap | 3.4 KB, 16 bytes of RAM |
| `pico-clock` | The date and the time on a 16x2 LCD, once a second, set from a DS1307 real-time clock at start-up (or from a date when there is none) and kept by the Pico's clock: `Clock.Start`, `DateTime.Now`, `TryFormat` into a span for `Lcd.Line`; both on I2C1 (GPIO 26 and 27). No heap | 18.7 KB, 88 bytes of RAM |
| `pico-uart-echo` | A command prompt on the console: `led on`, `led off` and `status` read with `Console.ReadLine` and a `switch` on the line, anything else answered with what was typed | 16.3 KB, 1.2 KB of RAM + heap |
| `pico-adc-pwm` | A potentiometer on GPIO 26 sets the brightness of an LED on GPIO 15 through 1 kHz PWM, and each change of 0.1 V goes to the console. No heap | 10.5 KB, 128 bytes of RAM |
| `pico-servo` | A servo on GPIO 22 sweeps from 0 to 180 degrees and back with 50 Hz pulses of 1 to 2 ms. No heap | 6.1 KB, 116 bytes of RAM |

And for the Arduino Uno, built with `chipsharp build bin\Release\net10.0\<Name>.dll -t uno` (or flashed with
`chipsharp flash ... -t uno`, see [On an Arduino Uno](#on-an-arduino-uno)):

| Example | What it does | Uno |
|---|---|---|
| `uno-blink` | Blinks the LED "L" (D13) with a delay loop; in Wokwi, an LED on D13 and the GND beside it blinks with it | 1.2 KB, 15 bytes of RAM |
| `uno-timer-blink` | Blinks the LED "L" from the timer event (Timer1), while `Main` does nothing; the same circuit | 1.6 KB, 24 bytes of RAM |
| `uno-button` | A button between D2 and GND toggles the LED "L" through the pin event (INT0, falling edges); the program turns D2's pull-up on with `Registers.Write` of PORTD, so the button needs no resistor | 1.4 KB, 24 bytes of RAM |

A serial example for the Uno (a counter and an echo on the console) waits for `Console.Write` of text and numbers,
which needs the shared runtime's C made safe for the AVR (see [Arduino Uno](#arduino-uno-in-progress)).

The traffic lights, the bus examples, the 7-segment counter, the clock, the command prompt, the knob and the servo have a
`diagram.json` for Wokwi with the whole circuit, wired directly (Wokwi needs no resistors): common-cathode displays,
the button between GPIO 22 and 3V3, the potentiometer between 3V3 and GND, the servo on VBUS, the LCD in I2C mode and the DS1307 on
3V3, UART0 on the serial monitor (where the prompt's commands are typed); so do the Uno's, with a few parts placed so
that no wire crosses one. With the button pressed at 2.5 s, both
traffic lights print:

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

The 2,000 or so tests (about 2 minutes once the build cache is warm) check each stage on its own (reading IL, the whole-program pass, TIR, the LLVM code, linking, the `.uf2`),
then compile C# programs and run them: on the PC, and on the simulated Pico, comparing what they print, byte for byte,
with what .NET prints for the same program; the library code (collections, `StringBuilder`, sorting, `TimeSpan`, `DateTime` and its formats)
runs next to .NET's own on thousands of random operations, and the math functions on hundreds of thousands of values,
computed both with the PC's float hardware and in software as the Pico computes them. The PC tests need LLVM; the Pico tests also need Node.js
and `npm ci` in `tools\rp2040-sim` (the Uno's, Zig and `npm ci` in `tools\avr-sim`), and are skipped, saying why,
without them. The example projects are built with
`dotnet build` and `chipsharp build` and run in the simulator, the traffic lights with the button pressed.
`chipsharp flash` runs no real tool in the tests: avrdude is a batch file that keeps the command line it was given, and
the Pico's drive a folder.

The simulator, rp2040js 1.4.1, has one known fault: it sets the overflow flag of `SBCS` wrong when the borrow takes the
subtrahend to 0x80000000, so a comparison of extreme `long` values, such as `long.MaxValue` with `long.MinValue`, gives
the wrong answer on the simulated Pico only (a real RP2040 and the PC are right). The Pico tests leave those values to
the PC tests.

## How it works

### Layout

| Folder | What it holds |
|---|---|
| `src/ChipSharp.Frontend` | Reads the IL of a compiled C# `.dll`, finds every method the program uses (the whole-program pass), checks it against what ChipSharp compiles (`chipsharp check`), and translates it into TIR |
| `src/ChipSharp.Ir` | TIR (Typed Intermediate Representation), which every code generator reads: its verifier, printer and builder, and in `Optimizations` the value-range analysis and narrowing |
| `src/ChipSharp.Library` | ChipSharp's own C# for library types (`TimeSpan`, `List<T>`, `StringBuilder`, the exceptions, ...), compiled together with the programs that use them |
| `src/ChipSharp.Backend.Llvm` | The `llvm` code generator: TIR in, LLVM IR text out |
| `src/ChipSharp.Toolchain.Clang` | The `clang` toolchain: LLVM IR and runtime sources in, a linked program out; as the `zig` toolchain, the same with the clang and ld.lld inside Zig (AVR) |
| `src/ChipSharp.Runtime.*` | The runtimes, in C: `Shared` (console, text, floats, heap and collector), `Windows` (the PC), `ArmV6M` and `Rp2040` (start-up, UART0, timers, pins, interrupts), `Avr` (any AVR chip's start-up, multiply and divide helpers, soft float) and `Atmega328p` (the Uno's pins, Timer1's clock and delays, the pin and timer events, USART0) |
| `src/ChipSharp.Image.Uf2`, `src/ChipSharp.Image.Hex`, `src/ChipSharp.Build`, `src/ChipSharp.Cli` | The `.uf2` and `.hex` images, the build pipeline, and the `chipsharp` command |
| `src/ChipSharp.Hardware` | The library programs reference for pins, timing, the clock, registers, interrupts, serial ports, analog inputs, PWM and the I2C and SPI buses |
| `src/ChipSharp.Components` | Drivers of parts connected to a chip (the character LCD, the 7-segment display, the DS1307 real-time clock), plain C# that ChipSharp compiles with the program |
| `src/ChipSharp.Abstractions`, `src/ChipSharp.Targets` | The contracts (code generator, toolchain, image, runtime) and the target files' reader |
| `targets/arch`, `chip`, `board` | Target files |
| `samples`, `examples`, `tests`, `tools/rp2040-sim`, `tools/avr-sim` | See above |

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
otherwise, always so, so two equal strings have the same form. A
hole of a DateTime, DateOnly or TimeOnly (`$"{now:HH:mm}"`, `now.ToString("d")` printed) is a part of its own
(`FormattedValues.cs`): its format, known from the IL, becomes a constant span of UTF-16 chars in flash, as any string
literal passed as a `ReadOnlySpan<char>` does, and the struct's own `Print` writes it, a few chars at a time through a
buffer on the stack; kept text calls its `Text`, which makes the string. Reading strings (compare, search, measure, print) needs
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
David Schultz's BSD license), AMD's are BSD-3-Clause; each file has its notices, and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)
lists every third-party piece ChipSharp ships (compiler-rt, the Pico SDK's boot2, musl, AMD's library, .NET's code).

### Narrowing

On an 8-bit AVR an `int` operation takes four registers and four instructions, and the IL C# compiles to widens every
byte and short to an int32. ChipSharp puts the arithmetic back in 8 or 16 bits where that gives C#'s result exactly
(`src/ChipSharp.Ir/Optimizations`), once the IL is translated to TIR, for the targets whose `narrowing` capability is on
(by default those with 2-byte pointers): the code for the Pico and the PC is what it was.

**Value ranges.** `TirRangeAnalysis` proves an interval for every integer: from constants, the small types loads and
conversions make (a `byte` is 0 to 255), masks, shifts, divisions, the elements of constant tables, the length of an
array or span (no more than the target's largest memory has bytes: 32,256 on the Uno) and the branches taken (where
`i < 7` is true, i is at most 6). Loops are followed until nothing changes, a range that keeps growing jumping to the
function's next constant, and two more passes take back what the jumps gave away; a function only the program calls
gets the ranges its callers pass, and they get the range it returns. `nint`, whose width only the target knows, has no
range, so a module narrows alike for every target.

**The rules** (`TirNarrowing`). A variable whose every value fits 8 or 16 bits becomes that wide. `+ - * & | ^` and `<<`
are done in N bits when their result fits: their low bits depend only on the operands' low bits, so they wrap alike.
`>>`, division and remainder need the operands to fit too, and never the smallest value divided by -1; a signed division
of values that are never negative is the unsigned one. A comparison is done in N bits when both sides fit, read the same
way: two bytes compared as signed are compared unsigned, an sbyte compared with `200u` takes 16 bits. Where the full value
is needed (a call, memory, an index, a return) it is extended back, exactly. Where narrowing would cost more than it
saves it is not done: an operation narrows narrow operands only (but a multiplication, division or shift, which the AVR
calls helpers or loops for), and a comparison with a value cut from a wider one, such as an array's length, stays wide,
so that LLVM still sees that the check of that index against that length always passes.

**What it gives.** On the Uno the samples' code takes 5% less flash (22,300 bytes instead of 23,550), and none more:
ForLoops 310 bytes instead of 588, Primes 460 instead of 554, LookupTables 1,876 instead of 2,270. On the Pico it would
save 0.6% in all but make some programs larger (the traffic lights by 140 bytes), so it stays off there. The tests run
programs with every small type and operator, written by hand and generated, narrowed on the PC, the simulated Pico and
the Uno, and compare them with .NET; `CHIPSHARP_NARROWING=on` runs the whole suite narrowed (only the tests that compare
TIR text then differ).

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
