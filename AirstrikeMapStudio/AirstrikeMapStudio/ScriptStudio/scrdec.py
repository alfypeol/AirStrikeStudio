#!/usr/bin/env python3
"""
scrdec.py - decompile Air Strike 3D scripts (.scr) to C-like source.

    python scrdec.py script.scr [out.txt]
    python scrdec.py --stats <folder>        how many routines decompile cleanly

Model of the original compiler (derived from 391 scripts):
  * Registers r0..r15 form an expression stack. A sub-expression at depth d leaves its value in r<d>.
  * LOAD builds a REFERENCE to an entity field / vector component. Operand flags 0x0200 (a) and 0x2000 (b)
    dereference a register; 0x8000 on MOV/ADD/SUB/MUL stores through the reference (=, +=, -=, *=).
  * Call arguments travel in r0, r1, ...; CALL's b field is the register that receives the result.
    ENTER k / LEAVE k save and restore live registers around a call that sits inside an expression.
  * if / else / while compile to JZ and JMP; early exits are RETURN, a routine ends with END.
Anything that does not fit these patterns is printed as `goto`-style raw instructions and counted as "unstructured".
"""
import os
import struct
import sys

import scrasm
import scrsig

CMP = {0x05: "&", 0x07: "||", 0x08: "&&", 0x09: "==", 0x0A: "!=", 0x0B: ">", 0x0C: "<", 0x0D: ">=", 0x0E: "<=",
       0x01: "*", 0x02: "/", 0x03: "+", 0x04: "-"}
PREC = {"||": 1, "&&": 2, "==": 3, "!=": 3, "<": 4, ">": 4, "<=": 4, ">=": 4, "&": 5, "+": 6, "-": 6, "*": 7, "/": 7}


def f32(i):
    return struct.unpack("<f", struct.pack("<i", i))[0]


class Unstructured(Exception):
    """Raised when a routine cannot be turned into structured if/while code (hand-patched control flow)."""
    pass


# --------------------------------------------------------------- expressions (tuples)
# ('f', float) ('s', index) ('var', n) ('glob', name) ('field', base, idx) ('bin', op, l, r) ('un', op, e)
# ('call', id, target, args)  target: ('b', name) builtin | ('r', index) routine ; ('undef', reg)


def show(e, parent=0):
    k = e[0]
    if k == "f":
        v = f32(e[1])
        return ("%d" % v) if v == int(v) and abs(v) < 1e9 else ("%g" % v)
    if k == "s":
        return scrasm.quote(e[1])
    if k == "var":
        return "v%d" % e[1]
    if k == "glob":
        return e[1]
    if k == "field":
        base = show(e[1], 99)
        return "%s.f%d" % (base, e[2]) if e[1][0] in ("glob", "var") or e[1][0] == "field" and False else "%s[%d]" % (base, e[2]) \
            if e[1][0] == "field" else "%s.f%d" % (base, e[2])
    if k == "bin":
        p = PREC[e[1]]
        s = "%s %s %s" % (show(e[2], p), e[1], show(e[3], p + 1))
        return "(%s)" % s if p < parent else s
    if k == "un":
        return "%s%s" % (e[1], show(e[2], 9))
    if k == "call":
        tgt = e[2]
        nm = tgt[1]
        return "%s(%s)" % (nm, ", ".join(show(a) for a in e[3]))
    if k == "param":
        return "arg%d" % e[1]
    if k == "undef":
        return "?r%d" % e[1]
    return repr(e)


