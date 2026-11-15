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

    public sealed partial class GlTerrainWindow
    {
        /// <summary>
        /// Opens the built-in Script Studio dialog for inspecting, editing,
        /// decompiling, and assembling .scr scripts.
        /// </summary>
        public void OpenScriptInspector()
        {
            OpenScriptStudio(null);
        }

        /// <summary>
        /// Opens the built-in Script Studio dialog, optionally pre-selecting <paramref name="targetScript"/>.
        /// </summary>
        public void OpenScriptStudio(string targetScript = null)
        {
            using (var dialog = new ScriptStudioDialog(inspectorAssets, Preview, draft => Preview.ApplyProperties(draft), targetScript))
            {
                dialog.ShowDialog(this);
            }
        }
    }

    /// <summary>
    /// Comprehensive standalone desktop Script Studio dialog for AirStrike 3D
    /// compiled .scr ("RCSL") bytecode files. Provides a script library browser,
    /// C-like decompilation, editable assembly, instant F5 assembly &amp; saving,
    /// assembly diff view, engine API reference, and one-click map object assignment.
    /// </summary>
    /// <summary>
    /// Detailed metadata for an engine builtin function across AirStrike 3D and its sequel trilogy.
    /// </summary>
    public sealed class EngineBuiltin
    {
        public string Name { get; set; }
        public string Signature { get; set; }
        public string ReturnType { get; set; }
        public string Category { get; set; }
        public bool IsSequelOnly { get; set; }
        public string Description { get; set; }

        public string EngineTarget => IsSequelOnly ? "Sequel (AS2 / Gulf / OpenAirStrike)" : "Original AirStrike 3D + Sequel";
        public string ShortTag => IsSequelOnly ? "[Sequel]" : "[AS3D]";

        public string TooltipText => string.Format(
            "{0}\r\nCompatibility: {1}\r\nCategory: {2} | Returns: {3}\r\n\r\n{4}",
            Signature, EngineTarget, Category, ReturnType, Description);
    }

    /// <summary>
    /// Master repository containing all 101 script built-in functions discovered in the engine.
    /// Original AirStrike 3D: 85 built-in functions.
    /// AirStrike II / Gulf Thunder: 101 built-in functions (16 sequel extensions).
    /// </summary>
    public static class EngineBuiltinCatalog
    {
        public static readonly List<EngineBuiltin> All = new List<EngineBuiltin>
        {
            new EngineBuiltin { Name = "debug", Signature = "debug()", ReturnType = "void", Category = "Math / Debug", IsSequelOnly = false, Description = "Prints debug output / script trace message to console or debugger." },
            new EngineBuiltin { Name = "random", Signature = "random([max])", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns pseudorandom float between 0.0 and max (or 0.0..1.0)." },
            new EngineBuiltin { Name = "crandom", Signature = "crandom()", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns centered pseudorandom float between -1.0 and +1.0." },
            new EngineBuiltin { Name = "sin", Signature = "sin(rad)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Calculates trigonometric sine of angle in radians." },
            new EngineBuiltin { Name = "cos", Signature = "cos(rad)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Calculates trigonometric cosine of angle in radians." },
            new EngineBuiltin { Name = "tan", Signature = "tan(rad)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Calculates trigonometric tangent of angle in radians." },
            new EngineBuiltin { Name = "atan", Signature = "atan(x)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Calculates arc tangent of x in radians." },
            new EngineBuiltin { Name = "atan2", Signature = "atan2(y, x)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Calculates arc tangent of (y, x) in radians with quadrant disambiguation." },
            new EngineBuiltin { Name = "abs", Signature = "abs(val)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns absolute value of number." },
            new EngineBuiltin { Name = "min", Signature = "min(a, b)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns minimum of two values." },
            new EngineBuiltin { Name = "max", Signature = "max(a, b)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Returns maximum of two values." },
            new EngineBuiltin { Name = "lerp", Signature = "lerp(a, b, t)", ReturnType = "float", Category = "Math", IsSequelOnly = false, Description = "Linear interpolation between a and b by factor t (0.0..1.0)." },
            new EngineBuiltin { Name = "copysign", Signature = "copysign(x, y)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Returns magnitude of x with the sign of y." },
            new EngineBuiltin { Name = "floor", Signature = "floor(val)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Returns largest integer value less than or equal to val." },
            new EngineBuiltin { Name = "floor2", Signature = "floor2(val, step)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Snaps val down to nearest step interval." },
            new EngineBuiltin { Name = "fmod", Signature = "fmod(x, y)", ReturnType = "float", Category = "Math", IsSequelOnly = true, Description = "Floating-point remainder of x divided by y." },
            new EngineBuiltin { Name = "vec_copy", Signature = "vec_copy(dst, src)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Copies 3D vector components from src into dst." },
            new EngineBuiltin { Name = "vec_add", Signature = "vec_add(dst, a, b)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Vector addition: dst = a + b." },
            new EngineBuiltin { Name = "vec_sub", Signature = "vec_sub(dst, a, b)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Vector subtraction: dst = a - b." },
            new EngineBuiltin { Name = "vec_ma", Signature = "vec_ma(dst, a, scale, b)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Vector multiply-accumulate: dst = a + scale * b." },
            new EngineBuiltin { Name = "vec_length", Signature = "vec_length(v)", ReturnType = "float", Category = "Vector", IsSequelOnly = false, Description = "Returns Euclidean length/magnitude of 3D vector v." },
            new EngineBuiltin { Name = "vec_norm", Signature = "vec_norm(v)", ReturnType = "float", Category = "Vector", IsSequelOnly = false, Description = "Normalizes vector in-place to unit length and returns original length." },
            new EngineBuiltin { Name = "vec_scale", Signature = "vec_scale(v, s)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Scales 3D vector components in-place by scalar s." },
            new EngineBuiltin { Name = "vec_setlen", Signature = "vec_setlen(v, newLen)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Sets 3D vector to specified length preserving direction." },
            new EngineBuiltin { Name = "vec_toyaw", Signature = "vec_toyaw(v)", ReturnType = "float", Category = "Vector", IsSequelOnly = false, Description = "Calculates yaw angle in degrees from directional vector." },
            new EngineBuiltin { Name = "vec_toangles", Signature = "vec_toangles(v, outAngles)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Converts directional vector into Euler pitch/yaw/roll angles." },
            new EngineBuiltin { Name = "ClearAxis", Signature = "ClearAxis(matrix)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Resets 3x3 orientation rotation matrix to identity." },
            new EngineBuiltin { Name = "AnglesToAxis", Signature = "AnglesToAxis(angles, matrix)", ReturnType = "void", Category = "Vector", IsSequelOnly = false, Description = "Converts Euler angles into 3x3 rotation transformation matrix." },
            new EngineBuiltin { Name = "create", Signature = "create(defName, posVec)", ReturnType = "entity", Category = "Entity", IsSequelOnly = false, Description = "Spawns entity, weapon projectile, or particle effect at position." },
            new EngineBuiltin { Name = "remove", Signature = "remove(entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Despawns and deletes entity from the game world." },
            new EngineBuiltin { Name = "activate", Signature = "activate(entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Activates dormant or sleeping entity (enables AI/script execution)." },
            new EngineBuiltin { Name = "deactivate", Signature = "deactivate(entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Deactivates entity without removing it from memory." },
            new EngineBuiltin { Name = "getentity", Signature = "getentity(id)", ReturnType = "entity", Category = "Entity", IsSequelOnly = false, Description = "Resolves entity pointer/handle by object ID." },
            new EngineBuiltin { Name = "setskin", Signature = "setskin(entity, skinName)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Swaps texture skin of entity in real-time." },
            new EngineBuiltin { Name = "SetModel", Signature = "SetModel(modelName)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Replaces entity 3D model mesh with new asset." },
            new EngineBuiltin { Name = "SetFlag", Signature = "SetFlag(flagBit, entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Sets internal entity state flag (e.g. FL_ONWATER, FL_NODRAW)." },
            new EngineBuiltin { Name = "ClearFlag", Signature = "ClearFlag(flagBit, entity)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Clears internal entity state flag bit." },
            new EngineBuiltin { Name = "AttachEntity", Signature = "AttachEntity(child, parent, tagName, linkAngles)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Attaches child entity to parent model attachment tag." },
            new EngineBuiltin { Name = "DetachEntity", Signature = "DetachEntity(child)", ReturnType = "void", Category = "Attach", IsSequelOnly = true, Description = "Detaches child entity from its parent attachment point." },
            new EngineBuiltin { Name = "AttachActivate", Signature = "AttachActivate(tagName)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Activates attached sub-entities bound to named tag." },
            new EngineBuiltin { Name = "AttachDeactivate", Signature = "AttachDeactivate(tagName)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Deactivates attached sub-entities bound to named tag." },
            new EngineBuiltin { Name = "AttachCallback", Signature = "AttachCallback(tagName, msg, p1, p2)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Dispatches event callback to all entities attached to named tag." },
            new EngineBuiltin { Name = "ParentCallback", Signature = "ParentCallback(msg, p1, p2)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Sends callback notification upward to parent entity." },
            new EngineBuiltin { Name = "callback", Signature = "callback(target, msg, p1, p2)", ReturnType = "void", Category = "Attach", IsSequelOnly = false, Description = "Dispatches script callback event directly to target entity." },
            new EngineBuiltin { Name = "move", Signature = "move(speed)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Translates entity forward along orientation heading at given speed." },
            new EngineBuiltin { Name = "movex", Signature = "movex(dx)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Translates entity along local/world X axis." },
            new EngineBuiltin { Name = "movey", Signature = "movey(dy)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Translates entity along local/world Y axis." },
            new EngineBuiltin { Name = "movez", Signature = "movez(dz)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Translates entity along local/world Z axis." },
            new EngineBuiltin { Name = "rotate", Signature = "rotate(speed)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Applies continuous rotational angular velocity." },
            new EngineBuiltin { Name = "rotatex", Signature = "rotatex(deg)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Increments pitch rotation around X axis." },
            new EngineBuiltin { Name = "rotatey", Signature = "rotatey(deg)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Increments yaw rotation around Y axis." },
            new EngineBuiltin { Name = "rotatez", Signature = "rotatez(deg)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Increments roll rotation around Z axis." },
            new EngineBuiltin { Name = "sleep", Signature = "sleep(seconds)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Latent yield: pauses script execution for specified duration." },
            new EngineBuiltin { Name = "RotateTo", Signature = "RotateTo(targetVec, speed)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Rotates entity smoothly toward target direction at speed." },
            new EngineBuiltin { Name = "lRotateTo", Signature = "lRotateTo(targetVec, speed)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Linear straight rotation toward target direction." },
            new EngineBuiltin { Name = "RotateToClamp", Signature = "RotateToClamp(targetVec, mask, maxAngle, minAngle)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Clamped rotation toward target bounded by min/max limits." },
            new EngineBuiltin { Name = "lRotateToClamp", Signature = "lRotateToClamp(targetVec, mask, maxAngle, minAngle)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Linear clamped rotation bounded by angle limits." },
            new EngineBuiltin { Name = "MoveToNextWP", Signature = "MoveToNextWP(orient)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Advances unit along waypoint route toward next waypoint (latent)." },
            new EngineBuiltin { Name = "lMoveToNextWP", Signature = "lMoveToNextWP(orient)", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Moves unit linearly toward next waypoint along route." },
            new EngineBuiltin { Name = "RotateToNextWP", Signature = "RotateToNextWP()", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Rotates unit orientation to face next waypoint." },
            new EngineBuiltin { Name = "lRotateToNextWP", Signature = "lRotateToNextWP()", ReturnType = "void", Category = "Movement", IsSequelOnly = false, Description = "Linear rotational step toward next waypoint." },
            new EngineBuiltin { Name = "GetWaypointDelay", Signature = "GetWaypointDelay(wpIdx)", ReturnType = "float", Category = "Movement", IsSequelOnly = false, Description = "Returns pause/delay duration configured on waypoint." },
            new EngineBuiltin { Name = "TerrainHeight", Signature = "TerrainHeight(x, y)", ReturnType = "float", Category = "Environment", IsSequelOnly = false, Description = "Returns terrain elevation at specified (X, Y) coordinates." },
            new EngineBuiltin { Name = "WaterHeight", Signature = "WaterHeight(x, y)", ReturnType = "float", Category = "Environment", IsSequelOnly = true, Description = "Returns water surface level at specified (X, Y) coordinates." },
            new EngineBuiltin { Name = "TerraMorph", Signature = "TerraMorph(posVec, radius, depth)", ReturnType = "void", Category = "Environment", IsSequelOnly = true, Description = "Real-time terrain deformation: creates crater or trench at position." },
            new EngineBuiltin { Name = "Shoot", Signature = "Shoot(weaponName, tagName, dirVec)", ReturnType = "void", Category = "Combat", IsSequelOnly = false, Description = "Fires weapon projectile from tag in specified direction." },
            new EngineBuiltin { Name = "Damage", Signature = "Damage(target, amount)", ReturnType = "void", Category = "Combat", IsSequelOnly = false, Description = "Deals direct damage to target entity." },
            new EngineBuiltin { Name = "RadialDamage", Signature = "RadialDamage(posVec, radius, damage)", ReturnType = "void", Category = "Combat", IsSequelOnly = false, Description = "Deals area-of-effect blast damage with distance falloff." },
            new EngineBuiltin { Name = "RadialDamagePlayer", Signature = "RadialDamagePlayer(posVec, radius, damage)", ReturnType = "void", Category = "Combat", IsSequelOnly = true, Description = "Deals radial shockwave damage tailored specifically to player." },
            new EngineBuiltin { Name = "PlaceLight", Signature = "PlaceLight(posVec, colorVec, radius)", ReturnType = "void", Category = "FX", IsSequelOnly = false, Description = "Spawns dynamic point light source in 3D world." },
            new EngineBuiltin { Name = "CameraQuake", Signature = "CameraQuake(intensity)", ReturnType = "void", Category = "FX", IsSequelOnly = false, Description = "Triggers screen-shake camera vibration impact effect." },
            new EngineBuiltin { Name = "LockTarget", Signature = "LockTarget()", ReturnType = "entity", Category = "Combat", IsSequelOnly = false, Description = "Acquires and locks targeting reticle onto nearest target." },
            new EngineBuiltin { Name = "IsValidTarget", Signature = "IsValidTarget(target)", ReturnType = "float", Category = "Combat", IsSequelOnly = false, Description = "Returns 1 if target is valid, alive, and in range; 0 otherwise." },
            new EngineBuiltin { Name = "TraceLine", Signature = "TraceLine(startVec, endVec)", ReturnType = "entity", Category = "Combat", IsSequelOnly = false, Description = "Raycasts line-of-sight and returns first hit entity." },
            new EngineBuiltin { Name = "TraceLineDamage", Signature = "TraceLineDamage(startVec, endVec, damage)", ReturnType = "void", Category = "Combat", IsSequelOnly = false, Description = "Instant laser/bullet trace dealing beam damage along line." },
            new EngineBuiltin { Name = "Lightning", Signature = "Lightning(startVec, endVec)", ReturnType = "void", Category = "FX", IsSequelOnly = false, Description = "Spawns electric lightning bolt effect between coordinates." },
            new EngineBuiltin { Name = "StartSound", Signature = "StartSound(soundName)", ReturnType = "void", Category = "Audio", IsSequelOnly = false, Description = "Plays one-shot sound effect at entity location." },
            new EngineBuiltin { Name = "StartLoopingSound", Signature = "StartLoopingSound(soundName)", ReturnType = "void", Category = "Audio", IsSequelOnly = false, Description = "Starts continuous looping audio sample on entity." },
            new EngineBuiltin { Name = "StopLoopingSound", Signature = "StopLoopingSound(soundName)", ReturnType = "void", Category = "Audio", IsSequelOnly = false, Description = "Stops active looping sound effect on entity." },
            new EngineBuiltin { Name = "RespawnPlayer", Signature = "RespawnPlayer()", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Respawns the player helicopter owning calling entity." },
            new EngineBuiltin { Name = "EndLevel", Signature = "EndLevel()", ReturnType = "void", Category = "Game", IsSequelOnly = false, Description = "Triggers mission victory sequence and transitions to next level." },
            new EngineBuiltin { Name = "GameOver", Signature = "GameOver()", ReturnType = "void", Category = "Game", IsSequelOnly = true, Description = "Triggers immediate mission failure / game over sequence." },
            new EngineBuiltin { Name = "PushPlayer", Signature = "PushPlayer(forceVec)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Applies impulse force vector pushing player helicopter." },
            new EngineBuiltin { Name = "G_AddPowerUp", Signature = "G_AddPowerUp(type, count)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Adds bonus power-up item charges to player inventory." },
            new EngineBuiltin { Name = "G_UsePowerUp", Signature = "G_UsePowerUp()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Consumes active player power-up and returns success." },
            new EngineBuiltin { Name = "G_GetPowerUp", Signature = "G_GetPowerUp()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Queries currently active power-up type." },
            new EngineBuiltin { Name = "G_SetPowerUpCount", Signature = "G_SetPowerUpCount(type, count)", ReturnType = "void", Category = "Player", IsSequelOnly = true, Description = "Sets inventory charge count for specified power-up." },
            new EngineBuiltin { Name = "G_AddMissiles", Signature = "G_AddMissiles(type, count)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Adds secondary missile ammunition to player inventory." },
            new EngineBuiltin { Name = "G_UseMissile", Signature = "G_UseMissile()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Fires currently selected secondary missile weapon." },
            new EngineBuiltin { Name = "G_GetMissiles", Signature = "G_GetMissiles()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Queries remaining missile ammunition count." },
            new EngineBuiltin { Name = "G_GetUpgrade", Signature = "G_GetUpgrade()", ReturnType = "float", Category = "Player", IsSequelOnly = false, Description = "Queries player primary weapon upgrade level (0..4)." },
            new EngineBuiltin { Name = "G_SetUpgrade", Signature = "G_SetUpgrade(type, level)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Sets weapon upgrade level directly." },
            new EngineBuiltin { Name = "ShowTutorialHint", Signature = "ShowTutorialHint(hintIdx)", ReturnType = "void", Category = "UI", IsSequelOnly = false, Description = "Displays radio transmission / tutorial banner hint message." },
            new EngineBuiltin { Name = "PlayerFreezeHealth", Signature = "PlayerFreezeHealth(freeze)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Toggles invulnerability / god mode on player helicopter." },
            new EngineBuiltin { Name = "PlayerDisableAction", Signature = "PlayerDisableAction(disable)", ReturnType = "void", Category = "Player", IsSequelOnly = false, Description = "Freezes player input controls (for cutscenes and scripts)." },
            new EngineBuiltin { Name = "FreezeHealth", Signature = "FreezeHealth(entity, freeze)", ReturnType = "void", Category = "Entity", IsSequelOnly = false, Description = "Sets target entity invulnerable to damage." },
            new EngineBuiltin { Name = "IsMultiplayer", Signature = "IsMultiplayer()", ReturnType = "float", Category = "Game", IsSequelOnly = true, Description = "Returns 1 if running in 2-player cooperative mode, 0 for single player." },
            new EngineBuiltin { Name = "IsPlayerInGame", Signature = "IsPlayerInGame(playerIndex)", ReturnType = "float", Category = "Player", IsSequelOnly = true, Description = "Returns 1 if player 1 or player 2 helicopter is currently active." },
            new EngineBuiltin { Name = "GetMapPosOfs", Signature = "GetMapPosOfs()", ReturnType = "float", Category = "Environment", IsSequelOnly = true, Description = "Returns current flight path scroll offset along mission track." },
            new EngineBuiltin { Name = "GetPlayerAccel", Signature = "GetPlayerAccel(axis)", ReturnType = "float", Category = "Player", IsSequelOnly = true, Description = "Queries acceleration vector component of player aircraft." },
            new EngineBuiltin { Name = "GetPlayersDistance", Signature = "GetPlayersDistance()", ReturnType = "float", Category = "Player", IsSequelOnly = true, Description = "Calculates Euclidean distance to player aircraft or between co-op players." },
        };

        public static readonly Dictionary<string, EngineBuiltin> ByName =
            All.ToDictionary(b => b.Name, b => b, StringComparer.OrdinalIgnoreCase);

        public static readonly HashSet<string> SequelOnlyNames =
            new HashSet<string>(All.Where(b => b.IsSequelOnly).Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

        public static readonly Dictionary<string, string> KnownApiDocs =
            All.ToDictionary(b => b.Name, b => b.Description, StringComparer.OrdinalIgnoreCase);

        public static EngineBuiltin Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            ByName.TryGetValue(name, out var b);
            return b;
        }

        public static IEnumerable<EngineBuiltin> Filter(string query, int engineFilter, bool sequelOnlyFilter = false)
        {
            var res = All.AsEnumerable();
            if (engineFilter == 1) // Original only
                res = res.Where(b => !b.IsSequelOnly);
            else if (engineFilter == 2 || sequelOnlyFilter) // Sequel only
                res = res.Where(b => b.IsSequelOnly);

            if (!string.IsNullOrWhiteSpace(query))
            {
                string q = query.Trim();
                res = res.Where(b =>
                    b.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    b.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    b.Category.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    b.Signature.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            return res;
        }
    }

    public sealed class ScriptStudioDialog : Form
    {
        private readonly Assets assets;
        private readonly GlTerrainControl preview;
        private readonly Action<Placement> applyPlacement;

        private string currentFullPath;
        private string currentRelPath;
        private string originalDiskAsm = "";
        private ScrFile currentScr;

        private readonly List<ScriptItem> allScripts = new List<ScriptItem>();

        // UI Controls
        private readonly TextBox searchBox = new TextBox();
        private readonly ComboBox filterCombo = new ComboBox();
        private readonly ListBox scriptList = new ListBox();

        private readonly Label scriptTitleLabel = new Label();
        private readonly Label scriptStatusLabel = new Label();
        private readonly Button assembleBtn = new Button();
        private readonly Button assignToUnitBtn = new Button();
        private readonly Button newCloneBtn = new Button();
        private readonly Button exportSourceBtn = new Button();
        private readonly Button exportAsmBtn = new Button();
        private readonly Button openExternalToolBtn = new Button();

        private readonly TabControl editorTabs = new TabControl();
        private readonly TextBox sourceBox;
        private readonly TextBox asmBox;
        private readonly TextBox diffBox;
        private readonly ListView funcListView = new ListView();
        private readonly TextBox infoBox;
        private readonly TextBox objDefBox;

        public static Dictionary<string, string> KnownApiDocs => EngineBuiltinCatalog.KnownApiDocs;

        private readonly ComboBox targetEngineCombo = new ComboBox();
        private readonly TextBox searchApiBox = new TextBox();
        private readonly ComboBox apiScopeCombo = new ComboBox();
        private readonly ComboBox apiEngineFilterCombo = new ComboBox();
        private readonly ListBox autoCompleteList = new ListBox();
        private readonly ToolTip autoCompleteTip = new ToolTip { InitialDelay = 100, ReshowDelay = 100, AutoPopDelay = 12000 };

        private TextUndoManager sourceUndo;
        private TextUndoManager asmUndo;

        public sealed class TextUndoManager
        {
            private sealed class Snapshot
            {
                public string Text;
                public int SelectionStart;
                public int SelectionLength;
            }

            private readonly Stack<Snapshot> undoStack = new Stack<Snapshot>();
            private readonly Stack<Snapshot> redoStack = new Stack<Snapshot>();
            private readonly TextBox box;
            private bool isUpdating;
            private DateTime lastChange = DateTime.MinValue;

            public TextUndoManager(TextBox target)
            {
                box = target;
                undoStack.Push(new Snapshot { Text = box.Text ?? "", SelectionStart = 0, SelectionLength = 0 });

                box.TextChanged += (s, e) =>
                {
                    if (isUpdating) return;
                    DateTime now = DateTime.Now;
                    if ((now - lastChange).TotalMilliseconds > 500)
                    {
                        PushSnapshot();
                    }
                    lastChange = now;
                    redoStack.Clear();
                };
            }

            public void PushSnapshot()
            {
                if (box == null) return;
                string currentText = box.Text ?? "";
                if (undoStack.Count > 0 && undoStack.Peek().Text == currentText) return;
                undoStack.Push(new Snapshot
                {
                    Text = currentText,
                    SelectionStart = box.SelectionStart,
                    SelectionLength = box.SelectionLength
                });
                if (undoStack.Count > 80)
                {
                    var arr = undoStack.ToArray();
                    undoStack.Clear();
                    for (int i = Math.Min(arr.Length - 1, 70); i >= 0; i--)
                        undoStack.Push(arr[i]);
                }
            }

            public void ResetHistory()
            {
                undoStack.Clear();
                redoStack.Clear();
                undoStack.Push(new Snapshot { Text = box.Text ?? "", SelectionStart = 0, SelectionLength = 0 });
            }

            public bool CanUndo => undoStack.Count > 1;
            public bool CanRedo => redoStack.Count > 0;

            public void Undo()
            {
                if (undoStack.Count <= 1) return;
                isUpdating = true;
                try
                {
                    redoStack.Push(new Snapshot
                    {
                        Text = box.Text ?? "",
                        SelectionStart = box.SelectionStart,
                        SelectionLength = box.SelectionLength
                    });
                    undoStack.Pop(); // current state
                    var prev = undoStack.Peek();
                    box.Text = prev.Text;
                    box.SelectionStart = Math.Max(0, Math.Min(box.TextLength, prev.SelectionStart));
                    box.SelectionLength = Math.Max(0, Math.Min(box.TextLength - box.SelectionStart, prev.SelectionLength));
                    box.ScrollToCaret();
                }
                finally
                {
                    isUpdating = false;
                }
            }

            public void Redo()
            {
                if (redoStack.Count == 0) return;
                isUpdating = true;
                try
                {
                    var next = redoStack.Pop();
                    undoStack.Push(next);
                    box.Text = next.Text;
                    box.SelectionStart = Math.Max(0, Math.Min(box.TextLength, next.SelectionStart));
                    box.SelectionLength = Math.Max(0, Math.Min(box.TextLength - box.SelectionStart, next.SelectionLength));
                    box.ScrollToCaret();
                }
                finally
                {
                    isUpdating = false;
                }
            }
        }

        public class ScriptItem
        {
            public string RelativePath;
            public string FullPath;
            public string FileName;
            public bool CallsWaypoints;
            public int InstructionCount;
            public long FileSize;
            public bool IsUsedOnMap;
            public string Note = "";

            public override string ToString()
            {
                string wp = CallsWaypoints ? "[WP] " : "     ";
                string mapTag = IsUsedOnMap ? " ★ " : " ";
                return wp + FileName + mapTag + "(" + InstructionCount + " ops)";
            }
        }

        public ScriptStudioDialog(Assets assets, GlTerrainControl preview = null, Action<Placement> applyPlacement = null, string initialScriptPath = null)
        {
            this.assets = assets;
            this.preview = preview;
            this.applyPlacement = applyPlacement;

            Text = "Script Studio — AirStrike 3D Script Editor & Decompiler (.scr)";
            Width = 1180;
            Height = 760;
            MinimumSize = new Size(880, 560);
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            var codeFont = new Font("Consolas", 10f);
            Disposed += (s, e) => codeFont.Dispose();

            TextBox MakeBox(bool readOnly) => new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = readOnly,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsTab = true,
                Font = codeFont,
                BackColor = readOnly ? Color.FromArgb(248, 250, 252) : Color.White,
            };

            sourceBox = MakeBox(false);
            asmBox = MakeBox(false);
            diffBox = MakeBox(true);
            infoBox = MakeBox(true);
            objDefBox = MakeBox(true);

            Action<TextBox> enableCtrlA = box =>
            {
                box.KeyDown += (s, e) =>
                {
                    if (e.Control && e.KeyCode == Keys.A)
                    {
                        box.SelectAll();
                        e.SuppressKeyPress = true;
                    }
                };
            };
            enableCtrlA(sourceBox);
            enableCtrlA(asmBox);
            enableCtrlA(diffBox);
            enableCtrlA(infoBox);
            enableCtrlA(objDefBox);

            // Pre-declare IDE actions for top toolbar and shortcuts
            Action<bool> showFindBar = null;
            Action<TextBox> goToLine = null;

            // ── Top Toolbar ───────────────────────────────────────────────────
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68,
                BackColor = Color.FromArgb(243, 244, 246),
                Padding = new Padding(10, 6, 10, 6),
            };

            scriptTitleLabel.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            scriptTitleLabel.ForeColor = Color.FromArgb(30, 41, 59);
            scriptTitleLabel.AutoSize = true;
            scriptTitleLabel.Location = new Point(10, 8);
            scriptTitleLabel.Text = "No script selected";

            scriptStatusLabel.Font = new Font("Segoe UI", 9f);
            scriptStatusLabel.ForeColor = Color.FromArgb(100, 116, 139);
            scriptStatusLabel.AutoSize = true;
            scriptStatusLabel.Location = new Point(12, 36);
            scriptStatusLabel.Text = "Select or open a script from the library.";

            var actionsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 56,
                WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 10, 0, 0),
            };

            assembleBtn.Text = "Compile && Save (F5)";
            assembleBtn.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            assembleBtn.AutoSize = true;
            assembleBtn.Height = 32;
            assembleBtn.BackColor = Color.FromArgb(220, 252, 231);
            assembleBtn.Click += (s, e) => AssembleAndSave();

            targetEngineCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            targetEngineCombo.Font = new Font("Segoe UI", 9f);
            targetEngineCombo.Width = 220;
            targetEngineCombo.Height = 32;
            targetEngineCombo.Items.Add("Target: Auto-Detect");
            targetEngineCombo.Items.Add("Target: AirStrike 3D [85 Builtins]");
            targetEngineCombo.Items.Add("Target: Sequel / OpenAirStrike [101]");
            targetEngineCombo.SelectedIndex = 0;
            targetEngineCombo.SelectedIndexChanged += (s, e) =>
            {
                CheckScriptCompatibility();
                PopulateApiList();
            };

            var moreToolsBtn = new Button
            {
                Text = "Tools ▾",
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Height = 32,
            };

            var moreToolsMenu = new ContextMenuStrip();
            var assignItem = moreToolsMenu.Items.Add("Assign to Selected Map Unit", null, (s, e) => AssignToCurrentMapUnit());
            assignItem.Enabled = preview != null && preview.SelectedIndex >= 0;
            moreToolsMenu.Items.Add("New / Clone Script…", null, (s, e) => ShowNewCloneDialog());
            moreToolsMenu.Items.Add(new ToolStripSeparator());
            moreToolsMenu.Items.Add("Find in Script… (Ctrl+F)", null, (s, e) => { if (showFindBar != null) showFindBar(false); });
            moreToolsMenu.Items.Add("Find && Replace… (Ctrl+H)", null, (s, e) => { if (showFindBar != null) showFindBar(true); });
            moreToolsMenu.Items.Add("Go to Line… (Ctrl+G)", null, (s, e) =>
            {
                TextBox active = editorTabs.SelectedIndex == 1 ? asmBox : sourceBox;
                if (goToLine != null) goToLine(active);
            });
            moreToolsMenu.Items.Add(new ToolStripSeparator());
            moreToolsMenu.Items.Add("Compile Source to ASM View", null, (s, e) => CompileSourceToAsmPreview());
            moreToolsMenu.Items.Add("Export Decompiled C (.c)…", null, (s, e) => ExportFile(false));
            moreToolsMenu.Items.Add("Export Assembly (.asm)…", null, (s, e) => ExportFile(true));
            moreToolsMenu.Items.Add(new ToolStripSeparator());
            moreToolsMenu.Items.Add("Open in External ScriptStudio.py…", null, (s, e) => LaunchExternalScriptStudio());

            moreToolsBtn.Click += (s, e) =>
            {
                assignItem.Enabled = preview != null && preview.SelectedIndex >= 0;
                moreToolsMenu.Show(moreToolsBtn, 0, moreToolsBtn.Height);
            };

            var sidebarToggleBtn = new Button
            {
                Text = "📁 Scripts (Ctrl+B)",
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Height = 32,
            };

            actionsFlow.Controls.AddRange(new Control[]
            {
                assembleBtn, targetEngineCombo, moreToolsBtn, sidebarToggleBtn
            });

            topPanel.Controls.Add(scriptTitleLabel);
            topPanel.Controls.Add(scriptStatusLabel);
            topPanel.Controls.Add(actionsFlow);
            Controls.Add(topPanel);

            // ── Main Split View ───────────────────────────────────────────────
            // FixedPanel.Panel1 ensures the script list sidebar remains at a modest
            // fixed width (~270px), allocating 75% to 85%+ of window space directly
            // to the code editor IDE.
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                SplitterWidth = 6,
            };

            sidebarToggleBtn.Click += (s, e) =>
            {
                split.Panel1Collapsed = !split.Panel1Collapsed;
                sidebarToggleBtn.Text = split.Panel1Collapsed ? "📁 Show Scripts (Ctrl+B)" : "📁 Scripts (Ctrl+B)";
            };

            // ── Left Sidebar (Script Library Explorer) ────────────────────────
            var sidebar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

            var sidebarHeaderPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 24,
            };
            var searchHeader = new Label
            {
                Text = "SCRIPT EXPLORER",
                AutoSize = true,
                Dock = DockStyle.Left,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(71, 85, 105),
                Padding = new Padding(0, 3, 0, 0),
            };
            var collapseBtn = new Button
            {
                Text = "«",
                Dock = DockStyle.Right,
                Width = 24,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            };
            collapseBtn.FlatAppearance.BorderSize = 0;
            var collapseTip = new ToolTip();
            collapseTip.SetToolTip(collapseBtn, "Collapse script explorer / maximize editor (Ctrl+B)");
            collapseBtn.Click += (s, e) =>
            {
                split.Panel1Collapsed = true;
                sidebarToggleBtn.Text = "📁 Show Scripts (Ctrl+B)";
            };
            sidebarHeaderPanel.Controls.Add(searchHeader);
            sidebarHeaderPanel.Controls.Add(collapseBtn);

            searchBox.Dock = DockStyle.Top;
            searchBox.Font = new Font("Segoe UI", 9.5f);
            searchBox.TextChanged += (s, e) => FilterScriptList();

            filterCombo.Dock = DockStyle.Top;
            filterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            filterCombo.Items.Add("All scripts");
            filterCombo.Items.Add("Waypoint / Route units only");
            filterCombo.Items.Add("Used on this map");
            filterCombo.SelectedIndex = 0;
            filterCombo.SelectedIndexChanged += (s, e) => FilterScriptList();

            scriptList.Dock = DockStyle.Fill;
            scriptList.Font = new Font("Consolas", 8.5f);
            scriptList.IntegralHeight = false;
            scriptList.HorizontalScrollbar = true;
            scriptList.SelectedIndexChanged += (s, e) =>
            {
                if (scriptList.SelectedItem is ScriptItem item)
                    LoadScript(item.FullPath, item.RelativePath);
            };

            var sidebarBottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                Padding = new Padding(0, 4, 0, 0),
            };
            var refreshBtn = new Button { Text = "Refresh", AutoSize = true };
            refreshBtn.Click += (s, e) => ScanScripts();
            var openAnyBtn = new Button { Text = "Open .scr…", AutoSize = true };
            openAnyBtn.Click += (s, e) => OpenExternalScrFile();
            sidebarBottom.Controls.AddRange(new Control[] { refreshBtn, openAnyBtn });

            sidebar.Controls.Add(scriptList);
            sidebar.Controls.Add(sidebarBottom);
            sidebar.Controls.Add(filterCombo);
            sidebar.Controls.Add(searchBox);
            sidebar.Controls.Add(sidebarHeaderPanel);

            sidebarHeaderPanel.SendToBack();
            searchBox.SendToBack();
            filterCombo.SendToBack();
            sidebarBottom.SendToBack();
            scriptList.BringToFront();

            split.Panel1.Controls.Add(sidebar);

            // ── Right Editor Area (Tabs + IDE Status Strip) ───────────────────
            var tabSource = new TabPage("Decompiled Source (C-like)");
            tabSource.Controls.Add(sourceBox);

            var tabAsm = new TabPage("Assembly (.asm — Editable)");
            tabAsm.Controls.Add(asmBox);

            var tabDiff = new TabPage("Assembly Diff (Disk vs Edit)");
            tabDiff.Controls.Add(diffBox);

            var tabFuncs = new TabPage("Engine API & Functions");

            var apiTopPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                Padding = new Padding(4),
                BackColor = Color.FromArgb(243, 244, 246),
            };

            searchApiBox.Dock = DockStyle.Left;
            searchApiBox.Width = 220;
            searchApiBox.Font = new Font("Segoe UI", 9f);
            searchApiBox.Text = "";
            var searchLbl = new Label { Text = "Search API:", AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 6, 4, 0), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };

            apiScopeCombo.Dock = DockStyle.Left;
            apiScopeCombo.Width = 240;
            apiScopeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            apiScopeCombo.Font = new Font("Segoe UI", 9f);
            apiScopeCombo.Items.Add("All 101 Engine Builtins (API Reference)");
            apiScopeCombo.Items.Add("Functions Used in Current Script");
            apiScopeCombo.SelectedIndex = 0;

            apiEngineFilterCombo.Dock = DockStyle.Left;
            apiEngineFilterCombo.Width = 220;
            apiEngineFilterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            apiEngineFilterCombo.Font = new Font("Segoe UI", 9f);
            apiEngineFilterCombo.Items.Add("All Engine Builtins (101)");
            apiEngineFilterCombo.Items.Add("Original AS3D Only (85)");
            apiEngineFilterCombo.Items.Add("Sequel Only (16 Builtins)");
            apiEngineFilterCombo.SelectedIndex = 0;

            var insertApiBtn = new Button
            {
                Dock = DockStyle.Right,
                Text = "Insert Function",
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };

            searchApiBox.TextChanged += (s, e) => PopulateApiList();
            apiScopeCombo.SelectedIndexChanged += (s, e) => PopulateApiList();
            apiEngineFilterCombo.SelectedIndexChanged += (s, e) => PopulateApiList();

            apiTopPanel.Controls.Add(insertApiBtn);
            apiTopPanel.Controls.Add(apiEngineFilterCombo);
            apiTopPanel.Controls.Add(apiScopeCombo);
            apiTopPanel.Controls.Add(searchApiBox);
            apiTopPanel.Controls.Add(searchLbl);

            funcListView.Dock = DockStyle.Fill;
            funcListView.View = View.Details;
            funcListView.FullRowSelect = true;
            funcListView.GridLines = true;
            funcListView.Columns.Add("Function Name", 160);
            funcListView.Columns.Add("Engine Compatibility", 140);
            funcListView.Columns.Add("Category", 90);
            funcListView.Columns.Add("Calls", 50);
            funcListView.Columns.Add("Signature", 230);
            funcListView.Columns.Add("Description", 400);

            tabFuncs.Controls.Add(funcListView);
            tabFuncs.Controls.Add(apiTopPanel);

            Action insertSelectedApi = () =>
            {
                if (funcListView.SelectedItems.Count == 0) return;
                string fnName = funcListView.SelectedItems[0].Text;
                var b = EngineBuiltinCatalog.Find(fnName);
                string snippet = b != null ? b.Name + "()" : fnName + "()";
                TextBox target = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (!target.ReadOnly)
                {
                    target.Paste(snippet);
                    target.Focus();
                }
            };
            funcListView.DoubleClick += (s, e) => insertSelectedApi();
            insertApiBtn.Click += (s, e) => insertSelectedApi();

            var tabInfo = new TabPage("String Table & RCSL Info");
            tabInfo.Controls.Add(infoBox);

            var tabObj = new TabPage("Object Definitions (.obj)");
            tabObj.Controls.Add(objDefBox);

            editorTabs.Dock = DockStyle.Fill;
            editorTabs.TabPages.AddRange(new[] { tabSource, tabAsm, tabDiff, tabFuncs, tabInfo, tabObj });

            var editorContainer = new Panel { Dock = DockStyle.Fill };

            autoCompleteList.Font = new Font("Consolas", 9f);
            autoCompleteList.Visible = false;
            autoCompleteList.Width = 420;
            autoCompleteList.Height = 170;
            autoCompleteList.IntegralHeight = false;
            autoCompleteList.BorderStyle = BorderStyle.FixedSingle;
            autoCompleteList.BackColor = Color.White;

            Action<TextBox, bool> triggerAutocomplete = (box, force) =>
            {
                int cursor = box.SelectionStart;
                if (cursor < 0 || cursor > box.TextLength) { autoCompleteList.Visible = false; return; }

                int start = cursor;
                while (start > 0 && (char.IsLetterOrDigit(box.Text[start - 1]) || box.Text[start - 1] == '_'))
                    start--;

                string prefix = box.Text.Substring(start, cursor - start);
                if (prefix.Length < 2 && !force)
                {
                    autoCompleteList.Visible = false;
                    return;
                }

                int engFilter = targetEngineCombo.SelectedIndex == 1 ? 1 : 0;
                var matches = EngineBuiltinCatalog.Filter(prefix, engFilter).ToList();
                if (matches.Count == 0 && prefix.Length >= 2)
                {
                    matches = EngineBuiltinCatalog.Filter(prefix, 0).ToList();
                }

                if (matches.Count == 0)
                {
                    autoCompleteList.Visible = false;
                    return;
                }

                autoCompleteList.BeginUpdate();
                autoCompleteList.Items.Clear();
                foreach (var m in matches)
                {
                    string tag = m.IsSequelOnly ? "[Sequel]" : "[AS3D]";
                    autoCompleteList.Items.Add(string.Format(CultureInfo.InvariantCulture, "{0,-24} {1,-9} {2}", m.Name, tag, m.Category));
                }
                autoCompleteList.EndUpdate();
                autoCompleteList.SelectedIndex = 0;

                Point pt = box.GetPositionFromCharIndex(start);
                int posX = Math.Max(10, Math.Min(box.Width - autoCompleteList.Width - 20, pt.X));
                int posY = Math.Max(10, Math.Min(box.Height - autoCompleteList.Height - 10, pt.Y + 24));
                autoCompleteList.Location = new Point(posX, posY);
                autoCompleteList.Visible = true;
                autoCompleteList.BringToFront();

                if (matches.Count > 0)
                {
                    autoCompleteTip.Show(matches[0].TooltipText, editorContainer, posX + autoCompleteList.Width + 5, posY, 5000);
                }
            };

            Action<TextBox> commitAutocomplete = box =>
            {
                if (!autoCompleteList.Visible || autoCompleteList.SelectedIndex < 0) return;
                string itemStr = autoCompleteList.SelectedItem.ToString();
                string fnName = itemStr.Split(' ')[0];
                var b = EngineBuiltinCatalog.Find(fnName);
                if (b == null) return;

                int cursor = box.SelectionStart;
                int start = cursor;
                while (start > 0 && (char.IsLetterOrDigit(box.Text[start - 1]) || box.Text[start - 1] == '_'))
                    start--;

                box.Select(start, cursor - start);
                box.SelectedText = b.Name + "()";
                box.SelectionStart = start + b.Name.Length + 1;
                box.SelectionLength = 0;

                autoCompleteList.Visible = false;
                Point pt = box.GetPositionFromCharIndex(box.SelectionStart);
                autoCompleteTip.Show(b.Signature + "\r\n" + b.EngineTarget + " | " + b.Description, box, pt.X, pt.Y - 45, 6000);
            };

            Action<TextBox> checkParamHint = box =>
            {
                int cursor = box.SelectionStart;
                if (cursor <= 0 || cursor > box.TextLength) return;
                if (box.Text[cursor - 1] == '(')
                {
                    int start = cursor - 1;
                    while (start > 0 && char.IsWhiteSpace(box.Text[start - 1])) start--;
                    int endName = start;
                    while (start > 0 && (char.IsLetterOrDigit(box.Text[start - 1]) || box.Text[start - 1] == '_'))
                        start--;
                    string fnName = box.Text.Substring(start, endName - start);
                    var b = EngineBuiltinCatalog.Find(fnName);
                    if (b != null)
                    {
                        Point pt = box.GetPositionFromCharIndex(cursor);
                        autoCompleteTip.Show("▶ " + b.Signature + "\r\n[" + b.EngineTarget + "] " + b.Description, box, pt.X, Math.Max(0, pt.Y - 45), 7000);
                    }
                }
            };

            Action<TextBox> setupIntellisense = box =>
            {
                box.KeyDown += (s, e) =>
                {
                    if (autoCompleteList.Visible)
                    {
                        if (e.KeyCode == Keys.Down)
                        {
                            if (autoCompleteList.SelectedIndex < autoCompleteList.Items.Count - 1)
                                autoCompleteList.SelectedIndex++;
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            return;
                        }
                        if (e.KeyCode == Keys.Up)
                        {
                            if (autoCompleteList.SelectedIndex > 0)
                                autoCompleteList.SelectedIndex--;
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            return;
                        }
                        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)
                        {
                            commitAutocomplete(box);
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            return;
                        }
                        if (e.KeyCode == Keys.Escape)
                        {
                            autoCompleteList.Visible = false;
                            autoCompleteTip.Hide(editorContainer);
                            e.Handled = true;
                            return;
                        }
                    }
                    else if (e.Control && e.KeyCode == Keys.Space)
                    {
                        triggerAutocomplete(box, true);
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                };

                box.KeyUp += (s, e) =>
                {
                    if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab || e.KeyCode == Keys.Escape)
                        return;

                    if (char.IsLetterOrDigit((char)e.KeyValue) || e.KeyCode == Keys.Back)
                    {
                        triggerAutocomplete(box, false);
                    }
                    else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
                    {
                        autoCompleteList.Visible = false;
                    }
                };

                box.KeyPress += (s, e) =>
                {
                    if (e.KeyChar == '(')
                    {
                        BeginInvoke((Action)(() => checkParamHint(box)));
                    }
                };

                box.MouseDown += (s, e) =>
                {
                    autoCompleteList.Visible = false;
                };
            };

            setupIntellisense(sourceBox);
            setupIntellisense(asmBox);

            autoCompleteList.DoubleClick += (s, e) =>
            {
                TextBox active = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                commitAutocomplete(active);
            };

            autoCompleteList.SelectedIndexChanged += (s, e) =>
            {
                if (autoCompleteList.SelectedIndex >= 0 && autoCompleteList.SelectedItem != null)
                {
                    string itemStr = autoCompleteList.SelectedItem.ToString();
                    string fnName = itemStr.Split(' ')[0];
                    var b = EngineBuiltinCatalog.Find(fnName);
                    if (b != null)
                    {
                        Point p = autoCompleteList.Location;
                        autoCompleteTip.Show(b.TooltipText, editorContainer, p.X + autoCompleteList.Width + 5, p.Y, 5000);
                    }
                }
            };

            editorContainer.Controls.Add(autoCompleteList);
            autoCompleteList.BringToFront();

            // ── Floating In-Editor Find & Replace Bar ───────────────────────────
            var findPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 38,
                BackColor = Color.FromArgb(238, 242, 246),
                Visible = false,
                Padding = new Padding(6, 4, 6, 4),
            };

            var lblFind = new Label { Text = "Find:", AutoSize = true, Location = new Point(8, 10), Font = new Font("Segoe UI", 9f) };
            var findInput = new TextBox { Width = 150, Location = new Point(46, 7), Font = new Font("Segoe UI", 9f) };
            var btnFindNext = new Button { Text = "Next (↓)", Width = 64, Height = 25, Location = new Point(202, 6), Font = new Font("Segoe UI", 8.5f) };
            var btnFindPrev = new Button { Text = "Prev (↑)", Width = 64, Height = 25, Location = new Point(270, 6), Font = new Font("Segoe UI", 8.5f) };
            var chkMatchCase = new CheckBox { Text = "Match Case", AutoSize = true, Location = new Point(340, 9), Font = new Font("Segoe UI", 8.5f) };

            var lblReplace = new Label { Text = "Replace:", AutoSize = true, Location = new Point(434, 10), Font = new Font("Segoe UI", 9f) };
            var replaceInput = new TextBox { Width = 130, Location = new Point(490, 7), Font = new Font("Segoe UI", 9f) };
            var btnReplace = new Button { Text = "Replace", Width = 62, Height = 25, Location = new Point(626, 6), Font = new Font("Segoe UI", 8.5f) };
            var btnReplaceAll = new Button { Text = "All", Width = 40, Height = 25, Location = new Point(692, 6), Font = new Font("Segoe UI", 8.5f) };
            var findStatusLabel = new Label { Text = "", AutoSize = true, Location = new Point(738, 10), Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(71, 85, 105) };

            var btnCloseFind = new Button
            {
                Text = "✕",
                Width = 26,
                Height = 25,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(findPanel.Width - 32, 6),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            findPanel.Controls.AddRange(new Control[]
            {
                lblFind, findInput, btnFindNext, btnFindPrev, chkMatchCase,
                lblReplace, replaceInput, btnReplace, btnReplaceAll,
                findStatusLabel, btnCloseFind
            });

            Action<bool> doFind = reverse =>
            {
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                string query = findInput.Text;
                if (string.IsNullOrEmpty(query)) return;

                StringComparison comp = chkMatchCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                string allText = activeBox.Text;
                if (string.IsNullOrEmpty(allText)) { findStatusLabel.Text = "No text"; return; }

                int startPos = activeBox.SelectionStart;
                int foundIdx = -1;

                if (!reverse)
                {
                    int searchFrom = startPos + activeBox.SelectionLength;
                    if (searchFrom < allText.Length)
                        foundIdx = allText.IndexOf(query, searchFrom, comp);
                    if (foundIdx < 0)
                        foundIdx = allText.IndexOf(query, 0, comp);
                }
                else
                {
                    int searchFrom = startPos - 1;
                    if (searchFrom >= 0)
                        foundIdx = allText.LastIndexOf(query, searchFrom, comp);
                    if (foundIdx < 0)
                        foundIdx = allText.LastIndexOf(query, allText.Length - 1, comp);
                }

                if (foundIdx >= 0)
                {
                    activeBox.Select(foundIdx, query.Length);
                    activeBox.ScrollToCaret();
                    activeBox.Focus();
                    findStatusLabel.Text = "Found";
                    findStatusLabel.ForeColor = Color.FromArgb(22, 101, 52);
                }
                else
                {
                    findStatusLabel.Text = "No match";
                    findStatusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                }
            };

            btnFindNext.Click += (s, e) => doFind(false);
            btnFindPrev.Click += (s, e) => doFind(true);

            btnReplace.Click += (s, e) =>
            {
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (activeBox.ReadOnly) return;
                string query = findInput.Text;
                if (string.IsNullOrEmpty(query)) return;
                StringComparison comp = chkMatchCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                if (activeBox.SelectionLength > 0 && activeBox.SelectedText.Equals(query, comp))
                {
                    activeBox.SelectedText = replaceInput.Text;
                }
                doFind(false);
            };

            btnReplaceAll.Click += (s, e) =>
            {
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (activeBox.ReadOnly) return;
                string query = findInput.Text;
                if (string.IsNullOrEmpty(query)) return;
                StringComparison comp = chkMatchCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                string text = activeBox.Text;
                int count = 0;
                int idx = 0;
                while ((idx = text.IndexOf(query, idx, comp)) >= 0)
                {
                    count++;
                    idx += query.Length;
                }
                if (count > 0)
                {
                    if (comp == StringComparison.Ordinal)
                        activeBox.Text = text.Replace(query, replaceInput.Text);
                    else
                        activeBox.Text = Regex.Replace(text, Regex.Escape(query), Regex.Replace(replaceInput.Text, @"([$\\])", "$$$1"), RegexOptions.IgnoreCase);
                    findStatusLabel.Text = string.Format("{0} replaced", count);
                    findStatusLabel.ForeColor = Color.FromArgb(22, 101, 52);
                }
                else
                {
                    findStatusLabel.Text = "0 replaced";
                    findStatusLabel.ForeColor = Color.FromArgb(100, 116, 139);
                }
            };

            btnCloseFind.Click += (s, e) =>
            {
                findPanel.Visible = false;
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                activeBox.Focus();
            };

            KeyEventHandler findKeyHandler = (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    doFind(e.Shift);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    findPanel.Visible = false;
                    TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                    activeBox.Focus();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            findInput.KeyDown += findKeyHandler;
            replaceInput.KeyDown += findKeyHandler;

            showFindBar = withReplace =>
            {
                findPanel.Visible = true;
                lblReplace.Visible = withReplace;
                replaceInput.Visible = withReplace;
                btnReplace.Visible = withReplace;
                btnReplaceAll.Visible = withReplace;

                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (activeBox.SelectionLength > 0 && !activeBox.SelectedText.Contains("\n"))
                {
                    findInput.Text = activeBox.SelectedText;
                }
                findInput.Focus();
                findInput.SelectAll();
            };

            goToLine = box =>
            {
                using (var dlg = new Form
                {
                    Text = "Go to Line",
                    Width = 270,
                    Height = 140,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    MaximizeBox = false,
                    MinimizeBox = false,
                })
                {
                    int totalLines = box.Lines.Length;
                    int currentLine = box.GetLineFromCharIndex(box.SelectionStart) + 1;
                    var lbl = new Label { Text = string.Format("Line number (1 - {0}):", totalLines), Location = new Point(15, 15), AutoSize = true };
                    var num = new NumericUpDown { Minimum = 1, Maximum = Math.Max(1, totalLines), Value = currentLine, Location = new Point(18, 40), Width = 130 };
                    var btnGo = new Button { Text = "Go", Location = new Point(160, 38), Width = 75, Height = 26, DialogResult = DialogResult.OK };
                    dlg.Controls.AddRange(new Control[] { lbl, num, btnGo });
                    dlg.AcceptButton = btnGo;
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        int targetLine = (int)num.Value - 1;
                        int charIdx = box.GetFirstCharIndexFromLine(targetLine);
                        if (charIdx >= 0)
                        {
                            box.SelectionStart = charIdx;
                            box.SelectionLength = 0;
                            box.ScrollToCaret();
                            box.Focus();
                        }
                    }
                }
            };

            Action<TextBox> toggleComment = box =>
            {
                int selStart = box.SelectionStart;
                int selLen = box.SelectionLength;
                int startLine = box.GetLineFromCharIndex(selStart);
                int endLine = box.GetLineFromCharIndex(selStart + Math.Max(0, selLen - 1));

                var lines = box.Lines;
                if (lines == null || lines.Length == 0) return;

                bool allCommented = true;
                for (int i = startLine; i <= endLine && i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (trimmed.Length > 0 && !trimmed.StartsWith("//"))
                    {
                        allCommented = false;
                        break;
                    }
                }

                for (int i = startLine; i <= endLine && i < lines.Length; i++)
                {
                    if (allCommented)
                    {
                        int idx = lines[i].IndexOf("//");
                        if (idx >= 0)
                        {
                            int removeLen = (idx + 2 < lines[i].Length && lines[i][idx + 2] == ' ') ? 3 : 2;
                            lines[i] = lines[i].Remove(idx, removeLen);
                        }
                    }
                    else
                    {
                        int indent = 0;
                        while (indent < lines[i].Length && (lines[i][indent] == ' ' || lines[i][indent] == '\t')) indent++;
                        lines[i] = lines[i].Insert(indent, "// ");
                    }
                }

                box.Lines = lines;
                int newCharIndex = box.GetFirstCharIndexFromLine(startLine);
                int endCharIndex = endLine < lines.Length - 1 ? box.GetFirstCharIndexFromLine(endLine + 1) : box.TextLength;
                box.SelectionStart = newCharIndex >= 0 ? newCharIndex : selStart;
                box.SelectionLength = Math.Max(0, endCharIndex - box.SelectionStart);
            };

            Action<TextBox, bool> handleIndent = (box, outdent) =>
            {
                int selStart = box.SelectionStart;
                int selLen = box.SelectionLength;
                int startLine = box.GetLineFromCharIndex(selStart);
                int endLine = box.GetLineFromCharIndex(selStart + Math.Max(0, selLen - 1));

                if (startLine == endLine && selLen == 0 && !outdent)
                {
                    box.SelectedText = "    ";
                    return;
                }

                var lines = box.Lines;
                if (lines == null || lines.Length == 0) return;

                for (int i = startLine; i <= endLine && i < lines.Length; i++)
                {
                    if (outdent)
                    {
                        if (lines[i].StartsWith("    ")) lines[i] = lines[i].Substring(4);
                        else if (lines[i].StartsWith("\t")) lines[i] = lines[i].Substring(1);
                        else lines[i] = lines[i].TrimStart(' ');
                    }
                    else
                    {
                        lines[i] = "    " + lines[i];
                    }
                }
                box.Lines = lines;
                int newStart = box.GetFirstCharIndexFromLine(startLine);
                int newEnd = endLine < lines.Length - 1 ? box.GetFirstCharIndexFromLine(endLine + 1) : box.TextLength;
                box.SelectionStart = newStart >= 0 ? newStart : selStart;
                box.SelectionLength = Math.Max(0, newEnd - box.SelectionStart);
            };

            Action<TextBox> duplicateLine = box =>
            {
                int lineIndex = box.GetLineFromCharIndex(box.SelectionStart);
                var lines = box.Lines;
                if (lines == null || lineIndex < 0 || lineIndex >= lines.Length) return;
                string lineText = lines[lineIndex];
                int charIndex = box.GetFirstCharIndexFromLine(lineIndex);
                box.Select(charIndex + lineText.Length, 0);
                box.SelectedText = "\r\n" + lineText;
            };

            Action<TextBox, bool> moveLine = (box, moveUp) =>
            {
                int lineIndex = box.GetLineFromCharIndex(box.SelectionStart);
                var lines = box.Lines;
                if (lines == null || lineIndex < 0 || lineIndex >= lines.Length) return;
                if (moveUp && lineIndex > 0)
                {
                    string temp = lines[lineIndex];
                    lines[lineIndex] = lines[lineIndex - 1];
                    lines[lineIndex - 1] = temp;
                    box.Lines = lines;
                    int newChar = box.GetFirstCharIndexFromLine(lineIndex - 1);
                    if (newChar >= 0) box.SelectionStart = newChar;
                }
                else if (!moveUp && lineIndex < lines.Length - 1)
                {
                    string temp = lines[lineIndex];
                    lines[lineIndex] = lines[lineIndex + 1];
                    lines[lineIndex + 1] = temp;
                    box.Lines = lines;
                    int newChar = box.GetFirstCharIndexFromLine(lineIndex + 1);
                    if (newChar >= 0) box.SelectionStart = newChar;
                }
            };

            sourceUndo = new TextUndoManager(sourceBox);
            asmUndo = new TextUndoManager(asmBox);

            Action<TextBox, TextUndoManager> setupEditorShortcuts = (box, undoMgr) =>
            {
                box.KeyDown += (s, e) =>
                {
                    if (e.Control && e.KeyCode == Keys.Z)
                    {
                        if (e.Shift) undoMgr.Redo();
                        else undoMgr.Undo();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.Y)
                    {
                        undoMgr.Redo();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && (e.KeyCode == Keys.OemQuestion || e.KeyCode == Keys.Divide))
                    {
                        undoMgr.PushSnapshot();
                        toggleComment(box);
                        undoMgr.PushSnapshot();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.KeyCode == Keys.Tab)
                    {
                        undoMgr.PushSnapshot();
                        handleIndent(box, e.Shift);
                        undoMgr.PushSnapshot();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.D)
                    {
                        undoMgr.PushSnapshot();
                        duplicateLine(box);
                        undoMgr.PushSnapshot();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Alt && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
                    {
                        undoMgr.PushSnapshot();
                        moveLine(box, e.KeyCode == Keys.Up);
                        undoMgr.PushSnapshot();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.F)
                    {
                        showFindBar(false);
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.H)
                    {
                        showFindBar(true);
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.G)
                    {
                        goToLine(box);
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                };
            };

            setupEditorShortcuts(sourceBox, sourceUndo);
            setupEditorShortcuts(asmBox, asmUndo);

            var editorFooter = new StatusStrip
            {
                Dock = DockStyle.Bottom,
                BackColor = Color.FromArgb(241, 245, 249),
                SizingGrip = false,
            };
            var statusCursor = new ToolStripStatusLabel { Text = "Ln 1, Col 1", AutoSize = false, Width = 110 };
            var statusDocInfo = new ToolStripStatusLabel { Text = "0 lines • 0 chars", AutoSize = false, Width = 150 };
            var statusMode = new ToolStripStatusLabel { Text = "C-Source Mode", AutoSize = false, Width = 120 };
            var statusHelp = new ToolStripStatusLabel
            {
                Text = "F5: Compile & Save | Ctrl+F: Find | Ctrl+/: Comment | Tab: Indent | Ctrl+Z/Y: Undo/Redo | Ctrl+G: Line | Ctrl+B: Sidebar",
                Spring = true,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(100, 116, 139)
            };

            editorFooter.Items.AddRange(new ToolStripItem[] { statusCursor, statusDocInfo, statusMode, statusHelp });

            Action updateCursorInfo = () =>
            {
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (editorTabs.SelectedTab == tabDiff) activeBox = diffBox;
                else if (editorTabs.SelectedTab == tabInfo) activeBox = infoBox;
                else if (editorTabs.SelectedTab == tabObj) activeBox = objDefBox;

                int index = activeBox.SelectionStart;
                int line = activeBox.GetLineFromCharIndex(index);
                int firstChar = activeBox.GetFirstCharIndexFromLine(line);
                int col = Math.Max(0, index - firstChar);
                statusCursor.Text = string.Format(CultureInfo.InvariantCulture, "Ln {0}, Col {1}", line + 1, col + 1);
                statusDocInfo.Text = string.Format(CultureInfo.InvariantCulture, "{0} lines • {1:N0} chars", activeBox.Lines.Length, activeBox.TextLength);

                if (editorTabs.SelectedTab == tabSource)
                    statusMode.Text = "C-Source Mode";
                else if (editorTabs.SelectedTab == tabAsm)
                    statusMode.Text = "ASM Mode";
                else
                    statusMode.Text = "View Mode";
            };

            sourceBox.KeyUp += (s, e) => updateCursorInfo();
            sourceBox.MouseUp += (s, e) => updateCursorInfo();
            sourceBox.TextChanged += (s, e) => updateCursorInfo();
            asmBox.KeyUp += (s, e) => updateCursorInfo();
            asmBox.MouseUp += (s, e) => updateCursorInfo();
            asmBox.TextChanged += (s, e) => updateCursorInfo();

            editorTabs.SelectedIndexChanged += (s, e) =>
            {
                updateCursorInfo();
                if (editorTabs.SelectedTab == tabDiff)
                    UpdateDiff();
                else if (editorTabs.SelectedTab == tabSource)
                {
                    assembleBtn.Text = "Compile && Save (F5)";
                    assembleBtn.BackColor = Color.FromArgb(220, 252, 231);
                }
                else if (editorTabs.SelectedTab == tabAsm)
                {
                    assembleBtn.Text = "Assemble && Save (F5)";
                    assembleBtn.BackColor = Color.FromArgb(224, 231, 255);
                }
            };

            editorContainer.Controls.Add(findPanel);
            editorContainer.Controls.Add(editorTabs);
            editorContainer.Controls.Add(editorFooter);
            editorFooter.SendToBack();
            editorTabs.BringToFront();
            findPanel.BringToFront();

            split.Panel2.Controls.Add(editorContainer);
            Controls.Add(split);
            split.BringToFront();

            // Set and preserve sidebar width to ~270px so the editor occupies the majority of the IDE
            Action applyLayout = () =>
            {
                try
                {
                    if (split.Width > 450)
                    {
                        split.Panel1MinSize = 160;
                        split.Panel2MinSize = 250;
                        int maxDistance = split.Width - split.SplitterWidth - split.Panel2MinSize;
                        int desired = Math.Min(300, Math.Max(180, (int)(split.Width * 0.23f)));
                        if (desired >= split.Panel1MinSize && desired <= maxDistance)
                            split.SplitterDistance = desired;
                    }
                }
                catch { }
            };
            Load += (s, e) => applyLayout();
            Shown += (s, e) =>
            {
                applyLayout();
                updateCursorInfo();
            };

            // ── Shortcuts ─────────────────────────────────────────────────────
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.B)
                {
                    split.Panel1Collapsed = !split.Panel1Collapsed;
                    sidebarToggleBtn.Text = split.Panel1Collapsed ? "📁 Show Scripts (Ctrl+B)" : "📁 Scripts (Ctrl+B)";
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F5 && assembleBtn.Enabled)
                {
                    AssembleAndSave();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.S && assembleBtn.Enabled)
                {
                    AssembleAndSave();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.O)
                {
                    OpenExternalScrFile();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.F)
                {
                    showFindBar(false);
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.H)
                {
                    showFindBar(true);
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.G)
                {
                    TextBox active = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                    goToLine(active);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape && findPanel.Visible)
                {
                    findPanel.Visible = false;
                    TextBox active = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                    active.Focus();
                    e.Handled = true;
                }
            };

            // Initial scan & selection
            ScanScripts(initialScriptPath);
        }

        private void ScanScripts(string preselectPath = null)
        {
            allScripts.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Determine scripts used on current map
            var mapUsedScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (preview != null && preview.CurrentMap != null)
            {
                foreach (var obj in preview.CurrentMap.Objects)
                {
                    if (!string.IsNullOrEmpty(obj.Script))
                        mapUsedScripts.Add(obj.Script);
                    if (preview.PlacedObjectNames() != null && obj.Type < preview.CurrentMap.Types.Count)
                    {
                        string name = preview.CurrentMap.Types[obj.Type];
                        if (assets != null && assets.Definitions.TryGetValue(name, out var d) && !string.IsNullOrEmpty(d.Script))
                            mapUsedScripts.Add(d.Script);
                    }
                }
            }

            string root = assets?.Root ?? "";
            if (Directory.Exists(root))
            {
                foreach (string full in Directory.GetFiles(root, "*.scr", SearchOption.AllDirectories))
                {
                    if (full.EndsWith(".scr.bak", StringComparison.OrdinalIgnoreCase)) continue;

                    string rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        ? full.Substring(root.Length).TrimStart('\\', '/')
                        : full;

                    if (!seen.Add(rel)) continue;

                    var item = new ScriptItem
                    {
                        RelativePath = rel,
                        FullPath = full,
                        FileName = Path.GetFileName(full),
                        FileSize = new FileInfo(full).Length,
                        IsUsedOnMap = mapUsedScripts.Contains(rel),
                    };

                    try
                    {
                        var scr = ScrFile.Parse(File.ReadAllBytes(full));
                        item.CallsWaypoints = scr.CallsWaypointFunctions;
                        item.InstructionCount = scr.Code.Count;
                    }
                    catch
                    {
                        item.InstructionCount = 0;
                    }

                    allScripts.Add(item);
                }
            }

            // Sort: map used first, then alphabetical
            allScripts.Sort((a, b) =>
            {
                if (a.IsUsedOnMap != b.IsUsedOnMap) return b.IsUsedOnMap.CompareTo(a.IsUsedOnMap);
                return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });

            FilterScriptList();

            // Handle preselection
            if (!string.IsNullOrEmpty(preselectPath))
            {
                for (int i = 0; i < scriptList.Items.Count; i++)
                {
                    if (scriptList.Items[i] is ScriptItem it &&
                        (it.RelativePath.Equals(preselectPath, StringComparison.OrdinalIgnoreCase)
                         || it.FileName.Equals(Path.GetFileName(preselectPath), StringComparison.OrdinalIgnoreCase)))
                    {
                        scriptList.SelectedIndex = i;
                        return;
                    }
                }
            }

            if (preview != null && preview.SelectedIndex >= 0)
            {
                var draft = preview.SelectedDraft();
                string candidate = !string.IsNullOrEmpty(draft?.Script) ? draft.Script : null;
                if (candidate == null && assets != null)
                {
                    string tName = preview.PlacedObjectNames()[preview.SelectedIndex];
                    if (assets.Definitions.TryGetValue(tName, out var def))
                        candidate = def.Script;
                }

                if (!string.IsNullOrEmpty(candidate))
                {
                    for (int i = 0; i < scriptList.Items.Count; i++)
                    {
                        if (scriptList.Items[i] is ScriptItem it &&
                            (it.RelativePath.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                             || it.FileName.Equals(Path.GetFileName(candidate), StringComparison.OrdinalIgnoreCase)))
                        {
                            scriptList.SelectedIndex = i;
                            return;
                        }
                    }
                }
            }

            if (scriptList.Items.Count > 0)
                scriptList.SelectedIndex = 0;
        }

        private void FilterScriptList()
        {
            scriptList.BeginUpdate();
            scriptList.Items.Clear();

            string query = searchBox.Text.Trim();
            int filter = filterCombo.SelectedIndex;

            foreach (var item in allScripts)
            {
                if (filter == 1 && !item.CallsWaypoints) continue;
                if (filter == 2 && !item.IsUsedOnMap) continue;

                if (query.Length > 0 &&
                    item.FileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    item.RelativePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                scriptList.Items.Add(item);
            }

            scriptList.EndUpdate();
        }

        private void LoadScript(string fullPath, string relPath)
        {
            currentFullPath = fullPath;
            currentRelPath = relPath;

            scriptTitleLabel.Text = relPath;

            if (!File.Exists(fullPath))
            {
                scriptStatusLabel.Text = "File not found: " + fullPath;
                sourceBox.Text = "// File missing on disk:\r\n// " + fullPath;
                asmBox.Text = "";
                diffBox.Text = "No file on disk.";
                infoBox.Text = "Missing file.";
                objDefBox.Text = "";
                assembleBtn.Enabled = false;
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                currentScr = ScrFile.Parse(bytes);

                originalDiskAsm = currentScr.Disassemble().Replace("\r\n", "\n").Replace("\n", "\r\n");
                asmBox.Text = originalDiskAsm;
                sourceBox.Text = currentScr.DecompileToSource();
                infoBox.Text = currentScr.BuildInfoSummary(fullPath);

                sourceUndo?.ResetHistory();
                asmUndo?.ResetHistory();

                // Populate Engine API tab & check compatibility
                PopulateApiList();
                CheckScriptCompatibility();

                // Populate Object definitions tab
                var defsSummary = new StringBuilder();
                if (assets != null)
                {
                    var matching = assets.Definitions.Values.Where(d =>
                        !string.IsNullOrEmpty(d.Script) &&
                        (d.Script.Equals(relPath, StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileName(d.Script).Equals(Path.GetFileName(relPath), StringComparison.OrdinalIgnoreCase))).ToList();

                    if (matching.Count > 0)
                    {
                        defsSummary.AppendLine("// Objects using this script as their default in data/objects:");
                        foreach (var m in matching)
                        {
                            defsSummary.AppendLine("// ── " + m.Name + " (" + Path.GetFileName(m.Source) + ") ──");
                            defsSummary.AppendLine(m.Name + " {");
                            if (!string.IsNullOrEmpty(m.Model))  defsSummary.AppendLine("  model  \"" + m.Model + "\"");
                            if (!string.IsNullOrEmpty(m.Skin))   defsSummary.AppendLine("  skin   \"" + m.Skin + "\"");
                            defsSummary.AppendLine("  script \"" + m.Script + "\"");
                            defsSummary.AppendLine("}");
                            defsSummary.AppendLine();
                        }
                    }
                    else
                    {
                        defsSummary.AppendLine("// No loaded .obj definitions set this script as their default.");
                        defsSummary.AppendLine("// It may be assigned via map overrides or triggered by other scripts.");
                    }
                }
                objDefBox.Text = defsSummary.ToString();

                string wpTag = currentScr.CallsWaypointFunctions ? " • [Waypoints: YES]" : "";
                scriptStatusLabel.Text = string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} bytes • {1} instructions • {2} slots • Active: {3}{4}",
                    bytes.Length, currentScr.Code.Count, currentScr.Slots, currentScr.ActiveEntryRoles(), wpTag);

                assembleBtn.Enabled = true;
                UpdateDiff();
            }
            catch (Exception ex)
            {
                scriptStatusLabel.Text = "Parse error: " + ex.Message;
                sourceBox.Text = "// Failed to parse RCSL: " + ex.Message;
                asmBox.Text = "";
                diffBox.Text = ex.ToString();
                infoBox.Text = ex.ToString();
                assembleBtn.Enabled = false;
            }
        }

        private void CompileSourceToAsmPreview()
        {
            if (string.IsNullOrEmpty(sourceBox.Text)) return;
            try
            {
                var built = ScrFile.CompileSource(sourceBox.Text);
                asmBox.Text = built.Disassemble().Replace("\r\n", "\n").Replace("\n", "\r\n");
                infoBox.Text = built.BuildInfoSummary(currentFullPath);
                UpdateDiff();
                editorTabs.SelectedIndex = 1; // switch to tabAsm
                scriptStatusLabel.Text = "Compiled C source -> " + built.Code.Count + " instructions (preview in ASM tab).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "C-Source Compilation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AssembleAndSave()
        {
            if (string.IsNullOrEmpty(currentFullPath) || !assembleBtn.Enabled) return;

            try
            {
                bool fromSource = (editorTabs.SelectedIndex == 0); // tabSource is index 0
                ScrFile built;
                if (fromSource)
                {
                    built = ScrFile.CompileSource(sourceBox.Text);
                    asmBox.Text = built.Disassemble().Replace("\r\n", "\n").Replace("\n", "\r\n");
                }
                else
                {
                    built = ScrFile.Assemble(asmBox.Text);
                    sourceBox.Text = built.DecompileToSource();
                }

                byte[] bytes = built.Build();

                string bak = currentFullPath + ".bak";
                if (File.Exists(currentFullPath) && !File.Exists(bak))
                    File.Copy(currentFullPath, bak);

                File.WriteAllBytes(currentFullPath, bytes);

                originalDiskAsm = asmBox.Text;
                infoBox.Text = built.BuildInfoSummary(currentFullPath);
                scriptStatusLabel.Text = (fromSource ? "Compiled C-source" : "Assembled") + " & saved " + bytes.Length + " bytes to " + Path.GetFileName(currentFullPath) + " (backup at .bak)";
                UpdateDiff();

                MessageBox.Show(this,
                    string.Format("Successfully {0} and saved:\r\n{1}\r\n({2} bytes, {3} instructions)",
                        fromSource ? "compiled from C-like source" : "assembled",
                        currentFullPath,
                        bytes.Length,
                        built.Code.Count),
                    "Script Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, (editorTabs.SelectedIndex == 0) ? "C-Source Compilation Error" : "Assembly / Build Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateDiff()
        {
            diffBox.Text = ComputeDiff(originalDiskAsm, asmBox.Text);
        }

        private static string ComputeDiff(string orig, string edit)
        {
            if (string.IsNullOrEmpty(orig) && string.IsNullOrEmpty(edit))
                return "No content to compare.";
            if (orig == edit)
                return "Disk script and editor content are identical (Exact match — 0 changes).";

            var oLines = (orig ?? "").Replace("\r\n", "\n").Split('\n');
            var eLines = (edit ?? "").Replace("\r\n", "\n").Split('\n');

            var sb = new StringBuilder();
            sb.AppendLine("── Assembly Diff: Disk Original vs Current Editor ──");
            sb.AppendLine("  ( - ) Removed from disk version");
            sb.AppendLine("  ( + ) Added / changed in editor");
            sb.AppendLine();

            int i = 0, j = 0;
            int count = 0;
            while (i < oLines.Length || j < eLines.Length)
            {
                if (i < oLines.Length && j < eLines.Length && oLines[i] == eLines[j])
                {
                    sb.AppendLine("    " + oLines[i]);
                    i++; j++;
                }
                else
                {
                    count++;
                    int matchJ = -1;
                    for (int k = j; k < Math.Min(j + 10, eLines.Length); k++)
                    {
                        if (i < oLines.Length && oLines[i] == eLines[k]) { matchJ = k; break; }
                    }
                    if (matchJ >= 0)
                    {
                        while (j < matchJ) { sb.AppendLine(" +  " + eLines[j]); j++; }
                    }
                    else
                    {
                        if (i < oLines.Length) { sb.AppendLine(" -  " + oLines[i]); i++; }
                        if (j < eLines.Length) { sb.AppendLine(" +  " + eLines[j]); j++; }
                    }
                }
            }

            if (count == 0) return "Disk script and editor content are identical (Exact match — 0 changes).";
            return sb.ToString();
        }

        private void AssignToCurrentMapUnit()
        {
            if (preview == null || preview.SelectedIndex < 0 || applyPlacement == null || string.IsNullOrEmpty(currentRelPath))
            {
                MessageBox.Show(this, "Select an object on the map first to assign this script.", "Assign Script");
                return;
            }

            var draft = preview.SelectedDraft();
            if (draft == null) return;

            string objName = preview.PlacedObjectNames()[preview.SelectedIndex];
            draft.Script = currentRelPath;
            applyPlacement(draft);

            MessageBox.Show(this,
                string.Format("Successfully assigned map override script:\r\n'{0}'\r\nto Object #{1} ({2}).",
                    currentRelPath, preview.SelectedIndex, objName),
                "Script Assigned", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowNewCloneDialog()
        {
            using (var form = new Form
            {
                Text = "New Script / Clone Script",
                Width = 460,
                Height = 240,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
                layout.Controls.Add(new Label { Text = "Script template:", AutoSize = true });

                var templateCombo = new ComboBox { Width = 410, DropDownStyle = ComboBoxStyle.DropDownList };
                templateCombo.Items.Add("Clone currently open script (" + (string.IsNullOrEmpty(currentRelPath) ? "none" : Path.GetFileName(currentRelPath)) + ")");
                templateCombo.Items.Add("Waypoint Patrol Enemy (MoveToNextWP template)");
                templateCombo.Items.Add("Turret / Auto-targeting AI (LockTarget template)");
                templateCombo.Items.Add("Empty / Minimal RCSL template");
                templateCombo.SelectedIndex = 0;
                layout.Controls.Add(templateCombo);

                layout.Controls.Add(new Label { Text = "Relative file path (e.g. scripts\\custom_bot.scr):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
                var pathBox = new TextBox
                {
                    Width = 410,
                    Text = "scripts\\custom_" + (string.IsNullOrEmpty(currentRelPath) ? "unit.scr" : Path.GetFileName(currentRelPath)),
                };
                layout.Controls.Add(pathBox);

                var btnPanel = new FlowLayoutPanel { Width = 410, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
                var cancel = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
                var create = new Button { Text = "Create", Width = 80 };
                btnPanel.Controls.Add(cancel);
                btnPanel.Controls.Add(create);
                layout.Controls.Add(btnPanel);

                form.AcceptButton = create;
                form.CancelButton = cancel;

                create.Click += (s, e) =>
                {
                    string targetRel = pathBox.Text.Trim();
                    if (string.IsNullOrEmpty(targetRel))
                    {
                        MessageBox.Show(form, "Please enter a valid relative path.");
                        return;
                    }
                    if (!targetRel.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
                        targetRel += ".scr";

                    string root = assets?.Root ?? "";
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    {
                        MessageBox.Show(form, "Load a game data folder first.");
                        return;
                    }

                    string full = Path.Combine(root, targetRel);
                    if (File.Exists(full))
                    {
                        if (MessageBox.Show(form, "File already exists. Overwrite?", "File exists", MessageBoxButtons.YesNo) != DialogResult.Yes)
                            return;
                    }

                    string templateAsm = "";
                    int choice = templateCombo.SelectedIndex;
                    if (choice == 0 && !string.IsNullOrEmpty(asmBox.Text))
                        templateAsm = asmBox.Text;
                    else if (choice == 1)
                        templateAsm = ".code\r\n.slots 16\r\n.entry @0 L_init\r\n.entry @1 L_main\r\n.func MoveToNextWP\r\n.func GetWaypointDelay\r\n.var float 2 1.5f\r\n\r\nL_init:\r\n    RETURN\r\n\r\nL_main:\r\n    PUSHF v2\r\n    CALL MoveToNextWP\r\n    RETURN\r\n    END\r\n";
                    else if (choice == 2)
                        templateAsm = ".code\r\n.slots 16\r\n.entry @0 L_init\r\n.entry @1 L_main\r\n.func LockTarget\r\n.func IsValidTarget\r\n.func G_UseMissile\r\n\r\nL_init:\r\n    RETURN\r\n\r\nL_main:\r\n    CALL LockTarget\r\n    CALL IsValidTarget\r\n    JZ L_wait\r\n    CALL G_UseMissile\r\nL_wait:\r\n    RETURN\r\n    END\r\n";
                    else
                        templateAsm = ".code\r\n.slots 16\r\n.entry @0 L_init\r\n.entry @1 L_main\r\n\r\nL_init:\r\n    RETURN\r\n\r\nL_main:\r\n    RETURN\r\n    END\r\n";

                    try
                    {
                        var scr = ScrFile.Assemble(templateAsm);
                        string dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(full, scr.Build());

                        form.Close();
                        ScanScripts(targetRel);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(form, ex.Message, "Error creating script");
                    }
                };

                form.ShowDialog(this);
            }
        }

        private void ExportFile(bool isAsm)
        {
            if (string.IsNullOrEmpty(currentFullPath)) return;

            using (var sfd = new SaveFileDialog
            {
                Title = isAsm ? "Export Script Assembly" : "Export Decompiled C-like Source",
                Filter = isAsm ? "Assembly Files (*.asm)|*.asm|Text Files (*.txt)|*.txt|All Files (*.*)|*.*"
                               : "C Source Files (*.c)|*.c|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                FileName = Path.ChangeExtension(Path.GetFileName(currentFullPath), isAsm ? ".asm" : ".c"),
            })
            {
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllText(sfd.FileName, isAsm ? asmBox.Text : sourceBox.Text);
                        MessageBox.Show(this, "Exported successfully to:\r\n" + sfd.FileName, "Exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OpenExternalScrFile()
        {
            using (var ofd = new OpenFileDialog
            {
                Title = "Open AirStrike 3D Compiled Script (.scr)",
                Filter = "Compiled Scripts (*.scr)|*.scr|All Files (*.*)|*.*",
            })
            {
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    string root = assets?.Root ?? "";
                    string rel = (!string.IsNullOrEmpty(root) && ofd.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        ? ofd.FileName.Substring(root.Length).TrimStart('\\', '/')
                        : ofd.FileName;

                    LoadScript(ofd.FileName, rel);
                }
            }
        }

        private void PopulateApiList()
        {
            funcListView.BeginUpdate();
            funcListView.Items.Clear();

            var funcCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (currentScr != null)
            {
                foreach (var instr in currentScr.Code)
                {
                    int op = instr.Item1 & 0xFF;
                    if (op == 0x1C || op == 0x1D) // CALL or CALLP
                    {
                        int fIdx = instr.Item2;
                        if (fIdx >= 0 && fIdx < currentScr.Funcs.Count)
                        {
                            string fName = currentScr.Funcs[fIdx];
                            funcCalls[fName] = funcCalls.ContainsKey(fName) ? funcCalls[fName] + 1 : 1;
                        }
                    }
                }
            }

            string search = searchApiBox != null ? searchApiBox.Text.Trim() : "";
            int scope = apiScopeCombo != null ? apiScopeCombo.SelectedIndex : 0;
            int engine = apiEngineFilterCombo != null ? apiEngineFilterCombo.SelectedIndex : 0;

            bool isOriginalTarget = targetEngineCombo != null && (targetEngineCombo.SelectedIndex == 1 ||
                (targetEngineCombo.SelectedIndex == 0 && IsOriginalGameFolder(assets?.Root)));

            var itemsToShow = new List<Tuple<string, EngineBuiltin, int>>();

            if (scope == 1) // Used in script only
            {
                if (currentScr != null)
                {
                    foreach (var fn in currentScr.Funcs)
                    {
                        var b = EngineBuiltinCatalog.Find(fn);
                        if (engine == 1 && b != null && b.IsSequelOnly) continue;
                        if (engine == 2 && (b == null || !b.IsSequelOnly)) continue;
                        if (!string.IsNullOrEmpty(search))
                        {
                            if (fn.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                                (b == null || (b.Description.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                                               b.Category.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)))
                                continue;
                        }
                        int calls = funcCalls.ContainsKey(fn) ? funcCalls[fn] : 0;
                        itemsToShow.Add(Tuple.Create(fn, b, calls));
                    }
                }
            }
            else // All 101 builtins
            {
                var builtins = EngineBuiltinCatalog.Filter(search, engine);
                foreach (var b in builtins)
                {
                    int calls = funcCalls.ContainsKey(b.Name) ? funcCalls[b.Name] : 0;
                    itemsToShow.Add(Tuple.Create(b.Name, b, calls));
                }
            }

            foreach (var item in itemsToShow)
            {
                string fn = item.Item1;
                var b = item.Item2;
                int calls = item.Item3;

                var row = new ListViewItem(fn);
                string compat = b != null ? (b.IsSequelOnly ? "Sequel [AS2/Gulf]" : "Original [AS3D]") : "User Callback";
                string category = b != null ? b.Category : "Custom";
                string sig = b != null ? b.Signature : fn + "(...)";
                string desc = b != null ? b.Description : "User-defined or callback function";

                row.SubItems.Add(compat);
                row.SubItems.Add(category);
                row.SubItems.Add(calls.ToString());
                row.SubItems.Add(sig);
                row.SubItems.Add(desc);

                if (b != null && b.IsSequelOnly)
                {
                    if (isOriginalTarget && calls > 0)
                    {
                        row.ForeColor = Color.DarkRed;
                        row.BackColor = Color.FromArgb(254, 242, 242);
                    }
                    else
                    {
                        row.ForeColor = Color.FromArgb(120, 20, 140);
                    }
                }
                else
                {
                    row.ForeColor = Color.FromArgb(20, 80, 40);
                }

                funcListView.Items.Add(row);
            }

            funcListView.EndUpdate();
        }

        private void CheckScriptCompatibility()
        {
            if (currentScr == null) return;
            var sequelCalls = new List<string>();
            foreach (var fn in currentScr.Funcs)
            {
                if (EngineBuiltinCatalog.SequelOnlyNames.Contains(fn))
                    sequelCalls.Add(fn);
            }

            bool isOriginalTarget = targetEngineCombo != null && (targetEngineCombo.SelectedIndex == 1 ||
                (targetEngineCombo.SelectedIndex == 0 && IsOriginalGameFolder(assets?.Root)));

            if (sequelCalls.Count > 0)
            {
                string callList = string.Join(", ", sequelCalls.Distinct());
                if (isOriginalTarget)
                {
                    scriptStatusLabel.Text = "⚠ Compatibility Warning: Script calls Sequel-only builtins (" + callList + "). Will fail on original AirStrike3D.exe!";
                    scriptStatusLabel.ForeColor = Color.DarkRed;
                }
                else
                {
                    scriptStatusLabel.Text = "ℹ Sequel builtins active: " + callList + " (Supported in AS2, Gulf Thunder & OpenAirStrike).";
                    scriptStatusLabel.ForeColor = Color.FromArgb(112, 26, 117);
                }
            }
            else
            {
                scriptStatusLabel.Text = "✔ 100% Compatible with all engines (Original AirStrike 3D, AS2, Gulf & OpenAirStrike).";
                scriptStatusLabel.ForeColor = Color.FromArgb(22, 101, 52);
            }
        }

        public static bool IsOriginalGameFolder(string dataFolder)
        {
            if (string.IsNullOrEmpty(dataFolder)) return true;
            string lower = dataFolder.ToLowerInvariant();
            if (lower.Contains("gulf") || lower.Contains("airstrike2") || lower.Contains("airstrike 2") || lower.Contains("as3d2") || lower.Contains("airstrike3dii"))
                return false;
            try
            {
                var parent = Directory.GetParent(dataFolder);
                if (parent != null && (File.Exists(Path.Combine(parent.FullName, "as3d2.exe")) || File.Exists(Path.Combine(parent.FullName, "AirStrike3D II - Gulf.exe"))))
                    return false;
            }
            catch { }
            return true;
        }

        private void LaunchExternalScriptStudio()
        {
            if (string.IsNullOrEmpty(currentFullPath)) return;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exeCandidate = Path.Combine(baseDir, "ScriptStudio", "dist", "ScriptStudio.exe");
            string exeCandidate2 = Path.Combine(baseDir, "ScriptStudio.exe");
            string pyCandidate = Path.Combine(baseDir, "ScriptStudio", "scrstudio.py");

            try
            {
                if (File.Exists(exeCandidate))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exeCandidate, Arguments = "\"" + currentFullPath + "\"", UseShellExecute = true });
                }
                else if (File.Exists(exeCandidate2))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exeCandidate2, Arguments = "\"" + currentFullPath + "\"", UseShellExecute = true });
                }
                else if (File.Exists(pyCandidate))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "python", Arguments = "\"" + pyCandidate + "\" \"" + currentFullPath + "\"", WorkingDirectory = Path.GetDirectoryName(pyCandidate), UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show(this, "External Python ScriptStudio launcher was not found. You can edit, decompile, and assemble scripts right here inside this window!", "Script Studio");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not launch external tool: " + ex.Message + "\r\nAll editing and assembling works natively in this window.", "Script Studio");
            }
        }
    }
}
