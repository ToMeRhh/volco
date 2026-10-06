namespace Volco.Core.Controller;

/// <summary>ConfigResult codes (spec §5.4); also the code of a <see cref="ValidationResult"/>.</summary>
public enum ConfigResultCode : byte
{
    Ok = 0,
    BadSequence = 1,
    BadCrc = 2,
    TooLarge = 3,
    DecodeFailed = 4,
    Invalid = 5,
    StorageFailed = 6,
}

/// <summary>The field an INVALID result points at (spec §6.4).</summary>
public enum ConfigField : byte
{
    SliderPin = 1, ButtonPin = 2, LedPin = 3, PairPinA = 4, PairPinB = 5, ScreenBus = 6,
    ScreenAddress = 7, I2cMissing = 8, I2cPins = 9, I2cClock = 10, UdpSsid = 11, UdpPassword = 12,
    UdpPcIp = 13, UdpPort = 14, ScreenValue = 15, InputValue = 16,
}

/// <summary>The first violation of spec §6.4, the same triple the firmware would report.
/// <see cref="Item"/> is a slider/button index, an I2C bus, a range field number, or
/// <see cref="NoItem"/>; <see cref="Field"/> is a <see cref="ConfigField"/> or NoItem.</summary>
public readonly record struct ValidationResult(ConfigResultCode Code, byte Item, byte Field)
{
    public const byte NoItem = 0xFF;
    public static ValidationResult Ok { get; } = new(ConfigResultCode.Ok, NoItem, NoItem);
    public bool IsOk => Code == ConfigResultCode.Ok;

    public static ValidationResult Invalid(byte item, ConfigField field) => new(ConfigResultCode.Invalid, item, (byte)field);
}
