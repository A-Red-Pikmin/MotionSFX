using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Valve.VR;

namespace MotionSFX.Core;

internal sealed class SteamVrDashboardSnapshot
{
    public bool SteamVrDetected { get; init; }
    public bool ReceiverRunning { get; init; }
    public string ReceiverStatus { get; init; } = "Stopped";
    public string ReceiverDetail { get; init; } = "Reception is stopped";
    public int TrackerCount { get; init; }
    public int LiveTrackerCount { get; init; }
    public int BoneCount { get; init; }
    public long PacketCount { get; init; }
    public int ActivePresetCount { get; init; }
    public string ReceiverError { get; init; } = "";
    public IReadOnlyList<SteamVrDashboardTracker> Trackers { get; init; } = Array.Empty<SteamVrDashboardTracker>();
    public IReadOnlyList<SteamVrDashboardPreset> Presets { get; init; } = Array.Empty<SteamVrDashboardPreset>();

    public string Fingerprint()
    {
        StringBuilder value = new(256);
        value.Append(SteamVrDetected).Append('|').Append(ReceiverRunning).Append('|')
            .Append(ReceiverStatus).Append('|').Append(ReceiverDetail).Append('|')
            .Append(TrackerCount).Append('|').Append(LiveTrackerCount).Append('|')
            .Append(BoneCount).Append('|').Append(PacketCount).Append('|')
            .Append(ActivePresetCount).Append('|').Append(ReceiverError);
        foreach (SteamVrDashboardTracker tracker in Trackers)
            value.Append('|').Append(tracker.Label).Append(':').Append(tracker.Role).Append(':')
                .Append(tracker.Category).Append(':').Append(tracker.Status);
        foreach (SteamVrDashboardPreset preset in Presets)
            value.Append('|').Append(preset.Id).Append(':').Append(preset.Enabled).Append(':')
                .Append(preset.TargetRole).Append(':').Append(preset.Sensitivity).Append(':').Append(preset.Hotkey);
        return value.ToString();
    }
}

internal sealed record SteamVrDashboardTracker(string Label, string Role, string Category, string Status, long PacketCount);
internal sealed record SteamVrDashboardPreset(string Id, string Name, string Hotkey, string TargetRole, string Sensitivity, bool Enabled);

/// <summary>
/// Lightweight OpenVR dashboard adapter. It does not receive VMC data itself:
/// the desktop application's existing receiver remains the single source of truth.
/// Pump() is called from the existing 20 Hz UI timer.
/// </summary>
internal sealed class SteamVrDashboardService : IDisposable
{
    private const int CanvasWidth = 1000;
    private const int CanvasHeight = 760;
    private const float OverlayWidthMeters = 1.75f;
    private const int MaxEventsPerPump = 24;
    private const int MaxRetainedFrames = 5;
    private static readonly TimeSpan MinimumRenderInterval = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(12);

    private readonly string _logoPath;
    private readonly string _frameDirectory;
    private readonly Action<string, string> _command;
    private readonly BitmapSource _logo;
    private readonly Queue<string> _frameFiles = new();

    private SteamVrDashboardSnapshot _snapshot = new();
    private CVROverlay _overlay;
    private ulong _mainHandle;
    private ulong _thumbnailHandle;
    private bool _runtimeInitialized;
    private bool _overlayCreated;
    private bool _dirty = true;
    private bool _disposed;
    private int _page;
    private long _frameSequence;
    private Point _mousePosition;
    private bool _hasMousePosition;
    private bool _leftPointerButtonDown;
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private DateTime _lastRenderUtc = DateTime.MinValue;
    private DateTime _lastErrorLogUtc = DateTime.MinValue;
    private DateTime _lastClickDiagnosticUtc = DateTime.MinValue;
    private string _lastFingerprint = "";
    private string _lastInitError = "";

    public SteamVrDashboardService(string appRoot, Action<string, string> command)
    {
        _logoPath = Path.Combine(appRoot, "Assets", "MotionSFX-logo.png");
        // Per-process frame folders prevent PNG name collisions between app instances.
        string sessionId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        _frameDirectory = Path.Combine(AppLog.DataRoot, "dashboard-frames", sessionId);
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _logo = LoadLogo(_logoPath);
    }

