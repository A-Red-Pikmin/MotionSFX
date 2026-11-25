namespace MotionSFX.Core;

/// <summary>
/// Applies SlimeVR's physical-tracker eligibility rules to a tracker record.
/// BodyPart.WAIST=4, BodyPart.HIP=5, TrackerStatus.OK=2 in SolarXR Protocol.
/// </summary>
public static class PhysicalHipTrackerDetector
{
    public const byte WaistBodyPart = 4;
    public const byte HipBodyPart = 5;
    public const byte TrackerStatusOk = 2;

    public static bool IsConnectedPhysicalHipTracker(bool isComputed, byte bodyPart, byte status)
    {
        return !isComputed && status == TrackerStatusOk &&
            (bodyPart == WaistBodyPart || bodyPart == HipBodyPart);
    }
}
