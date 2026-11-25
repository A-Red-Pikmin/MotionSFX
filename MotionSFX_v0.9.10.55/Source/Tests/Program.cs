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
        ConsoleColor previousColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("PASS");
        Console.ForegroundColor = previousColor;
        Console.WriteLine("  " + name);
    }
    catch (Exception ex)
    {
        failures.Add(name + ": " + ex.Message);
        ConsoleColor previousColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("FAIL");
        Console.ForegroundColor = previousColor;
        Console.WriteLine("  " + name + " — " + ex.Message);
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
string appSource = File.ReadAllText(Path.Combine(sourceRoot, "App.xaml.cs"));
string hipDetectorSource = File.ReadAllText(Path.Combine(sourceRoot, "PhysicalHipTrackerDetector.cs"));
string trackerFeedSource = File.ReadAllText(Path.Combine(sourceRoot, "SlimeVrTrackerFeedClient.cs"));
string xaml = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml"));
string receiverSource = File.ReadAllText(Path.Combine(sourceRoot, "VmcReceiver.cs"));
string dashboardSource = File.ReadAllText(Path.Combine(sourceRoot, "SteamVrDashboard.cs"));
string projectSource = File.ReadAllText(Path.Combine(sourceRoot, "MotionSFX.csproj"));
string buildScript = File.ReadAllText(Path.Combine(sourceRoot, "BUILD_NATIVE.cmd"));
bool programSourceContainsColoredResults = File.ReadAllText(Path.Combine(sourceRoot, "Tests", "Program.cs")).Contains("Console.ForegroundColor = ConsoleColor.Green", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(sourceRoot, "Tests", "Program.cs")).Contains("Console.ForegroundColor = ConsoleColor.Red", StringComparison.Ordinal);
string testProjectSource = File.ReadAllText(Path.Combine(sourceRoot, "Tests", "MovementTriggerStateMachine.Tests.csproj"));
string shortcutProjectSource = File.ReadAllText(Path.Combine(sourceRoot, "BuildTools", "ShortcutCreator", "ShortcutCreator.csproj"));
string runScript = File.ReadAllText(Path.Combine(sourceRoot, "RUN_NATIVE.cmd"));

Test("SteamVR dashboard exposes the Performance page and all four performance charts", () =>
{
    Check(dashboardSource.Contains("case 4: DrawPerformancePage(dc, snapshot);", StringComparison.Ordinal),
        "The renderer must route page 4 to the Performance page.");
    Check(dashboardSource.Contains("DrawNavButton(dc, \"Performance\", 784, selectedPage == 4)", StringComparison.Ordinal),
        "The fifth navigation button must be visible and selectable.");
    Check(dashboardSource.Contains("DrawPerformanceMetricCard", StringComparison.Ordinal) &&
          dashboardSource.Contains("PerformanceCpuHistory", StringComparison.Ordinal) &&
          dashboardSource.Contains("PerformanceRamHistory", StringComparison.Ordinal) &&
          dashboardSource.Contains("PerformanceGpuHistory", StringComparison.Ordinal) &&
          dashboardSource.Contains("VmcDatagramRateHistory", StringComparison.Ordinal),
        "The page must display CPU, RAM, GPU, and VMC datagram history.");
    Check(mainSource.Contains("_latestPerformanceSnapshot = snapshot", StringComparison.Ordinal) &&
          mainSource.Contains("PerformanceCpuPercent = _latestPerformanceSnapshot?.CpuPercent", StringComparison.Ordinal) &&
          mainSource.Contains("PerformanceGpuHistory = _performanceHistory", StringComparison.Ordinal),
        "The dashboard must receive the existing desktop performance sampler and rolling histories.");
});

Test("Version 0.9.10.55 is consistent across application and build metadata", () =>
{
    Check(appSource.Contains("VersionText = \"0.9.10.55\"", StringComparison.Ordinal),
        "The runtime version constant must be 0.9.10.55.");
    Check(projectSource.Contains("<Version>0.9.10.55</Version>", StringComparison.Ordinal) &&
          projectSource.Contains("<FileVersion>0.9.10.55</FileVersion>", StringComparison.Ordinal) &&
          projectSource.Contains("<AssemblyVersion>0.9.10.55</AssemblyVersion>", StringComparison.Ordinal),
        "The assembly/package metadata must match the runtime version.");
    Check(xaml.Contains("Title=\"MotionSFX Alpha 0.9.10.55\"", StringComparison.Ordinal) &&
          dashboardSource.Contains("ALPHA 0.9.10.55", StringComparison.Ordinal) &&
          buildScript.Contains("MotionSFX Alpha 0.9.10.55 - Native Build", StringComparison.Ordinal),
        "Both visible interfaces and the native build banner must show the current version.");
});

Test("Debug Mode toggles both desktop debug tabs from the MotionSFX logo", () =>
{
    Check(xaml.Contains("x:Name=\"AppLogoClickTarget\"", StringComparison.Ordinal) &&
          xaml.Contains("x:Name=\"DebugModeLabel\" Text=\"DEBUG MODE\"", StringComparison.Ordinal),
        "The top-left logo must be a click target and the DEBUG MODE label must exist beside the title.");
    Check(xaml.Contains("x:Name=\"DebugModeLabel\" Text=\"DEBUG MODE\" FontSize=\"12\" FontWeight=\"ExtraBold\" Foreground=\"#A78BFA\" VerticalAlignment=\"Center\" Margin=\"12,3,0,0\" Visibility=\"Collapsed\"", StringComparison.Ordinal),
        "The purple DEBUG MODE label must be hidden when Debug Mode is off.");
    Check(xaml.Contains("x:Name=\"PerformanceTab\" Header=\"Performance\" Visibility=\"Collapsed\"", StringComparison.Ordinal) &&
          xaml.Contains("x:Name=\"DiagnosticsTab\" Header=\"Diagnostics\" Visibility=\"Collapsed\"", StringComparison.Ordinal),
        "Both desktop Performance and Diagnostics tabs must be hidden when the application starts.");
    Check(mainSource.Contains("AppLogoClickTarget.PreviewMouseLeftButtonUp", StringComparison.Ordinal) &&
          mainSource.Contains("ToggleDebugMode();", StringComparison.Ordinal) &&
          mainSource.Contains("_debugModeEnabled = !_debugModeEnabled;", StringComparison.Ordinal),
        "Each logo click must invert the Debug Mode state.");
    Check(mainSource.Contains("PerformanceTab.Visibility = debugVisibility", StringComparison.Ordinal) &&
          mainSource.Contains("DiagnosticsTab.Visibility = debugVisibility", StringComparison.Ordinal) &&
          mainSource.Contains("DebugModeLabel.Visibility = debugVisibility", StringComparison.Ordinal),
        "Both debug tabs and the purple label must follow the same Debug Mode state.");
    Check(mainSource.Contains("object.ReferenceEquals(MainTabs.SelectedItem, PerformanceTab)", StringComparison.Ordinal) &&
          mainSource.Contains("object.ReferenceEquals(MainTabs.SelectedItem, DiagnosticsTab)", StringComparison.Ordinal) &&
          mainSource.Contains("MainTabs.SelectedIndex = 0", StringComparison.Ordinal),
        "Disabling Debug Mode from a debug tab must return the user to Overview.");
    Check(mainSource.Contains("DEBUG MODE ACTIVATED", StringComparison.Ordinal) &&
          mainSource.Contains("DEBUG MODE DEACTIVATED", StringComparison.Ordinal),
        "Both Debug Mode state transitions must be recorded in the session log.");
});

Test("SteamVR Live Graphs use the requested concise tracker synchronization status", () =>
{
    Check(dashboardSource.Contains("DrawText(dc, CompactLiveGraphStatus(graph.Status)", StringComparison.Ordinal),
        "The SteamVR Live Graphs card must render the compact status rather than the full desktop message.");
    Check(dashboardSource.Contains("return \"Waiting: Tracker Synchronization.\";", StringComparison.Ordinal),
        "The SteamVR dashboard must use the exact requested tracker synchronization label.");
    Check(dashboardSource.Contains("Waiting for synchronized target and Hip reference data", StringComparison.Ordinal),
        "The compact label must be applied to the verbose synchronization wait state.");
});

Test("Desktop and SteamVR performance graphs overlay system totals with MotionSFX process usage", () =>
{
    Check(xaml.Contains("CpuSystemPerformanceValue", StringComparison.Ordinal) &&
          xaml.Contains("RamSystemPerformanceValue", StringComparison.Ordinal) &&
          xaml.Contains("GpuSystemPerformanceValue", StringComparison.Ordinal),
        "The desktop Performance tab must show system-total percentage labels for CPU, RAM, and GPU.");
    Check(mainSource.Contains("snapshot.SystemCpuPercent", StringComparison.Ordinal) &&
          mainSource.Contains("snapshot.SystemRamPercent", StringComparison.Ordinal) &&
          mainSource.Contains("snapshot.SystemGpuPercent", StringComparison.Ordinal) &&
          mainSource.Contains("DrawSeries(systemValues, \"#FBBF24\", dashed: true)", StringComparison.Ordinal),
        "The desktop charts must render a distinct dashed line from whole-system samples.");
    Check(dashboardSource.Contains("PerformanceSystemCpuHistory", StringComparison.Ordinal) &&
          dashboardSource.Contains("PerformanceSystemRamHistory", StringComparison.Ordinal) &&
          dashboardSource.Contains("PerformanceSystemGpuHistory", StringComparison.Ordinal) &&
          dashboardSource.Contains("SYSTEM TOTAL  ", StringComparison.Ordinal) &&
          dashboardSource.Contains("DashStyles.Dash", StringComparison.Ordinal),
        "The SteamVR Performance charts must show the same system-total series and percentages.");
    Check(mainSource.Contains("PerformanceSystemCpuPercent = _latestPerformanceSnapshot?.SystemCpuPercent", StringComparison.Ordinal) &&
          mainSource.Contains("PerformanceSystemRamPercent = _latestPerformanceSnapshot?.SystemRamPercent", StringComparison.Ordinal) &&
          mainSource.Contains("PerformanceSystemGpuPercent = _latestPerformanceSnapshot?.SystemGpuPercent", StringComparison.Ordinal),
        "The dashboard snapshot must receive all system-total values from the shared sampler.");
});

Test("Build preflight attempts to install the .NET 10 SDK with winget when missing", () =>
{
    int sdkCheck = buildScript.IndexOf("Checking installed .NET SDK before any build or test work", StringComparison.Ordinal);
    int wingetInstall = buildScript.IndexOf("winget install Microsoft.DotNet.SDK.10", StringComparison.Ordinal);
    int tests = buildScript.IndexOf("Building and running movement-trigger/configuration tests", StringComparison.Ordinal);
    int publish = buildScript.IndexOf("publish \"%SOURCE_DIR%\\MotionSFX.csproj\"", StringComparison.Ordinal);
    Check(sdkCheck >= 0 && wingetInstall > sdkCheck && tests > wingetInstall && publish > tests,
        "The .NET 10 SDK check and winget fallback must run before tests and application publishing.");
    Check(buildScript.Contains("findstr /b \"10.\"", StringComparison.Ordinal) &&
          buildScript.Contains("--accept-package-agreements", StringComparison.Ordinal),
        "The preflight must verify the .NET 10 SDK and accept winget package agreements.");
});


Test("Build log is color-coded, concise, and keeps useful detail", () =>
{
    Check(!buildScript.Contains("Build output mode: concise detailed.", StringComparison.Ordinal) &&
          !buildScript.Contains("Phase summaries, test results, warnings/errors", StringComparison.Ordinal) &&
          !buildScript.Contains("Repetitive restore diagnostics and low-level compiler command lines are suppressed.", StringComparison.Ordinal),
        "The build banner must not contain explanatory or self-descriptive preamble text.");
    Check(buildScript.Contains("set \"C_PHASE=%ESC%[96m\"", StringComparison.Ordinal) &&
          buildScript.Contains("set \"C_OK=%ESC%[92m\"", StringComparison.Ordinal) &&
          buildScript.Contains("set \"C_WARN=%ESC%[93m\"", StringComparison.Ordinal) &&
          buildScript.Contains("set \"C_ERROR=%ESC%[91m\"", StringComparison.Ordinal),
        "Build phases, success, warnings, and errors must each have distinct console colors.");
    Check(buildScript.Contains("MOTIONSFX_ANSI_CYAN", StringComparison.Ordinal) &&
          buildScript.Contains("MOTIONSFX_ANSI_RESET", StringComparison.Ordinal) &&
          programSourceContainsColoredResults,
        "Per-file compilation output and test PASS/FAIL results must also be color-coded.");
    Check(buildScript.Contains("--verbosity minimal", StringComparison.Ordinal) &&
          !buildScript.Contains("--verbosity normal", StringComparison.Ordinal),
        "All build commands must use minimal MSBuild verbosity instead of normal verbose output.");
    string[] buildCommands = buildScript.Split('\n')
        .Where(line => line.Contains("run --project", StringComparison.Ordinal) ||
                       line.Contains("publish \"%SOURCE_DIR%\\MotionSFX.csproj\"", StringComparison.Ordinal))
        .ToArray();
    Check(buildCommands.Length == 3 &&
          buildCommands.All(line => line.Contains("--verbosity minimal", StringComparison.Ordinal)),
        "The test, application publish, and shortcut commands must all use Minimal verbosity.");
    string compiledMessage = "Importance=\"high\" Text=\"$(MOTIONSFX_ANSI_CYAN)    [COMPILED] %(Compile.Identity)$(MOTIONSFX_ANSI_RESET)\"";
    Check(projectSource.Contains(compiledMessage, StringComparison.Ordinal) &&
          testProjectSource.Contains(compiledMessage, StringComparison.Ordinal) &&
          shortcutProjectSource.Contains(compiledMessage, StringComparison.Ordinal),
        "Each project must retain high-importance, colorized per-file [COMPILED] reports at minimal verbosity.");
    int timedPhaseCount = buildScript.Split("call :StartTask ", StringSplitOptions.None).Length - 1;
    Check(timedPhaseCount == 7 &&
          buildScript.Contains("set \"TASK_START_TIME=%TIME: =0%\"", StringComparison.Ordinal) &&
          buildScript.Contains("set \"TASK_END_TIME=%TIME: =0%\"", StringComparison.Ordinal) &&
          buildScript.Contains("[TIME] %TASK_LABEL%", StringComparison.Ordinal) &&
          buildScript.Contains("TASK_ELAPSED_CS=TASK_END_CS-TASK_START_CS", StringComparison.Ordinal) &&
          buildScript.Contains("TASK_ELAPSED_CS+=8640000", StringComparison.Ordinal),
        "All seven build phases must report elapsed time, including midnight-crossing builds.");
});
Test("Build keeps MotionSFX.exe in the application root and organizes other files into corresponding folders", () =>
{
    Check(buildScript.Contains("for %%I in (\"%~dp0..\") do set \"APP_DIR=%%~fI\"", StringComparison.Ordinal) &&
          buildScript.Contains("set \"APP_EXE=%APP_DIR%\\MotionSFX.exe\"", StringComparison.Ordinal) &&
          buildScript.Contains("set \"SOURCE_DIR=%APP_DIR%\\Source\"", StringComparison.Ordinal) &&
          buildScript.Contains("set \"BUILD_TOOLS_DIR=%APP_DIR%\\BuildTools\"", StringComparison.Ordinal),
        "The build script in BuildTools must resolve the parent application root and the organized source/tool folders.");
    Check(buildScript.Contains("--output \"%PUBLISH_STAGING%\"", StringComparison.Ordinal) &&
          buildScript.Contains("robocopy \"%PUBLISH_STAGING%\" \"%APP_DIR%\"", StringComparison.Ordinal) &&
          !buildScript.Contains("robocopy \"%PUBLISH_STAGING%\" \"%~dp0\"", StringComparison.Ordinal),
        "Publishing must copy staged runtime files to the normalized application root.");
    foreach (string folder in new[] { "\"%SOURCE_DIR%\\obj\"", "\"%SOURCE_DIR%\\bin\"", "\"%SOURCE_DIR%\\Tests\\obj\"", "\"%SOURCE_DIR%\\Tests\\bin\"", "\"%BUILD_TOOLS_DIR%\\ShortcutCreator\\obj\"", "\"%BUILD_TOOLS_DIR%\\ShortcutCreator\\bin\"", "\"%PUBLISH_STAGING%\"", "\"%APP_DIR%\\publish\"" })
        Check(buildScript.Contains("rmdir /s /q " + folder, StringComparison.Ordinal),
            $"Build cleanup must remove generated intermediate output: {folder}.");
    Check(buildScript.Contains(":OrganizeLooseFiles", StringComparison.Ordinal) &&
          buildScript.Contains("%APP_DIR%\\Documentation\\", StringComparison.Ordinal) &&
          buildScript.Contains("%APP_DIR%\\Source\\", StringComparison.Ordinal) &&
          buildScript.Contains("%APP_DIR%\\BuildTools\\", StringComparison.Ordinal) &&
          buildScript.Contains("if /I not \"%%~nxF\"==\"MotionSFX.exe\"", StringComparison.Ordinal),
        "The organization pass must sort loose docs, source, scripts, and utility EXEs while explicitly protecting MotionSFX.exe.");
    Check(buildScript.Contains("Main folder contains MotionSFX.exe plus categorized folders: Assets, Config, Source, BuildTools, and Documentation.", StringComparison.Ordinal),
        "The build should report the final organized folder layout.");
    int copyPublishedFiles = buildScript.IndexOf("robocopy \"%PUBLISH_STAGING%\" \"%APP_DIR%\"", StringComparison.Ordinal);
    int cleanupAfterBuild = buildScript.IndexOf("[7/7] Removing temporary build files and verifying the organized application folders", StringComparison.Ordinal);
    int organizeAfterBuild = buildScript.IndexOf("call :OrganizeLooseFiles", cleanupAfterBuild, StringComparison.Ordinal);
    int launchAfterCleanup = buildScript.IndexOf("Starting MotionSFX automatically", StringComparison.Ordinal);
    Check(copyPublishedFiles >= 0 && cleanupAfterBuild > copyPublishedFiles && organizeAfterBuild > cleanupAfterBuild && launchAfterCleanup > organizeAfterBuild,
        "The script must copy the published runtime, clean intermediates, organize loose files, and only then launch MotionSFX.");
    Check(runScript.Contains("for %%I in (\"%~dp0..\") do set \"APP_DIR=%%~fI\"", StringComparison.Ordinal) &&
          runScript.Contains("set \"APP_EXE=%APP_DIR%\\MotionSFX.exe\"", StringComparison.Ordinal) &&
          !runScript.Contains("publish\\MotionSFX.exe", StringComparison.Ordinal),
        "RUN_NATIVE.cmd in BuildTools must launch the executable from the parent application directory.");
    Check(projectSource.Contains("<ApplicationIcon>..\\Assets\\MotionSFX-icon.ico</ApplicationIcon>", StringComparison.Ordinal) &&
          projectSource.Contains("..\\Config\\settings.json", StringComparison.Ordinal) &&
          projectSource.Contains("..\\Documentation\\THIRD_PARTY_NOTICES.txt", StringComparison.Ordinal),
        "The project must link runtime assets/config and the third-party notice from their organized folders.");
    Check(testProjectSource.Contains("..\\..\\Config\\settings.json", StringComparison.Ordinal) &&
          testProjectSource.Contains("..\\..\\BuildTools\\BUILD_NATIVE.cmd", StringComparison.Ordinal) &&
          shortcutProjectSource.Length > 0,
        "The test project must reference configuration and build tools from their reorganized locations.");
});

Test("System CPU calculation subtracts idle time from kernel plus user time", () =>
{
    double usage = PerformanceMonitor.CalculateSystemCpuPercent(700, 300, 400);
    Check(Math.Abs(usage - 60.0) < 0.000001, $"Expected 60% total CPU usage, got {usage:N3}%.");
});

Test("System CPU calculation reports unavailable for a zero-length sample", () =>
{
    Check(PerformanceMonitor.CalculateSystemCpuPercent(0, 0, 0) < 0,
        "A zero-length sample must not be reported as 0% CPU usage.");
});

Test("System CPU calculation clamps inconsistent idle deltas to zero busy usage", () =>
{
    Check(PerformanceMonitor.CalculateSystemCpuPercent(10, 10, 30) == 0,
        "Idle time exceeding the measured total should clamp busy usage to 0%.");
});


Test("A connected physical SlimeVR Waist tracker enables Hip movement", () =>
{
    Check(PhysicalHipTrackerDetector.IsConnectedPhysicalHipTracker(false,
        PhysicalHipTrackerDetector.WaistBodyPart, PhysicalHipTrackerDetector.TrackerStatusOk),
        "A real connected Waist tracker should qualify for the Hip preset.");
});

Test("A connected physical SlimeVR Hip tracker enables Hip movement", () =>
{
    Check(PhysicalHipTrackerDetector.IsConnectedPhysicalHipTracker(false,
        PhysicalHipTrackerDetector.HipBodyPart, PhysicalHipTrackerDetector.TrackerStatusOk),
        "A real connected Hip tracker should qualify for the Hip preset.");
});

Test("Computed SlimeVR Waist transforms cannot masquerade as physical Hip hardware", () =>
{
    Check(!PhysicalHipTrackerDetector.IsConnectedPhysicalHipTracker(true,
        PhysicalHipTrackerDetector.WaistBodyPart, PhysicalHipTrackerDetector.TrackerStatusOk),
        "A computed Waist tracker must not qualify as physical hardware.");
});

Test("An omitted FlatBuffers is_computed field defaults to a physical tracker", () =>
{
    Check(trackerFeedSource.Contains("bool isComputed = computedField != 0 && data[computedField] != 0;", StringComparison.Ordinal),
        "The SolarXR parser must interpret an omitted is_computed field as false, as required by FlatBuffers default elision.");
    Check(trackerFeedSource.Contains("if (bodyPartField == 0) continue;", StringComparison.Ordinal),
        "Missing body-part identity must still be rejected.");
});

Test("Disconnected or non-Hip SlimeVR trackers cannot enable Hip movement", () =>
{
    Check(!PhysicalHipTrackerDetector.IsConnectedPhysicalHipTracker(false,
        PhysicalHipTrackerDetector.WaistBodyPart, 1),
        "A disconnected Waist tracker must not qualify.");
    Check(!PhysicalHipTrackerDetector.IsConnectedPhysicalHipTracker(false,
        3, PhysicalHipTrackerDetector.TrackerStatusOk),
        "A connected Chest tracker must not qualify as a Hip tracker.");
});

Test("HMD and controller-only hardware cannot enable Hip movement", () =>
{
    byte[] hmdAndControllerBodyParts = { 1, 18, 19 };
    foreach (byte bodyPart in hmdAndControllerBodyParts)
    {
        Check(!PhysicalHipTrackerDetector.IsConnectedPhysicalHipTracker(false,
            bodyPart, PhysicalHipTrackerDetector.TrackerStatusOk),
            $"Body part {bodyPart} (HMD/controller) must not qualify as physical Hip hardware.");
    }
});

Test("Hip movement is gated by fresh SlimeVR physical tracker status, not VMC alone", () =>
{
    int gate = mainSource.IndexOf("HasConfirmedPhysicalHipTracker()", StringComparison.Ordinal);
    int skip = mainSource.IndexOf("if (string.Equals(role, \"Hip\", StringComparison.Ordinal) && !physicalHipAvailable) continue;", StringComparison.Ordinal);
    Check(gate >= 0 && skip > gate,
        "The movement sample pipeline must suppress Hip role transforms unless physical hardware is confirmed.");
    Check(mainSource.Contains("No connected physical Hip/Waist tracker", StringComparison.Ordinal),
        "The application should explain why computed Hip VMC transforms are being ignored.");
    Check(hipDetectorSource.Contains("!isComputed", StringComparison.Ordinal),
        "Physical tracker classification must exclude computed SlimeVR trackers.");
});

Test("SlimeVR data feed requests physical device tracker info and status", () =>
{
    Check(trackerFeedSource.Contains("ws://127.0.0.1:21110/", StringComparison.Ordinal),
        "The tracker-status client should use SlimeVR's local WebSocket endpoint.");
    Check(trackerFeedSource.Contains("builder.AddBool(0, true, false); // info", StringComparison.Ordinal) &&
          trackerFeedSource.Contains("builder.AddBool(1, true, false); // status", StringComparison.Ordinal) &&
          trackerFeedSource.Contains("builder.AddBool(2, true, false); // rotation", StringComparison.Ordinal) &&
          trackerFeedSource.Contains("builder.AddBool(1, true, false); // device_data", StringComparison.Ordinal),
        "The SolarXR subscription must request physical device tracker metadata, status, and a live field to keep tracker presence fresh.");
    Check(trackerFeedSource.Contains("(utcNow - LastDeviceUpdateUtc).TotalSeconds <= 3.0", StringComparison.Ordinal),
        "Physical tracker confirmation must expire when the status feed becomes stale.");
    Check(trackerFeedSource.Contains("messageTypeField", StringComparison.Ordinal) &&
          trackerFeedSource.Contains("GetFieldPosition(data, tracker, 2)", StringComparison.Ordinal),
        "The SolarXR DataFeedUpdate parser must inspect actual device tracker status records.");
});

Test("Each application launch starts a timestamped session log with version as its first line", () =>
{
    int startSession = appSource.IndexOf("AppLog.StartSession(VersionText)", StringComparison.Ordinal);
    int registerHandlers = appSource.IndexOf("DispatcherUnhandledException +=", StringComparison.Ordinal);
    Check(startSession >= 0 && registerHandlers > startSession,
        "Session logging must start before other startup events can write log messages.");
    Check(appSource.Contains("MotionSFX-SessionLog_{timestamp}.log", StringComparison.Ordinal),
        "Session logs should use the MotionSFX-SessionLog timestamped filename.");
    Check(appSource.Contains("LAUNCHED VERSION: MotionSFX Alpha {version}", StringComparison.Ordinal),
        "The first line must record the launched MotionSFX version.");
    Check(appSource.Contains("Path.Combine(DataRoot, \"Logs\")", StringComparison.Ordinal),
        "Session logs must live inside the AppData MotionSFX\\Logs folder.");
});

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
ConsoleColor summaryColor = Console.ForegroundColor;
Console.ForegroundColor = failures.Count == 0 ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine($"Movement trigger and movement-math tests: {passed} passed, {failures.Count} failed.");
Console.ForegroundColor = summaryColor;
if (failures.Count > 0)
{
    foreach (string failure in failures) Console.Error.WriteLine(failure);
    return 1;
}
return 0;
