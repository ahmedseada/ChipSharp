// ChipSharp's hardware contract: every function a chip's runtime provides, the same on every chip. What a C# program
// calls in ChipSharp.Hardware (Pin, Timing, Registers, the [Interrupt] events, Clock, Uart, I2c, Spi, Adc, Pwm) and
// Console.Read become calls of the ChipSharp_Runtime_ functions below; the shared runtime code (chipsharp_console.c,
// an architecture's failures and start-up) calls the chipsharp_ hooks. A chip's runtime defines each function its
// target's file says it has (the guard of its section), includes this file, and so the compiler checks every
// definition against it. ChipSharp.Abstractions' HardwareContract is the same list in C#, which the frontend maps the
// library's calls with; a test keeps the two, and every runtime, in step.
//
// Freestanding: no other header, no C library. Clang, GCC (Arm, RISC-V, AVR) and SDCC (Padauk, STM8) compile it.
//
// Types. A C# int is 32 bits on every chip (C's int has 16 on the 8-bit ones), so the contract says what it means:
// chipsharp_i32 is a C# int (and a char, its UTF-16 code unit, and a bool, 0 or 1, as C# passes them), chipsharp_u32 a
// uint, chipsharp_i64 a long, chipsharp_f64 a double (IEEE's 64 bits), chipsharp_u8 a byte. A span arrives as a
// pointer to its first byte and its number of bytes (an empty one as a pointer to a local and 0). chipsharp_count is
// the size of a piece of the runtime's own text: C's unsigned int, as wide as the chip's int.
//
// Guards. Each section says which part of the target file a chip needs for its functions: "chip" (every chip), a
// capability ("clock": true, "uart": a number of ports above 0, ...) or an event of the "interrupts" table. chipsharp
// check refuses a program that calls a function its target does not have, so a runtime defines only what its target
// says; and every function it defines checks its numbers itself (a pin, port, bus, frequency or mode the chip does not
// have stops the program with a message, chipsharp_hardware_failure; a pin number no pin has is ignored by Pin).
//
// Interrupt handlers. A function marked CHIPSHARP_REENTRANT may be called from a program's [Interrupt] method while
// the program's Main is inside it too: on SDCC's Padauk ports, whose functions keep their parameters at fixed
// addresses, it must be __reentrant (CHIPSHARP_REENTRANT says so); elsewhere it only must not keep state it would lose.
// The others wait (for time, for input) and are never called from a handler: the interrupt that ends the wait cannot
// come while one runs.

#ifndef CHIPSHARP_HARDWARE_H
#define CHIPSHARP_HARDWARE_H

#if defined(__SDCC)
typedef unsigned char chipsharp_u8;
typedef long chipsharp_i32;
typedef unsigned long chipsharp_u32;
typedef long long chipsharp_i64;
typedef unsigned int chipsharp_count;
#define CHIPSHARP_NORETURN _Noreturn
// SDCC's double is its float, 32 bits: C#'s double has no C type there (the target's "double64": false), and the
// functions that take or give one are not in the contract of such a chip
#define CHIPSHARP_DOUBLE64 0
#else
typedef __UINT8_TYPE__ chipsharp_u8;
typedef __INT32_TYPE__ chipsharp_i32;
typedef __UINT32_TYPE__ chipsharp_u32;
typedef __INT64_TYPE__ chipsharp_i64;
typedef unsigned int chipsharp_count;
#define CHIPSHARP_NORETURN __attribute__((noreturn))
#define CHIPSHARP_DOUBLE64 (__SIZEOF_DOUBLE__ == 8)
#endif

#if CHIPSHARP_DOUBLE64
typedef double chipsharp_f64;
#endif

#if defined(__SDCC_pdk13) || defined(__SDCC_pdk14) || defined(__SDCC_pdk15)
#define CHIPSHARP_REENTRANT __reentrant
#else
#define CHIPSHARP_REENTRANT
#endif

// The text of a runtime's own message: in program memory where the chip reads it with other instructions than RAM
// (an AVR's __flash, LPM), else ordinary constant chars.
#if defined(__AVR__)
#define CHIPSHARP_MESSAGE_CHAR const __flash char
#else
#define CHIPSHARP_MESSAGE_CHAR const char
#endif

