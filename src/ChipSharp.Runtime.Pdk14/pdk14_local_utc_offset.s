; ChipSharp runtime for Padauk PDK14 chips: the offset of local time from UTC that a date's z and K formats write
; (ChipSharp.Library's Runtime.LocalUtcOffset): 0, as a chip keeps no time zone. A file of its own in the library.
;
;   chipsharp_i64 ChipSharp_Runtime_LocalUtcOffset(void) __reentrant

    .module pdk14_local_utc_offset
    .globl _ChipSharp_Runtime_LocalUtcOffset
    .globl _chipsharp_pdk14_arguments
    .globl _chipsharp_pdk14_result_8

    .area CODE
_ChipSharp_Runtime_LocalUtcOffset::
    disgint
    clear _chipsharp_pdk14_arguments+0
    clear _chipsharp_pdk14_arguments+1
    clear _chipsharp_pdk14_arguments+2
    clear _chipsharp_pdk14_arguments+3
    clear _chipsharp_pdk14_arguments+4
    clear _chipsharp_pdk14_arguments+5
    clear _chipsharp_pdk14_arguments+6
    clear _chipsharp_pdk14_arguments+7
    goto _chipsharp_pdk14_result_8
