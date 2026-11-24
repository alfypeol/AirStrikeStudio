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
}
