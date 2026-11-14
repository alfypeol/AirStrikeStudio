#!/usr/bin/env python3
"""
scrsrc.py - C-like source for Air Strike 3D scripts:  .scr -> source -> .scr

    python scrsrc.py dec  script.scr [script.c]      decompile to source
    python scrsrc.py comp script.c   [script.scr]    compile source to a script
    python scrsrc.py roundtrip <folder>              prove dec -> comp reproduces each script byte for byte

LANGUAGE (everything below is lossless: printing then parsing gives back the same program)

    slots 31;                                   // variable slots the compiler allocates (>= highest variable used + 1)
    entry init = r0;  entry main = r113;        // init, main, damaged, touched, callback
    cash "helic_dead", "helic_campfire";        // resources to pre-load
    globals self, other, frametime, camera;     // names the engine provides, in table order
    functions create, remove, sleep;            // engine functions you call, in table order
    var vec v23;   var float v18 = 0;           // declarations of script variables

    routine r113 {
        if (!self.f4) {                         // self.f5 = a field; self.f5[2] = component of a vector field
            v16 = create("boss1_sphere", self.f5);
            v21 += 1;                           // = += -= *= /=
            while (v21 < 10) { ... }
            latent sleep(2);                    // CALLP: wait / run for a duration
            latent(0.2) lMoveToNextWP(1);       // hand-patched: explicit PUSHF duration before a call that has its own argument
            pushf(0.2 + 0.4 * random());        // hand-patched: bare PUSHF, the CALLP follows after other statements
            return;
        }
    }

  Expressions: numbers, "strings", self/other/... (globals), v16 (script variables), arg0 (incoming call arguments),
  x.f5 (field), x[2] (component), calls, ! - * / + - & < > <= >= == != && ||   (precedence as in the table PREC below).
  Every string literal use gets its own copy in the table, in code order (the original compiler does the same).
"""
import os
import re
import struct
import sys

import scrasm
import scrcomp
import scrdec
import scrsig

PREC = scrdec.PREC
ROLES = ["init", "main", "damaged", "touched", "callback"]
IDENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")


class SrcError(Exception):
    pass


def f32(bits):
    return struct.unpack("<f", struct.pack("<i", bits))[0]


def bits32(f):
    return struct.unpack("<i", struct.pack("<f", f))[0]


# ============================================================ printing
def num(bits):
    f = f32(bits)
    if f != f or f in (float("inf"), float("-inf")):
        return "bits(0x%08x)" % (bits & 0xFFFFFFFF)
    if f == int(f) and abs(f) < 1e9:
        t = "%d" % int(f) if not (f == 0 and str(f).startswith("-")) else "-0"
    else:
        t = None
        for p in range(1, 10):
            c = "%.*g" % (p, f)
            if bits32(float(c)) == bits:
                t = c
                break
        if t is None:
            return "bits(0x%08x)" % (bits & 0xFFFFFFFF)
        if "e" in t and 1e-4 <= abs(f) < 1e9:
            from decimal import Decimal
            t = format(Decimal(t), "f")
    if bits32(float(t)) != bits:
        return "bits(0x%08x)" % (bits & 0xFFFFFFFF)
    return t


def pexpr(e, parent=0):
    k = e[0]
    if k == "f":
        return num(e[1])
    if k == "s":
        return scrasm.quote(e[1])
    if k == "var":
        return "v%d" % e[1]
    if k == "param":
        return "arg%d" % e[1]
    if k == "glob":
        return e[1]
    if k == "field":
        base = pexpr(e[1], 99)
        return ("%s[%d]" % (base, e[2])) if e[1][0] == "field" else ("%s.f%d" % (base, e[2]))
    if k == "bin":
        p = PREC[e[1]]
        s = "%s %s %s" % (pexpr(e[2], p), e[1], pexpr(e[3], p + 1))
        return "(%s)" % s if p < parent else s
    if k == "un":
        inner = e[2]
        if inner[0] == "f":                      # keep a NEG of a constant distinct from a negative literal
            return "%s(%s)" % (e[1], pexpr(inner))
        return "%s%s" % (e[1], pexpr(inner, 9))
    if k == "call":
        return pcall(e)
    raise SrcError("cannot print %r" % (e,))


def pcall(c):
    _, cid, tgt, args, isp = c
    if isinstance(isp, tuple):
        lat = "latent(%s) " % pexpr(isp[1])
    else:
        lat = "latent " if isp else ""
    return "%s%s(%s)" % (lat, tgt[1], ", ".join(pexpr(a) for a in args))


