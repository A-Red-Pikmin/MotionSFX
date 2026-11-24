LATEST UPDATE (v0.9.10.37)
- Removed the neutral-position/re-arm requirement. Each qualifying movement is handled immediately; after the attempt, the detector captures a fresh baseline, restoring the pre-v0.9.10.27 behavior.
- Kept the seven independent preset cooldowns and optional global activation delay. Cooldown expiry never creates a trigger by itself, and blocked movements are not queued.
- Restored the original 75 ms target/reference pairing window and added timestamp-aligned interpolation of reference position and quaternion orientation to reduce asynchronous head/chest and tracker/reference measurement error.
- Audited all seven preset mappings and routed every preset through the same Euclidean position and quaternion-angle calculations. Foot/leg targets use Hip; Head and hands use Chest; Chest uses Hip; Hip uses its incoming coordinate frame.
- Position readouts now identify the coordinate source: avatar-scale coordinates for VMC /Pos, or device-raw coordinates for /Pos/Local fallback. These are not guaranteed to be physical metres. Each preset prefers a complete GLOBAL target/reference pair and falls back to a complete LOCAL pair; the two coordinate spaces are never mixed.

BUILD OPTIONS (v0.9.10.32)
- The fixed desktop window is now 1280 x 860. No preset layout or movement behavior was changed.
- After a successful publish, BUILD_NATIVE.cmd asks whether to create or update a desktop shortcut. Choose Y to create/update MotionSFX.lnk or N to skip it.
- MotionSFX still launches automatically after a successful build, regardless of the shortcut choice.

WINDOW SIZE TEST (v0.9.10.31)
- Reduced the fixed desktop window from 1280 x 1000 to 1280 x 900. No preset layout or behavior was intentionally changed.

WINDOW SIZE TEST (v0.9.10.31)
- Increased the fixed desktop window from 1280 x 720 to 1280 x 1000 to test whether the additional vertical space prevents the Custom sensitivity controls from clipping. No other layout or behavior was intentionally changed.

BUILD FIX (v0.9.10.29)
- Fixed duplicate WPF MainWindow generated-code errors during the application build.
- The application now disables automatic recursive WPF Page inclusion and explicitly
  compiles only UI\MainWindow.xaml. This prevents the MainWindow.xaml copy created
  by the pre-build test harness under Tests\bin from being compiled as another page.
- Added a regression check for the one-page WPF project configuration and cleans root
  bin/obj/publish output before publishing.

MotionSFX Alpha 0.9.10.37 - Native C# Rewrite + SteamVR Dashboard
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
5. After publishing, BUILD_NATIVE.cmd asks whether to create or update MotionSFX.lnk on your Windows Desktop. Choose Y or N.
6. After a successful build, BUILD_NATIVE.cmd automatically launches MotionSFX for its first run, whether or not you created a shortcut.
7. The build window displays a 5-to-1 countdown, then closes automatically. Build errors remain visible and wait for a key press.
8. Later, open the desktop shortcut or run publish\MotionSFX.exe directly.
9. BUILD_NATIVE.cmd first runs the movement-trigger state-machine and configuration
   tests. If any test fails, the build stops before publishing the application.
10. The build uses detailed, plain-text MSBuild output. After each successful C# compilation,
    it lists the compiled source files individually with a `[COMPILED]` marker.
    This applies to the movement tests, MotionSFX application, and shortcut utility when used.

The build is self-contained and single-file for win-x64. A small C# build-time utility uses the native Windows Shell Link COM interface to create the desktop shortcut; it does not invoke PowerShell and is not included in the published application. The executable contains the .NET runtime, so the target PC does not need a separate .NET Desktop Runtime installed. The small Assets and Config folders are copied next to the executable because they contain the supplied app logo/icon and default configuration templates. The matching OpenVR native library is restored from NuGet and bundled into the single-file publish output. User settings and presets are stored separately in:
%APPDATA%\MotionSFX\config

DASHBOARD AND LIVE GRAPH FEATURES CARRIED FORWARD
-------------------------------------------------
- The desktop application window is fixed at 1280 x 800 in v0.9.10.37. Preset-page graphs are enlarged by 20%; the desktop Live View and SteamVR graph layouts remain unchanged.
- The SteamVR dashboard panel remains 1.9 metres wide with its 1000 x 760 render canvas.
- The dashboard uses one persistent Direct3D 11 DEFAULT display texture plus a
  DYNAMIC upload texture; the stable display texture is shared with OpenVR through
  the DXGI shared-handle path confirmed working in v0.9.10.25.
- Dashboard content refreshes at up to 20 FPS when visible content changes.
- SteamVR Live Graphs and the desktop Live View both display all seven movement
  graphs using the same 10-second histories and thresholds as the preset pages.
- The desktop includes Overview, Trackers, Movement Presets, Live View, and
  Diagnostics; SteamVR includes Overview, Trackers, Presets, and Live Graphs.

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


BUILD FIX (v0.9.10.28)
---------------------
- Added the explicit System.IO import required by the automated movement/configuration test harness.
- No runtime behavior changed from v0.9.10.27.

MOVEMENT TRIGGER TIMING (v0.9.10.27; neutral re-arming removed in v0.9.10.37)
--------------------------------------------------
- Each of the seven movement presets has an independent Individual Cooldown (ms)
  setting from 0 to 10000. A value of 0 disables that preset's cooldown.
- Individual cooldowns begin only after MotionSFX successfully sends an automatic
  movement shortcut. Manual Test keybinds, failed sends, disabled presets, and
  movements blocked by a delay do not start cooldown timers.
- The optional Global Activation Delay (ms), also 0 to 10000, limits the minimum
  interval between any two successfully sent automatic shortcuts. It is separate
  from each preset's individual cooldown. Existing saved global-delay values are
  preserved; new installations default to 0 (disabled).
- A qualifying movement consumes that preset's movement cycle, even if an
  individual cooldown or the global delay blocks the shortcut. Blocked movement
  is never queued and cannot fire when a timer later expires.
- v0.9.10.37 removes the neutral-position latch and 150 ms stabilization gate.
  A qualifying movement is evaluated immediately using the current baseline.
  After a successful, failed, or timing-blocked attempt, the current target sample
  becomes the new baseline, matching the earlier pre-hysteresis behavior.
- The individual preset cooldown and optional global activation delay remain
  independent safety timers. They do not wait for a neutral position and never
  queue a blocked movement for later activation. Successful automatic sends alone
  start the cooldown clocks; elapsed durations use monotonic Stopwatch timestamps.
- Movement Presets previews, desktop Live View, and SteamVR Live Graphs display
  the same runtime trigger state, timing restrictions, and graph histories.

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
