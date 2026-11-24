using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        public Func<int, Placement, Map> CommitProperties;

        public Placement SelectedDraft() => SelectedIndex < 0 ? null : map.Objects[SelectedIndex].Clone();
        public Map CurrentMap => map;

        public ObjectProperties DescribeDraft(Placement draft, Assets assets) =>
            draft == null ? null : new ObjectProperties(draft, map, assets);

        /// <summary>
        /// Applies a modified placement back to the map. Only one object at a
        /// time; group selection is not supported here. Changing object type
        /// through this panel is rejected (use Replace instead).
        /// </summary>
        public void ApplyProperties(Placement draft)
        {
            if (SelectedObjects.Length > 1) return;
            if (objectDrag || SelectedIndex < 0 || draft == null) return;

            //if (draft.X >= map.Width || draft.Y >= map.Height)
            //    throw new ArgumentException(
            //        "Position must be inside the map: X 0–" + (map.Width - 1) +
            //        ", Y 0–" + (map.Height - 1) + ".");

            if (draft.Type != map.Objects[SelectedIndex].Type)
                throw new ArgumentException("Changing object type is not supported by this panel.");

            // Build the expected map with the change applied, and only
            // proceed if the bytes actually differ.
            var expected = map.Clone();
            expected.Objects[SelectedIndex] = draft.Clone();
            var bytes = expected.Bytes();
            if (bytes.SequenceEqual(map.Bytes())) return;

            // Warm the terrain cache if the position moved.
            if (draft.X != map.Objects[SelectedIndex].X
                || draft.Y != map.Objects[SelectedIndex].Y)
                TerrainZ(draft.X, draft.Y);

            if (CommitProperties != null)
                SyncPlacements(CommitProperties(SelectedIndex, draft.Clone()));
            else
            {
                map.Objects[SelectedIndex] = draft.Clone();
                Changed();
            }
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private readonly PropertyGrid objectProperties = new PropertyGrid
        {
            Dock = DockStyle.Fill,
            HelpVisible = true,
            ToolbarVisible = false,
        };

        private readonly Assets inspectorAssets = new Assets();
        private Placement propertyDraft;

        private void SetupInspector(Panel panel, string data)
        {
            inspectorAssets.Open(data);
            panel.Controls.Add(objectProperties);
            objectProperties.BringToFront();

            Action autoScaleDoc = () =>
            {
                try
                {
                    Control doc = null;
                    foreach (Control c in objectProperties.Controls)
                    {
                        if (c.GetType().Name == "DocComment") { doc = c; break; }
                    }
                    if (doc == null) return;

                    var item = objectProperties.SelectedGridItem;
                    string desc = item?.PropertyDescriptor?.Description ?? "";

                    int textWidth = Math.Max(180, doc.Width - 24);
                    Size sz = TextRenderer.MeasureText(desc, doc.Font, new Size(textWidth, 2000), TextFormatFlags.WordBreak);
                    int neededLines = Math.Max(5, (int)Math.Ceiling(sz.Height / 13.0) + 2);

                    var prop = doc.GetType().GetProperty("Lines");
                    if (prop != null)
                        prop.SetValue(doc, neededLines, null);
                    else
                        doc.Height = Math.Max(80, sz.Height + 35);
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }
            };

            objectProperties.SelectedGridItemChanged += (s, e) => autoScaleDoc();
            panel.HandleCreated += (s, e) => autoScaleDoc();
            Shown += (s, e) => autoScaleDoc();
            panel.Resize += (s, e) => autoScaleDoc();

            Preview.SelectionChanged += (s, e) => RefreshInspector();

            objectProperties.PropertyValueChanged += (s, e) =>
            {
                try { Preview.ApplyProperties(propertyDraft); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Invalid object setting");
                }
                finally
                {
                    RefreshInspector();
                }
            };

            Disposed += (s, e) => inspectorAssets.Dispose();
            RefreshInspector();
        }

        private void RefreshInspector()
        {
            propertyDraft = Preview.SelectedDraft();
            objectProperties.SelectedObject = Preview.DescribeDraft(propertyDraft, inspectorAssets);
            objectProperties.Enabled = propertyDraft != null
                                    && !Preview.PlacementPending
                                    && Preview.SelectedObjects.Length <= 1;
        }
    }
}