// ---- Start-up and the program (chip) --------------------------------------------------------------------------------
//
// The architecture's reset handler sets up the stack, the variables (.data, .bss) and calls chipsharp_chip_init, then
// the program, chipsharp_entry, then chipsharp_halt. The program's [Interrupt] methods are the code generator's too:
// each event of the target's "interrupts" table ("timer", "pin", "uart") has an entry, void chipsharp_isr_irq<n>(void)
// with n its "irq", which calls the event's "clear" function first (when the table names one: the RP2040's
// chipsharp_clear_timer_event and chipsharp_clear_pin_event), then the method. The runtime gives each entry a weak
// default and calls it: from the vector table (the RP2040's IRQ 1 and 13), or from its own handler of the chip's
// interrupt once it found the event happened (every event of the ATmega328P, numbered past its vector table). A chip
// whose interrupts all share a vector (the PFS154's) has one handler that finds which.

// The chip's own set-up, before the program, with interrupts off: its clocks, the timer that counts time (the clock,
// the delays), peripherals taken out of reset. The architecture's runtime has an empty weak one.
void chipsharp_chip_init(void);

// The program: its Main, which returns its exit code (0 for a void Main). Written by the code generator.
chipsharp_i32 chipsharp_entry(void);

// ---- Pins: ChipSharp.Hardware.Pin (chip) ----------------------------------------------------------------------------
//
// Pins by the target's numbers (the Pico's GPIO 0-29; the Uno's 0-13 are D0-D13, 14-19 A0-A5; the Padauk and STM8
// chips' port * 8 + bit). A number no pin has is ignored, and reads as low.