class Dec:
    """Structuring decompiler for one parsed script: turns routines into nested statement trees."""
    def __init__(self, P):
        self.P = P
        self.n = len(P.code)
        offs, o = [], 0
        for s in P.strings:
            offs.append(o)
            o += len(s) + 1
        self.off2idx = {}
        for k, off in enumerate(offs):
            self.off2idx.setdefault(off, k)
        self.first_zero = next((i for i, (op, a, b, c) in enumerate(P.code) if op == 0x1011 and b == 0), -1)
        self.next_id = 0
        self.unstructured = []

    # ------------------------------------------------------------ operand reading
    def string_at(self, i):
        op, a, b, c = self.P.code[i]
        if op == 0x1011 and b in self.off2idx and (b != 0 or i == self.first_zero):
            return self.off2idx[b]
        return None

    def opnd(self, st, hi, which, v):
        floatflag = hi & (0x0100 if which == "a" else 0x1000)
        if floatflag:
            return ("f", v)
        if v < 0:
            if -v <= len(self.P.defs):
                return ("glob", self.P.defs[-v - 1].decode("latin-1"))
            return ("glob", "#%d" % v)
        if v >= 16:
            return ("var", v)
        c = st["regs"][v]
        if c is None:
            return ("param", v)                       # read before written: an incoming call argument
        self.consume(st, c)
        st["fresh"].discard(v)
        return c

    def consume(self, st, e):
        """An expression that contains a call result is being used: the call is part of an expression."""
        if e[0] == "call":
            st["pending"].pop(e[1], None)
            for a in e[3]:
                self.consume(st, a)
        elif e[0] in ("bin",):
            self.consume(st, e[2])
            self.consume(st, e[3])
        elif e[0] in ("un",):
            self.consume(st, e[2])
        elif e[0] == "field":
            self.consume(st, e[1])

    # ------------------------------------------------------------ one instruction (no control flow)
    def step(self, st, i, out):
        op, a, b, c = self.P.code[i]
        low, hi = op & 0xFF, op & 0xFF00
        regs = st["regs"]
        if low == 0x13:                               # ENTER k: save register k (argument passing will clobber it)
            st["saved"].setdefault(a, []).append((regs[a], a in st["fresh"]))
            st["fresh"].discard(a)
            return
        if low == 0x14:                               # LEAVE k: restore it
            stack = st["saved"].get(a)
            if not stack:
                raise Unstructured("LEAVE %d without ENTER at %d" % (a, i))
            regs[a], was_fresh = stack.pop()
            if was_fresh:
                st["fresh"].add(a)                    # an argument already placed in r<a> is an argument again once restored
            return
        if low == 0x1E:                               # PUSHF
            st["pushed"] = True
            nxt = self.P.code[i + 1][0] & 0xFF if i + 1 < len(self.P.code) else None
            if not hi & 0x0100 and nxt != 0x1D:       # hand-patched: other code sits between PUSHF and its CALLP
                v = self.opnd(st, hi, "a", a)
                self.consume(st, v)
                out.append(("pushf", v))
                regs[0] = None
                st["fresh"].clear()
                return
            regs[0] = ("f", a) if hi & 0x0100 else self.opnd(st, hi, "a", a)
            st["fresh"].add(0)
            st["pushf"] = regs[0] if hi & 0x0100 else None
            st["pushf_over"] = False
            return
        if low == 0x12:                               # LOAD base.field -> reg
            base = self.opnd(st, 0, "a", a)
            regs[c] = ("field", base, b)
            st["fresh"].add(c)
            return
        if low == 0x11:                               # MOV
            s = self.string_at(i)
            if hi & 0x8000:                           # store through reference
                lv = regs[a]
                rhs = self.opnd(st, hi, "b", b)
                if lv is None or lv[0] != "field":
                    raise Unstructured("store through non-reference r%d at %d" % (a, i))
                self.consume(st, lv)
                out.append(("assign", lv, rhs))
                st["fresh"].clear()
                return
            src = ("s", self.P.strings[s]) if s is not None else self.opnd(st, hi, "b", b)
            self.dest(st, a, src, out, i)
            return
        if low in CMP:
            A = self.opnd(st, hi, "a", a)
            B = self.opnd(st, hi, "b", b)
            if hi & 0x8000:                           # compound store: *a op= b
                lv = regs[a]
                if lv is None or lv[0] != "field":
                    raise Unstructured("compound store through non-reference at %d" % i)
                self.consume(st, lv)
                out.append(("opassign", CMP[low], lv, B))
                st["fresh"].clear()
                return
            if low in (0x01, 0x02, 0x03, 0x04) and c == a and not hi & 0x0100 and (c >= 16 or c < 0):
                tgt = ("var", c) if c >= 16 else ("glob", self.P.defs[-c - 1].decode("latin-1"))
                out.append(("opassign", CMP[low], tgt, B))
                st["fresh"].clear()
                return
            self.dest(st, c, ("bin", CMP[low], A, B), out, i)
            return
        if low in (0x0F, 0x10):                       # NOT / NEG
            A = self.opnd(st, hi, "a", a)
            self.dest(st, c, ("un", "!" if low == 0x0F else "-", A), out, i)
            return
        if low in (0x1C, 0x1D):                       # CALL / CALLP
            tgt = ("b", self.P.funcs[-a - 1].decode("latin-1")) if a < 0 and -a <= len(self.P.funcs) else ("r", "r%d" % a)
            m = 0
            while m in st["fresh"]:
                m += 1
            pushed = st.pop("pushed", False)
            if (low == 0x1D and not pushed and m == 1 and regs[0] is not None and regs[0][0] == "call"
                    and (tgt[0] == "r" or tgt[1] == "sleep")):
                m = 0                                     # no PUSHF: the call result in r0 is a leftover, not the (single, PUSHF-carried) argument
            args = []
            for k in range(m):
                v = regs[k]
                args.append(v if v is not None else ("param", k))
                if v is not None:
                    self.consume(st, v)
            self.next_id += 1
            cid = self.next_id
            isp = low == 0x1D
            pf = st.pop("pushf", None)
            over = st.pop("pushf_over", False)
            if isp and pf is not None and pf[0] == "f" and over:
                isp = ("dur", pf)                     # PUSHF t overwritten by ordinary arguments: explicit duration
            call = ("call", cid, tgt, tuple(args), isp)
            st["pending"][cid] = len(out)
            out.append(("pcall", call))
            regs[b] = call
            st["fresh"] = {b} if (tgt[0] == "b" and tgt[1] in scrsig.RETURNS) else set()
            return
        raise Unstructured("unknown instruction %s at %d" % (hex(op), i))

    def dest(self, st, d, expr, out, i):
        if d == 0 and st.get("pushf") is not None:
            st["pushf_over"] = True                   # an ordinary argument replaced the pushed duration
        if d < 0 or d >= 16:                          # variable or global destination
            if d < 0:
                tgt = ("glob", self.P.defs[-d - 1].decode("latin-1")) if -d <= len(self.P.defs) else ("glob", "#%d" % d)
            else:
                tgt = ("var", d)
            self.consume(st, expr)
            out.append(("assign", tgt, expr))
            st["fresh"].clear()
        else:
            st["regs"][d] = expr
            st["fresh"].add(d)

    # ------------------------------------------------------------ structuring
    def new_state(self):
        return {"regs": [None] * 16, "fresh": set(), "pending": {}, "saved": {}}

    def finish(self, st, out):
        """Drop call statements whose value was consumed by an expression."""
        keep = []
        for s in out:
            if s[0] == "pcall":
                if s[1][1] not in st["pending"]:
                    continue                              # consumed: it is part of an expression
                s = ("callstmt", s[1])
            keep.append(s)
        return keep

    def block(self, i, end, st=None, loop=None):
        P = self.P
        st = st or self.new_state()
        out = []
        while i < end:
            op, a, b, c = P.code[i]
            low, hi = op & 0xFF, op & 0xFF00
            if low == 0x00:                           # END
                if i != end - 1:
                    raise Unstructured("END inside a block at %d" % i)
                i += 1
                continue
            if low == 0x18:
                if a != 0 or hi:                          # RETURN with a value (constant / variable), e.g. RETURN.0100 1.0 or RETURN v16
                    rv = self.opnd(st, hi, "a", a)
                    self.consume(st, rv)
                    out = self.finish(st, out)
                    out.append(("return", rv))
                else:
                    out = self.finish(st, out)
                    out.append(("return",))
                st = self.new_state()
                i += 1
                continue
            if low == 0x1B:                           # JMP: nop idiom, break, continue
                tgt = i + a
                out = self.finish(st, out)
                if a == 1:
                    out.append(("nop",))
                elif loop and tgt == loop[1]:
                    out.append(("break",))
                elif loop and tgt == loop[0]:
                    out.append(("continue",))
                else:
                    raise Unstructured("unstructured jump at %d -> %d" % (i, tgt))
                st = self.new_state()
                i += 1
                continue
            if low == 0x1A:
                cond = self.opnd(st, hi, "a", a)
                t = i + b
                out = self.finish(st, out)
                if not (i < t <= end):
                    raise Unstructured("jump out of block at %d" % i)
                last = P.code[t - 1]
                if t - 1 > i and (last[0] & 0xFF) == 0x1B and (t - 1) + last[1] <= i:      # backward jump: while
                    body = self.block(i + 1, t - 1, loop=((t - 1) + last[1], t))
                    out.append(("while", cond, body))
                    i = t
                elif t - 1 > i and (last[0] & 0xFF) == 0x1B and last[1] > 1 and (t - 1) + last[1] <= end:
                    u = (t - 1) + last[1]
                    then = self.block(i + 1, t - 1, loop=loop)
                    els = self.block(t, u, loop=loop)
                    out.append(("if", cond, then, els))
                    i = u
                else:
                    then = self.block(i + 1, t, loop=loop)
                    out.append(("if", cond, then, []))
                    i = t
                st = self.new_state()
                continue
            self.step(st, i, out)
            i += 1
        return self.finish(st, out)

    def routines(self):
        P = self.P
        starts = [0] + [i + 1 for i, (op, a, b, c) in enumerate(P.code) if op & 0xFF == 0 and i + 1 < self.n]
        ends = starts[1:] + [self.n]
        roles = {e: ["init", "main", "damaged", "touched", "callback"][k] for k, e in enumerate(P.entries) if e >= 0}
        return [(s, e, roles.get(s)) for s, e in zip(starts, ends)]

    def decompile(self):
        result = []
        for s, e, role in self.routines():
            try:
                body = self.block(s, e)
                result.append((s, e, role, body, None))
            except Unstructured as ex:
                result.append((s, e, role, None, str(ex)))
        return result


