#!/usr/bin/env python3
"""
scrasm.py - lossless assembler / disassembler for Air Strike 3D compiled scripts (.scr).

    python scrasm.py dis   script.scr [script.asm]     .scr -> editable text
    python scrasm.py asm   script.asm [script.scr]     text -> .scr
    python scrasm.py roundtrip <folder>                prove dis -> asm reproduces every .scr byte for byte

Design goals
  * Lossless: for every script the assembler can read back exactly the bytes the disassembler wrote.
    Anything not understood is kept as a plain number, never guessed.
  * Edit-safe: jumps, calls and entry points are LABELS; insert or delete instructions and every offset,
    the instruction count, string offsets and section sizes are recomputed. Strings may change length.
  * Strict: unknown names, undefined labels and out-of-range values are errors.

TEXT FORMAT
  ; comment                          (a ';' outside a string starts a comment)
  .slots N                           script variable slots (compiler allocation; the assembler warns if code uses more)
  .entry E0 L12 | none | #n          entry points E0 init, E1 main, E2 damaged, E3 touched, E4 callback
  .cash "name" [kind]                resources to pre-load
  .def name                          script-visible globals, in table order (-1, -2, ... in code)
  .func name                         engine functions, in table order (-1, -2, ... in code)
  .var float 18 0.0f                 variable declaration: float slot initial value
  .var vec 23                        variable declaration: 3-vector at slot 23 (components in 24..26)
  .data type slot 0xVALUE            any other declaration, raw
  .string "text"                     string table entry, in table order (each use of a literal has its own copy)
  .code                              instructions follow
  L12:                               label (any identifier)
  MOV.1000 r0, s3, 0                 NAME[.flags] a, b, c   (flags = high byte of the opcode, 4 hex digits, omitted if 0000)

OPERANDS
  r0..r15  register        v16..    script variable        $name  global from .def
  s3       string table entry 3 (offset resolved by the assembler)
  1.5f     float immediate (where the flags say the operand is a float)
  @name    engine function from .func            L12   label (jump/call target; relative offsets are computed)
  12       plain integer     #-9  raw integer that has no symbolic form
  bits:0x7fc00000   float given by its exact bit pattern
"""
import os
import re
import struct
import sys
from decimal import Decimal

OPN = {
    0x00: "END", 0x01: "MUL", 0x02: "DIV", 0x03: "ADD", 0x04: "SUB", 0x05: "BAND", 0x07: "OR", 0x08: "AND",
    0x09: "EQ", 0x0A: "NE", 0x0B: "GT", 0x0C: "LT", 0x0D: "GE", 0x0E: "LE", 0x0F: "NOT", 0x10: "NEG",
    0x11: "MOV", 0x12: "LOAD", 0x13: "ENTER", 0x14: "LEAVE", 0x18: "RETURN", 0x1A: "JZ", 0x1B: "JMP",
    0x1C: "CALL", 0x1D: "CALLP", 0x1E: "PUSHF",
}
NAME2OP = {v: k for k, v in OPN.items()}
BINARY = {0x01, 0x02, 0x03, 0x04, 0x05, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E}
# operand kinds: val = slot-or-float (flags decide), slot, raw, rel (relative jump), call (builtin or absolute label)
KINDS = {}
for _o in BINARY:
    KINDS[_o] = ("val", "val", "slot")
KINDS.update({0x0F: ("val", "raw", "slot"), 0x10: ("val", "raw", "slot"), 0x11: ("slot", "val", "raw"),
              0x12: ("slot", "raw", "slot"), 0x1A: ("slot", "rel", "raw"), 0x1B: ("rel", "raw", "raw"),
              0x1C: ("call", "raw", "raw"), 0x1D: ("call", "raw", "raw"), 0x1E: ("val", "raw", "raw")})
DEFAULT_KINDS = ("raw", "raw", "raw")
IDENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]*$")


class AsmError(Exception):
    pass


# ====================================================================== low level container
def _f32bits(f):
    return struct.unpack("<i", struct.pack("<f", f))[0]


def _bits2f(i):
    return struct.unpack("<f", struct.pack("<i", i))[0]


