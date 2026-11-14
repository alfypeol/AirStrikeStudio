using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Media3D;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Assembly flattening (hierarchical models → flat mesh list)
        // ═════════════════════════════════════════════════════════════════
        // Only a few object families have nested attachments that are visible
        // in-game (tanks with turrets, helicopters with guns, planes with
        // missiles). For those, we load the assembly tree and flatten it into
        // a list of meshes in world space. Everything else is a single mesh.
        //
        // Flattening bakes transforms into vertex coordinates, so each part
        // becomes an independent GeometryModel3D. This matches the GL render
        // path in GlTerrainWindow, which iterates scene.Children and skips
        // anything that is a Model3DGroup.
        // ═════════════════════════════════════════════════════════════════

        private readonly Dictionary<string, List<GeometryModel3D>> assemblyCache =
            new Dictionary<string, List<GeometryModel3D>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> assemblyNotes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the flattened parts of an object's model, or the fallback
        /// single-element list. Caches results per definition name.
        /// </summary>
        /// <remarks>
        /// Only these families get flattened (per <c>d.Source</c>'s filename):
        ///   tanks.obj, rocketlauncher.obj, ships.obj, helics.obj, planes.obj
        /// Everything else returns <c>[fallback]</c> unchanged.
        /// </remarks>
        private List<GeometryModel3D> ObjectParts(
            Assets assets, string name,
            GeometryModel3D fallback,
            ref string note)
        {
            if (fallback == markerModel
                || !assets.Definitions.TryGetValue(name, out var def)
                || def.Attachments.Count == 0)
                return new List<GeometryModel3D> { fallback };

            string file = Path.GetFileName(def.Source);
            if (LibraryFiles.Contains(file))
                return new List<GeometryModel3D> { fallback };

            if (assemblyCache.TryGetValue(name, out var cached))
            {
                note = assemblyNotes[name];
                return cached;
            }

            try
            {
                var assembly = AssemblyScene.Load(assets, name);
                var parts = new List<GeometryModel3D>();
                FlattenParts(assembly.Base,  Matrix3D.Identity, parts);
                FlattenParts(assembly.Parts, Matrix3D.Identity, parts);

                int skipped = 0;
                foreach (string entry in assembly.Report)
                    if (entry.StartsWith("Skipped ")) skipped++;

                note = "Static assembly: " + parts.Count + " mesh parts; " +
                       skipped + " skipped effects/unsupported parts. " +
                       "Script aiming and animation not simulated.";

                assemblyCache[name] = parts;
                assemblyNotes[name] = note;
                return parts;
            }
            catch (Exception ex)
            {
                note = "Base model only: attachment loading failed — " + ex.Message;
                return new List<GeometryModel3D> { fallback };
            }
        }

        /// <summary>
        /// Recursively walks a WPF model group, applying accumulated transforms
        /// to each leaf mesh, and appends each resulting GeometryModel3D to
        /// <paramref name="output"/>.
        /// </summary>
        private static void FlattenParts(
            Model3D node,
            Matrix3D parentTransform,
            List<GeometryModel3D> output)
        {
            var transform = node.Transform.Value;
            transform.Append(parentTransform);

            if (node is Model3DGroup group)
            {
                foreach (var child in group.Children)
                    FlattenParts(child, transform, output);
                return;
            }

            if (node is not GeometryModel3D geometry) return;

            // Clone the mesh so baking the transform doesn't corrupt the
            // shared source mesh (cached by AssemblyScene).
            var mesh = (MeshGeometry3D)geometry.Geometry.Clone();
            for (int i = 0; i < mesh.Positions.Count; i++)
                mesh.Positions[i] = transform.Transform(mesh.Positions[i]);

            var part = new GeometryModel3D(mesh, geometry.Material);
            part.Freeze();
            output.Add(part);
        }
    }
}