# --------------------------------------------------------------- printing
def render(body, ind=1):
    """Render a statement tree (as produced by Dec) to indented C-like text lines."""
    pad = "    " * ind
    lines = []
    for s in body:
        k = s[0]
        if k == "assign":
            lines.append("%s%s = %s;" % (pad, show(s[1]), show(s[2])))
        elif k == "opassign":
            lines.append("%s%s %s= %s;" % (pad, show(s[2]), s[1], show(s[3])))
        elif k == "callstmt":
            lines.append("%s%s;" % (pad, show(s[1])))
        elif k == "pushf":
            lines.append("%spushf(%s);" % (pad, show(s[1])))
        elif k == "return":
            lines.append("%sreturn%s;" % (pad, (" " + show(s[1])) if len(s) > 1 else ""))
        elif k in ("break", "continue", "nop"):
            lines.append("%s%s;" % (pad, k))
        elif k == "if":
            lines.append("%sif (%s) {" % (pad, show(s[1])))
            lines += render(s[2], ind + 1)
            if s[3]:
                lines.append("%s} else {" % pad)
                lines += render(s[3], ind + 1)
            lines.append("%s}" % pad)
        elif k == "while":
            lines.append("%swhile (%s) {" % (pad, show(s[1])))
            lines += render(s[2], ind + 1)
            lines.append("%s}" % pad)
        else:
            lines.append("%s// ?? %r" % (pad, s))
    return lines


