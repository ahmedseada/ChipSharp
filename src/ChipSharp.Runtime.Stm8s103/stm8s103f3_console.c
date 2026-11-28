// ChipSharp runtime for the STM8S103F3: UART1's sending, where the console's output (Console.Write and WriteLine, whose
// text comes from chipsharp_console.c) and the failures' messages (the STM8 runtime's stm8_failures.c) go.
//
// UART1 sends on PD5 (TX) at 115200 baud, 8N1, set up on the first write unless Uart.Open or Console.Read
// (stm8s103f3_uart.c) did: 16 MHz / 139 is 115108 baud, 0.08% slow. A byte goes out when the one before has (polling
// TXE, no send buffer, which would take RAM): 87 microseconds each; the check and the write go together with interrupts
// off, so that a handler that writes too never puts its byte in between, and the wait leaves them on. The STM8 reads
// flash and RAM at the same addresses, so the text is sent from where it is.
//
//   chipsharp_uart1_start, chipsharp_uart1_write   UART1 set up, and bytes sent (for stm8s103f3_uart.c too)
//   chipsharp_console_write                        Console.Write's bytes
//   chipsharp_failure_write                        a failure's message, a piece at a time; then chipsharp_halt_failure
//                                                  stops the program, after chipsharp_before_halt, which waits for the
//                                                  last byte to go out (stm8s103f3_runtime.c)

#include "chipsharp_hardware.h"
#include "stm8s103f3_runtime.h"

u8 chipsharp_uart1_started;

// What it was sending goes out first, at the speed it had; the receiver and its interrupt are on when receive is set.
// BRR2 holds the divider's highest and lowest four bits and is written first; BRR1, its middle eight, loads both.
void chipsharp_uart1_start(u16 divider, u8 receive)
{
    if (chipsharp_uart1_sent)
    {
        while ((UART1_SR & UART1_SR_TC_MASK) == 0)
        {
        }
    }

    u8 cc = chipsharp_interrupts_off();
    UART1_CR2 = 0;
    UART1_CR1 = 0; // 8 data bits, no parity
    UART1_CR3 = 0; // one stop bit
    UART1_BRR2 = (u8)(((divider >> 8) & 0xf0u) | (divider & 0x0fu));
    UART1_BRR1 = (u8)(divider >> 4);
    UART1_CR2 = UART1_CR2_TEN_MASK | (receive ? UART1_CR2_REN_MASK | UART1_CR2_RIEN_MASK : 0u);
    chipsharp_uart1_started = 1;
    chipsharp_uart1_sent = 0;
    chipsharp_interrupts_back(cc);
}

static void send(u8 value)
{
    for (;;)
    {
        while ((UART1_SR & UART1_SR_TXE_MASK) == 0)
        {
        }

        u8 cc = chipsharp_interrupts_off();
        if ((UART1_SR & UART1_SR_TXE_MASK) != 0)
        {
            UART1_DR = value;
            chipsharp_uart1_sent = 1;
            chipsharp_interrupts_back(cc);
            return;
        }

        chipsharp_interrupts_back(cc);
    }
}

void chipsharp_uart1_write(const char *bytes, chipsharp_count count)
{
    if (!chipsharp_uart1_started)
    {
        chipsharp_uart1_start(DIVIDER_115200, 0);
    }

    for (chipsharp_count i = 0; i < count; i++)
    {
        send((u8)bytes[i]);
    }
}

void chipsharp_console_write(const char *bytes, chipsharp_count count)
{
    chipsharp_uart1_write(bytes, count);
}

void chipsharp_failure_write(const char *text, chipsharp_count count)
{
    chipsharp_uart1_write(text, count);
}
