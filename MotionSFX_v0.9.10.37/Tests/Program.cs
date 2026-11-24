using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MotionSFX.Core;

int passed = 0;
List<string> failures = new();

void Test(string name, Action action)
{
    try
    {
        action();
        passed++;
        Console.WriteLine("PASS  " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name + ": " + ex.Message);
        Console.WriteLine("FAIL  " + name + " — " + ex.Message);
    }
}

static long AtMs(long milliseconds) => (long)Math.Ceiling(milliseconds * (double)Stopwatch.Frequency / 1000.0);
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static MovementTransform RotationY(double degrees)
{
    double half = degrees * Math.PI / 360.0;
    return new MovementTransform(0, 0, 0, 0, Math.Sin(half), 0, Math.Cos(half));
}

string sourceRoot = Path.Combine(AppContext.BaseDirectory, "Source");
string mainSource = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml.cs"));
string xaml = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml"));
string receiverSource = File.ReadAllText(Path.Combine(sourceRoot, "VmcReceiver.cs"));

Test("Individual cooldowns are independent", () =>
{
    MovementTriggerCoordinator c = new();
    c.RecordSuccessfulAutomaticShortcut("right-foot", AtMs(0));
    var right = c.ConsumeMovement("right-foot", 1000, 0, AtMs(100));
    var left = c.ConsumeMovement("left-foot", 300, 0, AtMs(100));
    Check(right.BlockReason == MovementTriggerBlockReason.IndividualCooldown, "Right foot should be cooling down.");
    Check(left.CanSend, "Left foot must remain independent.");
});

Test("Another preset becomes eligible after global delay while first preset cools down", () =>
{
    MovementTriggerCoordinator c = new();
    c.RecordSuccessfulAutomaticShortcut("right-foot", AtMs(0));
    var right = c.ConsumeMovement("right-foot", 1000, 500, AtMs(600));
    var left = c.ConsumeMovement("left-foot", 300, 500, AtMs(600));
    Check(right.BlockReason == MovementTriggerBlockReason.IndividualCooldown, "Right Foot should remain individually blocked.");
    Check(left.CanSend, "Left Foot should be allowed after the global delay expires.");
});

Test("A zero individual cooldown disables that preset timer", () =>
{
    MovementTriggerCoordinator c = new();
    c.RecordSuccessfulAutomaticShortcut("right-foot", AtMs(0));
    Check(c.ConsumeMovement("right-foot", 0, 0, AtMs(50)).CanSend, "Individual cooldown 0 must disable that timer.");
});

Test("Global activation delay blocks any preset", () =>
{
    MovementTriggerCoordinator c = new();
    c.RecordSuccessfulAutomaticShortcut("right-foot", AtMs(0));
    Check(c.ConsumeMovement("left-foot", 0, 500, AtMs(100)).BlockReason == MovementTriggerBlockReason.GlobalActivationDelay,
        "Expected global-delay block.");
});

Test("Disabling global delay permits independent presets", () =>
{
    MovementTriggerCoordinator c = new();
    c.RecordSuccessfulAutomaticShortcut("right-foot", AtMs(0));
    Check(c.ConsumeMovement("left-foot", 0, 0, AtMs(100)).CanSend, "Global delay 0 must not block another preset.");
});

Test("Individual and global delays can block simultaneously", () =>
{
    MovementTriggerCoordinator c = new();
    c.RecordSuccessfulAutomaticShortcut("right-foot", AtMs(0));
    var result = c.ConsumeMovement("right-foot", 1000, 500, AtMs(100));
    Check(result.BlockReason == MovementTriggerBlockReason.Both, "Expected both delay restrictions.");
    Check(result.IndividualCooldownRemainingMs > 0 && result.GlobalActivationDelayRemainingMs > 0,
        "Both remaining durations should be reported.");
});

Test("A failed or unrecorded send does not start cooldowns", () =>
{
    MovementTriggerCoordinator c = new();
    Check(c.ConsumeMovement("head", 1000, 500, AtMs(0)).CanSend, "First attempt should be permitted.");
    c.ResetMovementAttemptStatePreservingCooldown("head");
    Check(c.ConsumeMovement("head", 1000, 500, AtMs(100)).CanSend,
        "Without a successful-send record, neither cooldown should start.");
});

