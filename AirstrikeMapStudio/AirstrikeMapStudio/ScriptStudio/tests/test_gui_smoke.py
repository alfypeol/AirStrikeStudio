"""Smoke test for the Script Studio window: opens a script, builds from source and assembly,
verifies, and exports. Skipped automatically when tkinter or a display is unavailable
(on a headless Linux box run it as:  xvfb-run -a python -m unittest discover -s tests -v)."""
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

try:
    import tkinter as tk
    root = tk.Tk()
    root.destroy()
    HAVE_TK = True
except Exception:  # no tkinter module, or no display
    HAVE_TK = False


@unittest.skipUnless(HAVE_TK, "tkinter / display not available")
class StudioSmoke(unittest.TestCase):
    def setUp(self):
        import scrstudio_gui
        from tkinter import messagebox
        self.gui = scrstudio_gui
        # a modal dialog would hang the test run: record instead of showing
        self.dialogs = []
        for name in ("showerror", "showinfo", "showwarning"):
            setattr(messagebox, name, lambda *a, _n=name, **k: self.dialogs.append((_n, a)))
        self.root = tk.Tk()
        self.gui.MONO = self.gui.tkfont.Font(family="Courier", size=10)
        self.app = self.gui.Studio(self.root)
        self.sample = os.path.join(ROOT, "samples", "bot1.scr")

    def tearDown(self):
        self.root.destroy()

    def test_open_script_reports_exact(self):
        self.app.open_start(self.sample)
        self.assertEqual(self.app.status, "exact")
        self.assertIn("routine", self.app.src.get())
        self.assertEqual(self.dialogs, [])

    def test_build_from_source_is_byte_identical(self):
        self.app.open_start(self.sample)
        self.app.nb.select(0)
        self.assertTrue(self.app.build())
        self.assertEqual(self.app.cur, self.app.orig)

    def test_build_from_assembly_is_byte_identical(self):
        self.app.open_start(self.sample)
        self.app.nb.select(1)
        self.assertTrue(self.app.build())
        self.assertEqual(self.app.cur, self.app.orig)

    def test_broken_source_reports_error_and_keeps_original(self):
        self.app.open_start(self.sample)
        self.app.nb.select(0)
        self.app.src.set("routine {{{ this is not valid")
        self.assertFalse(self.app.build())
        self.assertEqual(self.app.cur, self.app.orig)
        self.assertTrue(any(kind == "showerror" for kind, _ in self.dialogs))

    def test_export_all_writes_source(self):
        from scrcore import export_all
        with tempfile.TemporaryDirectory() as out:
            written, skipped = export_all([self.sample], out, "source")
            self.assertEqual(written, 1)
            self.assertEqual(list(skipped), [])
            self.assertTrue(os.path.exists(os.path.join(out, "bot1.c")))

if __name__ == "__main__":
    unittest.main()
