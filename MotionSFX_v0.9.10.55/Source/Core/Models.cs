using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace MotionSFX.Core;

internal sealed class AppSettings
{
    [JsonPropertyName("bindAddress")] public string BindAddress { get; set; } = "127.0.0.1";
    [JsonPropertyName("udpPort")] public int UdpPort { get; set; } = 39539;
    [JsonPropertyName("staleAfterMs")] public int StaleAfterMs { get; set; } = 1500;
    [JsonPropertyName("lostAfterMs")] public int LostAfterMs { get; set; } = 5000;
    [JsonPropertyName("keybindDelayMs")] public int KeybindDelayMs { get; set; } = 0;
    [JsonPropertyName("configVersion")] public int ConfigVersion { get; set; } = 1;
}

internal sealed class MovementPreset
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("hotkey")] public string Hotkey { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("sensitivity")] public string Sensitivity { get; set; } = "Normal";
    [JsonPropertyName("cooldownMs")] public int CooldownMs { get; set; } = 0;
    [JsonPropertyName("customPositionThreshold")] public double CustomPositionThreshold { get; set; } = 0.15;
    [JsonPropertyName("customRotationThreshold")] public double CustomRotationThreshold { get; set; } = 10.0;
    [JsonPropertyName("targetRole")] public string TargetRole { get; set; } = "";
}

internal sealed class TransformData
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double QX { get; set; }
    public double QY { get; set; }
    public double QZ { get; set; }
    public double QW { get; set; }
    public string SourceAddress { get; set; } = "";
    public string SenderEndpoint { get; set; } = "";
    public DateTime ReceivedUtc { get; set; }
}

internal sealed class DeviceData
{
    public string Id { get; set; } = "";
    public string VmcLabel { get; set; } = "";
    public HashSet<string> Kinds { get; } = new(StringComparer.Ordinal);
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public long PacketCount { get; set; }
    public string SenderEndpoint { get; set; } = "";
    public TransformData Global { get; set; }
    public TransformData Local { get; set; }
}

internal sealed class BoneData
{
    public string Id { get; set; } = "";
    public string BoneName { get; set; } = "";
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public long PacketCount { get; set; }
    public string SenderEndpoint { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double QX { get; set; }
    public double QY { get; set; }
    public double QZ { get; set; }
    public double QW { get; set; } = 1.0;
}

internal sealed class TrackerGridRow
{
    public string id { get; set; } = "";
    public string vmcLabel { get; set; } = "";
    public string category { get; set; } = "";
    public string[] kinds { get; set; } = Array.Empty<string>();
    public string role { get; set; } = "Unassigned";
    public string status { get; set; } = "Lost";
    public int ageMs { get; set; }
    public long packetCount { get; set; }
    public string senderEndpoint { get; set; } = "";
    public string positionText { get; set; } = "—";
}

internal sealed class BoneGridRow
{
    public string id { get; set; } = "";
    public string boneName { get; set; } = "";
    public string status { get; set; } = "Lost";
    public int ageMs { get; set; }
    public long packetCount { get; set; }
    public string senderEndpoint { get; set; } = "";
    public string positionText { get; set; } = "";
    public string rotationText { get; set; } = "";
}

internal sealed class RoleOwnership
{
    public readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal);
    public readonly HashSet<string> AmbiguousRoles = new(StringComparer.Ordinal);
}

internal sealed class MovementAnchor
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double QX { get; set; }
    public double QY { get; set; }
    public double QZ { get; set; }
    public double QW { get; set; } = 1.0;
    public DateTime AnchorUtc { get; set; }
    public DateTime LastSampleUtc { get; set; }
    public string Space { get; set; } = "";
}


internal sealed record MovementPreviewSnapshot(
    bool HasMeasurement,
    double Position,
    double Rotation,
    double PositionThreshold,
    double RotationThreshold,
    DateTime LastSampleUtc,
    string Note,
    bool ThresholdReached,
    bool IsOutlier,
    bool SendFailed,
    DateTime LastShortcutSentUtc,
    DateTime LastDelayBlockedUtc,
    string CoordinateSpace);

internal sealed record MovementPreviewGraphSample(DateTime Utc, double PositionRatio, double RotationRatio);

