<p align="center">
    <picture>
<img width="1280" height="440" alt="M" src="https://github.com/user-attachments/assets/a3728533-bc95-4fe1-80c6-9e2330c56c03" />
    </picture>
  
# MotionSFX
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


Logs are written to:

`%APPDATA%\MotionSFX\motionsfx.log`

If the receiver reports no traffic, verify SlimeVR's destination IP and Port Out. A VMC identity count is not a physical IMU count: SlimeVR's VMC output contains computed tracker transforms, not a physical sensor inventory.