def fnum(bits):
    f = _bits2f(bits)
    if f != f or f in (float("inf"), float("-inf")):
        return "bits:0x%08x" % (bits & 0xFFFFFFFF)
    for p in range(1, 10):
        s = "%.*g" % (p, f)
        try:
            if _f32bits(float(s)) == bits:
                if "e" in s and (f == 0 or 1e-4 <= abs(f) < 1e9):
                    s = format(Decimal(s), "f")
                return s + "f"
        except OverflowError:
            break
    return "bits:0x%08x" % (bits & 0xFFFFFFFF)


def quote(b):
    out = ['"']
    for ch in b:
        if ch == 0x22:
            out.append('\\"')
        elif ch == 0x5C:
            out.append("\\\\")
        elif 32 <= ch < 127:
            out.append(chr(ch))
        else:
            out.append("\\x%02x" % ch)
    out.append('"')
    return "".join(out)


def unquote(tok):
    if len(tok) < 2 or tok[0] != '"' or tok[-1] != '"':
        raise AsmError("expected a quoted string, got %r" % tok)
    body, out, i = tok[1:-1], bytearray(), 0
    while i < len(body):
        ch = body[i]
        if ch == "\\":
            nxt = body[i + 1:i + 2]
            if nxt == "x":
                out.append(int(body[i + 2:i + 4], 16))
                i += 4
                continue
            if nxt in ('"', "\\"):
                out += nxt.encode("latin-1")
                i += 2
                continue
            raise AsmError("bad escape in string %r" % tok)
        out += ch.encode("latin-1")
        i += 1
    return bytes(out)


class Parsed:
    """Everything in a .scr, in editable form."""

    def __init__(self):
        self.slots = 16
        self.entries = [-1] * 5            # instruction indexes
        self.cash = []                     # (kind, bytes)
        self.defs = []                     # bytes
        self.funcs = []                    # bytes
        self.data = []                     # (type, slot, i32)
        self.strings = []                  # bytes
        self.code = []                     # (op, a, b, c)


def parse_scr(data):
    if data[:4] != b"RCSL":
        raise AsmError("not an RCSL script")
    hsz = struct.unpack_from("<I", data, 4)[0]
    if hsz != 16:
        raise AsmError("unexpected header size %d" % hsz)
    counts = struct.unpack_from("<4I", data, 8)
    ext = struct.unpack_from("<8i", data, 24)
    pos, secs = 56, {}
    while pos + 8 <= len(data):
        tag = data[pos:pos + 4].decode("latin-1")
        sz = struct.unpack_from("<I", data, pos + 4)[0]
        if pos + 8 + sz > len(data):
            raise AsmError("section %s runs past the end of the file" % tag)
        secs[tag] = data[pos + 8:pos + 8 + sz]
        pos += 8 + sz
    if pos != len(data):
        raise AsmError("%d unread trailing bytes" % (len(data) - pos))
    P = Parsed()
    P.slots = ext[0]
    P.entries = list(ext[3:8])
    b = secs.get("CASH", b"")
    i = 0
    while i < len(b):
        n = b[i + 1]
        P.cash.append((b[i], b[i + 2:i + 2 + n].split(b"\0", 1)[0] if n else b""))
        if b[i + 2:i + 2 + n][-1:] != b"\0":
            raise AsmError("CASH entry without NUL terminator")
        i += 2 + n
    for tag, dest in (("DEFS", P.defs), ("FUNC", P.funcs)):
        b, i = secs.get(tag, b""), 0
        while i < len(b):
            n = b[i]
            chunk = b[i + 1:i + 1 + n]
            if chunk[-1:] != b"\0" or chunk.count(b"\0") != 1:
                raise AsmError("%s entry without a clean NUL terminator" % tag)
            dest.append(chunk[:-1])
            i += 1 + n
    d = secs.get("DATA", b"")
    if len(d) % 8:
        raise AsmError("DATA size is not a multiple of 8")
    for k in range(0, len(d), 8):
        P.data.append(struct.unpack_from("<HHi", d, k))
    s = secs.get("STRG", b"")
    if s and not s.endswith(b"\0"):
        raise AsmError("STRG does not end with NUL")
    P.strings = s.split(b"\0")[:-1] if s else []
    c = secs.get("CODE", b"")                     # a completely empty script (e.g. svet_fonar_flicker) has no CODE section
    if len(c) % 14:
        raise AsmError("CODE size is not a multiple of 14")
    P.code = [struct.unpack_from("<Hiii", c, k) for k in range(0, len(c), 14)]
    # presence rules the writer relies on
    for tag in ("CASH", "DEFS", "FUNC", "STRG"):
        if (tag in secs) != (len(secs.get(tag, b"")) > 0):
            raise AsmError("section %s is present but empty (or vice versa)" % tag)
    if "DATA" not in secs:
        raise AsmError("DATA section missing")
    if "CODE" in secs and not secs["CODE"]:
        raise AsmError("CODE section present but empty")
    return P


