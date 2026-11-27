// ChipSharp runtime for ARMv6-M chips (Cortex-M0 and M0+): what happens when a program does something .NET throws an
// exception for, on a chip whose runtime does not say it another way.
//
// ChipSharp has no exceptions yet, so the program stops the way a division by zero does (chipsharp_divide_by_zero):
// at a breakpoint, so that a debugger stops there; without one the breakpoint becomes a HardFault, whose handler sleeps
// forever. A chip with a console says what happened first: its runtime defines these functions again (the RP2040's in
// rp2040_failures.c, over UART0), and since these are weak, the linker takes the chip's.
//
//   ChipSharp_Runtime_IndexOutOfRange    an array index outside the array (IndexOutOfRangeException)
//   ChipSharp_Runtime_TimeSpan...        a TimeSpan too long, or NaN given to one (ChipSharp.Library's System.TimeSpan)
//   ChipSharp_Runtime_NegateOverflow     the negative of the smallest value, e.g. -TimeSpan.MinValue
//   ChipSharp_Runtime_NullReference      a field, element or method of null (NullReferenceException)
//   ChipSharp_Runtime_OutOfMemory        an object that does not fit in the heap (OutOfMemoryException)
//   ChipSharp_Runtime_Overflow           an array of a negative length (OverflowException)
//   ChipSharp_Runtime_HeapInInterrupt    an interrupt handler that makes an object, which it never may
//   ChipSharp_Runtime_ArgumentOutOfRange, LengthOutOfRange, DestinationTooShort, DataMisaligned
//                                        a span's start or length outside it, too few bytes for MemoryMarshal.Read or
//                                        Write, a CopyTo a shorter span, a MemoryMarshal.Cast this chip cannot read
//   ChipSharp_Runtime_InvalidCast        a cast to a type the object is not of (InvalidCastException)
//   ChipSharp_Runtime_ArrayNull, ...     the arguments of the methods of System.Array (Array.Copy, Fill, IndexOf, ...):
//                                        null, an index or a length outside the array, a destination too short
//
// The TimeSpan ones and NegateOverflow are the methods of ChipSharp.Library.Runtime, which says when each happens (so
// are the four array ones Array.Sort and Array.BinarySearch call); InvalidCast is the program's casts'; the others are
// the code generator's and the heap's (chipsharp_heap.c, armv6m_heap.c).

__attribute__((noreturn)) static void fail(void)
{
    for (;;)
    {
        __asm__ volatile("bkpt #0\n\twfi");
    }
}

__attribute__((weak, noreturn)) void ChipSharp_Runtime_IndexOutOfRange(void) { fail(); }
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
__attribute__((weak, noreturn)) void ChipSharp_Runtime_LengthBelowZero(int value) { (void)value; fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_SourceIndexBelowZero(int value) { (void)value; fail(); }
__attribute__((weak, noreturn)) void ChipSharp_Runtime_DestinationIndexBelowZero(int value) { (void)value; fail(); }
