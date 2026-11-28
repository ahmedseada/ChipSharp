// ChipSharp runtime for the Espressif ESP32-C5: the console, on the chip's USB Serial/JTAG controller (the devkit's
// "ESP32-C5 USB Type-C Port", GPIO13 D- and GPIO14 D+, which the controller owns from reset: no pin or clock to set up).
//
// Freestanding, like esp32c5_runtime.c. A host that has the port open (a terminal on the serial port the devkit shows,
// 115200 or any speed: USB has none) reads what the program writes and writes what Console.Read returns.
//
//   chipsharp_console_write            where Console.Write and Console.WriteLine write (the text, with lines that end with
//                                      "\r\n" like .NET's on Windows, comes from chipsharp_console.c): the bytes go into
//                                      EP1's 64-byte IN FIFO, which WR_DONE sends as a packet when the write is over
//   chipsharp_failure_write            the contract's failure output: the same place, so one terminal shows both
//   chipsharp_before_halt              what runs before the program stops: sends what the FIFO still holds
//   ChipSharp_Runtime_ConsoleRead      Console.Read (and ReadLine, which reads with it): the next char of the host's
//                                      input, UTF-8 decoded as .NET decodes it; sleeps until a packet comes
//
// Writing. EP1 takes a byte at a time while EP1_CONF's SERIAL_IN_EP_DATA_FREE is set; the FIFO is free again once the host
// has fetched the packet (an IN token, which the host sends as often as its driver polls: a millisecond or less). With no
// host at the other end (the port closed, or the board on a charger) the FIFO never empties: the first bytes fit in it
// and the next would wait for ever. So a write waits at most WAIT_MICROSECONDS (ESP-IDF's console waits as long) for room,
// and a port that did not take a packet in that time is taken as having no host: the bytes are dropped without waiting,
// until the FIFO has room again (the host came back). The console therefore never stops a program that nobody listens to.
//
// Reading. A packet from the host fills EP1's OUT FIFO (SERIAL_OUT_EP_DATA_AVAIL says it has bytes; reading EP1 takes
// one) and raises SERIAL_OUT_RECV_PKT, which is the interrupt source USB_SERIAL_JTAG (routed to CLIC id 21, whose handler
// ends the wait). The host sends its next packet as the FIFO empties, so no byte is lost while the program is busy.

#include "chipsharp_hardware.h"
#include "esp32c5_chip.h"

#define WAIT_MICROSECONDS 50000u

static unsigned int usb_listening = 1; // a packet was taken by the host (or none was sent yet): worth waiting for
static unsigned int usb_pending;       // bytes are in the FIFO that WR_DONE has not sent

static int usb_free(void)
{
    return (USB_DEVICE_EP1_CONF & USB_DEVICE_EP1_CONF_SERIAL_IN_EP_DATA_FREE_MASK) != 0;
}

// Sends what the FIFO holds as a packet.
static void usb_send(void)
{
    if (usb_pending)
    {
        USB_DEVICE_INT_CLR = USB_DEVICE_INT_CLR_SERIAL_IN_EMPTY_INT_CLR_MASK;
        USB_DEVICE_EP1_CONF = USB_DEVICE_EP1_CONF_WR_DONE_MASK;
        usb_pending = 0;
    }
}

// Waits for room in the FIFO, as above: true when there is. What is in it goes out first, so that the host can take it.
static int usb_room(void)
{
    if (usb_free())
    {
        usb_listening = 1;
        return 1;
    }

    usb_send();
    if (!usb_listening)
    {
        return 0;
    }

    unsigned long long end = chipsharp_c5_ticks() + (unsigned long long)WAIT_MICROSECONDS * C5_TICKS_PER_MICROSECOND;
    while (!usb_free())
    {
        if (chipsharp_c5_ticks() >= end)
        {
            usb_listening = 0;
            return 0;
        }
    }

    return 1;
}

void chipsharp_c5_usb_write(const char *bytes, unsigned int count)
{
    for (unsigned int i = 0; i < count; i++)
    {
        if (!usb_room())
        {
            break; // nobody takes them: the rest is dropped
        }

        USB_DEVICE_EP1 = (unsigned char)bytes[i];
        usb_pending = 1;
    }

    usb_send();
}

// Console.Write and Console.WriteLine (chipsharp_console.c) write here: text as the UTF-8 bytes of its literal.
void chipsharp_console_write(const char *bytes, chipsharp_count count)
{
    chipsharp_c5_usb_write(bytes, count);
}

// The hardware contract's failure output: the console, where esp32c5_failures.c's messages go.
void chipsharp_failure_write(const char *text, chipsharp_count count)
{
    chipsharp_c5_usb_write(text, count);
}