def decompile_file(data):
    """Decompile RCSL bytes to a text listing, one block per routine; unstructured routines are marked in comments."""
    P = scrasm.parse_scr(data)
    D = Dec(P)
    res = D.decompile()
    out = []
    for s, e, role, body, err in res:
        out.append("routine_%d%s {   // instructions %d-%d" % (s, (" /* %s */" % role) if role else "", s, e - 1))
        if body is None:
            out.append("    // UNSTRUCTURED: %s" % err)
        else:
            out += render(body)
        out.append("}")
        out.append("")
    return "\n".join(out), res


def stats(folder):
    """Print how many scripts in folder the decompiler can fully structure."""
    total = clean = 0
    reasons = {}
    files = clean_files = 0
    for dp, dn, fn in os.walk(folder):
        for f in sorted(fn):
            if not f.endswith(".scr"):
                continue
            data = scrasm.read_bytes(os.path.join(dp, f))
            text, res = decompile_file(data)
            files += 1
            bad = 0
            for s, e, role, body, err in res:
                total += 1
                if body is None:
                    bad += 1
                    key = err.split(" at ")[0]
                    reasons[key] = reasons.get(key, 0) + 1
                else:
                    clean += 1
            clean_files += bad == 0
    print("scripts fully structured: %d / %d     routines: %d / %d" % (clean_files, files, clean, total))
    for k, v in sorted(reasons.items(), key=lambda kv: -kv[1])[:10]:
        print("  %4d  %s" % (v, k))


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "--stats":
        stats(sys.argv[2])
    elif len(sys.argv) >= 2:
        text, _ = decompile_file(scrasm.read_bytes(sys.argv[1]))
        if len(sys.argv) > 2:
            scrasm.write_text(sys.argv[2], text + "\n")
        else:
            print(text)
    else:
        print(__doc__)