Test("Only successful automatic-send records start both clocks", () =>
{
    MovementTriggerCoordinator c = new();
    Check(c.ConsumeMovement("chest", 800, 600, AtMs(0)).CanSend, "Initial attempt should be permitted.");
    c.RecordSuccessfulAutomaticShortcut("chest", AtMs(20));
    var same = c.ConsumeMovement("chest", 800, 600, AtMs(100));
    var other = c.ConsumeMovement("hip", 0, 600, AtMs(100));
    Check(same.BlockReason == MovementTriggerBlockReason.Both, "Same preset should observe both timers.");
    Check(other.BlockReason == MovementTriggerBlockReason.GlobalActivationDelay, "Other preset should observe only the global timer.");
});

Test("Preset accepts a new movement attempt without neutral-position re-arming", () =>
{
    MovementTriggerCoordinator c = new();
    Check(c.ConsumeMovement("left-arm", 0, 0, AtMs(0)).CanSend, "First qualifying movement should be accepted.");
    Check(c.ConsumeMovement("left-arm", 0, 0, AtMs(10)).CanSend,
        "The timing coordinator must not latch a preset or demand a neutral-position check.");
});

Test("Cooldown expiration only permits a fresh detector attempt", () =>
{
    MovementTriggerCoordinator c = new();
    c.RecordSuccessfulAutomaticShortcut("head", AtMs(0));
    Check(c.ConsumeMovement("head", 500, 0, AtMs(100)).BlockReason == MovementTriggerBlockReason.IndividualCooldown,
        "The attempt while cooling down should be blocked.");
    Check(c.GetSnapshot("head", 500, 0, AtMs(600)).IndividualCooldownRemainingMs == 0,
        "Cooldown should expire without generating a shortcut itself.");
    Check(c.ConsumeMovement("head", 500, 0, AtMs(600)).CanSend,
        "A later detector attempt can be accepted after the timer expires.");
});

Test("Resetting movement baselines preserves individual and global cooldown timestamps", () =>
{
    MovementTriggerCoordinator c = new();
    c.RecordSuccessfulAutomaticShortcut("hip", AtMs(0));
    c.ResetMovementAttemptStatesPreservingCooldowns();
    Check(c.ConsumeMovement("hip", 1000, 0, AtMs(100)).BlockReason == MovementTriggerBlockReason.IndividualCooldown,
        "Resetting a baseline must preserve the preset cooldown.");
    Check(c.ConsumeMovement("chest", 0, 500, AtMs(100)).BlockReason == MovementTriggerBlockReason.GlobalActivationDelay,
        "Resetting a baseline must preserve the global delay.");
});

Test("Simultaneous trigger candidates are processed deterministically", () =>
{
    MovementTriggerCoordinator c = new();
    Check(c.ConsumeMovement("right-foot", 0, 500, AtMs(0)).CanSend, "First preset should be allowed.");
    c.RecordSuccessfulAutomaticShortcut("right-foot", AtMs(0));
    Check(c.ConsumeMovement("left-foot", 0, 500, AtMs(0)).BlockReason == MovementTriggerBlockReason.GlobalActivationDelay,
        "The next preset should observe the successful first trigger.");
});

Test("Manual Test keybind path does not update automatic timing", () =>
{
    int marker = mainSource.IndexOf("Manual movement keybind test sent", StringComparison.Ordinal);
    int start = mainSource.LastIndexOf("test.Click +=", marker, StringComparison.Ordinal);
    int end = mainSource.IndexOf("};", marker, StringComparison.Ordinal);
    Check(marker >= 0 && start >= 0 && end > start, "Could not locate the manual test-keybind handler.");
    string handler = mainSource.Substring(start, end - start);
    Check(!handler.Contains("_movementTriggerCoordinator", StringComparison.Ordinal) &&
          !handler.Contains("RecordSuccessfulAutomaticShortcut", StringComparison.Ordinal),
        "Manual Test keybinds must not affect automatic cooldowns.");
});

Test("Only a successful automatic send records cooldown timestamps", () =>
{
    int consume = mainSource.IndexOf("_movementTriggerCoordinator.ConsumeMovement(", StringComparison.Ordinal);
    int send = mainSource.IndexOf("KeyboardInput.Send(shortcut);", consume, StringComparison.Ordinal);
    int record = mainSource.IndexOf("_movementTriggerCoordinator.RecordSuccessfulAutomaticShortcut(preset.Id", send, StringComparison.Ordinal);
    Check(consume >= 0 && send > consume && record > send,
        "Successful-send timestamps must be recorded after KeyboardInput.Send returns.");
});

