# QoLBar Glamour Preview

A Dalamud companion plugin that shows screenshots from **Glamourer Preview Manager (GPM)** when you hover matching text shortcuts in **QoLBar**.

Settings command: **`/qgp`** (legacy alias: **`/qolglampreview`**).

For example, hovering a QoLBar button named `Beach 2` displays the screenshot assigned to the Glamourer design named `Beach 2` in GPM.

## Requirements

- Dalamud API 15
- QoLBar
- Glamourer
- Glamourer Preview Manager
- Text-based QoLBar shortcut names that match the Glamourer design names

No QoLBar modification is required. The plugin observes text buttons only while they are being drawn inside a QoLBar window or one of its nested category popups.

## Build

1. Install the .NET 10 SDK.
2. Make sure XIVLauncher/Dalamud is installed and the current developer Dalamud files exist at:
   `%APPDATA%\XIVLauncher\addon\Hooks\dev\`
3. Open `QoLBarGlamourPreview.csproj` in Visual Studio or Rider.
4. Build the `Release` configuration.
5. The Dalamud packager should create `latest.zip` under the Release output directory.

You can also run:

```powershell
./build.ps1
```

## Developer-plugin installation

1. Open `/xlsettings` in game.
2. Enable Dalamud developer options if needed.
3. Add the output folder containing `QoLBarGlamourPreview.dll` as a developer plugin location.
4. Load **QoLBar Glamour Preview**.
5. Open settings with `/qgp` (legacy alias: `/qolglampreview`).

## How preview resolution works

The plugin:

1. Reads GPM's `PreviewsFolderPath` from its configuration.
2. Reads GPM's `allocation.json` mapping of Glamourer design UUIDs to image filenames.
3. Reads Glamourer's design JSON files to map those UUIDs to names.
4. Matches the QoLBar button label to that design name, case-insensitively.

It rescans periodically, so replacing a GPM screenshot or adding a new design does not require rebuilding the plugin.

## Limitations

- Only text buttons are resolvable. A button displayed solely as a custom/game icon has no design name available to this companion plugin.
- It uses native cimgui hooks, the same general technique used by GPM. A future Dalamud/cimgui change can require an update.
- Duplicate Glamourer design names are ambiguous; the last matching design discovered wins.

## Licensing and attribution

Licensed under AGPL-3.0-or-later.

The native cimgui hook approach and GPM storage layout were studied from the public Glamourer Preview Manager project by KaraRemy. No GPM binary or preview image is included.

## GitHub custom-repository publishing

A ready-to-run workflow is included at `.github/workflows/build-release.yml`.
It downloads the current Dalamud developer files, builds the plugin, creates a GitHub release containing `latest.zip`, and writes `pluginmaster.json` back to the repository root.

See `GITHUB_SETUP.md` for the exact setup steps.

## AI assistance disclosure

The initial implementation and repository automation were created with substantial AI assistance and require maintainer review and in-game testing before wider distribution.
