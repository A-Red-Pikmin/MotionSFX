MotionSFX Alpha 0.9.10.1 - Native C# Rewrite + SteamVR Dashboard
===========================================

This is a native C# Windows desktop application with a native OpenVR SteamVR
Dashboard tab. It does not use PowerShell, PowerShell scripts, a browser, an
embedded browser, a localhost web server, or an HTTP listener at runtime. The
desktop interface is WPF and the SteamVR tab is rendered through OpenVR. Both
interfaces run inside the existing MotionSFX.exe process.

BUILD
-----
1. Install the .NET 10 SDK for Windows x64:
   https://dotnet.microsoft.com/download/dotnet/10.0
2. Extract this complete folder.
3. Double-click BUILD_NATIVE.cmd.
4. The finished standalone executable is publish\MotionSFX.exe.
5. BUILD_NATIVE.cmd automatically creates or updates MotionSFX.lnk on your Windows Desktop.
6. Open the desktop shortcut to run MotionSFX, or run publish\MotionSFX.exe directly.

The build is self-contained and single-file for win-x64. A small C# build-time utility uses the native Windows Shell Link COM interface to create the desktop shortcut; it does not invoke PowerShell and is not included in the published application. The executable contains the .NET runtime, so the target PC does not need a separate .NET Desktop Runtime installed. The small Assets and Config folders are copied next to the executable because they contain the supplied app logo/icon and default configuration templates. The matching OpenVR native library is restored from NuGet and bundled into the single-file publish output. User settings and presets are stored separately in:
%APPDATA%\MotionSFX\config

STEAMVR DASHBOARD FIXES IN v0.9.10.1
-----------------------------------
- Controller pointer coordinates are converted from OpenVR's bottom-left origin to
  WPF's top-left origin before dashboard hit testing. Trigger-down activates controls;
  release is retained as a fallback if a press event was missed.
- The dashboard's physical width is 1.75 metres (previously 1.28 metres). Its 1000 x
  760 render canvas and visual layout are unchanged.
- The ALPHA version label is placed below the subtitle so it no longer collides with
  the Connected/Detected status badge.

STEAMVR DASHBOARD
-----------------
- Start SteamVR before or after MotionSFX. MotionSFX checks for the SteamVR
  runtime and registers a dashboard tab named MotionSFX when OpenVR is ready.
  Process detection is cached and checked every 10 seconds.
- Open the SteamVR dashboard with the headset system button and select the
  MotionSFX tab. The supplied Assets\MotionSFX-logo.png is used for both the
  dashboard thumbnail and the in-dashboard header logo.
- Overview displays receiver, tracker, preset and SteamVR status. Its Start
  and Stop controls operate the existing VMC receiver.
- Trackers displays up to six current VMC identities and their assigned roles.
- Presets toggles the existing seven movement presets and saves the changes
  to the same user configuration used by the desktop application.
- The dashboard does not open a second UDP socket or run another movement
  detection loop. It reads the existing MainWindow state and VMC receiver.
- OpenVR controller mouse events are polled by the existing 50 ms UI timer,
  with a 24-event limit per tick. OpenVR mouse coordinates are converted from
  bottom-left origin to the WPF bitmap top-left origin before hit testing. The
  dashboard panel is 1.75 metres wide. Dashboard PNG frames are rendered only while
  its tab is active and at most once per second to keep rendering overhead low.
- Each app process writes temporary dashboard frames to its own session folder,
  preventing filename collisions between separate launches.
- If SteamVR is not running or OpenVR cannot initialize, MotionSFX continues
  to work as a normal desktop app and retries the dashboard connection.

The integration uses Valve's OpenVR API and follows Valve's dashboard overlay
interface and helloWorldOverlay sample. The managed C# bindings and matching
native binary are supplied by the JeppDev.OpenVR 2.15.6 NuGet package; this
package is a community packaging project, not a Valve-maintained NuGet package.
The included THIRD_PARTY_NOTICES.txt carries the Valve OpenVR BSD 3-Clause
notice required for redistribution of the native library.

Official references:
- https://github.com/ValveSoftware/openvr/wiki/IVROverlay_Overview
- https://github.com/ValveSoftware/openvr/wiki/IVROverlay::CreateDashboardOverlay
- https://github.com/ValveSoftware/openvr/wiki/IVROverlay::SetOverlayFromFile
- https://github.com/ValveSoftware/openvr/tree/master/samples/helloworldoverlay
- https://valvesoftware.github.io/steamvr_unity_plugin/api/Valve.VR.CVROverlay.html

SLIMEVR CONFIGURATION
---------------------
In SlimeVR's VMC settings, set the destination to 127.0.0.1 and Port Out to
39539. Press Start receiving in MotionSFX.

LIGHTWEIGHT RUNTIME DESIGN
--------------------------
- UDP receive and OSC decoding run on a dedicated below-normal-priority C#
  thread. No socket reads or parsing happen on the WPF UI thread.
- Motion samples are rate-limited to approximately 30 Hz per transform/space.
- The motion queue is bounded at 8,192 samples; oldest samples are dropped
  during sustained overload instead of allowing unbounded memory growth.
- The UI polls at 20 Hz, processes at most the newest 150 motion samples per
  tick, and refreshes text/status data at 4 Hz.
- Tracker and skeleton tables refresh once per second only when the Trackers
  tab is visible.
- SteamVR process detection is cached and checked every 10 seconds.
- OpenVR overlay event polling reuses the existing 20 Hz UI timer; the dashboard
  only renders changed frames while its tab is active, limited to 1 Hz.
- Log history is capped at 500 display lines and 60,000 textbox characters.

FEATURES RETAINED
-----------------
- Native WPF dark interface and supplied brand assets.
- VMC tracker output list, skeleton-bone list, status indicators and counters.
- Raw VMC identities remain distinct from body-role assignments.
- HMD, controller, tracker, global and local variants merge by raw identity.
- Seven separate movement preset pages.
- Read-only hotkey capture boxes; Escape cancels and Backspace/Delete clears.
- High/Normal/Low thresholds: 0.10 units / 6 degrees, 0.15 / 10 degrees,
  and 0.20 / 17 degrees.
- Relative movement: legs from Hip; Head and hands from Chest; Chest from Hip.
- Reference transforms are paired within 75 ms.
- Configurable global keybind activation delay from 0 to 10,000 ms.
- Win32 SendInput for keyboard shortcuts.
- Atomic JSON saves and migration of unambiguous legacy tracker-role entries.
- Reset body roles without deleting detected rows or movement preset settings.

PERFORMANCE / TESTING NOTES
---------------------------
The source contains a VMC/OSC parser self-test that runs during app startup.
The project was rewritten and statically reviewed in a non-Windows build
container. That environment cannot compile or execute WPF, Win32 SendInput,
or Windows UDP loopback reception. Build and run on Windows using the steps
above, then test with SlimeVR before relying on movement triggers.

Logs:
%APPDATA%\MotionSFX\motionsfx.log

If the receiver reports no traffic, verify SlimeVR's destination IP and Port
Out. A VMC identity count is not a physical IMU count: SlimeVR's VMC output
contains computed tracker transforms, not a physical sensor inventory.
