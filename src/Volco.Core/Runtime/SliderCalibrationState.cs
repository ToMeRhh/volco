using Volco.Core.Communication;

namespace Volco.Core.Runtime;

/// <summary>A slider's calibration as the controller last reported it (slider-calibration spec):
/// whether its span is learned, whether a sweep is running, its Auto-calibrate setting and its two
/// raw ends (4095 and 0 before anything is learned).</summary>
public sealed record SliderCalibrationState(bool Learned, bool Sweeping, bool AutoCalibrate, ushort Low, ushort High)
{
    public static SliderCalibrationState From(CalibrationMessage m)
        => new(m.Learned, m.Sweeping, m.AutoCalibrate, m.Low, m.High);
}
