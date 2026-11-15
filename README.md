# AirStrike 3D Map Studio & Script Editor

[![Latest Release](https://img.shields.io/github/v/release/alfypeol/AirStrikeStudio?color=brightgreen&logo=github)](https://github.com/alfypeol/AirStrikeStudio/releases/latest)
[![Total Downloads](https://img.shields.io/github/downloads/alfypeol/AirStrikeStudio/total?color=blue&logo=github)](https://github.com/alfypeol/AirStrikeStudio/releases)
[![Engine Support](https://img.shields.io/badge/Engines-OpenAirStrike%20%7C%20Original%20Trilogy-blueviolet.svg?logo=c%2B%2B)](https://github.com/alfypeol/AirStrikeStudio#supported-game-engines)
[![Headless Verification](https://img.shields.io/badge/Verification-Headless%20Simulation%20(F7)-brightgreen.svg)](https://github.com/alfypeol/AirStrikeStudio#8-headless-map-integrity-verification-f7)
[![Script Studio](https://img.shields.io/badge/Script%20API-101%20Builtins%20IntelliSense-blue.svg)](https://github.com/alfypeol/AirStrikeStudio#6-script-studio-scr-ide--101-builtin-intellisense)
[![Platform](https://img.shields.io/badge/platform-Windows%20x86-orange.svg)]()
[![OpenGL](https://img.shields.io/badge/OpenGL-Hardware%20Accelerated-green.svg)]()
[![License](https://img.shields.io/badge/license-MIT-yellow.svg)](LICENSE)

**AirStrike 3D Map Studio** is an advanced, high-performance 3D level editor, real-time terrain sculptor, Bézier flight route authoring suite, and script development environment designed for helicopter combat games.

It features **official Dual-Engine architecture**, providing seamless compatibility with both the modern **OpenAirStrike Native Win64 Rebuild** and the classic **DivoGames Trilogy**:
- **OpenAirStrike** (Native Win64 C++ Engine Rebuild)
- **AirStrike 3D** (2002)
- **AirStrike II** (2003)
- **AirStrike II: Gulf Thunder** (2004)

![AirStrike 3D Map Studio Interface](docs/images/editor_screenshot.png)

---

## Supported Game Engines

Map Studio offers full dual-engine support with zero configuration required. It automatically detects which engine you are running and tailors test execution, map verification, and scripting APIs accordingly:

| Feature | OpenAirStrike Native Rebuild (`as3d_game.exe`) | Original Trilogy (`AirStrike3D.exe` / `as3d2.exe` / `Gulf`) |
| :--- | :--- | :--- |
| **Architecture** | Native 64-bit C++ modern runtime | 32-bit Direct3D 8 classic runtime |
| **Test Runner (F6)** | **Direct CLI launch** (`--level <N> --god`), skips menus | Journaled `levels.txt` swap with auto-restore |
| **levels.txt Protection**| **100% untouched** (no file locking or modification) | Safe byte-exact journal backup & restore |
| **God Mode Option** | Supported via `--god` CLI flag | N/A (Standard gameplay rules) |
| **Headless Map Audit (F7)**| **Native 60–120 frame simulation** in < 1 second | N/A (Requires manual in-game loading) |
| **Script Builtins API** | **101 builtins** (Includes all sequel extensions) | 85 builtins (AS3D) / 101 builtins (AS2/Gulf) |
| **Sequel Functions** | `TerraMorph`, `RadialDamagePlayer`, `WaterHeight`, etc. | Sequels only (Crashes on vanilla AS3D) |
| **Modern Display** | Widescreen (16:9 / 21:9), High-DPI, 4K+ scaling | 4:3 fixed letterbox (DWM mitigation applied) |

---

## Key Features

### 1. Authentic 3D OpenGL Viewport
- **Engine-Accurate Perspective**: Fixed-pipeline hardware OpenGL reproducing the original game engine's projection ($60^\circ$ vertical FOV, 4:3 aspect ratio letterboxing).
- **Realistic Environment Simulation**: Real-time sinusoidal water wave animations, customizable distance fog, and skybox lighting.
- **Fluid Camera Controls**: Left-drag orbital rotation (Heading & Pitch), Right-drag ground panning, smooth wheel zoom, and `Ctrl + F` selection focus.

### 2. Interactive 3D Transform Gizmo
- **Precision Translators**: Red $+X$ and Green $+Y$ axis constraint handles with pixel-accurate screen-space raycasting and grid snapping.
- **Center Diamond**: Free planar movement across the terrain surface.
- **12-Sector Rotation Ring**: Radial tick marks for AirStrike's 12 facing sectors ($0^\circ, 30^\circ, 60^\circ, \dots, 330^\circ$) with an outward gold pointer and live model drag preview.
- **Single Undo Integration**: Transformations cleanly commit as a single undo step (`Ctrl + Z`).

### 3. Streamlined Single-Row Toolbar
- Consolidates 23 separate controls into a modern, uncluttered single row with dropdown sub-menus:
  - `Duplicate ▾`: Interactive clone stamp, instant `Clone +2, +2`, and precision offset translation.
  - `Transform ▾`: $30^\circ$ left/right facing rotations and multi-unit group rotation with route pivoting.
  - `Group ▾`: `Align / Space Selection…` (Align X, Align Y, Distribute X, Distribute Y), `Move Selection to…`, and batch `Shared Settings…`.

### 4. Bézier Route & Waypoint Authoring
- **Cubic Bézier Navigation**: Full visual flight paths with numbered waypoints, **Handle A (Cyan)** arrival tangents, and **Handle B (Coral Pink)** departure tangents.
- **Curve Algorithms**: One-click Catmull-Rom smoothing (`Smooth Bezier Curve`), route direction reversal, and path rotation.
- **Auto-Scaled Precise Settings**: Dynamic description auto-measurement ensuring parameter documentation is never truncated.
- **CSV Data Exchange**: Full route export and import with external spreadsheet tools.

### 5. 3D Terrain Sculpting & Heightmaps
- **Dynamic Brushes**: Real-time `Raise / Lower`, `Level / Flatten` (with Alt-click ground height sampling), `Smooth`, `Noise`, and 2-click `Ramp / Slope`.
- **Water Level Presets**: One-click brush targeting matching the map's exact water elevation ($Z = \text{WaterLevel}$) for carving lakes and rivers.
- **Heightmap Import / Export**: Lossless 8-bit / 16-bit grayscale PNG heightmap interchange with Photoshop, GIMP, Blender, and World Machine.

### 6. Script Studio (.scr) IDE & 101-Builtin IntelliSense
- **Lossless Decompiler**: Decompiles binary `.scr` files into clean, readable C-like high-level structured code.
- **Native C-Compiler**: Edit logic directly with `if/else`, `while`, mathematical functions, and unit triggers; compiles directly to verified RCSL bytecode on `F5`.
- **Complete 101-Builtin Database**: Full API coverage for both original AirStrike 3D (85 functions) and sequel trilogy extensions (16 functions).
- **IntelliSense Autocomplete (`Ctrl + Space`)**: Suggests matching functions with compatibility tags (`[AS3D]` vs `[Sequel]`) and parameter signatures.
- **Live Parameter Hints**: Typing `(` opens interactive signature and documentation tooltips.
- **Compatibility Linter**: Automatically warns when sequel-only builtins (like `TerraMorph` or `RadialDamagePlayer`) are used in scripts intended for original AirStrike 3D.

### 7. Dual-Engine Playtesting (Test Mission — F6)
- **OpenAirStrike Native Win64**: Direct CLI invocation (`--data "<gameRoot>" --level <N> --god`). Skips in-game menus, loads the test map instantly, and leaves `levels.txt` 100% untouched.
- **Legacy Game Trilogy**: Full compatibility with `AirStrike3D.exe`, `as3d2.exe`, and `AirStrike3D II - Gulf.exe` via byte-exact journal backup and restore with DWM mitigation.
- **One-Click God Mode**: Toggleable invulnerability for safe, thorough playtesting of dense enemy layouts and boss phases.

### 8. Headless Map Integrity Verification (F7)
- Powered by OpenAirStrike: Executes an automated 60–120 frame simulation in under 1 second without opening a 3D window.
- Audits terrain boundaries, player spawn points, and waypoint paths.
- Flags missing script files, missing 3D models, unmapped texture skins, and missing audio samples with a color-coded diagnostic report.

### 9. Bit-Exact Serialization
- All reads and writes preserve original object types, link values, scripting references, and unusual waypoint flags bit-for-bit.

---

## Visual Showcase

| Routes & Bézier Tangents | Waypoint Precise Inspector |
| :---: | :---: |
| ![Routes Authoring](docs/images/screenshot_routes.png) | ![Waypoint Inspector](docs/images/screenshot_waypoint_dialog.png) |
| *Visual flight curves with Handle A (Cyan) and Handle B (Coral Pink)* | *Auto-scaling property grid with complete, un-truncated help text* |

| Terrain Sculptor & 3D Brush | Object Palette & 3D Previews |
| :---: | :---: |
| ![Terrain Sculpting](docs/images/screenshot_terrain.png) | ![Object Palette](docs/images/screenshot_palette.png) |
| *Real-time elevation deformation with Water Level presets* | *Categorized unit library with live 3D thumbnail generation* |

| Mission Settings (F4) | Script Studio & Decompiler (Ctrl+Alt+S) |
| :---: | :---: |
| ![Mission Settings](docs/images/screenshot_mission_settings.png) | ![Script Studio](docs/images/screenshot_script_studio.png) |
| *Skybox, fog boundaries, water elevation, and player vehicle* | *Integrated C-like script editor, bytecode compiler & API catalog* |

---

## Documentation

Comprehensive documentation guides are included in the repository:
- **[Map Editor User Manual & Reference Guide](MAP_EDITOR_MANUAL.md)**: Complete guide to 3D navigation, selection tools, submenus, dialogs, terrain sculpting, road painting, and keyboard shortcuts.
- **[Script Studio Technical Reference & RCSL Guide](SCRIPT_STUDIO_GUIDE.md)**: Deep architectural guide to the virtual machine, unit struct fields (`self.f5`, `self.f34`), engine functions catalog, opcodes, and 4 practical tutorials (destructible barrels, convoy trucks, attack helicopters, multi-phase bosses).

---

## Building from Source

### Prerequisites
- Windows 7 / 8 / 10 / 11 (32-bit or 64-bit)
- Visual Studio 2017, 2019, 2022 (or .NET SDK with MSBuild)
- .NET Framework 4.7.2 Developer Pack
- GPU supporting OpenGL 1.1 or higher

### Build via Command Line
```powershell
# Build Release x86 binary
dotnet msbuild AirstrikeMapStudio.sln -p:Configuration=Release -p:Platform=x86

# Run automated test suites
bin\x86\Release\AirstrikeMapStudio.exe --self-test "C:\Path\To\AirStrike"
```

### Build via Visual Studio
1. Open `AirstrikeMapStudio.sln` in Visual Studio.
2. Select **Release** configuration and **x86** platform.
3. Press `Ctrl + Shift + B` to build.
4. Output executable will be in `AirstrikeMapStudio\bin\x86\Release\AirstrikeMapStudio.exe`.

---

## Keyboard Shortcuts Reference

| Shortcut | Function | Shortcut | Function |
| :--- | :--- | :--- | :--- |
| `Ctrl + N` | New Map | `Ctrl + F` | Focus Selection in 3D View |
| `Ctrl + O` | Open Map (`.hsc`) | `Home` | Reset Camera to Default Angle |
| `Ctrl + S` | Save Map | `[` / `]` | Rotate Facing $-30^\circ$ / $+30^\circ$ |
| `F1` | Help & Keyboard Guide | `Alt + Click` | Overlap Chooser / Sample Tile / Ground |
| `F4` | Script Studio (.scr editor) | `Ctrl + D` | Interactive Duplicate Stamp |
| `F5` | Map Checks & Diagnostics | `Del` | Delete Selected Object(s) |
| `F6` | **Playtest Map (Dual-Engine Runner)** | `F7` | **Verify Map Integrity (Headless)** |

---

## License

This project is licensed under the [MIT License](LICENSE).
