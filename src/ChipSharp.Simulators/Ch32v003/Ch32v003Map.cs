namespace ChipSharp.Simulators.Ch32v003;

/// <summary>
/// The WCH CH32V003's memory map and the addresses of the peripherals the simulator models, in one table: the memories
/// are the CH32V003 Reference Manual's (V1.6, Figure 1-2 and 1.2.1: 16 KB of flash at 0x0800_0000, aliased at 0 where
/// the chip boots, 2 KB of SRAM at 0x2000_0000; the option bytes at 0x1FFF_F800, ESIG at 0x1FFF_F7E0), the peripherals'
/// base addresses WCH's SVD (chips/svd/CH32V003xx.svd) and EVT's ch32v00x.h, the core's PFIC and SysTick (STK) the
/// QingKeV2 Processor Manual's (Tables 3-2 and 4-1), and the interrupt numbers the RM's Table 6-1 (WCH's
/// startup_ch32v00x.S has its vector table in that order).
/// </summary>
public static class Ch32v003Map
{
    /// <summary>The code flash (16 KB) and its alias at 0, where the chip boots from the user area.</summary>
    public const uint FlashStart = 0x0800_0000, FlashSize = 16 * 1024, FlashAlias = 0;

    /// <summary>The SRAM (2 KB).</summary>
    public const uint SramStart = 0x2000_0000, SramSize = 2 * 1024;

    /// <summary>ESIG: the flash's size in KB (16 bits) and the 96-bit unique ID (UNIID1-3 at 0x1FFF_F7E8 on).</summary>
    public const uint EsigFlashCapacity = 0x1FFF_F7E0, EsigUniqueId = 0x1FFF_F7E8;

    /// <summary>The user option bytes (64 bytes): RDPR, USER, Data0-1, WRPR0-1 and their inverses.</summary>
    public const uint OptionBytes = 0x1FFF_F800, OptionBytesSize = 64;

    /// <summary>The peripherals' registers (APB and AHB).</summary>
    public const uint PeripheralStart = 0x4000_0000, PeripheralEnd = 0x4002_4000;

    public const uint Tim2 = 0x4000_0000, Wwdg = 0x4000_2C00, Iwdg = 0x4000_3000, I2c1 = 0x4000_5400, Pwr = 0x4000_7000,
        Afio = 0x4001_0000, Exti = 0x4001_0400, GpioA = 0x4001_0800, GpioC = 0x4001_1000, GpioD = 0x4001_1400,
        Adc1 = 0x4001_2400, Tim1 = 0x4001_2C00, Spi1 = 0x4001_3000, Usart1 = 0x4001_3800, Dma1 = 0x4002_0000,
        Rcc = 0x4002_1000, Flash = 0x4002_2000, Extend = 0x4002_3800;

    /// <summary>The core's own: the debug registers (DBGMCU), the PFIC and SysTick.</summary>
    public const uint Dbg = 0xE000_D000, Pfic = 0xE000_E000, SysTick = 0xE000_F000;

    /// <summary>The interrupt numbers the simulator raises (the RM's Table 6-1).</summary>
    public const int NmiNumber = 2, SysTickNumber = 12, SoftwareNumber = 14, WwdgNumber = 16, Exti7To0Number = 20,
        Usart1Number = 32;

    /// <summary>The PFIC's interrupts: 0-38 on the CH32V003 (TIM2's 38 the last).</summary>
    public const int InterruptCount = 39;

    /// <summary>The clocks: HSI (the 24 MHz RC oscillator), the PLL (HSI × 2), LSI (128 kHz).</summary>
    public const long HsiHz = 24_000_000, PllHz = 48_000_000, LsiHz = 128_000;

    /// <summary>
    /// The option bytes as WCH ships the chip, as the RM's reset values (16.5, Table 16-4) give them: RDPR 0xA5 (not read
    /// protected), USER 0xF7 (IWDG_SW 1: the IWDG is started by software; STANDBY_RST 1; RST_MODE 10: PD7 is NRST;
    /// START_MODE 1), Data0-1 0xFF, WRPR0-1 0xFF (no sector write protected); each byte followed by its inverse.
    /// </summary>
    public static IReadOnlyList<byte> FactoryOptionBytes { get; } =
        [0xA5, 0x5A, 0xF7, 0x08, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00];

    /// <summary>The peripherals the simulator models, by the SVD's names, and their base addresses (for the tests).</summary>
    public static IReadOnlyDictionary<string, uint> Peripherals { get; } = new Dictionary<string, uint>
    {
        ["TIM2"] = Tim2, ["WWDG"] = Wwdg, ["IWDG"] = Iwdg, ["I2C1"] = I2c1, ["PWR"] = Pwr, ["AFIO"] = Afio, ["EXTI"] = Exti,
        ["GPIOA"] = GpioA, ["GPIOC"] = GpioC, ["GPIOD"] = GpioD, ["ADC1"] = Adc1, ["TIM1"] = Tim1, ["SPI1"] = Spi1,
        ["USART1"] = Usart1, ["DMA1"] = Dma1, ["RCC"] = Rcc, ["FLASH"] = Flash, ["EXTEN"] = Extend, ["DBG"] = Dbg,
        ["PFIC"] = Pfic,
    };

    /// <summary>The interrupts the simulator raises, by the SVD's names of its &lt;interrupt&gt;s.</summary>
    public static IReadOnlyDictionary<string, int> Interrupts { get; } = new Dictionary<string, int>
    {
        ["WWDG"] = WwdgNumber, ["EXTI7_0"] = Exti7To0Number, ["USART1"] = Usart1Number,
    };
}
