using MotionSFX.Core;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MotionSFX;

public partial class MainWindow : Window
{
    private const string VersionText = "0.9.10.26";

    // DWM caption colors use COLORREF byte order (0x00BBGGRR).
    // These colors match the application's existing dark-purple controls.
    private const uint DwmwaCaptionColor = 35;
    private const uint DwmwaTextColor = 36;
    private const uint CaptionPurple = 0x004D2630; // #30264D
    private const uint CaptionTextWhite = 0x00FFFFFF;
    private const string BindAddress = "127.0.0.1";
    private const int DefaultUdpPort = 39539;
    private const int MaxMotionBatch = 150;
    private const double MinCustomPositionThreshold = 0.001;
    private const double MaxCustomPositionThreshold = 2.0;
    private const double MinCustomRotationThreshold = 0.1;
    private const double MaxCustomRotationThreshold = 180.0;

    private static readonly string[] KnownRoles =
    {
        "Unassigned", "LeftFoot", "RightFoot", "Head", "LeftHand", "RightHand",
        "Hip", "Chest", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg", "Other"
    };

    private static readonly Dictionary<string, (string Hotkey, string Enabled, string Test, string Target, string Sensitivity, string CustomPanel, string CustomPosition, string CustomRotation, string[] Options)> PresetUi = new(StringComparer.Ordinal)
    {
        ["right-foot"] = ("PresetRightFootHotkey", "PresetRightFootEnabled", "PresetRightFootTest", "PresetRightFootRole", "PresetRightFootSensitivity", "PresetRightFootCustomPanel", "PresetRightFootCustomPosition", "PresetRightFootCustomRotation", new[] { "RightFoot", "RightLowerLeg", "RightUpperLeg" }),
        ["left-foot"]  = ("PresetLeftFootHotkey", "PresetLeftFootEnabled", "PresetLeftFootTest", "PresetLeftFootRole", "PresetLeftFootSensitivity", "PresetLeftFootCustomPanel", "PresetLeftFootCustomPosition", "PresetLeftFootCustomRotation", new[] { "LeftFoot", "LeftLowerLeg", "LeftUpperLeg" }),
        ["right-arm"]  = ("PresetRightArmHotkey", "PresetRightArmEnabled", "PresetRightArmTest", "PresetRightArmRole", "PresetRightArmSensitivity", "PresetRightArmCustomPanel", "PresetRightArmCustomPosition", "PresetRightArmCustomRotation", new[] { "RightHand" }),
        ["left-arm"]   = ("PresetLeftArmHotkey", "PresetLeftArmEnabled", "PresetLeftArmTest", "PresetLeftArmRole", "PresetLeftArmSensitivity", "PresetLeftArmCustomPanel", "PresetLeftArmCustomPosition", "PresetLeftArmCustomRotation", new[] { "LeftHand" }),
        ["head"]       = ("PresetHeadHotkey", "PresetHeadEnabled", "PresetHeadTest", "PresetHeadRole", "PresetHeadSensitivity", "PresetHeadCustomPanel", "PresetHeadCustomPosition", "PresetHeadCustomRotation", new[] { "Head" }),
        ["chest"]      = ("PresetChestHotkey", "PresetChestEnabled", "PresetChestTest", "PresetChestRole", "PresetChestSensitivity", "PresetChestCustomPanel", "PresetChestCustomPosition", "PresetChestCustomRotation", new[] { "Chest" }),
        ["hip"]        = ("PresetHipHotkey", "PresetHipEnabled", "PresetHipTest", "PresetHipRole", "PresetHipSensitivity", "PresetHipCustomPanel", "PresetHipCustomPosition", "PresetHipCustomRotation", new[] { "Hip" })
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _projectRoot = AppContext.BaseDirectory;
    private readonly string _userConfigRoot = Path.Combine(AppLog.DataRoot, "config");
    private readonly string _settingsPath;
    private readonly string _presetsPath;
    private readonly string _rolesPath;
    private readonly Dictionary<string, DeviceData> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoneData> _bones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _roleStore = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MovementAnchor> _movementStates = new(StringComparer.Ordinal);
    // Separate anchors are used only to visualize disabled/failed presets. The actual
    // detector anchors remain untouched while a preset is disabled or send-failed.
    private readonly Dictionary<string, MovementAnchor> _movementPreviewAnchors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _movementSendFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _missingReferenceWarnings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PresetControlSet> _presetControls = new(StringComparer.Ordinal);
    private readonly object _movementPreviewLock = new();
    private readonly Dictionary<string, MovementPreviewSnapshot> _movementPreviewSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MovementPreviewViewModel> _movementPreviewViewModels = new(StringComparer.Ordinal);

    private AppSettings _settings = new();
    private List<MovementPreset> _presets = new();
    private MotionSfxVmcReceiver _receiver;
    private SteamVrDashboardService _steamVrDashboard;
    private DispatcherTimer _refreshTimer;
    private TextBox _captureBox;
    private string _captureOriginal = "";
    private DateTime _startedUtc = DateTime.UtcNow;
    private DateTime _lastStatusRefreshUtc = DateTime.MinValue;
    private DateTime _lastGridRefreshUtc = DateTime.MinValue;
    private DateTime _lastLatestDrainUtc = DateTime.MinValue;
    private DateTime _lastProcessCheckUtc = DateTime.MinValue;
    private DateTime _lastSummaryUtc = DateTime.UtcNow;
    private DateTime _lastNoTrafficNoticeUtc = DateTime.MinValue;
    private DateTime _lastBacklogNoticeUtc = DateTime.MinValue;
    private DateTime _lastKeybindActivationUtc = DateTime.MinValue;
    private long _lastSummaryPacketCount;
    private long _lastSummaryTrackerCount;
    private long _lastSummaryMalformedCount;
    private long _lastMotionQueueDroppedCount;
    private long _motionBacklogSkipped;
    private long _packetCount;
    private long _trackerUpdateCount;
    private long _malformedCount;
    private int _staleAfterMs = 1500;
    private int _lostAfterMs = 5000;
    private int _keybindDelayMs = 1000;
    private bool _receiverRunning;
    private bool _isRefreshingTrackerGrid;
    private string _roleEditorTrackerId;
    private bool _receiverErrorLogged;
    private bool _closing;
    private string _udpError = "";
    private bool _steamVrDetected;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNativeCaptionTheme();
        _settingsPath = Path.Combine(_userConfigRoot, "settings.json");
        _presetsPath = Path.Combine(_userConfigRoot, "presets.json");
        _rolesPath = Path.Combine(_userConfigRoot, "tracker-roles.json");

        try
        {
            Directory.CreateDirectory(AppLog.DataRoot);
            AppLog.Write("INFO", $"Starting MotionSFX Alpha {VersionText} native C# desktop application.");
            AppLog.Write("INFO", "Runtime: .NET " + Environment.Version + "; OS: " + Environment.OSVersion.VersionString);
            AppLog.Write("INFO", "Architecture: " + (Environment.Is64BitProcess ? "x64" : "x86") + "; UI: native Windows Presentation Foundation.");
            AppLog.Write("INFO", "No PowerShell process, browser, HTTP listener, or local web server is used. SteamVR integration uses the native OpenVR API through C# bindings.");

            if (!MotionSfxVmcReceiver.RunSelfTest())
                throw new InvalidOperationException("The built-in VMC/OSC receiver self-test failed.");
            AppLog.Write("INFO", "Native C# VMC/OSC receiver self-test passed.");

            LoadConfiguration();
            InitializeWindowAssets();
            AppLog.Attach(LogView);
            InitializePresetControls();
            InitializeMovementPreview();
            WireUiEvents();
            _steamVrDashboard = new SteamVrDashboardService(_projectRoot, HandleDashboardAction);
            StartApplicationTimer();
            RefreshInterface(throttle: false);
            AppLog.Write("INFO", $"Application ready. Persistent user settings: {_userConfigRoot}");
        }
        catch (Exception ex)
        {
            AppLog.Write("FATAL", "Application initialization failed: " + ex);
            throw new InvalidOperationException("MotionSFX could not finish starting. See the application log for details.", ex);
        }
    }

