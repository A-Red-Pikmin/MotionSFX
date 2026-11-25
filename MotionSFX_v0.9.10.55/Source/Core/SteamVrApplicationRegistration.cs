using System.IO;
using System.Text;
using System.Text.Json;
using Valve.VR;

namespace MotionSFX.Core;

/// <summary>
/// Registers MotionSFX as a persistent SteamVR dashboard application and enables
/// SteamVR's own startup-overlay auto-launch flag. The manifest is stored under
/// the user's AppData directory so it remains available independently of the
/// main application folder.
/// </summary>
internal static class SteamVrApplicationRegistration
{
    public const string ApplicationKey = "motionsfx.dashboard.native";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true
    };

    public static void RegisterAndEnableAutoLaunch(string logoPath)
    {
        string processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Windows did not provide the current MotionSFX executable path.");

        string executablePath = Path.GetFullPath(processPath);
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("The MotionSFX executable could not be found for SteamVR registration.", executablePath);

        string workingDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("The MotionSFX executable directory could not be determined.");
        string fullLogoPath = Path.GetFullPath(logoPath);
        if (!File.Exists(fullLogoPath))
            throw new FileNotFoundException("The supplied MotionSFX logo is required for SteamVR registration.", fullLogoPath);

        string manifestDirectory = Path.Combine(AppLog.DataRoot, "SteamVR");
        string manifestPath = Path.Combine(manifestDirectory, "MotionSFX.vrmanifest");
        Directory.CreateDirectory(manifestDirectory);

        var manifest = new
        {
            source = "builtin",
            applications = new[]
            {
                new
                {
                    app_key = ApplicationKey,
                    launch_type = "binary",
                    binary_path_windows = executablePath,
                    working_directory = workingDirectory,
                    is_dashboard_overlay = true,
                    image_path = fullLogoPath,
                    strings = new
                    {
                        en_us = new
                        {
                            name = "MotionSFX",
                            description = "MotionSFX full-body tracking movement and sound dashboard"
                        }
                    }
                }
            }
        };

        string manifestJson = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        if (!File.Exists(manifestPath) || !string.Equals(File.ReadAllText(manifestPath), manifestJson, StringComparison.Ordinal))
        {
            string temporaryPath = manifestPath + ".tmp";
            File.WriteAllText(temporaryPath, manifestJson, new UTF8Encoding(false));
            File.Move(temporaryPath, manifestPath, true);
        }

        CVRApplications applications = OpenVR.Applications;
        if (applications == null)
            throw new InvalidOperationException("OpenVR did not provide the applications interface required for SteamVR startup registration.");

        bool installed = applications.IsApplicationInstalled(ApplicationKey);
        if (installed)
        {
            string registeredExecutable = GetRegisteredExecutablePath(applications);
            if (!string.IsNullOrWhiteSpace(registeredExecutable) &&
                !string.Equals(Path.GetFullPath(registeredExecutable), executablePath, StringComparison.OrdinalIgnoreCase))
            {
                // The user may have moved or re-extracted MotionSFX into a new folder.
                // Refresh our persistent manifest so SteamVR starts the current executable.
                EVRApplicationError removeError = applications.RemoveApplicationManifest(manifestPath);
                if (removeError != EVRApplicationError.None)
                    throw new InvalidOperationException("SteamVR could not refresh the previous MotionSFX manifest (" + removeError + ").");
                installed = false;
                AppLog.Write("INFO", "SteamVR MotionSFX registration path changed; refreshing the application manifest.");
            }
        }

        if (!installed)
        {
            EVRApplicationError addError = applications.AddApplicationManifest(manifestPath, false);
            if (addError != EVRApplicationError.None)
                throw new InvalidOperationException("SteamVR rejected the MotionSFX application manifest (" + addError + ").");
        }

        if (!applications.IsApplicationInstalled(ApplicationKey))
            throw new InvalidOperationException("SteamVR did not report MotionSFX as an installed dashboard application after registration.");

        EVRApplicationError overlayPropertyError = EVRApplicationError.None;
        bool isDashboardOverlay = applications.GetApplicationPropertyBool(
            ApplicationKey, EVRApplicationProperty.IsDashboardOverlay_Bool, ref overlayPropertyError);
        if (overlayPropertyError == EVRApplicationError.None && !isDashboardOverlay)
            throw new InvalidOperationException("SteamVR registered MotionSFX, but did not identify it as a dashboard overlay.");

        EVRApplicationError autoLaunchError = applications.SetApplicationAutoLaunch(ApplicationKey, true);
        if (autoLaunchError != EVRApplicationError.None)
            throw new InvalidOperationException("SteamVR could not enable MotionSFX auto-launch (" + autoLaunchError + ").");

        if (!applications.GetApplicationAutoLaunch(ApplicationKey))
            throw new InvalidOperationException("SteamVR did not retain the MotionSFX auto-launch setting.");

        AppLog.Write("INFO", "SteamVR startup registration is active. MotionSFX is registered as a dashboard overlay and auto-launch is enabled. Manifest: " + manifestPath);
    }

    private static string GetRegisteredExecutablePath(CVRApplications applications)
    {
        StringBuilder value = new(2048);
        EVRApplicationError error = EVRApplicationError.None;
        applications.GetApplicationPropertyString(
            ApplicationKey,
            EVRApplicationProperty.BinaryPath_String,
            value,
            (uint)value.Capacity,
            ref error);
        return error == EVRApplicationError.None ? value.ToString() : string.Empty;
    }
}
