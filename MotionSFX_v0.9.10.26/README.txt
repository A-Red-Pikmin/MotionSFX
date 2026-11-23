MotionSFX Alpha 0.9.10.26 - Native C# Rewrite + SteamVR Dashboard
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
6. After a successful build, BUILD_NATIVE.cmd automatically launches MotionSFX for its first run.
7. The build window displays a 5-to-1 countdown, then closes automatically. Build errors remain visible and wait for a key press.
8. Later, open the desktop shortcut or run publish\MotionSFX.exe directly.

The build is self-contained and single-file for win-x64. A small C# build-time utility uses the native Windows Shell Link COM interface to create the desktop shortcut; it does not invoke PowerShell and is not included in the published application. The executable contains the .NET runtime, so the target PC does not need a separate .NET Desktop Runtime installed. The small Assets and Config folders are copied next to the executable because they contain the supplied app logo/icon and default configuration templates. The matching OpenVR native library is restored from NuGet and bundled into the single-file publish output. User settings and presets are stored separately in:
%APPDATA%\MotionSFX\config

STEAMVR DASHBOARD AND LIVE GRAPHS (CURRENT IN v0.9.10.26)
- The desktop also includes a Live View tab showing the same seven graph histories as the preset pages and SteamVR dashboard.
-------------------------------------------------------
- Increased the SteamVR dashboard panel width from 1.75 to 1.9 metres.
- Restored the desktop application window to its original fixed 1280 x 720 size.
- The dashboard render canvas remains 1000 x 760; its content layout and pointer coordinate mapping are unchanged.
- The main dashboard overlay uses one persistent Direct3D 11 DEFAULT display texture plus a CPU-writable DYNAMIC upload texture. MotionSFX maps the upload texture with WRITE_DISCARD and copies it into the stable display texture; OpenVR attaches the display texture once rather than re-importing it on every frame.
- Active dashboard content is refreshed at up to 20 FPS, matching the existing 50 ms dashboard pump, instead of the previous one-frame-per-second limit.
- All seven live movement graphs are displayed on a new Live Graphs page inside the SteamVR dashboard.
- The desktop app retains its four main tabs; no desktop Live View tab is added.
- The SteamVR graphs use the same 10-second position/rotation graph histories and thresholds as the Movement Presets pages.

STEAMVR DASHBOARD FIXES IN v0.9.10.1
-----------------------------------
- Controller pointer coordinates are converted from OpenVR's bottom-left origin to
  WPF's top-left origin before dashboard hit testing. Button-release events use their
  own coordinates, so a prior pointer-move event is not required for a click.
- The dashboard's physical width is 1.9 metres. Its 1000 x 760 render canvas and
  visual layout are unchanged.
- The ALPHA version label is placed below the subtitle so it no longer collides with
  the Connected/Detected status badge.

WINDOW TITLE BAR THEME IN v0.9.10.6
-----------------------------------
- The native Windows title bar uses a dark-purple caption and white text. The standard Windows frame border is retained.
- This uses Desktop Window Manager caption-color attributes supported on Windows 11 build 22000 and newer.
- The standard Windows caption buttons and window behavior are preserved. On unsupported Windows versions, the system title bar remains unchanged.

STEAMVR LIVE GRAPHS IN v0.9.10.20
--------------------------------
- A Live Graphs page in the SteamVR dashboard displays all seven position/rotation graphs together.
- It reuses the graph histories and detector measurements shown on each individual Movement Presets page; it does not create a second data pipeline.

STEAMVR AUTO-START REGISTRATION IN v0.9.10.6
---------------------------------------------
- When MotionSFX successfully connects to a running SteamVR instance, it writes
  a persistent application manifest to:
  %APPDATA%\MotionSFX\SteamVR\MotionSFX.vrmanifest
- MotionSFX registers that manifest through OpenVR as a dashboard overlay and
  enables SteamVR's application auto-launch flag. SteamVR can then launch
  MotionSFX automatically when the SteamVR runtime starts.
- Run MotionSFX manually once while SteamVR is running to complete the initial
  registration. After that, SteamVR can start MotionSFX on subsequent launches.
- To manage or disable automatic startup, open SteamVR Settings > Startup / Shutdown
  > Choose Startup Overlay Apps and change MotionSFX there.
- If MotionSFX is moved to a different folder, start the new copy once while
  SteamVR is running. It refreshes the stored executable path automatically.
