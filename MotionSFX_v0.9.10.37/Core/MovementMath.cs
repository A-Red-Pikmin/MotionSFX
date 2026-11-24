using System.Diagnostics;

namespace MotionSFX.Core;

internal readonly record struct MovementTransform(
    double X, double Y, double Z,
    double QX, double QY, double QZ, double QW);

internal readonly record struct MovementMeasurement(double Position, double RotationDegrees);

/// <summary>
/// Shared tracker-to-preset transform math. All presets use the same Euclidean
/// position delta and quaternion angular delta. VMC /Pos is avatar-scale; /Pos/Local
/// is device-raw scale. Samples are paired only within the same coordinate space.
/// </summary>
internal static class MovementMath
{
    // Preserve the original detector's 75 ms target/reference window. When samples
    // bracket a target timestamp, interpolate the reference instead of taking a
    // potentially stale nearest pose (important for HMD/chest rotation).
    public const double MaximumReferenceSampleSkewMs = 75.0;

    public static string GetReferenceRole(string targetRole)
    {
        if (targetRole is "LeftFoot" or "RightFoot" or "LeftUpperLeg" or "RightUpperLeg" or "LeftLowerLeg" or "RightLowerLeg") return "Hip";
        if (targetRole is "Head" or "LeftHand" or "RightHand") return "Chest";
        if (targetRole == "Chest") return "Hip";
        return "";
    }

