// ChipSharp runtime for the STM8S103F3: the handlers of the vector table, which SDCC writes in the file with main() (the
// STM8 runtime's stm8_startup.c) from the handlers declared there. They are stm8s103f3_runtime.c's, in every program:
// TIM1's update counts the clock's rounds; the others go to the hooks of the modules a program has (the timer event,
// the pin event, UART1's receiving).

#ifndef STM8_VECTORS_H
#define STM8_VECTORS_H

#include "stm8s103f3_interrupts.h"

STM8S103F3_HANDLER(EXTI_PORTA);
STM8S103F3_HANDLER(EXTI_PORTB);
STM8S103F3_HANDLER(EXTI_PORTC);
STM8S103F3_HANDLER(EXTI_PORTD);
STM8S103F3_HANDLER(TIM1_UPD_OVF);
STM8S103F3_HANDLER(TIM1_CAP_COM);
STM8S103F3_HANDLER(UART1_RX);

#endif