    public void UpdateSnapshot(SteamVrDashboardSnapshot snapshot)
    {
        if (_disposed || snapshot == null) return;
        string fingerprint = snapshot.Fingerprint();
        _snapshot = snapshot;
        if (!string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
        {
            _lastFingerprint = fingerprint;
            _dirty = true;
        }
    }

    /// <summary>Called by MotionSFX's existing 50 ms dispatcher timer.</summary>
    public void Pump()
    {
        if (_disposed) return;

        if (!_snapshot.SteamVrDetected)
        {
            if (_runtimeInitialized) Disconnect("SteamVR process is no longer detected.");
            return;
        }

        if (!_runtimeInitialized)
        {
            if (DateTime.UtcNow >= _nextRetryUtc) TryInitialize();
            return;
        }

        try
        {
            PollOverlayEvents();
            if (_overlayCreated && _overlay.IsActiveDashboardOverlay(_mainHandle) && _dirty &&
                DateTime.UtcNow - _lastRenderUtc >= MinimumRenderInterval)
            {
                RenderAndSubmitFrame();
            }
        }
        catch (Exception ex)
        {
            LogInitializationFailure("OpenVR dashboard connection was lost: " + ex.Message);
            Disconnect("OpenVR dashboard API error.");
            _nextRetryUtc = DateTime.UtcNow + RetryInterval;
        }
    }

    private void TryInitialize()
    {
        _nextRetryUtc = DateTime.UtcNow + RetryInterval;
        try
        {
            if (!File.Exists(_logoPath))
                throw new FileNotFoundException("The supplied MotionSFX logo is missing.", _logoPath);

            EVRInitError initError = EVRInitError.None;
            CVRSystem system = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Overlay);
            if (initError != EVRInitError.None || system == null)
            {
                string reason = OpenVR.GetStringForHmdError(initError);
                throw new InvalidOperationException("OpenVR initialization failed: " + initError + " - " + reason);
            }

            _runtimeInitialized = true;
            try
            {
                SteamVrApplicationRegistration.RegisterAndEnableAutoLaunch(_logoPath);
            }
            catch (Exception registrationException)
            {
                // Startup registration is an optional convenience. A failure must not
                // prevent the existing in-session dashboard from connecting and working.
                AppLog.Write("WARN", "Could not enable SteamVR auto-launch for MotionSFX. The dashboard will remain available for this session. " + registrationException.Message);
            }

            _overlay = OpenVR.Overlay;
            if (_overlay == null)
                throw new InvalidOperationException("OpenVR did not provide the overlay interface.");

            EVROverlayError overlayError = _overlay.CreateDashboardOverlay(
                SteamVrApplicationRegistration.ApplicationKey, "MotionSFX", ref _mainHandle, ref _thumbnailHandle);
            EnsureOverlaySuccess(overlayError, "CreateDashboardOverlay");
            _overlayCreated = true;

            EnsureOverlaySuccess(_overlay.SetOverlayWidthInMeters(_mainHandle, OverlayWidthMeters), "SetOverlayWidthInMeters");
            EnsureOverlaySuccess(_overlay.SetOverlayInputMethod(_mainHandle, VROverlayInputMethod.Mouse), "SetOverlayInputMethod");
            HmdVector2_t mouseScale = new() { v0 = CanvasWidth, v1 = CanvasHeight };
            EnsureOverlaySuccess(_overlay.SetOverlayMouseScale(_mainHandle, ref mouseScale), "SetOverlayMouseScale");
            EnsureOverlaySuccess(_overlay.SetOverlayFromFile(_thumbnailHandle, _logoPath), "SetOverlayFromFile(thumbnail logo)");

            Directory.CreateDirectory(_frameDirectory);
            // The main dashboard image is rendered lazily, only after this tab becomes active.
            _dirty = true;
            _lastRenderUtc = DateTime.MinValue;
            AppLog.Write("INFO", "SteamVR dashboard tab registered through OpenVR. The dashboard uses the supplied MotionSFX logo and the existing VMC/preset state.");
            _lastInitError = "";
        }
        catch (Exception ex)
        {
            LogInitializationFailure("SteamVR dashboard initialization failed: " + ex.Message);
            Disconnect("Dashboard initialization failed.");
        }
    }

