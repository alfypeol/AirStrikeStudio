# Architecture notes

## Map Studio (C#, `AirstrikeMapStudio/AirstrikeMapStudio`)

| Area | Files |
| :--- | :--- |
| Data model / formats | `Map.cs`, `MapWorld.cs`, `Assets.cs`, `Assembly.cs`, `Recovery.cs`, `WorkspacePreferences.cs` |
| 3D viewport (`GlTerrainControl`, one `partial` class) | `GlTerrainWindow.cs` and the other `Gl*.cs` files |
| Script support | `ScrFile.cs` (parser / disassembler / assembler / decompiler), `ScrCompiler.cs`, `EngineBuiltin.cs`, `EngineBuiltinCatalog.cs`, `ScriptStudioDialog.cs`, `GlTerrainWindow.ScriptStudio.cs` |
| Test launches / verification | `TestMission.cs`, `LauncherPlayback.cs`, `MapChecks.cs`, `Program.cs` (command-line modes) |
| Diagnostics | `AppLog.cs` |

### Error handling
Best-effort operations (temp-file cleanup, icon loading, cosmetic reflection tweaks) must not
use an empty `catch`. Use `catch (Exception ex) { AppLog.Warn(ex); }`; the calling file and
method are recorded automatically in `%LOCALAPPDATA%\AirstrikeStudio\studio.log`. Failures that
the user must act on (saving a map, restoring `levels.txt`) should additionally show a message.

## Script Studio (Python, `.../ScriptStudio`)
`scrstudio.py` (entry) -> `scrstudio_gui.py` (window) -> `scrcore.py` (no-GUI logic) -> `scrasm / scrdec / scrcomp / scrsrc / scrsig`.
Run the tests with `python -m unittest discover -s tests -v`.

## Keeping the C# and Python toolchains in step
The C# `ScrFile` was ported from the Python modules. `tools/compare_toolchains.py` exports assembly
with the Python tools and diffs it against the C# "Export all" output, so drift shows up as a failing comparison.

## Open refactoring work
* `GlTerrainControl` is still one class spread over ~28 files with shared mutable state. Move
  non-UI logic (selection, routes, sculpting, undo) into plain classes that can be unit-tested.
* Picking (`PickAt`) tests every triangle in the scene. Add a spatial index (grid or BVH) if large
  maps feel slow; profile first.
* Add a C# test project: load/save every `.hsc` in the stock maps byte-for-byte, and run
  `ScrFile` disassemble -> assemble over the script corpus.
