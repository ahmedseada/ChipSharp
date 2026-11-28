# ChipSharp

C# for microcontrollers. You write an ordinary C# program and build it with `dotnet build` as usual; ChipSharp then
compiles it ahead of time, through LLVM, into native code: a small `.exe` for your PC, a `.uf2` file for a Raspberry
Pi Pico or an ESP32-C5 (a `.bin`), or a `.hex` file for an Arduino Uno, a Padauk PFS154 or an STM8S103 (these two through C and SDCC). On the chip there is no operating system, no .NET runtime and no
interpreter: only your program, a small runtime written in C, and, when the program needs one, a heap with a garbage
collector.

```
C# --Roslyn--> IL --ChipSharp--> TIR --LLVM--> .exe (PC)   or   .elf --> .uf2 (Pico), .bin (ESP32-C5), .hex (Arduino Uno)
                                     \--C--SDCC--> .hex (Padauk PFS154, STM8S103)
```

Where it stands: milestones M0 to M3, M4 (the ESP32-C5, simulated: not yet tried on a board), M6 (the Padauk PFS154 and the STM8S103, through C and SDCC) and M7 (the Arduino Uno) are done. Numbers, structs, arrays, spans, generics,
text, interrupts, `Math`, `TimeSpan` and `DateTime` (with its text and a clock) work on every chip, even one without a
heap, such as the Uno with its 2 KB of RAM; on a chip with a heap (the Pico, the ESP32-C5, the PC) so do classes, records, strings,
interfaces, delegates and lambdas, boxing, exceptions you throw (not yet catch) and the collections (`List<T>`,
`Dictionary<TKey, TValue>`, ...). Every sample prints exactly what .NET prints, on the PC and on a simulated Pico (and,
when it makes no object, on a simulated Uno), and the tests compare programs, and thousands of library calls, with
.NET's own results.