- This is a SteamVR Startup Overlay App registration, not a SteamVR driver
  installation under Manage Add-ons. The existing dashboard and VMC processing
  remain inside the same MotionSFX.exe process.

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
  dashboard panel is 1.9 metres wide. Frames are rendered only while its tab is
  active and at up to 20 FPS when dashboard data changes.
- The dashboard main image uses a persistent Direct3D 11 DEFAULT display texture
  and a separate DYNAMIC upload texture. Each refresh maps the upload texture with
  WRITE_DISCARD and copies its pixels into the stable display texture. OpenVR is
  given the stable display texture once; only the thumbnail loads from the supplied
  logo file.
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
- https://github.com/ValveSoftware/openvr/wiki/IVROverlay::SetOverlayRaw
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

CUSTOM MOVEMENT SENSITIVITY IN v0.9.10.6
----------------------------------------
- Each of the seven movement preset pages offers High, Normal, Low, and Custom.
- Selecting Custom reveals independent position and rotation threshold fields.
- Position is measured in VMC world-space units; rotation is measured in degrees.
- Valid position threshold: 0.001 to 2.0 units. Valid rotation threshold: 0.1 to 180 degrees.
- The values are saved separately for each preset in %APPDATA%\MotionSFX\config\presets.json.
- Smaller thresholds make movement easier to trigger. Larger thresholds require more movement.

FEATURES RETAINED
-----------------
- Native WPF dark interface and supplied brand assets.
- VMC tracker output list, skeleton-bone list, status indicators and counters.
- Raw VMC identities remain distinct from body-role assignments.
- HMD, controller, tracker, global and local variants merge by raw identity.
- Seven separate movement preset pages.
- Read-only hotkey capture boxes; Escape cancels and Backspace/Delete clears.
- High/Normal/Low thresholds: 0.10 units / 6 degrees, 0.15 / 10 degrees,
  and 0.20 / 17 degrees. Each movement preset also has a Custom option with
  independently saved position (0.001-2.0 units) and rotation (0.1-180 degrees)
  thresholds. Smaller threshold values make a preset trigger more easily.
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


LIVE MOVEMENT SENSITIVITY PREVIEW (0.9.10.10)
-------------------------------------------
The Movement Presets tab includes a live preview for all seven presets. Position
distance (VMC units) and rotation change (degrees) are shown separately as
progress bars against the active sensitivity thresholds. The detector's trigger
condition remains unchanged: either the position threshold OR the rotation
threshold being reached satisfies the movement threshold test.

The preview reuses the existing tracker assignments, relative reference roles,
movement measurements, sample freshness rules, sensitivity settings and global
keybind delay. Enabled presets display the exact anchor measurements already
used by the detector. Disabled or send-failed presets use preview-only anchors
so monitoring them cannot alter the detector's retained trigger anchors. The
preview itself never sends shortcuts or triggers presets. A successful send,
a global-delay block, a disabled preset, a missing/stale tracker, or a stopped
receiver are identified separately. The current build has no separate arm
switch; stopping VMC reception is treated as disarmed. Preview UI updates at
the existing 4 Hz status refresh cadence.

Preview readings reflect the saved/active preset configuration used by the
detector. Unsaved edits in the preset controls do not change movement detection
until Save changes is pressed.


INDIVIDUAL LIVE MOVEMENT GRAPHS (0.9.10.10)
-----------------------------------------
Each Movement Presets page now has its own live movement graph on the right.
The graph shows a ten-second trace for position and rotation as separate lines.
The horizontal dashed line marks 100% of each measurement's own configured
threshold; the two measurements are normalized independently for plotting only.
The raw position (units), rotation (degrees), and their current thresholds remain
shown above the graph. The graph reads the existing movement preview snapshots
at the normal 4 Hz UI refresh and does not run a separate detector or receiver.



UI REFINEMENTS (0.9.10.10)
--------------------------
- Test keybind buttons appear beneath each preset enable checkbox.
- All text-bearing buttons use bold text.
- Each preset preview uses one outer graph frame and has independent position and rotation axes.
- Position axis labels show actual VMC units; rotation axis labels show degrees. Both axes are calculated from that preset's active High, Normal, Low, or Custom thresholds.
- Graph labels and plot bounds were adjusted to fit the preview panel without clipping.
