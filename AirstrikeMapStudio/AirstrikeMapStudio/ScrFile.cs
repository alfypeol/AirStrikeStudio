using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// Native C# parser, lossless disassembler/assembler, and C-like structured
    /// decompiler for AirStrike 3D / AirStrike II / Gulf Thunder compiled .scr
    /// ("RCSL") script files. Ported directly from ScriptStudio (scrasm.py,
    /// scrdec.py, scrsrc.py, scrsig.py).
    /// </summary>
    public sealed partial class ScrFile
    {
        public int Slots = 16;
        public int[] Entries = { -1, -1, -1, -1, -1 }; // init, main, damaged, touched, callback
        public List< Tuple<byte, string> > Cash = new List< Tuple<byte, string> >();
        public List<string> Defs = new List<string>();
        public List<string> Funcs = new List<string>();
        public List< Tuple<ushort, ushort, int> > Data = new List< Tuple<ushort, ushort, int> >();
        public List<string> Strings = new List<string>();
        public List< Tuple<ushort, int, int, int> > Code = new List< Tuple<ushort, int, int, int> >();

        public static readonly string[] RoleNames = { "init", "main", "damaged", "touched", "callback" };

        private static readonly Dictionary<int, string> OpNames = new Dictionary<int, string>
        {
            { 0x00, "END" }, { 0x01, "MUL" }, { 0x02, "DIV" }, { 0x03, "ADD" }, { 0x04, "SUB" },
            { 0x05, "BAND" }, { 0x07, "OR" }, { 0x08, "AND" }, { 0x09, "EQ" }, { 0x0A, "NE" },
            { 0x0B, "GT" }, { 0x0C, "LT" }, { 0x0D, "GE" }, { 0x0E, "LE" }, { 0x0F, "NOT" },
            { 0x10, "NEG" }, { 0x11, "MOV" }, { 0x12, "LOAD" }, { 0x13, "ENTER" }, { 0x14, "LEAVE" },
            { 0x18, "RETURN" }, { 0x1A, "JZ" }, { 0x1B, "JMP" }, { 0x1C, "CALL" }, { 0x1D, "CALLP" },
            { 0x1E, "PUSHF" },
        };

        private static readonly Dictionary<string, int> NameToOp =
            OpNames.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

        private static readonly HashSet<int> BinaryOps = new HashSet<int>
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E,
        };

        private static readonly HashSet<string> ReturnFuncs = new HashSet<string>(StringComparer.Ordinal)
        {
            "atan2", "copysign", "atan", "floor2", "fmod", "IsMultiplayer", "GetPlayersDistance",
            "getentity", "G_GetMissiles", "random", "max", "G_GetUpgrade", "sin", "GetWaypointDelay",
            "G_GetPowerUp", "abs", "crandom", "G_UseMissile", "ClearFlag", "TerrainHeight",
            "vec_length", "G_UsePowerUp", "cos", "IsValidTarget", "SetFlag", "LockTarget", "min", "create",
        };

        private static readonly Regex IdentRegex = new Regex(@"^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.Compiled);
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public bool CallsWaypointFunctions =>
            Funcs.Any(f => f.Equals("MoveToNextWP", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("lMoveToNextWP", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("lRotateToNextWP", StringComparison.OrdinalIgnoreCase)
                        || f.Equals("GetWaypointDelay", StringComparison.OrdinalIgnoreCase));

        public string ActiveEntryRoles()
        {
            var active = new List<string>();
            for (int i = 0; i < 5 && i < Entries.Length; i++)
                if (Entries[i] >= 0)
                    active.Add(RoleNames[i]);
            return active.Count > 0 ? string.Join(", ", active) : "none";
        }

        // ═════════════════════════════════════════════════════════════════
        // Binary RCSL Parser & Builder
        // ═════════════════════════════════════════════════════════════════

        public static ScrFile Parse(byte[] data)
        {
            if (data == null || data.Length < 56
                || data[0] != 'R' || data[1] != 'C' || data[2] != 'S' || data[3] != 'L')
                throw new InvalidDataException("Not an RCSL compiled script.");

            uint hsz = BitConverter.ToUInt32(data, 4);
            if (hsz != 16)
                throw new InvalidDataException("Unexpected RCSL header size: " + hsz);

            var scr = new ScrFile();
            scr.Slots = BitConverter.ToInt32(data, 24);
            for (int i = 0; i < 5; i++)
                scr.Entries[i] = BitConverter.ToInt32(data, 36 + i * 4);

            int pos = 56;
            var secs = new Dictionary<string, byte[]>();
            while (pos + 8 <= data.Length)
            {
                string tag = Latin1.GetString(data, pos, 4);
                int sz = (int)BitConverter.ToUInt32(data, pos + 4);
                if (sz < 0 || pos + 8 + sz > data.Length)
                    throw new InvalidDataException("Section " + tag + " runs past end of file.");
                byte[] chunk = new byte[sz];
                Buffer.BlockCopy(data, pos + 8, chunk, 0, sz);
                secs[tag] = chunk;
                pos += 8 + sz;
            }
            if (pos != data.Length)
                throw new InvalidDataException((data.Length - pos) + " unread trailing bytes in .scr file.");

            if (secs.TryGetValue("CASH", out byte[] cashBytes))
            {
                int i = 0;
                while (i < cashBytes.Length)
                {
                    if (i + 2 > cashBytes.Length) throw new InvalidDataException("Truncated CASH section.");
                    byte kind = cashBytes[i];
                    int n = cashBytes[i + 1];
                    if (i + 2 + n > cashBytes.Length || n == 0 || cashBytes[i + 2 + n - 1] != 0)
                        throw new InvalidDataException("CASH entry without NUL terminator.");
                    string name = Latin1.GetString(cashBytes, i + 2, n - 1);
                    int nul = name.IndexOf('\0');
                    if (nul >= 0) name = name.Substring(0, nul);
                    scr.Cash.Add(Tuple.Create(kind, name));
                    i += 2 + n;
                }
            }

            foreach (var pair in new[] { Tuple.Create("DEFS", scr.Defs), Tuple.Create("FUNC", scr.Funcs) })
            {
                if (!secs.TryGetValue(pair.Item1, out byte[] b)) continue;
                int i = 0;
                while (i < b.Length)
                {
                    int n = b[i];
                    if (n <= 0 || i + 1 + n > b.Length || b[i + n] != 0)
                        throw new InvalidDataException(pair.Item1 + " entry without clean NUL terminator.");
                    pair.Item2.Add(Latin1.GetString(b, i + 1, n - 1));
                    i += 1 + n;
                }
            }

            if (!secs.TryGetValue("DATA", out byte[] dBytes))
                throw new InvalidDataException("DATA section missing.");
            if (dBytes.Length % 8 != 0)
                throw new InvalidDataException("DATA section size is not a multiple of 8.");
            for (int k = 0; k < dBytes.Length; k += 8)
            {
                ushort ty = BitConverter.ToUInt16(dBytes, k);
                ushort sl = BitConverter.ToUInt16(dBytes, k + 2);
                int val = BitConverter.ToInt32(dBytes, k + 4);
                scr.Data.Add(Tuple.Create(ty, sl, val));
            }

            if (secs.TryGetValue("STRG", out byte[] sBytes) && sBytes.Length > 0)
            {
                if (sBytes[sBytes.Length - 1] != 0)
                    throw new InvalidDataException("STRG section does not end with NUL.");
                int start = 0;
                for (int i = 0; i < sBytes.Length; i++)
                {
                    if (sBytes[i] == 0)
                    {
                        scr.Strings.Add(Latin1.GetString(sBytes, start, i - start));
                        start = i + 1;
                    }
                }
            }

            if (secs.TryGetValue("CODE", out byte[] cBytes))
            {
                if (cBytes.Length == 0 || cBytes.Length % 14 != 0)
                    throw new InvalidDataException("CODE section size is not a positive multiple of 14.");
                for (int k = 0; k < cBytes.Length; k += 14)
                {
                    ushort op = BitConverter.ToUInt16(cBytes, k);
                    int a = BitConverter.ToInt32(cBytes, k + 2);
                    int b = BitConverter.ToInt32(cBytes, k + 6);
                    int c = BitConverter.ToInt32(cBytes, k + 10);
                    scr.Code.Add(Tuple.Create(op, a, b, c));
                }
            }

            return scr;
        }

        public byte[] Build()
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Latin1))
            {
                byte[] cashPayload;
                using (var cms = new MemoryStream())
                {
                    foreach (var item in Cash)
                    {
                        byte[] nb = Latin1.GetBytes(item.Item2);
                        if (nb.Length > 253) throw new InvalidDataException("CASH resource name too long.");
                        cms.WriteByte(item.Item1);
                        cms.WriteByte((byte)(nb.Length + 1));
                        cms.Write(nb, 0, nb.Length);
                        cms.WriteByte(0);
                    }
                    cashPayload = cms.ToArray();
                }

                byte[] LenStr(List<string> list)
                {
                    using (var lms = new MemoryStream())
                    {
                        foreach (string s in list)
                        {
                            byte[] nb = Latin1.GetBytes(s);
                            if (nb.Length > 253) throw new InvalidDataException("Identifier too long: " + s);
                            lms.WriteByte((byte)(nb.Length + 1));
                            lms.Write(nb, 0, nb.Length);
                            lms.WriteByte(0);
                        }
                        return lms.ToArray();
                    }
                }

                byte[] defsPayload = LenStr(Defs);
                byte[] funcsPayload = LenStr(Funcs);

                byte[] dataPayload = new byte[Data.Count * 8];
                for (int i = 0; i < Data.Count; i++)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(Data[i].Item1), 0, dataPayload, i * 8, 2);
                    Buffer.BlockCopy(BitConverter.GetBytes(Data[i].Item2), 0, dataPayload, i * 8 + 2, 2);
                    Buffer.BlockCopy(BitConverter.GetBytes(Data[i].Item3), 0, dataPayload, i * 8 + 4, 4);
                }

                byte[] strgPayload;
                using (var sms = new MemoryStream())
                {
                    foreach (string s in Strings)
                    {
                        byte[] sb = Latin1.GetBytes(s);
                        sms.Write(sb, 0, sb.Length);
                        sms.WriteByte(0);
                    }
                    strgPayload = sms.ToArray();
                }

                byte[] codePayload = new byte[Code.Count * 14];
                for (int i = 0; i < Code.Count; i++)
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(Code[i].Item1), 0, codePayload, i * 14, 2);
                    Buffer.BlockCopy(BitConverter.GetBytes(Code[i].Item2), 0, codePayload, i * 14 + 2, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(Code[i].Item3), 0, codePayload, i * 14 + 6, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(Code[i].Item4), 0, codePayload, i * 14 + 10, 4);
                }

                bw.Write(Latin1.GetBytes("RCSL"));
                bw.Write((uint)16);
                bw.Write((uint)Cash.Count);
                bw.Write((uint)Defs.Count);
                bw.Write((uint)Funcs.Count);
                bw.Write((uint)Data.Count);
                bw.Write(Slots);
                bw.Write(strgPayload.Length);
                bw.Write(Code.Count);
                for (int i = 0; i < 5; i++)
                    bw.Write(Entries[i]);

                var sections = new[]
                {
                    Tuple.Create("CASH", cashPayload),
                    Tuple.Create("DEFS", defsPayload),
                    Tuple.Create("FUNC", funcsPayload),
                    Tuple.Create("DATA", dataPayload),
                    Tuple.Create("STRG", strgPayload),
                    Tuple.Create("CODE", codePayload),
                };

                foreach (var sec in sections)
                {
                    if (sec.Item2.Length > 0 || sec.Item1 == "DATA")
                    {
                        bw.Write(Latin1.GetBytes(sec.Item1));
                        bw.Write((uint)sec.Item2.Length);
                        bw.Write(sec.Item2);
                    }
                }

                return ms.ToArray();
            }
        }

        // ═════════════════════════════════════════════════════════════════
        // Disassembler & Assembler (scrasm.py compatible)
        // ═════════════════════════════════════════════════════════════════

        private static float BitsToFloat(int bits) =>
            BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

        private static int FloatToBits(float f) =>
            BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

        public static string FormatFloatAsm(int bits)
        {
            float f = BitsToFloat(bits);
            if (float.IsNaN(f) || float.IsInfinity(f))
                return string.Format(CultureInfo.InvariantCulture, "bits:0x{0:x8}", (uint)bits);
            string s = f.ToString("G9", CultureInfo.InvariantCulture);
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                && FloatToBits(parsed) == bits)
                return s + "f";
            return string.Format(CultureInfo.InvariantCulture, "bits:0x{0:x8}", (uint)bits);
        }

        public static string FormatFloatSrc(int bits)
        {
            float f = BitsToFloat(bits);
            if (float.IsNaN(f) || float.IsInfinity(f))
                return string.Format(CultureInfo.InvariantCulture, "bits(0x{0:x8})", (uint)bits);
            if (f == (int)f && Math.Abs(f) < 1e9f)
                return ((int)f).ToString(CultureInfo.InvariantCulture);
            string s = f.ToString("G7", CultureInfo.InvariantCulture);
            return s;
        }

        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (byte ch in Latin1.GetBytes(s))
            {
                if (ch == 0x22) sb.Append("\\\"");
                else if (ch == 0x5C) sb.Append("\\\\");
                else if (ch >= 32 && ch < 127) sb.Append((char)ch);
                else sb.AppendFormat(CultureInfo.InvariantCulture, "\\x{0:x2}", ch);
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string Unquote(string tok)
        {
            if (tok.Length < 2 || tok[0] != '"' || tok[tok.Length - 1] != '"')
                throw new InvalidDataException("Expected quoted string, got: " + tok);
            string body = tok.Substring(1, tok.Length - 2);
            var bytes = new List<byte>();
            int i = 0;
            while (i < body.Length)
            {
                char ch = body[i];
                if (ch == '\\' && i + 1 < body.Length)
                {
                    char nxt = body[i + 1];
                    if (nxt == 'x' && i + 3 < body.Length)
                    {
                        bytes.Add(Convert.ToByte(body.Substring(i + 2, 2), 16));
                        i += 4;
                        continue;
                    }
                    if (nxt == '"' || nxt == '\\')
                    {
                        bytes.Add((byte)nxt);
                        i += 2;
                        continue;
                    }
                }
                bytes.Add((byte)ch);
                i++;
            }
            return Latin1.GetString(bytes.ToArray());
        }

        private static bool IdentOk(string s) =>
            !string.IsNullOrEmpty(s) && IdentRegex.IsMatch(s) && s.All(c => c < 128);

        private static string[] GetOperandKinds(int low)
        {
            if (BinaryOps.Contains(low)) return new[] { "val", "val", "slot" };
            switch (low)
            {
                case 0x0F:
                case 0x10: return new[] { "val", "raw", "slot" };
                case 0x11: return new[] { "slot", "val", "raw" };
                case 0x12: return new[] { "slot", "raw", "slot" };
                case 0x1A: return new[] { "slot", "rel", "raw" };
                case 0x1B: return new[] { "rel", "raw", "raw" };
                case 0x1C:
                case 0x1D: return new[] { "call", "raw", "raw" };
                case 0x1E: return new[] { "val", "raw", "raw" };
                default:   return new[] { "raw", "raw", "raw" };
            }
        }

        public string Disassemble()
        {
            int n = Code.Count;
            var off2idx = new Dictionary<int, int>();
            int off = 0;
            for (int k = 0; k < Strings.Count; k++)
            {
                if (!off2idx.ContainsKey(off)) off2idx[off] = k;
                off += Latin1.GetByteCount(Strings[k]) + 1;
            }

            int firstZero = -1;
            for (int i = 0; i < n; i++)
            {
                if (Code[i].Item1 == 0x1011 && Code[i].Item3 == 0)
                {
                    firstZero = i;
                    break;
                }
            }

            bool IsString(int i)
            {
                var ins = Code[i];
                if (ins.Item1 != 0x1011 || !off2idx.ContainsKey(ins.Item3)) return false;
                return ins.Item3 != 0 || i == firstZero;
            }

            var targets = new HashSet<int>();
            foreach (int e in Entries)
                if (e >= 0 && e <= n) targets.Add(e);
            for (int i = 0; i < n; i++)
            {
                var ins = Code[i];
                int low = ins.Item1 & 0xFF;
                if (low == 0x1A && i + ins.Item3 >= 0 && i + ins.Item3 <= n) targets.Add(i + ins.Item3);
                if (low == 0x1B && i + ins.Item2 >= 0 && i + ins.Item2 <= n) targets.Add(i + ins.Item2);
                if ((low == 0x1C || low == 0x1D) && ins.Item2 >= 0 && ins.Item2 <= n) targets.Add(ins.Item2);
            }

            string Slot(int v)
            {
                if (v < 0 && -v <= Defs.Count && IdentOk(Defs[-v - 1]))
                    return "$" + Defs[-v - 1];
                if (v >= 0 && v < 16) return "r" + v;
                if (v >= 16) return "v" + v;
                return "#" + v;
            }

            var sb = new StringBuilder();
            sb.AppendLine("; Air Strike 3D script assembly (scrasm v1)");
            sb.AppendLine(".slots " + Slots);
            for (int k = 0; k < 5; k++)
            {
                int e = Entries[k];
                string et = e == -1 ? "none" : (e >= 0 && e <= n ? "L" + e : "#" + e);
                sb.AppendLine(".entry E" + k + " " + et + " ; " + RoleNames[k]);
            }
            foreach (var c in Cash)
                sb.AppendLine(".cash " + Quote(c.Item2) + (c.Item1 != 0 ? " " + c.Item1 : ""));
            foreach (string d in Defs)
                sb.AppendLine(".def " + (IdentOk(d) ? d : Quote(d)));
            foreach (string f in Funcs)
                sb.AppendLine(".func " + (IdentOk(f) ? f : Quote(f)));
            foreach (var d in Data)
            {
                if (d.Item1 == 2)
                    sb.AppendLine(".var float " + d.Item2 + " " + FormatFloatAsm(d.Item3));
                else if (d.Item1 == 3 && d.Item3 == d.Item2 + 1)
                    sb.AppendLine(".var vec " + d.Item2);
                else
                    sb.AppendFormat(CultureInfo.InvariantCulture, ".data {0} {1} 0x{2:x8}\r\n", d.Item1, d.Item2, (uint)d.Item3);
            }
            foreach (string s in Strings)
                sb.AppendLine(".string " + Quote(s));
            sb.AppendLine(".code");

            for (int i = 0; i < n; i++)
            {
                if (targets.Contains(i))
                    sb.AppendLine("L" + i + ":");

                var ins = Code[i];
                int low = ins.Item1 & 0xFF;
                int hi = ins.Item1 & 0xFF00;
                var kinds = GetOperandKinds(low);
                int[] vals = { ins.Item2, ins.Item3, ins.Item4 };
                var toks = new string[3];
                string note = "";

                for (int pos = 0; pos < 3; pos++)
                {
                    string kind = kinds[pos];
                    int v = vals[pos];
                    bool isFloat = (pos == 0 && (hi & 0x0100) != 0) || (pos == 1 && (hi & 0x1000) != 0);

                    if (kind == "val" && IsString(i) && pos == 1)
                    {
                        int k = off2idx[v];
                        toks[pos] = "s" + k;
                        note = " ; " + Quote(Strings[k]);
                    }
                    else if (kind == "val" && isFloat)
                    {
                        toks[pos] = FormatFloatAsm(v);
                    }
                    else if (kind == "val" || kind == "slot")
                    {
                        toks[pos] = Slot(v);
                    }
                    else if (kind == "rel")
                    {
                        int t = i + v;
                        toks[pos] = (t >= 0 && t <= n) ? "L" + t : "#" + v;
                    }
                    else if (kind == "call")
                    {
                        if (v < 0 && -v <= Funcs.Count && IdentOk(Funcs[-v - 1]))
                            toks[pos] = "@" + Funcs[-v - 1];
                        else if (v >= 0 && v <= n)
                            toks[pos] = "L" + v;
                        else
                            toks[pos] = "#" + v;
                    }
                    else
                    {
                        toks[pos] = v.ToString(CultureInfo.InvariantCulture);
                    }
                }

                string name = OpNames.TryGetValue(low, out string opName)
                    ? opName
                    : string.Format(CultureInfo.InvariantCulture, "OP_{0:x2}", low);
                if (hi != 0)
                    name += string.Format(CultureInfo.InvariantCulture, ".{0:x4}", hi);

                sb.AppendFormat(CultureInfo.InvariantCulture, "    {0,-11} {1}{2}\r\n", name, string.Join(", ", toks), note);
            }
            if (targets.Contains(n))
                sb.AppendLine("L" + n + ":");

            return sb.ToString();
        }

        private static string StripAsmComment(string line)
        {
            bool inq = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (ch == '\\' && inq) { i++; continue; }
                if (ch == '"') inq = !inq;
                else if (ch == ';' && !inq) return line.Substring(0, i);
            }
            return line;
        }

        private static List<string> SplitAsmArgs(string s)
        {
            var toks = new List<string>();
            var cur = new StringBuilder();
            bool inq = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (inq)
                {
                    cur.Append(ch);
                    if (ch == '\\' && i + 1 < s.Length) cur.Append(s[++i]);
                    else if (ch == '"') inq = false;
                }
                else if (ch == '"')
                {
                    inq = true;
                    cur.Append(ch);
                }
                else if (char.IsWhiteSpace(ch))
                {
                    if (cur.Length > 0) { toks.Add(cur.ToString()); cur.Clear(); }
                }
                else
                {
                    cur.Append(ch);
                }
            }
            if (cur.Length > 0) toks.Add(cur.ToString());
            return toks;
        }

        private static int ParseFloatToken(string tok, int lineNo)
        {
            if (tok.StartsWith("bits:", StringComparison.OrdinalIgnoreCase))
                return unchecked((int)Convert.ToUInt32(tok.Substring(5), 16));
            if (tok.EndsWith("f", StringComparison.OrdinalIgnoreCase))
            {
                string num = tok.Substring(0, tok.Length - 1);
                if (float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                    return FloatToBits(f);
            }
            throw new InvalidDataException("Line " + lineNo + ": expected float literal (e.g. 1.5f), got " + tok);
        }

        public static ScrFile Assemble(string text)
        {
            var scr = new ScrFile();
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            var pending = new List< Tuple<int, string[], ushort, int> >();
            var entryTokens = new Dictionary<int, string>();
            string mode = "head";

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int idx = 0; idx < lines.Length; idx++)
            {
                int lineNo = idx + 1;
                string line = StripAsmComment(lines[idx]).Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith(".", StringComparison.Ordinal))
                {
                    var parts = SplitAsmArgs(line);
                    string d = parts[0];
                    if (d == ".code") mode = "code";
                    else if (d == ".slots") scr.Slots = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    else if (d == ".entry")
                    {
                        int eIdx = int.Parse(parts[1].Substring(1), CultureInfo.InvariantCulture);
                        entryTokens[eIdx] = parts[2];
                    }
                    else if (d == ".cash")
                    {
                        byte kind = parts.Count > 2 ? byte.Parse(parts[2], CultureInfo.InvariantCulture) : (byte)0;
                        scr.Cash.Add(Tuple.Create(kind, Unquote(parts[1])));
                    }
                    else if (d == ".def" || d == ".func")
                    {
                        string symName = parts[1].StartsWith("\"", StringComparison.Ordinal) ? Unquote(parts[1]) : parts[1];
                        (d == ".def" ? scr.Defs : scr.Funcs).Add(symName);
                    }
                    else if (d == ".var")
                    {
                        ushort slot = ushort.Parse(parts[2], CultureInfo.InvariantCulture);
                        if (parts[1] == "float")
                            scr.Data.Add(Tuple.Create((ushort)2, slot, ParseFloatToken(parts[3], lineNo)));
                        else if (parts[1] == "vec")
                            scr.Data.Add(Tuple.Create((ushort)3, slot, slot + 1));
                        else
                            throw new InvalidDataException("Line " + lineNo + ": unknown .var kind " + parts[1]);
                    }
                    else if (d == ".data")
                    {
                        ushort ty = ushort.Parse(parts[1], CultureInfo.InvariantCulture);
                        ushort sl = ushort.Parse(parts[2], CultureInfo.InvariantCulture);
                        int val = parts[3].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? unchecked((int)Convert.ToUInt32(parts[3].Substring(2), 16))
                            : int.Parse(parts[3], CultureInfo.InvariantCulture);
                        scr.Data.Add(Tuple.Create(ty, sl, val));
                    }
                    else if (d == ".string")
                    {
                        scr.Strings.Add(Unquote(parts[1]));
                    }
                    else
                    {
                        throw new InvalidDataException("Line " + lineNo + ": unknown directive " + d);
                    }
                    continue;
                }

                if (line.EndsWith(":", StringComparison.Ordinal))
                {
                    string lbl = line.Substring(0, line.Length - 1).Trim();
                    if (mode != "code") throw new InvalidDataException("Line " + lineNo + ": label outside .code");
                    if (labels.ContainsKey(lbl)) throw new InvalidDataException("Line " + lineNo + ": duplicate label " + lbl);
                    labels[lbl] = pending.Count;
                    continue;
                }

                if (mode != "code")
                    throw new InvalidDataException("Line " + lineNo + ": instruction before .code");

                int sp = line.IndexOf(' ');
                string head = sp < 0 ? line : line.Substring(0, sp);
                string rest = sp < 0 ? "" : line.Substring(sp + 1);
                int dot = head.IndexOf('.');
                string nm = dot < 0 ? head : head.Substring(0, dot);
                string hx = dot < 0 ? "" : head.Substring(dot + 1);

                int low;
                if (!NameToOp.TryGetValue(nm, out low))
                {
                    if (nm.StartsWith("OP_", StringComparison.OrdinalIgnoreCase))
                        low = Convert.ToInt32(nm.Substring(3), 16);
                    else
                        throw new InvalidDataException("Line " + lineNo + ": unknown opcode " + nm);
                }
                int hi = hx.Length > 0 ? Convert.ToInt32(hx, 16) : 0;
                var argList = new List<string>(rest.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
                while (argList.Count < 3) argList.Add("0");
                if (argList.Count > 3)
                    throw new InvalidDataException("Line " + lineNo + ": expected at most 3 operands, got " + argList.Count);
                string[] args = argList.ToArray();

                pending.Add(Tuple.Create(pending.Count, args, (ushort)(low | hi), lineNo));
            }

            var strOffs = new int[scr.Strings.Count];
            int sOff = 0;
            for (int k = 0; k < scr.Strings.Count; k++)
            {
                strOffs[k] = sOff;
                sOff += Latin1.GetByteCount(scr.Strings[k]) + 1;
            }

            var defMap = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int k = 0; k < scr.Defs.Count; k++)
                if (!defMap.ContainsKey(scr.Defs[k])) defMap[scr.Defs[k]] = -(k + 1);

            var funcMap = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int k = 0; k < scr.Funcs.Count; k++)
                if (!funcMap.ContainsKey(scr.Funcs[k])) funcMap[scr.Funcs[k]] = -(k + 1);

            for (int k = 0; k < 5; k++)
            {
                if (!entryTokens.TryGetValue(k, out string tok) || tok == "none") scr.Entries[k] = -1;
                else if (tok.StartsWith("#", StringComparison.Ordinal)) scr.Entries[k] = int.Parse(tok.Substring(1), CultureInfo.InvariantCulture);
                else if (labels.TryGetValue(tok, out int target)) scr.Entries[k] = target;
                else throw new InvalidDataException("Undefined entry label: " + tok);
            }

            int ParseSlot(string tok, int lineNo)
            {
                if (tok.StartsWith("$", StringComparison.Ordinal))
                {
                    string name = tok.Substring(1);
                    if (!defMap.TryGetValue(name, out int g))
                        throw new InvalidDataException("Line " + lineNo + ": unknown global " + tok);
                    return g;
                }
                if (tok.StartsWith("r", StringComparison.Ordinal) && int.TryParse(tok.Substring(1), out int r) && r >= 0)
                    return r;
                if (tok.StartsWith("v", StringComparison.Ordinal) && int.TryParse(tok.Substring(1), out int v) && v >= 0)
                    return v;
                if (tok.StartsWith("#", StringComparison.Ordinal))
                    return int.Parse(tok.Substring(1), CultureInfo.InvariantCulture);
                if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plain))
                    return plain;
                throw new InvalidDataException("Line " + lineNo + ": bad slot operand " + tok);
            }

            foreach (var item in pending)
            {
                int i = item.Item1;
                string[] toks = item.Item2;
                ushort op = item.Item3;
                int lineNo = item.Item4;
                int low = op & 0xFF;
                int hi = op & 0xFF00;
                var kinds = GetOperandKinds(low);
                int[] vals = new int[3];

                for (int pos = 0; pos < 3; pos++)
                {
                    string kind = kinds[pos];
                    string tok = toks[pos];
                    bool isFloat = (pos == 0 && (hi & 0x0100) != 0) || (pos == 1 && (hi & 0x1000) != 0);

                    if (kind == "val" && tok.StartsWith("s", StringComparison.Ordinal) && int.TryParse(tok.Substring(1), out int sIdx))
                    {
                        if (op != 0x1011 || pos != 1)
                            throw new InvalidDataException("Line " + lineNo + ": string ref only valid as b of MOV.1000");
                        if (sIdx < 0 || sIdx >= strOffs.Length)
                            throw new InvalidDataException("Line " + lineNo + ": string index out of range: " + tok);
                        vals[pos] = strOffs[sIdx];
                    }
                    else if (kind == "val" && isFloat)
                    {
                        vals[pos] = ParseFloatToken(tok, lineNo);
                    }
                    else if (kind == "val" || kind == "slot")
                    {
                        vals[pos] = ParseSlot(tok, lineNo);
                    }
                    else if (kind == "rel")
                    {
                        if (tok.StartsWith("#", StringComparison.Ordinal))
                            vals[pos] = int.Parse(tok.Substring(1), CultureInfo.InvariantCulture);
                        else if (labels.TryGetValue(tok, out int target))
                            vals[pos] = target - i;
                        else
                            throw new InvalidDataException("Line " + lineNo + ": undefined label " + tok);
                    }
                    else if (kind == "call")
                    {
                        if (tok.StartsWith("@", StringComparison.Ordinal))
                        {
                            string fn = tok.Substring(1);
                            if (!funcMap.TryGetValue(fn, out int fIdx))
                                throw new InvalidDataException("Line " + lineNo + ": unknown function " + tok);
                            vals[pos] = fIdx;
                        }
                        else if (funcMap.TryGetValue(tok, out int fIdx))
                        {
                            vals[pos] = fIdx;
                        }
                        else if (tok.StartsWith("#", StringComparison.Ordinal))
                            vals[pos] = int.Parse(tok.Substring(1), CultureInfo.InvariantCulture);
                        else if (labels.TryGetValue(tok, out int target))
                            vals[pos] = target;
                        else
                            throw new InvalidDataException("Line " + lineNo + ": undefined call target " + tok);
                    }
                    else
                    {
                        vals[pos] = int.Parse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    }
                }

                scr.Code.Add(Tuple.Create(op, vals[0], vals[1], vals[2]));
            }

            return scr;
        }

        // ═════════════════════════════════════════════════════════════════
        // Structured C-like Decompiler (scrdec.py + scrsrc.py port)
        // ═════════════════════════════════════════════════════════════════

        private static readonly Dictionary<int, string> CmpOps = new Dictionary<int, string>
        {
            { 0x05, "&" }, { 0x07, "||" }, { 0x08, "&&" }, { 0x09, "==" }, { 0x0A, "!=" },
            { 0x0B, ">" }, { 0x0C, "<" },  { 0x0D, ">=" }, { 0x0E, "<=" },
            { 0x01, "*" }, { 0x02, "/" },  { 0x03, "+" },  { 0x04, "-" },
        };

        private static readonly Dictionary<string, int> OpPrec = new Dictionary<string, int>
        {
            { "||", 1 }, { "&&", 2 }, { "==", 3 }, { "!=", 3 },
            { "<", 4 },  { ">", 4 },  { "<=", 4 }, { ">=", 4 },
            { "&", 5 },  { "+", 6 },  { "-", 6 },  { "*", 7 }, { "/", 7 },
        };

        internal sealed class Expr
        {
            public string Kind;
            public int IntVal;
            public string StrVal;
            public Expr Left, Right;
            public List<Expr> Args;
            public bool IsLatent;
            public Expr LatentDur;
        }

        internal sealed class Stmt
        {
            public string Kind;
            public string Op;
            public Expr Lhs, Rhs;
            public List<Stmt> ThenBody, ElseBody;
        }

        private sealed class DecState
        {
            public Expr[] Regs = new Expr[16];
            public HashSet<int> Fresh = new HashSet<int>();
            public Dictionary<int, int> Pending = new Dictionary<int, int>();
            public Dictionary<int, Stack< Tuple<Expr, bool> >> Saved = new Dictionary<int, Stack< Tuple<Expr, bool> >>();
            public bool Pushed;
            public Expr Pushf;
            public bool PushfOver;
        }

        private static string PrintExpr(Expr e, int parentPrec = 0)
        {
            if (e == null) return "/*null*/";
            switch (e.Kind)
            {
                case "f": return FormatFloatSrc(e.IntVal);
                case "s": return Quote(e.StrVal);
                case "var": return "v" + e.IntVal;
                case "param": return "arg" + e.IntVal;
                case "glob": return e.StrVal;
                case "field":
                    string b = PrintExpr(e.Left, 99);
                    return e.Left.Kind == "field" ? b + "[" + e.IntVal + "]" : b + ".f" + e.IntVal;
                case "bin":
                    int p = OpPrec[e.StrVal];
                    string s = PrintExpr(e.Left, p) + " " + e.StrVal + " " + PrintExpr(e.Right, p + 1);
                    return p < parentPrec ? "(" + s + ")" : s;
                case "un":
                    if (e.Left.Kind == "f") return e.StrVal + "(" + PrintExpr(e.Left) + ")";
                    return e.StrVal + PrintExpr(e.Left, 9);
                case "call":
                    string lat = e.LatentDur != null
                        ? "latent(" + PrintExpr(e.LatentDur) + ") "
                        : (e.IsLatent ? "latent " : "");
                    return lat + e.StrVal + "(" + string.Join(", ", e.Args.Select(a => PrintExpr(a))) + ")";
                default:
                    return e.StrVal ?? "?";
            }
        }

        private static void PrintBlock(List<Stmt> body, int indent, List<string> lines)
        {
            string pad = new string(' ', indent * 4);
            foreach (var s in body)
            {
                if (s.Kind == "assign")
                    lines.Add(pad + PrintExpr(s.Lhs) + " = " + PrintExpr(s.Rhs) + ";");
                else if (s.Kind == "opassign")
                    lines.Add(pad + PrintExpr(s.Lhs) + " " + s.Op + "= " + PrintExpr(s.Rhs) + ";");
                else if (s.Kind == "callstmt")
                    lines.Add(pad + PrintExpr(s.Lhs) + ";");
                else if (s.Kind == "pushf")
                    lines.Add(pad + "pushf(" + PrintExpr(s.Lhs) + ");");
                else if (s.Kind == "return")
                    lines.Add(pad + (s.Lhs != null ? "return " + PrintExpr(s.Lhs) + ";" : "return;"));
                else if (s.Kind == "break" || s.Kind == "continue" || s.Kind == "nop")
                    lines.Add(pad + s.Kind + ";");
                else if (s.Kind == "if")
                {
                    lines.Add(pad + "if (" + PrintExpr(s.Lhs) + ") {");
                    PrintBlock(s.ThenBody, indent + 1, lines);
                    var els = s.ElseBody;
                    while (els != null && els.Count > 0)
                    {
                        if (els.Count == 1 && els[0].Kind == "if")
                        {
                            lines.Add(pad + "} else if (" + PrintExpr(els[0].Lhs) + ") {");
                            PrintBlock(els[0].ThenBody, indent + 1, lines);
                            els = els[0].ElseBody;
                        }
                        else
                        {
                            lines.Add(pad + "} else {");
                            PrintBlock(els, indent + 1, lines);
                            els = null;
                        }
                    }
                    lines.Add(pad + "}");
                }
                else if (s.Kind == "while")
                {
                    lines.Add(pad + "while (" + PrintExpr(s.Lhs) + ") {");
                    PrintBlock(s.ThenBody, indent + 1, lines);
                    lines.Add(pad + "}");
                }
            }
        }

        public string DecompileToSource()
        {
            int n = Code.Count;
            var off2idx = new Dictionary<int, int>();
            int off = 0;
            for (int k = 0; k < Strings.Count; k++)
            {
                if (!off2idx.ContainsKey(off)) off2idx[off] = k;
                off += Latin1.GetByteCount(Strings[k]) + 1;
            }

            int firstZero = -1;
            for (int i = 0; i < n; i++)
            {
                if (Code[i].Item1 == 0x1011 && Code[i].Item3 == 0) { firstZero = i; break; }
            }

            int nextCallId = 0;

            int? StringAt(int i)
            {
                var ins = Code[i];
                if (ins.Item1 == 0x1011 && off2idx.TryGetValue(ins.Item3, out int idx) && (ins.Item3 != 0 || i == firstZero))
                    return idx;
                return null;
            }

            void Consume(DecState st, Expr e)
            {
                if (e == null) return;
                if (e.Kind == "call")
                {
                    st.Pending.Remove(e.IntVal);
                    foreach (var a in e.Args) Consume(st, a);
                }
                else if (e.Kind == "bin") { Consume(st, e.Left); Consume(st, e.Right); }
                else if (e.Kind == "un" || e.Kind == "field") Consume(st, e.Left);
            }

            Expr Opnd(DecState st, int hi, string which, int v)
            {
                bool floatFlag = (hi & (which == "a" ? 0x0100 : 0x1000)) != 0;
                if (floatFlag) return new Expr { Kind = "f", IntVal = v };
                if (v < 0)
                {
                    string g = (-v <= Defs.Count) ? Defs[-v - 1] : ("#" + v);
                    return new Expr { Kind = "glob", StrVal = g };
                }
                if (v >= 16) return new Expr { Kind = "var", IntVal = v };
                var c = st.Regs[v];
                if (c == null) return new Expr { Kind = "param", IntVal = v };
                Consume(st, c);
                st.Fresh.Remove(v);
                return c;
            }

            void Dest(DecState st, int d, Expr expr, List<Stmt> outStmts)
            {
                if (d == 0 && st.Pushf != null) st.PushfOver = true;
                if (d < 0 || d >= 16)
                {
                    Expr tgt = d < 0
                        ? new Expr { Kind = "glob", StrVal = (-d <= Defs.Count) ? Defs[-d - 1] : ("#" + d) }
                        : new Expr { Kind = "var", IntVal = d };
                    Consume(st, expr);
                    outStmts.Add(new Stmt { Kind = "assign", Lhs = tgt, Rhs = expr });
                    st.Fresh.Clear();
                }
                else
                {
                    st.Regs[d] = expr;
                    st.Fresh.Add(d);
                }
            }

            void Step(DecState st, int i, List<Stmt> outStmts)
            {
                var ins = Code[i];
                int low = ins.Item1 & 0xFF;
                int hi = ins.Item1 & 0xFF00;
                int a = ins.Item2, b = ins.Item3, c = ins.Item4;

                if (low == 0x13) // ENTER
                {
                    if (!st.Saved.TryGetValue(a, out var stack))
                        st.Saved[a] = stack = new Stack< Tuple<Expr, bool> >();
                    stack.Push(Tuple.Create(st.Regs[a], st.Fresh.Contains(a)));
                    st.Fresh.Remove(a);
                    return;
                }
                if (low == 0x14) // LEAVE
                {
                    if (!st.Saved.TryGetValue(a, out var stack) || stack.Count == 0)
                        throw new InvalidOperationException("LEAVE without ENTER at instruction " + i);
                    var top = stack.Pop();
                    st.Regs[a] = top.Item1;
                    if (top.Item2) st.Fresh.Add(a);
                    return;
                }
                if (low == 0x1E) // PUSHF
                {
                    st.Pushed = true;
                    int? nxt = (i + 1 < n) ? (int?)(Code[i + 1].Item1 & 0xFF) : null;
                    if ((hi & 0x0100) == 0 && nxt != 0x1D)
                    {
                        var v = Opnd(st, hi, "a", a);
                        Consume(st, v);
                        outStmts.Add(new Stmt { Kind = "pushf", Lhs = v });
                        st.Regs[0] = null;
                        st.Fresh.Clear();
                        return;
                    }
                    st.Regs[0] = (hi & 0x0100) != 0 ? new Expr { Kind = "f", IntVal = a } : Opnd(st, hi, "a", a);
                    st.Fresh.Add(0);
                    st.Pushf = (hi & 0x0100) != 0 ? st.Regs[0] : null;
                    st.PushfOver = false;
                    return;
                }
                if (low == 0x12) // LOAD
                {
                    var baseExpr = Opnd(st, 0, "a", a);
                    st.Regs[c] = new Expr { Kind = "field", Left = baseExpr, IntVal = b };
                    st.Fresh.Add(c);
                    return;
                }
                if (low == 0x11) // MOV
                {
                    int? sIdx = StringAt(i);
                    if ((hi & 0x8000) != 0)
                    {
                        var lv = st.Regs[a];
                        var rhs = Opnd(st, hi, "b", b);
                        if (lv == null || lv.Kind != "field")
                            throw new InvalidOperationException("Store through non-reference at instruction " + i);
                        Consume(st, lv);
                        outStmts.Add(new Stmt { Kind = "assign", Lhs = lv, Rhs = rhs });
                        st.Fresh.Clear();
                        return;
                    }
                    var src = sIdx.HasValue
                        ? new Expr { Kind = "s", StrVal = Strings[sIdx.Value] }
                        : Opnd(st, hi, "b", b);
                    Dest(st, a, src, outStmts);
                    return;
                }
                if (CmpOps.TryGetValue(low, out string cmpOp))
                {
                    var A = Opnd(st, hi, "a", a);
                    var B = Opnd(st, hi, "b", b);
                    if ((hi & 0x8000) != 0)
                    {
                        var lv = st.Regs[a];
                        if (lv == null || lv.Kind != "field")
                            throw new InvalidOperationException("Compound store through non-reference at instruction " + i);
                        Consume(st, lv);
                        outStmts.Add(new Stmt { Kind = "opassign", Op = cmpOp, Lhs = lv, Rhs = B });
                        st.Fresh.Clear();
                        return;
                    }
                    if ((low == 0x01 || low == 0x02 || low == 0x03 || low == 0x04)
                        && c == a && (hi & 0x0100) == 0 && (c >= 16 || c < 0))
                    {
                        Expr tgt = c >= 16
                            ? new Expr { Kind = "var", IntVal = c }
                            : new Expr { Kind = "glob", StrVal = (-c <= Defs.Count) ? Defs[-c - 1] : ("#" + c) };
                        outStmts.Add(new Stmt { Kind = "opassign", Op = cmpOp, Lhs = tgt, Rhs = B });
                        st.Fresh.Clear();
                        return;
                    }
                    Dest(st, c, new Expr { Kind = "bin", StrVal = cmpOp, Left = A, Right = B }, outStmts);
                    return;
                }
                if (low == 0x0F || low == 0x10) // NOT / NEG
                {
                    var A = Opnd(st, hi, "a", a);
                    Dest(st, c, new Expr { Kind = "un", StrVal = low == 0x0F ? "!" : "-", Left = A }, outStmts);
                    return;
                }
                if (low == 0x1C || low == 0x1D) // CALL / CALLP
                {
                    bool isBuiltin = a < 0 && -a <= Funcs.Count;
                    string tgtName = isBuiltin ? Funcs[-a - 1] : ("r" + a);
                    int m = 0;
                    while (st.Fresh.Contains(m)) m++;
                    bool pushed = st.Pushed;
                    st.Pushed = false;
                    if (low == 0x1D && !pushed && m == 1 && st.Regs[0] != null && st.Regs[0].Kind == "call"
                        && (!isBuiltin || tgtName == "sleep"))
                    {
                        m = 0;
                    }
                    var args = new List<Expr>();
                    for (int k = 0; k < m; k++)
                    {
                        var v = st.Regs[k];
                        args.Add(v ?? new Expr { Kind = "param", IntVal = k });
                        if (v != null) Consume(st, v);
                    }
                    int cid = ++nextCallId;
                    bool isp = low == 0x1D;
                    var pf = st.Pushf;
                    bool over = st.PushfOver;
                    st.Pushf = null;
                    st.PushfOver = false;

                    var callExpr = new Expr
                    {
                        Kind = "call",
                        IntVal = cid,
                        StrVal = tgtName,
                        Args = args,
                        IsLatent = isp,
                        LatentDur = (isp && pf != null && pf.Kind == "f" && over) ? pf : null,
                    };
                    st.Pending[cid] = outStmts.Count;
                    outStmts.Add(new Stmt { Kind = "pcall", Lhs = callExpr });
                    st.Regs[b] = callExpr;
                    st.Fresh.Clear();
                    if (isBuiltin && ReturnFuncs.Contains(tgtName))
                        st.Fresh.Add(b);
                    return;
                }
                throw new InvalidOperationException("Unsupported opcode at instruction " + i);
            }

            List<Stmt> Finish(DecState st, List<Stmt> raw)
            {
                var keep = new List<Stmt>();
                foreach (var s in raw)
                {
                    if (s.Kind == "pcall")
                    {
                        if (!st.Pending.ContainsKey(s.Lhs.IntVal)) continue;
                        keep.Add(new Stmt { Kind = "callstmt", Lhs = s.Lhs });
                    }
                    else keep.Add(s);
                }
                return keep;
            }

            List<Stmt> ParseBlock(int i, int end, Tuple<int, int> loop = null)
            {
                var st = new DecState();
                var outStmts = new List<Stmt>();
                while (i < end)
                {
                    var ins = Code[i];
                    int low = ins.Item1 & 0xFF;
                    int hi = ins.Item1 & 0xFF00;
                    int a = ins.Item2, b = ins.Item3;

                    if (low == 0x00) // END
                    {
                        if (i != end - 1) throw new InvalidOperationException("Mid-block END at instruction " + i);
                        i++;
                        continue;
                    }
                    if (low == 0x18) // RETURN
                    {
                        if (a != 0 || hi != 0)
                        {
                            var rv = Opnd(st, hi, "a", a);
                            Consume(st, rv);
                            outStmts = Finish(st, outStmts);
                            outStmts.Add(new Stmt { Kind = "return", Lhs = rv });
                        }
                        else
                        {
                            outStmts = Finish(st, outStmts);
                            outStmts.Add(new Stmt { Kind = "return" });
                        }
                        st = new DecState();
                        i++;
                        continue;
                    }
                    if (low == 0x1B) // JMP
                    {
                        int tgt = i + a;
                        outStmts = Finish(st, outStmts);
                        if (a == 1) outStmts.Add(new Stmt { Kind = "nop" });
                        else if (loop != null && tgt == loop.Item2) outStmts.Add(new Stmt { Kind = "break" });
                        else if (loop != null && tgt == loop.Item1) outStmts.Add(new Stmt { Kind = "continue" });
                        else throw new InvalidOperationException("Unstructured jump at instruction " + i + " -> " + tgt);
                        st = new DecState();
                        i++;
                        continue;
                    }
                    if (low == 0x1A) // JZ
                    {
                        var cond = Opnd(st, hi, "a", a);
                        int t = i + b;
                        outStmts = Finish(st, outStmts);
                        if (!(i < t && t <= end))
                            throw new InvalidOperationException("Jump out of block at instruction " + i);
                        var last = Code[t - 1];
                        if (t - 1 > i && (last.Item1 & 0xFF) == 0x1B && (t - 1) + last.Item2 <= i)
                        {
                            var body = ParseBlock(i + 1, t - 1, Tuple.Create((t - 1) + last.Item2, t));
                            outStmts.Add(new Stmt { Kind = "while", Lhs = cond, ThenBody = body });
                            i = t;
                        }
                        else if (t - 1 > i && (last.Item1 & 0xFF) == 0x1B && last.Item2 > 1 && (t - 1) + last.Item2 <= end)
                        {
                            int u = (t - 1) + last.Item2;
                            var thenB = ParseBlock(i + 1, t - 1, loop);
                            var elseB = ParseBlock(t, u, loop);
                            outStmts.Add(new Stmt { Kind = "if", Lhs = cond, ThenBody = thenB, ElseBody = elseB });
                            i = u;
                        }
                        else
                        {
                            var thenB = ParseBlock(i + 1, t, loop);
                            outStmts.Add(new Stmt { Kind = "if", Lhs = cond, ThenBody = thenB });
                            i = t;
                        }
                        st = new DecState();
                        continue;
                    }
                    Step(st, i, outStmts);
                    i++;
                }
                return Finish(st, outStmts);
            }

            var lines = new List<string>
            {
                "// AS3D script source (decompiled by AirstrikeMapStudio / ScriptStudio)",
                "slots " + Slots + ";",
            };
            for (int k = 0; k < 5; k++)
                if (Entries[k] >= 0)
                    lines.Add("entry " + RoleNames[k] + " = r" + Entries[k] + ";");
            if (Cash.Count > 0)
                lines.Add("cash " + string.Join(", ", Cash.Select(c => Quote(c.Item2) + (c.Item1 != 0 ? " " + c.Item1 : ""))) + ";");
            if (Defs.Count > 0)
                lines.Add("globals " + string.Join(", ", Defs) + ";");
            if (Funcs.Count > 0)
                lines.Add("functions " + string.Join(", ", Funcs) + ";");
            foreach (var d in Data)
            {
                if (d.Item1 == 2)
                    lines.Add("var float v" + d.Item2 + " = " + FormatFloatSrc(d.Item3) + ";");
                else if (d.Item1 == 3 && d.Item3 == d.Item2 + 1)
                    lines.Add("var vec v" + d.Item2 + ";");
                else
                    lines.Add(string.Format(CultureInfo.InvariantCulture, "data {0} {1} 0x{2:x8};", d.Item1, d.Item2, (uint)d.Item3));
            }
            lines.Add("");

            var starts = new List<int> { 0 };
            for (int i = 0; i < n; i++)
                if ((Code[i].Item1 & 0xFF) == 0 && i + 1 < n)
                    starts.Add(i + 1);
            var ends = new List<int>(starts.Skip(1)) { n };

            var roleMap = new Dictionary<int, string>();
            for (int k = 0; k < 5; k++)
                if (Entries[k] >= 0 && !roleMap.ContainsKey(Entries[k]))
                    roleMap[Entries[k]] = RoleNames[k];

            for (int idx = 0; idx < starts.Count; idx++)
            {
                int s = starts[idx], e = ends[idx];
                if (s == e) continue;
                roleMap.TryGetValue(s, out string role);
                lines.Add("routine r" + s + (role != null ? " /* " + role + " */" : "") + " {");
                try
                {
                    var body = ParseBlock(s, e);
                    PrintBlock(body, 1, lines);
                }
                catch (Exception ex)
                {
                    lines.Add("    // [Hand-patched / unstructured routine: " + ex.Message + "]");
                    lines.Add("    // Switch to the Assembly (.asm) tab to view or edit instructions.");
                }
                lines.Add("}");
                lines.Add("");
            }

            return string.Join("\r\n", lines);
        }

        public string BuildInfoSummary(string filePath = "")
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(filePath))
                sb.AppendLine("File:              " + filePath);
            sb.AppendLine("Variable slots:    " + Slots);
            sb.AppendLine("Active entries:    " + string.Join(", ",
                Enumerable.Range(0, 5).Select(k => RoleNames[k] + "=" + (Entries[k] >= 0 ? ("instr " + Entries[k]) : "none"))));
            sb.AppendLine("Instructions:      " + Code.Count);
            sb.AppendLine("Waypoint support:  " + (CallsWaypointFunctions
                ? "YES (calls MoveToNextWP / lMoveToNextWP / GetWaypointDelay)"
                : "NO (script does not reference waypoint movement functions)"));
            sb.AppendLine("Preloaded (CASH):  " + (Cash.Count > 0 ? string.Join(", ", Cash.Select(c => c.Item2)) : "(none)"));
            sb.AppendLine("Globals (DEFS):    " + (Defs.Count > 0 ? string.Join(", ", Defs) : "(none)"));
            sb.AppendLine("Engine functions:  " + (Funcs.Count > 0 ? string.Join(", ", Funcs) : "(none)"));
            sb.AppendLine();
            sb.AppendLine("── String Table (" + Strings.Count + " entries) ──");
            int off = 0;
            for (int i = 0; i < Strings.Count; i++)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "  s{0,-3} (+{1,4}): {2}\r\n", i, off, Quote(Strings[i]));
                off += Latin1.GetByteCount(Strings[i]) + 1;
            }
            return sb.ToString();
        }
    }
}
