#!/usr/bin/env python3
"""
scrstudio.py - Script Studio: desktop editor for Air Strike 3D / Airstrike 2 compiled .scr scripts.

Put this file in the same folder as scrasm.py, scrcomp.py, scrdec.py, scrdis.py, scrsig.py, scrsrc.py and run:

    python scrstudio.py [file.scr | folder]

Everything is done by your existing toolchain; this file only adds the window around it.
  Source tab    C-like source (scrsrc).  F5 compiles it to a script.
  Assembly tab  scrasm text.  F5 assembles it (use this for hand-patched scripts the decompiler cannot structure).
  Diff tab      assembly difference between the original file and the current build.
  Strings tab   the string table of the current build.
  Info tab      header facts (slots, entries, globals, functions...).
  Tools > Batch check   round-trips every script in a folder and lists exact / hand-patched / failed.
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
    return data[:4] == MAGIC


def find_scripts(folder):
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
    warns = []
    return scrsrc.compile_source(text, warns.append), warns


def build_from_asm(text):
    warns = []
    return scrasm.assemble(text, warns.append), warns


def asm_text(data):
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
    P = scrasm.parse_scr(data)
    rows, off = [], 0
    for s in P.strings:
        rows.append((off, s.decode("latin-1")))
        off += len(s) + 1
    return rows


def info_text(data, status="", path=""):
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
        data = open(path, "rb").read()
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
            data = open(p, "rb").read()
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
    m = re.match(r"line (\d+)", str(msg))
    return int(m.group(1)) if m else None


# =====================================================================================================
# GUI
# =====================================================================================================
def run_gui(start=None):
    import tkinter as tk
    from tkinter import filedialog, messagebox, ttk
    import tkinter.font as tkfont

    KEYWORDS = r"\b(routine|entry|slots|cash|globals|functions|var|vec|float|data|if|else|while|return|break|continue|latent|pushf|nop)\b"
    SRC_RULES = [("comment", r"//[^\n]*|/\*.*?\*/"), ("string", r'"(?:[^"\\\n]|\\.)*"'), ("keyword", KEYWORDS),
                 ("label", r"\b(?:r\d+|v\d+|arg\d+|f\d+)\b"), ("number", r"\b(?:0[xX][0-9a-fA-F]+|\d+\.?\d*)\b")]
    ASM_RULES = [("comment", r";[^\n]*"), ("string", r'"(?:[^"\\\n]|\\.)*"'), ("keyword", r"^\s*\.\w+"),
                 ("label", r"\bL\d+:?|\bE\d\b"), ("number", r"-?\b\d+\.?\d*f?\b")]

    class Editor(ttk.Frame):
        def __init__(self, master, rules, on_modify):
            super().__init__(master)
            self.rules = [(t, re.compile(p, re.M | re.S if t == "comment" and "/*" in p else re.M)) for t, p in rules]
            self.on_modify = on_modify
            self.text = tk.Text(self, wrap="none", undo=True, maxundo=-1, font=MONO, tabs=("4c",), borderwidth=0,
                                highlightthickness=0)
            ys = ttk.Scrollbar(self, orient="vertical", command=self.text.yview)
            xs = ttk.Scrollbar(self, orient="horizontal", command=self.text.xview)
            self.text.configure(yscrollcommand=ys.set, xscrollcommand=xs.set)
            self.text.grid(row=0, column=0, sticky="nsew")
            ys.grid(row=0, column=1, sticky="ns")
            xs.grid(row=1, column=0, sticky="ew")
            self.rowconfigure(0, weight=1)
            self.columnconfigure(0, weight=1)
            for tag, color in (("comment", "#6a9955"), ("string", "#a31515"), ("keyword", "#0000cc"),
                               ("label", "#7a3e9d"), ("number", "#098658")):
                self.text.tag_configure(tag, foreground=color)
            self.text.tag_configure("errline", background="#ffd6d6")
            self._job = None
            self._quiet = False
            self.text.bind("<<Modified>>", self._modified)

        def _modified(self, _e=None):
            if not self.text.edit_modified():
                return
            self.text.edit_modified(False)
            if self._quiet:
                return
            self.on_modify()
            if self._job:
                self.after_cancel(self._job)
            self._job = self.after(250, self.highlight)

        def set(self, value):
            self._quiet = True
            self.text.configure(state="normal")
            self.text.delete("1.0", "end")
            self.text.insert("1.0", value)
            self.text.edit_reset()
            self.text.edit_modified(False)
            self._quiet = False
            self.highlight()

        def get(self):
            return self.text.get("1.0", "end-1c")

        def highlight(self):
            self._job = None
            t = self.text
            for tag, _ in self.rules:
                t.tag_remove(tag, "1.0", "end")
            body = t.get("1.0", "end-1c")
            if len(body) > 600000:                      # keep the window responsive on huge scripts
                return
            for tag, rx in self.rules:
                for m in rx.finditer(body):
                    t.tag_add(tag, "1.0+%dc" % m.start(), "1.0+%dc" % m.end())
            t.tag_raise("comment")
            t.tag_raise("string")

        def goto(self, line):
            self.text.tag_remove("errline", "1.0", "end")
            if line:
                self.text.tag_add("errline", "%d.0" % line, "%d.0 lineend+1c" % line)
                self.text.see("%d.0" % line)
                self.text.mark_set("insert", "%d.0" % line)

        def clear_error(self):
            self.text.tag_remove("errline", "1.0", "end")

    class Studio:
        def __init__(self, root):
            self.root = root
            root.title("Script Studio")
            root.geometry("1280x800")
            self.path = None
            self.orig = None            # original bytes of the opened file
            self.cur = None             # bytes of the current build
            self.status = ""
            self.dirty = {"source": False, "asm": False}     # edited but not yet built
            self.file_items = {}        # path -> tree item
            self.folder = None
            self._build_ui()
            self._bind()

        # ------------------------------------------------------------------ layout
        def _build_ui(self):
            r = self.root
            menu = tk.Menu(r)
            fm = tk.Menu(menu, tearoff=0)
            fm.add_command(label="Open file...", accelerator="Ctrl+O", command=self.open_file)
            fm.add_command(label="Open folder...", accelerator="Ctrl+Shift+O", command=self.open_folder)
            fm.add_separator()
            fm.add_command(label="Save script", accelerator="Ctrl+S", command=self.save)
            fm.add_command(label="Save script as...", accelerator="Ctrl+Shift+S", command=self.save_as)
            fm.add_separator()
            fm.add_command(label="Export source (.c)...", command=lambda: self.export_current("source"))
            fm.add_command(label="Export assembly (.asm)...", command=lambda: self.export_current("asm"))
            fm.add_separator()
            fm.add_command(label="Quit", command=self.quit)
            menu.add_cascade(label="File", menu=fm)
            tm = tk.Menu(menu, tearoff=0)
            tm.add_command(label="Build  (compile / assemble active tab)", accelerator="F5", command=self.build)
            tm.add_command(label="Revert to original", command=self.revert)
            tm.add_command(label="Verify round trip", command=self.verify)
            tm.add_separator()
            tm.add_command(label="Batch check folder...", command=self.batch_dialog)
            tm.add_command(label="Export all in folder...", command=self.export_all_dialog)
            menu.add_cascade(label="Tools", menu=tm)
            r.configure(menu=menu)

            bar = ttk.Frame(r, padding=(6, 4))
            bar.pack(fill="x")
            for text, cmd in (("Open", self.open_file), ("Open folder", self.open_folder), ("Build  F5", self.build),
                              ("Save", self.save), ("Revert", self.revert), ("Verify", self.verify),
                              ("Batch check", self.batch_dialog)):
                ttk.Button(bar, text=text, command=cmd).pack(side="left", padx=2)

            self.msg = tk.StringVar(value="Open a .scr file or a folder of scripts.")
            ttk.Label(r, textvariable=self.msg, anchor="w", relief="sunken", padding=(6, 2)).pack(fill="x", side="bottom")

            pan = ttk.PanedWindow(r, orient="horizontal")
            pan.pack(fill="both", expand=True)

            left = ttk.Frame(pan)
            self.filter = tk.StringVar()
            fe = ttk.Entry(left, textvariable=self.filter)
            fe.pack(fill="x", padx=4, pady=4)
            self.filter.trace_add("write", lambda *a: self.refill())
            self.tree = ttk.Treeview(left, columns=("st",), selectmode="browse", height=20)
            self.tree.heading("#0", text="Script")
            self.tree.heading("st", text="Source")
            self.tree.column("#0", width=210)
            self.tree.column("st", width=70, anchor="center")
            sb = ttk.Scrollbar(left, orient="vertical", command=self.tree.yview)
            self.tree.configure(yscrollcommand=sb.set)
            self.tree.pack(side="left", fill="both", expand=True)
            sb.pack(side="right", fill="y")
            pan.add(left, weight=0)

            right = ttk.Frame(pan)
            self.nb = ttk.Notebook(right)
            self.nb.pack(fill="both", expand=True)
            self.src = Editor(self.nb, SRC_RULES, lambda: self._edited("source"))
            self.asm = Editor(self.nb, ASM_RULES, lambda: self._edited("asm"))
            self.diff = Editor(self.nb, [("comment", r"^@@[^\n]*"), ("string", r"^\+[^\n]*"), ("keyword", r"^-[^\n]*")], lambda: None)
            self.diff.text.tag_configure("string", foreground="#0a7a0a")
            self.diff.text.tag_configure("keyword", foreground="#b00020")
            self.strings = ttk.Treeview(self.nb, columns=("off", "len", "txt"), show="headings")
            for c, t, w in (("off", "Offset", 80), ("len", "Length", 70), ("txt", "Text", 700)):
                self.strings.heading(c, text=t)
                self.strings.column(c, width=w, anchor="w")
            self.info = tk.Text(self.nb, wrap="word", font=MONO, state="disabled", borderwidth=0)
            for name, w in (("Source", self.src), ("Assembly", self.asm), ("Diff vs original", self.diff),
                            ("Strings", self.strings), ("Info", self.info)):
                self.nb.add(w, text=name)
            pan.add(right, weight=1)


        def _bind(self):
            r = self.root
            r.bind("<F5>", lambda e: self.build())
            r.bind("<Control-s>", lambda e: self.save())
            r.bind("<Control-S>", lambda e: self.save_as())
            r.bind("<Control-o>", lambda e: self.open_file())
            r.bind("<Control-O>", lambda e: self.open_folder())
            r.protocol("WM_DELETE_WINDOW", self.quit)
            self.tree.bind("<<TreeviewSelect>>", self._select)
            r.bind_class("Text", "<Control-o>", lambda e: (self.open_file(), "break")[1])
            r.bind_class("Text", "<Control-O>", lambda e: (self.open_folder(), "break")[1])

        # ------------------------------------------------------------------ helpers
        def say(self, text):
            self.msg.set(text)

        def _edited(self, which):
            self.dirty[which] = True
            self._title()

        def _title(self):
            mark = "*" if any(self.dirty.values()) else ""
            self.root.title("Script Studio - %s%s" % (os.path.basename(self.path) if self.path else "no file", mark))

        def active(self):
            return "asm" if self.nb.index(self.nb.select()) == 1 else "source"

        def confirm_discard(self):
            if any(self.dirty.values()):
                return messagebox.askyesno("Unbuilt edits", "The current script has edits that were not built. Discard them?")
            return True

        # ------------------------------------------------------------------ file list
        def open_folder(self):
            d = filedialog.askdirectory(title="Folder with .scr scripts")
            if d:
                self.load_folder(d)

        def load_folder(self, d):
            self.folder = d
            self.files = find_scripts(d)
            self.statuses = {}
            self.refill()
            self.say("%d scripts in %s" % (len(self.files), d))

        def refill(self):
            self.tree.delete(*self.tree.get_children())
            self.file_items = {}
            q = self.filter.get().lower()
            for p in getattr(self, "files", []):
                rel = os.path.relpath(p, self.folder) if self.folder else p
                if q and q not in rel.lower():
                    continue
                self.file_items[p] = self.tree.insert("", "end", iid=p, text=rel, values=(self.statuses.get(p, ""),))

        def set_status(self, path, st):
            self.statuses[path] = st
            if path in self.file_items:
                self.tree.set(path, "st", st)

        def _select(self, _e=None):
            sel = self.tree.selection()
            if sel and sel[0] != self.path:
                if not self.confirm_discard():
                    if self.path in self.file_items:
                        self.tree.selection_set(self.path)
                    return
                self.load(sel[0])

        def open_file(self):
            p = filedialog.askopenfilename(title="Open script", filetypes=[("Compiled scripts", "*.scr"), ("All files", "*.*")])
            if p and self.confirm_discard():
                if not (self.folder and p in getattr(self, "files", [])):
                    self.folder = os.path.dirname(p)
                    self.files = [p]
                    self.statuses = {}
                    self.refill()
                self.load(p)

        # ------------------------------------------------------------------ load / build
        def load(self, path):
            try:
                data = open(path, "rb").read()
                res = analyze(data)
            except Exception as ex:                                    # noqa
                messagebox.showerror("Cannot open", "%s\n\n%s: %s" % (path, type(ex).__name__, ex))
                return
            self.path, self.orig, self.cur, self.status = path, data, data, res["status"]
            self.src.set(res["source"])
            self.asm.set(res["asm"])
            self.dirty = {"source": False, "asm": False}
            self.set_status(path, {"exact": "exact", "near": "near", "asm": "asm only"}[res["status"]])
            if path in self.file_items:
                self.tree.selection_set(path)
                self.tree.see(path)
            self.refresh_views()
            self.nb.select(0 if res["status"] != "asm" else 1)
            self._title()
            self.say("%s: %s" % (os.path.basename(path), res["note"] or "source round-trips exactly"))

        def refresh_views(self):
            self.diff.set(asm_diff(self.orig, self.cur) or "; current build is byte-identical to the original\n")
            self.strings.delete(*self.strings.get_children())
            try:
                for off, s in string_table(self.cur):
                    self.strings.insert("", "end", values=(off, len(s) + 1, s))
                txt = info_text(self.cur, self.status, self.path or "")
            except Exception as ex:                                    # noqa
                txt = "Cannot read the build: %s: %s\n" % (type(ex).__name__, ex)
            self.info.configure(state="normal")
            self.info.delete("1.0", "end")
            self.info.insert("1.0", txt)
            self.info.configure(state="disabled")

        def build(self):
            if self.orig is None:
                return False
            tab = self.nb.index(self.nb.select())
            if tab == 0:
                which = "source"
            elif tab == 1:
                which = "asm"
            else:                                                       # on a view tab: build whichever editor was edited
                which = "asm" if self.dirty["asm"] and not self.dirty["source"] else "source"
            ed = self.asm if which == "asm" else self.src
            ed.clear_error()
            try:
                data, warns = (build_from_asm if which == "asm" else build_from_source)(ed.get())
            except BUILD_ERRORS as ex:
                ed.goto(error_line(ex))
                self.say("Build failed: %s" % ex)
                messagebox.showerror("Build failed", str(ex))
                return False
            except Exception as ex:                                    # noqa
                self.say("Build failed: %s: %s" % (type(ex).__name__, ex))
                messagebox.showerror("Build failed", "%s: %s" % (type(ex).__name__, ex))
                return False
            self.cur = data
            try:                                                       # keep the other editor in sync with the new build
                if which == "source":
                    self.asm.set(asm_text(data))
                else:
                    s = scrsrc.decompile_to_source(data, verify=False)
                    self.src.set((s + "\n") if s else "// (this build cannot be shown as source - use the Assembly tab)\n")
            except Exception:                                          # noqa
                pass
            self.dirty = {"source": False, "asm": False}
            self.refresh_views()
            self._title()
            same = "identical to the original file" if data == self.orig else "%d bytes (original %d)" % (len(data), len(self.orig))
            self.say("Built from %s: %s%s" % ("assembly" if which == "asm" else "source", same,
                                              ("  |  warning: " + "; ".join(warns)) if warns else ""))
            return True

        def revert(self):
            if self.path and messagebox.askyesno("Revert", "Discard all edits and reload the original script?"):
                self.dirty = {"source": False, "asm": False}
                self.load(self.path)

        def verify(self):
            if self.orig is None:
                return
            st, detail = check_file(self.path)
            self.say("Verify %s: %s (%s)" % (os.path.basename(self.path), st, detail))
            messagebox.showinfo("Verify", "%s\n\nResult: %s\n%s" % (self.path, st, detail))

        # ------------------------------------------------------------------ saving / exporting
        def save(self):
            if self.cur is None:
                return
            if any(self.dirty.values()) and not self.build():
                return
            if os.path.exists(self.path) and self.cur != self.orig:
                bak = self.path + ".bak"
                if not os.path.exists(bak):
                    with open(bak, "wb") as fh:
                        fh.write(self.orig)
            with open(self.path, "wb") as fh:
                fh.write(self.cur)
            self.orig = self.cur
            self.refresh_views()
            self.say("Saved %s (%d bytes); the first overwrite keeps a .bak copy" % (self.path, len(self.cur)))

        def save_as(self):
            if self.cur is None:
                return
            if any(self.dirty.values()) and not self.build():
                return
            p = filedialog.asksaveasfilename(defaultextension=".scr", initialfile=os.path.basename(self.path),
                                             filetypes=[("Compiled scripts", "*.scr")])
            if p:
                with open(p, "wb") as fh:
                    fh.write(self.cur)
                self.say("Wrote %s (%d bytes)" % (p, len(self.cur)))

        def export_current(self, kind):
            if self.cur is None:
                return
            ext = ".c" if kind == "source" else ".asm"
            p = filedialog.asksaveasfilename(defaultextension=ext, initialfile=os.path.splitext(os.path.basename(self.path))[0] + ext)
            if p:
                text = (self.src if kind == "source" else self.asm).get()
                with open(p, "w", encoding="utf-8", newline="\n") as fh:
                    fh.write(text.rstrip("\n") + "\n")
                self.say("Wrote %s" % p)

        def export_all_dialog(self):
            files = getattr(self, "files", [])
            if not files:
                messagebox.showinfo("Export all", "Open a folder first.")
                return
            kind = "source" if messagebox.askyesno("Export all", "Export decompiled SOURCE (.c)?\n\nYes = source, No = assembly (.asm).") else "asm"
            out = filedialog.askdirectory(title="Output folder")
            if not out:
                return
            written, skipped = export_all(files, out, kind)
            msg = "Wrote %d files to %s." % (written, out)
            if skipped:
                msg += "\n\nSkipped %d (hand-patched or unreadable): %s%s" % (len(skipped), ", ".join(skipped[:10]), " ..." if len(skipped) > 10 else "")
            messagebox.showinfo("Export all", msg)

        # ------------------------------------------------------------------ batch check
        def batch_dialog(self):
            files = getattr(self, "files", [])
            if not files:
                messagebox.showinfo("Batch check", "Open a folder first.")
                return
            win = tk.Toplevel(self.root)
            win.title("Batch check - %d scripts" % len(files))
            win.geometry("820x520")
            top = ttk.Frame(win, padding=6)
            top.pack(fill="x")
            prog = ttk.Progressbar(top, maximum=len(files))
            prog.pack(side="left", fill="x", expand=True)
            summary = tk.StringVar(value="running...")
            ttk.Label(top, textvariable=summary, padding=(8, 0)).pack(side="left")
            cols = ttk.Treeview(win, columns=("st", "detail"), selectmode="browse")
            cols.heading("#0", text="Script")
            cols.heading("st", text="Result")
            cols.heading("detail", text="Detail")
            cols.column("#0", width=300)
            cols.column("st", width=90, anchor="center")
            cols.column("detail", width=380)
            cols.tag_configure("FAIL", foreground="#b00020")
            cols.tag_configure("asm-only", foreground="#a06000")
            cols.pack(fill="both", expand=True)
            btns = ttk.Frame(win, padding=6)
            btns.pack(fill="x")
            q, stop, counts = queue.Queue(), threading.Event(), {}

            def worker():
                for p in files:
                    if stop.is_set():
                        break
                    q.put((p,) + check_file(p))
                q.put(None)

            def poll():
                try:
                    while True:
                        item = q.get_nowait()
                        if item is None:
                            summary.set(", ".join("%d %s" % (v, k) for k, v in sorted(counts.items())) + "  (done)")
                            return
                        p, st, detail = item
                        counts[st] = counts.get(st, 0) + 1
                        cols.insert("", "end", iid=p, text=os.path.relpath(p, self.folder), values=(st, detail), tags=(st,))
                        self.set_status(p, {"exact": "exact", "near": "near", "asm-only": "asm only"}.get(st, st))
                        prog.step(1)
                except queue.Empty:
                    pass
                summary.set(", ".join("%d %s" % (v, k) for k, v in sorted(counts.items())))
                win.after(60, poll)

            def open_row(_e=None):
                s = cols.selection()
                if s and self.confirm_discard():
                    self.load(s[0])

            def save_csv():
                p = filedialog.asksaveasfilename(defaultextension=".csv", initialfile="batch_check.csv")
                if p:
                    with open(p, "w", encoding="utf-8", newline="") as fh:
                        fh.write("script,result,detail\n")
                        for iid in cols.get_children():
                            v = cols.item(iid, "values")
                            fh.write('"%s",%s,"%s"\n' % (cols.item(iid, "text"), v[0], str(v[1]).replace('"', "'")))

            ttk.Button(btns, text="Open selected", command=open_row).pack(side="left")
            ttk.Button(btns, text="Save as CSV...", command=save_csv).pack(side="left", padx=6)
            ttk.Button(btns, text="Stop", command=stop.set).pack(side="right")
            cols.bind("<Double-1>", open_row)
            win.protocol("WM_DELETE_WINDOW", lambda: (stop.set(), win.destroy()))
            threading.Thread(target=worker, daemon=True).start()
            poll()

        def quit(self):
            if self.confirm_discard():
                self.root.destroy()

    root = tk.Tk()
    global MONO
    MONO = tkfont.Font(family="Consolas" if sys.platform.startswith("win") else "Courier", size=10)
    try:
        ttk.Style().theme_use("vista" if sys.platform.startswith("win") else "clam")
    except tk.TclError:
        pass
    app = Studio(root)
    if start:
        if os.path.isdir(start):
            app.load_folder(start)
        elif os.path.isfile(start):
            app.folder, app.files, app.statuses = os.path.dirname(os.path.abspath(start)), [os.path.abspath(start)], {}
            app.refill()
            app.load(os.path.abspath(start))
    root.mainloop()


if __name__ == "__main__":
    run_gui(sys.argv[1] if len(sys.argv) > 1 else None)