internal sealed class MovementPreviewViewModel : INotifyPropertyChanged
{
    private string _name = "";
    private string _statusText = "Waiting for tracker data";
    private string _timingText = "Trigger: Ready · Individual cooldown: Disabled · Global activation delay: Disabled";
    private string _timingCompactText = "Ready · I off · G off";
    private string _positionText = "Position: —";
    private string _rotationText = "Rotation: —";
    private string _positionAxisMaxText = "0.300";
    private string _positionAxisThresholdText = "0.150";
    private string _rotationAxisMaxText = "20.0";
    private string _rotationAxisThresholdText = "10.0";
    private Brush _statusBrush = Brushes.Gray;
    private PointCollection _positionGraphPoints = new();
    private PointCollection _rotationGraphPoints = new();
    private readonly List<(DateTime Utc, double PositionRatio, double RotationRatio)> _graphHistory = new();
    private DateTime _lastGraphSampleUtc = DateTime.MinValue;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string TimingText { get => _timingText; set => Set(ref _timingText, value); }
    public string TimingCompactText { get => _timingCompactText; set => Set(ref _timingCompactText, value); }
    public string PositionText { get => _positionText; set => Set(ref _positionText, value); }
    public string RotationText { get => _rotationText; set => Set(ref _rotationText, value); }
    public string PositionAxisMaxText { get => _positionAxisMaxText; private set => Set(ref _positionAxisMaxText, value); }
    public string PositionAxisThresholdText { get => _positionAxisThresholdText; private set => Set(ref _positionAxisThresholdText, value); }
    public string RotationAxisMaxText { get => _rotationAxisMaxText; private set => Set(ref _rotationAxisMaxText, value); }
    public string RotationAxisThresholdText { get => _rotationAxisThresholdText; private set => Set(ref _rotationAxisThresholdText, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    public void SetGraphThresholds(double positionThreshold, double rotationThreshold)
    {
        // The graph plots each channel as a ratio of its own configured threshold,
        // but the two axes are labeled in their real, separate measurement units.
        PositionAxisMaxText = (positionThreshold * 2.0).ToString("0.000", CultureInfo.CurrentCulture);
        PositionAxisThresholdText = positionThreshold.ToString("0.000", CultureInfo.CurrentCulture);
        RotationAxisMaxText = (rotationThreshold * 2.0).ToString("0.0", CultureInfo.CurrentCulture);
        RotationAxisThresholdText = rotationThreshold.ToString("0.0", CultureInfo.CurrentCulture);
    }
    public IReadOnlyList<MovementPreviewGraphSample> GetGraphHistorySnapshot() =>
        _graphHistory.Select(sample => new MovementPreviewGraphSample(sample.Utc, sample.PositionRatio, sample.RotationRatio)).ToArray();

    public PointCollection PositionGraphPoints { get => _positionGraphPoints; private set => Set(ref _positionGraphPoints, value); }
    public PointCollection RotationGraphPoints { get => _rotationGraphPoints; private set => Set(ref _rotationGraphPoints, value); }

    public void RefreshGraph(DateTime now, DateTime sampleUtc, bool hasFreshSample,
        double positionRatio, double rotationRatio)
    {
        // The chart samples the existing detector snapshot at the existing 4 Hz UI refresh,
        // retaining at most ten seconds. It never reads tracker data or runs detector logic.
        if (hasFreshSample && sampleUtc > _lastGraphSampleUtc)
        {
            _graphHistory.Add((sampleUtc, Math.Max(0, positionRatio), Math.Max(0, rotationRatio)));
            _lastGraphSampleUtc = sampleUtc;
        }

        DateTime cutoff = now.AddSeconds(-10);
        _graphHistory.RemoveAll(p => p.Utc < cutoff);
        PositionGraphPoints = BuildGraphPoints(now, usePosition: true);
        RotationGraphPoints = BuildGraphPoints(now, usePosition: false);
    }

    private PointCollection BuildGraphPoints(DateTime now, bool usePosition)
    {
        const double left = 55.0;
        const double right = 248.0;
        const double top = 17.0;
        const double bottom = 107.0;
        List<Point> points = new(_graphHistory.Count + 1);
        DateTime start = now.AddSeconds(-10);
        foreach (var sample in _graphHistory)
        {
            double xFraction = Math.Clamp((sample.Utc - start).TotalSeconds / 10.0, 0.0, 1.0);
            double ratio = Math.Clamp(usePosition ? sample.PositionRatio : sample.RotationRatio, 0.0, 2.0);
            double x = left + (right - left) * xFraction;
            double y = bottom - (bottom - top) * (ratio / 2.0);
            points.Add(new Point(x, y));
        }
        // A single incoming sample should still be visible as a tiny line segment.
        if (points.Count == 1)
            points.Add(new Point(Math.Min(right, points[0].X + 1.5), points[0].Y));
        return new PointCollection(points);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class PresetControlSet
{
    public System.Windows.Controls.TextBox Hotkey { get; set; }
    public System.Windows.Controls.CheckBox Enabled { get; set; }
    public System.Windows.Controls.Button Test { get; set; }
    public System.Windows.Controls.ComboBox Target { get; set; }
    public System.Windows.Controls.ComboBox Sensitivity { get; set; }
    public System.Windows.Controls.StackPanel CustomSensitivityPanel { get; set; }
    public System.Windows.Controls.TextBox CustomPosition { get; set; }
    public System.Windows.Controls.TextBox CustomRotation { get; set; }
    public System.Windows.Controls.TextBox CooldownMs { get; set; }
    public string[] Options { get; set; } = Array.Empty<string>();
}