// Pin.Output: the chip drives the pin, low until written.
void ChipSharp_Runtime_PinOutput(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

// Pin.Input: the chip stops driving the pin and puts no pull on it, the same on every chip (whatever the chip's reset
// left on it); PinRead reads it.
void ChipSharp_Runtime_PinInput(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

// Pin.Input(pin, pull): as PinInput, then the pull: 0 none (PinInput itself), 1 up, 2 down. A function of its own, so that
// a program that never asks for a pull links no code for one. Every chip has the pull-up; the pull-down is the target's
// "pullDown": true, which chipsharp check checks for a constant Pull.Down; a pull the chip does not have, or a number
// above 2, stops the program (chipsharp_hardware_failure).
void ChipSharp_Runtime_PinInputPull(chipsharp_i32 pin, chipsharp_i32 pull) CHIPSHARP_REENTRANT;

// Pin.Write: drives an output high (any high but 0) or low.
void ChipSharp_Runtime_PinWrite(chipsharp_i32 pin, chipsharp_i32 high) CHIPSHARP_REENTRANT;

// Pin.Toggle: drives an output to the other level.
void ChipSharp_Runtime_PinToggle(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

// Pin.Read: 1 when the pin is high, else 0.
chipsharp_i32 ChipSharp_Runtime_PinRead(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

// ---- Delays: ChipSharp.Hardware.Timing.Delay... (chip) --------------------------------------------------------------
//
// Each waits at least the time given (0 or less returns at once), and not much more: a few microseconds, what the chip
// takes to read its timer. They wait, so a handler never calls them.

void ChipSharp_Runtime_DelayMilliseconds(chipsharp_i32 milliseconds);

void ChipSharp_Runtime_DelayMicroseconds(chipsharp_i32 microseconds);

// ---- Registers: ChipSharp.Hardware.Registers (chip) -----------------------------------------------------------------
//
// A read or write of the chip's register at an address, as wide as the chip's registers are (32 bits on the RP2040,
// 8 on the 8-bit chips: the value's low bits); on a chip with an I/O space of its own (the Padauk's), its addresses.

chipsharp_u32 ChipSharp_Runtime_RegisterRead(chipsharp_u32 address) CHIPSHARP_REENTRANT;

void ChipSharp_Runtime_RegisterWrite(chipsharp_u32 address, chipsharp_u32 value) CHIPSHARP_REENTRANT;

// ---- The timer event: Timing.Every..., After..., Stop (interrupts.timer) --------------------------------------------
//
// Run the program's [Interrupt(Event.Timer)] method every so long (the first time that long from now), or once, until
// TimerStop or another call; 0 or less: Every stops the timer, After runs it at once. Milliseconds above 2147483 count
// as 2147483. A time missed while a handler ran too long comes at once, so Every runs it as many times as the time says.

void ChipSharp_Runtime_TimerEveryMilliseconds(chipsharp_i32 milliseconds) CHIPSHARP_REENTRANT;

void ChipSharp_Runtime_TimerEveryMicroseconds(chipsharp_i32 microseconds) CHIPSHARP_REENTRANT;

void ChipSharp_Runtime_TimerAfterMilliseconds(chipsharp_i32 milliseconds) CHIPSHARP_REENTRANT;

void ChipSharp_Runtime_TimerAfterMicroseconds(chipsharp_i32 microseconds) CHIPSHARP_REENTRANT;

void ChipSharp_Runtime_TimerStop(void) CHIPSHARP_REENTRANT;

// ---- The pin event: Pin.Watch... (interrupts.pin) -------------------------------------------------------------------
//
// From now on the pin's changes (both edges, rising, falling; Unwatch: none) run the program's [Interrupt(Event.Pin)]
// method; changes from before are forgotten. One method handles every pin watched. A pin the chip cannot watch is a
// mistake (the PFS154 watches PA0 and PB0 only).

void ChipSharp_Runtime_PinWatch(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

void ChipSharp_Runtime_PinWatchRising(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

void ChipSharp_Runtime_PinWatchFalling(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

void ChipSharp_Runtime_PinUnwatch(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

// ---- The clock: DateTime.Now, Clock.Start, Stopwatch, Environment.TickCount (clock) ---------------------------------
//
// From the chip's timer: a counter of microseconds since start-up (64 bits, so it never goes round), and a software
// clock of DateTime's ticks of 100 ns, 1 January 2000 until Clock.Start gives it a date. A chip keeps no time zone: its
// local time is UTC. Read and set with interrupts off, so a handler never sees half of one.

// DateTime.UtcNow's ticks.
chipsharp_i64 ChipSharp_Runtime_ClockUtcTicks(void) CHIPSHARP_REENTRANT;

// DateTime.Now's ticks: the same.
chipsharp_i64 ChipSharp_Runtime_ClockLocalTicks(void) CHIPSHARP_REENTRANT;

// Clock.Start: the clock counts from these ticks from now on.
void ChipSharp_Runtime_ClockStart(chipsharp_i64 ticks) CHIPSHARP_REENTRANT;

// Stopwatch.GetTimestamp: microseconds since start-up.
chipsharp_i64 ChipSharp_Runtime_ClockTimestamp(void) CHIPSHARP_REENTRANT;

// Stopwatch.Frequency: 1000000.
chipsharp_i64 ChipSharp_Runtime_ClockFrequency(void) CHIPSHARP_REENTRANT;

// Stopwatch.GetElapsedTime's ticks from one timestamp to another: (end - start) * 10.
chipsharp_i64 ChipSharp_Runtime_ClockElapsedTicks(chipsharp_i64 start, chipsharp_i64 end) CHIPSHARP_REENTRANT;

// Environment.TickCount64: milliseconds since start-up.
chipsharp_i64 ChipSharp_Runtime_ClockTickCount64(void) CHIPSHARP_REENTRANT;

// ---- Time zones (chip) ----------------------------------------------------------------------------------------------

// The offset of local time from UTC, in ticks, which a date's z and K formats write: 0 (the architecture's runtime
// has it, weak).
chipsharp_i64 ChipSharp_Runtime_LocalUtcOffset(void) CHIPSHARP_REENTRANT;

// ---- The console: Console.Write and Console.WriteLine (console) -----------------------------------------------------

// Where the program's text goes (chipsharp_console.c makes it: UTF-8, lines ending with "\r\n"): count bytes, sent
// before it returns (polling: it may run with interrupts off), set up on the first call (the Pico's UART0 and the
// Uno's USART0 at 115200 baud). On an AVR an address with bit 15 set is 0x8000 plus a flash address.
void chipsharp_console_write(const char *bytes, chipsharp_count count) CHIPSHARP_REENTRANT;

// ---- The console's input: Console.Read (consoleInput) ---------------------------------------------------------------

// The next char of the console's input (UTF-8 in, UTF-16 out, as .NET's), waiting for it; -1 never comes.
chipsharp_i32 ChipSharp_Runtime_ConsoleRead(void);

// ---- Serial ports: ChipSharp.Hardware.Uart (uart) -------------------------------------------------------------------
//
// Ports by number from 0 (the target's "uart" is how many); port 0 may be the console's too. Received bytes go to a
// buffer from UartOpen on, filled by the port's interrupt.

// Uart.Open: the port on its TX and RX pins (the target's "pinFunctions" uart<n>.tx and .rx) at a speed, 8N1.
void ChipSharp_Runtime_UartOpen(chipsharp_i32 port, chipsharp_i32 tx, chipsharp_i32 rx, chipsharp_i32 baud) CHIPSHARP_REENTRANT;

// Sends a byte (0 to 255) once the port has room for it.
void ChipSharp_Runtime_UartWriteByte(chipsharp_i32 port, chipsharp_i32 value) CHIPSHARP_REENTRANT;

// The next byte received, or -1 when none is waiting.
chipsharp_i32 ChipSharp_Runtime_UartReadByte(chipsharp_i32 port) CHIPSHARP_REENTRANT;

// How many bytes received are waiting.
chipsharp_i32 ChipSharp_Runtime_UartAvailable(chipsharp_i32 port) CHIPSHARP_REENTRANT;

// Uart.Watch: from now on bytes arriving on the port run the program's [Interrupt(Event.Uart)] method (the target's
// interrupts.uart), once they are in its buffer. Unwatch stops it.
void ChipSharp_Runtime_UartWatch(chipsharp_i32 port) CHIPSHARP_REENTRANT;

void ChipSharp_Runtime_UartUnwatch(chipsharp_i32 port) CHIPSHARP_REENTRANT;

// ---- The I2C buses: ChipSharp.Hardware.I2c (i2c) --------------------------------------------------------------------
//
// Buses by number from 0, on the pins and clocks of the target's "i2c" table. A transfer gives up (and returns 0)
// when nothing answers its address, or a device holds the clock low for longer than the bytes would take.

// I2c.Open: the bus as controller on its SDA and SCL pins (their pull-ups on) at the nearest frequency at or below.
void ChipSharp_Runtime_I2cOpen(chipsharp_i32 bus, chipsharp_i32 sda, chipsharp_i32 scl, chipsharp_i32 frequency) CHIPSHARP_REENTRANT;

// Sends count bytes from data on to the 7-bit address, with a repeated start when the transfer before was a write
// without a stop, and a stop after them when stop is not 0. 1 when every byte was taken, else 0.
chipsharp_i32 ChipSharp_Runtime_I2cWrite(chipsharp_i32 bus, chipsharp_i32 address, const chipsharp_u8 *data, chipsharp_i32 count, chipsharp_i32 stop) CHIPSHARP_REENTRANT;

// Reads count bytes into data on from the address (a repeated start as for I2cWrite), then stops. 1 when it answered.
chipsharp_i32 ChipSharp_Runtime_I2cRead(chipsharp_i32 bus, chipsharp_i32 address, chipsharp_u8 *data, chipsharp_i32 count) CHIPSHARP_REENTRANT;

// ---- The SPI buses: ChipSharp.Hardware.Spi (spi) --------------------------------------------------------------------
//
// Buses by number from 0, on the pins and clocks of the target's "spi" table, as controller, 8 bits a word, most
// significant bit first; a chip select is the program's own pin.

// Spi.Open: the bus on its SCK, TX (MOSI) and RX (MISO) pins at the nearest frequency at or below, in mode 0 to 3.
void ChipSharp_Runtime_SpiOpen(chipsharp_i32 bus, chipsharp_i32 sck, chipsharp_i32 tx, chipsharp_i32 rx, chipsharp_i32 frequency, chipsharp_i32 mode) CHIPSHARP_REENTRANT;

// Sends count bytes from data on, and drops what comes back.
void ChipSharp_Runtime_SpiWrite(chipsharp_i32 bus, const chipsharp_u8 *data, chipsharp_i32 count) CHIPSHARP_REENTRANT;

// Receives count bytes into data on, sending zeros.
void ChipSharp_Runtime_SpiRead(chipsharp_i32 bus, chipsharp_u8 *data, chipsharp_i32 count) CHIPSHARP_REENTRANT;

// Sends write_count bytes and receives as many; the program stops when read_count is not the same.
void ChipSharp_Runtime_SpiTransfer(chipsharp_i32 bus, const chipsharp_u8 *write, chipsharp_i32 write_count, chipsharp_u8 *read, chipsharp_i32 read_count) CHIPSHARP_REENTRANT;

// ---- Analog inputs: ChipSharp.Hardware.Adc (adc) --------------------------------------------------------------------
//
// The pins of the target's "pinFunctions" adc, each made an input first; readings in the chip's own range, 0 to
// AdcFullScale() - 1. The double ones are not on a chip without a 64-bit double.

// Adc.Input: the pin an analog input (its digital input off).
void ChipSharp_Runtime_AdcInput(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

// One reading of an analog input.
chipsharp_i32 ChipSharp_Runtime_AdcRead(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

#if CHIPSHARP_DOUBLE64
// Adc.Temperature: the chip's temperature in degrees Celsius, by its datasheet's formula.
chipsharp_f64 ChipSharp_Runtime_AdcTemperature(void) CHIPSHARP_REENTRANT;

// The volts a reading of AdcFullScale() would be: the board's reference.
chipsharp_f64 ChipSharp_Runtime_AdcReference(void) CHIPSHARP_REENTRANT;
#endif

// One more than the highest reading: 4096 for 12 bits, 1024 for 10.
chipsharp_i32 ChipSharp_Runtime_AdcFullScale(void) CHIPSHARP_REENTRANT;

// ---- PWM outputs: ChipSharp.Hardware.Pwm (pwm) ----------------------------------------------------------------------
//
// The pins of the target's "pinFunctions" pwm. An output's period starts high; a new duty or pulse takes effect at the
// next period, so no period is cut short.

// Pwm.Output: the pin a PWM output at the nearest frequency the chip makes, low (duty 0).
void ChipSharp_Runtime_PwmOutput(chipsharp_i32 pin, chipsharp_i32 frequency) CHIPSHARP_REENTRANT;

#if CHIPSHARP_DOUBLE64
// Pwm.Duty: the part of each period the output is high, 0 to 1.
void ChipSharp_Runtime_PwmDuty(chipsharp_i32 pin, chipsharp_f64 duty) CHIPSHARP_REENTRANT;
#endif

// Pwm.PulseMicroseconds: how long the output is high at the start of each period.
void ChipSharp_Runtime_PwmPulse(chipsharp_i32 pin, chipsharp_i32 microseconds) CHIPSHARP_REENTRANT;

// Pwm.Stop: the pin no longer driven, as after start-up (an input); its timer is free again once nothing uses it.
void ChipSharp_Runtime_PwmStop(chipsharp_i32 pin) CHIPSHARP_REENTRANT;

// ---- Failure output and the stops (chip) ----------------------------------------------------------------------------
//
// A program that does what .NET throws an exception for says so (the architecture's failures file has .NET's
// messages: "Unhandled exception. System.IndexOutOfRangeException: ...") and stops as a failure. How a program stops
// is what a debugger or a simulator (rp2040js, avr8js, chipsim) tells apart, the same way on every chip:
//
//   it ended    the chip sleeps with nothing left to wake it: the Cortex-M's WFI (its events may still run: interrupts
//               stay on), the AVR's SLEEP with interrupts off, the STM8's HALT (or WFI with no interrupt left to
//               come), the Padauk's STOPSYS or STOPEXE
//   it failed   the chip stops at its breakpoint: the Cortex-M's BKPT, the AVR's BREAK, the STM8's BREAK; the
//               Padauk, which has none, runs its RESET instruction, which a simulator stops at (on the chip it starts
//               the program again)

// Where the failure messages go, a piece at a time, text in RAM: the console when the chip has one (sent before it
// returns, polling: interrupts may be off), else nowhere. The architecture's runtime has an empty weak one.
void chipsharp_failure_write(const char *text, chipsharp_count count);

// What runs before the program stops, either way: the console sends what it still holds. Empty unless the chip's
// runtime has one (the architecture's is weak).
void chipsharp_before_halt(void);

// The program has ended: chipsharp_before_halt, then sleep with nothing left to wake the chip (above).
CHIPSHARP_NORETURN void chipsharp_halt(void);

// The program failed: chipsharp_before_halt, then the failure stop: the breakpoint, or the Padauk's RESET (above).
CHIPSHARP_NORETURN void chipsharp_halt_failure(void);

// A pin, port, bus, frequency or mode the chip does not have, or a peripheral used before it was set up: "Unhandled
// exception. " and the text (".NET's exception type: message"), with the numbers n0 to n3 in place of {0} to {3}, as
// string.Format writes them, where chipsharp_failure_write writes; then chipsharp_halt_failure.
CHIPSHARP_NORETURN void chipsharp_hardware_failure(CHIPSHARP_MESSAGE_CHAR *text, chipsharp_i32 n0, chipsharp_i32 n1, chipsharp_i32 n2, chipsharp_i32 n3);

#endif
