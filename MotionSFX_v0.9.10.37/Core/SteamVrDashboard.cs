using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

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
    public IReadOnlyList<SteamVrDashboardGraph> Graphs { get; init; } = Array.Empty<SteamVrDashboardGraph>();

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
        foreach (SteamVrDashboardGraph graph in Graphs)
        {
            value.Append('|').Append("graph:").Append(graph.Id).Append(':').Append(graph.Status).Append(':').Append(graph.TimingStatus)
                .Append(':').Append(graph.PositionThreshold.ToString("R", CultureInfo.InvariantCulture))
                .Append(':').Append(graph.RotationThreshold.ToString("R", CultureInfo.InvariantCulture));
            if (graph.Samples.Count > 0)
            {
                MovementPreviewGraphSample last = graph.Samples[^1];
                value.Append(':').Append(last.Utc.Ticks)
                    .Append(':').Append(last.PositionRatio.ToString("R", CultureInfo.InvariantCulture))
                    .Append(':').Append(last.RotationRatio.ToString("R", CultureInfo.InvariantCulture));
            }
            value.Append(':').Append(graph.Samples.Count);
        }
        return value.ToString();
    }
}

internal sealed record SteamVrDashboardTracker(string Label, string Role, string Category, string Status, long PacketCount);
internal sealed record SteamVrDashboardPreset(string Id, string Name, string Hotkey, string TargetRole, string Sensitivity, bool Enabled);
internal sealed record SteamVrDashboardGraph(
    string Id, string Name, string Status, string TimingStatus,
    double PositionThreshold, double RotationThreshold,
    IReadOnlyList<MovementPreviewGraphSample> Samples);

/// <summary>
/// Lightweight OpenVR dashboard adapter. It does not receive VMC data itself:
/// the desktop application's existing receiver remains the single source of truth.
/// Pump() is called from the existing 20 Hz UI timer.
/// </summary>
internal sealed class SteamVrDashboardService : IDisposable
{
    private const int CanvasWidth = 1000;
    private const int CanvasHeight = 760;
    private const float OverlayWidthMeters = 1.9f;
    private const int MaxEventsPerPump = 24;
    private static readonly TimeSpan MinimumRenderInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(12);

    private readonly string _logoPath;
    private readonly Action<string, string> _command;
    private readonly BitmapSource _logo;
    // Reuse CPU-side pixel buffers, one stable OpenVR display texture and one fast
    // CPU-writable upload texture. Only the upload texture is mapped; its contents
    // are copied into the stable display texture held by the compositor.
    private readonly byte[] _pbgraBuffer = new byte[CanvasWidth * CanvasHeight * 4];
    private readonly byte[] _rgbaBuffer = new byte[CanvasWidth * CanvasHeight * 4];
    private ID3D11Device _textureDevice;
    private ID3D11DeviceContext _textureContext;
    // OpenVR holds one stable DEFAULT texture for the lifetime of the overlay.
    // CPU writes go to a separate DYNAMIC upload texture using WRITE_DISCARD, then
    // CopyResource updates the stable display texture without renaming its resource.
    private ID3D11Texture2D _overlayTexture;
    private ID3D11Texture2D _uploadTexture;
    // OpenVR's DXGISharedHandle texture type expects the HANDLE value itself,
    // not a pointer to a variable containing the HANDLE.
    private IntPtr _sharedOverlayTextureHandle;
    private bool _overlayTextureAssigned;

    private static readonly Guid DxgiResourceIid = new("035F3AB4-482E-4E50-B41F-8A7F8BD8960B");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DxgiGetSharedHandleDelegate(IntPtr dxgiResource, out IntPtr sharedHandle);

