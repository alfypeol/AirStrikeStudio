Script Studio - desktop editor for Air Strike 3D / Airstrike 2 .scr scripts
==========================================================================

Run:      python scrstudio.py              (needs Python 3.8+ with tkinter; the python.org Windows installer has it)
          python scrstudio.py script.scr   or   python scrstudio.py C:\path\to\scripts
Build:    double-click build_exe.bat       -> dist\ScriptStudio.exe  (needs: pip install pyinstaller; the script uses "python -m PyInstaller" so PATH does not matter)

Keys:     F5 build (compile Source tab / assemble Assembly tab)   Ctrl+S save   Ctrl+O open   Ctrl+Shift+O open folder

How it works
  * Open a file or a folder. The list shows each script's status once opened or batch-checked:
        exact     source <-> script round-trips byte for byte: edit on the Source tab
        near      decompiler structured it but recompiling differs: check the Diff tab after building
        asm only  hand-patched code: edit on the Assembly tab
  * F5 builds the tab you are on. The other editor, the Diff, Strings and Info tabs refresh from the new build.
  * Save writes the built .scr. The first overwrite of a file keeps the original as <name>.scr.bak.
  * Tools > Batch check runs the round-trip over a whole folder and can save the result as CSV.
  * Tools > Export all writes .c (source) or .asm for every script in the folder.

scrstudio.py only adds the window; all decompiling/compiling is done by scrasm / scrcomp / scrdec / scrsig / scrsrc.