def pblock(body, ind):
    pad = "    " * ind
    out = []
    for s in body:
        k = s[0]
        if k == "assign":
            out.append("%s%s = %s;" % (pad, pexpr(s[1]), pexpr(s[2])))
        elif k == "opassign":
            out.append("%s%s %s= %s;" % (pad, pexpr(s[2]), s[1], pexpr(s[3])))
        elif k == "callstmt":
            out.append("%s%s;" % (pad, pcall(s[1])))
        elif k == "pushf":
            out.append("%spushf(%s);" % (pad, pexpr(s[1])))
        elif k == "return" and len(s) > 1:
            out.append("%sreturn %s;" % (pad, pexpr(s[1])))
        elif k in ("return", "break", "continue", "nop"):
            out.append("%s%s;" % (pad, k))
        elif k == "if":
            out.append("%sif (%s) {" % (pad, pexpr(s[1])))
            out += pblock(s[2], ind + 1)
            els = s[3]
            while els:
                if len(els) == 1 and els[0][0] == "if":
                    out.append("%s} else if (%s) {" % (pad, pexpr(els[0][1])))
                    out += pblock(els[0][2], ind + 1)
                    els = els[0][3]
                else:
                    out.append("%s} else {" % pad)
                    out += pblock(els, ind + 1)
                    els = []
            out.append("%s}" % pad)
        elif k == "while":
            out.append("%swhile (%s) {" % (pad, pexpr(s[1])))
            out += pblock(s[2], ind + 1)
            out.append("%s}" % pad)
        else:
            raise SrcError("cannot print statement %r" % (s,))
    return out


def decompile_to_source(data, verify=True):
    """Source text for a script, or None if it cannot be reproduced exactly (hand-patched code).
    With verify=True (default) the source is compiled again and must give back the identical bytes."""
    src = _decompile(data)
    if src is None or not verify:
        return src
    try:
        return src if compile_source(src) == data else None
    except (SrcError, scrcomp.CompileError, KeyError, ValueError):
        return None


def _decompile(data):
    P = scrasm.parse_scr(data)
    D = scrdec.Dec(P)
    res = D.decompile()
    if any(r[3] is None for r in res):
        return None
    L = ["// AS3D script source (scrsrc)", "slots %d;" % P.slots]
    for k, e in enumerate(P.entries):
        if e >= 0:
            L.append("entry %s = r%d;" % (ROLES[k], e))
    if P.cash:
        L.append("cash %s;" % ", ".join(scrasm.quote(n) + (" %d" % kind if kind else "") for kind, n in P.cash))
    if P.defs:
        L.append("globals %s;" % ", ".join(d.decode("latin-1") for d in P.defs))
    if P.funcs:
        L.append("functions %s;" % ", ".join(f.decode("latin-1") for f in P.funcs))
    for ty, sl, val in P.data:
        if ty == 2:
            L.append("var float v%d = %s;" % (sl, num(val)))
        elif ty == 3 and val == sl + 1:
            L.append("var vec v%d;" % sl)
        else:
            L.append("data %d %d 0x%08x;" % (ty, sl, val & 0xFFFFFFFF))
    L.append("")
    for s, e, role, body, err in res:
        if s == e:                                    # empty script: no instructions, no routine
            continue
        L.append("routine r%d {" % s)
        L += pblock(body, 1)
        L.append("}")
        L.append("")
    return "\n".join(L)


# ============================================================ tokenizer / parser
TOKEN = re.compile(r"""
    (?P<ws>\s+|//[^\n]*|/\*.*?\*/)
  | (?P<num>\d+\.\d*(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?|\d+(?:[eE][-+]?\d+)?)
  | (?P<hex>0[xX][0-9a-fA-F]+)
  | (?P<str>"(?:[^"\\]|\\.)*")
  | (?P<id>[A-Za-z_][A-Za-z0-9_]*)
  | (?P<op>\|\||&&|==|!=|<=|>=|\+=|-=|\*=|/=|[-+*/&!<>=.,;(){}\[\]])
""", re.X | re.S)


def tokenize(text):
    pos, toks, line = 0, [], 1
    while pos < len(text):
        m = TOKEN.match(text, pos)
        if not m:
            raise SrcError("line %d: unexpected character %r" % (line, text[pos]))
        kind = m.lastgroup
        tok = m.group()
        if kind != "ws":
            toks.append((kind, tok, line))
        line += tok.count("\n")
        pos = m.end()
    toks.append(("eof", "", line))
    return toks


