#!/usr/bin/env python3
"""
scrcore.py - Script Studio core (no GUI): everything the window does goes through these functions.

Kept free of tkinter so it can be imported, tested and scripted on machines without Tk:
    is_script / find_scripts / analyze / build_from_source / build_from_asm / asm_text / asm_diff /
    string_table / info_text / check_file / export_all / error_line
"""
import difflib
import os
import queue
import re
import sys
import threading

import scrasm
import scrcomp
import scrsrc

MAGIC = b"RCSL"
ROLE_NAMES = ["init", "main", "damaged", "touched", "callback"]
BUILD_ERRORS = (scrsrc.SrcError, scrcomp.CompileError, scrasm.AsmError, KeyError, ValueError, IndexError)


# =====================================================================================================
# core (no GUI): everything the window does goes through these functions
# =====================================================================================================
def is_script(data):
    """True if data starts with the RCSL magic number."""
    return data[:4] == MAGIC


def find_scripts(folder):
    """Sorted list of every .scr file under folder (recursive)."""
    out = []
    for dp, dn, fn in os.walk(folder):
        dn.sort()
        for f in sorted(fn):
            if f.lower().endswith(".scr"):
                out.append(os.path.join(dp, f))
    return out


def analyze(data):
    """Decompile a script. Returns dict(status, source, asm, note).
    status: 'exact'   source recompiles to identical bytes
            'near'    decompiler structured it but recompiling gives different bytes (edit with care / use assembly)
            'asm'     hand-patched code the decompiler cannot structure: assembly only"""
    if not is_script(data):
        raise ValueError("not a compiled script (missing RCSL header)")
    scrasm.parse_scr(data)                                             # raises on a truncated / damaged file
    res = {"status": "asm", "source": "", "asm": "", "note": ""}
    try:
        res["asm"] = scrasm.disassemble(data)
    except Exception as ex:                                            # noqa
        res["asm"] = "; could not disassemble: %s: %s\n" % (type(ex).__name__, ex)
    try:
        src = scrsrc.decompile_to_source(data, verify=True)
        if src is not None:
            res.update(status="exact", source=src + "\n")
            return res
        src = scrsrc.decompile_to_source(data, verify=False)
    except Exception as ex:                                            # noqa
        src = None
        res["note"] = "decompiler error: %s: %s" % (type(ex).__name__, ex)
    if src is not None:
        res.update(status="near", source=src + "\n",
                   note="Source recompiles to different bytes than the original (hand patch or compiler variant).")
    else:
        res["source"] = ("// This script contains hand-patched (unstructured) code, so no source can be shown.\n"
                         "// Use the Assembly tab.\n")
        res["note"] = res["note"] or "Unstructured code: use the Assembly tab."
    return res


def build_from_source(text):
    """Compile source text. Returns (bytes, warnings); raises the build errors listed in BUILD_ERRORS."""
    warns = []
    return scrsrc.compile_source(text, warns.append), warns


def build_from_asm(text):
    """Assemble assembly text. Returns (bytes, warnings); raises the build errors listed in BUILD_ERRORS."""
    warns = []
    return scrasm.assemble(text, warns.append), warns


def asm_text(data):
    """Assembly text for compiled script bytes."""
    return scrasm.disassemble(data)


def _norm_labels(text):
    """Rename labels by order of appearance so inserting one instruction does not renumber every label in the diff."""
    order = re.findall(r"^(L\d+):", text, re.M)
    mapping = {old: "L%d" % k for k, old in enumerate(order)}
    return re.sub(r"\bL\d+\b", lambda m: mapping.get(m.group(0), m.group(0)), text)


def asm_diff(a, b):
    """Unified diff of the two scripts' assembly ('' when identical). Label numbers are normalised."""
    if a == b:
        return ""
    la, lb = _norm_labels(asm_text(a)).splitlines(), _norm_labels(asm_text(b)).splitlines()
    return "\n".join(difflib.unified_diff(la, lb, "original", "current build", lineterm="", n=3))


def string_table(data):
    """Rows of (byte offset, text) for the script string table."""
    P = scrasm.parse_scr(data)
    rows, off = [], 0
    for s in P.strings:
        rows.append((off, s.decode("latin-1")))
        off += len(s) + 1
    return rows


def info_text(data, status="", path=""):
    """Human-readable header summary (size, slots, entries, globals, functions) for the Info tab."""
    P = scrasm.parse_scr(data)
    L = []
    if path:
        L.append("File:      %s" % path)
    L.append("Size:      %d bytes" % len(data))
    if status:
        L.append("Source:    %s" % {"exact": "exact (source <-> script round-trips)",
                                    "near": "structured, but not byte-exact",
                                    "asm": "hand-patched, assembly only"}.get(status, status))
    L.append("Slots:     %d" % P.slots)
    L.append("Entries:   " + ", ".join("%s=%s" % (ROLE_NAMES[k], ("instr %d" % e) if e >= 0 else "none")
                                         for k, e in enumerate(P.entries)))
    L.append("Code:      %d instructions" % len(P.code))
    L.append("Strings:   %d" % len(P.strings))
    L.append("Cash:      " + (", ".join(n.decode("latin-1") for _, n in P.cash) or "-"))
    L.append("Globals:   " + (", ".join(d.decode("latin-1") for d in P.defs) or "-"))
    L.append("Functions: " + (", ".join(f.decode("latin-1") for f in P.funcs) or "-"))
    return "\n".join(L) + "\n"


def check_file(path):
    """Batch check of one script. Returns (status, detail).
    status: exact | near | asm-only | FAIL | not-a-script ;  detail: short text."""
    try:
        data = scrasm.read_bytes(path)
        if not is_script(data):
            return "not-a-script", "missing RCSL header"
        src = scrsrc.decompile_to_source(data, verify=True)
        if src is not None:
            return "exact", "%d bytes" % len(data)
        near = scrsrc.decompile_to_source(data, verify=False) is not None
        try:
            asm_ok = scrasm.assemble(scrasm.disassemble(data)) == data
        except Exception:                                              # noqa
            asm_ok = False
        if not asm_ok:
            return "FAIL", "assembly does not round-trip"
        return ("near" if near else "asm-only"), "assembly round-trips" + ("; source differs" if near else "")
    except Exception as ex:                                            # noqa
        return "FAIL", "%s: %s" % (type(ex).__name__, ex)


def export_all(files, outdir, kind, progress=None, cancel=None):
    """Write <name>.c (kind='source') or <name>.asm (kind='asm') for every script. Returns (written, skipped)."""
    os.makedirs(outdir, exist_ok=True)
    written, skipped = 0, []
    for k, p in enumerate(files):
        if cancel and cancel():
            break
        try:
            data = scrasm.read_bytes(p)
            text = (scrsrc.decompile_to_source(data) if kind == "source" else scrasm.disassemble(data))
            if text is None:
                skipped.append(os.path.basename(p))
            else:
                base = os.path.splitext(os.path.basename(p))[0]
                with open(os.path.join(outdir, base + (".c" if kind == "source" else ".asm")), "w",
                          encoding="utf-8", newline="\n") as fh:
                    fh.write(text.rstrip("\n") + "\n")
                written += 1
        except Exception:                                              # noqa
            skipped.append(os.path.basename(p))
        if progress:
            progress(k + 1, len(files))
    return written, skipped


def error_line(msg):
    """Line number from a "line N: ..." build error message, or None."""
    m = re.match(r"line (\d+)", str(msg))
    return int(m.group(1)) if m else None


# =====================================================================================================
# GUI
