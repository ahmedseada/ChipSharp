// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): what happens when a program does something .NET throws an
// exception for, on a chip whose runtime does not say it another way.
//
// ChipSharp has no exceptions yet, so the program stops the way a division by zero does (chipsharp_divide_by_zero):
// at a breakpoint, so that a debugger stops there; without one the breakpoint becomes a HardFault, whose handler sleeps
// forever. A chip with a console says what happened first: its runtime defines these functions again (the RP2040's in
// rp2040_failures.c, over UART0), and since these are weak, the linker takes the chip's.
//
//   ChipSharp_Runtime_IndexOutOfRange    an array index outside the array (IndexOutOfRangeException)
//   ChipSharp_Runtime_DivideByZero       a whole number divided by zero, x / 0 or x % 0 (DivideByZeroException)
//   ChipSharp_Runtime_TimeSpan...        a TimeSpan too long, or NaN given to one (ChipSharp.Library's System.TimeSpan)
//   ChipSharp_Runtime_NegateOverflow     the negative of the smallest value, e.g. -TimeSpan.MinValue
//   ChipSharp_Runtime_NullReference      a field, element or method of null (NullReferenceException)
//   ChipSharp_Runtime_OutOfMemory        an object that does not fit in the heap (OutOfMemoryException)
//   ChipSharp_Runtime_Overflow           an array of a negative length, int.MinValue / -1 (OverflowException)
//   ChipSharp_Runtime_HeapInInterrupt    an interrupt handler that makes an object, which it never may
//   ChipSharp_Runtime_ArgumentOutOfRange, LengthOutOfRange, DestinationTooShort, DataMisaligned
//                                        a span's start or length outside it, too few bytes for MemoryMarshal.Read or
//                                        Write, a CopyTo a shorter span, a MemoryMarshal.Cast this chip cannot read
//   ChipSharp_Runtime_Unhandled          C#'s throw: an exception nothing catches, with its ToString() (as UTF-8, from
//                                        chipsharp_exceptions.c), as .NET prints it
//   ChipSharp_Runtime_InvalidCast        a cast to a type the object is not of (InvalidCastException)
//   ChipSharp_Runtime_EnumCompareType    IComparable's CompareTo of a boxed enum and a box of another type
//   ChipSharp_Runtime_ArrayNull, ...     the arguments of the methods of System.Array (Array.Copy, Fill, IndexOf, ...):
//                                        null, an index or a length outside the array, a destination too short
//   ChipSharp_Runtime_ArithmeticNaN, ...MinMax..., RoundingDigits, InvalidMidpointRounding
//                                        the methods of System.Math: Sign of NaN, Clamp given a min above its max, Round
//                                        given too many digits or a MidpointRounding that is none of its five
//
// The TimeSpan ones and NegateOverflow are the methods of ChipSharp.Library.Runtime, which says when each happens (so
// are the four array ones Array.Sort and Array.BinarySearch call); InvalidCast is the program's casts'; the others are
// the code generator's and the heap's (chipsharp_heap.c, armv6m_heap.c).
//
// And the hardware contract's (chipsharp_hardware.h) failure output and stop, for code shared with the other chips:
//
//   chipsharp_failure_write              where a failure's message goes: nowhere here (weak; the RP2040's is UART0)
//   chipsharp_halt_failure               the failure stop: chipsharp_before_halt, then the breakpoint

#include "chipsharp_hardware.h"

__attribute__((noreturn)) static void fail(void)
{
    for (;;)
    {
        __asm__ volatile("bkpt #0\n\twfi");
    }
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_IndexOutOfRange(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_DivideByZero(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanOverflow(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanOutOfRange(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanNaN(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanNaNFactor(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanNaNDivisor(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_TimeSpanDurationOverflow(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_NegateOverflow(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_NullReference(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_OutOfMemory(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_Overflow(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_HeapInInterrupt(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_ArgumentOutOfRange(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_LengthOutOfRange(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_DestinationTooShort(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_DataMisaligned(void) { fail(); }

__attribute__((weak, noreturn)) void ChipSharp_Runtime_InvalidCast(const char *from, const char *to)
{
    (void)from;
    (void)to;
    fail();
}

__attribute__((weak, noreturn)) void chipsharp_fail(const char *message) { (void)message; fail(); }

__attribute__((weak, noreturn)) void ChipSharp_Runtime_EnumCompareType(const char *passed, const char *type)
{
    (void)passed;
    (void)type;
    fail();
}
__attribute__((weak, noreturn)) void ChipSharp_Runtime_ArrayNull(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_SourceArrayNull(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_DestinationArrayNull(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_SourceArrayTooShort(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_DestinationArrayTooShort(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_HugeSourceIndex(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_HugeDestinationIndex(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_HugeLength(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_HugeIndex(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_StartIndexPastEnd(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_StartIndexAtEnd(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_CountOutOfRange(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_NegativeIndex(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_NegativeLength(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_NegativeNewSize(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_InvalidOffsetLength(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_MatchNull(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_ActionNull(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_ConverterNull(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_ComparisonNull(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_DelegateTypes(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_LengthBelowZero(int value) { (void)value; fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_SourceIndexBelowZero(int value) { (void)value; fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_DestinationIndexBelowZero(int value) { (void)value; fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_Unhandled(const void *text) { (void)text; fail(); }

// the methods of System.Math: Sign of NaN, Clamp given a min above its max, Round given too many digits or a
// MidpointRounding that is none of its five
__attribute__((weak, noreturn)) void ChipSharp_Runtime_ArithmeticNaN(void) { fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxInt64(long long min, long long max)
{
    (void)min;
    (void)max;
    fail();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxUInt64(unsigned long long min, unsigned long long max)
{
    (void)min;
    (void)max;
    fail();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxFloat64(double min, double max)
{
    (void)min;
    (void)max;
    fail();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxFloat32(float min, float max)
{
    (void)min;
    (void)max;
    fail();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_RoundingDigits(int maximum) { (void)maximum; fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_InvalidMidpointRounding(int mode) { (void)mode; fail(); }

// ---- The stack, and fatal errors ----------------------------------------------------------------------------------
//
//   chipsharp_stack_overflow           a function that checks the stack found it below __chipsharp_stack_limit (the
//                                      code generator's check, LlvmStackChecks): it would soon run into the variables
//                                      or the heap. The stack pointer may be below the stack already, so this turns
//                                      interrupts off (nothing of the program runs again), puts the stack pointer back
//                                      at the top of the stack, and calls ChipSharp_Runtime_StackOverflow there
//   ChipSharp_Runtime_StackOverflow    says so ("Stack overflow.", .NET's words, on a chip with a console) and stops
//   chipsharp_fatal                    a fatal error, the whole line given (the heap verifier's, chipsharp_heap.c)

__attribute__((naked, noreturn)) void chipsharp_stack_overflow(void)
{
    __asm__ volatile(
        "cpsid i\n\t"
        "ldr r0, 1f\n\t"
        "mov sp, r0\n\t"
        "bl ChipSharp_Runtime_StackOverflow\n\t"
        ".balign 4\n"
        "1: .word __chipsharp_stack_top");
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_StackOverflow(void) { fail(); }
__attribute__((weak, noreturn)) void chipsharp_fatal(const char *message) { (void)message; fail(); }

__attribute__((weak)) void chipsharp_failure_write(const char *text, unsigned int count)
{
    (void)text;
    (void)count;
}

__attribute__((noreturn)) void chipsharp_halt_failure(void)
{
    chipsharp_before_halt();
    fail();
}