class Parser:
    def __init__(self, text):
        self.t = tokenize(text)
        self.i = 0
        self.routines = {m.group(1) for m in re.finditer(r"\broutine\s+([A-Za-z_]\w*)", text)}
        self.globals = set()
        self.next_id = 0
        # directive results
        self.slots = 16
        self.entries = {}
        self.cash = []
        self.defs = []
        self.funcs = []
        self.data = []
        self.body = []       # [(name, stmts)]

    # ---- token helpers
    def peek(self, k=0):
        return self.t[self.i + k]

    def err(self, msg):
        raise SrcError("line %d: %s (at %r)" % (self.peek()[2], msg, self.peek()[1]))

    def eat(self, text=None, kind=None):
        tk = self.peek()
        if (text is not None and tk[1] != text) or (kind is not None and tk[0] != kind):
            self.err("expected %s" % (text or kind))
        self.i += 1
        return tk

    def accept(self, text):
        if self.peek()[1] == text and self.peek()[0] in ("op", "id"):
            self.i += 1
            return True
        return False

    # ---- file
    def parse(self):
        while self.peek()[0] != "eof":
            tk = self.peek()
            w = tk[1]
            if w == "slots":
                self.i += 1
                self.slots = int(self.eat(kind="num")[1])
                self.eat(";")
            elif w == "entry":
                self.i += 1
                role = self.eat(kind="id")[1]
                if role not in ROLES:
                    self.err("entry role must be one of %s" % ROLES)
                self.eat("=")
                self.entries[ROLES.index(role)] = self.eat(kind="id")[1]
                self.eat(";")
            elif w == "cash":
                self.i += 1
                while True:
                    name = scrasm.unquote(self.eat(kind="str")[1])
                    kind = int(self.eat(kind="num")[1]) if self.peek()[0] == "num" else 0
                    self.cash.append((kind, name))
                    if not self.accept(","):
                        break
                self.eat(";")
            elif w in ("globals", "functions"):
                self.i += 1
                lst = self.defs if w == "globals" else self.funcs
                while True:
                    lst.append(self.eat(kind="id")[1].encode("latin-1"))
                    if not self.accept(","):
                        break
                self.eat(";")
                if w == "globals":
                    self.globals = {d.decode() for d in self.defs}
            elif w == "var":
                self.i += 1
                typ = self.eat(kind="id")[1]
                name = self.eat(kind="id")[1]
                m = re.match(r"^v(\d+)$", name)
                if not m:
                    self.err("variables are named v<slot>")
                slot = int(m.group(1))
                if typ == "float":
                    self.eat("=")
                    self.data.append((2, slot, self.number_bits()))
                elif typ == "vec":
                    self.data.append((3, slot, slot + 1))
                else:
                    self.err("var type must be float or vec")
                self.eat(";")
            elif w == "data":
                self.i += 1
                ty = int(self.eat(kind="num")[1])
                sl = int(self.eat(kind="num")[1])
                v = int(self.eat(kind="hex")[1], 16)
                self.data.append((ty, sl, v - (1 << 32) if v >= 1 << 31 else v))
                self.eat(";")
            elif w == "routine":
                self.i += 1
                name = self.eat(kind="id")[1]
                self.eat("{")
                self.body.append((name, self.block()))
            else:
                self.err("expected a declaration or routine")
        return self

    def number_bits(self):
        neg = self.accept("-")
        tk = self.peek()
        if tk[0] == "id" and tk[1] == "bits":
            self.i += 1
            self.eat("(")
            v = int(self.eat(kind="hex")[1], 16)
            self.eat(")")
            return v - (1 << 32) if v >= 1 << 31 else v
        f = float(self.eat(kind="num")[1])
        return bits32(-f if neg else f)

    # ---- statements
    def block(self):
        out = []
        while not self.accept("}"):
            if self.peek()[0] == "eof":
                self.err("missing }")
            out.append(self.stmt())
        return out

    def stmt(self):
        tk = self.peek()
        w = tk[1]
        if tk[0] == "id":
            if w in ("return", "break", "continue", "nop"):
                self.i += 1
                if w == "return" and self.peek()[1] != ";":    # return <value>
                    e = self.expr()
                    self.eat(";")
                    return ("return", e)
                self.eat(";")
                return (w,)
            if w == "pushf" and self.peek(1)[1] == "(":     # pushf(expr): push the duration for a later latent call
                self.i += 2
                e = self.expr()
                self.eat(")")
                self.eat(";")
                return ("pushf", e)
            if w == "if":
                return self.if_stmt()
            if w == "while":
                self.i += 1
                self.eat("(")
                cond = self.expr()
                self.eat(")")
                self.eat("{")
                return ("while", cond, self.block())
        e = self.expr()
        op = self.peek()[1]
        if op in ("=", "+=", "-=", "*=", "/=") and self.peek()[0] == "op":
            self.i += 1
            if e[0] not in ("field", "var", "glob"):
                self.err("cannot assign to this")
            rhs = self.expr()
            self.eat(";")
            return ("assign", e, rhs) if op == "=" else ("opassign", op[0], e, rhs)
        if e[0] == "call":
            self.eat(";")
            return ("callstmt", e)
        self.err("expected a statement")

    def if_stmt(self):
        self.i += 1
        self.eat("(")
        cond = self.expr()
        self.eat(")")
        self.eat("{")
        then = self.block()
        els = []
        if self.peek()[1] == "else" and self.peek()[0] == "id":
            self.i += 1
            if self.peek()[1] == "if" and self.peek()[0] == "id":
                els = [self.if_stmt()]
            else:
                self.eat("{")
                els = self.block()
        return ("if", cond, then, els)

    # ---- expressions (precedence climbing)
    def expr(self, minp=1):
        left = self.unary()
        while True:
            tk = self.peek()
            op = tk[1]
            if tk[0] != "op" or op not in PREC or PREC[op] < minp:
                return left
            self.i += 1
            right = self.expr(PREC[op] + 1)
            left = ("bin", op, left, right)

    def unary(self):
        tk = self.peek()
        if tk[0] == "op" and tk[1] in ("!", "-"):
            self.i += 1
            nxt = self.peek()
            if tk[1] == "-" and nxt[0] == "num":                    # negative literal
                self.i += 1
                return self.postfix(("f", bits32(-float(nxt[1]))))
            return ("un", tk[1], self.unary())
        return self.postfix(self.primary())

    def postfix(self, e):
        while True:
            tk = self.peek()
            if tk[1] == "." and tk[0] == "op":
                self.i += 1
                name = self.eat(kind="id")[1]
                m = re.match(r"^f(\d+)$", name)
                if not m:
                    self.err("fields are written .f<number>")
                e = ("field", e, int(m.group(1)))
            elif tk[1] == "[" and tk[0] == "op":
                self.i += 1
                k = int(self.eat(kind="num")[1])
                self.eat("]")
                e = ("field", e, k)
            else:
                return e

    def primary(self):
        tk = self.peek()
        kind, w = tk[0], tk[1]
        if kind == "num":
            self.i += 1
            return ("f", bits32(float(w)))
        if kind == "str":
            self.i += 1
            return ("s", scrasm.unquote(w))
        if kind == "op" and w == "(":
            self.i += 1
            e = self.expr()
            self.eat(")")
            return e
        if kind == "id":
            self.i += 1
            if w == "bits" and self.peek()[1] == "(":
                self.eat("(")
                v = int(self.eat(kind="hex")[1], 16)
                self.eat(")")
                return ("f", v - (1 << 32) if v >= 1 << 31 else v)
            isp = False
            if w == "latent":
                isp = True
                if self.peek()[1] == "(" and self.peek()[0] == "op":     # latent(<seconds>) f(args): explicit duration push
                    self.i += 1
                    dur = self.expr()
                    self.eat(")")
                    if dur[0] != "f":
                        self.err("latent(...) duration must be a number")
                    isp = ("dur", dur)
                w = self.eat(kind="id")[1]
            if self.peek()[1] == "(" and self.peek()[0] == "op":
                self.i += 1
                args = []
                if not self.accept(")"):
                    while True:
                        args.append(self.expr())
                        if self.accept(")"):
                            break
                        self.eat(",")
                self.next_id += 1
                tgt = ("r", w) if w in self.routines else ("b", w)
                return ("call", self.next_id, tgt, tuple(args), isp)
            if isp:
                self.err("latent needs a call")
            m = re.match(r"^v(\d+)$", w)
            if m:
                return ("var", int(m.group(1)))
            m = re.match(r"^arg(\d+)$", w)
            if m:
                return ("param", int(m.group(1)))
            if w not in self.globals:
                self.err("unknown name %r (declare it in 'globals')" % w)
            return ("glob", w)
        self.err("expected an expression")