    private SteamVrDashboardSnapshot _snapshot = new();
    private CVROverlay _overlay;
    private ulong _mainHandle;
    private ulong _thumbnailHandle;
    private bool _runtimeInitialized;
    private bool _overlayCreated;
    private bool _dirty = true;
    private bool _disposed;
    private int _page;
    private Point _mousePosition;
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private DateTime _lastRenderUtc = DateTime.MinValue;
    private DateTime _lastErrorLogUtc = DateTime.MinValue;
    private DateTime _lastClickDiagnosticUtc = DateTime.MinValue;
    private string _lastFingerprint = "";
    private string _lastInitError = "";

    public SteamVrDashboardService(string appRoot, Action<string, string> command)
    {
        _logoPath = Path.Combine(appRoot, "Assets", "MotionSFX-logo.png");
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

    /// <summary>Called by MotionSFX's existing 50 ms dispatcher timer; active dashboards can render at up to 20 FPS.</summary>
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
                    break;
                case EVREventType.VREvent_MouseButtonDown:
                    if (IsLeftMouseButton(overlayEvent.data.mouse.button))
                    {
                        // Mouse button events carry their own GL-space coordinates.
                        // Capture them directly instead of depending on a prior MouseMove.
                        _mousePosition = ToDashboardPoint(overlayEvent.data.mouse.x, overlayEvent.data.mouse.y);
                    }
                    break;
                case EVREventType.VREvent_MouseButtonUp:
                    if (IsLeftMouseButton(overlayEvent.data.mouse.button))
                    {
                        // Route the click on release, using the release event's own
                        // coordinates. This still works if SteamVR omits a MouseMove.
                        _mousePosition = ToDashboardPoint(overlayEvent.data.mouse.x, overlayEvent.data.mouse.y);
                        HandleClick(_mousePosition);
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
        if (new Rect(638, 124, 190, 54).Contains(point)) { SelectPage(3); return; }

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

        // Render into WPF's premultiplied BGRA surface, then convert to straight RGBA
        // for the persistent R8G8B8A8 D3D11 texture.
        RenderTargetBitmap bitmap = DashboardRenderer.Render(_snapshot, _page, _logo, _runtimeInitialized);
        int stride = checked(CanvasWidth * 4);
        bitmap.CopyPixels(_pbgraBuffer, stride, 0);
        ConvertPbgra32ToRgba(_pbgraBuffer, _rgbaBuffer);

        EnsureOverlayTexture();
        UploadOverlayTexture();

        // Attach the stable DEFAULT texture once. Subsequent frames copy new pixels
        // into this same resource; they do not re-submit or re-import the texture.
        if (!_overlayTextureAssigned)
        {
            if (_sharedOverlayTextureHandle == IntPtr.Zero)
                throw new InvalidOperationException("The SteamVR dashboard shared texture handle is unavailable.");

            Texture_t texture = new()
            {
                // TextureType_DXGISharedHandle takes the HANDLE value directly.
                // Do not pass the address of a local HANDLE variable here.
                handle = _sharedOverlayTextureHandle,
                eType = (ETextureType)5, // TextureType_DXGISharedHandle
                eColorSpace = EColorSpace.Auto
            };
            EnsureOverlaySuccess(_overlay.SetOverlayTexture(_mainHandle, ref texture),
                "SetOverlayTexture(main dashboard, DXGI shared handle)");
            _overlayTextureAssigned = true;
            AppLog.Write("INFO", "SteamVR dashboard attached through a persistent DXGI shared texture handle.");
        }

        _lastRenderUtc = DateTime.UtcNow;
        _dirty = false;
    }

    private void EnsureOverlayTexture()
    {
        if (_textureDevice != null && _textureContext != null && _overlayTexture != null &&
            _uploadTexture != null && _sharedOverlayTextureHandle != IntPtr.Zero)
            return;

        Result deviceResult = D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out ID3D11Device device,
            out _,
            out ID3D11DeviceContext context);
        if (deviceResult.Failure)
            throw new InvalidOperationException("Direct3D 11 device creation failed: " + deviceResult);

        _textureDevice = device;
        _textureContext = context;

        try
        {
            Texture2DDescription displayDescription = new()
            {
                Width = (uint)CanvasWidth,
                Height = (uint)CanvasHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                // The shared overlay texture is read by SteamVR's compositor.
                // Keep it shader-readable and render-target capable, and mark it
                // with D3D11_RESOURCE_MISC_SHARED for OpenVR's legacy DXGI handle path.
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared
            };
            _overlayTexture = _textureDevice.CreateTexture2D(displayDescription);
            _sharedOverlayTextureHandle = GetDxgiSharedHandle(_overlayTexture);

            Texture2DDescription uploadDescription = displayDescription;
            uploadDescription.Usage = ResourceUsage.Dynamic;
            uploadDescription.BindFlags = BindFlags.ShaderResource;
            uploadDescription.CPUAccessFlags = CpuAccessFlags.Write;
            // The upload surface is local CPU-writable memory, not a shared
            // compositor resource. Shared + Dynamic + CPU write is an invalid
            // D3D11 resource combination, so clear the inherited shared flag.
            uploadDescription.MiscFlags = ResourceOptionFlags.None;
            _uploadTexture = _textureDevice.CreateTexture2D(uploadDescription);
        }
        catch
        {
            DisposeDashboardGraphics();
            throw;
        }
    }

