namespace ChipSharp.Simulators.Devices;

/// <summary>
/// The InvenSense MPU-6050 accelerometer and gyroscope, at I2C address 0x68 (0x69 with AD0 high), as its register map
/// (RM-MPU-6000A) describes it and tools/rp2040-sim/devices/mpu6050.mjs models it: WHO_AM_I (0x75) is 0x68; it starts
/// asleep (PWR_MGMT_1, 0x6B, is 0x40), with its measurements reading 0 until the program wakes it (writes 0 there);
/// ACCEL_CONFIG (0x1C) and GYRO_CONFIG (0x1B) choose the ranges; the measurements are big-endian 16-bit values from 0x3B
/// on: acceleration X, Y, Z, temperature, rotation X, Y, Z. It measures a board lying still but tilted: 0.5 g, -0.25 g
/// and 1 g, a rotation of 10, -5 and 0 degrees per second, and 24 degrees Celsius.
/// </summary>
public sealed class Mpu6050 : RegisterDevice
{
    private const int GyroConfig = 0x1B, AccelConfig = 0x1C, AccelXoutH = 0x3B, GyroZoutL = 0x48, PwrMgmt1 = 0x6B;
    private const int WhoAmI = 0x75, Sleep = 0x40, DeviceReset = 0x80;

    public static IReadOnlyList<double> Acceleration { get; } = [0.5, -0.25, 1.0];

    public static IReadOnlyList<double> Rotation { get; } = [10, -5, 0];

    public const double Temperature = 24;

    public Mpu6050()
        : base("MPU-6050") => Reset();

    private void Reset()
    {
        Array.Clear(Registers);
        Registers[PwrMgmt1] = Sleep;
        Registers[WhoAmI] = 0x68;
    }

    protected override void Written(int register, byte value)
    {
        if (register == PwrMgmt1 && (value & DeviceReset) != 0)
        {
            Reset();
        }
    }

    protected override byte Value(int register)
    {
        if (register is < AccelXoutH or > GyroZoutL)
        {
            return Registers[register];
        }

        // asleep, it has measured nothing
        if ((Registers[PwrMgmt1] & Sleep) != 0)
        {
            return 0;
        }

        var measured = new byte[14];
        var accelerationRange = (Registers[AccelConfig] >> 3) & 3; // 2, 4, 8 or 16 g
        var rotationRange = (Registers[GyroConfig] >> 3) & 3;       // 250, 500, 1000 or 2000 degrees/s
        for (var axis = 0; axis < 3; axis++)
        {
            SetBigEndian(measured, 2 * axis, (int)Math.Round(Acceleration[axis] * 16384 / (1 << accelerationRange)));
            SetBigEndian(measured, 8 + 2 * axis, (int)Math.Round(Rotation[axis] * 131 / (1 << rotationRange)));
        }

        SetBigEndian(measured, 6, (int)Math.Round((Temperature - 36.53) * 340));
        return measured[register - AccelXoutH];
    }
}

/// <summary>
/// The Bosch BMP280 pressure and temperature sensor, at I2C address 0x76 (0x77 with SDO high), as its datasheet
/// (BST-BMP280-DS001) describes it and tools/rp2040-sim/devices/bmp280.mjs models it: chip id (0xD0) 0x58; reset (0xE0) on
/// 0xB6; status (0xF3), ctrl_meas (0xF4) and config (0xF5); the calibration words dig_T1 to dig_P9, little-endian, from
/// 0x88; the raw pressure (0xF7 to 0xF9) and temperature (0xFA to 0xFC), 20 bits each, most significant byte first. Its
/// calibration and its measurements are the datasheet's own example (section 3.12): adc_T = 519888 and adc_P = 415148,
/// 25.08 degrees Celsius and 100653.27 Pa. A measurement it has not made (in sleep mode, as after a reset, or one skipped
/// by an oversampling of 0) reads 0x80000, as on the chip.
/// </summary>
public sealed class Bmp280 : RegisterDevice
{
    private const int Calibration = 0x88, ChipId = 0xD0, ResetRegister = 0xE0, Status = 0xF3, CtrlMeas = 0xF4;
    private const int PressMsb = 0xF7, TempXlsb = 0xFC, ResetWord = 0xB6, NotMeasured = 0x80000;

    /// <summary>dig_T1, dig_T2, dig_T3, dig_P1, ..., dig_P9 (T1 and P1 unsigned, the others signed).</summary>
    public static IReadOnlyList<int> CalibrationWords { get; } =
        [27504, 26435, -1000, 36477, -10685, 3024, 2855, 140, -7, 15500, -14600, 6000];

    public const int AdcT = 519888, AdcP = 415148;

    public Bmp280()
        : base("BMP280") => Reset();

    private void Reset()
    {
        Array.Clear(Registers);
        Registers[ChipId] = 0x58;
        for (var index = 0; index < CalibrationWords.Count; index++)
        {
            SetLittleEndian(Registers, Calibration + 2 * index, CalibrationWords[index] & 0xFFFF);
        }
    }

    protected override void Written(int register, byte value)
    {
        if (register == ResetRegister && value == ResetWord)
        {
            Reset();
        }
    }

    protected override byte Value(int register)
    {
        if (register is ResetRegister or Status)
        {
            return 0; // reset reads as 0; never measuring, never copying its calibration: a measurement is there at once
        }

        if (register is < PressMsb or > TempXlsb)
        {
            return Registers[register];
        }

        // ctrl_meas: osrs_t (bits 7-5), osrs_p (4-2), mode (1-0; 0 is sleep)
        var control = Registers[CtrlMeas];
        var measuring = (control & 3) != 0;
        var pressure = measuring && ((control >> 2) & 7) != 0 ? AdcP : NotMeasured;
        var temperature = measuring && ((control >> 5) & 7) != 0 ? AdcT : NotMeasured;
        var raw = register < 0xFA ? pressure : temperature;
        return ((register - PressMsb) % 3) switch
        {
            0 => (byte)(raw >> 12),
            1 => (byte)(raw >> 4),
            _ => (byte)((raw << 4) & 0xF0),
        };
    }
}
