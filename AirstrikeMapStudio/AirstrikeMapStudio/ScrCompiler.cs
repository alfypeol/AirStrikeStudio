using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AirstrikeStudio
{
    public sealed partial class ScrFile
    {
        private static readonly HashSet<int> VectorFields = new HashSet<int> { 3, 5, 6, 8, 14, 17, 20, 28 };

        private static readonly Dictionary<string, int> OpcodeMap = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "*", 0x01 }, { "/", 0x02 }, { "+", 0x03 }, { "-", 0x04 }, { "&", 0x05 },
            { "||", 0x07 }, { "&&", 0x08 }, { "==", 0x09 }, { "!=", 0x0A },
            { ">", 0x0B }, { "<", 0x0C }, { ">=", 0x0D }, { "<=", 0x0E }
        };

        private static readonly Dictionary<string, int> CompoundMap = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "+", 0x03 }, { "-", 0x04 }, { "*", 0x01 }, { "/", 0x02 }
        };

        private static readonly Dictionary<string, int> PrecMap = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "||", 1 }, { "&&", 2 }, { "==", 3 }, { "!=", 3 },
            { "<", 4 }, { ">", 4 }, { "<=", 4 }, { ">=", 4 },
            { "&", 5 }, { "+", 6 }, { "-", 6 }, { "*", 7 }, { "/", 7 }
        };

        /// <summary>
        /// Compiles C-like script source text (.c format produced by DecompileToSource)
        /// directly into a fully structured, byte-compatible ScrFile bytecode object.
        /// Native C# implementation of ScriptStudio's scrsrc.py &amp; scrcomp.py.
        /// </summary>
        public static ScrFile CompileSource(string sourceText)
        {
            if (string.IsNullOrWhiteSpace(sourceText))
                throw new InvalidDataException("Source text is empty.");

            var parser = new ScrSrcParser(sourceText);
            parser.Parse();

            var gen = new ScrCodeGen(parser);
            foreach (var r in parser.Routines)
            {
                gen.CompileRoutine(r.Key, r.Value);
            }

            return gen.BuildScrFile();
        }

        #region Tokenizer and AST

        private struct ScrToken
        {
            public string Kind; // "ws", "num", "hex", "str", "id", "op", "eof"
            public string Text;
            public int Line;
        }

        private static readonly Regex TokenRegex = new Regex(
            @"(?<ws>\s+|//[^\r\n]*|/\*.*?\*/)" +
            @"|(?<hex>0[xX][0-9a-fA-F]+)" +
            @"|(?<num>\d+\.\d*(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?|\d+(?:[eE][-+]?\d+)?)" +
            @"|(?<str>""(?:[^""\\]|\\.)*"")" +
            @"|(?<id>[A-Za-z_][A-Za-z0-9_]*)" +
            @"|(?<op>\|\||&&|==|!=|<=|>=|\+=|-=|\*=|/=|[-+*/&!<>=.,;(){}\[\]])",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static List<ScrToken> TokenizeSource(string text)
        {
            var tokens = new List<ScrToken>();
            int pos = 0;
            int line = 1;
            while (pos < text.Length)
            {
                var m = TokenRegex.Match(text, pos);
                if (!m.Success || m.Index != pos)
                    throw new InvalidDataException(string.Format("Line {0}: Unexpected character '{1}'", line, text[pos]));

                string kind = "";
                if (m.Groups["ws"].Success) kind = "ws";
                else if (m.Groups["hex"].Success) kind = "hex";
                else if (m.Groups["num"].Success) kind = "num";
                else if (m.Groups["str"].Success) kind = "str";
                else if (m.Groups["id"].Success) kind = "id";
                else if (m.Groups["op"].Success) kind = "op";

                string tok = m.Value;
                if (kind != "ws")
                {
                    tokens.Add(new ScrToken { Kind = kind, Text = tok, Line = line });
                }

                for (int i = 0; i < tok.Length; i++)
                {
                    if (tok[i] == '\n') line++;
                }
                pos += tok.Length;
            }
            tokens.Add(new ScrToken { Kind = "eof", Text = "", Line = line });
            return tokens;
        }

        private static string UnquoteString(string s)
        {
            if (s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
                s = s.Substring(1, s.Length - 2);
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char c = s[i];
                    if (c == 'n') sb.Append('\n');
                    else if (c == 'r') sb.Append('\r');
                    else if (c == 't') sb.Append('\t');
                    else sb.Append(c);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }


        #endregion

        #region Source Parser

        private sealed class ScrSrcParser
        {
            private readonly List<ScrToken> tokens;
            private int index;

            public int Slots = 16;
            public readonly Dictionary<int, string> Entries = new Dictionary<int, string>();
            public readonly List<Tuple<byte, string>> Cash = new List<Tuple<byte, string>>();
            public readonly List<string> Defs = new List<string>();
            public readonly List<string> Funcs = new List<string>();
            public readonly List<Tuple<ushort, ushort, int>> Data = new List<Tuple<ushort, ushort, int>>();
            public readonly List<KeyValuePair<string, List<Stmt>>> Routines = new List<KeyValuePair<string, List<Stmt>>>();

            private readonly HashSet<string> routineNames = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> globalNames = new HashSet<string>(StringComparer.Ordinal);

            public ScrSrcParser(string text)
            {
                tokens = TokenizeSource(text);
                // Pre-scan routine names
                var rMatch = Regex.Matches(text, @"\broutine\s+([A-Za-z_][A-Za-z0-9_]*)");
                foreach (Match m in rMatch)
                {
                    routineNames.Add(m.Groups[1].Value);
                }
            }

            private ScrToken Peek(int k = 0)
            {
                int p = index + k;
                return (p < tokens.Count) ? tokens[p] : tokens[tokens.Count - 1];
            }

            private void Error(string msg)
            {
                var tk = Peek();
                throw new InvalidDataException(string.Format("Line {0}: {1} (at '{2}')", tk.Line, msg, tk.Text));
            }

            private ScrToken Eat(string text = null, string kind = null)
            {
                var tk = Peek();
                if ((text != null && tk.Text != text) || (kind != null && tk.Kind != kind))
                    Error(string.Format("Expected '{0}'", text ?? kind));
                index++;
                return tk;
            }

            private bool Accept(string text)
            {
                if (Peek().Text == text && (Peek().Kind == "op" || Peek().Kind == "id"))
                {
                    index++;
                    return true;
                }
                return false;
            }

            public void Parse()
            {
                while (Peek().Kind != "eof")
                {
                    var tk = Peek();
                    string w = tk.Text;
                    if (w == "slots")
                    {
                        index++;
                        Slots = int.Parse(Eat(kind: "num").Text, CultureInfo.InvariantCulture);
                        Eat(";");
                    }
                    else if (w == "entry")
                    {
                        index++;
                        string role = Eat(kind: "id").Text;
                        int roleIdx = Array.IndexOf(RoleNames, role);
                        if (roleIdx < 0)
                            Error("Entry role must be one of: init, main, damaged, touched, callback");
                        Eat("=");
                        string target = Eat(kind: "id").Text;
                        Eat(";");
                        Entries[roleIdx] = target;
                    }
                    else if (w == "cash")
                    {
                        index++;
                        while (true)
                        {
                            string str = UnquoteString(Eat(kind: "str").Text);
                            byte kind = 0;
                            if (Peek().Kind == "num")
                                kind = byte.Parse(Eat(kind: "num").Text, CultureInfo.InvariantCulture);
                            Cash.Add(Tuple.Create(kind, str));
                            if (!Accept(",")) break;
                        }
                        Eat(";");
                    }
                    else if (w == "globals" || w == "functions")
                    {
                        index++;
                        var list = (w == "globals") ? Defs : Funcs;
                        while (true)
                        {
                            string item = Eat(kind: "id").Text;
                            list.Add(item);
                            if (w == "globals") globalNames.Add(item);
                            if (!Accept(",")) break;
                        }
                        Eat(";");
                    }
                    else if (w == "var")
                    {
                        index++;
                        string typ = Eat(kind: "id").Text;
                        string name = Eat(kind: "id").Text;
                        var m = Regex.Match(name, @"^v(\d+)$");
                        if (!m.Success) Error("Variables must be named v<slot> (e.g. v16)");
                        ushort slot = ushort.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

                        if (typ == "float")
                        {
                            Eat("=");
                            int bits = NumberBits();
                            Data.Add(Tuple.Create((ushort)2, slot, bits));
                        }
                        else if (typ == "vec")
                        {
                            Data.Add(Tuple.Create((ushort)3, slot, (int)(slot + 1)));
                        }
                        else
                        {
                            Error("Variable type must be 'float' or 'vec'");
                        }
                        Eat(";");
                    }
                    else if (w == "data")
                    {
                        index++;
                        ushort ty = ushort.Parse(Eat(kind: "num").Text, CultureInfo.InvariantCulture);
                        ushort sl = ushort.Parse(Eat(kind: "num").Text, CultureInfo.InvariantCulture);
                        string hexStr = Eat(kind: "hex").Text;
                        uint uv = Convert.ToUInt32(hexStr, 16);
                        int val = unchecked((int)uv);
                        Data.Add(Tuple.Create(ty, sl, val));
                        Eat(";");
                    }
                    else if (w == "routine")
                    {
                        index++;
                        string rName = Eat(kind: "id").Text;
                        Eat("{");
                        var stmts = Block();
                        Routines.Add(new KeyValuePair<string, List<Stmt>>(rName, stmts));
                    }
                    else
                    {
                        Error("Expected a declaration or routine");
                    }
                }
            }

            private int NumberBits()
            {
                bool neg = Accept("-");
                var tk = Peek();
                if (tk.Kind == "id" && tk.Text == "bits")
                {
                    index++;
                    Eat("(");
                    string hex = Eat(kind: "hex").Text;
                    Eat(")");
                    uint uv = Convert.ToUInt32(hex, 16);
                    return unchecked((int)uv);
                }
                float f = float.Parse(Eat(kind: "num").Text, CultureInfo.InvariantCulture);
                return FloatToBits(neg ? -f : f);
            }

            private List<Stmt> Block()
            {
                var list = new List<Stmt>();
                while (!Accept("}"))
                {
                    if (Peek().Kind == "eof") Error("Missing '}'");
                    list.Add(ParseStmt());
                }
                return list;
            }

            private Stmt ParseStmt()
            {
                var tk = Peek();
                string w = tk.Text;
                if (tk.Kind == "id")
                {
                    if (w == "return" || w == "break" || w == "continue" || w == "nop")
                    {
                        index++;
                        if (w == "return" && Peek().Text != ";")
                        {
                            var retVal = ParseExpr();
                            Eat(";");
                            return new Stmt { Kind = "return", Lhs = retVal };
                        }
                        Eat(";");
                        return new Stmt { Kind = w };
                    }
                    if (w == "pushf" && Peek(1).Text == "(")
                    {
                        index += 2;
                        var dur = ParseExpr();
                        Eat(")");
                        Eat(";");
                        return new Stmt { Kind = "pushf", Lhs = dur };
                    }
                    if (w == "if")
                    {
                        return ParseIfStmt();
                    }
                    if (w == "while")
                    {
                        index++;
                        Eat("(");
                        var cond = ParseExpr();
                        Eat(")");
                        Eat("{");
                        var body = Block();
                        return new Stmt { Kind = "while", Lhs = cond, ThenBody = body };
                    }
                }

                var expr = ParseExpr();
                var next = Peek();
                string op = next.Text;
                if (next.Kind == "op" && (op == "=" || op == "+=" || op == "-=" || op == "*=" || op == "/="))
                {
                    index++;
                    if (expr.Kind != "field" && expr.Kind != "var" && expr.Kind != "glob")
                        Error("Cannot assign to this expression");
                    var rhs = ParseExpr();
                    Eat(";");
                    if (op == "=")
                        return new Stmt { Kind = "assign", Lhs = expr, Rhs = rhs };
                    else
                        return new Stmt { Kind = "opassign", Op = op.Substring(0, 1), Lhs = expr, Rhs = rhs };
                }
                if (expr.Kind == "call")
                {
                    Eat(";");
                    return new Stmt { Kind = "callstmt", Lhs = expr };
                }
                Error("Expected a statement");
                return null;
            }

            private Stmt ParseIfStmt()
            {
                index++;
                Eat("(");
                var cond = ParseExpr();
                Eat(")");
                Eat("{");
                var thenBody = Block();
                var elseBody = new List<Stmt>();
                if (Peek().Kind == "id" && Peek().Text == "else")
                {
                    index++;
                    if (Peek().Kind == "id" && Peek().Text == "if")
                    {
                        elseBody.Add(ParseIfStmt());
                    }
                    else
                    {
                        Eat("{");
                        elseBody = Block();
                    }
                }
                return new Stmt { Kind = "if", Lhs = cond, ThenBody = thenBody, ElseBody = elseBody };
            }

            private Expr ParseExpr(int minPrec = 1)
            {
                var left = ParseUnary();
                while (true)
                {
                    var tk = Peek();
                    string op = tk.Text;
                    if (tk.Kind != "op" || !PrecMap.ContainsKey(op) || PrecMap[op] < minPrec)
                        return left;
                    index++;
                    var right = ParseExpr(PrecMap[op] + 1);
                    left = new Expr { Kind = "bin", StrVal = op, Left = left, Right = right };
                }
            }

            private Expr ParseUnary()
            {
                var tk = Peek();
                if (tk.Kind == "op" && (tk.Text == "!" || tk.Text == "-"))
                {
                    index++;
                    var next = Peek();
                    if (tk.Text == "-" && next.Kind == "num")
                    {
                        index++;
                        float f = float.Parse(next.Text, CultureInfo.InvariantCulture);
                        return ParsePostfix(new Expr { Kind = "f", IntVal = FloatToBits(-f) });
                    }
                    return new Expr { Kind = "un", StrVal = tk.Text, Left = ParseUnary() };
                }
                return ParsePostfix(ParsePrimary());
            }

            private Expr ParsePostfix(Expr e)
            {
                while (true)
                {
                    var tk = Peek();
                    if (tk.Kind == "op" && tk.Text == ".")
                    {
                        index++;
                        string fName = Eat(kind: "id").Text;
                        var m = Regex.Match(fName, @"^f(\d+)$");
                        if (!m.Success) Error("Fields must be written .f<number> (e.g. self.f5)");
                        int fieldIdx = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        e = new Expr { Kind = "field", Left = e, IntVal = fieldIdx };
                    }
                    else if (tk.Kind == "op" && tk.Text == "[")
                    {
                        index++;
                        int compIdx = int.Parse(Eat(kind: "num").Text, CultureInfo.InvariantCulture);
                        Eat("]");
                        e = new Expr { Kind = "field", Left = e, IntVal = compIdx };
                    }
                    else
                    {
                        return e;
                    }
                }
            }

            private Expr ParsePrimary()
            {
                var tk = Peek();
                string kind = tk.Kind;
                string w = tk.Text;
                if (kind == "num")
                {
                    index++;
                    float f = float.Parse(w, CultureInfo.InvariantCulture);
                    return new Expr { Kind = "f", IntVal = FloatToBits(f) };
                }
                if (kind == "str")
                {
                    index++;
                    return new Expr { Kind = "s", StrVal = UnquoteString(w) };
                }
                if (kind == "op" && w == "(")
                {
                    index++;
                    var inner = ParseExpr();
                    Eat(")");
                    return inner;
                }
                if (kind == "id")
                {
                    index++;
                    if (w == "bits" && Peek().Text == "(")
                    {
                        Eat("(");
                        string hex = Eat(kind: "hex").Text;
                        Eat(")");
                        uint uv = Convert.ToUInt32(hex, 16);
                        return new Expr { Kind = "f", IntVal = unchecked((int)uv) };
                    }

                    bool isLatent = false;
                    Expr latentDur = null;
                    if (w == "latent")
                    {
                        isLatent = true;
                        if (Peek().Text == "(" && Peek().Kind == "op")
                        {
                            Eat("(");
                            latentDur = ParseExpr();
                            Eat(")");
                            if (latentDur.Kind != "f")
                                Error("latent(...) duration must be a numeric constant");
                        }
                        w = Eat(kind: "id").Text;
                    }

                    if (Peek().Text == "(" && Peek().Kind == "op")
                    {
                        Eat("(");
                        var args = new List<Expr>();
                        if (!Accept(")"))
                        {
                            while (true)
                            {
                                args.Add(ParseExpr());
                                if (Accept(")")) break;
                                Eat(",");
                            }
                        }

                        int targetType = routineNames.Contains(w) ? 1 : 0;
                        return new Expr
                        {
                            Kind = "call",
                            StrVal = w,
                            IntVal = targetType,
                            Args = args,
                            IsLatent = isLatent,
                            LatentDur = latentDur
                        };
                    }

                    if (isLatent) Error("latent keyword must be followed by a function call");

                    var mVar = Regex.Match(w, @"^v(\d+)$");
                    if (mVar.Success)
                    {
                        return new Expr { Kind = "var", IntVal = int.Parse(mVar.Groups[1].Value, CultureInfo.InvariantCulture) };
                    }
                    var mArg = Regex.Match(w, @"^arg(\d+)$");
                    if (mArg.Success)
                    {
                        return new Expr { Kind = "param", IntVal = int.Parse(mArg.Groups[1].Value, CultureInfo.InvariantCulture) };
                    }

                    if (!globalNames.Contains(w))
                        Error(string.Format("Unknown identifier '{0}' (must be declared in 'globals')", w));

                    return new Expr { Kind = "glob", StrVal = w };
                }

                Error("Expected an expression");
                return null;
            }
        }

        #endregion

        #region Code Generator

        private sealed class ScrCodeGen
        {
            private readonly ScrSrcParser parser;
            private readonly Dictionary<string, int> defIdx;
            private readonly Dictionary<string, int> funcIdx;

            private readonly List<string> pool = new List<string>();
            private int poolSize = 0;

            private sealed class AsmItem
            {
                public string Kind; // "i", "jz", "jmp", "call", "L"
                public ushort Op;
                public int A, B, C;
                public string TargetLabel;
            }

            private readonly List<AsmItem> code = new List<AsmItem>();
            private int labelCounter = 0;
            private int targetReg = -1;
            private HashSet<int> refRegs = new HashSet<int>();
            private readonly Stack<Tuple<string, string>> loops = new Stack<Tuple<string, string>>();

            public ScrCodeGen(ScrSrcParser parser)
            {
                this.parser = parser;
                defIdx = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < parser.Defs.Count; i++) defIdx[parser.Defs[i]] = i;
                funcIdx = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < parser.Funcs.Count; i++) funcIdx[parser.Funcs[i]] = i;
            }

            private string NextLabel()
            {
                return "_L" + (++labelCounter);
            }

            private void Emit(ushort op, int a = 0, int b = 0, int c = 0)
            {
                code.Add(new AsmItem { Kind = "i", Op = op, A = a, B = b, C = c });
            }

            private int StringOffset(string text)
            {
                int off = poolSize;
                pool.Add(text);
                poolSize += Latin1.GetByteCount(text) + 1;
                return off;
            }

            private int Glob(string name)
            {
                if (!defIdx.TryGetValue(name, out int idx))
                    throw new InvalidOperationException("Unknown global variable: " + name);
                return -(idx + 1);
            }

            public void CompileRoutine(string name, List<Stmt> body)
            {
                code.Add(new AsmItem { Kind = "L", TargetLabel = "R" + name });
                Block(body);
                Emit(0x00); // END
            }

            private void Block(List<Stmt> stmts)
            {
                foreach (var s in stmts) CompileStmt(s);
            }

            private void CompileStmt(Stmt s)
            {
                string k = s.Kind;
                if (k == "assign")
                {
                    var lv = s.Lhs;
                    if (lv.Kind == "field") Store(lv, s.Rhs, 0x11);
                    else if (lv.Kind == "var") AssignSlot(lv.IntVal, s.Rhs);
                    else if (lv.Kind == "glob") AssignSlot(Glob(lv.StrVal), s.Rhs);
                    else throw new InvalidOperationException("Bad assignment target");
                }
                else if (k == "opassign")
                {
                    var tgt = s.Lhs;
                    ushort opVal = (ushort)CompoundMap[s.Op];
                    if (tgt.Kind == "field")
                    {
                        Store(tgt, s.Rhs, opVal);
                    }
                    else
                    {
                        int slot = (tgt.Kind == "var") ? tgt.IntVal : Glob(tgt.StrVal);
                        var b = Operand(s.Rhs, 0, "b");
                        Emit((ushort)(OpcodeMap[s.Op] | b.Item2), slot, b.Item1, slot);
                    }
                }
                else if (k == "callstmt")
                {
                    Call(s.Lhs, 0, true);
                }
                else if (k == "pushf")
                {
                    var a = Operand(s.Lhs, 0, "a");
                    Emit((ushort)(0x1E | a.Item2), a.Item1, 0, 0);
                    refRegs.Clear();
                }
                else if (k == "return")
                {
                    if (s.Lhs != null)
                    {
                        var a = Operand(s.Lhs, 0, "a");
                        Emit((ushort)(0x18 | a.Item2), a.Item1, 0, 0);
                    }
                    else
                    {
                        Emit(0x18);
                    }
                }
                else if (k == "nop")
                {
                    Emit(0x1B, 1, 0, 0);
                }
                else if (k == "break")
                {
                    code.Add(new AsmItem { Kind = "jmp", TargetLabel = loops.Peek().Item2 });
                }
                else if (k == "continue")
                {
                    code.Add(new AsmItem { Kind = "jmp", TargetLabel = loops.Peek().Item1 });
                }
                else if (k == "if")
                {
                    string lelse = NextLabel();
                    string lend = NextLabel();
                    bool hasElse = s.ElseBody != null && s.ElseBody.Count > 0;
                    CondJz(s.Lhs, hasElse ? lelse : lend);
                    Block(s.ThenBody);
                    if (hasElse)
                    {
                        code.Add(new AsmItem { Kind = "jmp", TargetLabel = lend });
                        code.Add(new AsmItem { Kind = "L", TargetLabel = lelse });
                        Block(s.ElseBody);
                    }
                    code.Add(new AsmItem { Kind = "L", TargetLabel = lend });
                }
                else if (k == "while")
                {
                    string head = NextLabel();
                    string exit = NextLabel();
                    code.Add(new AsmItem { Kind = "L", TargetLabel = head });
                    CondJz(s.Lhs, exit);
                    loops.Push(Tuple.Create(head, exit));
                    Block(s.ThenBody);
                    loops.Pop();
                    code.Add(new AsmItem { Kind = "jmp", TargetLabel = head });
                    code.Add(new AsmItem { Kind = "L", TargetLabel = exit });
                }
                else
                {
                    throw new InvalidOperationException("Unknown statement: " + k);
                }
            }

            private void CondJz(Expr cond, string targetLabel)
            {
                string k = cond.Kind;
                if (k == "var")
                {
                    code.Add(new AsmItem { Kind = "jz", Op = 0x1A, A = cond.IntVal, TargetLabel = targetLabel });
                }
                else if (k == "glob")
                {
                    code.Add(new AsmItem { Kind = "jz", Op = 0x1A, A = Glob(cond.StrVal), TargetLabel = targetLabel });
                }
                else if (k == "field")
                {
                    LoadField(cond, 0);
                    code.Add(new AsmItem { Kind = "jz", Op = 0x1A | 0x0200, A = 0, TargetLabel = targetLabel });
                }
                else
                {
                    ValueToReg(cond, 0);
                    code.Add(new AsmItem { Kind = "jz", Op = 0x1A, A = 0, TargetLabel = targetLabel });
                }
            }

            private void Store(Expr lv, Expr rhs, ushort opcode)
            {
                LoadField(lv, 0);
                refRegs = new HashSet<int> { 0 };
                targetReg = 0;
                string k = rhs.Kind;
                int b = 0, fb = 0;
                if (k == "f")
                {
                    b = rhs.IntVal; fb = 0x1000;
                }
                else if (k == "var")
                {
                    b = rhs.IntVal; fb = 0;
                }
                else if (k == "glob")
                {
                    b = Glob(rhs.StrVal); fb = 0;
                }
                else if (k == "field")
                {
                    LoadField(rhs, 1);
                    b = 1; fb = 0x2000;
                }
                else if (k == "s")
                {
                    b = StringOffset(rhs.StrVal); fb = 0x1000;
                }
                else
                {
                    ValueToReg(rhs, 1);
                    b = 1; fb = 0;
                }
                targetReg = -1;
                refRegs.Clear();
                Emit((ushort)(opcode | 0x8200 | fb), 0, b, 0);
            }

            private void AssignSlot(int dest, Expr rhs)
            {
                string k = rhs.Kind;
                if (k == "f")
                {
                    Emit(0x1011, dest, rhs.IntVal, 0);
                }
                else if (k == "s")
                {
                    Emit(0x1011, dest, StringOffset(rhs.StrVal), 0);
                }
                else if (k == "var" || k == "param")
                {
                    Emit(0x11, dest, rhs.IntVal, 0);
                }
                else if (k == "glob")
                {
                    Emit(0x11, dest, Glob(rhs.StrVal), 0);
                }
                else if (k == "field")
                {
                    LoadField(rhs, 1);
                    Emit(0x2011, dest, 1, 0);
                }
                else
                {
                    ValueToReg(rhs, 1);
                    Emit(0x11, dest, 1, 0);
                }
            }

            private void LoadField(Expr e, int r)
            {
                var baseExpr = e.Left;
                int idx = e.IntVal;
                if (baseExpr.Kind == "glob")
                {
                    Emit(0x12, Glob(baseExpr.StrVal), idx, r);
                }
                else if (baseExpr.Kind == "var")
                {
                    Emit(0x12, baseExpr.IntVal, idx, r);
                }
                else if (baseExpr.Kind == "field")
                {
                    LoadField(baseExpr, r);
                    Emit(0x12, r, idx, r);
                }
                else
                {
                    throw new InvalidOperationException("Unsupported field base: " + baseExpr.Kind);
                }
            }

            private Tuple<int, int> Operand(Expr e, int d, string which)
            {
                string k = e.Kind;
                int flFloat = (which == "a") ? 0x0100 : 0x1000;
                int flDeref = (which == "a") ? 0x0200 : 0x2000;
                int reg = (which == "a") ? d : d + 1;
                if (k == "f") return Tuple.Create(e.IntVal, flFloat);
                if (k == "param") return Tuple.Create(e.IntVal, 0);
                if (k == "var") return Tuple.Create(e.IntVal, 0);
                if (k == "glob") return Tuple.Create(Glob(e.StrVal), 0);
                if (k == "field")
                {
                    LoadField(e, reg);
                    refRegs.Add(reg);
                    return Tuple.Create(reg, flDeref);
                }
                ValueToReg(e, reg);
                return Tuple.Create(reg, 0);
            }

            private void ValueToReg(Expr e, int r)
            {
                string k = e.Kind;
                if (k == "f")
                {
                    Emit(0x1011, r, e.IntVal, 0);
                }
                else if (k == "s")
                {
                    Emit(0x1011, r, StringOffset(e.StrVal), 0);
                }
                else if (k == "var" || k == "param")
                {
                    Emit(0x11, r, e.IntVal, 0);
                }
                else if (k == "glob")
                {
                    Emit(0x11, r, Glob(e.StrVal), 0);
                }
                else if (k == "field")
                {
                    LoadField(e, r);
                    refRegs.Add(r);
                }
                else if (k == "bin")
                {
                    var a = Operand(e.Left, r, "a");
                    var b = Operand(e.Right, r, "b");
                    Emit((ushort)(OpcodeMap[e.StrVal] | a.Item2 | b.Item2), a.Item1, b.Item1, r);
                    refRegs.Remove(a.Item1);
                    refRegs.Remove(r);
                }
                else if (k == "un")
                {
                    var a = Operand(e.Left, r, "a");
                    Emit((ushort)((e.StrVal == "!" ? 0x0F : 0x10) | a.Item2), a.Item1, 0, r);
                    refRegs.Remove(a.Item1);
                    refRegs.Remove(r);
                }
                else if (k == "call")
                {
                    Call(e, r, false);
                }
                else
                {
                    throw new InvalidOperationException("Cannot compile expression: " + k);
                }
            }

            private void Call(Expr e, int d, bool asStmt)
            {
                var saved = new List<int>();
                for (int r = 0; r < d; r++) saved.Add(r);
                foreach (int r in saved)
                {
                    int flag = (r == targetReg || (r == 0 && refRegs.Contains(r))) ? 0x8000 : 0;
                    Emit((ushort)(0x13 | flag), r, 0, 0); // ENTER
                }

                if (e.LatentDur != null)
                {
                    Emit(0x011E, e.LatentDur.IntVal, 0, 0); // PUSHF duration
                    if (e.Args != null)
                    {
                        for (int k = 0; k < e.Args.Count; k++)
                            Arg(e.Args[k], k, d);
                    }
                }
                else if (e.IsLatent && e.Args != null && e.Args.Count == 1 && (e.IntVal == 1 || e.StrVal == "sleep"))
                {
                    var a = Operand(e.Args[0], d, "a");
                    Emit((ushort)(0x1E | a.Item2), a.Item1, 0, 0); // PUSHF
                }
                else
                {
                    if (e.Args != null)
                    {
                        for (int k = 0; k < e.Args.Count; k++)
                            Arg(e.Args[k], k, d);
                    }
                }

                if (e.IntVal == 0) // Engine func
                {
                    if (!funcIdx.TryGetValue(e.StrVal, out int fidx))
                        throw new InvalidOperationException("Unknown engine function: " + e.StrVal);
                    int a = -(fidx + 1);
                    code.Add(new AsmItem { Kind = "i", Op = (ushort)(e.IsLatent ? 0x1D : 0x1C), A = a, B = d, C = 0 });
                }
                else // Routine
                {
                    code.Add(new AsmItem { Kind = "call", Op = (ushort)(e.IsLatent ? 0x1D : 0x1C), TargetLabel = "R" + e.StrVal, B = d });
                }

                for (int i = saved.Count - 1; i >= 0; i--)
                {
                    int r = saved[i];
                    int flag = refRegs.Contains(r) ? 0x8000 : 0;
                    Emit((ushort)(0x14 | flag), r, 0, r); // LEAVE
                }
            }

            private void Arg(Expr arg, int k, int d)
            {
                int t = d + k;
                string kind = arg.Kind;
                if (kind == "f")
                {
                    Emit(0x1011, k, arg.IntVal, 0);
                }
                else if (kind == "s")
                {
                    Emit(0x1011, k, StringOffset(arg.StrVal), 0);
                }
                else if (kind == "var" || kind == "param")
                {
                    Emit(0x11, k, arg.IntVal, 0);
                }
                else if (kind == "glob")
                {
                    Emit(0x11, k, Glob(arg.StrVal), 0);
                }
                else if (kind == "field")
                {
                    if (arg.Left.Kind != "field" && VectorFields.Contains(arg.IntVal))
                    {
                        LoadField(arg, t);
                        if (t != k) Emit(0x11, k, t, 0);
                    }
                    else
                    {
                        LoadField(arg, t);
                        Emit(0x2011, k, t, 0);
                    }
                }
                else
                {
                    ValueToReg(arg, t);
                    if (t != k) Emit(0x11, k, t, 0);
                }
            }

            public ScrFile BuildScrFile()
            {
                var pos = new Dictionary<string, int>();
                int idx = 0;
                foreach (var item in code)
                {
                    if (item.Kind == "L") pos[item.TargetLabel] = idx;
                    else idx++;
                }

                var scr = new ScrFile
                {
                    Slots = parser.Slots,
                    Cash = new List<Tuple<byte, string>>(parser.Cash),
                    Defs = new List<string>(parser.Defs),
                    Funcs = new List<string>(parser.Funcs),
                    Data = new List<Tuple<ushort, ushort, int>>(parser.Data),
                    Strings = new List<string>(pool),
                };

                idx = 0;
                foreach (var item in code)
                {
                    if (item.Kind == "L") continue;
                    if (item.Kind == "i")
                    {
                        scr.Code.Add(Tuple.Create(item.Op, item.A, item.B, item.C));
                    }
                    else if (item.Kind == "jz")
                    {
                        if (!pos.TryGetValue(item.TargetLabel, out int targetIdx))
                            throw new InvalidOperationException("Jump to unresolved label: " + item.TargetLabel);
                        scr.Code.Add(Tuple.Create(item.Op, item.A, targetIdx - idx, 0));
                    }
                    else if (item.Kind == "jmp")
                    {
                        if (!pos.TryGetValue(item.TargetLabel, out int targetIdx))
                            throw new InvalidOperationException("Jump to unresolved label: " + item.TargetLabel);
                        scr.Code.Add(Tuple.Create((ushort)0x1B, targetIdx - idx, 0, 0));
                    }
                    else if (item.Kind == "call")
                    {
                        if (!pos.TryGetValue(item.TargetLabel, out int targetIdx))
                            throw new InvalidOperationException("Call to unresolved routine: " + item.TargetLabel);
                        scr.Code.Add(Tuple.Create(item.Op, targetIdx, item.B, 0));
                    }
                    idx++;
                }

                for (int i = 0; i < 5; i++)
                {
                    if (parser.Entries.TryGetValue(i, out string rName))
                    {
                        string lab = "R" + rName;
                        if (!pos.TryGetValue(lab, out int targetPos))
                            throw new InvalidOperationException("Entry role " + RoleNames[i] + " refers to unknown routine: " + rName);
                        scr.Entries[i] = targetPos;
                    }
                    else
                    {
                        scr.Entries[i] = -1;
                    }
                }

                return scr;
            }
        }

        #endregion
    }
}