    private void PollOverlayEvents()
    {
        if (!_overlayCreated || _overlay == null || _mainHandle == 0) return;
        uint eventSize = (uint)Marshal.SizeOf<VREvent_t>();
        VREvent_t overlayEvent = new();

        for (int count = 0; count < MaxEventsPerPump &&
             _overlay.PollNextOverlayEvent(_mainHandle, ref overlayEvent, eventSize); count++)
        {
            EVREventType eventType = (EVREventType)overlayEvent.eventType;
            switch (eventType)
            {
                case EVREventType.VREvent_MouseMove:
                    // OpenVR mouse coordinates use a bottom-left origin. The dashboard is
                    // rendered by WPF into a top-left-origin bitmap, so flip Y before hit testing.
                    _mousePosition = ToDashboardPoint(overlayEvent.data.mouse.x, overlayEvent.data.mouse.y);
                    _hasMousePosition = true;
                    break;
                case EVREventType.VREvent_MouseButtonDown:
                    if (IsLeftMouseButton(overlayEvent.data.mouse.button))
                    {
                        _leftPointerButtonDown = true;
                        // Dashboard controls activate on trigger-down for immediate feedback.
                        // Valve's sample also uses the most recent MouseMove location for clicks.
                        if (_hasMousePosition) HandleClick(_mousePosition);
                    }
                    break;
                case EVREventType.VREvent_MouseButtonUp:
                    if (IsLeftMouseButton(overlayEvent.data.mouse.button))
                    {
                        // If SteamVR delivered only the release event, retain a safe fallback.
                        if (!_leftPointerButtonDown && _hasMousePosition) HandleClick(_mousePosition);
                        _leftPointerButtonDown = false;
                    }
                    break;
                case EVREventType.VREvent_OverlayShown:
                case EVREventType.VREvent_DashboardActivated:
                    _dirty = true;
                    _lastRenderUtc = DateTime.MinValue;
                    break;
            }
        }
    }

    private static bool IsLeftMouseButton(uint button) =>
        (button & (uint)EVRMouseButton.Left) != 0;

    private static Point ToDashboardPoint(float openVrX, float openVrY)
    {
        // EVRMouse_t documents coordinates as GL-space with (0,0) at bottom-left.
        // WPF DrawingContext uses a top-left origin, so invert Y and clamp to the canvas.
        double x = Math.Clamp(openVrX, 0f, CanvasWidth - 1f);
        double y = Math.Clamp(CanvasHeight - 1f - openVrY, 0f, CanvasHeight - 1f);
        return new Point(x, y);
    }

