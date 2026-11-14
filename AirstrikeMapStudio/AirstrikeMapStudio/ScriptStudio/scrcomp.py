#!/usr/bin/env python3
"""
scrcomp.py - code generator: AST (as produced by scrdec) -> Air Strike 3D instructions.

The rules below reproduce the original compiler (derived from 391 scripts, checked by test_scrdec.py):
  * An expression compiled at depth d leaves its value in register r<d>. Left operands go to r<d>, right operands to r<d+1>.
  * Leaves (float constants, variables, globals) are used directly as operands; fields are LOADed into a register
    and flagged as dereferenced (0x0200 for operand a, 0x2000 for operand b).
  * A call at depth d > 0 first saves r0..r<d-1> with ENTER (flag 0x8000 on the register that holds the pending store
    target), evaluates its arguments, moves them into r0, r1, ..., calls with b = d (the result register) and restores
    with LEAVE in reverse order.
  * Assignments: lvalue in r0, right-hand side at depth 1; MOV/ADD/SUB/MUL with 0x8000 store through r0.
"""
import scrasm
import scrsig

OPCODE = {"*": 0x01, "/": 0x02, "+": 0x03, "-": 0x04, "&": 0x05, "||": 0x07, "&&": 0x08, "==": 0x09, "!=": 0x0A,
          ">": 0x0B, "<": 0x0C, ">=": 0x0D, "<=": 0x0E}
COMPOUND = {"+": 0x03, "-": 0x04, "*": 0x01, "/": 0x02}


LEAF = ("f", "var", "glob")


class CompileError(Exception):
    pass