def build_scr(P):
    def lenstr(items):
        out = bytearray()
        for it in items:
            if len(it) > 253 or b"\0" in it:
                raise AsmError("name too long or contains NUL: %r" % it)
            out += bytes([len(it) + 1]) + it + b"\0"
        return bytes(out)

    cash = bytearray()
    for kind, name in P.cash:
        if len(name) > 253:
            raise AsmError("resource name too long")
        cash += bytes([kind, len(name) + 1]) + name + b"\0"
    strg = b"".join(s + b"\0" for s in P.strings)
    data = b"".join(struct.pack("<HHi", *d) for d in P.data)
    code = b"".join(struct.pack("<Hiii", *i) for i in P.code)
    sections = []
    for tag, payload in (("CASH", bytes(cash)), ("DEFS", lenstr(P.defs)), ("FUNC", lenstr(P.funcs)),
                         ("DATA", data), ("STRG", strg), ("CODE", code)):
        if payload or tag == "DATA":                # CODE is omitted when the script has no instructions
            sections.append((tag, payload))
    out = bytearray(b"RCSL") + struct.pack("<I", 16)
    out += struct.pack("<4I", len(P.cash), len(P.defs), len(P.funcs), len(P.data))
    out += struct.pack("<8i", P.slots, len(strg), len(P.code), *P.entries)
    for tag, payload in sections:
        out += tag.encode() + struct.pack("<I", len(payload)) + payload
    return bytes(out)


# ====================================================================== disassembler
def _ident_ok(b):
    return bool(IDENT.match(b.decode("latin-1"))) and all(c < 128 for c in b)


