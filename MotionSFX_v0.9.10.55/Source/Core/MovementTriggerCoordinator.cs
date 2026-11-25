using System.Diagnostics;

namespace MotionSFX.Core;

internal enum MovementTriggerBlockReason
{
    None,
    IndividualCooldown,
    GlobalActivationDelay,
    Both
}

internal readonly record struct MovementTriggerAttemptResult(
    MovementTriggerBlockReason BlockReason,
    int IndividualCooldownRemainingMs,
    int GlobalActivationDelayRemainingMs)
{
    public bool CanSend => BlockReason == MovementTriggerBlockReason.None;
}

internal readonly record struct MovementTriggerRuntimeSnapshot(
    int IndividualCooldownRemainingMs,
    int GlobalActivationDelayRemainingMs,
    MovementTriggerBlockReason LastAttemptBlockReason,
    int LastAttemptAgeMs,
    bool HasLastAttempt,
    bool LastAttemptWasSuccessful,
    bool HasSuccessfulTrigger,
    int SinceLastSuccessfulTriggerMs);

/// <summary>
/// Tracks independent per-preset cooldowns and the optional global activation delay.
/// A movement attempt does not latch the preset: the movement detector re-baselines
/// after each qualifying attempt, matching the pre-v0.9.10.27 trigger behavior.
/// Only successful automatic shortcut sends update either cooldown timestamp.
/// All elapsed-time calculations use monotonic Stopwatch ticks.
/// </summary>
internal sealed class MovementTriggerCoordinator
{
    public const int MaximumCooldownMs = 10000;

    private sealed class PresetState
    {
        public bool HasSuccessfulTrigger;
        public long LastSuccessfulTriggerTimestamp;
        public bool HasLastAttempt;
        public long LastAttemptTimestamp;
        public MovementTriggerBlockReason LastAttemptBlockReason;
        public bool LastAttemptWasSuccessful;

        public void ResetAttemptState()
        {
            HasLastAttempt = false;
            LastAttemptTimestamp = 0;
            LastAttemptBlockReason = MovementTriggerBlockReason.None;
            LastAttemptWasSuccessful = false;
            // Deliberately preserve successful-trigger timestamps and cooldowns.
        }
    }

    private readonly Dictionary<string, PresetState> _presets = new(StringComparer.Ordinal);
    private bool _hasGlobalSuccessfulTrigger;
    private long _lastGlobalSuccessfulTriggerTimestamp;

    public MovementTriggerAttemptResult ConsumeMovement(
        string presetId, int individualCooldownMs, int globalActivationDelayMs, long nowTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        PresetState state = GetState(presetId);
        int individualRemaining = GetRemainingMs(state.HasSuccessfulTrigger,
            state.LastSuccessfulTriggerTimestamp, NormalizeDelay(individualCooldownMs), nowTimestamp);
        int globalRemaining = GetRemainingMs(_hasGlobalSuccessfulTrigger,
            _lastGlobalSuccessfulTriggerTimestamp, NormalizeDelay(globalActivationDelayMs), nowTimestamp);

        MovementTriggerBlockReason reason = (individualRemaining > 0, globalRemaining > 0) switch
        {
            (true, true) => MovementTriggerBlockReason.Both,
            (true, false) => MovementTriggerBlockReason.IndividualCooldown,
            (false, true) => MovementTriggerBlockReason.GlobalActivationDelay,
            _ => MovementTriggerBlockReason.None
        };

        // No latch or neutral-position gate is kept here. The caller consumes this
        // movement attempt by resetting its measurement anchor after the attempt.
        state.HasLastAttempt = true;
        state.LastAttemptTimestamp = nowTimestamp;
        state.LastAttemptBlockReason = reason;
        state.LastAttemptWasSuccessful = false;
        return new MovementTriggerAttemptResult(reason, individualRemaining, globalRemaining);
    }

    /// <summary>Call only after KeyboardInput.Send succeeds for an automatic trigger.</summary>
    public void RecordSuccessfulAutomaticShortcut(string presetId, long nowTimestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        PresetState state = GetState(presetId);
        state.HasSuccessfulTrigger = true;
        state.LastSuccessfulTriggerTimestamp = nowTimestamp;
        state.LastAttemptWasSuccessful = true;
        _hasGlobalSuccessfulTrigger = true;
        _lastGlobalSuccessfulTriggerTimestamp = nowTimestamp;
    }

    public MovementTriggerRuntimeSnapshot GetSnapshot(
        string presetId, int individualCooldownMs, int globalActivationDelayMs, long nowTimestamp)
    {
        PresetState state = GetState(presetId);
        int individualRemaining = GetRemainingMs(state.HasSuccessfulTrigger,
            state.LastSuccessfulTriggerTimestamp, NormalizeDelay(individualCooldownMs), nowTimestamp);
        int globalRemaining = GetRemainingMs(_hasGlobalSuccessfulTrigger,
            _lastGlobalSuccessfulTriggerTimestamp, NormalizeDelay(globalActivationDelayMs), nowTimestamp);
        int attemptAge = state.HasLastAttempt
            ? GetElapsedMsRounded(state.LastAttemptTimestamp, nowTimestamp)
            : int.MaxValue;
        int sinceLastSuccess = state.HasSuccessfulTrigger
            ? GetElapsedMsRounded(state.LastSuccessfulTriggerTimestamp, nowTimestamp)
            : int.MaxValue;

        return new MovementTriggerRuntimeSnapshot(
            individualRemaining,
            globalRemaining,
            state.LastAttemptBlockReason,
            attemptAge,
            state.HasLastAttempt,
            state.LastAttemptWasSuccessful,
            state.HasSuccessfulTrigger,
            sinceLastSuccess);
    }

    /// <summary>
    /// Clear transient attempt status when tracker baselines are re-established,
    /// without resetting any active individual or global cooldown timestamps.
    /// </summary>
    public void ResetMovementAttemptStatesPreservingCooldowns()
    {
        foreach (PresetState state in _presets.Values)
            state.ResetAttemptState();
    }

    public void ResetMovementAttemptStatePreservingCooldown(string presetId) => GetState(presetId).ResetAttemptState();

    private PresetState GetState(string presetId)
    {
        if (!_presets.TryGetValue(presetId, out PresetState state))
        {
            state = new PresetState();
            _presets.Add(presetId, state);
        }
        return state;
    }

    private static int NormalizeDelay(int delayMs) => Math.Clamp(delayMs, 0, MaximumCooldownMs);

    private static int GetRemainingMs(bool hasTimestamp, long lastTimestamp, int delayMs, long nowTimestamp)
    {
        if (!hasTimestamp || delayMs <= 0) return 0;
        double elapsed = GetElapsedMs(lastTimestamp, nowTimestamp);
        return Math.Clamp((int)Math.Ceiling(Math.Max(0.0, delayMs - elapsed)), 0, delayMs);
    }

    private static int GetElapsedMsRounded(long startTimestamp, long endTimestamp) =>
        (int)Math.Clamp(Math.Round(GetElapsedMs(startTimestamp, endTimestamp)), 0.0, int.MaxValue);

    private static double GetElapsedMs(long startTimestamp, long endTimestamp)
    {
        if (endTimestamp <= startTimestamp) return 0.0;
        return ((double)(endTimestamp - startTimestamp) * 1000.0) / Stopwatch.Frequency;
    }
}
