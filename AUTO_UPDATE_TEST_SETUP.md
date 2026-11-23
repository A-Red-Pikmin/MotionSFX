# MotionSFX automatic-update test (v0.9.10.2 → v0.9.10.3)

The updater downloads compiled Windows runtime packages, not source ZIPs.
GitHub Releases is the download location. GitHub Actions is the Windows build
machine that creates the runtime ZIP and attaches it to a release.

## One-time test setup

1. Upload `MotionSFX_v0.9.10.3_SOURCE.zip` to the repository root and commit it to `main`.
2. Open the repository's **Releases** page and choose **Draft a new release**.
3. Enter tag `v0.9.10.3`. Create the tag from the `main` branch.
4. Save the release as a **draft**. Do not publish it yet.
5. Open the repository's **Actions** tab.
6. Select **Build Windows Update Package** and click **Run workflow**.
7. Enter `v0.9.10.3` in the `release_tag` input and run the workflow.
8. Wait until the workflow run is green. Return to the draft release and verify
   that its Assets list contains `MotionSFX-win-x64.zip`.
9. Only then publish the release.

The workflow checks out the release tag, extracts the root-level source archive,
compiles the main app and native updater on a GitHub-hosted Windows runner,
validates required files, and attaches the runtime ZIP to the draft release.

## Test the update on Windows

1. Keep the installed v0.9.10.2 version as the starting version.
2. Launch `MotionSFX.exe` from the v0.9.10.2 install.
3. The app should find the newer stable v0.9.10.3 release and show:
   `Update Found. Would you like to Update now?`
4. Select **Yes**. The app downloads the runtime asset, checks its SHA-256
   digest, starts the separate updater helper, exits, and is replaced/relaunched.
5. Verify the desktop app and SteamVR dashboard show v0.9.10.3.
6. Launch it again. It should not offer the same update again.

Select **No** to verify that the old version starts normally. If GitHub or the
release asset is unavailable, the current installed version should still start.

Logs:
- `%APPDATA%\MotionSFX\motionsfx.log`
- `%APPDATA%\MotionSFX\updater.log`

User settings are stored under `%APPDATA%\MotionSFX\config` and are not part
of the runtime package.
