# Advanced Land Development Tools – v1.0
### Civil 3D 2026 (.NET 8) Productivity Plugin

---

## Overview
A professional Civil 3D plugin that adds a dedicated **"Advanced Land Dev Tools"**
ribbon tab with four productivity tools for land development workflows.

### Commands

| Command | Tool | Description |
|---|---|---|
| `BULKSUR` | Bulk Surface Profile Creator | Batch-creates surface profiles and profile views for multiple alignments |
| `ALIGNDEPLOY` | Align Deploy | Deploys copies of a cross alignment along a main alignment at intervals |
| `PIPEMAGIC` | Pipe Magic | Detects crossing pipe networks and projects them into profile views |
| `INVERTPULLUP` | Invert Pull Up | Calculates invert elevation at any point along a pipe and labels it |

---

## File Structure

```
AdvancedLandDevTools/
│
├── AdvancedLandDevTools.csproj    ← .NET 8 / WPF project
├── AppLoader.cs                   ← IExtensionApplication (ribbon bootstrap)
├── PackageContents.xml            ← Bundle manifest for auto-load
│
├── Commands/
│   ├── AlignDeployCommand.cs      ← [CommandMethod("ALIGNDEPLOY")]
│   ├── BulkSurfaceProfileCommand.cs ← [CommandMethod("BULKSUR")]
│   ├── InvertPullUpCommand.cs     ← [CommandMethod("INVERTPULLUP")]
│   └── PipeMagicCommand.cs        ← [CommandMethod("PIPEMAGIC")]
│
├── Engine/
│   ├── AlignDeployEngine.cs       ← Alignment deployment logic
│   ├── BulkSurfaceProfileEngine.cs ← Profile/view creation API calls
│   ├── InvertPullUpEngine.cs      ← Invert calculation + label placement
│   └── PipeMagicEngine.cs         ← Pipe crossing detection + projection
│
├── Helpers/
│   └── StationParser.cs           ← "12+00" ↔ 1200.0 conversion
│
├── Models/
│   └── Models.cs                  ← AlignmentItem, NamedItem, BulkProfileSettings
│
├── Ribbon/
│   ├── RibbonBuilder.cs           ← Builds the ribbon tab with all tool buttons
│   └── RibbonCommandHandler.cs    ← ICommand that posts commands to AutoCAD
│
├── UI/
│   ├── BulkSurfaceProfileDialog.xaml / .xaml.cs
│   └── InvertPullUpDialog.xaml / .xaml.cs
│
└── Installer/
    └── AdvancedLandDevTools_Setup.iss  ← Inno Setup installer script
```

---

## Prerequisites

| Requirement | Version |
|---|---|
| Visual Studio | 2022 (17.x) |
| .NET SDK | 8.0 |
| Autodesk Civil 3D | **2026** (R26) |
| Windows | 10+ 64-bit |
| Inno Setup (for installer only) | 6.x |

---

## Build Instructions

### 1. Set the Civil 3D install path
Open `AdvancedLandDevTools.csproj` and verify:
```xml
<AcadDir>D:\AutoCAD 2026</AcadDir>
```
Or set as environment variable: `set ACADDIR=D:\AutoCAD 2026`

### 2. Build
```
dotnet build -c Release -p:Platform=x64
```

The post-build target automatically assembles the bundle:
```
Publish\AdvancedLandDevTools.bundle\
    PackageContents.xml
    Contents\
        AdvancedLandDevTools.dll
```

### 3. Build the Installer (optional)
1. Install [Inno Setup 6](https://jrsoftware.org/isdl.php)
2. Open `Installer\AdvancedLandDevTools_Setup.iss`
3. Click Build → Compile
4. Output: `Installer\Output\AdvancedLandDevTools_v1.0.0_Setup.exe`

---

## Installation

### Option A: Installer (recommended)
Run `AdvancedLandDevTools_v1.0.0_Setup.exe` — it copies the bundle to:
```
%APPDATA%\Autodesk\ApplicationPlugins\AdvancedLandDevTools.bundle\
```
Uninstall via Windows Settings → Apps.

### Option B: Manual bundle drop
1. Copy the entire `AdvancedLandDevTools.bundle` folder to:
   ```
   %APPDATA%\Autodesk\ApplicationPlugins\
   ```
2. Restart Civil 3D 2026 → the tab appears automatically.

### Option C: NETLOAD (development)
```
NETLOAD
```
Browse to `AdvancedLandDevTools.dll`.

---

## Autodesk App Store Submission (future)

When ready to publish, you need:
- [ ] Signed installer (the Inno Setup EXE)
- [ ] App icon: 128×128 and 256×256 PNG
- [ ] 3+ screenshots of the plugin in action
- [ ] Description and changelog
- [ ] Submit at https://apps.autodesk.com/

The `PackageContents.xml` and bundle structure already follow the App Store schema.

---

## Adding Future Tools

1. Add a new command in `Commands/` with `[CommandMethod("YOURCOMMAND")]`
2. Add the engine logic in `Engine/`
3. Add a WPF dialog in `UI/` if needed
4. Add a `RibbonButton` in `RibbonBuilder.cs`
5. No changes needed to `AppLoader.cs` or `PackageContents.xml`

---

## Known Civil 3D API Quirks

| Issue | Workaround |
|---|---|
| New ProfileView shows 0–5 elevation range | Create profile before the view, or set `UserSpecified` range immediately after creation |
| `ElevationMin`/`Max` throw on wrong order | Always set `ElevationRangeMode` first |
| `SplitProfileViewCreationOptions` locks elevation range | Use `CreateMultiple` without split mode instead |
| `StationOffsetLabel.Create` requires valid marker style | Never pass `ObjectId.Null` — always provide a real `MarkerStyles[n]` |
| Duplicate profile names crash `CreateFromSurface` | `MakeUniqueName()` in the engine handles this |

---

*Advanced Land Development Tools – v1.0 – Civil 3D 2026*
