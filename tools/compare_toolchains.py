#!/usr/bin/env python3
"""
Compare the Python and C# script toolchains so they cannot silently drift apart.

The C# Map Studio (ScrFile / ScrCompiler) was ported from the Python Script Studio. Run both over
the same scripts and diff the assembly they produce:

  1. Python side:   python tools/compare_toolchains.py export  <scripts-folder> <out-py>
  2. C# side:       Map Studio > Script Studio > Export all (assembly)  ->  <out-cs>
  3. Compare:       python tools/compare_toolchains.py compare <out-py> <out-cs>

`compare` lists every script whose .asm differs (ignoring line-ending and trailing-space
differences) and exits with status 1 if there are any.
"""
import difflib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "AirstrikeMapStudio", "AirstrikeMapStudio", "ScriptStudio"))

import scrasm  # noqa: E402


def export(scripts, out):
    os.makedirs(out, exist_ok=True)
    done = bad = 0
    for dirpath, _dirs, names in os.walk(scripts):
        for name in sorted(names):
            path = os.path.join(dirpath, name)
            data = scrasm.read_bytes(path)
            if data[:4] != b"RCSL":
                continue
            try:
                scrasm.write_text(os.path.join(out, os.path.splitext(name)[0] + ".asm"), scrasm.disassemble(data))
                done += 1
            except scrasm.AsmError as ex:
                print("skip %s: %s" % (name, ex))
                bad += 1
    print("exported %d scripts (%d skipped) to %s" % (done, bad, out))
    return 0


def _norm(text):
    return [line.rstrip() for line in text.replace("\r\n", "\n").split("\n")]


def compare(a, b):
    only_a = sorted(set(os.listdir(a)) - set(os.listdir(b)))
    only_b = sorted(set(os.listdir(b)) - set(os.listdir(a)))
    differ = []
    for name in sorted(set(os.listdir(a)) & set(os.listdir(b))):
        if not name.endswith(".asm"):
            continue
        ta, tb = _norm(scrasm.read_text(os.path.join(a, name))), _norm(scrasm.read_text(os.path.join(b, name)))
        if ta != tb:
            differ.append((name, ta, tb))
    for name in only_a:
        print("only in %s: %s" % (a, name))
    for name in only_b:
        print("only in %s: %s" % (b, name))
    for name, ta, tb in differ:
        print("DIFFERS: %s" % name)
        for line in list(difflib.unified_diff(ta, tb, a, b, lineterm="", n=1))[:12]:
            print("   " + line)
    print("%d differ, %d only in first, %d only in second" % (len(differ), len(only_a), len(only_b)))
    return 1 if (differ or only_a or only_b) else 0


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "export":
        sys.exit(export(sys.argv[2], sys.argv[3]))
    if len(sys.argv) == 4 and sys.argv[1] == "compare":
        sys.exit(compare(sys.argv[2], sys.argv[3]))
    print(__doc__)
    sys.exit(2)
