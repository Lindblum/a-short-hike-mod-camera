# A Short Hike – Camera Modes & 3D

A BepInEx mod for *A Short Hike* that adds player-controlled camera modes and half side-by-side stereo 3D.

![3rd-person camera](docs/screenshot.png)

## Features

- **Camera modes:** press **R3** (or **F5**) to cycle **auto → 3rd person → 1st person**.
  - **auto:** the game's original camera.
  - **3rd person:** orbit Claire with the right stick. The camera pulls in when terrain is in the way.
  - **1st person:** look from Claire's head with the right stick. Her model is hidden but still casts a shadow.
  - The game's cutscene, fishing and similar cameras still take over when needed.
- **3D (half SBS):** toggle with **Options → Graphics → 3d**. Each eye is a full render, so outlines and colour correction work in 3D. HUD and text appear in both eyes at screen depth.
- Mode changes are announced in the game's own top-left status box.

## Install

1. Install [BepInEx 5 (x86)](https://github.com/BepInEx/BepInEx/releases) into the game folder (next to `AShortHike.exe`) and run the game once.
2. In `BepInEx\config\BepInEx.cfg`, set `HideManagerGameObject = true`.
3. Copy `ShortHikeCamera.dll` into `BepInEx\plugins\`.

## Settings

`BepInEx\config\local.ashorthike.cameramodes.cfg` (reloaded live while the game runs):

| Section | Setting | Default | |
|---|---|---|---|
| Controls | `CycleKey` | F5 | Keyboard key for cycling modes |
| Controls | `YawSpeed` / `PitchSpeed` | 180 / 120 | Look speed (°/s) |
| Controls | `InvertY` | false | |
| ThirdPerson | `Distance` | 10 | Camera distance |
| ThirdPerson | `PivotHeight` | 2.2 | Orbit point height above feet |
| ThirdPerson | `FieldOfView` | 50 | |
| FirstPerson | `EyeHeightOffset` | 0 | Added to head height |
| FirstPerson | `FieldOfView` | 70 | |
| FirstPerson | `HidePlayerModel` | true | |
| Stereo3D | `Mode` | Off | `Off` / `HalfSBS` |
| Stereo3D | `EyeSeparation` | 0.1 | Higher = stronger 3D |
| Stereo3D | `Convergence` | 10 | Distance at screen depth |
| Stereo3D | `SwapEyes` | false | Use if depth looks inverted |

## Build

Requires the .NET SDK. `GamePath` in `ShortHikeCamera.csproj` must point to your install.

```
dotnet build -c Release
```

This builds the plugin and copies it into the game's `BepInEx\plugins` folder.

## Known limitations

- Some interiors only hide their roof when the game's own camera is active, so use auto there.
- In 3D the mouse cursor doesn't line up with menus. Use a controller.
- Keyboard/mouse look is tuned for a gamepad stick.

## Uninstall

Delete `BepInEx\plugins\ShortHikeCamera.dll`. To remove BepInEx entirely, also delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and the `BepInEx` folder.