Test("Blocked attempts rebaseline immediately instead of waiting for neutral", () =>
{
    int block = mainSource.IndexOf("if (!attempt.CanSend)", StringComparison.Ordinal);
    int rebaseline = mainSource.IndexOf("SetMovementAnchor(state, sample, space);", block, StringComparison.Ordinal);
    int nextContinue = mainSource.IndexOf("continue;", block, StringComparison.Ordinal);
    Check(block >= 0 && rebaseline > block && rebaseline < nextContinue,
        "A blocked movement must be consumed by resetting its baseline before continuing.");
    Check(!mainSource.Contains("Waiting for neutral position", StringComparison.Ordinal) &&
          !mainSource.Contains("UpdateRearming", StringComparison.Ordinal) &&
          !mainSource.Contains("IsLatched", StringComparison.Ordinal) &&
          !mainSource.Contains("IsNeutralStabilizing", StringComparison.Ordinal),
        "Neutral-position hysteresis must be removed from runtime processing.");
});

Test("Successful and failed trigger attempts rebaseline the measurement anchor", () =>
{
    int send = mainSource.IndexOf("KeyboardInput.Send(shortcut);", StringComparison.Ordinal);
    int rebaseline = mainSource.IndexOf("SetMovementAnchor(state, sample, space);", send, StringComparison.Ordinal);
    Check(send >= 0 && rebaseline > send, "The anchor must be reset after automatic shortcut send handling.");
});

Test("All seven preset target/reference relationships are correct", () =>
{
    (string Target, string Reference)[] expected =
    {
        ("RightFoot", "Hip"), ("LeftFoot", "Hip"),
        ("RightHand", "Chest"), ("LeftHand", "Chest"),
        ("Head", "Chest"), ("Chest", "Hip"), ("Hip", "")
    };
    foreach (var pair in expected)
        Check(MovementMath.GetReferenceRole(pair.Target) == pair.Reference,
            $"{pair.Target} should use '{pair.Reference}' as its movement reference.");
});

Test("Selectable lower-leg and upper-leg targets use the hip reference", () =>
{
    foreach (string role in new[] { "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg" })
        Check(MovementMath.GetReferenceRole(role) == "Hip", $"{role} should use Hip as its reference.");
});

Test("Head position units are measured as Euclidean VMC translation deltas", () =>
{
    MovementTransform chest = new(0, 1.0, 0, 0, 0, 0, 1);
    MovementTransform headAtAnchor = new(0, 1.60, 0, 0, 0, 0, 1);
    MovementTransform headMoved = new(0.12, 1.60, 0, 0, 0, 0, 1);
    MovementTransform relativeAnchor = MovementMath.ToRelativeTransform(headAtAnchor, chest);
    MovementTransform relativeMoved = MovementMath.ToRelativeTransform(headMoved, chest);
    MovementMeasurement measured = MovementMath.MeasureFromAnchor(relativeAnchor, relativeMoved);
    Check(Math.Abs(measured.Position - 0.12) < 0.000001,
        $"A 0.12 VMC-unit head translation should measure 0.12, got {measured.Position:N6}.");
});

Test("Position delta is not lost when the reference tracker is rotated", () =>
{
    MovementTransform chest = new(0, 0, 0, 0, Math.Sin(Math.PI / 4), 0, Math.Cos(Math.PI / 4));
    MovementTransform head0 = new(0, 1.60, 0, 0, 0, 0, 1);
    MovementTransform head1 = new(0.15, 1.60, 0, 0, 0, 0, 1);
    MovementTransform rel0 = MovementMath.ToRelativeTransform(head0, chest);
    MovementTransform rel1 = MovementMath.ToRelativeTransform(head1, chest);
    MovementMeasurement measured = MovementMath.MeasureFromAnchor(rel0, rel1);
    Check(Math.Abs(measured.Position - 0.15) < 0.000001,
        "Rotating the reference frame must not change the Euclidean translation magnitude.");
});