    private void HandleClick(Point point)
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastClickDiagnosticUtc >= TimeSpan.FromMilliseconds(500))
        {
            AppLog.Write("DEBUG", $"SteamVR dashboard pointer click mapped to ({point.X:0}, {point.Y:0}) on page {_page}.");
            _lastClickDiagnosticUtc = now;
        }
        if (new Rect(32, 124, 190, 54).Contains(point)) { SelectPage(0); return; }
        if (new Rect(234, 124, 190, 54).Contains(point)) { SelectPage(1); return; }
        if (new Rect(436, 124, 190, 54).Contains(point)) { SelectPage(2); return; }

        if (_page == 0)
        {
            if (new Rect(710, 452, 110, 48).Contains(point))
            {
                _command("start", "");
                _dirty = true;
            }
            else if (new Rect(838, 452, 110, 48).Contains(point))
            {
                _command("stop", "");
                _dirty = true;
            }
            return;
        }

        if (_page == 2)
        {
            for (int index = 0; index < _snapshot.Presets.Count && index < 7; index++)
            {
                // Match DrawPresetsPage: row y = 264 + 61 * index, button y = row y + 6.
                Rect toggle = new(840, 270 + index * 61, 112, 42);
                if (toggle.Contains(point))
                {
                    _command("toggle-preset", _snapshot.Presets[index].Id);
                    _dirty = true;
                    return;
                }
            }
        }
    }

    private void SelectPage(int page)
    {
        if (_page == page) return;
        _page = page;
        _dirty = true;
        _lastRenderUtc = DateTime.MinValue;
    }

    /// <summary>Forces a prompt redraw after a dashboard command changes app state.</summary>
    public void RequestImmediateRender()
    {
        if (_disposed) return;
        _dirty = true;
        _lastRenderUtc = DateTime.MinValue;
    }

    private void RenderAndSubmitFrame()
    {
        if (!_overlayCreated || _overlay == null) return;
        Directory.CreateDirectory(_frameDirectory);
        string framePath = Path.Combine(_frameDirectory, "dashboard_" + (++_frameSequence).ToString("D8", CultureInfo.InvariantCulture) + ".png");

        RenderTargetBitmap bitmap = DashboardRenderer.Render(_snapshot, _page, _logo, _runtimeInitialized);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = new(framePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            encoder.Save(stream);

        EVROverlayError result = _overlay.SetOverlayFromFile(_mainHandle, framePath);
        if (result != EVROverlayError.None)
        {
            TryDelete(framePath);
            EnsureOverlaySuccess(result, "SetOverlayFromFile(main dashboard)");
        }

        _frameFiles.Enqueue(framePath);
        while (_frameFiles.Count > MaxRetainedFrames)
            TryDelete(_frameFiles.Dequeue());

        _lastRenderUtc = DateTime.UtcNow;
        _dirty = false;
    }

    private void Disconnect(string reason)
    {
        bool hadRuntime = _runtimeInitialized;
        if (_overlayCreated && _overlay != null)
        {
            // CreateDashboardOverlay returns two independently addressable handles.
            try { if (_mainHandle != 0) _overlay.DestroyOverlay(_mainHandle); } catch { }
            try { if (_thumbnailHandle != 0) _overlay.DestroyOverlay(_thumbnailHandle); } catch { }
        }

        _overlayCreated = false;
        _mainHandle = 0;
        _thumbnailHandle = 0;
        _overlay = null;
        _hasMousePosition = false;
        _leftPointerButtonDown = false;
        _mousePosition = new Point();

        if (hadRuntime)
        {
            try { OpenVR.Shutdown(); } catch { }
            AppLog.Write("INFO", "SteamVR dashboard disconnected. " + reason);
        }
        _runtimeInitialized = false;
        _lastRenderUtc = DateTime.MinValue;
        _dirty = true;

        while (_frameFiles.Count > 0) TryDelete(_frameFiles.Dequeue());
    }

    private void LogInitializationFailure(string message)
    {
        DateTime now = DateTime.UtcNow;
        if (string.Equals(_lastInitError, message, StringComparison.Ordinal) &&
            now - _lastErrorLogUtc < TimeSpan.FromMinutes(1)) return;
        _lastInitError = message;
        _lastErrorLogUtc = now;
        AppLog.Write("WARN", message + " MotionSFX's desktop receiver remains available; the dashboard will retry while SteamVR is running.");
    }

    private static BitmapSource LoadLogo(string path)
    {
        if (!File.Exists(path)) return null;
        BitmapImage image = new();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void EnsureOverlaySuccess(EVROverlayError result, string operation)
    {
        if (result != EVROverlayError.None)
            throw new InvalidOperationException(operation + " returned OpenVR overlay error " + result + ".");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect("MotionSFX is closing.");
        try
        {
            if (Directory.Exists(_frameDirectory))
                Directory.Delete(_frameDirectory, recursive: true);
        }
        catch { }
    }
}

internal static class DashboardRenderer
{
    public const int Width = 1000;
    public const int Height = 760;

    private static readonly Brush Background = MakeBrush("#17121F");
    private static readonly Brush Panel = MakeBrush("#292130");
    private static readonly Brush PanelAlt = MakeBrush("#211A29");
    private static readonly Brush Border = MakeBrush("#493A55");
    private static readonly Brush Text = MakeBrush("#F4F0FA");
    private static readonly Brush Muted = MakeBrush("#B5A7C3");
    private static readonly Brush Purple = MakeBrush("#9B69D5");
    private static readonly Brush PurpleDark = MakeBrush("#523477");
    private static readonly Brush Green = MakeBrush("#50D69B");
    private static readonly Brush Yellow = MakeBrush("#E8C46A");
    private static readonly Brush Red = MakeBrush("#E57886");

    private static SolidColorBrush MakeBrush(string hex)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static RenderTargetBitmap Render(SteamVrDashboardSnapshot snapshot, int page, BitmapSource logo, bool runtimeConnected)
    {
        DrawingVisual visual = new();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, Width, Height));
            DrawHeader(dc, snapshot, logo, runtimeConnected);
            DrawNavigation(dc, page);
            switch (page)
            {
                case 1: DrawTrackersPage(dc, snapshot); break;
                case 2: DrawPresetsPage(dc, snapshot); break;
                default: DrawOverviewPage(dc, snapshot, runtimeConnected); break;
            }
            DrawFooter(dc, snapshot);
        }

        RenderTargetBitmap bitmap = new(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawHeader(DrawingContext dc, SteamVrDashboardSnapshot snapshot, BitmapSource logo, bool runtimeConnected)
    {
        DrawRoundRect(dc, Panel, Border, new Rect(30, 20, 82, 82), 16);
        if (logo != null) dc.DrawImage(logo, new Rect(36, 26, 70, 70));
        DrawText(dc, "MotionSFX", 128, 30, 30, Text, 420);
        DrawText(dc, "SteamVR Dashboard", 130, 66, 15, Muted, 420);
        DrawText(dc, "ALPHA 0.9.10.2", 130, 89, 11, Purple, 260, true);

        bool connected = runtimeConnected;
        Brush stateBrush = connected ? Green : snapshot.SteamVrDetected ? Yellow : Red;
        string status = connected ? "CONNECTED" : snapshot.SteamVrDetected ? "DETECTED" : "NOT RUNNING";
        DrawRoundRect(dc, PanelAlt, Border, new Rect(830, 35, 138, 38), 18);
        DrawCircle(dc, stateBrush, new Point(849, 54), 5);
        DrawText(dc, status, 864, 44, 12, stateBrush, 94, true);
        dc.DrawLine(new Pen(Border, 1), new Point(30, 114), new Point(970, 114));
    }

    private static void DrawNavigation(DrawingContext dc, int selectedPage)
    {
        DrawNavButton(dc, "Overview", 32, selectedPage == 0);
        DrawNavButton(dc, "Trackers", 234, selectedPage == 1);
        DrawNavButton(dc, "Presets", 436, selectedPage == 2);
    }

    private static void DrawNavButton(DrawingContext dc, string label, double x, bool selected)
    {
        Rect rect = new(x, 124, 190, 54);
        DrawRoundRect(dc, selected ? PurpleDark : Panel, selected ? Purple : Border, rect, 12);
        DrawText(dc, label, x + 20, 139, 17, selected ? Text : Muted, 150, true);
    }

    private static void DrawOverviewPage(DrawingContext dc, SteamVrDashboardSnapshot snapshot, bool runtimeConnected)
    {
        DrawText(dc, "Overview", 34, 198, 25, Text, 400, true);
        DrawText(dc, "Live status from the existing MotionSFX receiver and movement engine", 36, 230, 13, Muted, 760);

        DrawMetricCard(dc, 32, 264, "VMC RECEIVER", snapshot.ReceiverStatus,
            snapshot.ReceiverRunning ? (snapshot.LiveTrackerCount > 0 ? Green : Yellow) : Red,
            snapshot.ReceiverDetail, 216);
        DrawMetricCard(dc, 266, 264, "TRACKER OUTPUTS", $"{snapshot.LiveTrackerCount} / {snapshot.TrackerCount}",
            snapshot.LiveTrackerCount > 0 ? Green : Yellow, "Live / detected identities", 216);
        DrawMetricCard(dc, 500, 264, "ACTIVE PRESETS", $"{snapshot.ActivePresetCount} / 7",
            snapshot.ActivePresetCount > 0 ? Green : Muted, "Movement actions enabled", 216);
        DrawMetricCard(dc, 734, 264, "STEAMVR RUNTIME", runtimeConnected ? "Connected" : snapshot.SteamVrDetected ? "Detected" : "Offline",
            runtimeConnected ? Green : snapshot.SteamVrDetected ? Yellow : Red,
            runtimeConnected ? "OpenVR dashboard ready" : "Waiting for OpenVR connection", 234);

        DrawRoundRect(dc, Panel, Border, new Rect(32, 414, 936, 154), 14);
        DrawText(dc, "VMC RECEPTION", 56, 436, 13, Muted, 260, true);
        DrawText(dc, snapshot.ReceiverRunning ? "Receiver is active" : "Receiver is stopped", 56, 463, 20, Text, 480, true);
        string detail = string.IsNullOrWhiteSpace(snapshot.ReceiverError) ? snapshot.ReceiverDetail : snapshot.ReceiverError;
        DrawText(dc, detail, 58, 498, 13, Muted, 620);
        DrawText(dc, $"UDP 127.0.0.1:39539   ·   {snapshot.PacketCount:N0} packets   ·   {snapshot.BoneCount} skeleton bones",
            58, 529, 12, Muted, 620);
        DrawButton(dc, "START", new Rect(710, 452, 110, 48), !snapshot.ReceiverRunning);
        DrawButton(dc, "STOP", new Rect(838, 452, 110, 48), snapshot.ReceiverRunning);

        DrawText(dc, "TRACKER SNAPSHOT", 34, 596, 13, Muted, 400, true);
        if (snapshot.Trackers.Count == 0)
        {
            DrawText(dc, "No VMC tracker outputs have been detected yet.", 36, 626, 15, Muted, 800);
        }
        else
        {
            int y = 625;
            foreach (SteamVrDashboardTracker tracker in snapshot.Trackers.Take(3))
            {
                DrawText(dc, Clip(tracker.Label, 34), 38, y, 13, Text, 300, true);
                DrawText(dc, tracker.Role, 360, y, 12, Muted, 190);
                DrawStatusPill(dc, tracker.Status.ToUpperInvariant(), tracker.Status == "Live" ? Green : tracker.Status == "Stale" ? Yellow : Red,
                    new Rect(822, y - 2, 116, 27));
                y += 37;
            }
        }
    }

    private static void DrawTrackersPage(DrawingContext dc, SteamVrDashboardSnapshot snapshot)
    {
        DrawText(dc, "Detected Trackers", 34, 198, 25, Text, 520, true);
        DrawText(dc, $"{snapshot.TrackerCount} unique VMC identities · {snapshot.LiveTrackerCount} currently live · {snapshot.BoneCount} skeleton bones",
            36, 230, 13, Muted, 860);

        DrawRoundRect(dc, Panel, Border, new Rect(32, 266, 936, 420), 14);
        DrawText(dc, "VMC IDENTITY", 54, 285, 11, Muted, 300, true);
        DrawText(dc, "BODY ROLE", 480, 285, 11, Muted, 170, true);
        DrawText(dc, "TYPE", 650, 285, 11, Muted, 125, true);
        DrawText(dc, "STATUS", 820, 285, 11, Muted, 125, true);
        dc.DrawLine(new Pen(Border, 1), new Point(52, 312), new Point(948, 312));

        if (snapshot.Trackers.Count == 0)
        {
            DrawText(dc, "Waiting for VMC tracker output…", 58, 350, 16, Muted, 700);
            DrawText(dc, "Start reception in MotionSFX or from the Overview tab.", 58, 382, 13, Muted, 700);
            return;
        }

        int rowIndex = 0;
        foreach (SteamVrDashboardTracker tracker in snapshot.Trackers.Take(6))
        {
            double y = 324 + rowIndex * 56;
            DrawText(dc, Clip(tracker.Label, 38), 54, y + 5, 14, Text, 400, true);
            DrawText(dc, tracker.Role, 480, y + 6, 13, Muted, 160);
            DrawText(dc, tracker.Category, 650, y + 6, 12, Muted, 150);
            DrawStatusPill(dc, tracker.Status.ToUpperInvariant(), tracker.Status == "Live" ? Green : tracker.Status == "Stale" ? Yellow : Red,
                new Rect(820, y, 112, 30));
            dc.DrawLine(new Pen(Border, 0.7), new Point(52, y + 46), new Point(948, y + 46));
            rowIndex++;
        }
        if (snapshot.Trackers.Count > 6)
            DrawText(dc, $"+ {snapshot.Trackers.Count - 6} more tracker output(s) in the desktop Trackers view", 56, 666, 11, Muted, 850);
    }

    private static void DrawPresetsPage(DrawingContext dc, SteamVrDashboardSnapshot snapshot)
    {
        DrawText(dc, "Movement Presets", 34, 198, 25, Text, 520, true);
        DrawText(dc, "Toggle the existing seven movement presets. Detailed keybind and sensitivity editing stays in the desktop app.",
            36, 230, 13, Muted, 900);

        int index = 0;
        foreach (SteamVrDashboardPreset preset in snapshot.Presets.Take(7))
        {
            double y = 264 + index * 61;
            DrawRoundRect(dc, Panel, Border, new Rect(32, y, 936, 54), 11);
            DrawText(dc, preset.Name, 52, y + 8, 15, Text, 250, true);
            DrawText(dc, $"Target: {preset.TargetRole}", 322, y + 10, 12, Muted, 190);
            DrawText(dc, $"Sensitivity: {preset.Sensitivity}", 520, y + 10, 12, Muted, 170);
            DrawText(dc, $"Key: {(string.IsNullOrWhiteSpace(preset.Hotkey) ? "Not set" : preset.Hotkey)}", 690, y + 10, 12, Muted, 135);
            DrawButton(dc, preset.Enabled ? "ENABLED" : "DISABLED",
                new Rect(840, y + 6, 112, 42), preset.Enabled);
            index++;
        }

        if (snapshot.Presets.Count != 7)
            DrawText(dc, "Preset configuration is incomplete. Open MotionSFX on the desktop to inspect it.", 36, 704, 12, Yellow, 900);
    }

    private static void DrawMetricCard(DrawingContext dc, double x, double y, string label, string value, Brush state, string detail, double width)
    {
        DrawRoundRect(dc, Panel, Border, new Rect(x, y, width, 126), 14);
        DrawText(dc, label, x + 18, y + 16, 11, Muted, width - 32, true);
        DrawText(dc, value, x + 18, y + 43, 21, state, width - 32, true);
        DrawText(dc, detail, x + 18, y + 82, 11, Muted, width - 32);
    }

    private static void DrawButton(DrawingContext dc, string label, Rect rect, bool primary)
    {
        DrawRoundRect(dc, primary ? PurpleDark : PanelAlt, primary ? Purple : Border, rect, 10);
        DrawText(dc, label, rect.X + 8, rect.Y + 13, 12, primary ? Text : Muted, rect.Width - 16, true, TextAlignment.Center);
    }

    private static void DrawStatusPill(DrawingContext dc, string text, Brush color, Rect rect)
    {
        DrawRoundRect(dc, PanelAlt, color, rect, 14);
        DrawText(dc, text, rect.X + 6, rect.Y + 7, 10, color, rect.Width - 12, true, TextAlignment.Center);
    }

    private static void DrawFooter(DrawingContext dc, SteamVrDashboardSnapshot snapshot)
    {
        dc.DrawLine(new Pen(Border, 1), new Point(30, 720), new Point(970, 720));
        DrawText(dc, "MOTIONSFX  ·  NATIVE OPENVR INTEGRATION", 34, 732, 10, Muted, 560, true);
        DrawText(dc, snapshot.ReceiverRunning ? "VMC: RUNNING" : "VMC: STOPPED", 800, 732, 10,
            snapshot.ReceiverRunning ? Green : Red, 160, true, TextAlignment.Right);
    }

    private static void DrawRoundRect(DrawingContext dc, Brush fill, Brush border, Rect rect, double radius)
    {
        Pen pen = border == null ? null : new Pen(border, 1);
        dc.DrawRoundedRectangle(fill, pen, rect, radius, radius);
    }

    private static void DrawCircle(DrawingContext dc, Brush fill, Point center, double radius) =>
        dc.DrawEllipse(fill, null, center, radius, radius);

    private static void DrawText(DrawingContext dc, string value, double x, double y, double size, Brush brush,
        double maxWidth, bool bold = false, TextAlignment alignment = TextAlignment.Left)
    {
        FormattedText text = new(value ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, brush, 1.0)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            TextAlignment = alignment,
            Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(text, new Point(x, y));
    }

    private static string Clip(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
        return value[..Math.Max(1, maxLength - 1)] + "…";
    }
}