def disassemble(data):
    P = parse_scr(data)
    n = len(P.code)
    offs, o = [], 0
    for s in P.strings:
        offs.append(o)
        o += len(s) + 1
    off2idx = {}
    for k, off in enumerate(offs):
        off2idx.setdefault(off, k)
    ndefs, nfuncs = len(P.defs), len(P.funcs)

    # --- which instruction operands are string references (op 0x1011 immediate b, exact offset)
    first_zero = next((i for i, (op, a, b, c) in enumerate(P.code) if op == 0x1011 and b == 0), -1)

    def is_string(i):
        op, a, b, c = P.code[i]
        if op != 0x1011 or b not in off2idx:
            return False
        return b != 0 or i == first_zero

    # --- labels
    targets = set()
    for e in P.entries:
        if 0 <= e <= n:
            targets.add(e)
    for i, (op, a, b, c) in enumerate(P.code):
        low = op & 0xFF
        if low == 0x1A and 0 <= i + b <= n:
            targets.add(i + b)
        if low == 0x1B and 0 <= i + a <= n:
            targets.add(i + a)
        if low in (0x1C, 0x1D) and 0 <= a <= n:
            targets.add(a)

    def slot(v):
        if v < 0 and -v <= ndefs and _ident_ok(P.defs[-v - 1]):
            return "$" + P.defs[-v - 1].decode("latin-1")
        if 0 <= v < 16:
            return "r%d" % v
        if v >= 16:
            return "v%d" % v
        return "#%d" % v

    out = ["; Air Strike 3D script assembly (scrasm v1)", ".slots %d" % P.slots]
    for k, e in enumerate(P.entries):
        out.append(".entry E%d %s" % (k, "none" if e == -1 else ("L%d" % e if 0 <= e <= n else "#%d" % e)))
    for kind, name in P.cash:
        out.append(".cash %s%s" % (quote(name), (" %d" % kind) if kind else ""))
    for d in P.defs:
        out.append(".def %s" % (d.decode("latin-1") if _ident_ok(d) else quote(d)))
    for f in P.funcs:
        out.append(".func %s" % (f.decode("latin-1") if _ident_ok(f) else quote(f)))
    for ty, sl, val in P.data:
        if ty == 2:
            out.append(".var float %d %s" % (sl, fnum(val)))
        elif ty == 3 and val == sl + 1:
            out.append(".var vec %d" % sl)
        else:
            out.append(".data %d %d 0x%08x" % (ty, sl, val & 0xFFFFFFFF))
    for s in P.strings:
        out.append(".string %s" % quote(s))
    out.append(".code")
    for i, (op, a, b, c) in enumerate(P.code):
        if i in targets:
            out.append("L%d:" % i)
        low, hi = op & 0xFF, op & 0xFF00
        kinds = KINDS.get(low, DEFAULT_KINDS)
        toks, note = [], ""
        for pos, (kind, v) in enumerate(zip(kinds, (a, b, c))):
            isfloat = (pos == 0 and hi & 0x0100) or (pos == 1 and hi & 0x1000)
            if kind == "val" and is_string(i) and pos == 1:
                k = off2idx[v]
                toks.append("s%d" % k)
                note = " ; " + quote(P.strings[k])
            elif kind == "val" and isfloat:
                toks.append(fnum(v))
            elif kind in ("val", "slot"):
                toks.append(slot(v))
            elif kind == "rel":
                t = i + v
                toks.append("L%d" % t if 0 <= t <= n else "#%d" % v)
            elif kind == "call":
                if v < 0 and -v <= nfuncs and _ident_ok(P.funcs[-v - 1]):
                    toks.append("@" + P.funcs[-v - 1].decode("latin-1"))
                elif 0 <= v <= n:
                    toks.append("L%d" % v)
                else:
                    toks.append("#%d" % v)
            else:
                toks.append("%d" % v)
        name = OPN.get(low, "OP_%02x" % low)
        if hi:
            name += ".%04x" % hi
        out.append("    %-11s %s%s" % (name, ", ".join(toks), note))
    if n in targets:
        out.append("L%d:" % n)
    return "\n".join(out) + "\n"


# ====================================================================== assembler
def _strip_comment(line):
    inq = False
    i = 0
    while i < len(line):
        ch = line[i]
        if ch == "\\" and inq:
            i += 2
            continue
        if ch == '"':
            inq = not inq
        elif ch == ";" and not inq:
            return line[:i]
        i += 1
    return line


def _split_args(s):
    """Split on whitespace, keeping quoted strings together."""
    toks, cur, inq, i = [], "", False, 0
    while i < len(s):
        ch = s[i]
        if inq:
            cur += ch
            if ch == "\\":
                cur += s[i + 1]
                i += 1
            elif ch == '"':
                inq = False
        elif ch == '"':
            inq = True
            cur += ch
        elif ch.isspace():
            if cur:
                toks.append(cur)
                cur = ""
        else:
            cur += ch
        i += 1
    if cur:
        toks.append(cur)
    return toks


FLOAT_RE = re.compile(r"^[-+]?(\d+\.?\d*|\.\d+)(e[-+]?\d+)?f$", re.I)


