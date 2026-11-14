using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Color = System.Windows.Media.Color;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        // ═════════════════════════════════════════════════════════════════
        // Scenery (placed object) loading
        // ═════════════════════════════════════════════════════════════════
        // For each object on the map, load its definition, resolve its model,
        // flatten its assembly, apply per-object Z rules, and add each part to
        // the scene as a separate GeometryModel3D.
        //
        // Two height caches are built first:
        //   routeHeights — per-grid-cell terrain Z (sampled from any mesh with
        //                  >4 vertices that isn't water). Used by route
        //                  rendering, sculpt cursor, and tile cursor.
        //   objectWaterZ — single water surface Z (first water mesh found).
        //                  Used by FL_ONWATER objects (ships, cutters).
        //
        // Aircraft starting heights are handled by AircraftRule (GlAircraft.cs):
        // if the effective script hashes to a known "flying" or "landing" list,
        // the object is placed at world Z 100 or terrain + 7.
        // ═════════════════════════════════════════════════════════════════

        private readonly HashSet<string> waterObjects =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private double? objectWaterZ;

        /// <summary>
        /// World Z for an object at the given cell. Rules (in order):
        ///   1. Aircraft rule (flying = 100, landing = terrain + 7)
        ///   2. FL_ONWATER object on a map with water → water surface Z
        ///   3. Otherwise → terrain Z at (x, y)
        /// </summary>
        private double ObjectZ(Placement p, int x, int y)
        {
            if (aircraftRules.TryGetValue(HeightKey(p), out int rule))
            {
                if (rule == AircraftHeightFlying)  return 100;
                if (rule == AircraftHeightLanding) return TerrainZ(x, y) + 7;
            }

            if (objectWaterZ.HasValue && waterObjects.Contains(map.Types[p.Type]))
                return objectWaterZ.Value;

            return TerrainZ(x, y);
        }

        public int SceneryCount { get; private set; }
        public int SceneryModels { get; private set; }
        public int MarkerCount   { get; private set; }

        public readonly List<string> SceneryReport = new List<string>();

        private readonly Dictionary<string, GeometryModel3D> sceneryCache =
            new Dictionary<string, GeometryModel3D>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<int, string> objectVisualNotes = new Dictionary<int, string>();

        private GeometryModel3D markerModel;

        /// <summary>
        /// Returns the shared "missing/unsupported" marker mesh: an orange
        /// arrow/cone that always points up, frozen and reusable.
        /// </summary>
        private GeometryModel3D Marker()
        {
            if (markerModel != null) return markerModel;

            var mesh = new MeshGeometry3D();
            foreach (var p in new[]
            {
                new Point3D(0, 0, 22),
                new Point3D(-9, 0, 10),
                new Point3D(0, 9, 10),
                new Point3D(9, 0, 10),
                new Point3D(0, -9, 10),
                new Point3D(0, 0, 1),
            })
            {
                mesh.Positions.Add(p);
                mesh.TextureCoordinates.Add(new System.Windows.Point(0.5, 0.5));
            }
            foreach (int i in new[]
            {
                0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1,
                5, 2, 1, 5, 3, 2, 5, 4, 3, 5, 1, 4,
            })
                mesh.TriangleIndices.Add(i);

            var bitmap = BitmapSource.Create(
                1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 40, 160, 255, 255 }, 4);
            bitmap.Freeze();

            markerModel = new GeometryModel3D(
                mesh,
                new DiffuseMaterial(new ImageBrush(bitmap)));
            markerModel.Freeze();
            return markerModel;
        }

        /// <summary>
        /// Loads the model for <paramref name="name"/> (or a marker if not
        /// available). Never throws; instead sets <paramref name="note"/> to
        /// a human-readable explanation.
        /// </summary>
        private GeometryModel3D ObjectModel(Assets assets, string name, out string note)
        {
            note = "Base model; attachments, animation and runtime height not simulated.";

            if (!assets.Definitions.TryGetValue(name, out var def))
            {
                note = "Marker: definition missing from loaded data.";
                return Marker();
            }

            if (def.Hidden || def.Additive || def.Model.Length == 0)
            {
                note = "Marker: invisible, sprite, light or effect definition.";
                return Marker();
            }

            try
            {
                string skin = SkinPath(assets, def);
                string key  = def.Model + "|" + skin;

                if (!sceneryCache.TryGetValue(key, out var model))
                {
                    model = (GeometryModel3D)ModelViewer.ReadMesh(
                        assets.Resolve(def.Model),
                        assets.Resolve(skin)).Model;
                    sceneryCache.Add(key, model);
                }

                return model;
            }
            catch (Exception ex)
            {
                note = "Marker: model unavailable — " + ex.Message;
                return Marker();
            }
        }

        /// <summary>
        /// The visual note for the currently selected object, if any.
        /// </summary>
        public string SelectedVisualNote =>
            objectVisualNotes.TryGetValue(SelectedIndex, out var note) ? note : "";

        private Assets cachedAssets;

        private Assets GetAssets()
        {
            if (cachedAssets == null)
            {
                cachedAssets = new Assets();
                cachedAssets.Open(assetRoot);
            }
            return cachedAssets;
        }

        /// <summary>
        /// Rebuilds the whole scenery layer from the map: height caches,
        /// water Z, aircraft rules, and one scene insertion per part.
        /// </summary>
        private void LoadScenery(string dataFolder, bool rebuildTerrainCache = true)
        {
            if (rebuildTerrainCache || routeHeights == null)
            {
                // ── 1. Build the terrain height cache ────────────────────────
                var heights = new Dictionary<string, double>();
                foreach (var child in scene.Children)
                {
                    if (child is not GeometryModel3D g) continue;
                    if (sceneryIds.ContainsKey(g)) continue;

                    if (!(g.Geometry is MeshGeometry3D mesh)) continue;
                    if (!(g.Material is DiffuseMaterial mat)) continue;
                    if (TerrainViewer.IsWater(mat)) continue;
                    if (mesh.Positions.Count <= 4) continue;

                    foreach (var p in mesh.Positions)
                    {
                        string key = ((int)Math.Round(p.X / 40)) + "," + ((int)Math.Round(p.Y / 40));
                        if (!heights.ContainsKey(key))
                            heights[key] = p.Z;
                    }
                }

                // ── 2. Find the water surface Z (first water mesh) ───────────
                objectWaterZ = null;
                foreach (var child in scene.Children)
                {
                    if (child is not GeometryModel3D g) continue;
                    if (sceneryIds.ContainsKey(g)) continue;

                    var mat = g.Material as DiffuseMaterial;
                    var mesh = g.Geometry as MeshGeometry3D;
                    if (mat == null || mesh == null || mesh.Positions.Count == 0) continue;
                    if (!TerrainViewer.IsWater(mat)) continue;

                    objectWaterZ = mesh.Positions[0].Z;
                    break;
                }

                routeHeights = heights;
            }

            // ── 3. Reset caches ──────────────────────────────────────────
            aircraftRules.Clear();
            waterObjects.Clear();
            MarkerCount = 0;
            objectVisualNotes.Clear();

            // ── 4. Load definitions and place each object ────────────────
            var assets = GetAssets();

            foreach (var definition in assets.Definitions.Values)
                if (definition.OnWater)
                    waterObjects.Add(definition.Name);

            // Find the first water mesh index once so we don't scan scene.Children O(N^2) times.
            int insertAt = scene.Children.Count;
            for (int i = 0; i < scene.Children.Count; i++)
            {
                if (scene.Children[i] is GeometryModel3D g
                    && TerrainViewer.IsWater(g.Material as DiffuseMaterial))
                {
                    insertAt = i;
                    break;
                }
            }

            for (int index = 0; index < map.Objects.Count; index++)
            {
                var placement = map.Objects[index];
                string name = map.Types[placement.Type];

                // Model
                var source = ObjectModel(assets, name, out string note);
                if (source == markerModel)
                {
                    MarkerCount++;
                    SceneryReport.Add(name + ": " + note);
                }

                // Z
                double z = TerrainZ(placement.X, placement.Y);
                var parts = ObjectParts(assets, name, source, ref note);

                if (waterObjects.Contains(name))
                {
                    if (objectWaterZ.HasValue)
                    {
                        z = objectWaterZ.Value;
                        note += " Water-level preview (FL_ONWATER); " +
                                "bobbing/script offsets not simulated.";
                    }
                    else
                    {
                        note += " FL_ONWATER: no water level available; " +
                                "terrain fallback.";
                    }
                }

                int heightRule = AircraftRule(assets, placement, name, ref note);
                aircraftRules[HeightKey(placement)] = heightRule;

                if (heightRule == AircraftHeightFlying)
                    z = 100;
                else if (heightRule == AircraftHeightLanding)
                    z += 7;

                objectVisualNotes[index] = note;

                // ── Flatten each part into world space ──────────────
                foreach (var part in parts)
                {
                    var mesh = (MeshGeometry3D)part.Geometry.Clone();
                    double angle = placement.Setting6 * Math.PI / 6;
                    double c = Math.Cos(angle);
                    double s = Math.Sin(angle);

                    for (int i = 0; i < mesh.Positions.Count; i++)
                    {
                        var p = mesh.Positions[i];
                        mesh.Positions[i] = new Point3D(
                            c * p.X - s * p.Y + MapWorld.SpawnX(placement.X),
                            s * p.X + c * p.Y + MapWorld.SpawnY(placement.Y),
                            p.Z + z);
                    }

                    var placed = new GeometryModel3D(mesh, part.Material);
                    scene.Children.Insert(insertAt++, placed);
                    sceneryIds[placed] = index;
                }

                SceneryCount++;
            }

            SceneryModels = sceneryCache.Count;
        }
    }
}