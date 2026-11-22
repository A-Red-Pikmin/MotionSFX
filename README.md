<p align="center">
    <picture>
<img width="1" height="440" alt="M" src="https://github.com/user-attachments/assets/a3728533-bc95-4fe1-80c6-9e2330c56c03" />
    </picture>
  
# MotionSFX Alpha v0.9.9.0
MotionSFX is a C# Vibecoded project that translates SlimeVR FBT movement into a native Windows keybind set.
This program requires the .NET 10 SDK for Windows and currently does not support Linux.

## Build

1. Install the [.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Clone or download this repository.
3. Run `BUILD_NATIVE.cmd`.
4. The finished standalone executable is `publish\MotionSFX.exe`.
5. Run `publish\MotionSFX.exe` (or use `RUN_NATIVE.cmd`).

The build is self-contained and single-file for `win-x64`. The executable contains the .NET runtime, so the target PC does not need a separate .NET Desktop Runtime installed. The small `Assets` and `Config` folders are copied next to the executable because they contain the supplied app logo/icon and default configuration templates. User settings and presets are stored separately in:

`%APPDATA%\MotionSFX\config`

## SlimeVR configuration

In SlimeVR's VMC settings, set the destination to `127.0.0.1` and Port Out to `39539`. Press **Start receiving** in MotionSFX.

## Lightweight runtime design

- UDP receive and OSC decoding run on a dedicated below-normal-priority C# thread. No socket reads or parsing happen on the WPF UI thread.
- Motion samples are rate-limited to approximately 30 Hz per transform/space.
- The motion queue is bounded at 8,192 samples; oldest samples are dropped during sustained overload instead of allowing unbounded memory growth.
- The UI polls at 20 Hz, processes at most the newest 150 motion samples per tick, and refreshes text/status data at 4 Hz.
- Tracker and skeleton tables refresh once per second only when the Trackers tab is visible.
- SteamVR process detection is cached and checked every 10 seconds.
- Log history is capped at 500 display lines and 60,000 textbox characters.

## Features retained

- Native WPF dark interface and supplied brand assets.
- VMC tracker output list, skeleton-bone list, status indicators and counters.
- Raw VMC identities remain distinct from body-role assignments.
- HMD, controller, tracker, global and local variants merge by raw identity.
- Seven separate movement preset pages.
- Read-only hotkey capture boxes; Escape cancels and Backspace/Delete clears.
- High/Normal/Low thresholds: 0.10 units / 6 degrees, 0.15 / 10 degrees, and 0.20 / 17 degrees.
- Relative movement: legs from Hip; Head and hands from Chest; Chest from Hip.
- Reference transforms are paired within 75 ms.
- Configurable global keybind activation delay from 0 to 10,000 ms.
- Win32 SendInput for keyboard shortcuts.
- Atomic JSON saves and migration of unambiguous legacy tracker-role entries.
- Reset body roles without deleting detected rows or movement preset settings.

## Performance and testing notes

The source contains a VMC/OSC parser self-test that runs during app startup. The project was rewritten and statically reviewed in a non-Windows build container. That environment cannot compile or execute WPF, Win32 SendInput, or Windows UDP loopback reception. Build and run on Windows using the steps above, then test with SlimeVR before relying on movement triggers.

Logs are written to:

`%APPDATA%\MotionSFX\motionsfx.log`

If the receiver reports no traffic, verify SlimeVR's destination IP and Port Out. A VMC identity count is not a physical IMU count: SlimeVR's VMC output contains computed tracker transforms, not a physical sensor inventory.