    /// <summary>
    /// Retrieves the legacy DXGI shared HANDLE required by OpenVR overlays.
    /// OpenVR does not currently support the NT-handle/keyed-mutex variant for
    /// TextureType_DXGISharedHandle, so the backing texture uses D3D11_RESOURCE_MISC_SHARED.
    /// </summary>
    private static IntPtr GetDxgiSharedHandle(ID3D11Texture2D texture)
    {
        if (texture == null || texture.NativePointer == IntPtr.Zero)
            throw new ArgumentNullException(nameof(texture));

        Guid resourceIid = DxgiResourceIid;
        int queryResult = Marshal.QueryInterface(texture.NativePointer, in resourceIid, out IntPtr resourcePointer);
        if (queryResult < 0 || resourcePointer == IntPtr.Zero)
            Marshal.ThrowExceptionForHR(queryResult < 0 ? queryResult : unchecked((int)0x80004003));

        try
        {
            // IDXGIResource inherits IUnknown (3), IDXGIObject (4), and
            // IDXGIDeviceSubObject (1); GetSharedHandle is vtable slot 8.
            IntPtr vtable = Marshal.ReadIntPtr(resourcePointer);
            IntPtr methodPointer = Marshal.ReadIntPtr(vtable, 8 * IntPtr.Size);
            if (methodPointer == IntPtr.Zero)
                throw new InvalidOperationException("IDXGIResource.GetSharedHandle is unavailable.");

            DxgiGetSharedHandleDelegate getSharedHandle =
                Marshal.GetDelegateForFunctionPointer<DxgiGetSharedHandleDelegate>(methodPointer);
            int handleResult = getSharedHandle(resourcePointer, out IntPtr sharedHandle);
            if (handleResult < 0)
                Marshal.ThrowExceptionForHR(handleResult);
            if (sharedHandle == IntPtr.Zero)
                throw new InvalidOperationException("IDXGIResource.GetSharedHandle returned a null handle.");

            return sharedHandle;
        }
        finally
        {
            Marshal.Release(resourcePointer);
        }
    }

    private void UploadOverlayTexture()
    {
        if (_textureContext == null || _overlayTexture == null || _uploadTexture == null)
            throw new InvalidOperationException("The SteamVR dashboard textures have not been initialized.");

        Result mapResult = _textureContext.Map(
            _uploadTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped);
        if (mapResult.Failure)
            throw new InvalidOperationException("Could not map the SteamVR dashboard texture: " + mapResult);

        try
        {
            int sourceStride = checked(CanvasWidth * 4);
            int destinationStride = checked((int)mapped.RowPitch);
            for (int row = 0; row < CanvasHeight; row++)
            {
                Marshal.Copy(
                    _rgbaBuffer,
                    row * sourceStride,
                    IntPtr.Add(mapped.DataPointer, row * destinationStride),
                    sourceStride);
            }
        }
        finally
        {
            _textureContext.Unmap(_uploadTexture, 0);
        }

        // Copy CPU-written staging pixels into the stable GPU texture that OpenVR
        // references. This avoids WRITE_DISCARD renaming the resource held by the compositor.
        _textureContext.CopyResource(_overlayTexture, _uploadTexture);
        _textureContext.Flush();
    }

