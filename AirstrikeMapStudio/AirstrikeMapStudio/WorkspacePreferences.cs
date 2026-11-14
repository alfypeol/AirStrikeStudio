using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// Persisted workspace state: window size/maximized, sidebar width,
    /// route-display preferences, recent-object history, and camera
    /// bookmarks. Stored in a simple <c>Key=Value</c> text file.
    /// </summary>
    /// <remarks>
    /// Format (one per line, unknown keys ignored, malformed values fall
    /// back to defaults so a hand-edited file cannot break startup):
    ///
    ///   Width=1100
    ///   Height=820
    ///   Sidebar=380
    ///   RouteScope=0
    ///   Maximized=0
    ///   Routes=1
    ///   Recent0=rocketlauncher_mobile_lgreen
    ///   Recent1=tank_flame
    ///   Bookmark0=16.0,52.0,-90.0,45.0,381.8376618
    ///   Bookmark1=empty
    ///
    /// Bookmark values are five invariant-culture doubles; "empty" is a
    /// cleared slot. Recent entries are ordered newest-first.
    /// </remarks>
    public sealed class WorkspacePreferences
    {
        public int Width = 1100;
        public int Height = 820;
        public int Sidebar = 380;
        public int RouteScope;
        public bool Maximized;
        public bool Routes = true;
        public bool IndependentHandles = true;

        /// <summary>Up to five most-recently-used object names, newest first.</summary>
        public List<string> Recent = new List<string>();

        /// <summary>Five camera bookmarks; each entry is null or {CX,CY,Heading,Pitch,Distance}.</summary>
        public double[][] Bookmarks = new double[5][];

        public static WorkspacePreferences Read(string path)
        {
            var result = new WorkspacePreferences();
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq);
                    string raw = line.Substring(eq + 1);

                    switch (key)
                    {
                        case "Width": if (Int(raw, 800, 10000, out int w)) result.Width = w; break;
                        case "Height": if (Int(raw, 600, 10000, out int h)) result.Height = h; break;
                        case "Sidebar": if (Int(raw, 270, 4000, out int s)) result.Sidebar = s; break;
                        case "RouteScope": if (Int(raw, 0, 2, out int sc)) result.RouteScope = sc; break;
                        case "Maximized": result.Maximized = raw.Trim() == "1"; break;
                        case "Routes": result.Routes = raw.Trim() != "0"; break;
                        case "IndependentHandles": result.IndependentHandles = raw.Trim() != "0"; break;

                        default:
                            if (key.StartsWith("Recent", StringComparison.Ordinal)
                                && int.TryParse(key.Substring(6), NumberStyles.Integer,
                                                CultureInfo.InvariantCulture, out int ri)
                                && ri >= 0 && ri < 5
                                && !string.IsNullOrWhiteSpace(raw))
                            {
                                result.Recent.Add(raw);
                            }
                            else if (key.StartsWith("Bookmark", StringComparison.Ordinal)
                                && int.TryParse(key.Substring(8), NumberStyles.Integer,
                                                CultureInfo.InvariantCulture, out int bi)
                                && bi >= 0 && bi < 5)
                            {
                                result.Bookmarks[bi] = ParseBookmark(raw);
                            }
                            break;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        private static bool Int(string raw, int min, int max, out int value) =>
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max;

        private static double[] ParseBookmark(string raw)
        {
            if (raw.Trim() == "empty") return null;

            var parts = raw.Split(',');
            if (parts.Length != 5) return null;

            var values = new double[5];
            for (int i = 0; i < 5; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float,
                                     CultureInfo.InvariantCulture, out values[i])
                    || double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                    return null;
            }
            return values;
        }

        public void Write(string path)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var lines = new List<string>
                {
                    "Width="      + Width,
                    "Height="     + Height,
                    "Sidebar="    + Sidebar,
                    "RouteScope=" + RouteScope,
                    "Maximized="  + (Maximized ? 1 : 0),
                    "Routes="     + (Routes    ? 1 : 0),
                    "IndependentHandles=" + (IndependentHandles ? 1 : 0),
                };

                for (int i = 0; i < Recent.Count && i < 5; i++)
                    lines.Add("Recent" + i + "=" + Recent[i]);

                for (int i = 0; i < 5; i++)
                {
                    var b = Bookmarks != null && i < Bookmarks.Length ? Bookmarks[i] : null;
                    if (b == null || b.Length != 5)
                    {
                        lines.Add("Bookmark" + i + "=empty");
                        continue;
                    }
                    lines.Add("Bookmark" + i + "=" + string.Join(",", new[]
                    {
                        b[0].ToString("R", CultureInfo.InvariantCulture),
                        b[1].ToString("R", CultureInfo.InvariantCulture),
                        b[2].ToString("R", CultureInfo.InvariantCulture),
                        b[3].ToString("R", CultureInfo.InvariantCulture),
                        b[4].ToString("R", CultureInfo.InvariantCulture),
                    }));
                }

                File.WriteAllLines(temp, lines);

                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public sealed partial class GlTerrainWindow
    {
        /// <summary>Depth-first enumeration of every control, including nested ones.</summary>
        private static IEnumerable<Control> PreferenceControls(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                yield return control;
                foreach (var child in PreferenceControls(control))
                    yield return child;
            }
        }

        public void ConnectWorkspacePreferences(string path)
        {
            var preferences = WorkspacePreferences.Read(path);

            var split = PreferenceControls(this).OfType<SplitContainer>().First(c => c.Orientation == Orientation.Vertical);
            var route = PreferenceControls(this).OfType<CheckBox>().Single(c => c.Text == "Route");
            var scope = PreferenceControls(this).OfType<ComboBox>().Single(
                c => c.Items.Count == 3 && Convert.ToString(c.Items[0]) == "Active object");

            Action<WorkspacePreferences> apply = value =>
            {
                WindowState = FormWindowState.Normal;

                Rectangle area = Screen.FromControl(this).WorkingArea;
                Size = new Size(
                    Math.Max(MinimumSize.Width, Math.Min(value.Width, area.Width)),
                    Math.Max(MinimumSize.Height, Math.Min(value.Height, area.Height)));

                route.Checked = value.Routes;
                scope.SelectedIndex = value.RouteScope;
                Preview.IndependentHandles = value.IndependentHandles;

                // Restore bookmarks and recent objects before the visible
                // layout settles, so the toolbar dropdown and the 1..5 jump
                // shortcuts work on the first interaction.
                for (int i = 0; i < 5; i++)
                    Preview.SetBookmark(i,
                        value.Bookmarks != null && i < value.Bookmarks.Length
                            ? value.Bookmarks[i] : null);

                if (value.Recent != null)
                {
                    RecentObjects.Clear();
                    RecentObjects.AddRange(value.Recent);
                }

                if (value.Maximized) WindowState = FormWindowState.Maximized;
                PerformLayout();

                int maximum = split.Width - split.SplitterWidth - split.Panel2MinSize;
                if (maximum >= split.Panel1MinSize)
                    split.SplitterDistance = Math.Max(split.Panel1MinSize,
                        Math.Min(maximum, split.Width - split.SplitterWidth - value.Sidebar));
            };

            Shown += (s, e) => apply(preferences);

            FormClosed += (s, e) =>
            {
                Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                var value = new WorkspacePreferences
                {
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Maximized = WindowState == FormWindowState.Maximized,
                    Sidebar = split.Panel2.Width,
                    Routes = Preview.ShowRoute,
                    RouteScope = Preview.RouteDisplayScope,
                    IndependentHandles = Preview.IndependentHandles,
                    Recent = new List<string>(RecentObjects),
                    Bookmarks = new double[5][],
                };

                // Preview is still alive here; GlTerrainControl is disposed
                // with the parent form, which happens after FormClosed.
                for (int i = 0; i < 5; i++)
                    value.Bookmarks[i] = Preview.GetBookmark(i);

                try { value.Write(path); }
                catch (Exception error)
                {
                    MessageBox.Show(this,
                        "View preferences could not be saved. Your map is unaffected.\r\n" + error.Message,
                        "Editor preferences");
                }
            };

            var reset = new Button { Text = "Reset view preferences", AutoSize = true };
            reset.Click += (s, e) => apply(new WorkspacePreferences());
            documentTabs.TabPages.Cast<TabPage>().Single(t => t.Text == "View & camera")
                .Controls.OfType<FlowLayoutPanel>().Single().Controls.Add(reset);
        }
    }
}