# ============================================================ compile
def _walk(e, fn):
    if not isinstance(e, tuple):
        return
    fn(e)
    if e[0] == "bin":
        _walk(e[2], fn), _walk(e[3], fn)
    elif e[0] == "un":
        _walk(e[2], fn)
    elif e[0] == "field":
        _walk(e[1], fn)
    elif e[0] == "call":
        for a in e[3]:
            _walk(a, fn)


def _walk_stmts(body, fn):
    for s in body:
        k = s[0]
        if k == "assign":
            _walk(s[1], fn), _walk(s[2], fn)
        elif k == "opassign":
            _walk(s[2], fn), _walk(s[3], fn)
        elif k in ("callstmt", "pushf"):
            _walk(s[1], fn)
        elif k == "if":
            _walk(s[1], fn), _walk_stmts(s[2], fn), _walk_stmts(s[3], fn)
        elif k == "while":
            _walk(s[1], fn), _walk_stmts(s[2], fn)


def compile_source(text, warn=None):
    """Compile source text to a .scr. Errors raise SrcError; suspicious-but-legal things go to warn(msg)."""
    warn = warn or (lambda m: None)
    ps = Parser(text).parse()
    top = [ps.slots - 1]
    for ty, sl, val in ps.data:
        top.append(sl + (3 if ty == 3 else 0))

    def check(e):
        if e[0] == "var" and e[1] >= ps.slots:
            raise SrcError("variable v%d is used but 'slots' is only %d - raise it to at least %d" % (e[1], ps.slots, e[1] + 1))
        if e[0] == "call" and e[2][0] == "b" and e[2][1] in scrsig.ARITY and len(e[3]) not in scrsig.ARITY[e[2][1]]:
            warn("%s() is called with %d argument(s); the original scripts only use %s"
                 % (e[2][1], len(e[3]), "/".join(str(a) for a in scrsig.ARITY[e[2][1]])))
    for name, body in ps.body:
        _walk_stmts(body, check)
    for ty, sl, val in ps.data:
        if sl + (3 if ty == 3 else 0) >= ps.slots:
            raise SrcError("declaration at v%d does not fit in 'slots %d'" % (sl, ps.slots))
    P = scrasm.Parsed()
    P.slots = ps.slots
    P.cash = ps.cash
    P.defs = ps.defs
    P.funcs = ps.funcs
    P.data = ps.data
    G = scrcomp.Gen(P)
    names = [n for n, _ in ps.body]
    if len(set(names)) != len(names):
        raise SrcError("duplicate routine names")
    for name, body in ps.body:
        G.routine(name, body)
    try:
        P.code = G.resolve()
    except scrcomp.CompileError as e:
        raise SrcError(str(e))
    P.strings = G.pool
    for role, name in ps.entries.items():
        lab = "R" + name
        if lab not in G.pos:
            raise SrcError("entry %s: unknown routine %s" % (ROLES[role], name))
        P.entries[role] = G.pos[lab]
    return scrasm.build_scr(P)


