namespace ChipSharp.Simulators.Esp32c5;

/// <summary>
/// The ESP32-C5's memory map and the addresses of the peripherals the simulator models, in one table. The memories are
/// ESP-IDF's (components/soc/esp32c5/include/soc/soc.h: SOC_IROM_MASK_*, SOC_IRAM_*, SOC_RTC_IRAM_*, SOC_IROM_*), the
/// CPU's own registers the Technical Reference Manual's (v1.1, Table 2.7-2: the CLINT at 0x2000_0000, the CLIC at
/// 0x2080_0000), the peripherals' base addresses Espressif's SVD (esp-pacs, esp32c5.base.svd; ESP-IDF's reg_base.h
/// agrees), and the interrupt sources ESP-IDF's soc/interrupts.h (the SVD's &lt;interrupt&gt; numbers agree). The
/// interrupt matrix's map registers are at 4 × the source, as ESP-IDF's interrupt_matrix_reg.h has them: the SVD's
/// INTERRUPT_CORE0 leaves out the maps of CPU_APM_M0 and M1 (sources 42 and 43), so its maps from MSPI on (UART0_INTR_MAP
/// at 0xb4, not 0xbc) and its INT_STATUS registers (0x140, not 0x150) are off.
/// </summary>
public static class Esp32c5Map
{
    /// <summary>The mask ROM (320 KB), where the ROM's functions are (components/esp_rom/esp32c5/ld/esp32c5.rom.ld).</summary>
    public const uint RomStart = 0x4000_0000, RomEnd = 0x4005_0000;

    /// <summary>The HP SRAM (384 KB), instructions and data.</summary>
    public const uint SramStart = 0x4080_0000, SramEnd = 0x4086_0000;

    /// <summary>The LP SRAM (16 KB).</summary>
    public const uint LpSramStart = 0x5000_0000, LpSramEnd = 0x5000_4000;

    /// <summary>Flash (and PSRAM) through the cache and its MMU: 32 MB of addresses.</summary>
    public const uint FlashMapStart = 0x4200_0000, FlashMapEnd = 0x4400_0000;

    /// <summary>The CLINT (msip, mtimecmp, mtime) and the CLIC, the CPU's own.</summary>
    public const uint Clint = 0x2000_0000, Clic = 0x2080_0000;

    /// <summary>The peripherals' registers.</summary>
    public const uint PeripheralStart = 0x6000_0000, PeripheralEnd = 0x6010_0000;

    public const uint Uart0 = 0x6000_0000, Timg0 = 0x6000_8000, Timg1 = 0x6000_9000, Systimer = 0x6000_A000,
        UsbSerialJtag = 0x6000_F000, InterruptMatrix = 0x6001_0000, IoMux = 0x6009_0000, Gpio = 0x6009_1000,
        Pcr = 0x6009_6000, LpWdt = 0x600B_1C00, Intpri = 0x600C_5000, I2c0 = 0x6000_4000, Ledc = 0x6000_7000,
        ApbSaradc = 0x6000_E000, Spi2 = 0x6008_1000;

    /// <summary>The interrupt sources the simulator raises (ETS_..._SOURCE), by ESP-IDF's names.</summary>
    public const int LpWdtSource = 18, FromCpu0Source = 23, GpioSource = 31, Uart0Source = 47, UsbSerialJtagSource = 54,
        Tg0WdtSource = 58, Tg1WdtSource = 60, SystimerTarget0Source = 61;

    /// <summary>The interrupt matrix's sources (ETS_MAX_INTR_SOURCE).</summary>
    public const int SourceCount = 84;

    /// <summary>The GPIO pins: GPIO0 to GPIO28.</summary>
    public const int GpioCount = 29;

    /// <summary>The clocks: the devkit's 48 MHz crystal, the PLL's outputs, RC_FAST (the TRM's 20 MHz), RC_SLOW (ESP-IDF's 136 kHz).</summary>
    public const long XtalHz = 48_000_000, Pll160Hz = 160_000_000, Pll240Hz = 240_000_000, Pll80Hz = 80_000_000,
        RcFastHz = 20_000_000, RcSlowHz = 136_000;

    /// <summary>The value that unlocks a watchdog's registers (TIMG_WDT_WKEY, LP_WDT_WKEY, LP_WDT_SWD_WKEY).</summary>
    public const uint WatchdogKey = 0x50D8_3AA1;

    /// <summary>The peripherals the simulator models, by the SVD's names, and their base addresses (for the tests).</summary>
    public static IReadOnlyDictionary<string, uint> Peripherals { get; } = new Dictionary<string, uint>
    {
        ["UART0"] = Uart0, ["TIMG0"] = Timg0, ["TIMG1"] = Timg1, ["SYSTIMER"] = Systimer, ["USB_DEVICE"] = UsbSerialJtag,
        ["INTERRUPT_CORE0"] = InterruptMatrix, ["IO_MUX"] = IoMux, ["GPIO"] = Gpio, ["PCR"] = Pcr, ["LP_WDT"] = LpWdt,
        ["INTPRI"] = Intpri, ["I2C0"] = I2c0, ["LEDC"] = Ledc, ["APB_SARADC"] = ApbSaradc, ["SPI2"] = Spi2,
    };

    /// <summary>The interrupt sources the simulator raises, by the SVD's names of its &lt;interrupt&gt;s.</summary>
    public static IReadOnlyDictionary<string, int> Interrupts { get; } = new Dictionary<string, int>
    {
        ["LP_WDT"] = LpWdtSource, ["FROM_CPU_INTR0"] = FromCpu0Source, ["FROM_CPU_INTR1"] = FromCpu0Source + 1,
        ["FROM_CPU_INTR2"] = FromCpu0Source + 2, ["FROM_CPU_INTR3"] = FromCpu0Source + 3, ["GPIO"] = GpioSource,
        ["UART0"] = Uart0Source, ["USB_SERIAL_JTAG"] = UsbSerialJtagSource, ["TG0_WDT_LEVEL"] = Tg0WdtSource,
        ["TG1_WDT_LEVEL"] = Tg1WdtSource, ["SYSTIMER_TARGET0"] = SystimerTarget0Source,
        ["SYSTIMER_TARGET1"] = SystimerTarget0Source + 1, ["SYSTIMER_TARGET2"] = SystimerTarget0Source + 2,
    };
}
