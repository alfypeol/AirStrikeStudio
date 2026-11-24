#!/usr/bin/env python3
"""
scrstudio.py - Script Studio: desktop editor for Air Strike 3D / Airstrike 2 compiled .scr scripts.

Put this file in the same folder as scrasm.py, scrcomp.py, scrdec.py, scrdis.py, scrsig.py, scrsrc.py,
scrcore.py and scrstudio_gui.py and run:

    python scrstudio.py [file.scr | folder]

Everything is done by your existing toolchain; the window itself lives in scrstudio_gui.py.
  Source tab    C-like source (scrsrc).  F5 compiles it to a script.
  Assembly tab  scrasm text.  F5 assembles it (use this for hand-patched scripts the decompiler cannot structure).
  Diff tab      assembly difference between the original file and the current build.
  Strings tab   the string table of the current build.
  Info tab      header facts (slots, entries, globals, functions...).
  Tools > Batch check   round-trips every script in a folder and lists exact / hand-patched / failed.
"""
import sys

# Core functions stay importable from scrstudio for existing scripts (scrstudio.check_file, ...).
from scrcore import *  # noqa: F401,F403
from scrcore import _norm_labels  # noqa: F401


def run_gui(start=None):
    """Import the GUI lazily so headless machines (no tkinter) can still use scrcore / this module."""
    import scrstudio_gui
    scrstudio_gui.run(start)


if __name__ == "__main__":
    run_gui(sys.argv[1] if len(sys.argv) > 1 else None)
