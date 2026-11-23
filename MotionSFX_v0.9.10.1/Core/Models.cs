using System.Text.Json.Serialization;

namespace MotionSFX.Core;

internal sealed class AppSettings
{
    [JsonPropertyName("bindAddress")] public string BindAddress { get; set; } = "127.0.0.1";
    [JsonPropertyName("udpPort")] public int UdpPort { get; set; } = 39539;
    [JsonPropertyName("staleAfterMs")] public int StaleAfterMs { get; set; } = 1500;
    [JsonPropertyName("lostAfterMs")] public int LostAfterMs { get; set; } = 5000;
    [JsonPropertyName("keybindDelayMs")] public int KeybindDelayMs { get; set; } = 1000;
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
    [JsonPropertyName("targetRole")] public string TargetRole { get; set; } = "";
}

internal sealed class TransformData
{
    public double X, Y, Z, QX, QY, QZ, QW;
    public string SourceAddress = "";
    public string SenderEndpoint = "";
    public DateTime ReceivedUtc;
}

internal sealed class DeviceData
{
    public string Id = "";
    public string VmcLabel = "";
    public readonly HashSet<string> Kinds = new(StringComparer.Ordinal);
    public DateTime FirstSeenUtc;
    public DateTime LastSeenUtc;
    public long PacketCount;
    public string SenderEndpoint = "";
    public TransformData Global;
    public TransformData Local;
}

internal sealed class BoneData
{
    public string Id = "";
    public string BoneName = "";
    public DateTime FirstSeenUtc;
    public DateTime LastSeenUtc;
    public long PacketCount;
    public string SenderEndpoint = "";
    public double X, Y, Z, QX, QY, QZ, QW = 1.0;
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
    public double X, Y, Z, QX, QY, QZ, QW = 1.0;
    public DateTime AnchorUtc;
    public DateTime LastSampleUtc;
    public string Space = "";
}

internal sealed class PresetControlSet
{
    public System.Windows.Controls.TextBox Hotkey;
    public System.Windows.Controls.CheckBox Enabled;
    public System.Windows.Controls.Button Test;
    public System.Windows.Controls.ComboBox Target;
    public System.Windows.Controls.ComboBox Sensitivity;
    public string[] Options = Array.Empty<string>();
}