    public static double GetTimestampSkewMs(long firstTimestamp, long secondTimestamp)
    {
        if (firstTimestamp <= 0 || secondTimestamp <= 0) return double.MaxValue;
        return Math.Abs((double)(firstTimestamp - secondTimestamp) * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>
    /// Selects a single coordinate space shared by a preset's target and reference.
    /// GLOBAL avatar-scale data is preferred when the complete pair is available;
    /// otherwise LOCAL device-raw data is used. Mixing the two scales is never valid.
    /// </summary>
    public static string SelectCoordinateSpace(
        IReadOnlyList<MotionSfxVmcSample> globalTargets,
        IReadOnlyList<MotionSfxVmcSample> globalReferences,
        IReadOnlyList<MotionSfxVmcSample> localTargets,
        IReadOnlyList<MotionSfxVmcSample> localReferences,
        long targetTimestamp,
        bool requiresReference,
        out double referenceSkewMs)
    {
        referenceSkewMs = 0;
        if (HasAlignedTargetReference(globalTargets, globalReferences, targetTimestamp, requiresReference, out double globalSkew))
        {
            referenceSkewMs = globalSkew;
            return "GLOBAL";
        }
        if (HasAlignedTargetReference(localTargets, localReferences, targetTimestamp, requiresReference, out double localSkew))
        {
            referenceSkewMs = localSkew;
            return "LOCAL";
        }
        return string.Empty;
    }

    private static bool HasAlignedTargetReference(
        IReadOnlyList<MotionSfxVmcSample> targets,
        IReadOnlyList<MotionSfxVmcSample> references,
        long targetTimestamp,
        bool requiresReference,
        out double referenceSkewMs)
    {
        referenceSkewMs = 0;
        if (targets == null || targets.Count == 0) return false;
        MotionSfxVmcSample nearestTarget = null;
        double nearestTargetSkew = double.MaxValue;
        foreach (MotionSfxVmcSample candidate in targets)
        {
            if (candidate == null || candidate.ReceivedTimestamp <= 0) continue;
            double skew = GetTimestampSkewMs(targetTimestamp, candidate.ReceivedTimestamp);
            if (skew < nearestTargetSkew)
            {
                nearestTargetSkew = skew;
                nearestTarget = candidate;
            }
        }
        if (nearestTarget == null || nearestTargetSkew > MaximumReferenceSampleSkewMs) return false;
        if (!requiresReference) return true;
        return TryAlignReference(references, nearestTarget.ReceivedTimestamp, out _, out referenceSkewMs);
    }

    public static string[] GetAllowedRolesForKind(string kind) => kind switch
    {
        "TRA" => new[] { "LeftFoot", "RightFoot", "Hip", "Chest", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg", "Other" },
        "HMD" => new[] { "Head" },
        "CON" => new[] { "LeftHand", "RightHand" },
        _ => new[] { "Other" }
    };

    /// <summary>
    /// Aligns a reference pose to a target sample using monotonic receive timestamps.
    /// Prefer linear-position + quaternion-SLERP interpolation between surrounding
    /// reference samples; otherwise use the nearest sample within the original window.
    /// </summary>
    public static bool TryAlignReference(
        IReadOnlyList<MotionSfxVmcSample> references,
        long targetTimestamp,
        out MovementTransform alignedReference,
        out double nearestSkewMs)
    {
        alignedReference = default;
        nearestSkewMs = double.MaxValue;
        if (references == null || references.Count == 0 || targetTimestamp <= 0) return false;

        MotionSfxVmcSample before = null;
        MotionSfxVmcSample after = null;
        MotionSfxVmcSample nearest = null;
        foreach (MotionSfxVmcSample candidate in references)
        {
            if (candidate == null || candidate.ReceivedTimestamp <= 0) continue;
            double skew = GetTimestampSkewMs(targetTimestamp, candidate.ReceivedTimestamp);
            if (skew < nearestSkewMs)
            {
                nearestSkewMs = skew;
                nearest = candidate;
            }

            if (candidate.ReceivedTimestamp <= targetTimestamp &&
                (before == null || candidate.ReceivedTimestamp > before.ReceivedTimestamp))
                before = candidate;
            if (candidate.ReceivedTimestamp >= targetTimestamp &&
                (after == null || candidate.ReceivedTimestamp < after.ReceivedTimestamp))
                after = candidate;
        }

        if (before != null && after != null)
        {
            double beforeSkew = GetTimestampSkewMs(targetTimestamp, before.ReceivedTimestamp);
            double afterSkew = GetTimestampSkewMs(targetTimestamp, after.ReceivedTimestamp);
            if (beforeSkew <= MaximumReferenceSampleSkewMs && afterSkew <= MaximumReferenceSampleSkewMs)
            {
                if (before.ReceivedTimestamp == after.ReceivedTimestamp)
                {
                    alignedReference = FromSample(before);
                    nearestSkewMs = 0;
                    return true;
                }

                double fraction = (double)(targetTimestamp - before.ReceivedTimestamp) /
                    (after.ReceivedTimestamp - before.ReceivedTimestamp);
                alignedReference = Interpolate(FromSample(before), FromSample(after), fraction);
                nearestSkewMs = Math.Max(beforeSkew, afterSkew);
                return true;
            }
        }

        if (nearest != null && nearestSkewMs <= MaximumReferenceSampleSkewMs)
        {
            alignedReference = FromSample(nearest);
            return true;
        }

        return false;
    }

    public static MovementTransform Interpolate(MovementTransform first, MovementTransform second, double fraction)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        QuaternionD q = Slerp(
            new QuaternionD(first.QX, first.QY, first.QZ, first.QW),
            new QuaternionD(second.QX, second.QY, second.QZ, second.QW), fraction);
        return new MovementTransform(
            first.X + (second.X - first.X) * fraction,
            first.Y + (second.Y - first.Y) * fraction,
            first.Z + (second.Z - first.Z) * fraction,
            q.X, q.Y, q.Z, q.W);
    }

    public static MovementTransform ToRelativeTransform(MovementTransform target, MovementTransform reference)
    {
        QuaternionD referenceRotation = Normalize(new QuaternionD(reference.QX, reference.QY, reference.QZ, reference.QW));
        QuaternionD inverseReference = new(-referenceRotation.X, -referenceRotation.Y, -referenceRotation.Z, referenceRotation.W);
        Vector3D relativePosition = Rotate(inverseReference,
            new Vector3D(target.X - reference.X, target.Y - reference.Y, target.Z - reference.Z));
        QuaternionD targetRotation = Normalize(new QuaternionD(target.QX, target.QY, target.QZ, target.QW));
        QuaternionD relativeRotation = Multiply(inverseReference, targetRotation);
        relativeRotation = Normalize(relativeRotation);
        return new MovementTransform(relativePosition.X, relativePosition.Y, relativePosition.Z,
            relativeRotation.X, relativeRotation.Y, relativeRotation.Z, relativeRotation.W);
    }

    public static MovementMeasurement MeasureFromAnchor(MovementTransform anchor, MovementTransform sample)
    {
        double dx = sample.X - anchor.X;
        double dy = sample.Y - anchor.Y;
        double dz = sample.Z - anchor.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        QuaternionD anchorRotation = Normalize(new QuaternionD(anchor.QX, anchor.QY, anchor.QZ, anchor.QW));
        QuaternionD sampleRotation = Normalize(new QuaternionD(sample.QX, sample.QY, sample.QZ, sample.QW));
        double dot = Math.Abs(anchorRotation.X * sampleRotation.X + anchorRotation.Y * sampleRotation.Y +
            anchorRotation.Z * sampleRotation.Z + anchorRotation.W * sampleRotation.W);
        dot = Math.Clamp(dot, 0.0, 1.0);
        double angle = 2.0 * Math.Acos(dot) * (180.0 / Math.PI);
        return new MovementMeasurement(distance, angle);
    }

    public static MovementTransform FromSample(MotionSfxVmcSample sample) =>
        new(sample.X, sample.Y, sample.Z, sample.QX, sample.QY, sample.QZ, sample.QW);

    public static MovementTransform FromAnchor(MovementAnchor anchor) =>
        new(anchor.X, anchor.Y, anchor.Z, anchor.QX, anchor.QY, anchor.QZ, anchor.QW);

    private readonly record struct Vector3D(double X, double Y, double Z);
    private readonly record struct QuaternionD(double X, double Y, double Z, double W);

    private static QuaternionD Normalize(QuaternionD value)
    {
        double norm = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z + value.W * value.W);
        return norm > 0.000001
            ? new QuaternionD(value.X / norm, value.Y / norm, value.Z / norm, value.W / norm)
            : new QuaternionD(0, 0, 0, 1);
    }

    private static QuaternionD Multiply(QuaternionD a, QuaternionD b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    private static Vector3D Rotate(QuaternionD rotation, Vector3D value)
    {
        QuaternionD v = new(value.X, value.Y, value.Z, 0);
        QuaternionD inverse = new(-rotation.X, -rotation.Y, -rotation.Z, rotation.W);
        QuaternionD result = Multiply(Multiply(rotation, v), inverse);
        return new Vector3D(result.X, result.Y, result.Z);
    }

    private static QuaternionD Slerp(QuaternionD first, QuaternionD second, double fraction)
    {
        QuaternionD a = Normalize(first);
        QuaternionD b = Normalize(second);
        double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
        if (dot < 0.0)
        {
            b = new QuaternionD(-b.X, -b.Y, -b.Z, -b.W);
            dot = -dot;
        }
        dot = Math.Clamp(dot, -1.0, 1.0);
        if (dot > 0.9995)
        {
            return Normalize(new QuaternionD(
                a.X + fraction * (b.X - a.X), a.Y + fraction * (b.Y - a.Y),
                a.Z + fraction * (b.Z - a.Z), a.W + fraction * (b.W - a.W)));
        }

        double theta = Math.Acos(dot);
        double sinTheta = Math.Sin(theta);
        double firstWeight = Math.Sin((1.0 - fraction) * theta) / sinTheta;
        double secondWeight = Math.Sin(fraction * theta) / sinTheta;
        return Normalize(new QuaternionD(
            firstWeight * a.X + secondWeight * b.X,
            firstWeight * a.Y + secondWeight * b.Y,
            firstWeight * a.Z + secondWeight * b.Z,
            firstWeight * a.W + secondWeight * b.W));
    }
}