    private void ApplyNativeCaptionTheme()
    {
        // Keep the normal Windows title bar and caption buttons, but recolor the frame.
        // DWMWA_CAPTION_COLOR/TEXT_COLOR/BORDER_COLOR are supported on Windows 11 22000+.
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            uint caption = CaptionPurple;
            uint text = CaptionTextWhite;
            int captionResult = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(uint));
            int textResult = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(uint));

            if (captionResult >= 0 && textResult >= 0)
            {
                AppLog.Write("INFO", "Applied purple Windows title bar with white caption text.");
            }
            else
            {
                AppLog.Write("WARN", "Windows did not accept one or more custom title-bar colors. " +
                    $"Caption=0x{unchecked((uint)captionResult):X8}; Text=0x{unchecked((uint)textResult):X8}; " +
                    "Custom caption colors require Windows 11 build 22000 or newer.");
            }
        }
        catch (Exception ex)
        {
            // Title-bar styling is optional and must never prevent MotionSFX from starting.
            AppLog.Write("WARN", "Could not apply the custom Windows title-bar colors: " + ex.Message);
        }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    private string DefaultConfigPath(string fileName) => Path.Combine(_projectRoot, "Config", fileName);

    private void LoadConfiguration()
    {
        Directory.CreateDirectory(_userConfigRoot);
        foreach (string name in new[] { "settings.json", "presets.json", "tracker-roles.json" })
        {
            string target = Path.Combine(_userConfigRoot, name);
            string source = DefaultConfigPath(name);
            if (!File.Exists(target))
            {
                if (!File.Exists(source)) throw new FileNotFoundException("Required default configuration is missing.", source);
                File.Copy(source, target);
            }
        }

        try
        {
            _settings = ReadJson<AppSettings>(_settingsPath) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "Saved application settings could not be read; restoring defaults. " + ex.Message);
            _settings = ReadJson<AppSettings>(DefaultConfigPath("settings.json")) ?? new AppSettings();
            WriteJsonAtomic(_settingsPath, _settings);
        }
        if (_settings.ConfigVersion != 1)
        {
            _settings = ReadJson<AppSettings>(DefaultConfigPath("settings.json")) ?? new AppSettings();
            WriteJsonAtomic(_settingsPath, _settings);
            AppLog.Write("WARN", "Unrecognized settings schema; restored the current default settings.");
        }
        if (_settings.BindAddress != BindAddress)
        {
            AppLog.Write("WARN", "Only loopback address 127.0.0.1 is supported; ignoring the saved bind address.");
            _settings.BindAddress = BindAddress;
        }
        if (_settings.UdpPort is < 1 or > 65535) _settings.UdpPort = DefaultUdpPort;
        if (_settings.StaleAfterMs is < 250 or > 10000) _settings.StaleAfterMs = 1500;
        if (_settings.LostAfterMs <= _settings.StaleAfterMs || _settings.LostAfterMs > 30000)
            _settings.LostAfterMs = Math.Max(5000, _settings.StaleAfterMs + 500);
        if (_settings.KeybindDelayMs is < 0 or > 10000) _settings.KeybindDelayMs = 1000;
        _staleAfterMs = _settings.StaleAfterMs;
        _lostAfterMs = _settings.LostAfterMs;
        _keybindDelayMs = _settings.KeybindDelayMs;

        List<MovementPreset> defaults = ReadJson<List<MovementPreset>>(DefaultConfigPath("presets.json")) ?? new();
        if (defaults.Count != 7) throw new InvalidDataException("The built-in preset file must contain exactly seven entries.");
        List<MovementPreset> saved = new();
        try
        {
            saved = ReadJson<List<MovementPreset>>(_presetsPath) ?? new();
            AppLog.Write("INFO", $"Loaded saved movement preset file with {saved.Count} entries.");
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "Saved movement presets could not be read; using defaults. " + ex.Message);
        }

        _presets.Clear();
        foreach (MovementPreset defaultPreset in defaults)
        {
            MovementPreset previous = saved.FirstOrDefault(p => string.Equals(p.Id, defaultPreset.Id, StringComparison.Ordinal));
            MovementPreset current = previous == null ? ClonePreset(defaultPreset) : new MovementPreset
            {
                Id = defaultPreset.Id,
                Name = defaultPreset.Name,
                Description = defaultPreset.Description,
                Hotkey = previous.Hotkey ?? defaultPreset.Hotkey,
                Enabled = previous.Enabled,
                Sensitivity = previous.Sensitivity ?? "Normal",
                CustomPositionThreshold = previous.CustomPositionThreshold,
                CustomRotationThreshold = previous.CustomRotationThreshold,
                TargetRole = string.IsNullOrWhiteSpace(previous.TargetRole) ? defaultPreset.TargetRole : previous.TargetRole
            };
            if (current.Sensitivity == "Standard") current.Sensitivity = "Normal";
            if (current.Sensitivity is not ("Low" or "Normal" or "High" or "Custom")) current.Sensitivity = "Normal";
            if (!double.IsFinite(current.CustomPositionThreshold) || current.CustomPositionThreshold < MinCustomPositionThreshold || current.CustomPositionThreshold > MaxCustomPositionThreshold)
                current.CustomPositionThreshold = 0.15;
            if (!double.IsFinite(current.CustomRotationThreshold) || current.CustomRotationThreshold < MinCustomRotationThreshold || current.CustomRotationThreshold > MaxCustomRotationThreshold)
                current.CustomRotationThreshold = 10.0;
            _presets.Add(current);
        }
        string[] expectedIds = { "right-foot", "left-foot", "right-arm", "left-arm", "head", "chest", "hip" };
        if (!_presets.Select(p => p.Id).SequenceEqual(expectedIds, StringComparer.Ordinal))
            throw new InvalidDataException("Movement preset IDs are missing or out of order.");
        WriteJsonAtomic(_presetsPath, _presets);

        try
        {
            Dictionary<string, string> loaded = ReadJson<Dictionary<string, string>>(_rolesPath) ?? new(StringComparer.Ordinal);
            foreach (var assignment in loaded)
            {
                if (KnownRoles.Contains(assignment.Value, StringComparer.Ordinal))
                    _roleStore[assignment.Key] = assignment.Value;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("WARN", "Saved tracker roles could not be read; starting with no assignments. " + ex.Message);
        }
        NormalizeRoleStoreUniqueness();
        AppLog.Write("INFO", $"Configuration loaded. VMC receiver={BindAddress}:{_settings.UdpPort}; presets={_presets.Count}; saved tracker roles={_roleStore.Count}.");
    }

    private static MovementPreset ClonePreset(MovementPreset p) => new()
    {
        Id = p.Id, Name = p.Name, Description = p.Description, Hotkey = p.Hotkey,
        Enabled = p.Enabled, Sensitivity = p.Sensitivity,
        CustomPositionThreshold = p.CustomPositionThreshold,
        CustomRotationThreshold = p.CustomRotationThreshold,
        TargetRole = p.TargetRole
    };

    private static T ReadJson<T>(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string temp = path + ".tmp";
        string backup = path + ".bak";
        string json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
        if (File.Exists(path))
        {
            try { File.Replace(temp, path, backup, true); }
            catch
            {
                try { File.Copy(path, backup, true); } catch { }
                File.Move(temp, path, true);
            }
        }
        else File.Move(temp, path);
    }

    private void SaveRoleStore() => WriteJsonAtomic(_rolesPath, _roleStore);
    private void SavePresetStore() => WriteJsonAtomic(_presetsPath, _presets);

    private void SaveApplicationSettings()
    {
        _settings.KeybindDelayMs = _keybindDelayMs;
        WriteJsonAtomic(_settingsPath, _settings);
    }

    private void NormalizeRoleStoreUniqueness()
    {
        RoleOwnership ownership = GetRoleOwnership();
        if (ownership.AmbiguousRoles.Count == 0) return;
        foreach (string role in ownership.AmbiguousRoles.ToArray())
        {
            foreach (string identity in _roleStore.Where(kv => kv.Value == role).Select(kv => kv.Key).ToArray())
                _roleStore.Remove(identity);
            AppLog.Write("WARN", $"Multiple raw VMC identities were assigned body role {role}; cleared conflicting assignments. Assign that role to exactly one identity on Trackers.");
        }
        SaveRoleStore();
    }

    private RoleOwnership GetRoleOwnership()
    {
        RoleOwnership result = new();
        foreach (var assignment in _roleStore)
        {
            string role = assignment.Value;
            if (!KnownRoles.Contains(role, StringComparer.Ordinal) || role == "Unassigned" || result.AmbiguousRoles.Contains(role))
                continue;
            if (result.Owners.ContainsKey(role))
            {
                result.Owners.Remove(role);
                result.AmbiguousRoles.Add(role);
            }
            else result.Owners[role] = assignment.Key;
        }
        return result;
    }

    private void MigrateLegacyRoleAssignment(string newId, string vmcLabel)
    {
        if (_roleStore.ContainsKey(newId)) return;
        string[] oldKeys = { "HMD|" + vmcLabel, "CON|" + vmcLabel, "TRA|" + vmcLabel };
        var entries = oldKeys.Where(_roleStore.ContainsKey).Select(k => (Key: k, Role: _roleStore[k])).ToArray();
        if (entries.Length == 0) return;
        string[] roles = entries.Select(e => e.Role).Distinct(StringComparer.Ordinal).ToArray();
        if (roles.Length != 1)
        {
            AppLog.Write("WARN", $"Conflicting legacy roles exist for raw VMC identity=\"{vmcLabel}\". Left it unassigned instead of guessing.");
            return;
        }
        string role = roles[0];
        bool occupied = _roleStore.Any(kv => !oldKeys.Contains(kv.Key, StringComparer.Ordinal) && kv.Value == role);
        foreach (var entry in entries) _roleStore.Remove(entry.Key);
        if (occupied)
        {
            SaveRoleStore();
            AppLog.Write("WARN", $"Did not migrate legacy body role {role} onto raw VMC identity=\"{vmcLabel}\" because that role is already assigned to another identity.");
            return;
        }
        _roleStore[newId] = role;
        SaveRoleStore();
        AppLog.Write("INFO", $"Merged a saved legacy body-role assignment for raw VMC identity=\"{vmcLabel}\".");
    }

    private void InitializeWindowAssets()
    {
        string logoPath = Path.Combine(_projectRoot, "Assets", "MotionSFX-logo.png");
        string iconPath = Path.Combine(_projectRoot, "Assets", "MotionSFX-icon.ico");
        if (File.Exists(logoPath))
        {
            BitmapImage logo = new();
            logo.BeginInit();
            logo.UriSource = new Uri(logoPath, UriKind.Absolute);
            logo.CacheOption = BitmapCacheOption.OnLoad;
            logo.EndInit();
            logo.Freeze();
            AppLogo.Source = logo;
        }
        if (File.Exists(iconPath)) Icon = BitmapFrame.Create(new Uri(iconPath, UriKind.Absolute));
        KeybindDelayInput.Text = _keybindDelayMs.ToString(CultureInfo.InvariantCulture);
        AppLog.Write("INFO", "MotionSFX application and taskbar icon initialized in the native process.");
    }

    private void InitializePresetControls()
    {
        _presetControls.Clear();
        foreach (MovementPreset preset in _presets)
        {
            if (!PresetUi.TryGetValue(preset.Id, out var names))
                throw new InvalidDataException("Unknown preset ID in configuration: " + preset.Id);

            TextBox hotkey = FindName(names.Hotkey) as TextBox;
            CheckBox enabled = FindName(names.Enabled) as CheckBox;
            Button test = FindName(names.Test) as Button;
            ComboBox target = FindName(names.Target) as ComboBox;
            ComboBox sensitivity = FindName(names.Sensitivity) as ComboBox;
            StackPanel customPanel = FindName(names.CustomPanel) as StackPanel;
            TextBox customPosition = FindName(names.CustomPosition) as TextBox;
            TextBox customRotation = FindName(names.CustomRotation) as TextBox;
            if (hotkey == null || enabled == null || test == null || target == null || sensitivity == null ||
                customPanel == null || customPosition == null || customRotation == null)
                throw new InvalidDataException("The native UI is missing one or more preset controls for " + preset.Id);

            hotkey.Text = preset.Hotkey;
            hotkey.Tag = preset.Id;
            hotkey.GotKeyboardFocus += (_, _) => BeginKeybindCapture(hotkey);
            hotkey.LostKeyboardFocus += (_, _) =>
            {
                if (_captureBox == hotkey)
                {
                    hotkey.Text = _captureOriginal;
                    _captureBox = null;
                }
            };
            hotkey.PreviewMouseLeftButtonDown += (_, args) =>
            {
                hotkey.Focus();
                BeginKeybindCapture(hotkey);
                args.Handled = true;
            };
            hotkey.PreviewKeyDown += HandleKeybindPreviewKeyDown;

            enabled.IsChecked = preset.Enabled;
            target.Items.Clear();
            foreach (string role in names.Options) target.Items.Add(role);
            target.SelectedItem = names.Options.Contains(preset.TargetRole, StringComparer.Ordinal)
                ? preset.TargetRole : names.Options[0];
            if (!names.Options.Contains(preset.TargetRole, StringComparer.Ordinal))
                AppLog.Write("WARN", "Preset target role was invalid and reset to its default: " + preset.Id);

            sensitivity.Items.Clear();
            sensitivity.SelectedValuePath = "Tag";
            AddSensitivity(sensitivity, "High", "High (0.10 units / 6°)");
            AddSensitivity(sensitivity, "Normal", "Normal (0.15 units / 10°)");
            AddSensitivity(sensitivity, "Low", "Low (0.20 units / 17°)");
            AddSensitivity(sensitivity, "Custom", "Custom");
            customPosition.Text = preset.CustomPositionThreshold.ToString("0.###", CultureInfo.InvariantCulture);
            customRotation.Text = preset.CustomRotationThreshold.ToString("0.###", CultureInfo.InvariantCulture);
            sensitivity.SelectedValue = preset.Sensitivity;
            if (sensitivity.SelectedItem == null) sensitivity.SelectedValue = "Normal";
            customPanel.Visibility = sensitivity.SelectedValue as string == "Custom" ? Visibility.Visible : Visibility.Collapsed;
            sensitivity.SelectionChanged += (_, _) =>
            {
                customPanel.Visibility = sensitivity.SelectedValue as string == "Custom" ? Visibility.Visible : Visibility.Collapsed;
            };

            test.Tag = preset.Id;
            test.Click += (_, _) =>
            {
                try
                {
                    string shortcut = _presetControls[preset.Id].Hotkey.Text;
                    KeyboardInput.Send(shortcut);
                    AppLog.Write("INFO", $"Manual movement keybind test sent: preset={preset.Name}; keybind={KeyboardInput.Validate(shortcut)}");
                }
                catch (Exception ex)
                {
                    AppLog.Write("ERROR", "Preset test failed: " + ex.Message);
                    MessageBox.Show(ex.Message, "MotionSFX - Keybind test", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            _presetControls[preset.Id] = new PresetControlSet
            {
                Hotkey = hotkey, Enabled = enabled, Test = test,
                Target = target, Sensitivity = sensitivity, Options = names.Options,
                CustomSensitivityPanel = customPanel, CustomPosition = customPosition, CustomRotation = customRotation
            };
        }

        if (PresetTabs == null || PresetTabs.Items.Count != 7)
            throw new InvalidDataException("Movement Presets must contain exactly seven tabs.");
        PresetTabs.SelectedIndex = 0;
        AppLog.Write("INFO", "Movement preset UI initialized: seven independent C#-driven preset pages.");
    }

    private static void AddSensitivity(ComboBox combo, string level, string label)
    {
        combo.Items.Add(new ComboBoxItem { Content = label, Tag = level });
    }

    private void InitializeMovementPreview()
    {
        _movementPreviewViewModels.Clear();
        Dictionary<string, (ContentControl PresetHost, ContentControl LiveHost)> previewHosts = new(StringComparer.Ordinal)
        {
            ["right-foot"] = (PresetRightFootPreview, LiveRightFootPreview),
            ["left-foot"] = (PresetLeftFootPreview, LiveLeftFootPreview),
            ["right-arm"] = (PresetRightArmPreview, LiveRightArmPreview),
            ["left-arm"] = (PresetLeftArmPreview, LiveLeftArmPreview),
            ["head"] = (PresetHeadPreview, LiveHeadPreview),
            ["chest"] = (PresetChestPreview, LiveChestPreview),
            ["hip"] = (PresetHipPreview, LiveHipPreview)
        };

        foreach (MovementPreset preset in _presets)
        {
            if (!previewHosts.TryGetValue(preset.Id, out var hosts) || hosts.PresetHost == null || hosts.LiveHost == null)
                throw new InvalidDataException($"The live preview host for preset '{preset.Id}' is missing.");
            MovementPreviewViewModel viewModel = new() { Name = preset.Name };
            _movementPreviewViewModels[preset.Id] = viewModel;
            hosts.PresetHost.Content = viewModel;
            hosts.LiveHost.Content = viewModel;
        }
        AppLog.Write("INFO", "Live movement graphs initialized on the seven preset pages, desktop Live View tab, and SteamVR dashboard using shared view-model histories.");
    }

    private void StoreMovementPreview(MovementPreset preset, bool hasMeasurement,
        double position, double rotation, DateTime sampleUtc, string note,
        bool thresholdReached = false, bool isOutlier = false, bool sendFailed = false,
        bool shortcutSent = false, bool delayBlocked = false)
    {
        (double Position, double Rotation) threshold = GetMovementThresholds(
            preset.Sensitivity, preset.CustomPositionThreshold, preset.CustomRotationThreshold);
        lock (_movementPreviewLock)
        {
            MovementPreviewSnapshot previous = _movementPreviewSnapshots.TryGetValue(preset.Id, out MovementPreviewSnapshot existing)
                ? existing : null;
            DateTime lastSent = previous?.LastShortcutSentUtc ?? DateTime.MinValue;
            DateTime lastDelayBlocked = previous?.LastDelayBlockedUtc ?? DateTime.MinValue;
            if (shortcutSent) lastSent = DateTime.UtcNow;
            if (delayBlocked) lastDelayBlocked = DateTime.UtcNow;
            _movementPreviewSnapshots[preset.Id] = new MovementPreviewSnapshot(
                hasMeasurement, position, rotation, threshold.Position, threshold.Rotation,
                sampleUtc, note ?? "", thresholdReached, isOutlier, sendFailed, lastSent, lastDelayBlocked);
        }
    }

    private void StoreMovementPreviewIssue(MovementPreset preset, string note)
    {
        (double Position, double Rotation) threshold = GetMovementThresholds(
            preset.Sensitivity, preset.CustomPositionThreshold, preset.CustomRotationThreshold);
        lock (_movementPreviewLock)
        {
            MovementPreviewSnapshot previous = _movementPreviewSnapshots.TryGetValue(preset.Id, out MovementPreviewSnapshot existing)
                ? existing : null;
            DateTime lastSent = previous?.LastShortcutSentUtc ?? DateTime.MinValue;
            DateTime lastDelayBlocked = previous?.LastDelayBlockedUtc ?? DateTime.MinValue;
            _movementPreviewSnapshots[preset.Id] = new MovementPreviewSnapshot(
                false, 0, 0, threshold.Position, threshold.Rotation, DateTime.UtcNow,
                note ?? "", false, false, _movementSendFailures.Contains(preset.Id), lastSent, lastDelayBlocked);
        }
    }

    private MovementPreviewSnapshot GetMovementPreviewSnapshot(string presetId)
    {
        lock (_movementPreviewLock)
            return _movementPreviewSnapshots.TryGetValue(presetId, out MovementPreviewSnapshot snapshot) ? snapshot : null;
    }

    private void ClearMovementPreviewSnapshots()
    {
        lock (_movementPreviewLock) _movementPreviewSnapshots.Clear();
    }

    private string GetMovementPreviewSetupIssue(MovementPreset preset, RoleOwnership ownership)
    {
        string targetRole = preset.TargetRole;
        if (ownership.AmbiguousRoles.Contains(targetRole)) return "Target role ambiguous · check Trackers";
        if (!ownership.Owners.ContainsKey(targetRole)) return "Target tracker role not assigned";
        string referenceRole = GetMovementReferenceRole(targetRole);
        if (!string.IsNullOrEmpty(referenceRole))
        {
            if (ownership.AmbiguousRoles.Contains(referenceRole)) return "Reference role ambiguous · check Trackers";
            if (!ownership.Owners.ContainsKey(referenceRole)) return $"Reference role {referenceRole} not assigned";
        }
        return "No fresh tracker data";
    }

    private void RefreshMovementPreviewUi(DateTime now)
    {
        RoleOwnership ownership = GetRoleOwnership();
        double delayRemaining = _lastKeybindActivationUtc == DateTime.MinValue || _keybindDelayMs <= 0
            ? 0 : Math.Max(0, _keybindDelayMs - (now - _lastKeybindActivationUtc).TotalMilliseconds);

        foreach (MovementPreset preset in _presets)
        {
            if (!_movementPreviewViewModels.TryGetValue(preset.Id, out MovementPreviewViewModel viewModel)) continue;
            MovementPreviewSnapshot snapshot = GetMovementPreviewSnapshot(preset.Id);
            string status;
            Brush statusBrush;
            bool fresh = snapshot != null && snapshot.HasMeasurement &&
                (now - snapshot.LastSampleUtc).TotalMilliseconds <= _staleAfterMs;

            (double Position, double Rotation) configuredThresholds = GetMovementThresholds(
                preset.Sensitivity, preset.CustomPositionThreshold, preset.CustomRotationThreshold);
            double positionLimit = snapshot != null && snapshot.PositionThreshold > 0
                ? snapshot.PositionThreshold : configuredThresholds.Position;
            double rotationLimit = snapshot != null && snapshot.RotationThreshold > 0
                ? snapshot.RotationThreshold : configuredThresholds.Rotation;
            viewModel.SetGraphThresholds(positionLimit, rotationLimit);

            double positionRatio = fresh && snapshot.PositionThreshold > 0
                ? snapshot.Position / snapshot.PositionThreshold : 0;
            double rotationRatio = fresh && snapshot.RotationThreshold > 0
                ? snapshot.Rotation / snapshot.RotationThreshold : 0;
            viewModel.RefreshGraph(now, fresh ? snapshot.LastSampleUtc : DateTime.MinValue,
                fresh, positionRatio, rotationRatio);

            if (!_receiverRunning)
            {
                status = string.IsNullOrWhiteSpace(_udpError)
                    ? (preset.Enabled ? "Disarmed · VMC reception stopped" : "Disarmed · preset disabled")
                    : "Receiver error · disarmed";
                statusBrush = Brushes.Gray;
            }
            else if (snapshot == null)
            {
                string issue = GetMovementPreviewSetupIssue(preset, ownership);
                status = preset.Enabled ? issue : "Disabled · " + issue;
                statusBrush = issue.Contains("not assigned", StringComparison.OrdinalIgnoreCase) ||
                              issue.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
                    ? Brushes.Orange : Brushes.Gray;
            }
            else if (!snapshot.HasMeasurement)
            {
                string issue = snapshot.Note.Length > 0 && (now - snapshot.LastSampleUtc).TotalSeconds <= 3.0
                    ? snapshot.Note : GetMovementPreviewSetupIssue(preset, ownership);
                status = preset.Enabled ? issue : "Disabled · " + issue;
                statusBrush = issue.Contains("not assigned", StringComparison.OrdinalIgnoreCase) ||
                              issue.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
                    ? Brushes.Orange : Brushes.Gray;
            }
            else if (!fresh)
            {
                string issue = GetMovementPreviewSetupIssue(preset, ownership);
                status = preset.Enabled ? issue : "Disabled · " + issue;
                statusBrush = issue.Contains("not assigned", StringComparison.OrdinalIgnoreCase) ||
                              issue.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
                    ? Brushes.Orange : Brushes.Gray;
            }
            else if (snapshot.IsOutlier)
            {
                status = "Large transform jump · ignored / baseline reset";
                statusBrush = Brushes.Orange;
            }
            else if (_movementSendFailures.Contains(preset.Id) || snapshot.SendFailed)
            {
                status = "Shortcut sending failed · disabled this session";
                statusBrush = Brushes.IndianRed;
            }
            else if (snapshot.LastShortcutSentUtc != DateTime.MinValue &&
                     (now - snapshot.LastShortcutSentUtc).TotalMilliseconds <= 1200)
            {
                double elapsed = Math.Max(0, (now - snapshot.LastShortcutSentUtc).TotalSeconds);
                status = $"Shortcut sent · {elapsed:0.0}s ago";
                statusBrush = Brushes.LimeGreen;
            }
            else if (snapshot.LastDelayBlockedUtc != DateTime.MinValue &&
                     (now - snapshot.LastDelayBlockedUtc).TotalMilliseconds <= 1200)
            {
                status = delayRemaining > 0
                    ? $"Threshold reached · blocked by global delay ({delayRemaining:0} ms)"
                    : "Threshold was blocked by the global delay";
                statusBrush = Brushes.Orange;
            }
            else if (snapshot.ThresholdReached && !preset.Enabled)
            {
                status = "Threshold reached · preset disabled";
                statusBrush = Brushes.MediumPurple;
            }
            else if (!preset.Enabled)
            {
                status = "Disabled · preview only";
                statusBrush = Brushes.Gray;
            }
            else if (snapshot.Note.StartsWith("Establishing", StringComparison.Ordinal) ||
                     snapshot.Note.StartsWith("Re-establishing", StringComparison.Ordinal))
            {
                status = snapshot.Note;
                statusBrush = Brushes.Gray;
            }
            else if (snapshot.ThresholdReached)
            {
                status = "Threshold reached · trigger condition met";
                statusBrush = Brushes.Gold;
            }
            else if (delayRemaining > 0)
            {
                double intensity = Math.Max(snapshot.Position / snapshot.PositionThreshold,
                    snapshot.Rotation / snapshot.RotationThreshold);
                status = intensity >= 0.75
                    ? $"Approaching threshold · global delay {delayRemaining:0} ms"
                    : $"Below threshold · global delay {delayRemaining:0} ms";
                statusBrush = Brushes.Orange;
            }
            else
            {
                double intensity = Math.Max(snapshot.Position / snapshot.PositionThreshold,
                    snapshot.Rotation / snapshot.RotationThreshold);
                status = intensity >= 0.75 ? "Approaching threshold" : "Ready · below threshold";
                statusBrush = intensity >= 0.75 ? Brushes.Gold : Brushes.MediumPurple;
            }

            viewModel.StatusText = status;
            viewModel.StatusBrush = statusBrush;
            if (fresh)
            {
                viewModel.PositionText = $"Position  {snapshot.Position:0.000} / {snapshot.PositionThreshold:0.000} units";
                viewModel.RotationText = $"Rotation  {snapshot.Rotation:0.0} / {snapshot.RotationThreshold:0.0}°";
            }
            else
            {
                viewModel.PositionText = $"Position  — / {positionLimit:0.000} units";
                viewModel.RotationText = $"Rotation  — / {rotationLimit:0.0}°";
            }
        }
    }

    private void WireUiEvents()
    {
        StartReceivingButton.Click += (_, _) =>
        {
            try { StartReceiver(); RefreshInterface(false); }
            catch (Exception ex)
            {
                _udpError = ex.Message;
                AppLog.Write("ERROR", "Could not start VMC reception: " + ex.Message);
                RefreshInterface(false);
                MessageBox.Show(ex.Message, "MotionSFX - Start reception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        StopReceivingButton.Click += (_, _) =>
        {
            try { StopReceiver("Stopped by user"); RefreshInterface(false); }
            catch (Exception ex) { AppLog.Write("ERROR", "Could not stop VMC reception cleanly: " + ex.Message); }
        };
        TrackerGrid.SelectionChanged += (_, _) =>
        {
            // ItemsSource is rebuilt during live refreshes. Ignore the transient
            // deselection/reselection events so they cannot reset the role editor.
            if (_isRefreshingTrackerGrid) return;
            _roleEditorTrackerId = (TrackerGrid.SelectedItem as TrackerGridRow)?.id;
            SetSelectedTrackerRoleOptions();
        };
        AssignRoleButton.Click += (_, _) => AssignSelectedRole();
        ResetBodyRolesButton.Click += (_, _) => ResetTrackerRoles();
        ClearDevicesButton.Click += (_, _) => ClearSessionData();
        SavePresetsButton.Click += (_, _) => SavePresetsFromUi();
        Closing += (_, _) =>
        {
            _closing = true;
            AppLog.Write("INFO", "MotionSFX window close requested.");
            _refreshTimer?.Stop();
            try { _steamVrDashboard?.Dispose(); } catch (Exception ex) { AppLog.Write("WARN", "SteamVR dashboard shutdown was not clean: " + ex.Message); }
            StopReceiver("Application closing");
        };
    }

    private void HandleDashboardAction(string action, string value)
    {
        if (_closing) return;
        // Defer the action until the current OpenVR event-pump call has returned.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => HandleDashboardActionCore(action, value)));
    }

    private void HandleDashboardActionCore(string action, string value)
    {
        if (_closing) return;
        try
        {
            switch (action)
            {
                case "start":
                    StartReceiver();
                    break;
                case "stop":
                    StopReceiver("Stopped from SteamVR dashboard");
                    break;
                case "toggle-preset":
                {
                    MovementPreset preset = _presets.FirstOrDefault(p => string.Equals(p.Id, value, StringComparison.Ordinal));
                    if (preset == null)
                    {
                        AppLog.Write("WARN", "SteamVR dashboard requested an unknown movement preset: " + value);
                        return;
                    }
                    preset.Enabled = !preset.Enabled;
                    if (_presetControls.TryGetValue(preset.Id, out PresetControlSet controls))
                        controls.Enabled.IsChecked = preset.Enabled;
                    SavePresetStore();
                    ResetMovementState();
                    _movementSendFailures.Clear();
                    _missingReferenceWarnings.Clear();
                    AppLog.Write("INFO", $"Movement preset toggled from SteamVR dashboard: {preset.Name}={(preset.Enabled ? "enabled" : "disabled")}");
                    break;
                }
                default:
                    AppLog.Write("WARN", "Unknown SteamVR dashboard action ignored: " + action);
                    return;
            }
            RefreshInterface(throttle: false);
            _steamVrDashboard?.RequestImmediateRender();
        }
        catch (Exception ex)
        {
            if (action == "start") _udpError = ex.Message;
            AppLog.Write("ERROR", $"SteamVR dashboard action failed ({action}): {ex.Message}");
            RefreshInterface(throttle: false);
            _steamVrDashboard?.RequestImmediateRender();
        }
    }

    private void BeginKeybindCapture(TextBox box)
    {
        if (box == null || _captureBox == box) return;
        _captureBox = box;
        _captureOriginal = box.Text;
        box.Text = "Press a key or combination…";
    }

    private void HandleKeybindPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || _captureBox != box) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        string[] modifiers = GetModifierNames(key);

        if (key == Key.Escape && modifiers.Length == 0)
        {
            box.Text = _captureOriginal;
            _captureBox = null;
            e.Handled = true;
            return;
        }
        if ((key == Key.Back || key == Key.Delete) && modifiers.Length == 0)
        {
            box.Text = "";
            _captureBox = null;
            e.Handled = true;
            return;
        }
        if (IsModifierKey(key))
        {
            box.Text = modifiers.Length == 0 ? "Press a key or combination…" : string.Join(" + ", modifiers) + " + …";
            e.Handled = true;
            return;
        }

        string keyName = ConvertWpfKeyName(key);
        if (string.IsNullOrWhiteSpace(keyName))
        {
            box.Text = "Unsupported key. Try a letter, number, function, or navigation key.";
            e.Handled = true;
            return;
        }
        try
        {
            string shortcut = KeyboardInput.Validate(string.Join("+", modifiers.Append(keyName)));
            box.Text = shortcut;
            _captureBox = null;
        }
        catch
        {
            box.Text = "Unsupported key combination. Try fewer modifiers.";
        }
        e.Handled = true;
    }

    private static string[] GetModifierNames(Key pressedKey)
    {
        ModifierKeys state = Keyboard.Modifiers;
        List<string> result = new(4);
        if ((state & ModifierKeys.Control) != 0 || pressedKey is Key.LeftCtrl or Key.RightCtrl) result.Add("CTRL");
        if ((state & ModifierKeys.Alt) != 0 || pressedKey is Key.LeftAlt or Key.RightAlt) result.Add("ALT");
        if ((state & ModifierKeys.Shift) != 0 || pressedKey is Key.LeftShift or Key.RightShift) result.Add("SHIFT");
        if ((state & ModifierKeys.Windows) != 0 || pressedKey is Key.LWin or Key.RWin) result.Add("WIN");
        return result.ToArray();
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string ConvertWpfKeyName(Key key)
    {
        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) return name[1].ToString();
        if (name.StartsWith("NumPad", StringComparison.Ordinal) && name.Length == 7 && char.IsDigit(name[6])) return "NUM" + name[6];
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Return"] = "ENTER", ["Back"] = "BACKSPACE", ["Escape"] = "ESC", ["Space"] = "SPACE",
            ["PageUp"] = "PAGEUP", ["PageDown"] = "PAGEDOWN", ["Oem1"] = "OEM_1", ["OemSemicolon"] = "OEM_1",
            ["OemPlus"] = "OEM_PLUS", ["OemComma"] = "OEM_COMMA", ["OemMinus"] = "OEM_MINUS",
            ["OemPeriod"] = "OEM_PERIOD", ["Oem2"] = "OEM_2", ["OemQuestion"] = "OEM_2",
            ["Oem3"] = "OEM_3", ["OemTilde"] = "OEM_3", ["Oem4"] = "OEM_4", ["OemOpenBrackets"] = "OEM_4",
            ["Oem5"] = "OEM_5", ["OemPipe"] = "OEM_5", ["OemBackslash"] = "OEM_5",
            ["Oem6"] = "OEM_6", ["OemCloseBrackets"] = "OEM_6", ["Oem7"] = "OEM_7", ["OemQuotes"] = "OEM_7",
            ["Oem8"] = "OEM_8", ["Oem102"] = "OEM_102", ["CapsLock"] = "CAPSLOCK", ["NumLock"] = "NUMLOCK",
            ["Scroll"] = "SCROLLLOCK", ["Snapshot"] = "PRINTSCREEN", ["Apps"] = "APPS",
            ["Add"] = "ADD", ["Subtract"] = "SUBTRACT", ["Multiply"] = "MULTIPLY", ["Divide"] = "DIVIDE", ["Decimal"] = "DECIMAL"
        };
        if (map.TryGetValue(name, out string mapped)) return mapped;
        try { return KeyboardInput.Validate(name); } catch { return ""; }
    }

    private void SavePresetsFromUi()
    {
        try
        {
            if (!int.TryParse(KeybindDelayInput.Text, out int requestedDelay) || requestedDelay is < 0 or > 10000)
                throw new InvalidOperationException("Enter a minimum delay from 0 to 10000 milliseconds. Use 0 to disable the delay.");

            var updated = new List<(MovementPreset Preset, string Hotkey, string Target, bool Enabled, string Sensitivity, double CustomPosition, double CustomRotation)>();
            foreach (MovementPreset preset in _presets)
            {
                PresetControlSet controls = _presetControls[preset.Id];
                string hotkey = KeyboardInput.Validate(controls.Hotkey.Text);
                string target = controls.Target.SelectedItem as string ?? throw new InvalidOperationException("Choose a tracker role for preset " + preset.Id + ".");
                string sensitivity = controls.Sensitivity.SelectedValue as string;
                if (sensitivity is not ("Low" or "Normal" or "High" or "Custom"))
                    throw new InvalidOperationException("Choose a movement sensitivity for preset " + preset.Id + ".");

                double customPosition = preset.CustomPositionThreshold;
                double customRotation = preset.CustomRotationThreshold;
                if (sensitivity == "Custom")
                {
                    if (!double.TryParse(controls.CustomPosition.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out customPosition) ||
                        !double.IsFinite(customPosition) || customPosition < MinCustomPositionThreshold || customPosition > MaxCustomPositionThreshold)
                        throw new InvalidOperationException($"Custom position threshold for {preset.Name} must be between {MinCustomPositionThreshold:0.###} and {MaxCustomPositionThreshold:0.###} units.");
                    if (!double.TryParse(controls.CustomRotation.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out customRotation) ||
                        !double.IsFinite(customRotation) || customRotation < MinCustomRotationThreshold || customRotation > MaxCustomRotationThreshold)
                        throw new InvalidOperationException($"Custom rotation threshold for {preset.Name} must be between {MinCustomRotationThreshold:0.###} and {MaxCustomRotationThreshold:0.###} degrees.");
                }
                updated.Add((preset, hotkey, target, controls.Enabled.IsChecked == true, sensitivity, customPosition, customRotation));
            }

            foreach (var item in updated)
            {
                item.Preset.Hotkey = item.Hotkey;
                item.Preset.TargetRole = item.Target;
                item.Preset.Enabled = item.Enabled;
                item.Preset.Sensitivity = item.Sensitivity;
                item.Preset.CustomPositionThreshold = item.CustomPosition;
                item.Preset.CustomRotationThreshold = item.CustomRotation;
            }
            _keybindDelayMs = requestedDelay;
            SavePresetStore();
            SaveApplicationSettings();
            ResetMovementState();
            _movementSendFailures.Clear();
            _missingReferenceWarnings.Clear();
            string delayNotice = requestedDelay == 0
                ? "The automatic movement delay is disabled."
                : $"Automatic movement keybinds now wait at least {requestedDelay} ms between activations.";
            AppLog.Write("INFO", "Movement presets and keybind delay saved: " + delayNotice);
            MessageBox.Show("Movement presets saved. " + delayNotice, "MotionSFX",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "Saving presets failed: " + ex.Message);
            MessageBox.Show(ex.Message, "MotionSFX - Save changes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AssignSelectedRole()
    {
        try
        {
            if (TrackerGrid.SelectedItem is not TrackerGridRow selected)
                throw new InvalidOperationException("Select a detected output first.");
            string role = RoleCombo.SelectedItem as string ?? "Unassigned";
            if (!GetAllowedRoles(selected.kinds).Contains(role, StringComparer.Ordinal))
                throw new InvalidOperationException("That role is not valid for this VMC output type.");

            string[] displaced = Array.Empty<string>();
            if (role == "Unassigned") _roleStore.Remove(selected.id);
            else
            {
                displaced = _roleStore.Where(kv => kv.Value == role && kv.Key != selected.id).Select(kv => kv.Key).ToArray();
                foreach (string id in displaced) _roleStore.Remove(id);
                _roleStore[selected.id] = role;
            }
            SaveRoleStore();
            ResetMovementState();
            _missingReferenceWarnings.Clear();
            if (displaced.Length > 0)
                AppLog.Write("WARN", $"Body role {role} was reassigned to raw VMC identity=\"{selected.vmcLabel}\"; removed prior owner(s)={string.Join(", ", displaced)}.");
            AppLog.Write("INFO", $"VMC body-role assignment saved: type={selected.category}; VMC label=\"{selected.vmcLabel}\"; role={role}");
            RefreshInterface(false);
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "Role assignment failed: " + ex.Message);
            MessageBox.Show(ex.Message, "MotionSFX - Tracker role", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResetTrackerRoles()
    {
        if (_roleStore.Count == 0)
        {
            MessageBox.Show("There are no saved tracker body roles to reset.", "MotionSFX - Reset body roles",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MessageBoxResult answer = MessageBox.Show(
            "Reset every saved tracker body-role assignment? Detected tracker rows and movement preset settings will be kept.",
            "MotionSFX - Reset body roles", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            int count = _roleStore.Count;
            _roleStore.Clear();
            SaveRoleStore();
            ResetMovementState();
            _missingReferenceWarnings.Clear();
            AppLog.Write("INFO", $"Reset all saved tracker body-role assignments: count={count}.");
            RefreshInterface(false);
        }
        catch (Exception ex)
        {
            AppLog.Write("ERROR", "Resetting tracker body roles failed: " + ex.Message);
            MessageBox.Show(ex.Message, "MotionSFX - Reset body roles", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearSessionData()
    {
        _devices.Clear();
        _bones.Clear();
        ResetMovementState();
        _movementSendFailures.Clear();
        AppLog.Write("INFO", "Detected VMC tracker and skeleton-bone lists cleared by the user. Live session counters continue until reception is restarted.");
        RefreshInterface(false);
    }

    private void ResetMovementState()
    {
        _movementStates.Clear();
        _movementPreviewAnchors.Clear();
        ClearMovementPreviewSnapshots();
        _lastKeybindActivationUtc = DateTime.MinValue;
    }

    private void StartApplicationTimer()
    {
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            try { RefreshInterface(throttle: true); }
            catch (Exception ex) { AppLog.Write("ERROR", "Interface refresh failed: " + ex.Message); }
        };
        _refreshTimer.Start();
    }

    private void StartReceiver()
    {
        if (_receiverRunning)
        {
            AppLog.Write("WARN", "Start receiving was ignored because the VMC receiver is already running.");
            return;
        }
        MotionSfxVmcReceiver receiver = null;
        try
        {
            receiver = new MotionSfxVmcReceiver(_settings.UdpPort);
            receiver.Start();
            _receiver = receiver;
            _receiverRunning = true;
            _udpError = "";
            _receiverErrorLogged = false;
            ResetMovementState();
            _movementSendFailures.Clear();
            _lastMotionQueueDroppedCount = 0;
            _packetCount = _trackerUpdateCount = _malformedCount = 0;
            _lastSummaryUtc = DateTime.UtcNow;
            _lastSummaryPacketCount = _lastSummaryTrackerCount = _lastSummaryMalformedCount = 0;
            _lastNoTrafficNoticeUtc = DateTime.MinValue;
            _lastProcessCheckUtc = DateTime.MinValue;
            AppLog.Write("INFO", $"SlimeVR VMC reception STARTED on {BindAddress}:{_settings.UdpPort}. A dedicated low-priority C# thread receives and parses packets; no network work runs on the WPF dispatcher.");
        }
        catch
        {
            receiver?.Dispose();
            _receiver = null;
            _receiverRunning = false;
            throw;
        }
    }

    private void StopReceiver(string reason)
    {
        MotionSfxVmcReceiver receiver = _receiver;
        if (receiver == null)
        {
            _receiverRunning = false;
            if (reason != "Receiver error") _udpError = "";
            return;
        }
        try { receiver.Stop(); } catch { }
        try
        {
            foreach (MotionSfxVmcSample sample in receiver.DrainLatest())
            {
                if (sample.Kind == "BONE") UpdateBoneFromSample(sample);
                else UpdateDeviceFromSample(sample);
            }
        }
        catch { }
        _packetCount = receiver.PacketCount;
        _trackerUpdateCount = receiver.TransformCount;
        _malformedCount = receiver.MalformedCount;
        receiver.Dispose();
        _receiver = null;
        _receiverRunning = false;
        if (reason != "Receiver error") _udpError = "";
        AppLog.Write("INFO", "SlimeVR VMC reception STOPPED. Reason: " + reason + ". UDP socket closed; no further data is accepted.");
    }

    private void UpdateDeviceFromSample(MotionSfxVmcSample sample)
    {
        if (sample == null) return;
        if (!_devices.TryGetValue(sample.Id, out DeviceData device))
        {
            MigrateLegacyRoleAssignment(sample.Id, sample.VmcLabel);
            device = new DeviceData
            {
                Id = sample.Id,
                VmcLabel = sample.VmcLabel,
                FirstSeenUtc = sample.ReceivedUtc,
                LastSeenUtc = sample.ReceivedUtc,
                PacketCount = sample.PacketCount,
                SenderEndpoint = sample.SenderEndpoint
            };
            _devices.Add(sample.Id, device);
            AppLog.Write("INFO", $"New VMC device transform detected: type={sample.Kind}; VMC label=\"{sample.VmcLabel}\"; address={sample.SourceAddress}; sender={sample.SenderEndpoint}");
        }
        device.Kinds.Add(sample.Kind);
        TransformData transform = new()
        {
            X = sample.X, Y = sample.Y, Z = sample.Z,
            QX = sample.QX, QY = sample.QY, QZ = sample.QZ, QW = sample.QW,
            SourceAddress = sample.SourceAddress,
            SenderEndpoint = sample.SenderEndpoint,
            ReceivedUtc = sample.ReceivedUtc
        };
        if (sample.IsLocal) device.Local = transform;
        else device.Global = transform;
        device.LastSeenUtc = sample.ReceivedUtc;
        device.SenderEndpoint = sample.SenderEndpoint;
        device.PacketCount = sample.PacketCount;
    }

    private void UpdateBoneFromSample(MotionSfxVmcSample sample)
    {
        if (sample == null || sample.Kind != "BONE") return;
        if (!_bones.TryGetValue(sample.Id, out BoneData bone))
        {
            bone = new BoneData
            {
                Id = sample.Id, BoneName = sample.VmcLabel,
                FirstSeenUtc = sample.ReceivedUtc, LastSeenUtc = sample.ReceivedUtc,
                PacketCount = sample.PacketCount, SenderEndpoint = sample.SenderEndpoint
            };
            _bones.Add(sample.Id, bone);
            AppLog.Write("INFO", $"New SlimeVR skeleton bone detected: name=\"{sample.VmcLabel}\"; address={sample.SourceAddress}; sender={sample.SenderEndpoint}");
        }
        bone.X = sample.X; bone.Y = sample.Y; bone.Z = sample.Z;
        bone.QX = sample.QX; bone.QY = sample.QY; bone.QZ = sample.QZ; bone.QW = sample.QW;
        bone.LastSeenUtc = sample.ReceivedUtc;
        bone.PacketCount = sample.PacketCount;
        bone.SenderEndpoint = sample.SenderEndpoint;
    }

    private static string[] GetAllowedRoles(IEnumerable<string> kinds)
    {
        List<string> roles = new() { "Unassigned" };
        foreach (string kind in kinds)
        {
            string[] categories = kind switch
            {
                "TRA" => new[] { "LeftFoot", "RightFoot", "Hip", "Chest", "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg", "Other" },
                "HMD" => new[] { "Head" },
                "CON" => new[] { "LeftHand", "RightHand" },
                _ => new[] { "Other" }
            };
            foreach (string role in categories)
                if (!roles.Contains(role, StringComparer.Ordinal)) roles.Add(role);
        }
        return roles.ToArray();
    }

    private List<TrackerGridRow> GetDeviceRows()
    {
        DateTime now = DateTime.UtcNow;
        List<TrackerGridRow> rows = new(_devices.Count);
        foreach (DeviceData device in _devices.Values)
        {
            double age = Math.Max(0, (now - device.LastSeenUtc).TotalMilliseconds);
            string status = age <= _staleAfterMs ? "Live" : age <= _lostAfterMs ? "Stale" : "Lost";
            TransformData sample = device.Global ?? device.Local;
            string position = "—";
            if (sample != null)
            {
                string coord = sample.SourceAddress.EndsWith("/Local", StringComparison.Ordinal) ? "Local" : "Global";
                position = string.Format(CultureInfo.CurrentCulture, "{0:N3}, {1:N3}, {2:N3} ({3})", sample.X, sample.Y, sample.Z, coord);
            }
            string[] kinds = device.Kinds.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            string role = _roleStore.TryGetValue(device.Id, out string saved) ? saved : "Unassigned";
            if (!GetAllowedRoles(kinds).Contains(role, StringComparer.Ordinal)) role = "Unassigned";
            rows.Add(new TrackerGridRow
            {
                id = device.Id, vmcLabel = device.VmcLabel,
                category = string.Join(", ", kinds), kinds = kinds,
                role = role, status = status, ageMs = (int)Math.Round(age),
                packetCount = device.PacketCount, senderEndpoint = device.SenderEndpoint,
                positionText = position
            });
        }
        return rows.OrderBy(r => r.category, StringComparer.Ordinal).ThenBy(r => r.vmcLabel, StringComparer.Ordinal).ToList();
    }

    private List<BoneGridRow> GetBoneRows()
    {
        DateTime now = DateTime.UtcNow;
        List<BoneGridRow> rows = new(_bones.Count);
        foreach (BoneData bone in _bones.Values)
        {
            double age = Math.Max(0, (now - bone.LastSeenUtc).TotalMilliseconds);
            string status = age <= _staleAfterMs ? "Live" : age <= _lostAfterMs ? "Stale" : "Lost";
            rows.Add(new BoneGridRow
            {
                id = bone.Id, boneName = bone.BoneName, status = status, ageMs = (int)Math.Round(age),
                packetCount = bone.PacketCount, senderEndpoint = bone.SenderEndpoint,
                positionText = string.Format(CultureInfo.CurrentCulture, "{0:N3}, {1:N3}, {2:N3}", bone.X, bone.Y, bone.Z),
                rotationText = string.Format(CultureInfo.CurrentCulture, "{0:N3}, {1:N3}, {2:N3}, {3:N3}", bone.QX, bone.QY, bone.QZ, bone.QW)
            });
        }
        return rows.OrderBy(r => r.boneName, StringComparer.Ordinal).ToList();
    }

    private void SetSelectedTrackerRoleOptions(string preferredRole = null)
    {
        if (TrackerGrid.SelectedItem is not TrackerGridRow selected)
        {
            if (RoleCombo.Items.Count != 1 || !Equals(RoleCombo.Items[0], "Unassigned"))
            {
                RoleCombo.Items.Clear();
                RoleCombo.Items.Add("Unassigned");
            }
            RoleCombo.SelectedItem = "Unassigned";
            SelectedTrackerText.Text = "Select a VMC tracker output to assign its body role";
            return;
        }

        SelectedTrackerText.Text = selected.category + " · " + selected.vmcLabel;
        string[] options = GetAllowedRoles(selected.kinds);

        // Keep the existing ComboBox item collection whenever the selected
        // tracker's available roles have not changed. Rebuilding it on every
        // live refresh closes the dropdown and can discard its selection.
        bool optionsChanged = RoleCombo.Items.Count != options.Length;
        if (!optionsChanged)
        {
            for (int i = 0; i < options.Length; i++)
            {
                if (!Equals(RoleCombo.Items[i], options[i]))
                {
                    optionsChanged = true;
                    break;
                }
            }
        }
        if (optionsChanged)
        {
            RoleCombo.Items.Clear();
            foreach (string option in options) RoleCombo.Items.Add(option);
        }

        string roleToSelect = !string.IsNullOrEmpty(preferredRole) && options.Contains(preferredRole, StringComparer.Ordinal)
            ? preferredRole
            : options.Contains(selected.role, StringComparer.Ordinal) ? selected.role : "Unassigned";
        RoleCombo.SelectedItem = roleToSelect;
    }

    private void RefreshTrackerGrid()
    {
        // Keep both the row identity and the editor's current choice before
        // replacing ItemsSource. The latter may be an intentional, not-yet-
        // committed selection the user is preparing to assign.
        string selectedId = (TrackerGrid.SelectedItem as TrackerGridRow)?.id ?? _roleEditorTrackerId;
        string preferredRole = RoleCombo.SelectedItem as string;
        List<TrackerGridRow> rows = GetDeviceRows();

        _isRefreshingTrackerGrid = true;
        try
        {
            TrackerGrid.ItemsSource = rows;
            TrackerGrid.SelectedItem = !string.IsNullOrEmpty(selectedId)
                ? rows.FirstOrDefault(r => r.id == selectedId)
                : null;
        }
        finally
        {
            _isRefreshingTrackerGrid = false;
        }

        if (TrackerGrid.SelectedItem is TrackerGridRow selected)
        {
            bool sameTracker = string.Equals(_roleEditorTrackerId, selected.id, StringComparison.Ordinal);
            _roleEditorTrackerId = selected.id;
            SetSelectedTrackerRoleOptions(sameTracker ? preferredRole : null);
        }
        else
        {
            _roleEditorTrackerId = null;
            SetSelectedTrackerRoleOptions();
        }
    }

    private void RefreshBoneGrid()
    {
        string selectedId = (BoneGrid.SelectedItem as BoneGridRow)?.id;
        List<BoneGridRow> rows = GetBoneRows();
        BoneGrid.ItemsSource = rows;
        if (!string.IsNullOrEmpty(selectedId))
            BoneGrid.SelectedItem = rows.FirstOrDefault(r => r.id == selectedId);
    }

    private static void SetStatus(TextBlock element, string tone)
    {
        element.Foreground = tone switch
        {
            "Green" => Brushes.LimeGreen,
            "Yellow" => Brushes.Yellow,
            _ => Brushes.Red
        };
    }

    private bool? _lastSteamSnapshot;
    private void RefreshInterface(bool throttle)
    {
        if (_closing) return;
        try { _steamVrDashboard?.Pump(); }
        catch (Exception ex) { AppLog.Write("WARN", "SteamVR dashboard update failed: " + ex.Message); }
        DateTime now = DateTime.UtcNow;
        if (_receiver != null)
        {
            if (!throttle || (now - _lastLatestDrainUtc).TotalMilliseconds >= 250)
            {
                _lastLatestDrainUtc = now;
                foreach (MotionSfxVmcSample sample in _receiver.DrainLatest())
                {
                    if (sample.Kind == "BONE") UpdateBoneFromSample(sample);
                    else UpdateDeviceFromSample(sample);
                }
            }

            MotionSfxVmcSample[] motionSamples = _receiver.DrainMotionSamples();
            if (motionSamples.Length > MaxMotionBatch)
            {
                int skipped = motionSamples.Length - MaxMotionBatch;
                _motionBacklogSkipped += skipped;
                motionSamples = motionSamples[^MaxMotionBatch..];
                if ((now - _lastBacklogNoticeUtc).TotalSeconds >= 10)
                {
                    AppLog.Write("WARN", $"Movement processing fell behind; skipped {_motionBacklogSkipped} old movement samples so the interface stays responsive.");
                    _lastBacklogNoticeUtc = now;
                }
            }
            if (motionSamples.Length > 0) ProcessMovementSamples(motionSamples);

            long dropped = _receiver.MotionQueueDroppedCount;
            if (dropped > _lastMotionQueueDroppedCount)
            {
                AppLog.Write("WARN", $"Movement sample queue overflowed; oldest movement samples dropped={dropped - _lastMotionQueueDroppedCount}.");
                _lastMotionQueueDroppedCount = dropped;
            }

            _packetCount = _receiver.PacketCount;
            _trackerUpdateCount = _receiver.TransformCount;
            _malformedCount = _receiver.MalformedCount;
            string workerError = _receiver.LastError;
            if (!string.IsNullOrWhiteSpace(workerError) && !_receiverErrorLogged)
            {
                _udpError = workerError;
                _receiverErrorLogged = true;
                AppLog.Write("ERROR", "VMC background receiver failed: " + _udpError);
                StopReceiver("Receiver error");
            }
            else if (_receiverRunning && !_receiver.IsRunning && !_receiverErrorLogged)
            {
                _udpError = "The background UDP receiver stopped unexpectedly.";
                _receiverErrorLogged = true;
                AppLog.Write("ERROR", _udpError);
                StopReceiver("Receiver error");
            }

            if (_receiverRunning && (now - _lastSummaryUtc).TotalSeconds >= 5)
            {
                long packetDelta = _packetCount - _lastSummaryPacketCount;
                long transformDelta = _trackerUpdateCount - _lastSummaryTrackerCount;
                long malformedDelta = _malformedCount - _lastSummaryMalformedCount;
                if (packetDelta > 0 || malformedDelta > 0)
                    AppLog.Write("INFO", $"VMC traffic (last 5s): datagrams={packetDelta}; valid VMC transforms={transformDelta}; unique raw VMC identities={_devices.Count}; skeleton bones={_bones.Count}; malformed/unsupported datagrams={malformedDelta}.");
                else if ((now - _lastNoTrafficNoticeUtc).TotalSeconds >= 15)
                {
                    AppLog.Write("WARN", $"No UDP datagrams received on {BindAddress}:{_settings.UdpPort}. Check SlimeVR VMC output destination and Port Out.");
                    _lastNoTrafficNoticeUtc = now;
                }
                _lastSummaryUtc = now;
                _lastSummaryPacketCount = _packetCount;
                _lastSummaryTrackerCount = _trackerUpdateCount;
                _lastSummaryMalformedCount = _malformedCount;
            }
        }

        if (throttle && (now - _lastStatusRefreshUtc).TotalMilliseconds < 250) return;
        _lastStatusRefreshUtc = now;
        RefreshMovementPreviewUi(now);
        UpdateProcessSnapshot(now);
        List<TrackerGridRow> rowsNow = GetDeviceRows();
        int liveCount = rowsNow.Count(r => r.status == "Live");
        List<BoneGridRow> boneRowsNow = GetBoneRows();
        int liveBoneCount = boneRowsNow.Count(r => r.status == "Live");

        if (_receiverRunning && _packetCount > 0)
        {
            SlimeStatus.Text = "Receiving";
            SetStatus(SlimeStatus, "Green");
            SlimeDetail.Text = $"VMC packets received · {_devices.Count} unique VMC identities · {_bones.Count} skeleton bones";
        }
        else if (_receiverRunning)
        {
            SlimeStatus.Text = "Listening";
            SetStatus(SlimeStatus, "Yellow");
            SlimeDetail.Text = "Waiting for SlimeVR VMC packets";
        }
        else
        {
            SlimeStatus.Text = "Stopped";
            SetStatus(SlimeStatus, "Red");
            SlimeDetail.Text = "Reception is stopped";
        }

        SteamStatus.Text = _steamVrDetected ? "Detected" : "Not detected";
        SetStatus(SteamStatus, _steamVrDetected ? "Green" : "Red");
        SteamDetail.Text = _steamVrDetected ? "SteamVR runtime is running" : "SteamVR runtime not found";

        string vmcTone;
        if (!string.IsNullOrWhiteSpace(_udpError))
        {
            VmcStatus.Text = "Receiver error";
            VmcDetail.Text = _udpError;
            vmcTone = "Red";
        }
        else if (!_receiverRunning)
        {
            VmcStatus.Text = "Stopped";
            VmcDetail.Text = $"Press Start receiving to listen on {BindAddress}:{_settings.UdpPort}";
            vmcTone = "Red";
        }
        else if (liveCount > 0 || liveBoneCount > 0)
        {
            VmcStatus.Text = "Receiving";
            VmcDetail.Text = $"{BindAddress}:{_settings.UdpPort} · {liveCount} live unique VMC identity(ies) · {liveBoneCount} live bone(s)";
            vmcTone = "Green";
        }
        else
        {
            VmcStatus.Text = "Listening";
            VmcDetail.Text = $"{BindAddress}:{_settings.UdpPort} · waiting for packets";
            vmcTone = "Yellow";
        }
        SetStatus(VmcStatus, vmcTone);

        MetricDevices.Text = rowsNow.Count.ToString(CultureInfo.InvariantCulture);
        MetricLive.Text = liveCount.ToString(CultureInfo.InvariantCulture);
        MetricPackets.Text = _packetCount.ToString(CultureInfo.InvariantCulture);
        MetricMalformed.Text = _malformedCount.ToString(CultureInfo.InvariantCulture);
        StartReceivingButton.IsEnabled = !_receiverRunning;
        StopReceivingButton.IsEnabled = _receiverRunning;
        StartReceivingButton.Opacity = _receiverRunning ? 0.45 : 1.0;
        StopReceivingButton.Opacity = _receiverRunning ? 1.0 : 0.45;

        if (!string.IsNullOrWhiteSpace(_udpError))
        {
            OverallStatusText.Text = "VMC RECEIVER ERROR";
            SetStatus(OverallStatusText, "Red");
        }
        else if (_receiverRunning && (liveCount > 0 || liveBoneCount > 0))
        {
            OverallStatusText.Text = "VMC DATA RECEIVING";
            SetStatus(OverallStatusText, "Green");
        }
        else if (_receiverRunning)
        {
            OverallStatusText.Text = "LISTENING";
            SetStatus(OverallStatusText, "Yellow");
        }
        else
        {
            OverallStatusText.Text = "RECEPTION STOPPED";
            SetStatus(OverallStatusText, "Red");
        }
        OverallStatusDot.Fill = OverallStatusText.Foreground;

        FooterLeft.Text = $"VMC UDP: {BindAddress}:{_settings.UdpPort}  ·  {(_receiverRunning ? "RUNNING" : "STOPPED")}";
        FooterRight.Text = $"Uptime: {(int)(DateTime.UtcNow - _startedUtc).TotalSeconds}s  ·  Outputs: {rowsNow.Count}";

        if (MainTabs.SelectedIndex == 1 && (now - _lastGridRefreshUtc).TotalSeconds >= 1)
        {
            if (TrackerDataTabs.SelectedIndex == 0) RefreshTrackerGrid();
            else if (TrackerDataTabs.SelectedIndex == 1) RefreshBoneGrid();
            _lastGridRefreshUtc = now;
        }

        _steamVrDashboard?.UpdateSnapshot(new SteamVrDashboardSnapshot
        {
            SteamVrDetected = _steamVrDetected,
            ReceiverRunning = _receiverRunning,
            ReceiverStatus = !string.IsNullOrWhiteSpace(_udpError) ? "Error" :
                !_receiverRunning ? "Stopped" : (liveCount > 0 || liveBoneCount > 0) ? "Receiving" : "Listening",
            ReceiverDetail = !string.IsNullOrWhiteSpace(_udpError) ? _udpError :
                !_receiverRunning ? "Reception is stopped" :
                $"127.0.0.1:{_settings.UdpPort} · {liveCount} live VMC identity(ies)",
            TrackerCount = rowsNow.Count,
            LiveTrackerCount = liveCount,
            BoneCount = _bones.Count,
            PacketCount = _packetCount,
            ActivePresetCount = _presets.Count(p => p.Enabled),
            ReceiverError = _udpError,
            Trackers = rowsNow.Take(8).Select(r => new SteamVrDashboardTracker(
                r.vmcLabel, r.role, r.category, r.status, r.packetCount)).ToArray(),
            Presets = _presets.Select(p => new SteamVrDashboardPreset(
                p.Id, p.Name, p.Hotkey, p.TargetRole, p.Sensitivity, p.Enabled)).ToArray(),
            Graphs = _presets.Select(p =>
            {
                _movementPreviewViewModels.TryGetValue(p.Id, out MovementPreviewViewModel vm);
                MovementPreviewSnapshot preview = GetMovementPreviewSnapshot(p.Id);
                (double Position, double Rotation) configured = GetMovementThresholds(
                    p.Sensitivity, p.CustomPositionThreshold, p.CustomRotationThreshold);
                double positionThreshold = preview != null && preview.PositionThreshold > 0
                    ? preview.PositionThreshold : configured.Position;
                double rotationThreshold = preview != null && preview.RotationThreshold > 0
                    ? preview.RotationThreshold : configured.Rotation;
                return new SteamVrDashboardGraph(
                    p.Id, p.Name, vm?.StatusText ?? "Waiting for tracker data",
                    positionThreshold, rotationThreshold,
                    vm?.GetGraphHistorySnapshot() ?? Array.Empty<MovementPreviewGraphSample>());
            }).ToArray()
        });
    }

    private void UpdateProcessSnapshot(DateTime now)
    {
        if ((now - _lastProcessCheckUtc).TotalSeconds < 10) return;
        bool found = false;
        try
        {
            foreach (string processName in new[] { "vrserver", "vrmonitor", "vrcompositor" })
            {
                Process[] processes = Process.GetProcessesByName(processName);
                if (processes.Length > 0) found = true;
                foreach (Process process in processes) process.Dispose();
                if (found) break;
            }
        }
        catch { }
        if (_lastSteamSnapshot == null || _lastSteamSnapshot.Value != found)
            AppLog.Write("INFO", "Process detection changed: SteamVR=" + found);
        _lastSteamSnapshot = found;
        _steamVrDetected = found;
        _lastProcessCheckUtc = now;
    }

    private void ProcessMovementSamples(MotionSfxVmcSample[] samples)
    {
        if (!_receiverRunning || samples == null || samples.Length == 0) return;
        RoleOwnership ownership = GetRoleOwnership();
        Dictionary<string, Dictionary<string, MotionSfxVmcSample>> frames = new(StringComparer.Ordinal);
        List<string> frameOrder = new(samples.Length);
        Dictionary<string, List<MotionSfxVmcSample>> roleHistory = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<long>> roleHistoryKeys = new(StringComparer.Ordinal);
        Dictionary<string, bool> allowedRoleCache = new(StringComparer.Ordinal);

        foreach (MotionSfxVmcSample sample in samples)
        {
            if (sample == null || sample.Kind == "BONE") continue;
            if (!_devices.TryGetValue(sample.Id, out DeviceData device)) continue;
            bool globalFresh = device.Global != null && (DateTime.UtcNow - device.Global.ReceivedUtc).TotalMilliseconds <= _staleAfterMs;
            if (sample.IsLocal && globalFresh) continue;
            string space = sample.IsLocal ? "LOCAL" : "GLOBAL";
            if (!_roleStore.TryGetValue(sample.Id, out string role)) continue;
            if (ownership.AmbiguousRoles.Contains(role) || !ownership.Owners.TryGetValue(role, out string owner) || owner != sample.Id) continue;

            string allowedKey = sample.Id + "|" + role;
            if (!allowedRoleCache.TryGetValue(allowedKey, out bool allowed))
            {
                allowed = GetAllowedRoles(device.Kinds).Contains(role, StringComparer.Ordinal);
                allowedRoleCache[allowedKey] = allowed;
            }
            if (!allowed) continue;

            string historyKey = role + "|" + space;
            if (!roleHistory.TryGetValue(historyKey, out List<MotionSfxVmcSample> history))
            {
                history = new List<MotionSfxVmcSample>();
                roleHistory[historyKey] = history;
                roleHistoryKeys[historyKey] = new HashSet<long>();
            }
            if (roleHistoryKeys[historyKey].Add(sample.ReceivedUtc.Ticks)) history.Add(sample);

            string frameKey = sample.ReceivedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + space;
            if (!frames.TryGetValue(frameKey, out Dictionary<string, MotionSfxVmcSample> frame))
            {
                frame = new Dictionary<string, MotionSfxVmcSample>(StringComparer.Ordinal);
                frames[frameKey] = frame;
                frameOrder.Add(frameKey);
            }
            if (!frame.ContainsKey(role)) frame[role] = sample;
        }

        foreach (string frameKey in frameOrder)
        {
            Dictionary<string, MotionSfxVmcSample> frame = frames[frameKey];
            string space = frameKey.EndsWith("|LOCAL", StringComparison.Ordinal) ? "LOCAL" : "GLOBAL";
            foreach (MovementPreset preset in _presets)
            {
                string targetRole = preset.TargetRole;
                if (ownership.AmbiguousRoles.Contains(targetRole))
                {
                    StoreMovementPreviewIssue(preset, "Target tracker role is ambiguous · reassign on Trackers");
                    continue;
                }
                if (!ownership.Owners.TryGetValue(targetRole, out string targetId))
                {
                    StoreMovementPreviewIssue(preset, "Target tracker role not assigned");
                    continue;
                }
                if (!frame.TryGetValue(targetRole, out MotionSfxVmcSample sample)) continue;

                string referenceRole = GetMovementReferenceRole(targetRole);
                if (!string.IsNullOrEmpty(referenceRole))
                {
                    if (ownership.AmbiguousRoles.Contains(referenceRole))
                    {
                        StoreMovementPreviewIssue(preset, "Reference role is ambiguous · reassign on Trackers");
                        if (_missingReferenceWarnings.Add(preset.Id))
                            AppLog.Write("WARN", $"Movement preset {preset.Name} needs an unambiguous {referenceRole} reference role; its keybind is suppressed until corrected.");
                        continue;
                    }
                    if (!ownership.Owners.ContainsKey(referenceRole))
                    {
                        StoreMovementPreviewIssue(preset, $"Reference tracker role {referenceRole} not assigned");
                        if (_missingReferenceWarnings.Add(preset.Id))
                            AppLog.Write("WARN", $"Movement preset {preset.Name} needs the {referenceRole} body role assigned on Trackers; its keybind is suppressed until that reference is available.");
                        continue;
                    }
                    if (!roleHistory.TryGetValue(referenceRole + "|" + space, out List<MotionSfxVmcSample> references))
                    {
                        StoreMovementPreviewIssue(preset, $"Waiting for synchronized {referenceRole} reference data");
                        continue;
                    }
                    MotionSfxVmcSample reference = null;
                    double nearestMs = double.MaxValue;
                    foreach (MotionSfxVmcSample candidate in references)
                    {
                        double delta = Math.Abs((sample.ReceivedUtc - candidate.ReceivedUtc).TotalMilliseconds);
                        if (delta < nearestMs)
                        {
                            nearestMs = delta;
                            reference = candidate;
                        }
                        else if (delta > nearestMs) break;
                    }
                    if (reference == null || nearestMs > 75.0)
                    {
                        StoreMovementPreviewIssue(preset, $"Waiting for synchronized {referenceRole} reference data");
                        continue;
                    }
                    sample = ConvertToRelativeMotionSample(sample, reference);
                }

                string stateKey = preset.Id + "|" + targetId;
                bool previewOnly = !preset.Enabled || _movementSendFailures.Contains(preset.Id);
                Dictionary<string, MovementAnchor> anchorStore = previewOnly ? _movementPreviewAnchors : _movementStates;
                if (!anchorStore.TryGetValue(stateKey, out MovementAnchor state))
                {
                    state = new MovementAnchor();
                    SetMovementAnchor(state, sample, space);
                    anchorStore[stateKey] = state;
                    StoreMovementPreview(preset, true, 0, 0, sample.ReceivedUtc, "Establishing movement baseline");
                    continue;
                }
                DateTime sampleUtc = sample.ReceivedUtc;
                double sampleGapMs = (sampleUtc - state.LastSampleUtc).TotalMilliseconds;
                double anchorAgeMs = (sampleUtc - state.AnchorUtc).TotalMilliseconds;
                if (state.Space != space || sampleGapMs <= 0 || sampleGapMs > 1500 || anchorAgeMs < 0 || anchorAgeMs > 1500)
                {
                    SetMovementAnchor(state, sample, space);
                    StoreMovementPreview(preset, true, 0, 0, sampleUtc, "Re-establishing movement baseline");
                    continue;
                }

                // These are the detector's existing measurements: Euclidean distance and
                // quaternion angular difference, both measured from the same saved anchor.
                double dx = sample.X - state.X, dy = sample.Y - state.Y, dz = sample.Z - state.Z;
                double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double anchorNorm = Math.Sqrt(state.QX * state.QX + state.QY * state.QY + state.QZ * state.QZ + state.QW * state.QW);
                double sampleNorm = Math.Sqrt(sample.QX * sample.QX + sample.QY * sample.QY + sample.QZ * sample.QZ + sample.QW * sample.QW);
                double angle = 0;
                if (anchorNorm > 0.000001 && sampleNorm > 0.000001)
                {
                    double dot = Math.Abs((state.QX * sample.QX + state.QY * sample.QY + state.QZ * sample.QZ + state.QW * sample.QW) / (anchorNorm * sampleNorm));
                    dot = Math.Min(1.0, Math.Max(0.0, dot));
                    angle = 2.0 * Math.Acos(dot) * (180.0 / Math.PI);
                }

                (double Position, double Rotation) threshold = GetMovementThresholds(
                    preset.Sensitivity, preset.CustomPositionThreshold, preset.CustomRotationThreshold);
                bool thresholdReached = !(distance < threshold.Position && angle < threshold.Rotation);
                bool outlier = distance > 1.25 || angle > 170.0;
                if (outlier)
                {
                    // Keep the detector's existing jump guard and re-baseline behavior visible.
                    StoreMovementPreview(preset, true, distance, angle, sampleUtc,
                        "Large transform jump ignored by detector", thresholdReached, isOutlier: true);
                    SetMovementAnchor(state, sample, space);
                    continue;
                }

                if (!thresholdReached)
                {
                    StoreMovementPreview(preset, true, distance, angle, sampleUtc, "Below threshold");
                    if (anchorAgeMs > 900) SetMovementAnchor(state, sample, space);
                    else state.LastSampleUtc = sampleUtc;
                    continue;
                }

                // Disabled or previously failed presets are measured on the preview-only
                // anchor store above; they never enter the shortcut-send path.
                if (previewOnly && !preset.Enabled)
                {
                    StoreMovementPreview(preset, true, distance, angle, sampleUtc,
                        "Threshold reached while preset is disabled", thresholdReached: true);
                    SetMovementAnchor(state, sample, space);
                    continue;
                }

                if (previewOnly && _movementSendFailures.Contains(preset.Id))
                {
                    StoreMovementPreview(preset, true, distance, angle, sampleUtc,
                        "Shortcut sending failed; preset disabled for this session", thresholdReached: true, sendFailed: true);
                    SetMovementAnchor(state, sample, space);
                    continue;
                }

                double delayRemaining = _lastKeybindActivationUtc == DateTime.MinValue || _keybindDelayMs <= 0
                    ? 0 : _keybindDelayMs - (DateTime.UtcNow - _lastKeybindActivationUtc).TotalMilliseconds;
                if (delayRemaining > 0)
                {
                    StoreMovementPreview(preset, true, distance, angle, sampleUtc,
                        "Threshold reached but blocked by global activation delay", thresholdReached: true, delayBlocked: true);
                    SetMovementAnchor(state, sample, space);
                    continue;
                }

                try
                {
                    string shortcut = KeyboardInput.Validate(preset.Hotkey);
                    if (string.IsNullOrWhiteSpace(shortcut)) throw new InvalidOperationException("This movement preset has no saved keybind.");
                    KeyboardInput.Send(shortcut);
                    _lastKeybindActivationUtc = DateTime.UtcNow;
                    StoreMovementPreview(preset, true, distance, angle, sampleUtc,
                        "Shortcut sent successfully", thresholdReached: true, shortcutSent: true);
                    AppLog.Write("INFO", $"Movement detected; keybind sent: preset={preset.Name}; role={targetRole}; reference role={(string.IsNullOrEmpty(referenceRole) ? "none" : referenceRole)}; raw VMC identity=\"{sample.VmcLabel}\"; keybind={shortcut}; minimum activation delay={_keybindDelayMs} ms; relative position delta={distance:N3}; relative rotation delta={angle:N1} degrees.");
                }
                catch (Exception ex)
                {
                    _movementSendFailures.Add(preset.Id);
                    StoreMovementPreview(preset, true, distance, angle, sampleUtc,
                        "Shortcut sending failed; preset disabled for this session", thresholdReached: true, sendFailed: true);
                    AppLog.Write("ERROR", $"Automatic movement keybind disabled for this session: preset={preset.Name}; {ex.Message}");
                }
                SetMovementAnchor(state, sample, space);
            }
        }
    }

    private static (double Position, double Rotation) GetMovementThresholds(
        string sensitivity, double customPosition, double customRotation) => sensitivity switch
    {
        "High" => (0.10, 6.0),
        "Low" => (0.20, 17.0),
        "Custom" => (customPosition, customRotation),
        _ => (0.15, 10.0)
    };

    private static string GetMovementReferenceRole(string targetRole)
    {
        if (targetRole is "LeftFoot" or "RightFoot" or "LeftUpperLeg" or "RightUpperLeg" or "LeftLowerLeg" or "RightLowerLeg") return "Hip";
        if (targetRole is "Head" or "LeftHand" or "RightHand") return "Chest";
        if (targetRole == "Chest") return "Hip";
        return "";
    }

    private static MotionSfxVmcSample ConvertToRelativeMotionSample(MotionSfxVmcSample target, MotionSfxVmcSample reference)
    {
        double norm = Math.Sqrt(reference.QX * reference.QX + reference.QY * reference.QY + reference.QZ * reference.QZ + reference.QW * reference.QW);
        double ix = 0, iy = 0, iz = 0, iw = 1;
        if (norm > 0.000001)
        {
            ix = -reference.QX / norm; iy = -reference.QY / norm; iz = -reference.QZ / norm; iw = reference.QW / norm;
        }
        double wx = target.X - reference.X, wy = target.Y - reference.Y, wz = target.Z - reference.Z;
        double cx = 2.0 * (iy * wz - iz * wy);
        double cy = 2.0 * (iz * wx - ix * wz);
        double cz = 2.0 * (ix * wy - iy * wx);
        double rx = wx + iw * cx + iy * cz - iz * cy;
        double ry = wy + iw * cy + iz * cx - ix * cz;
        double rz = wz + iw * cz + ix * cy - iy * cx;
        double targetNorm = Math.Sqrt(target.QX * target.QX + target.QY * target.QY + target.QZ * target.QZ + target.QW * target.QW);
        double tx = 0, ty = 0, tz = 0, tw = 1;
        if (targetNorm > 0.000001)
        {
            tx = target.QX / targetNorm; ty = target.QY / targetNorm; tz = target.QZ / targetNorm; tw = target.QW / targetNorm;
        }
        double qx = ix * tw + iw * tx + iy * tz - iz * ty;
        double qy = iy * tw + iw * ty + iz * tx - ix * tz;
        double qz = iz * tw + iw * tz + ix * ty - iy * tx;
        double qw = iw * tw - ix * tx - iy * ty - iz * tz;
        return new MotionSfxVmcSample(target.Id, target.VmcLabel, target.Kind, target.SourceAddress,
            target.IsLocal, rx, ry, rz, qx, qy, qz, qw, target.ReceivedUtc,
            target.SenderEndpoint, target.PacketCount);
    }

    private static void SetMovementAnchor(MovementAnchor state, MotionSfxVmcSample sample, string space)
    {
        state.X = sample.X; state.Y = sample.Y; state.Z = sample.Z;
        state.QX = sample.QX; state.QY = sample.QY; state.QZ = sample.QZ; state.QW = sample.QW;
        state.AnchorUtc = sample.ReceivedUtc;
        state.LastSampleUtc = sample.ReceivedUtc;
        state.Space = space;
    }
}