Test("Head rotation is measured in degrees relative to its chest reference", () =>
{
    MovementTransform chest = new(0, 0, 0, 0, 0, 0, 1);
    MovementTransform head0 = new(0, 0, 0, 0, 0, 0, 1);
    MovementTransform headMoved = RotationY(35);
    MovementTransform rel0 = MovementMath.ToRelativeTransform(head0, chest);
    MovementTransform relMoved = MovementMath.ToRelativeTransform(headMoved, chest);
    MovementMeasurement measured = MovementMath.MeasureFromAnchor(rel0, relMoved);
    Check(Math.Abs(measured.RotationDegrees - 35.0) < 0.0001,
        $"A 35 degree relative head rotation should measure 35 degrees, got {measured.RotationDegrees:N3}.");
});

Test("Common chest and target rotation cancels in relative head measurement", () =>
{
    MovementTransform chest0 = new(0, 0, 0, 0, 0, 0, 1);
    MovementTransform head0 = new(0, 0, 0, 0, 0, 0, 1);
    MovementTransform chest1 = RotationY(42);
    MovementTransform head1 = RotationY(42);
    MovementTransform rel0 = MovementMath.ToRelativeTransform(head0, chest0);
    MovementTransform rel1 = MovementMath.ToRelativeTransform(head1, chest1);
    MovementMeasurement measured = MovementMath.MeasureFromAnchor(rel0, rel1);
    Check(measured.RotationDegrees < 0.0001,
        "A common body/head rotation should cancel when measuring head motion relative to chest.");
});


Test("Reference poses are interpolated to the target timestamp", () =>
{
    long t0 = AtMs(100);
    long t1 = AtMs(200);
    long targetTime = AtMs(150);
    MotionSfxVmcSample before = new("chest", "Chest", "TRA", "/VMC/Ext/Tra/Pos", false,
        0, 1.0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 1, t0);
    MotionSfxVmcSample after = new("chest", "Chest", "TRA", "/VMC/Ext/Tra/Pos", false,
        0.10, 1.0, 0, 0, Math.Sin(Math.PI / 8), 0, Math.Cos(Math.PI / 8), DateTime.UtcNow, "test", 2, t1);
    Check(MovementMath.TryAlignReference(new[] { before, after }, targetTime, out MovementTransform aligned, out double skew),
        "A target timestamp bracketed by two reference samples should be alignable.");
    Check(Math.Abs(aligned.X - 0.05) < 0.000001, "Reference position should be linearly interpolated at the target timestamp.");
    MovementMeasurement angle = MovementMath.MeasureFromAnchor(
        new MovementTransform(0, 0, 0, 0, 0, 0, 1),
        new MovementTransform(0, 0, 0, aligned.QX, aligned.QY, aligned.QZ, aligned.QW));
    Check(Math.Abs(angle.RotationDegrees - 22.5) < 0.001, "Reference orientation should use quaternion SLERP.");
    Check(Math.Abs(skew - 50.0) < 0.1, "The interpolated reference should report the bracket half-span.");
});

Test("Reference pairing preserves the original 75 ms tolerance", () =>
{
    Check(MovementMath.MaximumReferenceSampleSkewMs == 75.0,
        "Use the previous detector's 75 ms reference pairing tolerance.");
    MotionSfxVmcSample reference = new("chest", "Chest", "TRA", "/VMC/Ext/Tra/Pos", false,
        0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 1, AtMs(100));
    Check(MovementMath.TryAlignReference(new[] { reference }, AtMs(170), out _, out _),
        "A nearest reference 70 ms away should remain accepted.");
    Check(!MovementMath.TryAlignReference(new[] { reference }, AtMs(180), out _, out _),
        "A nearest reference more than 75 ms away should be rejected.");
});

Test("Every preset measures both position and rotation through the shared math", () =>
{
    (string Target, string Reference)[] presets =
    {
        ("RightFoot", "Hip"), ("LeftFoot", "Hip"),
        ("RightHand", "Chest"), ("LeftHand", "Chest"),
        ("Head", "Chest"), ("Chest", "Hip"), ("Hip", "")
    };
    foreach (var preset in presets)
    {
        MovementTransform target0 = new(0.20, 1.25, -0.10, 0, 0, 0, 1);
        MovementTransform target1 = new(0.32, 1.25, -0.10, 0, Math.Sin(Math.PI / 24), 0, Math.Cos(Math.PI / 24));
        MovementTransform first = target0;
        MovementTransform second = target1;
        if (!string.IsNullOrEmpty(preset.Reference))
        {
            MovementTransform reference0 = new(0.05, 0.90, 0.02, 0, 0, 0, 1);
            MovementTransform reference1 = new(0.05, 0.90, 0.02, 0, 0, 0, 1);
            first = MovementMath.ToRelativeTransform(target0, reference0);
            second = MovementMath.ToRelativeTransform(target1, reference1);
        }
        MovementMeasurement value = MovementMath.MeasureFromAnchor(first, second);
        Check(Math.Abs(value.Position - 0.12) < 0.000001, $"{preset.Target} position channel should measure 0.12 VMC units; got {value.Position:N6}.");
        Check(Math.Abs(value.RotationDegrees - 15.0) < 0.001, $"{preset.Target} rotation channel should measure 15 degrees; got {value.RotationDegrees:N3}.");
    }
});