// The program stops (it ended, or failed): what the FIFO holds goes out, and the host takes it (SERIAL_IN_EMPTY), unless
// there is no host: the core then sleeps for good, and a debugger or the simulator sees the bytes taken.
void chipsharp_before_halt(void)
{
    usb_send();
    if (!usb_listening)
    {
        return;
    }

    unsigned long long end = chipsharp_c5_ticks() + (unsigned long long)WAIT_MICROSECONDS * C5_TICKS_PER_MICROSECOND;
    while ((USB_DEVICE_INT_RAW & USB_DEVICE_INT_RAW_SERIAL_IN_EMPTY_INT_RAW_MASK) == 0 && chipsharp_c5_ticks() < end)
    {
    }
}

// ---- Console.Read ------------------------------------------------------------------------------------------------

static volatile unsigned int usb_woken;
static unsigned int usb_receiving; // the interrupt for a packet from the host is routed and enabled

// A packet came from the host: the interrupt is cleared and the wait for it ends.
void chipsharp_isr_irq21(void)
{
    USB_DEVICE_INT_CLR = USB_DEVICE_INT_CLR_SERIAL_OUT_RECV_PKT_INT_CLR_MASK;
    chipsharp_c5_wake(&usb_woken);
}

// A byte read too far (not part of the char before it), which the next char starts with; -1 for none.
static int console_next = -1;

// The second half (a low surrogate) of a char above U+FFFF, which the next Console.Read returns; 0 for none.
static unsigned int console_low_surrogate;

// The next byte of the host's input, asleep until a packet comes.
static unsigned int console_byte(void)
{
    if (console_next >= 0)
    {
        unsigned int value = (unsigned int)console_next;
        console_next = -1;
        return value;
    }

    if (!usb_receiving)
    {
        c5_route(ESP32C5_IRQ_USB_SERIAL_JTAG, C5_ID_USB_RECEIVE);
        c5_clic_enable(C5_ID_USB_RECEIVE);
        USB_DEVICE_INT_CLR = USB_DEVICE_INT_CLR_SERIAL_OUT_RECV_PKT_INT_CLR_MASK;
        USB_DEVICE_INT_ENA |= USB_DEVICE_INT_ENA_SERIAL_OUT_RECV_PKT_INT_ENA_MASK;
        usb_receiving = 1;
    }

    for (;;)
    {
        usb_woken = 0;
        if ((USB_DEVICE_EP1_CONF & USB_DEVICE_EP1_CONF_SERIAL_OUT_EP_DATA_AVAIL_MASK) != 0)
        {
            return USB_DEVICE_EP1 & 0xffu;
        }

        // what the program wrote waits for its host, who may be about to answer it
        usb_send();
        chipsharp_c5_sleep(&usb_woken);
    }
}

// Console.Read: the next char of the console's input, as .NET reads UTF-8: a char of one to three bytes, or the two
// halves of one of four bytes (a surrogate pair), and U+FFFD for each byte that is not part of a well-formed one. It
// never ends: a serial line has no end of input.
chipsharp_i32 ChipSharp_Runtime_ConsoleRead(void)
{
    if (console_low_surrogate != 0)
    {
        unsigned int low = console_low_surrogate;
        console_low_surrogate = 0;
        return (chipsharp_i32)low;
    }

    unsigned int first = console_byte();
    unsigned int count;
    unsigned int value;
    unsigned int low = 0x80u; // the range the next byte must be in (the second one's is narrower after some firsts)
    unsigned int high = 0xbfu;
    if (first < 0x80u)
    {
        return (chipsharp_i32)first;
    }
    else if (first >= 0xc2u && first <= 0xdfu)
    {
        count = 1;
        value = first & 0x1fu;
    }
    else if (first >= 0xe0u && first <= 0xefu)
    {
        count = 2;
        value = first & 0x0fu;
        low = first == 0xe0u ? 0xa0u : low;   // not an overlong form
        high = first == 0xedu ? 0x9fu : high; // not a surrogate
    }
    else if (first >= 0xf0u && first <= 0xf4u)
    {
        count = 3;
        value = first & 0x07u;
        low = first == 0xf0u ? 0x90u : low;   // not an overlong form
        high = first == 0xf4u ? 0x8fu : high; // not above U+10FFFF
    }
    else
    {
        return 0xfffd;
    }

    for (unsigned int i = 0; i < count; i++)
    {
        unsigned int next = console_byte();
        if (next < low || next > high)
        {
            console_next = (int)next;
            return 0xfffd;
        }

        value = (value << 6) | (next & 0x3fu);
        low = 0x80u;
        high = 0xbfu;
    }

    if (value >= 0x10000u)
    {
        value -= 0x10000u;
        console_low_surrogate = 0xdc00u | (value & 0x3ffu);
        return (chipsharp_i32)(0xd800u | (value >> 10));
    }

    return (chipsharp_i32)value;
}
