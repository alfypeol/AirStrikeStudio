"""Round-trip tests for the Script Studio toolchain (scrasm / scrdec / scrsrc / scrcomp).

Run from the ScriptStudio folder:

    python -m unittest discover -s tests -v

Always tests the bundled samples. To test a whole corpus (for example the 391
original scripts, or the Airstrike 2 / Gulf Thunder folders), point the
AS3D_SCRIPTS environment variable at a folder; every RCSL file below it is
checked for a byte-exact disassemble -> assemble round trip:

    set AS3D_SCRIPTS=C:\\path\\to\\scripts
    python -m unittest discover -s tests -v
"""
import os
import sys
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

import scrasm      # noqa: E402
import scrsrc      # noqa: E402

SAMPLES = os.path.join(ROOT, "samples")


def read(path):
    with open(path, "rb") as fh:
        return fh.read()


def rcsl_files(folder):
    for dirpath, _dirs, names in os.walk(folder):
        for name in sorted(names):
            path = os.path.join(dirpath, name)
            with open(path, "rb") as fh:
                if fh.read(4) == b"RCSL":
                    yield path


class SampleRoundTrip(unittest.TestCase):
    """bot1.scr / bot1.c ship with the project and must always round-trip."""

    def setUp(self):
        self.data = read(os.path.join(SAMPLES, "bot1.scr"))
        self.source = read(os.path.join(SAMPLES, "bot1.c")).decode("utf-8")

    def test_header(self):
        self.assertEqual(self.data[:4], b"RCSL")

    def test_disassemble_assemble_is_byte_exact(self):
        self.assertEqual(scrasm.assemble(scrasm.disassemble(self.data)), self.data)

    def test_decompile_then_compile_is_byte_exact(self):
        src = scrsrc.decompile_to_source(self.data, verify=True)
        self.assertIsNotNone(src, "decompiler should structure this script")
        self.assertEqual(scrsrc.compile_source(src), self.data)

    def test_shipped_source_compiles_to_shipped_script(self):
        self.assertEqual(scrsrc.compile_source(self.source), self.data)

    def test_disassembly_is_stable(self):
        once = scrasm.disassemble(self.data)
        twice = scrasm.disassemble(scrasm.assemble(once))
        self.assertEqual(once, twice)


class BadInput(unittest.TestCase):
    """Garbage must fail with a library exception (or be handled), never corrupt output."""

    def test_not_a_script(self):
        with self.assertRaises(Exception):
            scrasm.disassemble(b"this is not a script")

    def test_truncated_script_does_not_roundtrip_silently(self):
        data = read(os.path.join(SAMPLES, "bot1.scr"))
        cut = data[: len(data) // 2]
        try:
            ok = scrasm.assemble(scrasm.disassemble(cut)) == cut
        except Exception:
            ok = False  # raising is acceptable; claiming success is not
        self.assertFalse(ok)

    def test_bad_assembly_text_raises_asm_error(self):
        with self.assertRaises(scrasm.AsmError):
            scrasm.assemble("this is not assembly\n")


@unittest.skipUnless(os.environ.get("AS3D_SCRIPTS"), "set AS3D_SCRIPTS to a scripts folder to run")
class CorpusRoundTrip(unittest.TestCase):
    def test_every_script_assembly_roundtrips(self):
        folder = os.environ["AS3D_SCRIPTS"]
        tested, failures = 0, []
        for path in rcsl_files(folder):
            tested += 1
            data = read(path)
            try:
                if scrasm.assemble(scrasm.disassemble(data)) != data:
                    failures.append((path, "bytes differ"))
            except Exception as ex:  # noqa: BLE001 - report every file, not just the first
                failures.append((path, "%s: %s" % (type(ex).__name__, ex)))
        self.assertGreater(tested, 0, "no RCSL scripts found in %s" % folder)
        report = "\n".join("  %s: %s" % (os.path.relpath(p, folder), why) for p, why in failures[:25])
        self.assertEqual(failures, [], "%d of %d scripts failed:\n%s" % (len(failures), tested, report))


if __name__ == "__main__":
    unittest.main()