Test("All incoming tracker categories and coordinate spaces are parsed consistently", () =>
{
    foreach (string address in new[]
    {
        "/VMC/Ext/Hmd/Pos", "/VMC/Ext/Con/Pos", "/VMC/Ext/Tra/Pos",
        "/VMC/Ext/Hmd/Pos/Local", "/VMC/Ext/Con/Pos/Local", "/VMC/Ext/Tra/Pos/Local"
    })
        Check(receiverSource.Contains(address, StringComparison.Ordinal), $"Missing VMC transform address: {address}.");
    Check(receiverSource.Contains("string id = (kind == \"BONE\" ? \"BONE|\" : \"VMC|\") + vmcLabel;", StringComparison.Ordinal),
        "Tracker identities must remain consistent across HMD/controller/tracker message categories.");
});

Test("The complete selectable body-role set is covered by the preset mapping", () =>
{
    foreach (string role in new[]
    {
        "LeftFoot", "RightFoot", "LeftLowerLeg", "RightLowerLeg", "LeftUpperLeg", "RightUpperLeg",
        "RightHand", "LeftHand", "Head", "Chest", "Hip"
    })
        Check(mainSource.Contains($"\"{role}\"", StringComparison.Ordinal), $"Missing selectable movement target role: {role}.");
});

Test("Target and reference samples are paired within the same coordinate space", () =>
{
    Check(mainSource.Contains("string historyKey = role + \"|\" + space;", StringComparison.Ordinal),
        "Reference history must be partitioned by role and GLOBAL/LOCAL coordinate space.");
    Check(mainSource.Contains("string space = sample.IsLocal ? \"LOCAL\" : \"GLOBAL\";", StringComparison.Ordinal),
        "Every target and reference sample must carry its original coordinate-space selection.");
});

Test("GLOBAL avatar-scale and LOCAL device-raw units are identified correctly", () =>
{
    Check(mainSource.Contains("snapshot.CoordinateSpace == \"LOCAL\" ? \"device-raw VMC units\" : \"avatar-scale VMC units\"", StringComparison.Ordinal),
        "Live position values must identify the selected VMC coordinate scale.");
    Check(xaml.Contains("/Pos/Local fallback", StringComparison.Ordinal) &&
          xaml.Contains("device-raw coordinates", StringComparison.Ordinal),
        "The preset help text must explain avatar-scale versus device-raw coordinates.");
    Check(mainSource.Contains("Position  — / {positionLimit:0.000} units", StringComparison.Ordinal),
        "A stale preview must not assume a coordinate space before tracker data is available.");
});

Test("A complete GLOBAL target/reference pair is preferred over LOCAL", () =>
{
    long t = AtMs(100);
    MotionSfxVmcSample gt = new("head", "Head", "HMD", "/VMC/Ext/Hmd/Pos", false, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 1, t);
    MotionSfxVmcSample gr = new("chest", "Chest", "TRA", "/VMC/Ext/Tra/Pos", false, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 1, t);
    MotionSfxVmcSample lt = new("head", "Head", "HMD", "/VMC/Ext/Hmd/Pos/Local", true, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 2, t);
    MotionSfxVmcSample lr = new("chest", "Chest", "TRA", "/VMC/Ext/Tra/Pos/Local", true, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 2, t);
    string selected = MovementMath.SelectCoordinateSpace(new[] { gt }, new[] { gr }, new[] { lt }, new[] { lr }, t, true, out _);
    Check(selected == "GLOBAL", "A complete avatar-scale pair should take priority when both spaces are available.");
});