def assemble(text, warn=None):
    warn = warn if warn is not None else (lambda m: None)
    P = Parsed()
    P.entries = [-1] * 5
    labels, pending = {}, []        # pending: (index, tokens, op, lineno)
    entry_tokens = {}
    mode = "head"
    for lineno, raw in enumerate(text.splitlines(), 1):
        line = _strip_comment(raw).strip()
        if not line:
            continue

        def err(msg):
            raise AsmError("line %d: %s   [%s]" % (lineno, msg, raw.strip()))

        if line.startswith("."):
            parts = _split_args(line)
            d = parts[0]
            if d == ".code":
                mode = "code"
            elif d == ".slots":
                P.slots = int(parts[1])
            elif d == ".entry":
                m = re.match(r"^E([0-4])$", parts[1])
                if not m:
                    err("expected E0..E4")
                entry_tokens[int(m.group(1))] = parts[2]
            elif d == ".cash":
                P.cash.append((int(parts[2]) if len(parts) > 2 else 0, unquote(parts[1])))
            elif d in (".def", ".func"):
                nm = unquote(parts[1]) if parts[1].startswith('"') else parts[1].encode("latin-1")
                (P.defs if d == ".def" else P.funcs).append(nm)
            elif d == ".var":
                if parts[1] == "float":
                    P.data.append((2, int(parts[2]), _tok_float(parts[3], err)))
                elif parts[1] == "vec":
                    P.data.append((3, int(parts[2]), int(parts[2]) + 1))
                else:
                    err("unknown .var kind")
            elif d == ".data":
                v = int(parts[3], 0)
                P.data.append((int(parts[1]), int(parts[2]), v - (1 << 32) if v >= 1 << 31 else v))
            elif d == ".string":
                P.strings.append(unquote(parts[1]))
            else:
                err("unknown directive")
            continue
        m = re.match(r"^([A-Za-z_][A-Za-z0-9_.]*):$", line)
        if m:
            if mode != "code":
                err("label outside .code")
            if m.group(1) in labels:
                err("duplicate label")
            labels[m.group(1)] = len(pending)
            continue
        if mode != "code":
            err("instruction before .code")
        head, _, rest = line.partition(" ")
        nm, _, hx = head.partition(".")
        if nm in NAME2OP:
            low = NAME2OP[nm]
        else:
            mm = re.match(r"^OP_([0-9a-fA-F]{2})$", nm)
            if not mm:
                err("unknown instruction %r" % nm)
            low = int(mm.group(1), 16)
        hi = int(hx, 16) if hx else 0
        if hi & 0xFF or hi > 0xFFFF:
            err("bad flags")
        toks = [t.strip() for t in rest.split(",")] if rest.strip() else []
        if len(toks) > 3:
            err("too many operands")
        toks += ["0"] * (3 - len(toks))
        pending.append((len(pending), toks, low | hi, lineno, raw.strip()))

    n = len(pending)
    # string offsets
    offs, o = [], 0
    for s in P.strings:
        offs.append(o)
        o += len(s) + 1
    defidx = {d: k for k, d in enumerate(P.defs)}
    funcidx = {f: k for k, f in enumerate(P.funcs)}

    def label_target(name, err):
        if name in labels:
            return labels[name]
        err("undefined label %r" % name)

    for idx, toks, op, lineno, raw in pending:
        def err(msg, lineno=lineno, raw=raw):
            raise AsmError("line %d: %s   [%s]" % (lineno, msg, raw))

        low = op & 0xFF
        kinds = KINDS.get(low, DEFAULT_KINDS)
        vals = []
        for kind, tok in zip(kinds, toks):
            if kind == "rel":
                if IDENT.match(tok) and not re.match(r"^[rvs]\d+$", tok):
                    t = label_target(tok, err)
                    vals.append(t - idx)
                else:
                    vals.append(_tok_int(tok, err, defidx, offs, None))
            elif kind == "call":
                if tok.startswith("@"):
                    nm = tok[1:].encode("latin-1")
                    if nm not in funcidx:
                        err("unknown engine function %r (declare it with .func)" % tok)
                    vals.append(-(funcidx[nm] + 1))
                elif IDENT.match(tok):
                    vals.append(label_target(tok, err))
                else:
                    vals.append(_tok_int(tok, err, defidx, offs, None))
            else:
                vals.append(_tok_int(tok, err, defidx, offs, kind))
        P.code.append((op, vals[0], vals[1], vals[2]))
    # entries
    for k, tok in entry_tokens.items():
        if tok == "none":
            P.entries[k] = -1
        elif tok.startswith("#"):
            P.entries[k] = int(tok[1:])
        else:
            if tok not in labels:
                raise AsmError(".entry E%d: undefined label %r" % (k, tok))
            P.entries[k] = labels[tok]
    # sanity warnings
    for i, (op, a, b, c) in enumerate(P.code):
        low = op & 0xFF
        for pos, v in enumerate((a, b, c)):
            kind = KINDS.get(low, DEFAULT_KINDS)[pos]
            isfloat = (pos == 0 and op & 0x0100) or (pos == 1 and op & 0x1000)
            if kind in ("slot", "val") and not isfloat and v >= P.slots and not (low == 0x11 and pos == 1 and op == 0x1011):
                warn("instruction %d uses variable slot %d but .slots is %d" % (i, v, P.slots))
    return build_scr(P)