    private void DisposeDashboardGraphics()
    {
        _overlayTextureAssigned = false;
        // GetSharedHandle returns a legacy DXGI handle, not a Win32 NT handle.
        // Its lifetime is tied to the shared texture; do not call CloseHandle.
        _sharedOverlayTextureHandle = IntPtr.Zero;
        try { _overlayTexture?.Dispose(); } catch { }
        try { _uploadTexture?.Dispose(); } catch { }
        try { _textureContext?.Dispose(); } catch { }
        try { _textureDevice?.Dispose(); } catch { }
        _overlayTexture = null;
        _uploadTexture = null;
        _textureContext = null;
        _textureDevice = null;
    }

    private static void ConvertPbgra32ToRgba(byte[] pbgraPixels, byte[] rgbaPixels)
    {
        if (pbgraPixels == null || rgbaPixels == null || pbgraPixels.Length != rgbaPixels.Length ||
            (pbgraPixels.Length % 4) != 0)
            throw new ArgumentException("The WPF and OpenVR dashboard pixel buffers must have matching four-byte pixels.");

        for (int i = 0; i < pbgraPixels.Length; i += 4)
        {
            byte alpha = pbgraPixels[i + 3];
            if (alpha == 255)
            {
                // The dashboard background is opaque, so most pixels take this fast path.
                rgbaPixels[i] = pbgraPixels[i + 2];
                rgbaPixels[i + 1] = pbgraPixels[i + 1];
                rgbaPixels[i + 2] = pbgraPixels[i];
            }
            else if (alpha == 0)
            {
                rgbaPixels[i] = 0;
                rgbaPixels[i + 1] = 0;
                rgbaPixels[i + 2] = 0;
            }
            else
            {
                // WPF stores Pbgra32 as premultiplied B,G,R,A. The D3D11 texture
                // uses straight R,G,B,A, so un-premultiply and reorder translucent pixels.
                rgbaPixels[i] = Unpremultiply(pbgraPixels[i + 2], alpha);
                rgbaPixels[i + 1] = Unpremultiply(pbgraPixels[i + 1], alpha);
                rgbaPixels[i + 2] = Unpremultiply(pbgraPixels[i], alpha);
            }
            rgbaPixels[i + 3] = alpha;
        }
    }

    private static byte Unpremultiply(byte value, byte alpha) =>
        (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);

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
        DisposeDashboardGraphics();
        _mousePosition = new Point();

        if (hadRuntime)
        {
            try { OpenVR.Shutdown(); } catch { }
            AppLog.Write("INFO", "SteamVR dashboard disconnected. " + reason);
        }
        _runtimeInitialized = false;
        _lastRenderUtc = DateTime.MinValue;
        _dirty = true;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect("MotionSFX is closing.");
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
    private static readonly Brush PositionGraphBrush = MakeBrush("#A78BFA");
    private static readonly Brush RotationGraphBrush = MakeBrush("#60A5FA");
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
                case 3: DrawLiveGraphsPage(dc, snapshot); break;
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
        DrawText(dc, "ALPHA 0.9.10.37", 130, 89, 11, Purple, 260, true);

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
        DrawNavButton(dc, "Live Graphs", 638, selectedPage == 3);
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

