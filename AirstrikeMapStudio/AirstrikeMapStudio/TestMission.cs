using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AirstrikeStudio
{
    /// <summary>
    /// One in-progress "test mission" session. The session points one mission
    /// in <c>levels.txt</c> at the user's map; the game will load that map
    /// when the user selects the assigned mission.
    /// </summary>
    /// <remarks>
    /// Lifecycle:
    ///   Begin()   — records the original file bytes into the journal (preserving
    ///               the true pre-test original across multiple re-assignments),
    ///               then writes the modified mission entry to levels.txt.
    ///   Restore() — restores levels.txt back to the original pre-test bytes
    ///               and deletes the journal.
    /// </remarks>
    public sealed class TestMissionSession
    {
        public readonly string Levels;
        public readonly string Journal;

        public TestMissionSession(string dataFolder)
        {
            if (string.IsNullOrEmpty(dataFolder))
                throw new ArgumentNullException(nameof(dataFolder));

            Levels  = Path.Combine(Path.GetFullPath(dataFolder), "maps", "levels.txt");
            Journal = Levels + ".studio-test-session";
        }

        public bool Active => File.Exists(Journal);

        /// <summary>
        /// Replaces <paramref name="path"/>'s contents with
        /// <paramref name="replacement"/>, optionally ensuring existing contents
        /// equal <paramref name="expected"/>.
        /// </summary>
        private static void Replace(string path, byte[] expected, byte[] replacement)
        {
            if (File.Exists(path) && expected != null && !File.ReadAllBytes(path).SequenceEqual(expected))
                throw new IOException("levels.txt changed outside this operation. Nothing was overwritten.");

            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(replacement, 0, replacement.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path) && expected != null && !File.ReadAllBytes(path).SequenceEqual(expected))
                    throw new IOException("levels.txt changed during this operation. Nothing was overwritten.");

                File.Replace(temp, path, path + ".bak-test-" + Guid.NewGuid().ToString("N"));
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        /// <summary>
        /// Writes the journal and modifies levels.txt to point the chosen mission at mapPath.
        /// If a session is already active, retains the original pre-test bytes in the journal
        /// so Restore always reverts back to the clean original file.
        /// </summary>
        public void Begin(string dataFolder, string mapPath, int slot, string environment)
        {
            string lockPath = Journal + ".lock";
            using (var guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                byte[] originalBefore = null;

                // If a session was already active, preserve the true original pre-test bytes
                if (Active)
                {
                    try
                    {
                        Read(out _, out originalBefore, out _);
                    }
                    catch
                    {
                        originalBefore = null;
                    }
                }

                var document = new MissionDocument(Levels);
                if (slot < 0 || slot >= document.Entries.Count)
                    throw new ArgumentOutOfRangeException(nameof(slot), "The chosen mission slot is invalid.");

                var settings = MissionSettings.Read(environment ?? document.Entries[slot].Block);
                settings.MapFile = MissionSettings.RelativePath(dataFolder, mapPath);
                settings.Validate(dataFolder);

                byte[] currentDisk = document.Original;
                byte[] newAfter    = document.EncodeText(document.Build(slot, settings));

                if (originalBefore == null)
                    originalBefore = currentDisk;

                // Durable journal write
                string temp = Journal + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new BinaryWriter(stream))
                    {
                        writer.Write("AirstrikeTestMission1");
                        writer.Write(Path.GetFullPath(Levels));
                        writer.Write(document.Entries[slot].ToString());
                        writer.Write(originalBefore.Length);
                        writer.Write(originalBefore);
                        writer.Write(newAfter.Length);
                        writer.Write(newAfter);
                        writer.Flush();
                        stream.Flush(flushToDisk: true);
                    }
                    if (File.Exists(Journal)) File.Delete(Journal);
                    File.Move(temp, Journal);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }

                // Update levels.txt on disk if needed
                if (!currentDisk.SequenceEqual(newAfter))
                {
                    Replace(Levels, currentDisk, newAfter);
                }
            }
        }

        /// <summary>Human-readable description of the currently-assigned mission.</summary>
        public string Description()
        {
            if (!Active) return "None";
            try
            {
                Read(out string label, out _, out _);
                return label;
            }
            catch
            {
                return "Unknown test mission";
            }
        }

        /// <summary>
        /// Finds which slot index in the given document matches the active journal's assigned mission.
        /// Returns -1 if no matching mission is found.
        /// </summary>
        public int FindAssignedSlot(MissionDocument document)
        {
            if (!Active || document == null) return -1;
            try
            {
                Read(out string label, out _, out _);
                for (int i = 0; i < document.Entries.Count; i++)
                {
                    if (string.Equals(document.Entries[i].ToString(), label, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }
            catch { }
            return -1;
        }

        private void Read(out string label, out byte[] before, out byte[] after)
        {
            using (var reader = new BinaryReader(File.OpenRead(Journal)))
            {
                if (reader.ReadString() != "AirstrikeTestMission1"
                    || !string.Equals(reader.ReadString(), Path.GetFullPath(Levels), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Test-session record does not match this mission file.");

                label  = reader.ReadString();
                before = ReadBytes(reader);
                after  = ReadBytes(reader);

                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    throw new InvalidDataException("Invalid test-session record.");
            }
        }

        private static byte[] ReadBytes(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 1 || length > 16 * 1024 * 1024 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Incomplete test-session record.");
            return reader.ReadBytes(length);
        }

        /// <summary>
        /// Rolls levels.txt back to its original bytes and deletes the journal.
        /// If force is true, restores even if levels.txt was modified externally.
        /// </summary>
        public void Restore(bool force = false)
        {
            string lockPath = Journal + ".lock";
            using (var guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (!File.Exists(Journal)) return;

                Read(out string label, out byte[] before, out byte[] after);

                if (!File.Exists(Levels))
                {
                    File.WriteAllBytes(Levels, before);
                    File.Delete(Journal);
                    return;
                }

                var current = File.ReadAllBytes(Levels);

                if (current.SequenceEqual(before))
                {
                    File.Delete(Journal);
                    return;
                }

                if (!current.SequenceEqual(after) && !force)
                {
                    throw new InvalidOperationException(
                        "levels.txt changed since the test assignment was recorded.\r\n" +
                        "Use Force Restore to overwrite with original backup, or Clear Session to keep changes.");
                }

                Replace(Levels, current, before);
                File.Delete(Journal);
            }
        }

        /// <summary>
        /// Discards the session journal and lock file without modifying levels.txt.
        /// </summary>
        public void ClearSession()
        {
            try
            {
                if (File.Exists(Journal)) File.Delete(Journal);
                string lockPath = Journal + ".lock";
                if (File.Exists(lockPath)) File.Delete(lockPath);
            }
            catch { }
        }
    }

    /// <summary>
    /// The "Temporary test mission" dialog. One ComboBox of mission entries,
    /// a "use open map's environment" toggle, an optional "launch game after
    /// assigning" toggle, and Assign / Restore / Close buttons.
    /// </summary>
    public sealed class TestMissionDialog : Form
    {
        private static int lastSelectedSlot = -1;
        private static string customExecutable = null;

        public TestMissionDialog(
            string dataFolder,
            Func<bool> save,
            Func<string> mapPath,
            Func<string> environment,
            Action<string> onMapCopied = null,
            Func<string> launchGame = null)
        {
            Text = "Temporary test mission";
            Width = 760;
            Height = 540;
            MinimumSize = new Size(680, 500);
            StartPosition = FormStartPosition.CenterParent;

            var session  = new TestMissionSession(dataFolder);
            var document = new MissionDocument(session.Levels);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(12),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
            };
            Controls.Add(flow);

            var infoLabel = new Label
            {
                Width = 710,
                Height = 65,
                Text = "Temporarily assign the open map to an in-game mission slot in levels.txt.\r\n" +
                       "• You can edit the map and click 'Save map & reassign' repeatedly to iterate.\r\n" +
                       "• When done testing, click 'Restore previous assignment' to return levels.txt to normal.\r\n" +
                       "• Close does not restore automatically, so your test assignment stays active.",
            };
            flow.Controls.Add(infoLabel);

            // Warning panel for maps outside data folder
            var mapWarningPanel = new Panel
            {
                Width = 710,
                Height = 60,
                BackColor = Color.FromArgb(255, 245, 230),
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false,
            };
            var mapWarningText = new Label
            {
                Location = new Point(8, 8),
                Size = new Size(540, 42),
                ForeColor = Color.DarkRed,
                Text = "Map must be inside the game's data folder to test.",
            };
            var saveCopyBtn = new Button
            {
                Location = new Point(560, 12),
                Size = new Size(136, 32),
                Text = "Save copy to data\\maps…",
            };
            mapWarningPanel.Controls.Add(mapWarningText);
            mapWarningPanel.Controls.Add(saveCopyBtn);
            flow.Controls.Add(mapWarningPanel);

            // Mission slot selector
            var slotLabel = new Label { Text = "Target mission slot in levels.txt:", AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
            flow.Controls.Add(slotLabel);

            var slots = new ComboBox { Width = 710, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var entry in document.Entries)
                slots.Items.Add(entry);

            // Pre-select the assigned slot if session is active, or last used slot
            int activeSlot = session.FindAssignedSlot(document);
            if (activeSlot >= 0 && activeSlot < slots.Items.Count)
                slots.SelectedIndex = activeSlot;
            else if (lastSelectedSlot >= 0 && lastSelectedSlot < slots.Items.Count)
                slots.SelectedIndex = lastSelectedSlot;
            else if (slots.Items.Count > 0)
                slots.SelectedIndex = 0;

            slots.SelectedIndexChanged += (s, e) =>
            {
                if (slots.SelectedIndex >= 0) lastSelectedSlot = slots.SelectedIndex;
            };
            flow.Controls.Add(slots);

            var useEnvironment = new CheckBox
            {
                Text = "Use the open map's environment (terrain, water, fog, lighting)",
                AutoSize = true,
                Checked = environment() != null,
                Enabled = environment() != null,
                Margin = new Padding(0, 6, 0, 4),
            };
            flow.Controls.Add(useEnvironment);

            // Game Executable Info & Selection
            string detectedExe = customExecutable ?? TestGameLauncher.FindExecutable(dataFolder);
            bool exeFound = !string.IsNullOrEmpty(detectedExe) && File.Exists(detectedExe);

            var exePanel = new FlowLayoutPanel
            {
                Width = 710,
                Height = 32,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 4, 0, 4),
            };

            var exeLabel = new Label
            {
                AutoSize = false,
                Width = 560,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = exeFound
                    ? "Game: " + Path.GetFileName(detectedExe) + (TestGameLauncher.IsOpenAirStrike(detectedExe) ? " [OpenAirStrike Native]" : " [Legacy Trilogy]") + " (" + detectedExe + ")"
                    : "No game executable automatically found beside data folder.",
                ForeColor = exeFound ? (TestGameLauncher.IsOpenAirStrike(detectedExe) ? Color.FromArgb(16, 120, 50) : Color.DarkGreen) : Color.DarkOrange,
            };

            var browseExeBtn = new Button
            {
                Text = "Browse game exe…",
                AutoSize = true,
            };

            exePanel.Controls.Add(exeLabel);
            exePanel.Controls.Add(browseExeBtn);
            flow.Controls.Add(exePanel);

            // Engine Mode & Options Panel
            var optionsPanel = new FlowLayoutPanel
            {
                Width = 710,
                Height = 30,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 2, 0, 4),
            };

            var godModeCheck = new CheckBox
            {
                Text = "God Mode (Invulnerable player for rapid testing)",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 4, 16, 0),
            };

            bool canLaunch = launchGame != null || exeFound;
            var launch = new CheckBox
            {
                Text = "Launch / focus game after assigning",
                AutoSize = true,
                Checked = canLaunch,
                Enabled = canLaunch,
                Margin = new Padding(0, 4, 0, 0),
            };

            optionsPanel.Controls.Add(godModeCheck);
            optionsPanel.Controls.Add(launch);
            flow.Controls.Add(optionsPanel);

            browseExeBtn.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog
                {
                    Title = "Select AirStrike Game Executable",
                    Filter = "Game Executable (*.exe)|*.exe|All Files (*.*)|*.*",
                    InitialDirectory = !string.IsNullOrEmpty(detectedExe) ? Path.GetDirectoryName(detectedExe) : dataFolder,
                })
                {
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        customExecutable = ofd.FileName;
                        TestGameLauncher.CustomExecutablePath = customExecutable;
                        detectedExe = customExecutable;
                        exeFound = true;
                        exeLabel.Text = "Game: " + Path.GetFileName(detectedExe) + (TestGameLauncher.IsOpenAirStrike(detectedExe) ? " [OpenAirStrike Native]" : " [Legacy Trilogy]") + " (" + detectedExe + ")";
                        exeLabel.ForeColor = TestGameLauncher.IsOpenAirStrike(detectedExe) ? Color.FromArgb(16, 120, 50) : Color.DarkGreen;
                        launch.Enabled = true;
                        launch.Checked = true;
                    }
                }
            };

            // Buttons
            var buttons = new FlowLayoutPanel { Width = 710, Height = 38, Margin = new Padding(0, 6, 0, 4) };
            var assign = new Button { Text = "Save map and assign", AutoSize = true };
            var launchNow = new Button { Text = "Launch / focus game", AutoSize = true, Enabled = exeFound };
            var verifyHeadlessBtn = new Button
            {
                Text = "🔍 Verify Map (Headless)…",
                AutoSize = true,
                BackColor = Color.FromArgb(240, 249, 255),
            };
            var restore = new Button { Text = "Restore previous assignment", AutoSize = true };
            var clearLock = new Button { Text = "Clear session lock", AutoSize = true, Visible = false };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel };

            verifyHeadlessBtn.Click += (s, e) =>
            {
                try
                {
                    Validate();
                    if (!save()) return;
                    using (var dlg = new HeadlessVerificationDialog(dataFolder, mapPath(), slots.SelectedIndex, detectedExe))
                    {
                        dlg.ShowDialog(this);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Headless verification failed: " + ex.Message, "Verification Error");
                }
            };

            buttons.Controls.AddRange(new Control[] { assign, launchNow, verifyHeadlessBtn, restore, clearLock, close });
            flow.Controls.Add(buttons);
            CancelButton = close;

            var status = new TextBox
            {
                Width = 710,
                Height = 130,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
            };
            flow.Controls.Add(status);

            Action checkMapLocation = () =>
            {
                string currentPath = mapPath();
                bool inside = MissionSettings.IsInsideDataFolder(dataFolder, currentPath);
                if (!inside)
                {
                    mapWarningPanel.Visible = true;
                    mapWarningText.Text = "Current map is saved outside the game's data folder:\r\n" +
                                          (string.IsNullOrEmpty(currentPath) ? "(Unsaved map)" : currentPath) +
                                          "\r\nThe game engine requires test maps inside: " + Path.Combine(dataFolder, "maps");
                    assign.Enabled = false;
                }
                else
                {
                    mapWarningPanel.Visible = false;
                    assign.Enabled = slots.Items.Count > 0;
                }
            };

            saveCopyBtn.Click += (s, e) =>
            {
                string mapsDir = Path.Combine(dataFolder, "maps");
                if (!Directory.Exists(mapsDir)) Directory.CreateDirectory(mapsDir);

                string originalName = Path.GetFileName(mapPath());
                if (string.IsNullOrEmpty(originalName)) originalName = "test_map.hsc";

                using (var sfd = new SaveFileDialog
                {
                    Title = "Save Map Copy inside Game maps Folder",
                    Filter = "Airstrike map (*.hsc)|*.hsc",
                    InitialDirectory = mapsDir,
                    FileName = originalName,
                    OverwritePrompt = true,
                })
                {
                    if (sfd.ShowDialog(this) == DialogResult.OK)
                    {
                        try
                        {
                            if (save())
                            {
                                File.Copy(mapPath(), sfd.FileName, true);
                                if (onMapCopied != null) onMapCopied(sfd.FileName);
                                status.AppendText("\r\nSaved map copy to: " + sfd.FileName);
                                checkMapLocation();
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(this, "Failed to copy map: " + ex.Message, "Error");
                        }
                    }
                }
            };

            Action refresh = () =>
            {
                bool active = session.Active;
                assign.Text = active ? "Save map & reassign" : "Save map and assign";
                restore.Enabled = active;
                clearLock.Visible = active;
                checkMapLocation();

                if (active)
                {
                    try
                    {
                        status.Text =
                            "Temporary test assignment is ACTIVE for " + session.Description() + ".\r\n" +
                            "• Make map changes and click 'Save map & reassign' anytime to update.\r\n" +
                            "• Click 'Restore previous assignment' when finished testing.\r\n" +
                            "Session journal: " + session.Journal;
                    }
                    catch (Exception ex)
                    {
                        status.Text = ex.Message + "\r\nRecord retained: " + session.Journal;
                    }
                }
                else
                {
                    status.Text =
                        "No temporary test assignment active. Selecting 'Save map and assign' will " +
                        "preserve original levels.txt bytes for clean restoration.";
                }
            };

            assign.Click += (s, e) =>
            {
                try
                {
                    Validate();
                    if (!save()) return;

                    string currentMap = mapPath();
                    if (!MissionSettings.IsInsideDataFolder(dataFolder, currentMap))
                    {
                        MessageBox.Show(this,
                            "The map file must be inside the game's data folder:\r\n" + dataFolder +
                            "\r\nPlease use 'Save copy to data\\maps…' first.",
                            "Map Outside Data Folder");
                        return;
                    }

                    session.Begin(dataFolder, currentMap, slots.SelectedIndex,
                                  useEnvironment.Checked ? environment() : null);
                    refresh();
                    status.AppendText("\r\nSuccessfully assigned map to mission slot [" + slots.SelectedIndex + "].");
                }
                catch (Exception error)
                {
                    refresh();
                    MessageBox.Show(this, error.Message, "Test mission was not assigned");
                    return;
                }

                Action executeLaunch = () =>
                {
                    try
                    {
                        string targetExe = customExecutable ?? TestGameLauncher.FindExecutable(dataFolder);
                        string launchResult;
                        Process proc = null;
                        if (launchGame != null)
                        {
                            launchResult = launchGame();
                        }
                        else
                        {
                            proc = TestGameLauncher.LaunchProcess(dataFolder, targetExe, out launchResult, slots.SelectedIndex, godModeCheck.Checked);
                        }
                        status.AppendText("\r\n" + launchResult);

                        if (proc != null)
                        {
                            // Minimize editor and dialog during gameplay to eliminate GPU/CPU contention
                            if (this.Owner != null)
                                this.Owner.WindowState = FormWindowState.Minimized;
                            this.WindowState = FormWindowState.Minimized;

                            proc.EnableRaisingEvents = true;
                            proc.Exited += (sProc, eProc) =>
                            {
                                try
                                {
                                    this.BeginInvoke((Action)(() =>
                                    {
                                        if (this.WindowState == FormWindowState.Minimized)
                                            this.WindowState = FormWindowState.Normal;
                                        if (this.Owner != null && this.Owner.WindowState == FormWindowState.Minimized)
                                            this.Owner.WindowState = FormWindowState.Normal;
                                        this.Activate();
                                        refresh();
                                    }));
                                }
                                catch { }
                            };
                        }
                    }
                    catch (Exception error)
                    {
                        status.AppendText(
                            "\r\nMission assigned, but the game could not be launched: " +
                            error.Message +
                            " Start it manually or restore the previous assignment.");
                    }
                };

                if (launch.Checked)
                {
                    executeLaunch();
                }
            };

            launchNow.Click += (s, e) =>
            {
                try
                {
                    string targetExe = customExecutable ?? TestGameLauncher.FindExecutable(dataFolder);
                    string launchResult;
                    Process proc = null;
                    if (launchGame != null)
                    {
                        launchResult = launchGame();
                    }
                    else
                    {
                        proc = TestGameLauncher.LaunchProcess(dataFolder, targetExe, out launchResult, slots.SelectedIndex, godModeCheck.Checked);
                    }
                    status.AppendText("\r\n" + launchResult);

                    if (proc != null)
                    {
                        if (this.Owner != null)
                            this.Owner.WindowState = FormWindowState.Minimized;
                        this.WindowState = FormWindowState.Minimized;

                        proc.EnableRaisingEvents = true;
                        proc.Exited += (sProc, eProc) =>
                        {
                            try
                            {
                                this.BeginInvoke((Action)(() =>
                                {
                                    if (this.WindowState == FormWindowState.Minimized)
                                        this.WindowState = FormWindowState.Normal;
                                    if (this.Owner != null && this.Owner.WindowState == FormWindowState.Minimized)
                                        this.Owner.WindowState = FormWindowState.Normal;
                                    this.Activate();
                                    refresh();
                                }));
                            }
                            catch { }
                        };
                    }
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, "Could not launch game");
                }
            };

            restore.Click += (s, e) =>
            {
                try
                {
                    session.Restore(force: false);
                    refresh();
                    status.Text = "Previous mission file restored exactly. Original levels.txt restored.";
                }
                catch (InvalidOperationException error)
                {
                    var choice = MessageBox.Show(this,
                        error.Message + "\r\n\r\n" +
                        "Click 'Yes' to FORCE RESTORE the original pre-test backup (overwriting external changes),\r\n" +
                        "or 'No' to KEEP the current levels.txt and simply end the test session.",
                        "External Changes Detected",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Warning);

                    if (choice == DialogResult.Yes)
                    {
                        session.Restore(force: true);
                        refresh();
                        status.Text = "Force restored original levels.txt from session backup.";
                    }
                    else if (choice == DialogResult.No)
                    {
                        session.ClearSession();
                        refresh();
                        status.Text = "Test session cleared. External changes to levels.txt were kept.";
                    }
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, "Restore was not completed");
                }
            };

            clearLock.Click += (s, e) =>
            {
                if (MessageBox.Show(this,
                    "Clear the active session lock file? This will reset the test state without altering levels.txt.",
                    "Confirm Clear Lock",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    session.ClearSession();
                    refresh();
                }
            };

            refresh();
        }
    }

    public partial class Studio
    {
        void OpenTestMission(IWin32Window owner)
        {
            if (string.IsNullOrEmpty(assets.Root) || !Directory.Exists(assets.Root))
            {
                MessageBox.Show(owner,
                    "Please choose a valid game data folder first (File -> Choose game data folder…).",
                    "Test Mission");
                return;
            }
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                MessageBox.Show(owner,
                    "Please open or create and save a map first before testing.",
                    "Test Mission");
                return;
            }

            using (var dialog = new TestMissionDialog(
                assets.Root,
                () => !dirty && file.Length > 0 || Save(false),
                () => file,
                () => MissionPreset.Resolve(assets.Root, file, environmentOverride),
                newMapPath => { file = newMapPath; }))
            {
                dialog.ShowDialog(owner);
            }
        }

        void VerifyCurrentMapHeadless(IWin32Window owner)
        {
            if (string.IsNullOrEmpty(assets.Root) || !Directory.Exists(assets.Root))
            {
                MessageBox.Show(owner,
                    "Please choose a valid game data folder first (File -> Choose game data folder…).",
                    "Headless Verification");
                return;
            }
            if (string.IsNullOrEmpty(file) || !File.Exists(file))
            {
                MessageBox.Show(owner,
                    "Please open or create and save a map first before verification.",
                    "Headless Verification");
                return;
            }

            if (dirty || file.Length == 0)
            {
                if (!Save(false)) return;
            }

            using (var dialog = new HeadlessVerificationDialog(assets.Root, file, 0))
            {
                dialog.ShowDialog(owner);
            }
        }
    }

    /// <summary>
    /// Interactive dialog that executes OpenAirStrike in headless simulation mode
    /// (60-120 frames) to audit map geometry, script compilation, textures,
    /// models, waypoints, and sound assets without launching a 3D window.
    /// </summary>
    public sealed class HeadlessVerificationDialog : Form
    {
        private readonly string dataFolder;
        private readonly string mapPath;
        private readonly int missionSlot;
        private readonly string customExe;
        private readonly TextBox reportBox;
        private readonly Label statusBanner;
        private readonly Panel topPanel;

        public HeadlessVerificationDialog(string dataFolder, string mapPath, int missionSlot = 0, string customExe = null)
        {
            this.dataFolder = dataFolder;
            this.mapPath = mapPath;
            this.missionSlot = missionSlot;
            this.customExe = customExe;

            Text = "Map Integrity Verification — OpenAirStrike Engine (Headless)";
            Width = 840;
            Height = 620;
            MinimumSize = new Size(680, 480);
            StartPosition = FormStartPosition.CenterParent;

            topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 60,
                Padding = new Padding(14, 10, 14, 10),
                BackColor = Color.FromArgb(241, 245, 249),
            };

            statusBanner = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Running headless verification…",
                ForeColor = Color.FromArgb(71, 85, 105),
            };
            topPanel.Controls.Add(statusBanner);

            reportBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9.5f),
                WordWrap = false,
                BackColor = Color.FromArgb(255, 255, 255),
            };

            var bottomFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10, 6, 10, 6),
                BackColor = Color.FromArgb(248, 250, 252),
            };

            var closeBtn = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true, Height = 30 };
            var copyBtn = new Button { Text = "Copy Report", AutoSize = true, Height = 30 };
            copyBtn.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(reportBox.Text))
                {
                    Clipboard.SetText(reportBox.Text);
                    MessageBox.Show(this, "Verification report copied to clipboard.", "Copied");
                }
            };

            var rerunBtn = new Button { Text = "🔄 Re-run Check (60 frames)", AutoSize = true, Height = 30 };
            rerunBtn.Click += (s, e) => RunAudit();

            var testInGameBtn = new Button
            {
                Text = "▶ Test in Game (F6)",
                AutoSize = true,
                Height = 30,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.FromArgb(220, 252, 231),
                ForeColor = Color.FromArgb(22, 101, 52),
            };
            testInGameBtn.Click += (s, e) =>
            {
                try
                {
                    string targetExe = customExe ?? TestGameLauncher.FindExecutable(dataFolder);
                    TestGameLauncher.LaunchProcess(dataFolder, targetExe, out string msg, missionSlot, true);
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Launch failed: " + ex.Message, "Error");
                }
            };

            bottomFlow.Controls.AddRange(new Control[] { closeBtn, copyBtn, rerunBtn, testInGameBtn });

            Controls.Add(reportBox);
            Controls.Add(topPanel);
            Controls.Add(bottomFlow);

            Shown += (s, e) => RunAudit();
        }

        private void RunAudit()
        {
            statusBanner.Text = "Running 60-frame headless verification in OpenAirStrike…";
            statusBanner.ForeColor = Color.FromArgb(30, 41, 59);
            topPanel.BackColor = Color.FromArgb(241, 245, 249);
            reportBox.Text = "Running verification simulation… please wait.";
            Application.DoEvents();

            bool passed = TestGameLauncher.RunHeadlessVerification(
                dataFolder,
                missionSlot,
                out string report,
                customExe,
                frames: 60);

            reportBox.Text = report;

            if (report.Contains("❌ FAILED") || report.Contains("CRITICAL"))
            {
                statusBanner.Text = "❌ MAP VERIFICATION FAILED — Critical errors detected";
                statusBanner.ForeColor = Color.DarkRed;
                topPanel.BackColor = Color.FromArgb(254, 226, 226);
            }
            else if (report.Contains("⚠ PASSED WITH WARNINGS") || report.Contains("ASSET / SCRIPT WARNINGS"))
            {
                statusBanner.Text = "⚠ MAP VERIFIED WITH WARNINGS — Review missing assets or scripts";
                statusBanner.ForeColor = Color.FromArgb(133, 77, 14);
                topPanel.BackColor = Color.FromArgb(254, 240, 138);
            }
            else
            {
                statusBanner.Text = "✔ MAP INTEGRITY VERIFIED — All geometry, scripts & assets passed";
                statusBanner.ForeColor = Color.FromArgb(22, 101, 52);
                topPanel.BackColor = Color.FromArgb(220, 252, 231);
            }
        }
    }

    public sealed partial class GlTerrainWindow
    {
        private Action runTestMission;

        public void ConnectTestMission(Action action, Func<bool> active, Action verifyAction = null)
        {
            runTestMission = action;

            // 1. Keep the button in the "Assets & export" tab
            TabPage exportTab = null;
            foreach (TabPage t in documentTabs.TabPages)
            {
                if (t.Text == "Assets & export") { exportTab = t; break; }
            }
            Button tabButton = null;
            if (exportTab != null)
            {
                tabButton = new Button { Text = "Test mission…", AutoSize = true };
                var flow = exportTab.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
                if (flow != null) flow.Controls.Add(tabButton);
            }

            // 2. Add dedicated quick-access buttons to the documentBar
            var docButton = new Button { Text = "Test mission (F6)", AutoSize = true };
            AddDocumentTool(docButton);

            if (verifyAction != null)
            {
                var verifyDocButton = new Button { Text = "Verify (F7)", AutoSize = true };
                AddDocumentTool(verifyDocButton);
                verifyDocButton.Click += (s, e) =>
                {
                    try { verifyAction(); }
                    catch (Exception error) { MessageBox.Show(this, error.Message, "Verification Error"); }
                };
            }

            Action refresh = () =>
            {
                bool isAct = active != null && active();
                string title = isAct ? "Test mission [Active] (F6)" : "Test mission (F6)";
                docButton.Text = title;
                if (isAct)
                {
                    docButton.BackColor = Color.FromArgb(70, 48, 12);
                    docButton.ForeColor = Color.Gold;
                }
                else
                {
                    docButton.UseVisualStyleBackColor = true;
                    docButton.ForeColor = SystemColors.ControlText;
                }

                if (menuQuickTest != null)
                {
                    menuQuickTest.Text = isAct ? "▶ Test Mission [Active] (F6)" : "▶ Test Mission (F6)";
                    menuQuickTest.BackColor = isAct ? Color.FromArgb(254, 240, 138) : Color.FromArgb(220, 252, 231);
                    menuQuickTest.ForeColor = isAct ? Color.FromArgb(133, 77, 14) : Color.FromArgb(22, 101, 52);
                }
                if (menuTestMission != null)
                {
                    menuTestMission.Text = isAct ? "Test mission [Active] (F6)" : "Test mission (F6)";
                }

                if (tabButton != null)
                {
                    tabButton.Text = isAct ? "Test mission (active)…" : "Test mission…";
                }
            };

            EventHandler click = (s, e) =>
            {
                try
                {
                    Validate();
                    action();
                    RefreshDocumentTitle();
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, "Test mission unavailable");
                }
                finally
                {
                    refresh();
                }
            };

            docButton.Click += click;
            if (tabButton != null) tabButton.Click += click;
            if (menuTestMission != null) menuTestMission.Click += click;
            if (menuQuickTest != null) menuQuickTest.Click += click;
            if (menuVerifyMap != null && verifyAction != null)
            {
                menuVerifyMap.Click += (s, e) =>
                {
                    try { verifyAction(); }
                    catch (Exception error) { MessageBox.Show(this, error.Message, "Verification Error"); }
                };
            }

            Shown += (s, e) => refresh();
            refresh();
        }
    }

    /// <summary>
    /// Locates and launches the game executable. Refuses to launch duplicate instances
    /// if an AirStrike process is already running, focusing the running game window instead.
    /// Supports Dual Engines: OpenAirStrike Rebuild (Native Win64 CLI launch) and
    /// Original Game Trilogy (AirStrike 3D, AirStrike II, Gulf Thunder Direct3D 8 via levels.txt).
    /// </summary>
    public static class TestGameLauncher
    {
        private static readonly string[] KnownExecutables = new[]
        {
            "as3d_game.exe",
            "AirStrike3D II - Gulf.exe",
            "AirStrike3D.exe",
            "as3d2.exe",
            "AirStrike3Dmodified.exe",
            "AirStrike 3D.exe",
            "AirStrike3D II.exe",
            "AirStrike II.exe",
            "AirStrike.exe",
            "as3d.exe",
            "GulfThunder.exe",
        };

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public static string CustomExecutablePath { get; set; }

        public static bool IsOpenAirStrike(string executable)
        {
            if (string.IsNullOrEmpty(executable)) return false;
            string fname = Path.GetFileName(executable).ToLowerInvariant();
            return fname.Contains("as3d_game") || fname.Contains("openstrike");
        }

        public static string ResolveGameRoot(string dataFolder)
        {
            if (string.IsNullOrEmpty(dataFolder)) return "";
            string full = Path.GetFullPath(dataFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.GetFileName(full).Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                var p = Directory.GetParent(full);
                if (p != null) return p.FullName;
            }
            if (Directory.Exists(Path.Combine(full, "data")))
            {
                return full;
            }
            var parent = Directory.GetParent(full);
            return parent != null ? parent.FullName : full;
        }

        public static bool IsUninstallerOrTool(string filename)
        {
            string lower = filename.ToLowerInvariant();
            return lower.StartsWith("unins") ||
                   lower.Contains("uninstall") ||
                   lower.StartsWith("setup") ||
                   lower.Contains("installer") ||
                   lower.Contains("crash") ||
                   lower.Contains("update") ||
                   lower.Contains("editor") ||
                   lower.Contains("studio");
        }

        /// <summary>
        /// Finds the game executable beside or related to the given data folder.
        /// Searches known AirStrike executable variants and non-uninstaller EXEs.
        /// Returns null if no suitable executable could be found.
        /// </summary>
        public static string FindExecutable(string dataFolder)
        {
            if (!string.IsNullOrEmpty(CustomExecutablePath) && File.Exists(CustomExecutablePath))
                return CustomExecutablePath;

            if (string.IsNullOrEmpty(dataFolder)) return null;

            try
            {
                var fullData = Path.GetFullPath(dataFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Directory.GetParent(fullData);
                if (parent != null && parent.Exists)
                {
                    // 1. Check known executable names in parent folder
                    foreach (var name in KnownExecutables)
                    {
                        string candidate = Path.Combine(parent.FullName, name);
                        if (File.Exists(candidate)) return candidate;
                    }

                    // 2. Search all .exe files in parent directory, filtering out uninstallers/tools
                    var exes = parent.GetFiles("*.exe");
                    var candidates = exes.Where(f => !IsUninstallerOrTool(f.Name)).ToList();

                    var matched = candidates.FirstOrDefault(f =>
                    {
                        string lower = f.Name.ToLowerInvariant();
                        return lower.Contains("airstrike") || lower.Contains("as3d") ||
                               lower.Contains("gulf") || lower.Contains("thunder");
                    });
                    if (matched != null) return matched.FullName;

                    if (candidates.Count == 1) return candidates[0].FullName;
                }

                // 3. Check beside Map Studio binary
                string studioExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "as3d_game.exe");
                if (File.Exists(studioExe)) return studioExe;

                // 4. Check known modded / native win64 build location
                if (File.Exists(@"E:\AirStrike3D_Modded_Native_Win64\as3d_game.exe"))
                    return @"E:\AirStrike3D_Modded_Native_Win64\as3d_game.exe";
            }
            catch
            {
                // Directory access failure
            }

            return null;
        }

        /// <summary>
        /// Returns the path to the detected executable, or a reasonable default path
        /// beside the data folder if no executable is found.
        /// </summary>
        public static string Executable(string dataFolder)
        {
            string found = FindExecutable(dataFolder);
            if (found != null) return found;

            if (!string.IsNullOrEmpty(dataFolder))
            {
                var parent = Directory.GetParent(Path.GetFullPath(dataFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (parent != null)
                    return Path.Combine(parent.FullName, "AirStrike3D.exe");
            }
            return "AirStrike3D.exe";
        }

        public static ProcessStartInfo StartInfo(string dataFolder, string explicitExe = null, int? missionSlot = null, bool godMode = true)
        {
            string executable = explicitExe ?? FindExecutable(dataFolder) ?? Executable(dataFolder);
            if (!File.Exists(executable))
                throw new FileNotFoundException(
                    "Game executable was not found beside the selected data folder:\r\n" + executable,
                    executable);

            var psi = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable),
            };

            if (IsOpenAirStrike(executable))
            {
                string gameRoot = ResolveGameRoot(dataFolder);
                var args = new StringBuilder();
                if (!string.IsNullOrEmpty(gameRoot))
                    args.AppendFormat("--data \"{0}\"", gameRoot);
                if (missionSlot.HasValue && missionSlot.Value >= 0)
                    args.AppendFormat(" --level {0}", missionSlot.Value + 1);
                if (godMode)
                    args.Append(" --god");

                psi.Arguments = args.ToString().Trim();
                psi.UseShellExecute = true;
            }
            else
            {
                psi.UseShellExecute = true;
            }

            return psi;
        }

        public static string Launch(string dataFolder, string explicitExe = null)
        {
            LaunchProcess(dataFolder, explicitExe, out string message);
            return message;
        }

        public static Process LaunchProcess(string dataFolder, string explicitExe, out string message)
        {
            return LaunchProcess(dataFolder, explicitExe, out message, null, true);
        }

        public static Process LaunchProcess(string dataFolder, string explicitExe, out string message, int? missionSlot, bool godMode)
        {
            var info = StartInfo(dataFolder, explicitExe, missionSlot, godMode);
            string procName = Path.GetFileNameWithoutExtension(info.FileName);

            // Refuse to launch a second instance if game is already running; focus it instead.
            Process running = null;
            try
            {
                var matching = Process.GetProcessesByName(procName);
                if (matching.Length > 0 && !matching[0].HasExited)
                    running = matching[0];

                if (running == null)
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        try
                        {
                            string pName = p.ProcessName.ToLowerInvariant();
                            if ((pName.Contains("airstrike") || pName.Contains("as3d") || pName.Contains("gulf"))
                                && !IsUninstallerOrTool(pName) && !p.HasExited)
                            {
                                running = p;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            if (running != null)
            {
                try
                {
                    IntPtr handle = running.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                    {
                        ShowWindow(handle, SW_RESTORE);
                        SetForegroundWindow(handle);
                    }
                    message = "Game is already running (" + running.ProcessName + ".exe). Focused game window. Reload the mission in-game.";
                    return running;
                }
                catch
                {
                    message = "Game is already running (" + running.ProcessName + ".exe). Reload the mission in-game.";
                    return running;
                }
            }

            // Ensure DWM mitigation and high DPI awareness are applied for legacy Direct3D 8 execution
            if (!IsOpenAirStrike(info.FileName))
            {
                try
                {
                    Environment.SetEnvironmentVariable("__COMPAT_LAYER", "DWM8And16BitMitigation HIGHDPIAWARE");
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
                    {
                        if (key != null)
                        {
                            object val = key.GetValue(info.FileName);
                            string sVal = val != null ? val.ToString() : "";
                            if (!sVal.Contains("DWM8And16BitMitigation"))
                            {
                                string newVal = (sVal + " DWM8And16BitMitigation HIGHDPIAWARE").Trim();
                                key.SetValue(info.FileName, newVal);
                            }
                        }
                    }
                }
                catch { }
            }

            var process = Process.Start(info);
            if (process == null)
                throw new IOException("Windows did not return a game process.");

            if (IsOpenAirStrike(info.FileName))
            {
                message = string.Format(
                    "OpenAirStrike Native launched ({0} --level {1}{2}). Direct mission load active!",
                    Path.GetFileName(info.FileName),
                    missionSlot.HasValue ? (missionSlot.Value + 1).ToString() : "1",
                    godMode ? " --god" : "");
            }
            else
            {
                message = "Game launched (" + Path.GetFileName(info.FileName) + "). Select the assigned mission in-game.";
            }
            return process;
        }

        public static bool RunHeadlessVerification(string dataFolder, int missionSlot, out string report, string explicitExe = null, int frames = 60)
        {
            string exe = explicitExe ?? FindExecutable(dataFolder);
            if (!IsOpenAirStrike(exe))
            {
                string candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "as3d_game.exe");
                if (File.Exists(candidate)) exe = candidate;
                else if (File.Exists(@"E:\AirStrike3D_Modded_Native_Win64\as3d_game.exe")) exe = @"E:\AirStrike3D_Modded_Native_Win64\as3d_game.exe";
            }

            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                report = "OpenAirStrike rebuild engine (as3d_game.exe) not found.\r\n" +
                         "Headless map verification requires as3d_game.exe.\r\n" +
                         "Please place as3d_game.exe beside the data folder or in Map Studio directory.";
                return false;
            }

            string gameRoot = ResolveGameRoot(dataFolder);
            int levelNum = missionSlot >= 0 ? missionSlot + 1 : 1;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Format("--data \"{0}\" --level {1} --headless --frames {2}", gameRoot, levelNum, frames),
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            try
            {
                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        report = "Failed to launch headless verification process.";
                        return false;
                    }

                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(15000);

                    var lines = (stdout + "\r\n" + stderr).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    var warnings = new List<string>();
                    var errors = new List<string>();
                    var infos = new List<string>();
                    bool missionLoaded = false;
                    string missionName = "";
                    string doneSummary = "";

                    foreach (var l in lines)
                    {
                        string line = l.Trim();
                        if (line.StartsWith("mission ", StringComparison.OrdinalIgnoreCase))
                        {
                            missionLoaded = true;
                            missionName = line;
                        }
                        else if (line.StartsWith("done:", StringComparison.OrdinalIgnoreCase))
                        {
                            doneSummary = line;
                        }
                        else if (line.Contains("[error]") || line.Contains("FATAL") || line.Contains("Assertion failed"))
                        {
                            errors.Add(line);
                        }
                        else if (line.Contains("[warn]") || line.Contains("warning") || line.Contains("missing") || line.Contains("not found"))
                        {
                            warnings.Add(line);
                        }
                        else if (!string.IsNullOrWhiteSpace(line))
                        {
                            infos.Add(line);
                        }
                    }

                    var rep = new StringBuilder();
                    rep.AppendLine("═════════════════════════════════════════════════════════════════════");
                    rep.AppendLine("  HEADLESS MAP INTEGRITY VERIFICATION REPORT");
                    rep.AppendLine("═════════════════════════════════════════════════════════════════════");
                    rep.AppendLine("Engine:       OpenAirStrike Native Win64 (" + Path.GetFileName(exe) + ")");
                    rep.AppendLine("Data Root:    " + gameRoot);
                    rep.AppendLine("Mission Slot: [" + missionSlot + "] -> Level " + levelNum + (missionName.Length > 0 ? " (" + missionName + ")" : ""));
                    rep.AppendLine("Simulation:   " + frames + " frames (" + (frames / 60.0).ToString("0.0") + "s headless playback)");
                    rep.AppendLine("Status:       " + (errors.Count > 0 ? "❌ FAILED" : (warnings.Count > 0 ? "⚠ PASSED WITH WARNINGS" : "✔ PASSED (ALL CHECKS GREEN)")));
                    rep.AppendLine("─────────────────────────────────────────────────────────────────────");

                    if (!missionLoaded)
                    {
                        rep.AppendLine("❌ CRITICAL: Engine could not initialize or load the specified mission.");
                    }
                    else
                    {
                        rep.AppendLine("✔ Mission & Map Geometry Loaded Successfully");
                        if (!string.IsNullOrEmpty(doneSummary))
                            rep.AppendLine("✔ " + doneSummary);
                    }

                    if (errors.Count > 0)
                    {
                        rep.AppendLine();
                        rep.AppendLine("ERRORS DETECTED (" + errors.Count + "):");
                        foreach (var err in errors) rep.AppendLine("  • " + err);
                    }

                    if (warnings.Count > 0)
                    {
                        rep.AppendLine();
                        rep.AppendLine("ASSET / SCRIPT WARNINGS (" + warnings.Count + "):");
                        foreach (var warn in warnings) rep.AppendLine("  • " + warn);
                    }
                    else
                    {
                        rep.AppendLine("✔ No missing scripts, models, textures, or audio detected.");
                    }

                    if (infos.Count > 0)
                    {
                        rep.AppendLine();
                        rep.AppendLine("ENGINE LOG:");
                        foreach (var inf in infos) rep.AppendLine("  " + inf);
                    }

                    report = rep.ToString();
                    return errors.Count == 0 && missionLoaded;
                }
            }
            catch (Exception ex)
            {
                report = "Exception during headless verification: " + ex.Message;
                return false;
            }
        }
    }
}