def _tok_float(tok, err):
    if tok.startswith("bits:"):
        v = int(tok[5:], 16)
        return v - (1 << 32) if v >= 1 << 31 else v
    if FLOAT_RE.match(tok):
        return _f32bits(float(tok[:-1]))
    err("bad float %r" % tok)


def _tok_int(tok, err, defidx, offs, kind):
    if tok.startswith("$"):
        nm = tok[1:].encode("latin-1")
        if nm not in defidx:
            err("unknown global %r (declare it with .def)" % tok)
        return -(defidx[nm] + 1)
    m = re.match(r"^r(\d+)$", tok)
    if m:
        if int(m.group(1)) > 15:
            err("registers are r0..r15")
        return int(m.group(1))
    m = re.match(r"^v(\d+)$", tok)
    if m:
        if int(m.group(1)) < 16:
            err("variables start at v16")
        return int(m.group(1))
    m = re.match(r"^s(\d+)$", tok)
    if m:
        k = int(m.group(1))
        if k >= len(offs):
            err("string s%d does not exist" % k)
        return offs[k]
    if tok.startswith("#"):
        return int(tok[1:])
    if tok.startswith("bits:") or FLOAT_RE.match(tok):
        return _tok_float(tok, err)
    try:
        return int(tok, 0)
    except ValueError:
        err("cannot read operand %r" % tok)


# ====================================================================== commands
def roundtrip(folder, verbose=True):
    files = sorted(os.path.join(dp, f) for dp, dn, fn in os.walk(folder) for f in fn)
    tested = passed = 0
    failures = []
    for p in files:
        d = open(p, "rb").read()
        if d[:4] != b"RCSL":
            continue
        tested += 1
        try:
            back = assemble(disassemble(d))
            if back == d:
                passed += 1
            else:
                first = next((i for i, (x, y) in enumerate(zip(back, d)) if x != y), min(len(back), len(d)))
                failures.append((p, "bytes differ at offset %d (sizes %d vs %d)" % (first, len(back), len(d))))
        except Exception as e:  # noqa
            failures.append((p, "%s: %s" % (type(e).__name__, e)))
    if verbose:
        print("round trip dis -> asm: %d / %d files byte-identical" % (passed, tested))
        for p, why in failures[:20]:
            print("  FAIL", os.path.relpath(p, folder), why)
    return tested, passed, failures


def main(argv):
    if len(argv) >= 3 and argv[1] == "dis":
        text = disassemble(open(argv[2], "rb").read())
        if len(argv) > 3:
            open(argv[3], "w", encoding="utf-8", newline="\n").write(text)
        else:
            sys.stdout.write(text)
        return 0
    if len(argv) >= 3 and argv[1] == "asm":
        warnings = []
        data = assemble(open(argv[2], encoding="utf-8").read(), warnings.append)
        for w in warnings:
            print("warning:", w, file=sys.stderr)
        out = argv[3] if len(argv) > 3 else os.path.splitext(argv[2])[0] + ".scr"
        open(out, "wb").write(data)
        print("wrote %s (%d bytes)" % (out, len(data)))
        return 0
    if len(argv) >= 3 and argv[1] == "roundtrip":
        t, p, f = roundtrip(argv[2])
        return 0 if t == p else 1
    print(__doc__)
    return 2


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv))
    except AsmError as e:
        print("error:", e, file=sys.stderr)
        sys.exit(1)
