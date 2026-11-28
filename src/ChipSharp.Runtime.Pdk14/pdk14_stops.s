; ChipSharp runtime for Padauk PDK14 chips: how a program stops, the two ways a simulator (chipsim) tells apart.
;
;   chipsharp_halt           the program has ended: chipsharp_before_halt (the chip runtime's turns off what could
;                            wake the chip: a pin's change), interrupts off, then stopsys for good (the clocks stop; on
;                            a wake-up it stops again)
;   chipsharp_halt_failure   the program failed: chipsharp_before_halt, interrupts off, then reset, which a simulator
;                            stops at (the PDK14 has no breakpoint); on the chip it starts the program again
;
; And every failure a program can have that takes no parameters: a PDK14 chip has no console to say what happened
; (.NET's "Unhandled exception. System.IndexOutOfRangeException: ...", which the other chips' failures files write), so
; each is the failure stop itself, another name for chipsharp_halt_failure (no word of program memory of its own). The
; failures that take parameters are files of their own (Pdk14Runtime's ArgumentFailures): SDCC keeps a function's
; parameters in RAM, which only a program that can fail so gives them.

    .module pdk14_stops
    .globl _chipsharp_halt
    .globl _chipsharp_halt_failure
    .globl _chipsharp_before_halt

    .area CODE
_chipsharp_halt::
    call _chipsharp_before_halt
    disgint
00001$:
    stopsys
    goto 00001$

_ChipSharp_Runtime_IndexOutOfRange::
_ChipSharp_Runtime_DivideByZero::
_ChipSharp_Runtime_TimeSpanOverflow::
_ChipSharp_Runtime_TimeSpanOutOfRange::
_ChipSharp_Runtime_TimeSpanNaN::
_ChipSharp_Runtime_TimeSpanNaNFactor::
_ChipSharp_Runtime_TimeSpanNaNDivisor::
_ChipSharp_Runtime_TimeSpanDurationOverflow::
_ChipSharp_Runtime_NegateOverflow::
_ChipSharp_Runtime_NullReference::
_ChipSharp_Runtime_OutOfMemory::
_ChipSharp_Runtime_Overflow::
_ChipSharp_Runtime_ArgumentOutOfRange::
_ChipSharp_Runtime_LengthOutOfRange::
_ChipSharp_Runtime_DestinationTooShort::
_ChipSharp_Runtime_DataMisaligned::
_ChipSharp_Runtime_HeapInInterrupt::
_ChipSharp_Runtime_ArrayNull::
_ChipSharp_Runtime_SourceArrayNull::
_ChipSharp_Runtime_DestinationArrayNull::
_ChipSharp_Runtime_MatchNull::
_ChipSharp_Runtime_ActionNull::
_ChipSharp_Runtime_ConverterNull::
_ChipSharp_Runtime_ComparisonNull::
_ChipSharp_Runtime_DelegateTypes::
_ChipSharp_Runtime_SourceArrayTooShort::
_ChipSharp_Runtime_DestinationArrayTooShort::
_ChipSharp_Runtime_HugeSourceIndex::
_ChipSharp_Runtime_HugeDestinationIndex::
_ChipSharp_Runtime_HugeLength::
_ChipSharp_Runtime_HugeIndex::
_ChipSharp_Runtime_StartIndexPastEnd::
_ChipSharp_Runtime_StartIndexAtEnd::
_ChipSharp_Runtime_CountOutOfRange::
_ChipSharp_Runtime_NegativeIndex::
_ChipSharp_Runtime_NegativeLength::
_ChipSharp_Runtime_NegativeNewSize::
_ChipSharp_Runtime_InvalidOffsetLength::
_ChipSharp_Runtime_ArithmeticNaN::
_ChipSharp_Runtime_StackOverflow::
_chipsharp_halt_failure::
    call _chipsharp_before_halt
    disgint
    reset
