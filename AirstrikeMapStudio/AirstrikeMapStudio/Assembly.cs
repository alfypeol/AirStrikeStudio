using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    /// <summary>
    /// One <c>attach</c> directive parsed from an .obj definition.
    /// </summary>
    public class Attachment
    {
        public string ObjectName;
        public string Tag;
        public string Id = "";
        public bool Absolute;
        public bool Night;

        /// <summary>
        /// Parses all <c>attach</c> lines in <paramref name="body"/>.
        /// Unrecognized lines are added to <paramref name="warnings"/>.
        /// </summary>
        /// <remarks>
        /// Accepted forms:
        ///   attach "object" "tag"
        ///   attach abs "object" "tag"
        ///   attach night "object" "tag"
        ///   attach id "name" "object" "tag"
        ///   attach abs night id "name" "object" "tag"
        /// Order of modifiers is not significant.
        ///
        /// The <c>abs</c> modifier means "don't inherit the parent's rotation."
        /// The <c>night</c> modifier means "only render on night levels."
        /// The <c>id</c> modifier names the attachment so scripts can
        /// AttachActivate/AttachDeactivate it.
        /// </remarks>
        public static List<Attachment> Parse(string body, List<string> warnings)
        {
            var result = new List<Attachment>();

            foreach (string line in body.Split('\n'))
            {
                var tokens = Regex
                    .Matches(line, "\"([^\"]*)\"|([^\\s]+)")
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                    .ToArray();

                if (tokens.Length == 0 || tokens[0] != "attach") continue;

                var a = new Attachment();
                int i = 1;

                while (i < tokens.Length)
                {
                    if (tokens[i] == "abs") { a.Absolute = true; i++; }
                    else if (tokens[i] == "night") { a.Night = true; i++; }
                    else if (tokens[i] == "id" && i + 1 < tokens.Length)
                    {
                        a.Id = tokens[i + 1];
                        i += 2;
                    }
                    else break;
                }

                if (tokens.Length - i != 2)
                {
                    warnings.Add("Unrecognized attachment: " + line.Trim());
                    continue;
                }

                a.ObjectName = tokens[i];
                a.Tag = tokens[i + 1];
                result.Add(a);
            }

            return result;
        }
    }

    /// <summary>
    /// One mesh part of a loaded model: geometry, vertex/triangle counts, and
    /// the tag table (name → position). Tag warnings are surfaced to the
    /// model inspector.
    /// </summary>
    public class MeshPart
    {
        public Model3D Model;
        public int Vertices, Triangles;
        public Dictionary<string, Vector3D> Tags = new Dictionary<string, Vector3D>(StringComparer.Ordinal);
        public string TagWarning = "";

        /// <summary>
        /// Parses the tag table from an MDL v2 file.
        /// </summary>
        /// <remarks>
        /// Header layout (all little-endian 32-bit, offsets from start):
        ///   +0  : magic 0x214C444D ("MDL!")
        ///   +4  : version (2)
        ///   +76 : vertex count
        ///   +80 : UV count
        ///   +84 : face count
        ///   +88 : normal count
        ///   +92 : tag count
        /// Tag data begins at:
        ///   120 + 12*nv + 8*nu + 12*nf + 12*nn
        /// Each tag is 56 bytes: 32-byte name, 3 floats XYZ at +32/+36/+40,
        /// rotation at +44..+56 (ignored — identity is assumed).
        ///
        /// On any validation failure, sets <see cref="TagWarning"/> and
        /// returns with an empty tag table. Never throws.
        /// </remarks>
        public void ReadTags(byte[] b)
        {
            int nv = BitConverter.ToInt32(b, 76);
            int nu = BitConverter.ToInt32(b, 80);
            int nf = BitConverter.ToInt32(b, 84);
            int nn = BitConverter.ToInt32(b, 88);
            int nt = BitConverter.ToInt32(b, 92);

            long start = 120L + 12L * nv + 8L * nu + 12L * nf + 12L * nn;

            if (nn < 0 || nt < 0 || nt > 4096 || start < 120 || start + 56L * nt != b.Length)
            {
                TagWarning = "Attachment table layout is unsupported; base mesh only.";
                return;
            }

            var parsed = new Dictionary<string, Vector3D>(StringComparer.Ordinal);

            for (int i = 0; i < nt; i++)
            {
                int p = (int)start + i * 56;

                int n = 0;
                while (n < 32 && b[p + n] != 0) n++;

                string name = Encoding.ASCII.GetString(b, p, n);

                float x = BitConverter.ToSingle(b, p + 32);
                float y = BitConverter.ToSingle(b, p + 36);
                float z = BitConverter.ToSingle(b, p + 40);

                if (n == 0 || n == 32
                    || name.Any(c => c < 32 || c > 126)
                    || parsed.ContainsKey(name)
                    || !Finite(x) || !Finite(y) || !Finite(z))
                {
                    TagWarning = "Invalid attachment table; base mesh only.";
                    return;
                }

                parsed.Add(name, new Vector3D(x, y, z));
            }

            Tags = parsed;
        }

        private static bool Finite(float x) =>
            !float.IsNaN(x) && !float.IsInfinity(x) && Math.Abs(x) < 1e8;
    }

    /// <summary>
    /// The loaded assembly of a model: base mesh parts plus attached parts.
    /// Both groups are frozen so WPF can reuse them across frames.
    /// </summary>
    public class AssemblyScene
    {
        public Model3DGroup Base  = new Model3DGroup();
        public Model3DGroup Parts = new Model3DGroup();
        public List<string> Report = new List<string>();
        public int PartCount, Vertices, Triangles;
        public Dictionary<string, Vector3D> Positions = new Dictionary<string, Vector3D>();

        public static AssemblyScene Load(Assets assets, string name)
        {
            var builder = new AssemblyBuilder(assets);
            builder.Add(
                name,
                new Vector3D(),
                name,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                root: true);

            builder.Scene.Base.Freeze();
            builder.Scene.Parts.Freeze();
            return builder.Scene;
        }
    }

    /// <summary>
    /// Recursively loads a model's attachment tree.
    /// </summary>
    internal class AssemblyBuilder
    {
        private readonly Assets assets;
        private readonly Dictionary<string, MeshPart> cache =
            new Dictionary<string, MeshPart>(StringComparer.OrdinalIgnoreCase);

        public AssemblyScene Scene = new AssemblyScene();

        private int visited;

        public AssemblyBuilder(Assets a) { assets = a; }

        /// <summary>
        /// Adds <paramref name="name"/> and its attachments at
        /// <paramref name="position"/>.
        /// </summary>
        /// <remarks>
        /// <paramref name="ancestors"/> is the cycle-detection set. Names are
        /// added before recursing and removed after, so a shared sub-assembly
        /// (e.g., the same rocket launcher attached to two different
        /// helicopters) is loaded twice, once per parent. Do not replace with
        /// a visited-set; that would silently drop one copy.
        ///
        /// Caps:
        ///   ancestors.Count >= 16  → skip (cycle or absurdly deep nesting)
        ///   visited > 256          → skip (total part budget exhausted)
        /// Both are hard limits, not tunables. Raising them risks hangs and
        /// memory exhaustion on pathological model files.
        /// </remarks>
        public void Add(
            string name,
            Vector3D position,
            string path,
            HashSet<string> ancestors,
            bool root)
        {
            if (ancestors.Contains(name))
            {
                Scene.Report.Add("Skipped " + path + ": attachment cycle.");
                return;
            }

            if (ancestors.Count >= 16 || ++visited > 256)
            {
                Scene.Report.Add("Skipped " + path + ": assembly size limit.");
                return;
            }

            if (!assets.Definitions.TryGetValue(name, out var def))
            {
                if (root) throw new InvalidDataException("Object definition not found: " + name);
                Scene.Report.Add("Skipped " + path + ": missing definition.");
                return;
            }

            if (def.Hidden || def.Additive || def.Model.Length == 0 || def.Skin.Length == 0)
            {
                if (root) throw new InvalidDataException("This object has no supported visible base model.");
                Scene.Report.Add("Skipped " + path + ": invisible object, light, sprite or effect.");
                return;
            }

            MeshPart part;
            try
            {
                string key = def.Model + "|" + def.Skin;
                if (!cache.TryGetValue(key, out part))
                {
                    part = ModelViewer.ReadMesh(assets.Resolve(def.Model), assets.Resolve(def.Skin));
                    cache.Add(key, part);
                }
            }
            catch (Exception ex)
            {
                if (root) throw;
                Scene.Report.Add("Skipped " + path + ": " + ex.Message);
                return;
            }

            var placed = new Model3DGroup { Transform = new TranslateTransform3D(position) };
            placed.Children.Add(part.Model);
            (root ? Scene.Base : Scene.Parts).Children.Add(placed);

            Scene.PartCount++;
            Scene.Vertices  += part.Vertices;
            Scene.Triangles += part.Triangles;
            Scene.Positions[path] = position;
            Scene.Report.Add("Loaded " + path + " at " + position.ToString(CultureInfo.InvariantCulture));

            foreach (string warning in def.AttachmentWarnings)
                Scene.Report.Add(path + ": " + warning);
            if (part.TagWarning.Length > 0)
                Scene.Report.Add(path + ": " + part.TagWarning);

            ancestors.Add(name);

            for (int i = 0; i < def.Attachments.Count; i++)
            {
                var a = def.Attachments[i];
                string child = path + " / " + a.ObjectName + " [" + i + "]";

                if (a.Night)
                {
                    Scene.Report.Add("Skipped " + child + ": night-only attachment.");
                    continue;
                }

                Vector3D local = new Vector3D();
                if (a.Tag != "origin" && !part.Tags.TryGetValue(a.Tag, out local))
                {
                    Scene.Report.Add("Skipped " + child + ": missing or unsupported tag " + a.Tag + ".");
                    continue;
                }

                // With identity V2 tag bases and no script transforms, `abs`
                // and ordinary attachments both reduce to parent + tag.
                Add(a.ObjectName, position + local, child, ancestors, root: false);
            }

            // Required: without this, every subsequent add of the same name
            // is treated as a cycle, and the whole tree collapses.
            ancestors.Remove(name);
        }
    }
}