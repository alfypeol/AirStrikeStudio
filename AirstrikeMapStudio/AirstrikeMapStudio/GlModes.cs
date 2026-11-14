using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    public enum EditingMode
    {
        Select,
        Move,
        Route,
        Box,
        Terrain,
        Tiles,
    }

    public sealed partial class GlTerrainControl
    {
        private EditingMode editingMode;

        public event EventHandler EditingModeChanged;

        public EditingMode ActiveEditingMode => editingMode;

        public bool MoveMode       { get => editingMode == EditingMode.Move;    set => ToggleMode(EditingMode.Move,    value); }
        public bool EditRoute      { get => editingMode == EditingMode.Route;   set => ToggleMode(EditingMode.Route,   value); }
        public bool BoxSelectMode  { get => editingMode == EditingMode.Box;     set => ToggleMode(EditingMode.Box,     value); }
        public bool TerrainMode    { get => editingMode == EditingMode.Terrain; set => ToggleMode(EditingMode.Terrain, value); }
        public bool TileMode       { get => editingMode == EditingMode.Tiles;   set => ToggleMode(EditingMode.Tiles,   value); }

        private void ToggleMode(EditingMode mode, bool active)
        {
            if (active) SetEditingMode(mode);
            else if (editingMode == mode) SetEditingMode(EditingMode.Select);
        }

        /// <summary>Cancel any active mode and return to selection.</summary>
        public void CancelEditingAction() => SetEditingMode(EditingMode.Select);

        /// <summary>
        /// Switches to <paramref name="mode"/>. Discards any in-progress
        /// stroke (sculpt, tile, route-drag, object-drag) without committing.
        /// </summary>
        public void SetEditingMode(EditingMode mode)
        {
            if (!Enum.IsDefined(typeof(EditingMode), mode))
                throw new ArgumentOutOfRangeException(nameof(mode));

            boxDragging = false;
            EndTileStroke(false);
            EndSculpt(false);
            EndRouteDrag(false);
            EndObjectDrag(false);
            dragging = false;
            Capture = false;

            editingMode = mode;
            CancelPlacement();

            if (mode == EditingMode.Route) ShowRoute = true;

            EditingModeChanged?.Invoke(this, EventArgs.Empty);
            Changed();
        }

        /// <summary>
        /// One-line instructions for the status bar describing the current
        /// mode and its primary control.
        /// </summary>
        public string EditingInstructions
        {
            get
            {
                string action =
                    PlacementPending
                        ? "Place object — click ground"
                        : editingMode == EditingMode.Move
                            ? "Move objects — drag selected object"
                        : editingMode == EditingMode.Route
                            ? "Edit route — drag waypoint / handle"
                        : editingMode == EditingMode.Box
                            ? "Box select — drag rectangle; Ctrl toggles"
                        : editingMode == EditingMode.Terrain
                            ? "Sculpt terrain — drag brush; release applies"
                        : editingMode == EditingMode.Tiles
                            ? (EraseTile ? "Erase tiles — drag brush" : "Paint tiles — drag brush")
                        : "Select — click object; drag to orbit";

                return action
                     + " • Right-drag: pan • Wheel: zoom"
                     + (editingMode != EditingMode.Select || PlacementPending ? " • Esc: cancel / select" : "");
            }
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private readonly Dictionary<EditingMode, CheckBox> editingChecks = new Dictionary<EditingMode, CheckBox>();
        private bool syncingEditingChecks;

        /// <summary>
        /// Wires a checkbox to an editing mode. The guard prevents an infinite
        /// loop when <see cref="GlTerrainControl.EditingModeChanged"/> sets
        /// the checkbox states.
        /// </summary>
        private void RegisterEditingCheck(CheckBox check, EditingMode mode)
        {
            editingChecks[mode] = check;

            check.CheckedChanged += (s, e) =>
            {
                if (syncingEditingChecks) return;
                if (check.Checked) Preview.SetEditingMode(mode);
                else if (Preview.ActiveEditingMode == mode) Preview.SetEditingMode(EditingMode.Select);
            };

            Preview.EditingModeChanged += (s, e) =>
            {
                syncingEditingChecks = true;
                try
                {
                    foreach (var item in editingChecks)
                        item.Value.Checked = Preview.ActiveEditingMode == item.Key;
                }
                finally { syncingEditingChecks = false; }
            };
        }

        /// <summary>
        /// True if the focused control is a text, numeric, dropdown, or
        /// property editor (which retain their own Escape behavior).
        /// </summary>
        private bool ValueEditorHasFocus()
        {
            Control focused = ActiveControl;
            while (focused != null)
            {
                Control child = null;
                foreach (Control candidate in focused.Controls)
                {
                    if (candidate.ContainsFocus) { child = candidate; break; }
                }
                if (child == null) break;
                focused = child;
            }

            for (Control at = focused; at != null && at != this; at = at.Parent)
            {
                if (at is TextBoxBase || at is NumericUpDown || at is PropertyGrid || at is ComboBox)
                    return true;
            }
            return false;
        }

        private bool HandleEditingEscape()
        {
            if (ValueEditorHasFocus()) return false;
            if (Preview.PlacementPending)
            {
                Preview.CancelPlacement();
            }
            else if (Preview.SelectedIndex >= 0 || Preview.SelectedObjects.Length > 0)
            {
                Preview.SetObjectSelection(new int[0]);
            }
            else
            {
                Preview.CancelEditingAction();
            }
            Preview.Focus();
            return true;
        }
    }
}