**Contents:** [Quick start](#quick-start) · [Targets](#targets-what-a-chip-has) · [Arduino Uno](#arduino-uno) ·
[PFS154 and STM8S103](#pfs154-and-stm8s103-through-c-and-sdcc) · [A new chip](#a-new-chip-one-contract-one-suite-chips-as-data) ·
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
| [Node.js](https://nodejs.org) 18 or later | Only for the simulators, once each: `cd tools\rp2040-sim; npm ci` (the Pico's), `cd tools\avr-sim; npm ci` (the Uno's) |
| [Zig](https://ziglang.org/download/) 0.17 or later | Only for the Arduino Uno: its clang and ld.lld have the AVR code generator LLVM's Windows builds lack (see [Arduino Uno](#setting-up-for-the-uno)) |
| avrdude (it comes with the [Arduino IDE](https://www.arduino.cc/en/software)) | Only for flashing a real Uno (`chipsharp flash -t uno`, [below](#build-simulate-and-flash)) |
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

With [Zig](#setting-up-for-the-uno) installed, the same `build` makes a `.hex` for the Uno:

```powershell
cd examples\uno-blink
dotnet build -c Release
chipsharp build bin\Release\net10.0\UnoBlink.dll -t uno
```

```
...\chipsharp-output\uno\UnoBlink.hex
flash 1.2 KB of 31.5 KB (3.7%), RAM 15 bytes of 2 KB (stack 512 bytes reserved)
```

Run it in the simulator (`node tools\avr-sim\run.mjs chipsharp-output\uno\UnoBlink.hex --ms 3000`), in
[Wokwi](https://wokwi.com) (an Arduino Uno project, F1, "Upload Firmware and Start Simulation...", the `.hex`), or put it
on a real Uno through its USB port: `chipsharp flash bin\Release\net10.0\UnoBlink.dll -t uno --port COM5`. Everything
about the Uno, its pins and what runs on it: [Arduino Uno](#arduino-uno).

## Targets: what a chip has

A target file says what a chip or board is and has. Files inherit from each other, board → chip → architecture, so a
new board is a few lines of JSON:

| Target | What it is |
|---|---|
| `host` | Your PC: a Windows x64 console program, with no C runtime (inherits `x86_64`) |
| `host-c` | Your PC again, its programs written in C by the `c` code generator and compiled by clang: the C backend checked against .NET (inherits `host`) |
| `pico` | Raspberry Pi Pico: RP2040, 2 MB flash, LED on GPIO 25 (inherits `rp2040`, which inherits `armv6m`) |
| `pico-w` | Pico W: like the Pico, but its LED is wired to the Wi-Fi chip |
| `uno` | Arduino Uno: ATmega328P at 16 MHz, 32 KB flash, 2 KB RAM, LED on D13 (see [Arduino Uno](#arduino-uno); inherits `atmega328p`, which inherits `avr`) |
| `stm8s103-board` | The blue STM8S103F3P6 board: STM8 at 16 MHz, 8 KB flash, 1 KB RAM, LED on PB5, through C and SDCC (see [The STM8S103 board](#the-stm8s103-board); inherits `stm8s103f3`, which inherits `stm8`) |
| `pfs154-bare` | A Padauk PFS154 on its own at 2 MHz (IHRC / 8), through C and SDCC: 2K words of program memory, 128 bytes of RAM, LED on PA4 (see [The PFS154's runtime](#the-pfs154s-runtime); inherits `pfs154`, which inherits `pdk14`) |
| `esp32c5-devkitc` | Espressif ESP32-C5-DevKitC-1: ESP32-C5 (RISC-V, RV32IMAC, up to 240 MHz), 384 KB of SRAM, an RGB LED on GPIO 27; builds to a flashable `.bin` (see [ESP32-C5](#esp32-c5); inherits `esp32c5`, which inherits `riscv32`) |
| `ch32v003-evt` | WCH's CH32V003 evaluation board (CH32V003F4P6-R0-1v1): CH32V003 (RISC-V, RV32EC, 48 MHz), 16 KB flash, 2 KB SRAM, no heap; builds to a `.bin` of its flash, which wlink writes through a WCH-LinkE (see [CH32V003](#ch32v003); inherits `ch32v003`, which inherits `riscv32e`, which inherits `riscv32`) |
| `rp2040`, `atmega328p`, `pfs154`, `stm8s103f3`, `esp32c5`, `ch32v003`, `armv6m`, `avr`, `pdk14`, `stm8`, `riscv32`, `riscv32e`, `x86_64` | Chips and architectures, for target files to inherit; not built for directly |

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
  capabilities hwMultiply=true, hwDivide=false, fpu=false, atomics=none, unalignedAccess=false, stackCheck=true, heap=224K, console=true, clock=true, romFloat=true, pullDown=true, consoleInput=true, uart=2, adc=4, pwm=16, i2c=true, spi=true
  pins         LED=25
  interrupts   timer=IRQ 1, pin=IRQ 13, uart=IRQ 26
```

The `"capabilities"` decide what a program may use on the chip:

| Capability | Meaning | `pico` | `esp32c5-devkitc` | `uno` | `pfs154-bare` | `stm8s103-board` | `ch32v003-evt` | `host` | `armv6m` (a new chip, until its file says) |
|---|---|---|---|---|---|---|---|---|---|
| `heap` | The most RAM the heap may take, or `false` for none. Objects, kept strings, boxes, delegates and collections need one (see [Memory](#memory)) | 224 KB | 224 KB | none | none | none | none | 256 MB | none |
| `console` | Whether `Console.Write` has somewhere to go | UART0 | USB Serial/JTAG | USART0 | none | UART1 | USART1 | standard output | none |
| `consoleInput` | Whether `Console.Read` and `Console.ReadLine` have something to read | UART0 | USB Serial/JTAG | USART0 | none | UART1 | USART1 | not yet | none |
| `clock` | Whether a timer counts time for `DateTime.Now`, `Stopwatch`, `Environment.TickCount` and `Clock.Start` (see [Date and time](#date-and-time)) | the timer's microseconds | SYSTIMER's microseconds | Timer1's half microseconds | T16's microseconds | TIM1's microseconds | SysTick's sixths of a microsecond | Windows' clock | none |
| `uart`, `adc`, `pwm` | How many serial ports, analog inputs and PWM outputs `ChipSharp.Hardware` can use (see [Hardware](#hardware)); `"pinFunctions"` lists their pins (and `"pinNames"`, the Uno's, the names check's messages give them) | 2, 4, 16 | 1, 6, 6 | 1, 6, 5 | 0, 0, 2 | 1, 5, 3 | 1, 0, 0 | none | none |
| `unalignedAccess` | Whether a word may be read at any address; without it `MemoryMarshal.Read` reads byte by byte and `MemoryMarshal.Cast` checks alignment | no | no | yes | yes | yes | no | yes | no |
| `stackCheck` | The functions that can recurse check the stack, so a stack overflow says `Stack overflow.` instead of locking the core up | yes | yes | not yet | yes | yes | yes | (Windows' guard page) | yes |
| `narrowing` | Arithmetic in 8 or 16 bits where the values provably fit and the result is C#'s (see [Narrowing](#narrowing)); when not given, on for a chip with 2-byte pointers (the AVR) only | no | no | yes | yes | yes | no | no | no |
| `i2c`, `spi` | Whether `I2c` and `Spi` have buses (see [Hardware](#hardware)); the file's `"i2c"` and `"spi"` give each bus's pins and clocks | 2 each | 1 each | 1 each | none | 1 each | none (wave 2) | none | none |
| `pullDown` | Whether the chip has pull-down resistors: `Pin.Input(pin, Pull.Down)` (see [Pins](#pins)); every chip has pull-ups, and `chipsharp check` refuses a constant `Pull.Down` where this is false | yes | yes | no | no | no | yes | no | no |
| `double64` | Whether the C a code generator writes has a 64-bit `double`; `false` where it is SDCC's (see [Limits of the two chips](#limits-of-the-two-chips)). Not given: true | yes | yes | yes | no | no | yes | yes | yes |
| `hwMultiply`, `hwDivide`, `fpu`, `atomics`, `romFloat` | Describe the chip; the code itself follows the file's `triple`, `cpu` and `clang` flags (soft float on `armv6m` and `avr`; the AVR's `mul` is 8 x 8 bits, so `hwMultiply` is false) | | |  | | |  | | |

The file also gives the memory regions, the link layout (`"link.stack"`: 8 KB on the Pico) and the interrupts each
event uses (`"interrupts"` in `rp2040.json`). `chipsharp check -t <target>` and `chipsharp build -t <target>` judge the
program for that chip: on one without a heap or without a console, they name each place that needs it (see
[below](#when-chipsharp-check-says-no)). A new chip of a known architecture is a JSON file plus its runtime; a CPU no
code generator supported yet also needed a runtime for its architecture, as RISC-V did (`riscv32`, for the ESP32-C5).

## Arduino Uno

Milestone M7: the Arduino Uno's ATmega328P, an 8-bit AVR at 16 MHz with 32 KB of flash and 2 KB of RAM, is the target
`uno`. `chipsharp build -t uno` makes a `.hex` that runs on a real Uno (`chipsharp flash`), in Wokwi and in the
simulator in `tools\avr-sim`. 2 KB of RAM leaves no room for a heap, so the Uno runs what needs none (see
[On every chip, with no heap](#on-every-chip-with-no-heap)): numbers, floats, structs, enums, arrays and spans, static
tables, generics over values, record structs, tuples, nullables, fixed and printed text, `Math`, `TimeSpan`,
`DateTime` with its text and a clock, interrupts, all of `ChipSharp.Hardware` and the parts of `ChipSharp.Components`.
A program that makes objects is refused, place by place (see [What runs on the Uno](#what-runs-on-the-uno)).

### Setting up for the Uno

| What | Why, and where |
|---|---|
| [Zig](https://ziglang.org/download/) 0.17 or later | LLVM's release builds for Windows have no AVR code generator, so the target's toolchain, `zig`, runs the clang and ld.lld inside Zig (its LLVM 22 has one). Download the Windows zip and unpack it anywhere, e.g. `D:\Tools\zig`. ChipSharp finds `zig.exe` through `CHIPSHARP_ZIG` (the file or its folder), then `D:\Tools\zig\zig.exe`, then the PATH. The tests that need it are skipped, saying why, without it |
| The simulator | Node.js 18 or later, and once: `cd tools\avr-sim; npm ci` (avr8js 0.21.1, the simulator Wokwi is built on) |
| avrdude | Only to flash a real Uno. It comes with the [Arduino IDE](https://www.arduino.cc/en/software), whose copy ChipSharp finds (`%LOCALAPPDATA%\Arduino15\packages\arduino\tools\avrdude`), or [on its own](https://github.com/avrdudes/avrdude/releases): put its folder on the PATH, or set `CHIPSHARP_AVRDUDE` to `avrdude.exe` |

### Build, simulate and flash

```powershell
cd examples\uno-blink
dotnet build -c Release
chipsharp build bin\Release\net10.0\UnoBlink.dll -t uno
```

```
...\chipsharp-output\uno\UnoBlink.hex
flash 1.2 KB of 31.5 KB (3.7%), RAM 15 bytes of 2 KB (stack 512 bytes reserved)
```

The program has 31.5 KB of flash: the Uno's bootloader (optiboot), through which a program is uploaded over USB,
keeps the last 512 bytes of the chip's 32 KB. The `.hex` is Intel HEX, what `llvm-objcopy -O ihex` writes: the
flash's contents, the starting values of the variables included.

- **In the simulator** (avr8js, in Node.js): `node tools\avr-sim\run.mjs chipsharp-output\uno\UnoBlink.hex --ms 3000`
  prints every pin change by its Uno name (`[   20.822 ms] D13 high`), every line written to USART0
  (`USART0: Hello`), and each PWM output as it is measured (`D9 PWM 50.00 Hz, duty 7.50% (1500.00 us high)`). It plays
  the rest of the circuit too: `--input D2=low@500ms` drives a pin from outside (a button between D2 and GND pressed at
  0.5 s); `--send "text\n@50ms"` types into USART0 and `--usart0 file.bin` saves what it sent; `--adc A0=2.5V` sets
  an analog input (`A1=512`, a reading; `temp=30C`, the chip's temperature; `@100ms`, from a time on); the Pico
  simulator's devices go on the Uno's buses ([below](#buses-in-the-simulator)): `--i2c 0:0x27=lcd1602`,
  `--i2c 0:0x68=ds1307` (`ds1307@2026-11-27T18:00:00`, `ds1307@halted`), `--spi 0=74hc595@D10` (its latch on D10). A
  run stops when the program has finished (it sleeps with interrupts off) or reaches a `break` (a fatal error, exit
  code 3).
- **In Wokwi** ([wokwi.com](https://wokwi.com), in the browser): start a new Arduino Uno project, replace its
  `diagram.json` with the example's, press F1 in the code editor, pick "Upload Firmware and Start Simulation...", and
  choose the `.hex`. Wokwi's serial monitor, under the circuit, is USART0. The examples' circuits name the Uno's pins
  as Wokwi's `wokwi-arduino-uno` does: `uno:13`, `uno:2`, `uno:A0`, `uno:5V`, `uno:GND.1` (the GND beside 13; `GND.2`
  and `GND.3` are beside `5V`).
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
  (COM & LPT)". Close the serial monitor first: avrdude needs the port. The flasher is the target's (`uno.json`:
  `"flasher": "avrdude"`, and `"avrdude"`: the programmer `arduino`, optiboot's STK500 protocol, at 115200 baud; the
  chip is the `"mmcu"`); a board with another bootloader sets its own (an old Nano's: `"baud": 57600`). When avrdude
  fails, the error says what is likely wrong (the port would not open, the bootloader did not answer, the chip is
  another) and shows avrdude's last lines.

### The Uno's pins

`Pin`, `Adc`, `Pwm`, `I2c`, `Spi` and `Uart` take the Uno's (Arduino's) numbers: 0 to 13 are D0 to D13, 14 to 19 are
A0 to A5. `chipsharp check -t uno` names them as the headers do, and refuses a pin, a second port or bus, or a clock
the Uno does not have: `pin 4 (D4) cannot be UART0's TX on the target 'uno' ... UART0's TX can be pin 1 (D1).`

| Pin | Header | Besides `Pin` (input, output, `Watch`) |
|---|---|---|
| 0, 1 | D0, D1 | The serial port, USART0: RX and TX, the console's while the program runs |
| 2, 3 | D2, D3 | Edge interrupts of their own for `Pin.Watch...` (INT0, INT1); D3 is a PWM output (Timer2) |
| 4, 7, 8 | D4, D7, D8 | |
| 5, 6 | D5, D6 | PWM outputs (Timer0) |
| 9 | D9 | A PWM output timed to the half microsecond (Timer1): the pin for a servo |
| 10 | D10 | SPI's usual chip select (an output, high, once `Spi.Open` runs); no PWM output: its timer output is the timer event's |
| 11, 12, 13 | D11, D12, D13 | SPI's TX (MOSI), RX (MISO) and SCK; D11 is a PWM output (Timer2), D13 the LED "L" |
| 14 to 17 | A0 to A3 | Analog inputs (`Adc`) |
| 18, 19 | A4, A5 | Analog inputs; the I2C bus, SDA and SCL (bus 0) |

`Pin.Input(pin)` leaves a pin high impedance, with no pull-up, as on every chip. A button between a pin and GND, the
usual Arduino wiring (`INPUT_PULLUP`), asks for the pull-up: `Pin.Input(pin, Pull.Up)` sets the pin's bit of its port's
PORT register (PORTB for D8 to D13, PORTC for A0 to A5, PORTD for D0 to D7), as `examples/uno-button` does. A button to
5 V needs a resistor to GND instead: the ATmega328P's pins have no pull-down, so `Pull.Down` is refused (`"pullDown":
false`).

```csharp
Pin.Input(2, Pull.Up);     // D2's pull-up on: high until the button takes it to GND
Pin.WatchFalling(2);       // the [Interrupt(Event.Pin)] method runs at each press
```

### The chip's timers, clock and buses

The runtime is two, as the Pico's: `avr` for any AVR chip (start-up, the vector table, the 8-bit math helpers, float in
software, the failure stop) and `atmega328p` for the chip at 16 MHz (pins, the clock, the events, USART0, the TWI,
SPI, the ADC, PWM).

- **Interrupts**: `Pin.Watch...` on D2 and D3 uses INT0 and INT1, whose edges the chip keeps; on every other pin the
  pin change interrupt of its port, whose handler compares the levels and runs the `[Interrupt(Event.Pin)]` method for
  the edges watched (a pulse shorter than a few microseconds may pass unseen). Each event's interrupt comes through the
  runtime, which checks that the event happened and then calls the method, within some 50 microseconds
  (`"interrupts"` in `atmega328p.json`: numbers past the chip's vectors, as the Pico's uart event).
- **Timers and the clock**: Timer1 counts half microseconds (16 MHz / 8) for the clock, the delays and the timer event
  (its compare B), so the three keep the same time: `Timing.EveryMilliseconds(40)` comes every 40 ms to the
  microsecond, and a delay takes what it is given and a few microseconds more for the call. With the count of its
  rounds it makes 48 bits of half microseconds since start-up (they go round after 4.4 years): `Stopwatch` (1 MHz, as
  on the Pico), `Environment.TickCount` and `DateTime.Now` (from 1 January 2000, or the date `Clock.Start` gives) read
  it. Timer0 and Timer2 make the other PWM outputs.
- **The console** is USART0 on D1 (TX) and D0 (RX) at 115200 baud: 117647 really, 2.1% fast, with U2X as the Arduino
  core's `Serial.begin(115200)` has it, which the Arduino IDE's serial monitor and the USB chip take. A byte goes out
  when the one before has (no send buffer, no RAM taken); `Console.Read` and `Uart.Open(0, 1, 0, baud)` (489 to
  2000000 baud) receive into a buffer of 64 bytes, filled by the receive interrupt, as the Arduino core's. A failure
  prints .NET's message there, then stops.
- **I2C** (`I2c`) is the TWI, bus 0, SDA on A4 and SCL on A5, with their pull-ups on: 490 Hz to 400 kHz, the nearest
  at or below the frequency (100 and 400 kHz exactly), the same `true` and `false` as on the Pico (no answer, a refused
  byte, a bus held for longer than a byte's limit), a repeated start for `WriteRead`.
- **SPI** (`Spi`) is bus 0, SCK on D13, TX (MOSI) on D11, RX (MISO) on D12, modes 0 to 3, at 16 MHz / 2, 4, 8, ...,
  128 (8 MHz to 125 kHz), the fastest at or below the frequency. D10 (SS) becomes an output, high, as the Arduino
  core's `SPI.begin` makes it: as an input that goes low it would make the chip a device; it is the usual chip select.
- **Analog inputs** (`Adc`) are A0 to A5, read in the chip's own 10 bits, 0 to 1023 against the 5 V supply (AVcc), as
  Arduino's `analogRead` reads them: `Adc.Read` is the chip's range (the Pico's is 0 to 4095), and `Adc.Volts` the
  volts on both (reading * 5 / 1024 here). A reading takes 104 microseconds, with interrupts on (a handler that reads
  one meanwhile gets its own); `Adc.Temperature` is the chip's sensor against its 1.1 V, by the datasheet's typical
  readings (352 at 25 °C, 1.28 a degree), up to 10 degrees off. A4 and A5 are the I2C bus's while it is open.
- **PWM** (`Pwm`) is on D3, D5, D6, D9 and D11. D5 and D6 are Timer0's, D3 and D11 Timer2's: 8-bit timers, so a few
  frequencies, the nearest to the one asked for, from 30.6 Hz to 62.5 kHz (Timer0: 62500, 31373, 7813, 3922, 977,
  490, 244, 123, 61 and 31 Hz; Timer2 also 1953, 980, 488 and 245), a timer's two pins at its one frequency, the duty
  cycle in 256 steps (255 phase correct). Timer1 is the clock's, counting half microseconds for ever, so D9 is made by
  its compare A, which sets and clears the pin at the very count of each edge while its interrupt sets the next: any
  frequency from 1 Hz to 10 kHz, the duty cycle to the half microsecond, the pin for a servo (`Pwm.Output(9, 50)` and
  `Pwm.PulseMicroseconds(9, 1500)` are 20 ms and 1.5 ms exactly; on Timer0 and Timer2, 50 Hz would be 61 Hz in 64 us
  steps). A pulse or a gap shorter than 20 us is not made on D9 (the pin stays low, or high), and a long interrupt
  handler of the program's delays the edge after it. D10, Timer1's compare B, is the timer event's, and no PWM output.
- **ChipSharp.Components** runs as it is: `Lcd` on A4 and A5 (a 5 V LCD needs no level shifter on the Uno),
  `SevenSegment` on any pins, `Ds1307`, and a 74HC595's patterns over SPI.

### What runs on the Uno

On the simulated Uno, every sample and every program of the test corpora that makes no object prints exactly what
.NET prints (numbers, floats and their text, structs, enums, `ref`, arrays on the stack and in static fields,
constant tables, spans, generics over values, tuples, record structs, nullables, fixed and printed text, `TimeSpan`,
`DateTime`, `Math`), and where .NET throws it says .NET's message. Those that make objects are refused, place by place,
as the Uno has no heap; here the Pico traffic light written with classes (`examples/pico-traffic-light-m3`), first
lines:

```
error: PicoTrafficLightM3.dll uses 9 C# features that ChipSharp does not compile yet (54 places):
  generics over classes (a class, an interface, object or an array as a type argument) need a heap, and the target 'uno' (2 KB RAM) has no heap: 31 places in the top-level statements (IL_0000, IL_0012 and IL_0024), ... Use numbers, bools, enums and structs as type arguments: ChipSharp makes the code once for each.
  creating objects (new ...) needs a heap, and the target 'uno' (2 KB RAM) has no heap: 5 places in the top-level statements (IL_000d, IL_001f, IL_002a and 2 more). Use structs and static methods.
  ...
```

`examples/uno-traffic-light` is the same traffic light written with no heap (record structs in static tables, a
struct, static classes, a method for the event, literals and numbers on the LCD), and prints the same.

What things take of the 31.5 KB of flash and the 2 KB of RAM:

| Program | Flash | RAM |
|---|---|---|
| A blink (`Pin.Output`, `Pin.Toggle`, `Timing.DelayMilliseconds`) | 1.2 KB | 15 bytes |
| A few chars to the console | 1.4 KB | 30 bytes |
| A serial echo (`Uart.Open`, `Console.Read`, `Uart.WriteByte`), the receive buffer's 64 bytes among its RAM | 2.9 KB | 90 bytes |
| A 74HC595 over SPI | 4.2 KB | 20 bytes |
| `Lcd` showing a line and a number | 5.8 KB | 76 bytes |
| A servo sweep on D9; with a potentiometer on A0 and the angle on the console (`examples/uno-servo`) | 5.9 KB; 9.6 KB | 48; 98 bytes |
| The time from a `Ds1307` into `Clock.Start` | 8.2 KB | 90 bytes |
| The traffic light with an LCD, a 7-segment display and a button (`examples/uno-traffic-light`) | 13.0 KB | 136 bytes |
| The date and the time on an LCD, `TryFormat` with names of days and months (`examples/uno-lcd-clock`) | 26.0 KB | 96 bytes |
| The samples `Square`, `Structs`, `Spans` and `TrafficLights` | 1.7, 2.4, 5.5 and 8.2 KB | 20, 32, 66 and 68 bytes |
| The samples `PrintedText`, `FloatMath` and `TimeSpans` | 13.4, 17.1 and 22.1 KB | 70, 20 and 23 bytes |

### Limits on the Uno

- **No heap**: 2 KB of RAM, 512 bytes of it the stack's. Classes, strings that are kept, boxes, delegates and lambdas,
  collections, `throw` and `Console.ReadLine` (which makes a string; read char by char with `Console.Read`) are
  refused by `chipsharp check -t uno` and `build`, place by place, each with what to write instead.
- **31.5 KB of flash**: doubles are in software, and compiler-rt's add, multiply and divide take 3 to 4 KB each on an
  AVR, so `MathFunctions`, a program of every member of `DateTime`, and `Math.Sin`, `Cos`, `Tan`, `Atan2`, `Asinh`,
  `Acosh`, `Log`, `Log2` and `Pow` with the text of doubles do not fit (each gives the PC's bits all the same, on an AVR
  core of 64 KB); a program too large is refused by the memory report, with both sizes. Floats and doubles cost time
  too: every operation of theirs is a call into that software arithmetic.
- **`nint` and `nuint` are 16 bits**, the AVR's addresses (-123456 does not fit), and **a struct has no padding**:
  nothing is aligned on an AVR, so `struct { byte; ushort; int; }` takes 7 bytes, where the Pico and .NET take 8
  (see [differences](#where-a-program-behaves-differently)).
- **PWM** on five pins only: D5 and D6 share Timer0's frequency, D3 and D11 Timer2's, both a few frequencies only; D9
  is exact, 1 Hz to 10 kHz; D10 is no PWM output. `Pin 6 shares Timer0 with pin 5, which runs at 1000 Hz, ...`
- **The ADC** reads 0 to 1023 against 5 V (`Adc.Volts` gives the volts as on the Pico).
- **One** serial port, one I2C bus and one SPI bus; the console's port is D0 and D1.
- **No stack check yet**: a recursion with no end grows the stack into the static fields and the program goes wrong,
  where the Pico says `Stack overflow.` and stops.

### How it works on an AVR

- **The code**: the `llvm` code generator writes the AVR's data layout (2-byte pointers, nothing aligned; C#'s `int`
  stays 32 bits, `long` and `double` 64), and keeps every constant in flash, none of it copied to the 2 KB of RAM: the
  literals, tables, spans of constant data, enum and date names. The AVR reads flash only with its own instruction
  (LPM), so an address of flash data has bit 15 set (0x8000 plus its flash address: RAM's addresses never have it); a
  read whose address the compiler cannot place tests the bit, the others read flash or RAM straight away. The runtime
  reads what the program hands it so too. The capability `"separateFlash": false` keeps constants as ordinary data
  instead, which start-up copies to RAM, as avr-gcc does without `PROGMEM`. The runtime's C takes C#'s `int`, `char`
  and `bool` as 32 bits (C's `int` has 16 on an AVR) and keeps its own text and tables in flash, `Math`'s tables
  (22 KB) too.
- **8-bit arithmetic**: an `int` takes four of the AVR's registers and four instructions for every addition, and C#
  makes nearly everything an `int`, loop counters and bytes on IL's stack included; so on the AVR ChipSharp does the
  arithmetic in 8 or 16 bits wherever it proves the values fit and the result is C#'s ([narrowing](#narrowing)). The
  samples' code takes 5% less flash, loops of counters and small numbers up to half less (ForLoops: 310 bytes instead
  of 588).
- **What LLVM 22 gets wrong on an AVR**, and is kept from: a copy of LLVM's own from flash (a loop over a table made
  into `memcpy`, which would read RAM), so `memcpy` and `memmove` are no builtins in the program; a call that passes
  arguments on the stack from a function without a frame pointer, which sets the stack up through r31:r30 while it may
  hold a value, so such a function gets one (a byte of stack); and, in a function of thousands of instructions with
  hundreds of bytes spilled, its register allocation may spill a stale byte of a register pair (the machine verifier
  says "Using an undefined physical register"): so a function with a loop and more than 100 instructions is never
  inlined on an AVR (LLVM would merge every function called once into its caller), and the target turns LLVM's
  machine-sink pass off (`-mllvm -disable-machine-sink` in `avr.json`), where it showed in one such function too. LLVM
  itself is not changed. Each of these has a test on the Uno.
- **Zig**: the clang inside Zig compiles the runtime's C and the program's LLVM IR with `-mcpu=atmega328p
  -mmcu=atmega328p` (both: `-mmcu` alone would be the generic avr2, without `mul`, `jmp` and `movw`), and its ld.lld
  links them with the target's linker script.

### Examples for the Uno

Built with `chipsharp build bin\Release\net10.0\<Name>.dll -t uno` in their folders, or flashed with
`chipsharp flash ... -t uno`; each has a `diagram.json` for Wokwi.

| Example | What it does | Uno |
|---|---|---|
| `uno-blink` | Blinks the LED "L" (D13) with a delay loop; in Wokwi, an LED on D13 and the GND beside it blinks with it | 1.2 KB, 15 bytes of RAM |
| `uno-timer-blink` | Blinks the LED "L" from the timer event (Timer1), while `Main` does nothing; the same circuit | 1.6 KB, 24 bytes of RAM |
| `uno-button` | A button between D2 and GND toggles the LED "L" through the pin event (INT0, falling edges); `Pin.Input(2, Pull.Up)` turns D2's pull-up on, so the button needs no resistor | 1.6 KB, 26 bytes of RAM |
| `uno-serial` | A count on the console (USART0, 115200 baud) every second, and what is typed into it sent straight back (`Uart.Available`, `ReadByte`, `WriteByte`); in Wokwi, the serial monitor under the Uno | 5.4 KB, 132 bytes of RAM |
| `uno-traffic-light` | The Pico traffic light with an LCD brought to the Uno, with no heap: one signal (A0, A1, A2) for two lanes, the turn's countdown on a 7-segment display (D3, D4, D7 to D11, `SevenSegment`), a 16x2 LCD on I2C (`Road 1   turn 8s`, `green         4s`), a button on D2 that cuts a green short, the LED "L" blinking from the timer event; over the serial port exactly what the Pico's traffic lights print, line for line | 13.0 KB, 136 bytes of RAM |
| `uno-lcd-clock` | The date and the time on a 16x2 LCD, once a second, from a DS1307 read at start-up (`Clock.Start(Ds1307.Read())`) or a date it sets a halted one to; `TryFormat` into a span for `Lcd.Line`; both on I2C (A4, A5) | 26.0 KB, 96 bytes of RAM |
| `uno-servo` | A servo on D9 follows a potentiometer on A0 (`Adc.Read`, then `Pwm.PulseMicroseconds` of 1 to 2 ms at 50 Hz), and each change of 5 degrees goes to the console | 9.6 KB, 98 bytes of RAM |

The circuits are wired directly (Wokwi needs no resistors; a real LED or segment needs one of 220 ohm), with no wire
across a part: the LEDs' and the display's commons on GND, the button between D2 and GND, the LCD in I2C mode and the
DS1307 on 5V, the servo and the knob on 5V. Where two parts' pins come in another order than the Uno's (an LCD's SDA
above its SCL, A4 to the left of A5), two wires cross each other. In the simulator they all run as the tests run them
(`node tools\avr-sim\run.mjs ...`, as each `Program.cs` says): the traffic light with
`--input D2=low@2500ms --input D2=high@2700ms --i2c 0:0x27=lcd1602`, the clock with `--i2c 0:0x27=lcd1602
--i2c 0:0x68=ds1307`, the servo with `--adc A0=2.5V`.

## PFS154 and STM8S103 (through C and SDCC)

Milestone M6 builds for chips no LLVM supports through C: a code generator writes C, and the toolchain `sdcc` compiles
it with [SDCC](https://sdcc.sourceforge.net/) 4.6, the Small Device C Compiler, into an Intel HEX file. The two chips
are the Padauk PFS154 (`pfs154-bare`: PDK14, 2K words of 14-bit program memory, 128 bytes of RAM) and the STM8S103F3
(`stm8s103-board`, the blue board: 8 KB of flash, 1 KB of RAM, the LED on PB5). Their target files give the chips as
data: memory, pins, ports, registers or peripherals, interrupts, the sizes of SDCC's C types (`"c"`), and what SDCC
lacks (`"double64": false`: its `double` is 32 bits; on the PDK14, `"recursion": "reentrant"`). Both have their
runtimes: see [The PFS154's runtime](#the-pfs154s-runtime) and [The STM8S103 board](#the-stm8s103-board).

The C code generator (`"backend": "c"`, `src/ChipSharp.Backend.C`) is the `llvm` one's twin behind the same contract:
one `.c` file of C99 per program, with the same symbols for the runtime's functions, the same layout of structs,
objects and type descriptors, and the same `chipsharp_entry`. The C means what the C# means whatever the compiler's
`int` (16 bits with SDCC):

- Only fixed-width types: every integer is the unsigned type of its width, so `+`, `-` and `*` wrap around as in C#;
  8- and 16-bit products and left shifts are done as `unsigned` (C would make them a signed `int`, which may overflow);
  a signed division, remainder, shift right or comparison casts its operands to the signed type. Shift amounts are
  masked below the width, as C# masks them. A float becomes an integer through a helper that saturates as .NET does
  (C leaves a value out of range undefined); `%` on floats is `fmod`.
- A struct is a C struct, an array a struct around a C array (so it is a value in C too). The file checks, when it is
  compiled, that the compiler lays each out as TIR does, and that it has two's complement, an arithmetic shift right and
  an `int` of at most 32 bits (and the C type sizes the target file gives): a compiler that differs stops with the
  check's name rather than running a program that reads the wrong bytes.
- Constant data (string literals, tables, type descriptors) is `static const`: SDCC keeps it in code memory on the
  PDK14 and reads it through its generic pointers (bit 15 set), so an address of it is an ordinary `uint8_t *`. A target
  may name a qualifier more for it (`"c": { "constQualifier": "__code" }`).
- On the PDK14 the functions that may run again while they run (recursion, a function an interrupt handler and `Main`
  both call) or that are called through an address are `__reentrant`. A program that uses `double` is refused where
  `double` has 32 bits (`samples/MathFunctions ... uses double (C#'s 64-bit floating point), and target
  'stm8s103-board' compiles C whose double has 32 bits`): write `float` and `MathF`. On the big-endian STM8 a constant
  string's 2-byte chars are written high byte first.
- On the STM8 (`"c": { "structReturn": "pointer" }`) a function that returns a struct (or an array value) gives it
  through a pointer, its first parameter `cs_result`, which the caller points at the slot it stores the result in, or
  at its own `cs_result` for `return F(...)`; elsewhere it returns it as C does (on the PDK14 that keeps the copy in
  SDCC's own code: a function that copies a struct calls `memcpy`, and SDCC overlays the locals only of the functions
  that call nothing).
- `chipsharp_entry` returns an `int32_t`, a C# int, as the hardware contract has it. On the PDK14 a function an
  interrupt runs and Main does not keeps locals of its own (`#pragma nooverlay`): SDCC overlays the locals of the
  functions that call nothing, and one an interrupt runs would write over Main's.
- Only what the program can reach is written: a function, a global, a block; and a branch on a constant goes one way
  only, so the code of `if (typeof(T) == typeof(double))` in an instance of generic code for `int` (which the frontend
  makes `false`, in ChipSharp.Library's own generic code too) is not written, nor the double it uses: `Array.Sort` of
  ints needs no double on a chip that has none.
- SDCC 4.6 makes wrong code of some C, which the C steers around, each found by the conformance suite below and kept
  by it: `&`, `|` and `^` of two 64-bit values it knows while compiling are worked out in 32 bits
  (`(uint64_t)-1099511627776 | 255` is 255), so with SDCC they go through a function; on the STM8 its peephole
  optimizer drops the test after `ld a, xl`, which sets no flags (`(byte)(n % 7) == 0` held for every n), which the
  toolchain mends with a rule of its own (`--peep-file`); on the STM8 a function that returns a struct gets the
  address of its struct parameter wrong (hence `cs_result`); a local that copies a parameter is put where the parameter is, so its
  starting zero overwrote the parameter (a slot the function stores to first gets no zero); a struct passed as
  `*(struct S *)p` is passed wrong (only a slot is passed as it is, anything else through a copy); and on the STM8 a
  function whose frame its 8-bit offsets do not reach cannot return 3 or 4 bytes (`"c": { "largeFrame": 255 }`: such
  a function returns them through a pointer too).
- `"stackCheck": true` on both chips: the functions that may call themselves (and on the STM8, every frame on its
  stack, those whose locals and parameters take 256 bytes or more) check, when they start, that one more call of
  them has room, with the address of a byte of their frame (4 instructions on the STM8, 5 on the PDK14), and past the
  limit call the runtime's `chipsharp_stack_overflow` ("Stack overflow.", then the failure stop). The PDK14's stack
  grows up to the end of RAM (`"c": { "stack": "up" }`), the limit a quarter of `link.stack` (at least 4 bytes: 12 on the PFS154) before it; the STM8's grows down toward
  the variables, the limit their end: the program's own, as much again as a quarter of `link.stack` for the runtime's,
  and that quarter.
- `"narrowing": true` on both chips: arithmetic in 8 and 16 bits where that gives C#'s result (see
  [Narrowing](#narrowing)), which SDCC's 8-bit code needs: on the STM8, 4% less flash for the 59 programs of the corpora
  that fit both ways (137,824 bytes instead of 143,947; ForLoops 652 instead of 1,196, IfElse 812 instead of 1,008), on
  the PFS154 5% less program memory for its 10 (5,073 words instead of 5,360; Square 309 instead of 463). The suite's
  `SdccNarrowingTests` measures it.
- Target `host-c` builds the C for the PC with clang (and `-fno-strict-aliasing`: C# may read memory as another type
  than it wrote, as the LLVM IR allows). Every sample built for it, with narrowing and without, prints what .NET prints;
  every sample that needs neither a heap nor `double` compiles with sdcc for both chips without a warning.

**The backend conformance suite** (`tests/ChipSharp.Tests/Backends`) shows the backends interchangeable: every program of
every corpus the differential tests compare with .NET (the samples, the language corpora, the library tests, the Uno's,
the narrowing's edges, and the small chips' own, `Chips/...`, of the features without a heap written without a double)
goes through every backend of `BackendTargets` (`host`: llvm; `host-c`: C and clang; `pfs154-bare` and
`stm8s103-board`: C and SDCC, in `chipsim`; `esp32c5-devkitc`: llvm for riscv32, built as `chipsharp build` builds it
and run in `chipsim esp32c5`, the console the USB Serial/JTAG port, with no skips) and must print what .NET prints, byte for byte. A program a target cannot
take is skipped, and the suite checks each skip's reason against its list (`BackendSkips`), so that the list can
neither grow nor keep a program that runs: on the PC none; on the chips those that need a heap (65), use `double` (25),
call the runtime's float math (2) or fixed text's searches (1), or do not fit (the PFS154: 18 in its 128 bytes of RAM,
31 in its 2K words; the STM8: 6 in its 8 KB), or whose recursion outgrows the stack (2 and 1). Three run to the end and
print otherwise, by what the chip is: `nuint` and `nint` have 16 bits (2), and the STM8's `MemoryMarshal` is big-endian
(`samples/Spans`). On the chips the program runs with a startup and a console of the tests' own (the PFS154 has no
console): each `Console.Write` reports its value to the test
(through two variables and a hook in the simulator), which writes its text as .NET does. The PFS154's programs link with
16 bytes of RAM for the stack, not the 48 its target reserves: they are shallow, and the test's console takes RAM. A new backend adds its targets to the list, and its skips.

The toolchain `sdcc`:

- SDCC is found by the environment variable `CHIPSHARP_SDCC` (`sdcc.exe` or its folder), then its install folder
  `C:\Program Files\SDCC\bin`, then the PATH. It runs with its own folder first on the PATH, where its preprocessor
  finds `cc1`.
- Each `.c` file is compiled on its own (`sdcc -mstm8 --opt-code-size -c`), each `.s` assembled (`sdasstm8`), into a
  `.rel` kept in the build cache; the file with `main()` is linked first (SDCC puts the start-up code there).
- SDCC's linker keeps every file it is given whole, and has no weak symbols: the runtime's files a program may use
  nothing of (a `GeneratedFile` the runtime marks `Library`) go into a library (`<name>.lib`, made with `sdar`), from
  which the linker takes a file only for a symbol nothing else defines. So a program has only the runtime it uses, as
  `--gc-sections` gives the other chips, and a file linked whole takes the place of a library file that defines the
  same symbol (a chip's `chipsharp_chip_init`, the architecture's empty one).
- The memory report comes from SDCC's map: `flash 238 bytes of 8 KB (2.9%), RAM 12 bytes of 1 KB (stack 256 bytes
  reserved)` for the tests' blink in C, and on a Padauk chip in words, two bytes of the `.ihx` each: `program 174 words
  of 2048 words (8.5%), RAM 27 bytes of 128 bytes (stack 48 bytes reserved)`. A program too big for the target's memory
  fails with how much it needs, as on the other chips. The `.hex` is SDCC's `.ihx` in address order (byte addresses:
  a Padauk chip's word n at bytes 2n and 2n + 1, as the Easy PDK programmer reads it).
- **The simulator, `chipsim`** (`src/ChipSharp.Simulators`, ChipSharp's own instruction-set simulators, in C#): `chipsim
  pfs154 Blink.ihx --ms 1000` (or `stm8s103`; `esp32c5` runs an `.elf` or an image, see [ESP32-C5](#esp32-c5);
  `ch32v003` a `.bin`, `.hex` or `.elf`, see [The CH32V003 in chipsim](#the-ch32v003-in-chipsim); without installing,
  `src\ChipSharp.Simulators\bin\Release\net10.0\chipsim.exe`)
  runs a `.ihx` and prints what the Uno's simulator prints: every pin change (`[   12.000 ms] PA4 high`), UART1's lines
  (`UART1: Hello`) and baud rate, a timer's pin as PWM periods, what the buses do, and `stopped: <why>`.
  `--input PA3=low@100ms` drives a pin, `--send "on\n@100ms"` sends to UART1's RX (the STM8S103's PD6), `--uart1 file`
  keeps what it sent; the Pico's and the Uno's devices, in C# (`src/ChipSharp.Simulators/Devices`, from
  `tools/rp2040-sim/devices`, for any chip of chipsim's), go on the STM8S103's buses as they go on theirs:
  `--i2c 0:0x27=lcd1602`, `--i2c 0:0x68=ds1307@2026-11-27T18:00:00`, `--i2c 0:0x68=mpu6050`, `--spi 0=74hc595@PC4`,
  `--spi 0=loopback`; and `--adc PD3=1.65V@10ms` sets an analog input (or a reading, `PD3=512`). A run ends after
  `--ms`, or when the program has finished (it sleeps with nothing left to wake it: `stopsys`, `stopexe`; `halt`, `wfi`),
  or as a failure, exit code 3: the PFS154's `reset` instruction (the PDK14 has no breakpoint: a runtime's fatal error)
  or its watchdog (on after reset), the STM8's `break`, an instruction or address the chip does not have, memory the
  image does not fill, an interrupt with no handler. The PFS154: the whole PDK14 instruction set but `mul`, Timer16,
  Timer2 and Timer3 (PWM), the PA0 and PB0 interrupts, the system clocks of CLKMD (the IHRC at 16 MHz, the ILRC at about
  55 kHz), the ports with their pull-ups. The STM8S103: every instruction and addressing mode, the interrupt controller
  and its priorities, EXTI, TIM1, TIM2 (PWM), TIM4, UART1, the I2C controller (RM0016's events: SB, ADDR, AF, TXE, BTF,
  RXNE with ACK and POS, STOP, SWRST, each step at the bus's clock), the SPI controller, ADC1 (AIN2-AIN6 against
  3.3 V), the clock's dividers (2 MHz after reset) and the LSI, the IWDG. Cycles are the datasheets'; the tests run it
  in process (`Simulation.Run`).

### The PFS154's runtime

`chipsharp build -t pfs154-bare` builds C# for a PFS154 at 2 MHz: the runtimes `pdk14` (`src/ChipSharp.Runtime.Pdk14`:
the start-up, the one interrupt handler, the stops and failures) and `pfs154` (`src/ChipSharp.Runtime.Pfs154`: the
hardware contract's functions of the chip, from the headers `chips/pfs154.json` makes), registered as `avr` and
`atmega328p` are. A blink (`Pin.Toggle` and `Timing.DelayMilliseconds(500)` in a loop) takes 276 words of program memory
and 9 bytes of RAM; the runtime's own RAM is a few bytes for each part a program uses.

| What | On the PFS154 |
|---|---|
| Pins | PA0, PA3-PA7, PB0-PB7 as numbers 0, 3-7, 8-15 (port * 8 + bit); a number no pin has is ignored. `Pin.Input` has no pull; `Pin.Input(pin, Pull.Up)` turns the pin's pull-high on (PAPH, PBPH), in a file of its own that a program without it does not link; the chip has no pull-low (`"pullDown": false`) |
| Delays, clock | T16 counts microseconds (the IHRC / 16, whatever the system clock), and its interrupt its rounds: 48 bits of microseconds for the delays, `Stopwatch`, `Environment.TickCount64`, `DateTime.UtcNow` and `Clock.Start` (`"clock": true`) |
| Timer event | `Timing.Every...` and `After...` on the clock's time; TM2 wakes the handler up when it has come, to the microsecond (`interrupts.timer`: TM2's request bit 6) |
| Pin event | PA0 and PB0, the two pins with an interrupt (`interrupts.pin`); `chipsharp check` refuses another pin given as a constant (`"pinFunctions": { "pinEvent": [0, 8] }`), the runtime stops at one that is not |
| PWM | Two outputs at once (`"pwm": 2`): TM3 on PB5, PB6 or PB7, TM2 on PA3, PB2 or PB4 while the timer event is off; the nearest frequency the timer makes (1008 Hz for 1000), 4 Hz to 250 kHz. `Pwm.Duty` takes a double, which SDCC has not: `chipsharp check` says so |
| Registers | The I/O space, 0x00 to 0x3f |
| No console | No UART, no software one: a failure, and a mistake (a pin that cannot do what it is asked), stops at the `reset` instruction, the message .NET would give in a comment of the runtime |

- **The start-up**, before `main()` and with interrupts off, is easypdkprog's: the factory's trim of the IHRC (`call
  0x7ed; mov ihrcr, a`), the system clock (`CLKMD` 0x3c: IHRC / 8, the watchdog off), then the ten words of its
  calibration marker for the board's `clockHz` at 5.0 V, which easypdkprog finds and trims; in chipsim they are ANDs
  that change nothing. Then `chipsharp_chip_init` starts T16, `main()` turns interrupts on, runs the program and stops
  for good: `stopsys`, with no pin left to wake the chip.
- **The interrupt.** The PDK14 has one vector. Its handler (`pdk14_interrupt.s`) saves A, FLAG and SDCC's `p`, then
  runs what each file that handles an interrupt adds to its area `INTSRC` (the clock's round, the timer, the pins), so a
  program has the handlers of what it uses and no others; each calls the program's `[Interrupt]` method's entry,
  `chipsharp_isr_irq<n>`.
- **Assembly.** The functions a handler may call while `Main` is in them have their parameters on the stack
  (`__reentrant`), which SDCC's C reaches with several instructions a byte, and SDCC keeps every value its C works out
  in RAM of its own: so the runtime is mostly assembly. Its entries copy their parameters with interrupts off and work
  in 8 shared bytes; a pin changes with one `xor` or with interrupts off around a read and a write, so a handler that
  changes another pin of the port loses nothing.
- **The conformance suite** runs on `pfs154-bare` in chipsim: 7 cases pass (the pins, the pin event, the timer event
  (Every, After), PWM and the failure stop) and 2 are skipped, saying why: the delays' and the clock's. The cases link
  with 16 bytes of RAM for the stack, not the target's 48, as the backend suite's programs do. Their C# (`Timing.Delay` of a
  `TimeSpan`, `DateTime`) does not fit 2K words and 128 bytes as the `c` code generator writes it for SDCC, which keeps
  each function's values at addresses of their own; the runtime's delays and clock pass tests of their own.

### The STM8S103 board

`chipsharp build -t stm8s103-board` builds C# for the blue STM8S103F3P6 board at 16 MHz (the HSI, undivided from
start-up on): the runtimes `stm8` (`src/ChipSharp.Runtime.Stm8`: `main()` and so SDCC's start-up and vector table, the
stops, the failures' messages, the interrupts' helpers, a 64-bit multiply nine times faster than SDCC's) and
`stm8s103f3` (`src/ChipSharp.Runtime.Stm8s103`: the hardware contract's functions of the chip, from the headers
`chips/stm8s103f3.json` makes), registered as `avr` and `atmega328p` are. Every file of theirs but the start-up is a
library file, and a file whose parts a program may use one without the other is cut into modules at its `//@module`
lines (the failures one each, the pins, the delays, the clock, the buses, and the shared console, strings and
exceptions, which compile with SDCC too): a program has only what it uses.

| What | On the STM8S103 board |
|---|---|
| Pins | Port * 8 + bit: PA1-PA3 are 1-3, PB4 and PB5 12 and 13 (the LED, on when low: PB4 and PB5 are true open-drain), PC3-PC7 19-23, PD1-PD6 25-30 (PD1 is SWIM); a number no pin has is ignored. `Pin.Input` has no pull (CR1 clear, PD1 too); `Pin.Input(pin, Pull.Up)` sets the port's CR1 bit, in a module of its own that a program without it does not link; the chip has no pull-down (`"pullDown": false`) |
| Delays, clock | TIM1 counts microseconds (16 MHz / 16), its update interrupt its rounds: 48 bits for the delays, `Stopwatch`, `Environment.TickCount64`, `DateTime.UtcNow` and `Clock.Start`. TIM4 is the program's |
| Timer event | `Timing.Every...` and `After...` on TIM1's compare 1, at the clock's microsecond (`interrupts.timer`: 31) |
| Pin event | Every pin: EXTI of its port, both edges, the handler running the method for the edges watched (`interrupts.pin`: 30) |
| Console, Uart | UART1, TX on PD5, RX on PD6: the console at 115200 baud (115108 really, 0.08% slow) and serial port 0 at 245 to 1000000 baud; `Console.Read` and the port receive into a buffer of 32 bytes, filled by the receive interrupt (`interrupts.uart`: 32) |
| I2C | Bus 0, SDA on PB5 (the LED's pin) and SCL on PB4, which need the bus's pull-ups: 1954 Hz to 100 kHz in standard mode (100 kHz exactly), 381 kHz at 400; RM0016's sequences, a refusal or a device holding the clock longer than two bytes gives up |
| SPI | Bus 0, SCK on PC5, TX on PC6, RX on PC7 (either -1 for none), modes 0 to 3, 16 MHz / 2 to 256 |
| ADC | AIN2-AIN6 on PC4, PD2, PD3, PD5, PD6, in 10 bits against VDD (3.3 V) |
| PWM | TIM2's channels on PD4, PD3 and PA3, at one frequency (1 Hz to 8 MHz, the nearest), each pulse to a microsecond at up to 1 kHz. `Pwm.Duty` takes a double, which SDCC has not: `chipsharp check` says so |
| RAM | 1 KB, 256 bytes of it the stack's: a blink takes 16 bytes, the serial echo below 70 |

- **Sizes.** 8 KB of flash: a blink (`Pin.Toggle`, `Timing.DelayMilliseconds(500)`) takes 760 bytes and 16 bytes of
  RAM; a serial echo (`Uart.Open`, `Console.Read`, `Uart.WriteByte`) 2.8 KB and 58 bytes, the receive buffer's 32 among
  them; `Lcd` showing text and a number 7.7 KB; the conformance suite's clock case (`Stopwatch`, `DateTime` and
  `Clock.Start`) 7.9 KB. C#'s 64-bit arithmetic is SDCC's helpers (a division some 0.2 ms), and `DateTime`'s code is
  large: `Ds1307.Read` and `Write` do not fit with it (13 KB), its `Open` and `Running` do.
- **Interrupts.** Every interrupt keeps the priority it has after reset, so a handler is never interrupted; the
  runtime's handlers of TIM1, EXTI and UART1 are in every program's vector table, and go to the code of the event a
  program uses through a hook it sets (6 bytes of RAM). `halt` (the program ended) turns interrupts on again by itself,
  so before it the runtime turns off every pin's interrupt: nothing is left to wake the chip.
- **chipsim** runs it all: the conformance suite's 16 cases pass on `stm8s103-board`, I2C, SPI and the ADC with the
  devices and voltages of `--i2c`, `--spi` and `--adc`.

### Limits of the two chips

`chipsharp check -t pfs154-bare` (or `-t stm8s103-board`) names each of these places before SDCC runs, with the call
and what to write instead, as it names a heap or a console a target lacks (see
[below](#when-chipsharp-check-says-no)); `build` says the same:

| What the program does | What `check` says |
|---|---|
| Uses `double` (a literal, a local, a parameter, a result, a field, a call that takes or returns one) | `double (C#'s 64-bit floating point) is not available, and the target ... compiles C whose double has 32 bits`: use `float` and `MathF`. A `double` in ChipSharp's own library code, which only the C code generator sees, stops the build with the same words |
| Calls `Pwm.Duty` (it takes a `double`) | `Pwm.Duty takes a double, and the target ... has no 64-bit double`: use `Pwm.PulseMicroseconds` |
| Makes an object, keeps a string, boxes a value, builds a collection, calls `Console.ReadLine` | Needs a heap, which neither chip has (`Console.ReadLine`: read char by char with `Console.Read` into a `stackalloc` span) |
| Calls `Console.Write` or `Console.Read` on the PFS154 | The PFS154 has no console: it has no UART, and no software one |
| Watches a pin other than PA0 (0) or PB0 (8) on the PFS154 | `pin 3 (PA3) cannot be watched (the pin event) ...`: the two pins with an interrupt |
| Gives a pin, a port or a bus the chip does not have as a constant | The pins `"pinFunctions"` lists for it (a PWM output, an analog input, UART1's TX) |

What only the build knows is the size. A program that does not fit stops the build with how much of each region it needs
and the three biggest functions, e.g. `Big does not fit in target 'pfs154-bare': its code, constants and the starting
values of its variables need 3057 words of program memory, 1009 words more than the target's 2048 words. The biggest
functions: Program_Draw (1500 words), ... Make the program smaller, or build it for a chip with more memory.` A RAM
overflow counts the target's stack reserve too (48 bytes on the PFS154, 256 on the STM8S103). The PFS154's 2K words and
128 bytes are small enough that the library decides what fits (see [the gaps](#what-does-not-fit-the-small-chips)).

### Flashing the PFS154 and the STM8S103

`chipsharp flash <program.dll | Blink.hex> -t pfs154-bare` (or `-t stm8s103-board`) runs the board's programmer tool,
as `-t uno` runs avrdude; neither tool comes with ChipSharp:

| Board | Hardware | Tool, and where to get it | What runs |
|---|---|---|---|
| `pfs154-bare` | The [Easy PDK programmer](https://github.com/free-pdk/easy-pdk-programmer-hardware) (Free-PDK's open hardware; the chip in its socket), on USB as a serial port (USB 0483:5740) | `easypdkprog`, [its releases](https://github.com/free-pdk/easy-pdk-programmer-software/releases) (`EASYPDKPROG_WIN_<date>_1.3.zip`): found by `CHIPSHARP_EASYPDKPROG`, the PATH, `D:\Tools\easypdkprog`, `%LOCALAPPDATA%\Programs\easypdkprog`, `Program Files\easypdkprog` | `easypdkprog -n PFS154 -p COM7 write Blink.hex`: erase, write, verify, then the program's calibrations |
| `stm8s103-board` | An ST-LINK V2 (a clone will do), wired to the board's SWIM header: 3.3V, SWIM (PD1), GND, NRST | `stm8flash`, [built from source](https://github.com/vdudouyt/stm8flash) (libusb-1.0), or the `stm8flash.exe` the [sduino](https://github.com/tenbaht/sduino) core installs in Arduino15: found by `CHIPSHARP_STM8FLASH`, the PATH, `D:\Tools\stm8flash`, sduino's | `stm8flash -c stlinkv2 -p stm8s103f3 -s flash -w Blink.hex`; then the chip restarts and runs it |

- **The port.** Without `--port` the PFS154's flash takes the one serial port with the programmer's USB ids (or, where
  no port's ids are known, lets easypdkprog ask each port). The ST-LINK is a USB device, not a port: stm8flash finds it
  itself, `--port` is its serial number when several are plugged in (`stm8flash -c stlinkv2 -L` lists them), and the
  flasher checks which ST-LINKs are plugged in: a Nucleo's ST-LINK/V2-1 or an STLINK-V3 alone is taken instead
  (`-c stlinkv21`, `stlinkv3`), and a failure says whether there is none, or one Windows has no driver for (it needs
  WinUSB: ST's STSW-LINK009, or Zadig).
- **A locked STM8.** Many blue boards come read-out protected: stm8flash then fails, and the error gives the command
  that unlocks the chip, which erases it: `stm8flash -c stlinkv2 -p stm8s103f3 -u`. Every other failure says what is
  likely wrong (no programmer, another IC in the socket, the SWIM wires) and shows the tool's last lines.
- **The PFS154's clock.** easypdkprog trims the internal oscillator (IHRC) after writing when the program asks with
  Free-PDK's calibration marker (`EASY_PDK_CALIBRATE_IHRC`: ten `and a, #k` words in the start-up, for the board's
  `clockHz` at 5.0 V): it runs the chip, tunes IHRCR, and writes `mov a, #trim; mov ihrcr, a` in their place. ChipSharp
  never turns that off (`--nocalibrate`) and prints each result (`IHRC calibrated: 1998750 Hz for 2000000 Hz at 5.00 V
  (IHRCR 0x84)`). Without it (in chipsim) the chip keeps the factory's trim, the `ret k` at word 0x7ed, which
  easypdkprog never erases. The PFS154 runtime's start-up holds both (`EasyPdkProgFlasher` documents the words). After
  writing, the chip runs once it is out of the socket (or in it: `easypdkprog start`).

### Examples for the PFS154

Built with `chipsharp build bin\Release\net10.0\<Name>.dll -t pfs154-bare` in their folders, or written into the chip
with `chipsharp flash ... -t pfs154-bare`; each has a `README.md` with its wiring and a `chipsim` command to run it
without a chip. They use the portable API alone (`Pin`, `Timing`, `[Interrupt]`, `Pwm`; no `Registers`), so the same
C# builds for the Pico or the Uno with other pin numbers.

| Example | What it does | PFS154 |
|---|---|---|
| `pfs154-blink` | Blinks an LED on PA4 with a delay loop | 276 words, 9 bytes of RAM |
| `pfs154-timer-blink` | Blinks the LED on PA4 from the timer event (TM2), while `Main` does nothing | 612 words, 23 bytes of RAM |
| `pfs154-button` | A button between PA0 and GND toggles the LED on PA4 through the pin event (falling edges); `Pin.Input(PA0, Pull.Up)` holds it high, so the button needs no resistor | 435 words, 12 bytes of RAM |
| `pfs154-pwm-fade` | Fades an LED on PB5 (TM3's PWM, 1008 Hz) up and down with `Pwm.PulseMicroseconds`, 20 us more or less every 20 ms | 872 words, 53 bytes of RAM |
| `pfs154-traffic-light` | The Uno's traffic light with what the chip has: red PA3, yellow PA6, green PA7, the turn's countdown on a 7-segment display (PB1 to PB7), a pedestrian button on PA0 that cuts a green short, a heartbeat LED on PA4 from the timer event; no console, no LCD, the steps a method's parameters (a table of record structs does not fit beside the stack) | 1432 words, 72 bytes of RAM |

The stack grows from the end of the variables to the end of the 128 bytes, and the target reserves 48 bytes for it
(`link.stack`): the traffic light's goes about 48 bytes deep when TM2's interrupt comes while `Main` writes a pin (the
calls of `Main`, the pin write's, then the interrupt, its handler and the calls of that), and its test checks the room it
leaves. A program whose variables take more than 80 bytes does not link for the target: the memory report and the
error say so (see [Limits of the two chips](#limits-of-the-two-chips)). chipsim stops a run whose stack
grows past the end of RAM. The tests (`Pfs154ExampleTests`) build each example as above and run it in chipsim: the
button with presses (`--input PA0=low@200ms`; the pull-up holds PA0 high) and the traffic light with
`--input PA0=low@2500ms --input PA0=high@2700ms`, as each `Program.cs` says.

### Examples for the STM8S103 board

Built with `chipsharp build bin\Release\net10.0\<Name>.dll -t stm8s103-board` in their folders, or written into the
chip through an ST-LINK V2 with `chipsharp flash ... -t stm8s103-board`; each has a `README.md` with its wiring, by the
labels on the board, and a `chipsim` command to run it without a board. They use the portable API alone (`Pin`,
`Timing`, `[Interrupt]`, `Console`, `Adc`, `I2c`, `Pwm`, and `SevenSegment` and `Lcd` from ChipSharp.Components; no
`Registers`), so the same C# builds for the Pico or the Uno with other pin numbers.

| Example | What it does | STM8S103 |
|---|---|---|
| `stm8-blink` | Blinks the board's LED (PB5, lit while low) with a delay loop | 762 bytes, 16 bytes of RAM |
| `stm8-serial` | `pico-uart-echo` with no heap: a prompt on UART1 (PD5, PD6; 115200 baud) whose `on`, `off` and `status` switch the LED and say what it is; the line read char by char into a `stackalloc` span, each char sent back as it comes, a `switch` on the span | 6.7 KB, 64 bytes of RAM |
| `stm8-traffic-light` | The Uno's traffic light without its LCD: red PA1, yellow PA2, green PA3, the turn's countdown on a 7-segment display (PC3 to PC7, PD2, PD3), a pedestrian button on PD4 (to GND, with `Pull.Up`) that cuts a green short, the board's LED blinking from the timer event; over UART1 exactly what the Pico's and the Uno's traffic lights print, line for line | 6.4 KB, 54 bytes of RAM |
| `stm8-lcd` | A potentiometer on PD3 (AIN4) as its reading and voltage on a 16x2 LCD at 0x27 on I2C (SDA PB5, SCL PB4): `0512 = 1.650 V`, the digits written as chars | 7.5 KB, 45 bytes of RAM |
| `stm8-pwm-fade` | `pfs154-pwm-fade` on PD4: TIM2's PWM at 1000 Hz, its pulse 20 us longer or shorter every 20 ms (`Pwm.PulseMicroseconds`) | 3.3 KB, 23 bytes of RAM |

What 8 KB holds decides how they are written. The Uno's traffic light, with its table of record structs, its signal
and display passed from call to call and `Timing.Delay` of a `TimeSpan`, takes 10.8 KB as C for SDCC (9.0 KB without
the `TimeSpan`, whose code is 64-bit arithmetic): the STM8's is written as the heap-free Pico one is, the steps a
method's parameters and the parts static, so that no table is built and no struct copied from call to call. `Lcd` and the I2C bus take some 7 KB, and `Lcd.Write` of a
number beside the ADC goes just past the 8 KB: `stm8-lcd` writes its digits a char at a time, in fixed places. The
board's LED is PB5, which is also I2C's SDA: with the LCD on the bus it shows the bus's traffic. The traffic light's
button is to GND with `Pull.Up` (the STM8 has no pull-down). The tests (`Stm8ExampleTests`) build each example as above
and run it in chipsim as each README says: the prompt with commands sent to PD6, the traffic light with
`--input PD4=low@2500ms --input PD4=high@2700ms`, the LCD
with `--i2c 0:0x27=lcd1602 --adc PD3=...`.

## ESP32-C5

Milestone M4 (done) is Espressif's ESP32-C5 (`esp32c5-devkitc`, the ESP32-C5-DevKitC-1): one RISC-V core, RV32IMAC with
Zicsr and Zifencei (no float hardware), up to 240 MHz, 384 KB of SRAM, flash and PSRAM in the module. It is built the
way the Pico is, through the `llvm` code generator, whose `riscv32` output (triple `riscv32-unknown-elf`, the ilp32 ABI,
4-byte pointers) clang compiles with the chip's `"march"` and `"mabi"` (`-march=rv32imac_zicsr_zifencei -mabi=ilp32`,
ESP-IDF's own: the core's Zcmp is faulty and not used), and ld.lld links. It builds to a linked `.elf`, which
`chipsim esp32c5` runs, and to the ESP image (`.bin`), which esptool flashes. On it run the whole language, the heap and
the garbage collector, and `ChipSharp.Hardware`'s pins with pulls, events, timers, the clock, the console, a serial
port, I2C, SPI, the ADC and PWM; every program of the backend conformance suite prints what .NET prints on the simulated
chip. What is not there, and what has not been tried on a real board, is listed in [The ESP32-C5 limits](#the-esp32-c5-limits).

```powershell
cd examples\esp32c5-blink
dotnet build -c Release
chipsharp build bin\Release\net10.0\Esp32c5Blink.dll -t esp32c5-devkitc      # chipsharp-output\esp32c5-devkitc\Esp32c5Blink.bin
chipsharp flash bin\Release\net10.0\Esp32c5Blink.dll -t esp32c5-devkitc      # builds, then esptool writes it
chipsim esp32c5 chipsharp-output\esp32c5-devkitc\Esp32c5Blink.bin --ms 1600  # or run it in the simulator
```

- **The board's ports.** Two USB-C ports. The one marked **USB** is the chip's own USB Serial/JTAG (`303A:1001`) and
  is the **console**: `Console.Write` goes to it, `Console.Read` and `Console.ReadLine` read it (USB has no baud rate, so
  any terminal setting works). The one marked **UART** is a bridge (CP2102N) on UART0, GPIO 11 (TX) and GPIO 12 (RX),
  which is `Uart.Open(0, 11, 12, baud)` (the only port, 100 to 3,000,000 baud, from the crystal). Either port flashes the
  board; plug in **USB** to see the program's text and **UART** for a serial device on `Uart`. The program has both. If
  esptool cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**.
- **The clock.** The CPU runs at the crystal's **48 MHz**, as the ROM leaves it (`"clockHz": 48000000` in the board
  file). `160000000` is an opt-in for an image that a bootloader started with the PLL already running (the CPU then moves
  to PLL_F160M); when the CPU is not on a PLL at start-up the program says so and stops, because ChipSharp does not turn the
  480 MHz PLL on by itself (see [the limits](#the-esp32-c5-limits)) and the chip has no register that says whether it
  runs. Any other `clockHz` is refused by the build. Delays, `DateTime.Now` and `Stopwatch` follow SYSTIMER (16 MHz), so
  they are the same at either clock.
- **The pins.** `Pin`, `Pwm` and the buses take **GPIO 0-12 and 23-28** (the board file's `"pin"`); GPIO 13 and 14 are
  the USB port's D- and D+, and GPIO 15-22 the module's flash and PSRAM, so `chipsharp check` refuses them. It refuses
  too an analog input outside **GPIO 1-6** (ADC1) and UART0 pins other than 11 and 12. Caveats, all in the file: GPIO
  2, 3, 7 and 25-28 are strapping pins, read at reset, so what is wired to them must not hold a level that picks another
  boot mode (GPIO 28 low is the download mode, and the Boot button is on it); GPIO 2-5 are the JTAG pins (JTAG is on the
  USB port by default, so they are free); GPIO 0 and 1 are the 32 kHz crystal's pins, free on the devkit; GPIO 7 has no
  internal pull resistors; GPIO 11 and 12 are UART0's, which the bridge uses; GPIO 27 is also the RGB LED's data pin (see
  the limits). Inputs have `Pull.Up` and `Pull.Down` (the chip has both, so `check` allows both), and every allowed pin
  has pin events (`Pin.Watch`, `[Interrupt(Event.Pin)]`).
- **The heap.** 224 KB (`"heap": "224K"`) and a stack of 16 KB, in the one SRAM region of 369.4 KB (`0x40800000` to
  `0x4085c5a0`) that holds the program too. A program that makes no object gets no heap: its code, constants and
  variables may take up to 353.4 KB; with the heap, up to 129.4 KB. A larger program stops the build and says by how
  much: `Big does not fit in target 'esp32c5-devkitc': its code, constants and variables (431.7 KB) and the stack (16 KB,
  link.stack) need 447.7 KB of SRAM, 78.3 KB more than the target's 369.4 KB.`
- **Facts as data.** `targets/chip/esp32c5.json` says what the chip is, each fact with its source (the datasheet and
  TRM, ESP-IDF's sources, esptool's): the memory map, the CLIC (48 interrupt ids, 16-47 the CPU's 32 lines, to which
  the interrupt matrix routes the 84 peripheral sources), the watchdogs the ROM leaves running, the boot flow. Its
  registers come from Espressif's SVD through `src/ChipSharp.Chips` (`chips/esp32c5.json`), into
  `esp32c5_registers.h`, `esp32c5_interrupts.h` and `esp32c5_pins.h`.
- **The boot.** `chipsharp build` writes two images that differ only in segments: `Name.rom.bin` boots **directly**:
  the ROM reads it at flash offset `0x2000`, checks its header (chip id 23), loads its segments into SRAM and jumps to
  its entry; ESP-IDF's second-stage bootloader is not used (`chipsharp flash` writes this one there). `Name.bin` is an
  **app behind ESP-IDF's bootloader**, at `0x10000` (what Wokwi always does with an uploaded `.bin`, and what a devkit
  that still has its factory bootloader and partition table does): the bootloader checks the image (below), copies its
  SRAM segment and calls the entry. The build prints both paths, the memory report and a line saying which is which. Either way every byte loaded is in
  SRAM (no flash cache or MMU to set up), up to `0x4085c5a0`, where the ROM keeps its stack and data while it boots
  (`0x4084bba0` when ESP-IDF's bootloader runs: its own code is there until it jumps). The linker script has code and
  variables in one region (`"link.code"` and `"link.data"` both `sram`), so `.data` is loaded where it runs, then `.bss`
  and the stack (16 KB) up to `0x4085c5a0`.
- **The application description.** The first 256 bytes of the program, at `0x40800000`, are ESP-IDF's `esp_app_desc_t`
  (`esp32c5_app_desc.c`, the target's `"link.head"`): magic `0xABCD5432`, secure version 0, version `1.0`, the project
  name (the program's name, filled in by the image writer), the compile time and date (fixed text, `00:00:00` and
  `Jan  1 1970`, so a build is reproducible), `ChipSharp` as the IDF version, the SHA-256 of the `.elf`, eFuse block
  revisions 0 to 65535 (both mean "any"), MMU page size 2^16. ESP-IDF's bootloader reads it from the image's first
  segment (`esp_image_format.c`) and refused an image that had program code there ("Image requires efuse blk rev >=
  v176.67"); the ROM ignores it, so both images have it. After the SRAM segment `Name.bin` has two zero-length
  segments at flash-mapped addresses (`0x42...`), because the C5's bootloader asserts that an app has exactly two
  (`unpack_load_app`); `Name.rom.bin` has none, only the program's segments, as esptool's `elf2image` writes them (the
  ROM, whose handling of mapped addresses is not known, never sees them). `EspIdfBootloaderRules` checks every rule of the
  bootloader (header, chip and eFuse revisions, the segments' addresses against the bootloader's own SRAM,
  checksum, SHA-256, the mapped segments); a program that loads past `0x4084bba0` is a build error.
- **The image `esp`** (`src/ChipSharp.Image.Esp`): `chipsharp build -t esp32c5-devkitc` writes `Name.bin`, the ESP
  application image the ROM loads, as esptool's `elf2image` writes it: the 8-byte header (`0xE9`, the segment count,
  SPI flash mode `dio`, the flash size (8 MB, the module's) and frequency (40 MHz) in byte 3, the entry point,
  `chipsharp_reset`); the 16-byte extended header (WP pin `0xEE`, drive strengths 0, **chip id 23**, minimum revision 0,
  maximum 65535, hash appended); each segment (load address, length, bytes, padded to 4); zeros up to byte 15 of a
  16-byte block, the checksum (`0xEF` xor every segment byte); the SHA-256 of all before it. A segment outside
  `0x40800000-0x4085c59f` is a build error. The values are the target's `"esp"` settings; `EspImage.Read` parses and
  verifies an image. `esptool image-info` shows it valid, with its three segments (the two mapped ones empty); it prints
  an "Application information" only for a flash-mapped segment with data, which this image does not have. The memory report reads `SRAM 5.2 KB of 369.4 KB (1.4%) (stack 16 KB reserved)`.
- **Flashing.** `chipsharp flash Name.dll -t esp32c5-devkitc` (or the `Name.rom.bin`) runs
  `esptool --chip esp32c5 -p <port> -b 460800 write-flash 0x2000 Name.rom.bin` (`write_flash` for esptool 4: `esptool
  version` says which). esptool is found through `CHIPSHARP_ESPTOOL` (the file or its folder), the PATH (`esptool`,
  `esptool.py`), `D:\Tools\esptool\Scripts`, then Python's module (`py -m esptool`); install it with `py -m pip install esptool`. Plug in either
  USB-C port of the board: the one marked **USB** (the chip's own USB Serial/JTAG, `303A:1001`, which `chipsharp
  flash` takes first) or the one marked **UART** (the CP2102N bridge, `10C4:EA60`; CH343 `1A86:55D3` and CH340
  `1A86:7523` too); say which with `--port COM6` when both are plugged in. esptool puts the chip into the ROM's
  download mode itself; if it cannot connect, hold **BOOT**, press and release **RESET**, release **BOOT**, and flash
  again. The chip then runs the program from reset. Flashing at `0x2000` replaces ESP-IDF's bootloader; to keep one,
  write `Name.bin` at `0x10000` of a flash that has the bootloader and the partition table.
- **Wokwi.** Upload `Name.bin`, not the `.rom.bin` ("Upload Firmware and Start Simulation..."): Wokwi's C5 board always runs it as
  an app at `0x10000` behind ESP-IDF's own bootloader, so a merged whole-flash image gains nothing. The bootloader's log
  (`load:0x408556c0`, `entry 0x4084bba0`) is its own. `chipsim esp32c5 Name.bin --idf-bootloader` runs the app as that
  bootloader leaves the chip (the PLL on and the CPU at 160 MHz, the RTC watchdog running for 9 s, the bootloader's
  registers) after checking the image against the bootloader's rules.
- **The runtime `riscv32`** (`src/ChipSharp.Runtime.Riscv32`), every RV32 chip's: `chipsharp_reset` (gp, the stack,
  interrupts off, mtvec at the trap entry, the chip's first steps, `chipsharp_chip_boot`, then .data, .bss,
  `chipsharp_chip_init` and the program); the trap entry (`.vectors`, 64-byte aligned) and its dispatch by interrupt id
  to `chipsharp_isr_irq<n>`, as many as the chip's `"vectors"` (48); the 64-bit division and memcpy and friends (its
  own: with M, they are all RV32IMAC lacks) and compiler-rt's soft float (the armv6m runtime's files); the failure stop,
  `ebreak`, and the end, `wfi`; the heap's needs (the callee-saved registers and the stack to `__chipsharp_stack_top`)
  for a chip that gets a heap. The runtime `esp32c5` adds the chip's first steps: the RTC watchdog, both timer groups'
  and the super watchdog stopped (ESP-IDF's sequence, with their write keys), mtvec in CLIC mode.
- **Tests.** The image of a known `.elf` byte by byte (headers, chip id, segment table, checksum, SHA-256 worked out
  again), the reader, the SRAM-range error; esptool's command lines (4 and 5), its discovery order, the port by USB
  ids, a flash with a stand-in esptool; the target files, the flags and the linker script; the `.elf` of a program (every loaded segment in SRAM
  below the ROM's data, the trap entry at `0x40800000`, the entry point `chipsharp_reset`; `llvm-objdump` and
  `llvm-readobj` find RV32IMAC code, the start-up's gp, sp and mtvec, no float or Zcmp instruction); every program of
  the corpora without objects compiles and links for the board, each with objects is refused for the heap; the C5's
  headers compile with clang. The conformance suite has the chip's slot (`Esp32c5ChipSimulator`, which runs the image
  in chipsim). The whole language runs on it: all 159 programs of the backend conformance suite, heap and `double`
  included, print what .NET prints (`unalignedAccess` stays false: code that is right on a core that traps is right on
  one that splits the access, and the manual is not read here to say which the C5 is).
- **The simulator** (`chipsim esp32c5 program.elf`, in `src/ChipSharp.Simulators`: `RiscV` and `Esp32c5`) runs an ELF
  file or an ESP image as the ROM leaves the chip. The core: RV32IMAC, Zicsr, Zifencei, Zcb, Zcmp and Zcmt (Zcmp per
  its spec, not the C5's faulty one; no Xhwlp), machine and user mode, the CLIC's traps (mtvec in CLIC mode, the
  vector table at mtvt, mcause's previous level, mintstatus, mintthresh, mnxti), 1 cycle an instruction and the TRM's
  extras (a taken branch 1, jalr 2, mul 1, a division 1-18 by its quotient, a peripheral access 3). The chip, from the
  TRM, ESP-IDF's headers and the SVD (`Esp32c5Map`): HP and LP SRAM, the CLIC and CLINT, the interrupt matrix (a
  source's map register holds the CLIC id, 16 + the line), GPIO and IO MUX (outputs, the pads' pull-ups and pull-downs, open drain, edge and
  level interrupts on `--input GPIO9=low@5ms`), SYSTIMER (16 MHz, three alarms), UART0 (FIFOs, the baud from PCR and
  CLKDIV, its interrupts; `--send`), the USB Serial/JTAG port (`USB: ...` lines, `--send USB=...`), MWDT0/1, the RTC
  watchdog and the super watchdog (the ROM leaves MWDT0's and RWDT's flash boot protection and the super watchdog on:
  each stops the run unless the program turns it off or feeds it), PCR's clocks (XTAL 48 MHz after reset, the PLL's
  160 and 240 MHz at `BUS_CLOCK_UPDATE`, `CPU clock 240 MHz`), and a few ROM functions in C# (`ets_printf`,
  `ets_delay_us`, ...; another ROM call stops the run). It stops as a failure at `ebreak`, an exception without a trap
  handler or a watchdog's reset; it has finished at `wfi` with interrupts off (or with none that can come). About 70
  million instructions a second. Its tests run every instruction form clang assembles, every compressed one against
  its expansion, 2,688 results of C's arithmetic, and C programs for each peripheral.

- **I2C, SPI, ADC and PWM**, the same `ChipSharp.Hardware` API and contract functions as on the other chips,
  all clocked from the crystal (read at run time, 48 MHz on the devkit) and reaching any pin through the GPIO matrix
  (`esp32c5_i2c.c`, `esp32c5_spi.c`, `esp32c5_adc.c`, `esp32c5_pwm.c`; they poll, no interrupt id is used):
  - **I2C**: bus 0, the one HP controller (`I2C0`), as a controller, 200 Hz to 1 MHz (the divider of ESP-IDF's
    `i2c_ll_master_cal_bus_clk`, so 100 and 400 kHz are exact), SDA and SCL any of the board's pins, open-drain with
    their pull-ups on, up to 32 bytes a command list (longer transfers are several lists joined by END), a refused
    address or byte or a held clock gives `false` after the controller is reset and a STOP sent.
  - **SPI**: bus 0, GP-SPI2, modes 0-3, 46875 Hz to 26 MHz (the highest at or below the one asked for), SCK, TX and RX
    (or -1) any of the board's pins, 64 bytes a transaction.
  - **ADC**: ADC1 one-shot on GPIO1-GPIO6 (12 bits, 0 to 4095, 12 dB attenuation, `Adc.Reference` 3.3 V, uncalibrated),
    and `Adc.Temperature` from the chip's sensor; the board leaves GPIO1-GPIO6 (GPIO2 and GPIO3 are strapping pins).
  - **PWM**: the LEDC's 6 channels on up to 4 frequencies (1 Hz to 1 MHz, the finest duty resolution that fits), on any
    pin the board leaves; `Pwm.Duty`, `Pwm.PulseMicroseconds`, `Pwm.Stop` gives the pin back as an input.
  `chipsim esp32c5` models them with the shared devices: `--i2c 0:0x27=lcd1602`, `--spi 0=74hc595@GPIO23`,
  `--adc GPIO5=1.65V@10ms`, and a PWM output is measured on its pin (`GPIO4 PWM 1000.00 Hz, duty 25.00% ...`); the
  conformance suite's I2C, SPI, ADC and PWM cases run on `esp32c5-devkitc` (SDA GPIO4, SCL GPIO5; SCK GPIO0, MOSI GPIO1,
  MISO GPIO24; ADC GPIO3; PWM GPIO7), and `Lcd`, `Ds1307` and `SevenSegment` run unchanged. `chipsharp check` refuses
  a constant frequency outside these ranges (PWM 1 to 1,000,000 Hz, I2C 200 to 1,000,000, SPI 46,875 to 26,000,000).

### Examples for the ESP32-C5

Built with `chipsharp build bin\Release\net10.0\<Name>.dll -t esp32c5-devkitc` in their folders, or flashed with
`chipsharp flash ... -t esp32c5-devkitc` (through the USB-C port marked **USB** or **UART**, BOOT and RESET if esptool
cannot connect); each has a README with its wiring and a `chipsim esp32c5` command. They use the portable API alone
(`Pin`, `Timing`, `[Interrupt]`, `Console`), so the same C# builds for the Pico by changing the pin numbers and `-t`, on
pins the board leaves a program (the board's own LED is a WS2812, not a pin to switch; the LCD traffic light also uses pins 0, 1, 2, 3, 11 and 12 for its 7-segment display, as its README explains).

| Example | What it does | Image, SRAM |
|---|---|---|
| `esp32c5-blink` | Blinks an external LED on GPIO 4 with a delay loop | 1.8 KB, 2.1 KB |
| `esp32c5-button` | A button between GPIO 5 and GND (`Pull.Up`) toggles the LED on GPIO 4 through the pin event | 1.8 KB, 2.0 KB |
| `esp32c5-serial` | A command prompt on the USB console (`on`, `off`, `blink 200`, `status`) with `Console.ReadLine` and a `Dictionary<string, Action<string>>` | 21.3 KB, 22.6 KB + heap |
| `esp32c5-timer-blink` | Blinks the LED from the timer event while `Main` prints the uptime from a `Stopwatch` and `DateTime.Now` | 13.1 KB, 13.5 KB + heap |
| `esp32c5-classes` | A two-lane traffic light (6 LEDs, a button on GPIO 9 that cuts a green): `readonly record struct Lane`, a `Signal` class, an iterator over the steps, `List<Lane>`, status lines on the console | 13.7 KB, 15 KB + heap |
| `esp32c5-lcd-traffic-light` | The `PicoTrafficLightLcd` traffic light as it is (only pins and the I2C bus differ): `List<Lane>`, `record struct`s, an iterator, a 16x2 LCD (I2C 0x27), a 7-segment turn countdown, a heartbeat on the timer event and a button on the pin event; same console and LCD text as on the Pico | 20.2 KB, 21.6 KB + heap |
| `esp32c5-servo` | A servo on GPIO 6 sweeping 0 to 180 degrees with `Pwm.PulseMicroseconds` at 50 Hz | 7.0 KB, 7.3 KB |
| `esp32c5-lcd-clock` | A DS1307 and a 16x2 LCD on I2C bus 0 (SDA 4, SCL 5) show the date and the time, with `DateTime` | 25.5 KB, 26.0 KB + heap |

Wokwi has a part for the board (`board-esp32-c5-devkitc-1`, beta), so each example has a `diagram.json`, wired directly;
their circuits have not been tried in Wokwi (upload the example's `.bin`, see the Wokwi line of the ESP32-C5 chapter).

### The ESP32-C5 limits

What the ESP32-C5 does not do, and what has not been tried on a board. A limit a program can reach is refused by
`chipsharp check` or the build before the program runs, unless the row says otherwise.

| What | Where it stands |
|---|---|
| Wi-Fi 6, Bluetooth LE, Thread and Zigbee | Out of scope: Espressif's radio drivers are closed binaries, and there is no API for them |
| The CPU's speed | 48 MHz (the crystal) until the BBPLL bring-up (ESP-IDF's `rtc_clk_bbpll_enable` and `rtc_clk_bbpll_configure`) can be tried on a board; `"clockHz": 160000000` is only for an image started with the PLL running, and the build refuses any other value |
| The RGB LED (a WS2812 on GPIO 27) | No API yet: it is not a pin to switch, and the examples use an external LED on GPIO 4 |
| The 8 MB of PSRAM, and flash execute-in-place | Unused: the program and the heap live in the SRAM (369.4 KB usable; the largest image is 353.4 KB without a heap, 129.4 KB with the 224 KB heap) |
| The LP core and deep sleep | Not used |
| The ADC | Readings are uncalibrated (12 bits, 12 dB attenuation, `Adc.Reference` 3.3 V); the temperature sensor's accuracy (`Adc.Temperature`) is the sensor's own, uncalibrated |
| `Console.ReadLine` | Does not echo what is typed: turn on the terminal's local echo |
| `float` and `double` | In software, bit for bit like .NET, and slower than on a chip with an FPU |
| Pins | GPIO 13-22 are not available (USB and the module's flash and PSRAM); the strapping pins are read at reset (see above); UART0 only on GPIO 11 and 12 |
| Frequencies | PWM 1 Hz to 1 MHz (6 outputs on up to 4 different frequencies), I2C 200 Hz to 1 MHz, SPI 46,875 Hz to 26 MHz, UART0 100 to 3,000,000 baud. A constant outside is refused by `check`; one that only the running program knows stops it with a message |
| Memory | A program that does not fit the SRAM stops the build, saying how much it needs; it does not name the biggest functions, as the SDCC chips' message does |
| **Not yet tried on a real board** | The start-up on silicon (the ROM loading the image, the watchdog stops, the CLIC), flashing (tested against a stand-in esptool; `write-flash` never run), the ADC's power-up (the modem front end and PWDET registers, as ESP-IDF's sources say), the I2C recovery after a NACK, and the Wokwi diagrams |

## CH32V003

Milestone M5 (under way) is WCH's CH32V003 (`ch32v003-evt`, WCH's own evaluation board CH32V003F4P6-R0-1v1): one
QingKe V2A core, RV32EC (16 registers, compressed instructions, Zicsr; no multiply or divide instruction, no float),
48 MHz, 16 KB of flash and 2 KB of SRAM, so no heap. It is built the ESP32-C5's way, through the `llvm` code generator
(triple `riscv32-unknown-elf`, `-march=rv32ec_zicsr -mabi=ilp32e`: the ilp32e ABI, whose stack is 4-byte aligned)
and ld.lld, with the chip's facts as data (`targets/chip/ch32v003.json`, each with its source: WCH's reference manual
V1.5, datasheet V1.8, QingKe V2 processor manual V1.1 and its own start-up files) and its registers from WCH's SVD
(`chips/ch32v003.json` over `chips/svd/CH32V003xx.svd`, as ch32fun keeps it, into `ch32v003_registers.h`,
`ch32v003_interrupts.h` and `ch32v003_pins.h`). Wave 0 has the target files, the start-up and the helpers, and
`chipsim ch32v003` (see [The CH32V003 in chipsim](#the-ch32v003-in-chipsim)); wave 1 the chip's runtime, which
implements the hardware contract: the pins and their pulls (up and down), the delays, the clock and the timer event on
SysTick, the pin event on EXTI, and USART1 as the console both ways, serial port 0 and the uart event. Every program
of the corpora that makes no object compiles and links for the chip, but six too large for its flash; the build writes
the image `bin` and `chipsharp flash` writes it through a WCH-LinkE (see
[Flashing the CH32V003](#flashing-the-ch32v003)). I2C, SPI, the ADC and PWM come with wave 2: until then the target
says it has none, and `chipsharp check` refuses a program that uses them.

- **Memory.** The program is linked at the flash, `0x08000000`, where a programmer writes it: the vector table, the trap
  entry, the code and constants, then `.data`'s starting values. The core starts at the flash's alias, `0x00000000`, and
  the table's first instructions jump to the reset handler at its flash address, where the program then runs. The SRAM,
  `0x20000000`, has `.data`, `.bss` and, in its last 512 bytes (`"link.stack"`), the stack. A program that does not fit
  stops the build and says by how much.
- **The start-up.** The first 156 bytes of flash are the vector table (`ch32v003_vector_table.S`, the target's
  `"link.head"`): entries 0 and 1, where the core starts, are an absolute jump to `chipsharp_reset` (`lui`, `jalr`);
  entries 2 to 38 hold the address of the riscv32 runtime's trap entry, so every interrupt and exception reaches its
  dispatch by `mcause` (`chipsharp_isr_irq<n>`, the PFIC's 39 numbers: `"vectors": 39`). The chip's first steps
  (`ch32v003_boot.c`) clear INTSYSCR (no hardware prologue/epilogue, which clang cannot compile handlers for, and no
  nesting: the trap entry saves the registers), put `mtvec` at the table in vectored mode with the handlers' addresses
  (`| 3`, WCH's own), and raise the clock from the 8 MHz of reset (the HSI's 24 MHz divided by 3) to 48 MHz: one flash
  wait state, the AHB prescaler off, the PLL from the HSI, then the PLL as the system clock. Neither watchdog runs at
  reset (the IWDG only if the option bytes say so; ChipSharp leaves them alone).
- **The runtime `riscv32e`** is the riscv32 runtime's E variant (`src/ChipSharp.Runtime.Riscv32`): the same start-up,
  dispatch, failures and compiler-rt float; its trap entry saves RV32E's ten caller-saved registers (40 bytes), and
  `riscv32e_arithmetic.c` has what LLVM calls without M: `__mulsi3`, `__divsi3`, `__udivsi3`, `__modsi3`, `__umodsi3`
  and `__muldi3` (a division by zero gives what M's would: the code generator checks first). WCH's XW instructions
  (compressed byte and half-word loads and stores, clang's `xwchc`) would take 1.2% off the corpora's code and are not
  used.
- **The runtime `ch32v003`** (`src/ChipSharp.Runtime.Ch32v003`) is the hardware contract on the chip, from the
  generated headers, and keeps the 2 KB small: every function in a section of its own, so a program links only what
  it uses, and the handlers of the interrupts the runtime takes (SysTick's, USART1's), which the trap's dispatch names
  in every program, reach the timer event's and the receiving's code and variables through hooks their modules put in.
  A blink takes 52 bytes of SRAM (the runtime's count of time, its deadlines and hooks), the serial prompt 88.
  - **Pins** by port * 8 + bit, as the STM8's: PA1 and PA2 are 1 and 2, PC0-PC7 16-23, PD0-PD7 24-31. PD1 is SWIO, the
    one debug wire a WCH-LinkE programs the chip through, and PD7 NRST: the runtime leaves both alone (as a pin the
    chip does not have, so no program cuts the chip off from its programmer), and `chipsharp check` refuses them as
    constants (`"pinFunctions"`); on the evaluation board PA1 and PA2 too, which it wires to its crystal. `Pin.Output`,
    `Input`, `Write`, `Toggle` and `Read` through CFGLR, BSHR, BCR, OUTDR and INDR. Plain `Pin.Input` is floating;
    `Pull.Up` and `Pull.Down` are CFGLR's input with a pull, OUTDR's bit choosing which (`"pullDown": true`).
  - **The pin event** on EXTI's lines 0-7 (EXTI7_0, vector 20, the target's `"interrupts.pin"`): line n watches pin n
    of the port AFIO_EXTICR names, for the edges RTENR and FTENR give, so the chip watches **one pin of each number at
    a time** (PC3 or PD3, not both): watching the second while the first is watched stops the program, saying which
    to unwatch.
  - **Time** is SysTick's: it counts at HCLK / 8 (6 MHz, six counts a microsecond) from 0 at start-up, in 32 bits the
    runtime makes 64 by counting its rounds whenever it reads it. Its one compare is armed for the nearest of a delay's
    end, the timer event's time and a guard 2^31 counts ahead (so the count is read, and its rounds counted, at least
    every 358 s even when nothing else reads it). The delays sleep (`wfi`) until it; `Stopwatch`, `Environment.TickCount64`
    and `DateTime` (from `Clock.Start`) read the count; the timer event (`Timing.Every...`, `After...`) runs on its
    interrupt, numbered past the PFIC's (`"interrupts.timer"`: 39) as the Uno's and the STM8's are, since the runtime's
    SysTick handler decides when the event has come. TIM1 and TIM2 stay free for PWM.
  - **USART1** on PD5 (TX) and PD6 (RX), its default pins, 8N1 at 48 MHz / BRR (733 to 3000000 baud; 115200 is BRR
    417, 0.08% slow; 9600 exact): the console (`Console.Write` and `WriteLine`, `Console.Read`, the failures' messages,
    .NET's words), `Uart` port 0 with a receive buffer of 16 bytes its interrupt fills, and the uart event (40, past the
    PFIC's: USART1's handler fills the buffer, then runs the event on a watched port). A WCH-LinkE's RX on PD5 brings
    the console to the LinkE's own USB serial port.
  - **The program's end** sends what USART1 still holds, then turns interrupts off (mstatus.MIE and the PFIC's), so the
    `wfi` of the stop sleeps for good: a program's events end with it, as on the Uno (a program whose events should go
    on keeps `Main` in a loop). A failure says why on the console, then stops at `ebreak`.
- **Tests.** The target files and the flags, the linker script; the `.elf` of a program (the vector table's words at
  `0x08000000`, the code in flash and the variables in SRAM, RV32EC code with no M instruction and no register above x15
  as `llvm-objdump` reads it, the ilp32e stack alignment in its attributes, the start-up, the 40-byte trap frame, the
  chip's first steps, helpers that call nothing); every corpus program; the helpers built for the PC against .NET's
  arithmetic; WCH's SVD and the headers, compiled with clang for RV32EC; the runtime linked with every function its
  target has. The conformance suite runs in `chipsim ch32v003` the 16 cases the target has the guards of (all but
  I2C, SPI, the ADC and PWM) and passes them; the examples run there too
  ([Examples for the CH32V003](#examples-for-the-ch32v003)).

### Examples for the CH32V003

Built with `chipsharp build bin\Release\net10.0\<Name>.dll -t ch32v003-evt` in their folders, or flashed with
`chipsharp flash ... -t ch32v003-evt` through a WCH-LinkE; each has a README with its wiring and a `chipsim ch32v003`
command. They use the portable API alone (`Pin`, `Timing`, `[Interrupt]`, `Console`, `Clock`), so the same C# builds
for the Pico by changing the pin numbers and `-t`. The board's LEDs are on its header P4, from VCC through 1K: a wire
from LED1 to PD0 lights it while PD0 is low.

| Example | What it does | Flash, SRAM |
|---|---|---|
| `ch32v003-blink` | Blinks the LED on PD0 with a delay loop | 2.3 KB, 52 bytes |
| `ch32v003-button` | A button between PD3 and GND (`Pull.Up`) toggles the LED through the pin event | 2.8 KB, 52 bytes |
| `ch32v003-timer-blink` | Blinks the LED from the timer event while `Main` prints the uptime (`Stopwatch`) on USART1 | 5.7 KB, 72 bytes |
| `ch32v003-serial` | The STM8's command prompt on USART1 (`on`, `off`, `status`), each char echoed as it is typed; no heap | 4.7 KB, 88 bytes |
| `ch32v003-clock` | The time once a second on USART1 from `Clock.Start` and `DateTime.Now`, with `Environment.TickCount64` | 6.1 KB, 56 bytes |

### Flashing the CH32V003

`chipsharp build ... -t ch32v003-evt` writes `Name.bin`, the image format `bin`: the raw bytes of the chip's 16 KB
of flash from 0x08000000, the sections at their load addresses (`.data`'s starting values in flash) and the gaps 0xFF,
as erased flash reads (what `llvm-objcopy -O binary --gap-fill 0xff` writes). A program bigger than the flash is
refused, saying by how much; so is one that loads a byte outside it. `chipsharp flash <Name.dll | Name.bin> -t
ch32v003-evt` then writes it through a **WCH-LinkE**, WCH's own debug probe (about $5), with
[wlink](https://github.com/ch32-rs/wlink):

```
wlink --chip CH32V003 flash --address 0x08000000 Name.bin
```

wlink checks that the chip on the wire is a CH32V003, unprotects a read-protected chip by itself (which erases it),
writes the flash and resets the chip, which runs the program.

- **The hardware.** A WCH-LinkE in its RISC-V mode (USB `1a86:8010`; its ARM mode, `1a86:8012`, cannot program a
  RISC-V chip: `wlink mode-switch --rv`, or WCH-LinkUtility, switches it). Three wires: its **SWDIO** to the chip's
  **SWIO, PD1** (the CH32V003 has one debug wire), **GND** to GND, and **3V3** (or **5V**: the chip runs on 2.7-5.5 V)
  to the board's VCC unless the board has power of its own. The LinkE's **RX** to the chip's USART1 TX (**PD5**) brings
  the console to the LinkE's own USB serial port, which `chipsharp flash` names after writing a board with a console.
- **wlink on Windows.** Download `wlink-<version>-win-x64.zip` from [its releases](https://github.com/ch32-rs/wlink/releases)
  and unpack it into `D:\Tools\wlink` (or put it on the PATH, or set `CHIPSHARP_WLINK` to `wlink.exe` or its folder;
  `cargo install --git https://github.com/ch32-rs/wlink` puts it in Cargo's bin, where ChipSharp looks too). The LinkE's
  interface 0 (the probe) needs a driver: WCH's own, which
  [WCH-LinkUtility](https://www.wch-ic.com/downloads/WCH-LinkUtility_ZIP.html) and MounRiver Studio install, or WinUSB,
  with [Zadig](https://zadig.akeo.ie) (Options > List All Devices, the WCH-Link's Interface 0, WinUSB). Its serial port
  is a standard USB serial device, which Windows 10 and 11 drive themselves (`usbser`).
- **Why wlink.** A prebuilt Windows program; it works with WCH's driver as well as WinUSB (so WCH-LinkUtility
  keeps working next to it); it reads the `.bin` (and a `.hex` or `.elf`), checks the chip, and unprotects it.
  ch32v003fun's [minichlink](https://github.com/cnlohr/ch32v003fun/tree/master/minichlink) (`minichlink -w Name.bin
  flash -b`) is supported too, for a board file that names it (`{ "inherits": "ch32v003-evt", "flasher":
  "minichlink" }`; `CHIPSHARP_MINICHLINK`, the PATH, `D:\Tools\minichlink`), but it reads raw `.bin` files alone, needs
  the Visual C++ runtime and, on Windows, WinUSB on the LinkE's interface 0 in place of WCH's driver (Zadig, or the
  `.inf` in ch32v003fun's `misc\drivers_for_WCH-LinkE`), and it writes no read-protected chip until `minichlink -u`
  clears it.
- **The port.** The WCH-LinkE is a USB device, not a serial port: wlink finds it itself; `--port` names one when more
  than one is plugged in (`-d`: its index or serial number, as `wlink list` shows them; minichlink's `-l`, its serial).
- **When it fails**, the error says what is likely wrong, from what wlink printed and the WCH-Links plugged in: none
  (with their USB ids), one in its ARM mode, one Windows has no driver for, another chip on the wire, the chip not
  answering (the wires, its power; a program that turns the debug pin off or sleeps at once is reached by
  `wlink --chip CH32V003 erase --method power-off`, with the board powered from the LinkE), or a protected chip wlink
  could not unprotect (`wlink --chip CH32V003 unprotect`); then the tool's last lines.
- **Not yet tried on a real board.** wlink and minichlink are tested against stand-ins that print what the real ones
  print; neither has written a chip from ChipSharp yet.

### The CH32V003 in chipsim

`chipsim ch32v003 Name.bin --ms 1000` (or a `.hex`, at 0x08000000 or at the alias 0, or an `.elf`) loads the flash and
starts at 0, as reset leaves the chip: the core at 8 MHz (HSI / 3), the option bytes as WCH ships them (IWDG off,
PD7 NRST), mtvec 0. `src/ChipSharp.Simulators/Ch32v003` has what WCH's reference manual (V1.6), datasheet and QingKe V2
manual give:

- **The core**: the C5's RISC-V core configured as the QingKe V2A (`RiscVCore.QingKeV2A`): RV32EC, Zicsr, Zifencei and
  WCH's XW (`-march=rv32ec_zicsr_xwchc`: clang compresses byte and halfword accesses into them); x16-x31, M (clang calls
  `__mulsi3`, `__divsi3` and their kin: the runtime has them), A and the counters are illegal, a misaligned access
  traps. The PFIC (enables, pending, IPRIOR's preemption bit, the threshold, 2 levels of nesting, the 2 VTF channels,
  SYSRESET), mtvec's modes (WCH's start-up: a table of addresses at 0, MODE 3; exceptions through entry 3), the HPE
  (INTSYSCR's HWSTKEN: x1, x5-x7, x10-x15 pushed 48 bytes down, popped by mret, 2 levels), SysTick (32 bits up, HCLK or
  HCLK / 8, STRE, SWIE). Its cycles are the C5 core's (WCH does not publish the V2A's), a load or store one more, and
  with a flash wait state one more for each flash word fetched; it runs some 15 million instructions a second.
- **The peripherals**: RCC (HSI, the PLL × 2 to 48 MHz, HPRE, the clock enables: a register used with its clock off
  stops the run, as the chip would ignore it), FLASH_ACTLR (48 MHz without its wait state stops the run), GPIOA/C/D
  (`PA1`, `PA2`, `PC0`-`PC7`, `PD0`-`PD7`: CFGLR's modes, the pulls by OUTDR, BSHR, BCR, LCKR; PD7 as NRST and PD1 as
  SWIO refuse to be outputs), AFIO (USART1's remap, EXTICR), EXTI, USART1 (`USART1: Hello`, `--send`, `--usart1 file`;
  what it sends leaves the chip only through an alternate-function TX pin), IWDG and WWDG. TIM1, TIM2, I2C1, SPI1, ADC1
  and DMA1 come with M5's wave 2: a program that touches them stops, saying so.
- **The stops**: `ebreak` (the failure stop), a trap with no handler, a watchdog or a software reset fail (exit code
  3); `wfi` with interrupts off ends the run once USART1 has sent what it holds, and with interrupts on once nothing
  can come.

## A new chip: one contract, one suite, chips as data

Every chip is built the same way, so that a new one is data and one runtime, not changes all over ChipSharp:

- **The hardware contract.** `src/ChipSharp.Runtime.Shared/chipsharp_hardware.h` declares every function a chip's
  runtime provides (57): the pins, the delays, `Registers`, the timer and pin events, the clock, the console both ways,
  `Uart`, `I2c`, `Spi`, `Adc`, `Pwm`, the start-up, the failure output and the stops; each with fixed-width types
  (`chipsharp_i32` is a C# int, 32 bits, whatever C's `int`), what it must do, and its section's guard: every chip, a
  capability (`"clock"`, `"uart"`, ...) or an event of the target's `"interrupts"`. It also says how a program stops on
  every chip, which the simulators tell apart: it ended, the chip sleeps with nothing left to wake it (WFI, SLEEP, HALT,
  STOPSYS); it failed, it stops at its breakpoint (BKPT, BREAK), on the Padauk, which has none, at its `reset`
  instruction. The functions a handler may call while `Main` is inside them are `CHIPSHARP_REENTRANT`: `__reentrant` on
  SDCC's Padauk ports, as the C code generator declares them there too. `ChipSharp.Abstractions`' `HardwareContract` is
  the same list in C#, which the frontend maps `ChipSharp.Hardware` with; `HardwareContract.For(target)` is what a
  target's runtime must define. Each chip runtime's C includes the header, so the compiler checks every definition, and
  the tests check that the header, the C# list, the frontend and ChipSharp.Library say the same, and link each runtime
  with a program that references everything its target has.
- **The conformance suite** (`tests/ChipSharp.Tests/Conformance`): small C# programs, built by the build pipeline for
  each target and run in its simulator through one adapter (`ChipSimulator`: the pins its programs use there, its
  tolerances, and a run with a stimulus, pin levels, serial input, analog inputs and the simulators' shared devices,
  which comes back in the same words for every chip). A test runs on every chip whose target has what it needs
  (`[ChipTheory("uart")]`): the pins, the pin event, the delays' accuracy, the timer event (Every, After, Stop), the
  clock (Stopwatch, `Environment.TickCount64`, `DateTime.UtcNow` from `Clock.Start`), `Console.Write` and
  `Console.Read`, a serial port both ways and its event, I2C with an MPU-6050 and an address nothing answers, SPI through
  a loopback, an analog input, PWM, and a failure's stop. A program reports by toggling a pass or a fail pin, so it
  runs on a 128-byte Padauk too. The Pico and the Uno pass all 16; the PFS154 runs in `chipsim` the 9 its target has the
  guards of and passes 7, two skipped as too big for it (see [The PFS154's runtime](#the-pfs154s-runtime)); the STM8S103
  runs all 16 in `chipsim`, which has its I2C, SPI and ADC with devices and voltages, and passes them; the CH32V003
  runs in `chipsim` the 16 its target has the guards of (no I2C, SPI, ADC or PWM before M5's wave 2) and passes them.
- **Chips as data** (`src/ChipSharp.Chips`): a chip's registers (address, size, access, reset value, fields and their
  named values), interrupts, ports, pins and their functions, and memory, read from ChipSharp's own JSON description
  (`chips/pfs154.json`, `chips/stm8s103f3.json`, written from the datasheets) or from a vendor's CMSIS-SVD file (the
  RP2040's official one, `chips/svd/RP2040.svd`, from pico-sdk), or both: `chips/esp32c5.json` says `"svd":
  "svd/esp32c5.svd"` (Espressif's, as esp-pacs keeps it) and adds what SVD never says (memory, pins, ports, constants),
  the peripherals the SVD leaves out (the core's CLIC, an array of 48 registers with `"dim"`) and, with `"replace":
  true`, one it has wrong (the interrupt matrix, whose map registers it puts at the wrong addresses); the reader also
  turns Espressif's `\\` line breaks into `; `, so that no line of C ends in a backslash. `ChipHeaders` writes the C a
  runtime uses: `<chip>_registers.h` (SDCC's `__sfr __at` for the Padauk's I/O space, volatile pointers elsewhere;
  `UART1_SR`, `UART1_SR_ADDRESS`, `UART1_SR_TXE_POS`, `UART1_SR_TXE_MASK`, `CLK_CKDIVR_HSIDIV_DIV8`),
  `<chip>_interrupts.h` (`STM8S103F3_IRQ_UART1_RX`, SDCC's handler heads `STM8S103F3_HANDLER(TIM4_UPD_OVF)`, the
  Padauk's request bits), `<chip>_pins.h` (each pin's number, port, bit and mask, the ports' registers by role and their
  addresses, the pin of each function) and, for clang and GCC, `<chip>_vectors.c` (the handlers' table, each weak). The
  headers of the SDCC chips and the ESP32-C5 are kept in `src/ChipSharp.Chips/Generated`, for their runtimes to embed
  (the ESP32-C5's registers header is 3.7 MB: every register and field of the chip); a test makes them
  again from `chips/` and fails when they differ: after changing a description, run the tests once with
  `$env:CHIPSHARP_REGENERATE = "1"` to write them. The tests also check the descriptions against the target files'
  facts, and the addresses the RP2040's runtime defines by hand against its SVD.

## What C# compiles today

The rule of thumb: what is a **value** (numbers, structs, enums, spans, fixed text, arrays that stay in their method)
works on every chip, with no heap; what **makes objects while the program runs** (classes, strings that are kept,
boxes, delegates, collections, exceptions) needs a heap, which the Pico, the ESP32-C5 and the PC have. A program that
makes no object gets no heap at all, even on the Pico. `chipsharp check` tells you for your program, place by place.

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
| Arrays | On the stack: a constant size, made in a method and passed down to the methods it calls. In static fields: `static readonly byte[] Digits = { 0x3F, ... };` is a constant table in flash when nothing writes it; `static int[] buffer = new int[16];` a fixed array in RAM; a table whose elements its initializer computes (`static readonly Step[] Steps = [new(Light.Green, 4), new(Light.Red, 2)];` of record structs, tuples, calls) a fixed array the program fills when it starts. `foreach`, `Length`, bounds checks |
| Spans | `Span<T>` and `ReadOnlySpan<T>` of arrays, of `stackalloc` (a constant size), of constant data (`static ReadOnlySpan<byte> Table => new byte[] { ... };`, in flash) and of variables; indexer, `Slice`, ranges and indices (`s[2..5]`, `s[^1]`), `CopyTo`, `Fill`, `Clear`; `MemoryExtensions` (`IndexOf`, `SequenceEqual`, `StartsWith`, ...), `BinaryPrimitives`, `MemoryMarshal` (`Read`, `Write`, `Cast`, `AsBytes`) and `BitConverter` (`IsLittleEndian`, `SingleToInt32Bits` and the other bits of floats, `TryWriteBytes`, `ToInt32` and the others of a span or an array; `GetBytes` needs a heap), at any offset, also on a chip that faults on unaligned words; `BinaryPrimitives` give .NET's numbers on a big-endian chip too (the STM8), where `MemoryMarshal` and `BitConverter` have the chip's order, as .NET has on such a processor |
| Fixed text | String literals anywhere a string may be: printed, compared, measured and searched (`Length`, `s[i]`, `==`, `IndexOf`, `StartsWith`, `switch`); each is a constant object in flash (8 bytes and 1 byte per char on the Pico) |
| Printed text | Text built and printed straight away, with no string ever made (also in the body of a `foreach` over a collection or a `using`, where C# makes it otherwise): `Console.WriteLine($"road : {lane}")`, `Console.Write("lane " + lane + '!')`, `x.ToString()` printed, `Console.WriteLine("{0,4} {1:X2}", a, b)`. Holes of numbers, bools, chars, text, enums (by name, `[Flags]` combined), structs (their `ToString`) and nullables; alignments (`{x,5}`, `{x,-5}`); the formats `D`, `D<n>`, `X<n>`, `x<n>` and `G` of whole numbers, `G` and `R` of floats; dates with every format of theirs (`$"{now:HH:mm:ss}"`, `now.ToString("d")`, `Console.WriteLine(now)`, see [Date and time](#date-and-time)) |
| Type objects | `typeof(X)`, `typeof(T)`: `Name`, `FullName`, `Namespace`, `ToString()` as .NET 10 writes them, `==`, `IsValueType`, `IsArray`, `IsEnum`, `IsClass`, `IsInterface`, `IsPrimitive`, `IsGenericType` (constant objects in flash) |
| Math | `Math` and `MathF`: trigonometry (`Sin`, `Cos`, `Tan`, `SinCos`, `Asin`, `Acos`, `Atan`, `Atan2`), hyperbolic functions and their inverses, `Exp`, `Log` (in any base), `Log2`, `Log10`, `Pow`, `Sqrt`, `Cbrt`; `Floor`, `Ceiling`, `Truncate`, `Round` (to digits, by every `MidpointRounding`), `Clamp`, `Sign`, `Abs`, `Min`, `Max`, `MinMagnitude`, `MaxMagnitude`, `CopySign`, `FusedMultiplyAdd`, `IEEERemainder`, `ScaleB`, `ILogB`, `BitIncrement`, `BitDecrement`, the reciprocal estimates, `E`, `PI`, `Tau`; for every whole-number type `Abs`, `Min`, `Max`, `Clamp`, `Sign`, `DivRem`, and `BigMul`; `double.Sin(x)` and the like, and generic math over float types (`T.Sqrt`, `T.Sin`, ...). The exact ones give .NET's bits; the transcendental ones the same bits on the PC and the Pico, and .NET's but for the last bit of a few results (see [differences](#where-a-program-behaves-differently)). A program links only the functions it calls (`Math.Sin`: 9 KB on the Pico) |
| Library | `Console.Write` and `WriteLine`; `TimeSpan` (factories in any unit, parts, totals, arithmetic, comparisons); `DateTime`, `DateOnly` and `TimeOnly` (parts, arithmetic, comparisons, `TryFormat` into a span), `DateTime.Now`, `Stopwatch.GetTimestamp` and `GetElapsedTime`, `Environment.TickCount` (see [Date and time](#date-and-time)); `System.Array`: `Copy`, `ConstrainedCopy`, `CopyTo`, `Clear`, `Fill`, `IndexOf`, `LastIndexOf`, `Reverse`, and `Sort` and `BinarySearch` of numbers, bools and structs that implement `IComparable<T>` |
| Hardware | `ChipSharp.Hardware`: `Pin` (`Output`, `Input`, `Write`, `Read`, `Toggle`, `Watch`, `WatchRising`, `WatchFalling`, `Unwatch`), `Timing` (`Delay(TimeSpan)`, `DelayMilliseconds`, `DelayMicroseconds`, `EveryMilliseconds`, `EveryMicroseconds`, `AfterMilliseconds`, `AfterMicroseconds`, `Stop`), `Registers.Read` and `Write`, `Clock.Start`, and `[Interrupt]` methods (see [Interrupts](#interrupts)); the serial ports and the console's input, the analog inputs, the PWM outputs and the I2C and SPI buses: `Uart` (and `Console.Read`), `Adc`, `Pwm`, `I2c` and `Spi`; and `ChipSharp.Components`, the parts connected to the chip: `Lcd`, `SevenSegment` and `Ds1307` (see [Hardware](#hardware)) |
| Failures | Where .NET throws (an index outside an array, a whole number divided by zero, `int.MinValue / -1`, a `TimeSpan` too long, a field of null, a cast that fails, ...), the program stops with the message .NET prints: on standard error with exit code 1 on the PC, over the console's serial port (the Pico's UART0, the Uno's USART0) and then at a breakpoint on a chip |

### On the PFS154 and the STM8S103

The first table holds on both chips, which have no heap, with these differences. There is no `double`: SDCC's has 32
bits, so a program that uses one is refused (`chipsharp check` names each place); `float` and `MathF` work, in SDCC's
software routines. `nint` and `nuint` have 16 bits. `Console` writes only on the STM8 (UART1). What runs is what the
suites show: every program of every corpus the differential tests compare with .NET that needs neither a heap nor a
`double` and fits the chip builds with the `c` code generator and SDCC, runs in chipsim and prints what .NET prints (the
backend suite, with a list of what each chip skips and why), and the hardware suite's cases pass on the STM8 (16) and on
the PFS154 (7, 2 skipped). The chips' limits are in [Limits of the two chips](#limits-of-the-two-chips) and
[what does not fit](#what-does-not-fit-the-small-chips).

### On a chip with a heap (the Pico, the ESP32-C5 and the PC)

The ESP32-C5 (no FPU: `float` and `double` in software, bit for bit like .NET; 224 KB of heap, 16 KB of stack that the stack
check guards) takes every program of every corpus the differential tests compare with .NET, this table's included: the
backend conformance suite runs them in `chipsim esp32c5` with none skipped.

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
The limits of the SDCC chips (`double`, `Pwm.Duty`, a console the PFS154 has not, a pin event on the PFS154's other
pins) are found the same way, before SDCC runs: see [Limits of the two chips](#limits-of-the-two-chips).

## Known differences from .NET and gaps

### Where a program behaves differently

| What | .NET | ChipSharp | What to write |
|---|---|---|---|
| A failure | An exception, which a `catch` may handle | `try`/`catch` does not compile yet; a `throw`, or anything .NET would throw for, stops the program as an exception nothing catches does | Report failures through return values (`bool TryRead(out int value)`) |
| `finally` after a failure | .NET 10 prints the unhandled exception, then runs the `finally` blocks around the throw (a `using`'s `Dispose` too) | Stops right after printing it; a `finally` runs only when its `try` block is left normally (its end, `return`, `break`, `continue`) | Do not count on clean-up after a failure |
| What a failure prints | The exception and a stack trace. A stack overflow lists the methods that were running | The exception's first lines (`Unhandled exception. System...: message`) or `Stack overflow.`, with no stack trace; exit code 1 (stack overflow: .NET's 0xC00000FD). On a chip: over the console's serial port, then the core stops at a breakpoint | |
| A stack overflow on the Uno | `Stack overflow.` | The Uno's functions do not check the stack yet (`"stackCheck"` is false for the AVR): a recursion with no end grows the stack into the static fields, and the program goes wrong without a message | Keep recursion shallow on the Uno: its stack has 512 bytes |
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
| `Pin.Input` | .NET IoT opens an input as `PinMode.Input`, `InputPullUp` or `InputPullDown`: the pull is what you ask | `Pin.Input(pin)` is no pull, `Pin.Input(pin, Pull.Up)` and `Pull.Down` are the same three modes. Every chip has the pull-up, but only the Pico, the ESP32-C5 and the CH32V003 have a pull-down: `chipsharp check` refuses `Pull.Down` for the others | A resistor to GND, for a button to the supply on a chip without a pull-down |
| An LED's polarity | The program's own | Only the board's file knows it (`LED` in `pins`), not `Pin`: `Pin.Write` takes a level. The STM8 board's LED is lit while PB5 is low | Write the level the board needs; each example says which |
| `Adc.Read` | (.NET IoT's ADC readings are the converter's own) | The chip's own range: 0 to 4095 (3.3 V) on the Pico, 0 to 1023 (5 V) on the Uno | `Adc.Volts`, the same on both |
| A value's bytes on the STM8 (`MemoryMarshal`, `BitConverter`) | The PC is little-endian: an `int`'s low byte first, `BitConverter.IsLittleEndian` true | The STM8 is big-endian: its high byte first, `IsLittleEndian` false, as .NET on a big-endian processor (`BinaryPrimitives` of either order give the same numbers on every chip) | `BinaryPrimitives.ReadInt32LittleEndian` and the like, or check `BitConverter.IsLittleEndian` |
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

### What does not fit the small chips

The PFS154 has 2K words and 128 bytes of RAM, 48 of them the stack's; the STM8S103 8 KB and 1 KB.

| What | The PFS154 | The STM8S103 |
|---|---|---|
| `Pwm.Duty` | Takes a `double`, so it is not there where `double64` is false: `chipsharp check` says so (use `Pwm.PulseMicroseconds`) | The same |
| `Console.ReadLine` | Needs a heap, and the PFS154 has no console input: read char by char with `Console.Read` into a `stackalloc` span | Needs a heap: the same |
| `Ds1307.Read` and `Write` | | About 13 KB with `DateTime`: they do not fit 8 KB (`Open` and `Running` do) |
| `Timing.Delay(TimeSpan)` | About 360 bytes of RAM: it does not fit 128 bytes (`DelayMilliseconds` does) | Fits |
| A table of structs | Indexing one costs over 50 bytes of RAM: it does not fit beside the stack (the traffic light passes the steps as a method's parameters) | Fits, but the code is big (see [the examples](#examples-for-the-stm8s103-board)) |

- **The flashers** (`easypdkprog` for the PFS154, `stm8flash` for the STM8S103) are tested against stand-ins that record
  the command line and play back the tool's output, not against real hardware or the real tools.
- **No Wokwi parts** exist for these two chips, so their examples have no `diagram.json`: `chipsim` runs them instead
  (each example's README gives the command).
- **SDCC 4.6 makes wrong code** of some C, which the C code generator steers around (listed under [the C code
  generator](#pfs154-and-stm8s103-through-c-and-sdcc): 64-bit `&`, `|` and `^` of values it knows, the STM8's
  peephole optimizer, a struct returned or passed, a local that copies a parameter, a large STM8 frame returning 3 or 4
  bytes). One problem is not worked around: SDCC's STM8 port stops with an internal error, "Unbalanced stack", on a
  function that is long and heavy in `long` arithmetic. The build fails with SDCC's message; split the function.

### On the ESP32-C5

The full list, with the pins and ranges, is [The ESP32-C5 limits](#the-esp32-c5-limits). In short:

- **Out of scope:** Wi-Fi, Bluetooth LE, Thread and Zigbee (Espressif's radio drivers are closed binaries).
- **The CPU runs at 48 MHz** until the BBPLL bring-up (ESP-IDF's `rtc_clk_bbpll_enable` and `rtc_clk_bbpll_configure`) can be
  tried on a board; `"clockHz": 160000000` works only for an image started with the PLL running.
- **No API yet** for the RGB LED (a WS2812 on GPIO 27), the LP core, deep sleep; the 8 MB of PSRAM and flash
  execute-in-place are unused: the program and the heap live in the SRAM (369.4 KB; the largest image is 353.4 KB without a
  heap, 129.4 KB with the 224 KB heap).
- **The ADC is uncalibrated**, and so is the temperature sensor.
- **`Console.ReadLine` does not echo**: turn on the terminal's local echo.
- **Not yet tried on a real board:** the start-up on silicon, flashing, the ADC's power-up, the I2C recovery after a NACK,
  the Wokwi diagrams.

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

### Pins

`Pin.Output(pin)` drives the pin (low until written; `Write`, `Toggle`), `Pin.Read(pin)` reads it. `Pin.Input` takes a
pull, the chip's own resistor, the same on every chip:

```csharp
Pin.Input(15);              // no pull: the pin floats until something drives it, the same on every chip
Pin.Input(15, Pull.Up);     // pull-up: reads high until something (a button to GND) pulls it low; every chip has one
Pin.Input(15, Pull.Down);   // pull-down: reads low until a button to the supply pulls it high; only some chips have one
```

`Pull.None` is the one-argument form. The pull-downs are the Raspberry Pi Pico's, the ESP32-C5's and the CH32V003's; the ATmega328P, the
PFS154 and the STM8S103F3 have pull-ups only, and `chipsharp check` refuses a constant `Pull.Down` for them (the
capability `"pullDown"`: `Pin.Input(pin, Pull.Down) needs a pull-down, and the target 'uno' has none`). A program that
never asks for a pull links no code for one.


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
The Uno's are A0 to A5 (pins 14 to 19), read in its 10 bits: `Adc.Read(14)` is 0 to 1023 (5 V), `Adc.Volts` reading *
5 / 1024, so the volts are the same on both chips and the readings each chip's own (see [Arduino Uno](#the-chips-timers-clock-and-buses)).

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

The Uno's PWM pins are D3, D5, D6 and D11, on two 8-bit timers at a few frequencies (the nearest is taken), and D9,
whose edges Timer1's compare times to the half microsecond, the servo's pin: the same code runs there with `Pwm.Output(9,
50)` (see [Arduino Uno](#the-chips-timers-clock-and-buses)). Its timers are shared as the Pico's slices: `Pin 6 shares Timer0
with pin 5, which runs at 1000 Hz, ...`.

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
| `I2c.Open(bus, sda, scl, frequency)` | Sets the bus up on its pins, with the chip's pull-ups on, its clock from 2 kHz to 1 MHz (100 kHz standard mode, 400 kHz fast mode, 1 MHz fast mode plus). The Pico's bus 0 (I2C0) has SDA on GPIO 0, 4, 8, ..., 28 and SCL on the pin after each; bus 1 (I2C1) SDA on GPIO 2, 6, 10, ..., 26 and SCL on the pin after. The Uno's one bus 0 has SDA on A4 (18) and SCL on A5 (19), 490 Hz to 400 kHz |
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
| `Spi.Open(bus, sck, tx, rx, frequency, mode)` | Sets the bus up as its controller: 8 bits, most significant first, the clock the nearest the chip makes at or below `frequency` (2 kHz to 62.5 MHz), SPI `mode` 0 to 3; -1 for no `rx` (a device that only listens) or no `tx`. The Pico's bus 0 (SPI0) has SCK on GPIO 2, 6, 18 or 22, TX on 3, 7, 19 or 23, RX on 0, 4, 16 or 20; bus 1 (SPI1) SCK on 10, 14 or 26, TX on 11, 15 or 27, RX on 8, 12, 24 or 28. The Uno's one bus 0 has SCK on D13, TX on D11, RX on D12, 125 kHz to 8 MHz (16 MHz / 2 to 128) |
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

`tools/rp2040-sim/run.mjs` puts simulated devices on the buses (`tools/rp2040-sim/devices`; `devices/catalog.mjs` is
what `tools/avr-sim/run.mjs` shares of them, for the Uno's bus 0: `--i2c 0:0x27=lcd1602`, `--spi 0=74hc595@D10`), and
prints what the buses do with the pin changes:

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
| `chipsharp flash <program.dll \| image> -t <board> [--port <port>]` | Puts the program onto a board with its target's flasher: a `.dll` is built first (as `build` does, with its options), an image (`.hex`, `.uf2`) is flashed as it is. The Uno: avrdude through its USB port (`--port COM5`, or the one serial port that looks like an Arduino's); the Pico: a copy to its `RPI-RP2` drive (or `--port E:\`); the PFS154: easypdkprog, in the Easy PDK programmer (`--port COM7`, or the one port with its USB ids); the STM8S103: stm8flash, through an ST-LINK (`--port` its serial number); the CH32V003: wlink, through a WCH-LinkE (`--port` its index or serial number) |
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
| `CHIPSHARP_SDCC` | SDCC (`sdcc.exe` or its folder), for the Padauk and STM8 chips (default: `C:\Program Files\SDCC\bin`, then the PATH) |
| `CHIPSHARP_EASYPDKPROG` | easypdkprog (`easypdkprog.exe` or its folder), for `flash -t pfs154-bare` (default: the PATH, then `D:\Tools\easypdkprog` and the usual install folders) |
| `CHIPSHARP_STM8FLASH` | stm8flash (`stm8flash.exe` or its folder), for `flash -t stm8s103-board` (default: the PATH, then `D:\Tools\stm8flash`, then sduino's copy) |
| `CHIPSHARP_WLINK` | wlink (`wlink.exe` or its folder), for flashing a CH32V003 board (default: the PATH, then `D:\Tools\wlink`, then Cargo's bin) |
| `CHIPSHARP_MINICHLINK` | minichlink (`minichlink.exe` or its folder), for a board file that names it (default: the PATH, then `D:\Tools\minichlink`) |

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

**`examples/`** holds projects for the Pico (`pico-*`), the Arduino Uno (`uno-*`), the Padauk PFS154 (`pfs154-*`),
the STM8S103 board (`stm8-*`), the ESP32-C5 (`esp32c5-*`) and the CH32V003 (`ch32v003-*`), each a `.csproj` that
references `ChipSharp.Hardware`, built with `dotnet build -c Release` and then
`chipsharp build bin\Release\net10.0\<Name>.dll -t pico` (or `-t uno`, `-t pfs154-bare`, `-t stm8s103-board`,
`-t esp32c5-devkitc`, `-t ch32v003-evt`) in its folder. The Pico's:

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

The Arduino Uno's examples (the blink, the button, the serial port, a traffic light with an LCD, a clock, a servo) are
in [Examples for the Uno](#examples-for-the-uno), the Padauk PFS154's (`pfs154-*`: the blinks, the button, a PWM
fade, a traffic light) in [Examples for the PFS154](#examples-for-the-pfs154), the STM8S103 board's (`stm8-*`: the
blink, a serial prompt, a traffic light, an LCD, a PWM fade) in
[Examples for the STM8S103 board](#examples-for-the-stm8s103-board). The ESP32-C5's eight (`esp32c5-*`: the blink, a button on the
pin event, a serial prompt, a timer blink, a traffic light with classes, the Pico's LCD traffic light unchanged, a
servo and a DS1307 and LCD clock) are in [Examples for the ESP32-C5](#examples-for-the-esp32-c5), the CH32V003's
(`ch32v003-*`: the blink, a button on the pin event, a timer blink, a serial prompt, a clock) in
[Examples for the CH32V003](#examples-for-the-ch32v003).

The samples that need neither a heap nor a `double` also build for the PFS154 and the STM8S103 when they fit, and
`chipsim` runs them (the backend conformance suite checks each against .NET's output).

The traffic lights, the bus examples, the 7-segment counter, the clock, the command prompt, the knob and the servo have a
`diagram.json` for Wokwi with the whole circuit, wired directly (Wokwi needs no resistors): common-cathode displays,
the button between GPIO 22 and 3V3, the potentiometer between 3V3 and GND, the servo on VBUS, the LCD in I2C mode and the DS1307 on
3V3, UART0 on the serial monitor (where the prompt's commands are typed); so do the Uno's. With the button pressed at
2.5 s, both traffic lights print, and so does the Uno's (`examples/uno-traffic-light`, line for line):

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

The 4,252 tests (about 7 minutes once the build cache is warm) check each stage on its own (reading IL, the
whole-program pass, TIR, the LLVM code, linking, the `.uf2` and the `.hex`), then compile C# programs and run them: on
the PC, on the simulated Pico and on the simulated Uno, comparing what they print, byte for byte, with what .NET prints
for the same program; the library code (collections, `StringBuilder`, sorting, `TimeSpan`, `DateTime` and its formats)
runs next to .NET's own on thousands of random operations, and the math functions on hundreds of thousands of values,
computed both with the PC's float hardware and in software as the Pico computes them. The PC tests need LLVM; the Pico tests also need Node.js
and `npm ci` in `tools\rp2040-sim` (the Uno's, Zig and `npm ci` in `tools\avr-sim`), and are skipped, saying why,
without them. The example projects are built with `dotnet build` and `chipsharp build` and run in the simulators, with
what their circuits would give them: the traffic lights with the button pressed, the clocks with a simulated LCD and
DS1307, the servo with a knob turned; and their Wokwi circuits are checked, wire by wire.
`chipsharp flash` runs no real tool in the tests: avrdude, easypdkprog, stm8flash, esptool, wlink and minichlink are
batch files that keep the command line they were given and print what the real ones print (their failures included),
the serial ports, ST-LINKs and WCH-LinkEs are records as the registry would give them, and the Pico's drive a folder.
The SDCC chips' tests build hand-written C and assembly for the PFS154 and the STM8S103 with SDCC (skipped without it)
and compare the `.hex` and the memory report with SDCC's own map; the PFS154's runtime tests build C# for it and run it
in `chipsim` (a blink, a button, the timer event, PWM, the clock). The hardware contract's tests keep its header, its
C# list, the frontend and every chip runtime in step; the conformance suite runs the same C# programs on every chip's
simulator (the Pico's, the Uno's and the PFS154's today, the STM8S103's skipped until its runtime comes); the chip
descriptions' tests read them and the RP2040's SVD, compile the headers made from them with SDCC and clang, and fail
when the kept ones are out of date (`CHIPSHARP_REGENERATE=1` writes them). On `chipsim`, every form of every
instruction SDCC's assemblers encode is decoded to its length (and the STM8's to its listed cycles), the flags of each
core's ALU are checked an instruction at a time, and C programs run: the arithmetic of every width through SDCC's
helpers (each result compared with C's), the timers' interrupts and PWM to the microsecond, inputs on a timeline, UART1
both ways, and the ways a run stops. The C code generator's tests run every sample, and programs of every operation on
the values where C and C# part ways, through `host-c` and `host` against C#, and compile and link the C for both chips
with SDCC. The backend conformance suite runs every program of every corpus through every backend, on the PC and
on both simulated SDCC chips (see [PFS154 and STM8S103](#pfs154-and-stm8s103-through-c-and-sdcc)), and measures what narrowing saves there.

The simulator, rp2040js 1.4.1, has one known fault: it sets the overflow flag of `SBCS` wrong when the borrow takes the
subtrahend to 0x80000000, so a comparison of extreme `long` values, such as `long.MaxValue` with `long.MinValue`, gives
the wrong answer on the simulated Pico only (a real RP2040 and the PC are right). The Pico tests leave those values to
the PC tests.

The Uno's, avr8js 0.21.1, ends a fast PWM pulse one count of its timer early (at OCR, where the ATmega328P ends it at
OCR + 1), and moves a timer's edge by a clock cycle when the program reads the timer at its count; the Uno's PWM tests
allow for both. Its TWI takes no time on the bus: the I2C transfers' timing is the program's own.

## How it works

### Layout

| Folder | What it holds |
|---|---|
| `src/ChipSharp.Frontend` | Reads the IL of a compiled C# `.dll`, finds every method the program uses (the whole-program pass), checks it against what ChipSharp compiles (`chipsharp check`), and translates it into TIR |
| `src/ChipSharp.Ir` | TIR (Typed Intermediate Representation), which every code generator reads: its verifier, printer and builder, and in `Optimizations` the value-range analysis and narrowing |
| `src/ChipSharp.Library` | ChipSharp's own C# for library types (`TimeSpan`, `List<T>`, `StringBuilder`, the exceptions, ...), compiled together with the programs that use them |
| `src/ChipSharp.Backend.Llvm` | The `llvm` code generator: TIR in, LLVM IR text out |
| `src/ChipSharp.Backend.C` | The `c` code generator: TIR in, portable C99 out, for the chips that only have a C compiler (SDCC's) and the PC (`host-c`) |
| `src/ChipSharp.Toolchain.Clang` | The `clang` toolchain: LLVM IR and runtime sources in, a linked program out; as the `zig` toolchain, the same with the clang and ld.lld inside Zig (AVR) |
| `src/ChipSharp.Toolchain.Sdcc` | The `sdcc` toolchain: C in, SDCC's Intel HEX (`.ihx`) and map out, for the Padauk and STM8 chips |
| `src/ChipSharp.Simulators` | `chipsim`: the PFS154 (PDK14) and STM8S103 simulators, their peripherals, the devices of their buses (`Devices`: the LCDs, the DS1307, the MPU-6050, the BMP280, the 74HC595, a loopback; the Pico's and the Uno's, in C#), and the command |
| `src/ChipSharp.Chips`, `chips` | Chips as data: the model of a chip's registers, interrupts, pins and memory, its readers (ChipSharp's JSON, CMSIS-SVD, both together), the C headers it writes, and in `Generated` the SDCC chips', the ESP32-C5's and the CH32V003's headers; `chips` holds the descriptions (see [A new chip](#a-new-chip-one-contract-one-suite-chips-as-data)) |
| `src/ChipSharp.Image.Esp`, `src/ChipSharp.Flasher.Esptool` | The ESP image (`.bin`: headers, segments, checksum, SHA-256; and its reader) and the flasher that runs esptool on it, with its port detection by USB ids |
| `src/ChipSharp.Runtime.*` | The runtimes, in C: `Shared` (console, text, floats, heap and collector, and `chipsharp_hardware.h`, the hardware contract every chip runtime implements), `Windows` (the PC), `ArmV6M` and `Rp2040` (start-up, UART0, timers, pins, interrupts), `Avr` (any AVR chip's start-up, multiply and divide helpers, soft float), `Atmega328p` (the Uno's pins, Timer1's clock and delays, the pin and timer events, USART0, the TWI, SPI, the ADC and PWM), `Pdk14` (any Padauk PDK14 chip's start-up, interrupt handler and stops, for SDCC), `Pfs154` (its pins, T16's delays and clock, the timer and pin events, PWM; mostly assembly), `Stm8` (any STM8 chip's start-up, stops, failures and 64-bit multiply, for SDCC, and the modules a file is cut into), `Stm8s103` (its pins, TIM1's clock, delays and timer event, the pin event, UART1's console and serial port, I2C, SPI, the ADC and TIM2's PWM), `Riscv32` (any 32-bit RISC-V chip's start-up, trap entry and dispatch, 64-bit division, memory functions, soft float, stops; and its E variant, `riscv32e`, with the multiplication and division a core without M lacks), `Esp32c5` (its first steps: the watchdogs, the CLIC's trap mode) and `Ch32v003` (its vector table and first steps: INTSYSCR, `mtvec`, the 48 MHz clock) |
| `src/ChipSharp.Image.Uf2`, `src/ChipSharp.Image.Hex`, `src/ChipSharp.Image.Bin`, `src/ChipSharp.Build`, `src/ChipSharp.Cli` | The `.uf2`, `.hex` and `.bin` (the raw flash, from its origin) images, the build pipeline, and the `chipsharp` command |
| `src/ChipSharp.Flasher.*` | The flashers: `Avrdude` (the Uno), `Uf2Copy` (the Pico), `EasyPdkProg` (the PFS154), `Stm8Flash` (the STM8S103), `WchLink` (the CH32V003: wlink and minichlink, through a WCH-LinkE), and `Shared`, what those that run a programmer's tool share (finding and running it, the serial ports and USB devices plugged in, with their interfaces' drivers) |
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
| M6 (done) | C backend through SDCC: the Padauk PFS154 (2K words, 128 bytes of RAM) and the STM8S103 (8 KB, 1 KB), no heap, 32-bit `double` refused; the chips as data, a hardware contract with a conformance suite, the runtimes, `chipsim`, the flashers (`easypdkprog`, `stm8flash`) and examples (see [PFS154 and STM8S103](#pfs154-and-stm8s103-through-c-and-sdcc)) |
| M7 (done) | Arduino Uno: the ATmega328P, an 8-bit AVR with 2 KB of RAM and no heap, through the LLVM in Zig; constants in flash, 8- and 16-bit arithmetic, its pins, timers, clock, serial port, I2C, SPI, ADC and PWM, `chipsharp flash` with avrdude, Wokwi circuits and examples (see [Arduino Uno](#arduino-uno)) |
| M4 (done) | ESP32-C5 (RISC-V): its target files, the llvm code generator for riscv32, the start-up, Espressif's SVD through the shared generator, `chipsim esp32c5`, the ESP image (byte for byte esptool's) and the esptool flasher, the runtime (pins with pulls, events, the SYSTIMER clock, the USB Serial/JTAG console, UART0, a 224 KB heap with the collector, I2C, SPI, the ADC and PWM), the whole corpus on the simulated chip and 8 examples; not yet tried on a board (see [ESP32-C5](#esp32-c5)) |
| M5 (under way) | CH32V003, C# on a 10-cent chip with 2 KB of RAM: M2-style code and the M3 pieces that need no heap (generics over values, spans, fixed and printed text, record structs). Done: its target files, the riscv32e runtime and the start-up, WCH's SVD through the shared generator, every corpus program linked, `chipsim ch32v003`, the chip's runtime (pins and pulls, SysTick's delays, clock and timer event, the pin event, USART1's console and serial port) passing the conformance suite in it, five examples (see [CH32V003](#ch32v003)). Next: I2C, SPI, the ADC and PWM (wave 2) |
| Later | Catching exceptions (`try`/`catch`); more chips, and an interpreter mode for the smallest |
