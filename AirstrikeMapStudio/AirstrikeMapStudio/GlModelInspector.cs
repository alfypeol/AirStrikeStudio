using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// Generates the read-only model report shown by the "Inspect selected
    /// model…" button.
    /// </summary>
    public static class ModelInspection
    {
        /// <summary>
        /// Builds a multi-line diagnostic report for one placed object.
        /// </summary>
        /// <param name="previewAvailable">
        /// True if the assembly loaded without throwing (in which case the
        /// "Open 3D preview" button should be enabled).
        /// </param>
        public static string Describe(Assets assets, string name, Placement placement, out bool previewAvailable)
        {
            previewAvailable = false;

            var text = new StringBuilder();
            text.AppendLine("Object: " + name)
                .AppendLine("Data folder: " + assets.Root);

            if (!assets.Definitions.TryGetValue(name, out var definition))
            {
                text.AppendLine("Missing object definition. Choose the matching game data folder.");
                AppendPath(text, assets, "Script override", placement.Script);
                return text.ToString();
            }

            text.AppendLine("Definition file: " + definition.Source);
            AppendPath(text, assets, "Model", definition.Model);
            AppendPath(text, assets, "Texture", definition.Skin);
            AppendPath(text, assets, "Default script", definition.Script);
            AppendPath(text, assets, "Script override", placement.Script);
            AppendPath(text, assets, "Effective script",
                string.IsNullOrEmpty(placement.Script) ? definition.Script : placement.Script);

            text.AppendLine().AppendLine("STATIC ASSEMBLY");

            try
            {
                var assembly = AssemblyScene.Load(assets, name);
                previewAvailable = true;
                text.AppendLine(
                    assembly.PartCount + " mesh parts; " +
                    assembly.Vertices + " vertices; " +
                    assembly.Triangles + " triangles.");
                foreach (string line in assembly.Report)
                    text.AppendLine(line);
            }
            catch (Exception error)
            {
                text.AppendLine("Preview unavailable: " + error.Message);
            }

            text.AppendLine().AppendLine(
                "Inspection is read-only. Preview uses the object definition's " +
                "static assembly. Map script overrides do not alter the preview. " +
                "Animation, aiming, runtime script states and effects are not " +
                "simulated. Missing files and skipped attachments are listed above; " +
                "file existence does not verify script behavior.");

            return text.ToString();
        }

        private static void AppendPath(StringBuilder text, Assets assets, string label, string relative)
        {
            if (string.IsNullOrEmpty(relative))
            {
                text.AppendLine(label + ": (none)");
                return;
            }

            try
            {
                string path = assets.Resolve(relative);
                text.AppendLine(label + ": " + path + (File.Exists(path) ? " [found]" : " [MISSING]"));
            }
            catch (Exception error)
            {
                text.AppendLine(label + ": " + relative + " [invalid: " + error.Message + "]");
            }
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private void InspectSelectedModel()
        {
            if (Preview.SelectedObjects.Length != 1 || Preview.PlacementPending) return;

            var placement = Preview.SelectedDraft();
            string name = Preview.PlacedObjectNames()[Preview.SelectedIndex];

            try
            {
                string report = ModelInspection.Describe(
                    inspectorAssets, name, placement, out bool available);

                using (var form = new Form
                {
                    Text = "Model inspector — " + name,
                    Width = 850,
                    Height = 590,
                    MinimumSize = new Size(520, 350),
                    StartPosition = FormStartPosition.CenterParent,
                })
                {
                    var text = new TextBox
                    {
                        Dock = DockStyle.Fill,
                        Multiline = true,
                        ReadOnly = true,
                        WordWrap = false,
                        ScrollBars = ScrollBars.Both,
                        Text = report,
                    };
                    var buttons = new FlowLayoutPanel
                    {
                        Dock = DockStyle.Bottom,
                        Height = 40,
                        Padding = new Padding(4),
                    };
                    var preview = new Button
                    {
                        Text = "Open 3D preview",
                        AutoSize = true,
                        Enabled = available,
                    };
                    var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel };
                    buttons.Controls.AddRange(new Control[] { preview, close });
                    form.Controls.Add(text);
                    form.Controls.Add(buttons);
                    form.CancelButton = close;

                    preview.Click += (s, e) =>
                    {
                        try
                        {
                            using (var viewer = new ModelViewer(inspectorAssets, name))
                                viewer.ShowDialog(form);
                        }
                        catch (Exception error)
                        {
                            MessageBox.Show(form, error.Message, "Model preview unavailable");
                        }
                    };

                    form.ShowDialog(this);
                }
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Model inspection failed");
            }
        }
    }
}