class Gen:
    def __init__(self, P):
        self.P = P
        self.pool = []          # string table being built: one entry per literal use, in code order
        self.pool_size = 0
        self.defidx = {d.decode("latin-1"): k for k, d in enumerate(P.defs)}
        self.funcidx = {f.decode("latin-1"): k for k, f in enumerate(P.funcs)}
        self.code = []          # ('i', op, a, b, c) | ('jz', op, a, label) | ('jmp', label) | ('call', op, label, b) | ('L', label)
        self.nlabel = 0
        self.target_reg = None  # register holding the pending store target (flags ENTER with 0x8000)
        self.ref_regs = set()   # registers currently holding a reference (flags LEAVE with 0x8000)
        self.loops = []

    # ---------------------------------------------------------- helpers
    def label(self):
        self.nlabel += 1
        return "_L%d" % self.nlabel

    def emit(self, op, a=0, b=0, c=0):
        self.code.append(("i", op, a, b, c))

    def string(self, text):
        """Byte offset of a new string-table entry for this literal."""
        off = self.pool_size
        self.pool.append(text)
        self.pool_size += len(text) + 1
        return off

    def glob(self, name):
        if name not in self.defidx:
            raise CompileError("unknown global %s" % name)
        return -(self.defidx[name] + 1)

    # ---------------------------------------------------------- expressions
    def load_field(self, e, r):
        """Emit LOADs so that register r holds a reference to field expression e."""
        base, idx = e[1], e[2]
        if base[0] == "glob":
            self.emit(0x12, self.glob(base[1]), idx, r)
        elif base[0] == "var":
            self.emit(0x12, base[1], idx, r)
        elif base[0] == "field":
            self.load_field(base, r)
            self.emit(0x12, r, idx, r)
        else:
            raise CompileError("unsupported field base %r" % (base,))

    def operand(self, e, d, which):
        """(value, flag) for operand a (which=='a', register r<d>) or b (register r<d+1>)."""
        k = e[0]
        fl_float = 0x0100 if which == "a" else 0x1000
        fl_deref = 0x0200 if which == "a" else 0x2000
        reg = d if which == "a" else d + 1
        if k == "f":
            return e[1], fl_float
        if k == "param":
            return e[1], 0
        if k == "var":
            return e[1], 0
        if k == "glob":
            return self.glob(e[1]), 0
        if k == "field":
            self.load_field(e, reg)
            self.ref_regs.add(reg)
            return reg, fl_deref
        self.value_to_reg(e, reg)
        return reg, 0

    def value_to_reg(self, e, r):
        """Compile expression e so its value ends up in register r (r is the depth)."""
        k = e[0]
        if k == "f":
            self.emit(0x1011, r, e[1], 0)
        elif k == "s":
            self.emit(0x1011, r, self.string(e[1]), 0)
        elif k == "var":
            self.emit(0x11, r, e[1], 0)
        elif k == "param":
            self.emit(0x11, r, e[1], 0)
        elif k == "glob":
            self.emit(0x11, r, self.glob(e[1]), 0)
        elif k == "field":
            self.load_field(e, r)
            self.ref_regs.add(r)
        elif k == "bin":
            a, fa = self.operand(e[2], r, "a")
            b, fb = self.operand(e[3], r, "b")
            self.emit(OPCODE[e[1]] | fa | fb, a, b, r)
            self.ref_regs.discard(a)
            self.ref_regs.discard(r)                # a right-operand reference (b) is NOT cleared: the original compiler leaves it marked
        elif k == "un":
            a, fa = self.operand(e[2], r, "a")
            self.emit((0x0F if e[1] == "!" else 0x10) | fa, a, 0, r)
            self.ref_regs.discard(a)
            self.ref_regs.discard(r)
        elif k == "call":
            self.call(e, r)
        else:
            raise CompileError("cannot compile %r" % (e,))

    # ---------------------------------------------------------- calls
    def call(self, e, d, as_stmt=False):
        _, cid, tgt, args, isp = e
        saved = list(range(d))
        for r in saved:
            self.emit(0x13 | (0x8000 if (r == self.target_reg or (r == 0 and r in self.ref_regs)) else 0), r, 0, 0)
        if isinstance(isp, tuple):                      # latent(t) f(args): PUSHF t first, then ordinary arguments
            self.emit(0x011E, isp[1][1], 0, 0)
            for k, arg in enumerate(args):
                self.arg(arg, k, d)
        elif isp and len(args) == 1 and (tgt[0] == "r" or tgt[1] == "sleep"):
            a, fa = self.operand(args[0], d, "a")       # PUSHF carries the single argument (duration) of CALLP routine/sleep
            self.emit(0x1E | fa, a, 0, 0)
        else:
            for k, arg in enumerate(args):
                self.arg(arg, k, d)
        if tgt[0] == "b":
            if tgt[1] not in self.funcidx:
                raise CompileError("unknown function or routine %s (engine functions must be listed in 'functions'; routines are called as r<N>)" % tgt[1])
            a = -(self.funcidx[tgt[1]] + 1)
            self.code.append(("i", 0x1D if isp else 0x1C, a, d, 0))
        else:
            self.code.append(("call", 0x1D if isp else 0x1C, "R" + tgt[1], d))
        for r in reversed(saved):
            self.emit(0x14 | (0x8000 if r in self.ref_regs else 0), r, 0, r)

    def arg(self, arg, k, d):
        """Pass argument k in register r<k>."""
        t = d + k                                   # depth at which the argument is evaluated
        kind = arg[0]
        if kind == "f":
            self.emit(0x1011, k, arg[1], 0)
        elif kind == "s":
            self.emit(0x1011, k, self.string(arg[1]), 0)
        elif kind == "var":
            self.emit(0x11, k, arg[1], 0)
        elif kind == "glob":
            self.emit(0x11, k, self.glob(arg[1]), 0)
        elif kind == "field":
            if arg[1][0] != "field" and arg[2] in scrsig.VECTOR_FIELDS:      # vector field: passed by reference
                self.load_field(arg, t)
                if t != k:
                    self.emit(0x11, k, t, 0)
            else:                                                             # scalar: passed by value
                self.load_field(arg, t)
                self.emit(0x2011, k, t, 0)
        else:
            self.value_to_reg(arg, t)
            if t != k:
                self.emit(0x11, k, t, 0)

    # ---------------------------------------------------------- statements
    def cond_jz(self, cond, target):
        k = cond[0]
        if k == "var":
            self.code.append(("jz", 0x1A, cond[1], target))
        elif k == "glob":
            self.code.append(("jz", 0x1A, self.glob(cond[1]), target))
        elif k == "field":
            self.load_field(cond, 0)
            self.code.append(("jz", 0x1A | 0x0200, 0, target))
        else:
            self.value_to_reg(cond, 0)
            self.code.append(("jz", 0x1A, 0, target))

    def store(self, lv, rhs, opcode):
        """*lv = rhs  (opcode 0x11) or *lv op= rhs (0x03/0x04/0x01)."""
        self.load_field(lv, 0)
        self.ref_regs = {0}
        self.target_reg = 0
        k = rhs[0]
        if k == "f":
            b, fb = rhs[1], 0x1000
        elif k == "var":
            b, fb = rhs[1], 0
        elif k == "glob":
            b, fb = self.glob(rhs[1]), 0
        elif k == "field":
            self.load_field(rhs, 1)
            b, fb = 1, 0x2000
        elif k == "s":
            b, fb = self.string(rhs[1]), 0x1000
        else:
            self.value_to_reg(rhs, 1)
            b, fb = 1, 0
        self.target_reg = None
        self.ref_regs = set()
        self.emit(opcode | 0x8200 | fb, 0, b, 0)

    def assign_slot(self, dest, rhs):
        """dest is a variable slot (>=16) or a negative global number. The right-hand side is evaluated from depth 1
        (register r0 stays reserved for the destination)."""
        k = rhs[0]
        if k in ("f", "s"):
            self.emit(0x1011, dest, rhs[1] if k == "f" else self.string(rhs[1]), 0)
        elif k in ("var", "param"):
            self.emit(0x11, dest, rhs[1], 0)
        elif k == "glob":
            self.emit(0x11, dest, self.glob(rhs[1]), 0)
        elif k == "field":
            self.load_field(rhs, 1)
            self.emit(0x2011, dest, 1, 0)

        else:
            self.value_to_reg(rhs, 1)
            self.emit(0x11, dest, 1, 0)

    def block(self, stmts):
        for s in stmts:
            self.stmt(s)

    def stmt(self, s):
        k = s[0]
        if k == "assign":
            lv = s[1]
            if lv[0] == "field":
                self.store(lv, s[2], 0x11)
            elif lv[0] == "var":
                self.assign_slot(lv[1], s[2])
            elif lv[0] == "glob":
                self.assign_slot(self.glob(lv[1]), s[2])
            else:
                raise CompileError("bad assignment target %r" % (lv,))
        elif k == "opassign":
            tgt = s[2]
            if tgt[0] == "field":
                self.store(tgt, s[3], COMPOUND[s[1]])
            else:                                       # variable / global updated in place: OP v, rhs -> v
                slot = tgt[1] if tgt[0] == "var" else self.glob(tgt[1])
                b, fb = self.operand(s[3], 0, "b")
                self.emit(OPCODE[s[1]] | fb, slot, b, slot)
        elif k == "callstmt":
            self.call(s[1], 0, as_stmt=True)
        elif k == "pushf":
            a, fa = self.operand(s[1], 0, "a")
            self.emit(0x1E | fa, a, 0, 0)
            self.ref_regs = set()
        elif k == "return":
            if len(s) > 1:
                a, fa = self.operand(s[1], 0, "a")
                self.emit(0x18 | fa, a, 0, 0)
            else:
                self.emit(0x18)
        elif k == "nop":
            self.emit(0x1B, 1, 0, 0)
        elif k == "break":
            self.code.append(("jmp", self.loops[-1][1]))
        elif k == "continue":
            self.code.append(("jmp", self.loops[-1][0]))
        elif k == "if":
            cond, then, els = s[1], s[2], s[3]
            lelse, lend = self.label(), self.label()
            self.cond_jz(cond, lelse if els else lend)
            self.block(then)
            if els:
                self.code.append(("jmp", lend))
                self.code.append(("L", lelse))
                self.block(els)
            self.code.append(("L", lend))
        elif k == "while":
            head, exit_ = self.label(), self.label()
            self.code.append(("L", head))
            self.cond_jz(s[1], exit_)
            self.loops.append((head, exit_))
            self.block(s[2])
            self.loops.pop()
            self.code.append(("jmp", head))
            self.code.append(("L", exit_))
        else:
            raise CompileError("cannot compile statement %r" % (s,))

    # ---------------------------------------------------------- finishing
    def routine(self, name, body):
        self.code.append(("L", "R" + name))
        self.block(body)
        self.emit(0x00)

    def resolve(self):
        """Labels -> instruction list of (op, a, b, c)."""
        pos, idx = {}, 0
        for item in self.code:
            if item[0] == "L":
                pos[item[1]] = idx
            else:
                idx += 1
        self.pos = pos
        out, idx = [], 0
        for item in self.code:
            t = item[0]
            if t == "L":
                continue
            if t == "i":
                out.append(item[1:])
            elif t == "jz":
                out.append((item[1], item[2], pos[item[3]] - idx, 0))
            elif t == "jmp":
                out.append((0x1B, pos[item[1]] - idx, 0, 0))
            elif t == "call":
                if item[2] not in pos:
                    raise CompileError("call to unknown routine %s" % item[2][1:])
                out.append((item[1], pos[item[2]], item[3], 0))
            idx += 1
        return out