def roundtrip(folder, verbose=True):
    files = []
    for dp, dn, fn in os.walk(folder):
        for f in sorted(fn):
            p = os.path.join(dp, f)
            if open(p, "rb").read(4) == b"RCSL":
                files.append(p)
    exact = unstructured = bad = 0
    problems = []
    for p in sorted(files):
        data = open(p, "rb").read()
        try:
            src = decompile_to_source(data)
            if src is None:
                unstructured += 1
                continue
            back = compile_source(src)
            if back == data:
                exact += 1
            else:
                bad += 1
                problems.append((p, "bytes differ"))
        except (SrcError, scrcomp.CompileError, scrasm.AsmError, KeyError) as e:
            bad += 1
            problems.append((p, "%s: %s" % (type(e).__name__, e)))
    if verbose:
        print("source round trip: %d exact, %d hand-patched (need asm), %d failed, of %d scripts"
              % (exact, unstructured, bad, len(files)))
        for p, why in problems[:15]:
            print("   FAIL", os.path.relpath(p, folder), why)
    return exact, unstructured, bad, len(files), problems


def main(argv):
    if len(argv) >= 3 and argv[1] == "dec":
        src = decompile_to_source(open(argv[2], "rb").read())
        if src is None:
            print("this script has hand-patched (unstructured) code; use scrasm.py dis instead", file=sys.stderr)
            return 1
        if len(argv) > 3:
            open(argv[3], "w", encoding="utf-8", newline="\n").write(src + "\n")
        else:
            print(src)
        return 0
    if len(argv) >= 3 and argv[1] == "comp":
        data = compile_source(open(argv[2], encoding="utf-8").read(), lambda m: print("warning:", m, file=sys.stderr))
        out = argv[3] if len(argv) > 3 else os.path.splitext(argv[2])[0] + ".scr"
        open(out, "wb").write(data)
        print("wrote %s (%d bytes)" % (out, len(data)))
        return 0
    if len(argv) >= 3 and argv[1] == "roundtrip":
        e, u, b, n, _ = roundtrip(argv[2])
        return 0 if b == 0 else 1
    print(__doc__)
    return 2


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv))
    except (SrcError, scrcomp.CompileError, scrasm.AsmError) as e:
        print("error:", e, file=sys.stderr)
        sys.exit(1)
