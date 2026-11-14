using System;

namespace AirstrikeStudio
{
    public sealed partial class GlTerrainControl
    {
        /// <summary>
        /// Raises <see cref="SelectionChanged"/>, invalidates the viewport, and
        /// notifies <see cref="ViewChanged"/> so the status bar updates.
        /// Called whenever the selection or pending placement changes.
        /// </summary>
        internal void NotifySelection()
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}