Test("LOCAL is selected when it is the only complete target/reference pair", () =>
{
    long t = AtMs(100);
    MotionSfxVmcSample gt = new("head", "Head", "HMD", "/VMC/Ext/Hmd/Pos", false, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 1, t);
    MotionSfxVmcSample lt = new("head", "Head", "HMD", "/VMC/Ext/Hmd/Pos/Local", true, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 2, t);
    MotionSfxVmcSample lr = new("chest", "Chest", "TRA", "/VMC/Ext/Tra/Pos/Local", true, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 2, t);
    string selected = MovementMath.SelectCoordinateSpace(new[] { gt }, null, new[] { lt }, new[] { lr }, t, true, out _);
    Check(selected == "LOCAL", "A mixed or incomplete GLOBAL pair must fall back to a complete device-raw pair.");
});

Test("Mixed GLOBAL target and LOCAL reference streams are never combined", () =>
{
    long t = AtMs(100);
    MotionSfxVmcSample gt = new("head", "Head", "HMD", "/VMC/Ext/Hmd/Pos", false, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 1, t);
    MotionSfxVmcSample lr = new("chest", "Chest", "TRA", "/VMC/Ext/Tra/Pos/Local", true, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 2, t);
    string selected = MovementMath.SelectCoordinateSpace(new[] { gt }, null, null, new[] { lr }, t, true, out _);
    Check(string.IsNullOrEmpty(selected), "The detector must wait rather than calculate a relative transform from incompatible coordinate spaces.");
});

Test("Hip can use GLOBAL or LOCAL position data without a reference tracker", () =>
{
    long t = AtMs(100);
    MotionSfxVmcSample gt = new("hip", "Hip", "TRA", "/VMC/Ext/Tra/Pos", false, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 1, t);
    MotionSfxVmcSample lt = new("hip", "Hip", "TRA", "/VMC/Ext/Tra/Pos/Local", true, 0, 0, 0, 0, 0, 0, 1, DateTime.UtcNow, "test", 2, t);
    Check(MovementMath.SelectCoordinateSpace(new[] { gt }, null, new[] { lt }, null, t, false, out _) == "GLOBAL",
        "Hip should prefer avatar-scale coordinates when no reference is required.");
    Check(MovementMath.SelectCoordinateSpace(null, null, new[] { lt }, null, t, false, out _) == "LOCAL",
        "Hip should fall back to device-raw coordinates when GLOBAL is unavailable.");
});

Test("VMC receiver retains both coordinate streams for consistent pair selection", () =>
{
    Check(!receiverSource.Contains("GlobalFreshTicks", StringComparison.Ordinal) &&
          !receiverSource.Contains("_lastGlobalTicks", StringComparison.Ordinal),
        "The receiver must not discard LOCAL samples merely because the same device also emits GLOBAL samples.");
    Check(receiverSource.Contains("sample.Id + \"|\" + coordinateSpace", StringComparison.Ordinal),
        "GLOBAL and LOCAL stream throttling must remain independent per device.");
});