    private static void DrawLiveGraphsPage(DrawingContext dc, SteamVrDashboardSnapshot snapshot)
    {
        DrawText(dc, "Live Movement Graphs", 34, 198, 25, Text, 500, true);
        DrawText(dc, "The same seven live position and rotation histories shown on the preset pages · last 10 seconds",
            36, 230, 13, Muted, 900);

        if (snapshot.Graphs.Count == 0)
        {
            DrawText(dc, "Waiting for movement graph data from MotionSFX…", 40, 280, 16, Muted, 800);
            return;
        }

        for (int index = 0; index < snapshot.Graphs.Count && index < 7; index++)
        {
            SteamVrDashboardGraph graph = snapshot.Graphs[index];
            int column = index % 2;
            int row = index / 2;
            double x = 32 + column * 474;
            double y = 252 + row * 112;
            Rect card = new(x, y, 454, 104);
            DrawRoundRect(dc, Panel, Border, card, 11);

            DrawText(dc, graph.Name, x + 14, y + 11, 13, Text, 134, true);
            DrawText(dc, graph.Status, x + 14, y + 35, 9, Muted, 136);
            DrawText(dc, graph.TimingStatus, x + 14, y + 54, 8, Purple, 148);

            DrawCircle(dc, PositionGraphBrush, new Point(x + 164, y + 17), 4);
            DrawText(dc, $"POS {graph.PositionThreshold:0.000} u", x + 173, y + 9, 9, PositionGraphBrush, 92, true);
            DrawCircle(dc, RotationGraphBrush, new Point(x + 276, y + 17), 4);
            DrawText(dc, $"ROT {graph.RotationThreshold:0.0}°", x + 285, y + 9, 9, RotationGraphBrush, 122, true);

            Rect plot = new(x + 174, y + 29, 258, 62);
            dc.PushClip(new RectangleGeometry(plot));
            dc.DrawLine(new Pen(Border, 0.8), new Point(plot.Left, plot.Top), new Point(plot.Right, plot.Top));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(244, 201, 93)) { Opacity = 0.9 }, 1.0)
                { DashStyle = DashStyles.Dash },
                new Point(plot.Left, plot.Top + plot.Height / 2),
                new Point(plot.Right, plot.Top + plot.Height / 2));
            dc.DrawLine(new Pen(Border, 0.8), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
            DateTime now = DateTime.UtcNow;
            DrawGraphSeries(dc, graph.Samples, now, plot, usePosition: true, PositionGraphBrush);
            DrawGraphSeries(dc, graph.Samples, now, plot, usePosition: false, RotationGraphBrush);
            dc.Pop();

            DrawText(dc, "2×", x + 145, plot.Top - 4, 8, Muted, 24, false, TextAlignment.Right);
            DrawText(dc, "1×", x + 145, plot.Top + plot.Height / 2 - 4, 8, Yellow, 24, false, TextAlignment.Right);
            DrawText(dc, "0", x + 145, plot.Bottom - 8, 8, Muted, 24, false, TextAlignment.Right);
        }
    }

    private static void DrawGraphSeries(DrawingContext dc,
        IReadOnlyList<MovementPreviewGraphSample> samples, DateTime now, Rect plot,
        bool usePosition, Brush brush)
    {
        if (samples == null || samples.Count == 0) return;
        DateTime start = now.AddSeconds(-10);
        List<Point> points = new(samples.Count);
        foreach (MovementPreviewGraphSample sample in samples)
        {
            double timeFraction = Math.Clamp((sample.Utc - start).TotalSeconds / 10.0, 0.0, 1.0);
            double ratio = Math.Clamp(usePosition ? sample.PositionRatio : sample.RotationRatio, 0.0, 2.0);
            points.Add(new Point(plot.Left + timeFraction * plot.Width,
                plot.Bottom - ratio / 2.0 * plot.Height));
        }
        if (points.Count == 1)
            points.Add(new Point(Math.Min(plot.Right, points[0].X + 1.5), points[0].Y));
        if (points.Count < 2) return;

        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(brush, 2.0) { LineJoin = PenLineJoin.Round }, geometry);
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
