// ChipSharp runtime for AVR chips: what happens when a program does something .NET throws an exception for, on a chip
// whose runtime does not say it another way.
//
// The program stops as a failure (chipsharp_halt_failure, avr_runtime.c): BREAK, so that a debugger or the simulator
// stops there, then interrupts off and SLEEP forever. A chip with a console says what happened first: its runtime
// defines these functions again, writing .NET's message (as rp2040_failures.c does over UART0), and since these are
// weak, the linker takes the chip's. Its messages belong in flash (__flash, read with LPM): RAM is 2 KB on the
// ATmega328P, and a string constant in C is copied into RAM with .data.
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
//   ChipSharp_Runtime_Unhandled          C#'s throw: an exception nothing catches, with its ToString() (UTF-8)
//   ChipSharp_Runtime_InvalidCast        a cast to a type the object is not of (InvalidCastException)
//   ChipSharp_Runtime_EnumCompareType    IComparable's CompareTo of a boxed enum and a box of another type
//   ChipSharp_Runtime_ArrayNull, ...     the arguments of the methods of System.Array (Array.Copy, Fill, IndexOf, ...):
//                                        null, an index or a length outside the array, a destination too short
//   ChipSharp_Runtime_ArithmeticNaN, ...MinMax..., RoundingDigits, InvalidMidpointRounding
//                                        the methods of System.Math: Sign of NaN, Clamp given a min above its max, Round
//                                        given too many digits or a MidpointRounding that is none of its five
//   chipsharp_fail                       any other failure, its exception and message given
//
// A C# int is 32 bits: a long in AVR's C, whose int has 16. A double is 64 bits: this file, like the whole runtime, is
// compiled with -mdouble=64.

typedef long chipsharp_int;

#define FAILURE(name)                                                                                                  \
    __attribute__((weak, noreturn)) void name(void) { chipsharp_halt_failure(); }

__attribute__((noreturn)) void chipsharp_halt_failure(void);

FAILURE(ChipSharp_Runtime_IndexOutOfRange)
FAILURE(ChipSharp_Runtime_DivideByZero)
FAILURE(ChipSharp_Runtime_TimeSpanOverflow)
FAILURE(ChipSharp_Runtime_TimeSpanOutOfRange)
FAILURE(ChipSharp_Runtime_TimeSpanNaN)
FAILURE(ChipSharp_Runtime_TimeSpanNaNFactor)
FAILURE(ChipSharp_Runtime_TimeSpanNaNDivisor)
FAILURE(ChipSharp_Runtime_TimeSpanDurationOverflow)
FAILURE(ChipSharp_Runtime_NegateOverflow)
FAILURE(ChipSharp_Runtime_NullReference)
FAILURE(ChipSharp_Runtime_OutOfMemory)
FAILURE(ChipSharp_Runtime_Overflow)
FAILURE(ChipSharp_Runtime_HeapInInterrupt)
FAILURE(ChipSharp_Runtime_ArgumentOutOfRange)
FAILURE(ChipSharp_Runtime_LengthOutOfRange)
FAILURE(ChipSharp_Runtime_DestinationTooShort)
FAILURE(ChipSharp_Runtime_DataMisaligned)
FAILURE(ChipSharp_Runtime_ArrayNull)
FAILURE(ChipSharp_Runtime_SourceArrayNull)
FAILURE(ChipSharp_Runtime_DestinationArrayNull)
FAILURE(ChipSharp_Runtime_SourceArrayTooShort)
FAILURE(ChipSharp_Runtime_DestinationArrayTooShort)
FAILURE(ChipSharp_Runtime_HugeSourceIndex)
FAILURE(ChipSharp_Runtime_HugeDestinationIndex)
FAILURE(ChipSharp_Runtime_HugeLength)
FAILURE(ChipSharp_Runtime_HugeIndex)
FAILURE(ChipSharp_Runtime_StartIndexPastEnd)
FAILURE(ChipSharp_Runtime_StartIndexAtEnd)
FAILURE(ChipSharp_Runtime_CountOutOfRange)
FAILURE(ChipSharp_Runtime_NegativeIndex)
FAILURE(ChipSharp_Runtime_NegativeLength)
FAILURE(ChipSharp_Runtime_NegativeNewSize)
FAILURE(ChipSharp_Runtime_InvalidOffsetLength)
FAILURE(ChipSharp_Runtime_MatchNull)
FAILURE(ChipSharp_Runtime_ActionNull)
FAILURE(ChipSharp_Runtime_ConverterNull)
FAILURE(ChipSharp_Runtime_ComparisonNull)
FAILURE(ChipSharp_Runtime_DelegateTypes)
FAILURE(ChipSharp_Runtime_ArithmeticNaN)

__attribute__((weak, noreturn)) void ChipSharp_Runtime_InvalidCast(const char *from, const char *to)
{
    (void)from;
    (void)to;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_EnumCompareType(const char *passed, const char *type)
{
    (void)passed;
    (void)type;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void chipsharp_fail(const char *message)
{
    (void)message;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_Unhandled(const void *text)
{
    (void)text;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_LengthBelowZero(chipsharp_int value)
{
    (void)value;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_SourceIndexBelowZero(chipsharp_int value)
{
    (void)value;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_DestinationIndexBelowZero(chipsharp_int value)
{
    (void)value;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxInt64(long long min, long long max)
{
    (void)min;
    (void)max;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxUInt64(unsigned long long min, unsigned long long max)
{
    (void)min;
    (void)max;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxFloat64(double min, double max)
{
    (void)min;
    (void)max;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_MinMaxFloat32(float min, float max)
{
    (void)min;
    (void)max;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_RoundingDigits(chipsharp_int maximum)
{
    (void)maximum;
    chipsharp_halt_failure();
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_InvalidMidpointRounding(chipsharp_int mode)
{
    (void)mode;
    chipsharp_halt_failure();
}

// ---- The stack, and fatal errors ----------------------------------------------------------------------------------
//
//   chipsharp_stack_overflow           a function that checks the stack found it below __chipsharp_stack_limit (the
//                                      code generator's check, LlvmStackChecks): it would soon run into the variables
//                                      or the heap. The stack pointer may be below the stack already, so this turns
//                                      interrupts off (nothing of the program runs again), puts the stack pointer back
//                                      at the end of RAM, and calls ChipSharp_Runtime_StackOverflow there
//   ChipSharp_Runtime_StackOverflow    says so ("Stack overflow.", .NET's words, on a chip with a console) and stops
//   chipsharp_fatal                    a fatal error, the whole line given (the heap verifier's, chipsharp_heap.c)

#if defined(__AVR_HAVE_JMP_CALL__)
#define XJMP "jmp"
#else
#define XJMP "rjmp"
#endif

__attribute__((naked, noreturn)) void chipsharp_stack_overflow(void)
{
    __asm__ volatile(
        "cli\n\t"
        "ldi r28, lo8(__chipsharp_stack_top - 1)\n\t"
        "ldi r29, hi8(__chipsharp_stack_top - 1)\n\t"
        "out 0x3e, r29\n\t"
        "out 0x3d, r28\n\t"
        "clr r1\n\t" XJMP " ChipSharp_Runtime_StackOverflow");
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_StackOverflow(void) { chipsharp_halt_failure(); }
__attribute__((weak, noreturn)) void chipsharp_fatal(const char *message)
{
    (void)message;
    chipsharp_halt_failure();
}
