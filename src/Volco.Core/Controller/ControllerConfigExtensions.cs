using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>Google.Protobuf returns null for an absent submessage, while nanopb fills it with the
/// proto2 defaults. Read submessages through these, never directly (spec §6.1).</summary>
public static class ControllerConfigExtensions
{
    public static ScreenBehaviour EffectiveScreens(this ControllerConfig config) => config.Screens ?? new ScreenBehaviour();
    public static InputTuning EffectiveInput(this ControllerConfig config) => config.Input ?? new InputTuning();
    public static Udp EffectiveUdp(this ControllerConfig config) => config.Udp ?? new Udp();
}