Test("VMC device categories expose the correct preset tracker roles", () =>
{
    Check(MovementMath.GetAllowedRolesForKind("HMD").SequenceEqual(new[] { "Head" }),
        "HMD devices should map to the Head role.");
    Check(MovementMath.GetAllowedRolesForKind("CON").SequenceEqual(new[] { "LeftHand", "RightHand" }),
        "Controller devices should map to left and right hand roles.");
    string[] trackerRoles = MovementMath.GetAllowedRolesForKind("TRA");
    foreach (string role in new[] { "LeftFoot", "RightFoot", "Hip", "Chest", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg" })
        Check(trackerRoles.Contains(role, StringComparer.Ordinal), $"Generic tracker devices must allow the {role} role.");
});


Test("Reference sample synchronization uses monotonic timestamps", () =>
{
    Check(Math.Abs(MovementMath.GetTimestampSkewMs(AtMs(100), AtMs(130)) - 30.0) < 0.1,
        "A 30 ms sample difference should be measured using Stopwatch ticks.");
    Check(MovementMath.MaximumReferenceSampleSkewMs == 75.0,
        "Reference pairing should preserve the original 75 ms window.");
});

Test("Every preset uses the shared movement position and quaternion calculation", () =>
{
    Check(mainSource.Contains("MovementMath.MeasureFromAnchor(", StringComparison.Ordinal),
        "The movement pipeline must use the shared measurement helper.");
    Check(mainSource.Contains("MovementMath.ToRelativeTransform(", StringComparison.Ordinal),
        "The target/reference transform must use the shared relative-transform helper.");
    Check(!mainSource.Contains("GetMovementReferenceRole(", StringComparison.Ordinal),
        "Preset reference mappings should not be duplicated in MainWindow.");
});

Test("Position axes and custom controls identify VMC units", () =>
{
    Check(xaml.Contains("Position threshold (units)", StringComparison.Ordinal),
        "The custom movement position controls should identify the VMC coordinate scale.");
    Check(xaml.Contains("selected VMC coordinate space: avatar-scale for /Pos", StringComparison.Ordinal),
        "The threshold tooltip should explain the position scale.");
});

Test("WPF compiles the application MainWindow XAML exactly once", () =>
{
    string project = File.ReadAllText(Path.Combine(sourceRoot, "MotionSFX.csproj"));
    Check(project.Contains("<EnableDefaultPageItems>false</EnableDefaultPageItems>", StringComparison.Ordinal),
        "Default recursive WPF Page inclusion must be disabled.");
    string pageInclude = "<Page Include=\"UI\\MainWindow.xaml\" />";
    Check(project.Split(pageInclude, StringSplitOptions.None).Length - 1 == 1,
        "The application must include MainWindow.xaml exactly once.");
});

Test("All seven preset pages expose an individual cooldown control", () =>
{
    string[] names =
    {
        "PresetRightFootCooldown", "PresetLeftFootCooldown", "PresetRightArmCooldown",
        "PresetLeftArmCooldown", "PresetHeadCooldown", "PresetChestCooldown", "PresetHipCooldown"
    };
    foreach (string name in names)
        Check(xaml.Contains($"x:Name=\"{name}\"", StringComparison.Ordinal), $"Missing cooldown control: {name}.");
});

Test("Disabled presets remain on the preview-only path", () =>
{
    int previewOnly = mainSource.IndexOf("bool previewOnly = !preset.Enabled || _movementSendFailures.Contains(preset.Id);", StringComparison.Ordinal);
    int consume = mainSource.IndexOf("_movementTriggerCoordinator.ConsumeMovement(", StringComparison.Ordinal);
    int disabledBranch = mainSource.IndexOf("if (previewOnly && !preset.Enabled)", StringComparison.Ordinal);
    Check(previewOnly >= 0 && disabledBranch > previewOnly && consume > disabledBranch,
        "Disabled presets must not consume automatic trigger timing.");
});

Test("New defaults use zero global delay and zero per-preset cooldown", () =>
{
    AppSettings freshSettings = new();
    MovementPreset freshPreset = new();
    Check(freshSettings.KeybindDelayMs == 0, "New global delay should default to 0.");
    Check(freshPreset.CooldownMs == 0, "New individual cooldown should default to 0.");
    AppSettings shippedSettings = JsonSerializer.Deserialize<AppSettings>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Config", "settings.json")));
    Check(shippedSettings != null && shippedSettings.KeybindDelayMs == 0, "Shipped settings should default to 0.");
    List<MovementPreset> shippedPresets = JsonSerializer.Deserialize<List<MovementPreset>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Config", "presets.json")));
    Check(shippedPresets?.Count == 7 && shippedPresets.All(p => p.CooldownMs == 0),
        "All seven shipped presets should default to cooldownMs 0.");
});

Test("Older preset JSON without cooldownMs migrates to zero", () =>
{
    MovementPreset oldPreset = JsonSerializer.Deserialize<MovementPreset>(
        "{\"id\":\"right-foot\",\"name\":\"Right Foot Step\",\"hotkey\":\"CTRL+ALT+1\",\"enabled\":true,\"sensitivity\":\"Normal\",\"targetRole\":\"RightFoot\"}");
    Check(oldPreset != null && oldPreset.CooldownMs == 0, "Missing cooldownMs must default to zero.");
});

Test("Existing saved global delay values remain intact", () =>
{
    AppSettings existing = JsonSerializer.Deserialize<AppSettings>(
        "{\"bindAddress\":\"127.0.0.1\",\"udpPort\":39539,\"staleAfterMs\":1500,\"lostAfterMs\":5000,\"keybindDelayMs\":875,\"configVersion\":1}");
    Check(existing != null && existing.KeybindDelayMs == 875, "Existing global delay must be preserved.");
});

Console.WriteLine();
Console.WriteLine($"Movement trigger and movement-math tests: {passed} passed, {failures.Count} failed.");
if (failures.Count > 0)
{
    foreach (string failure in failures) Console.Error.WriteLine(failure);
    return 1;
}
return 0;
