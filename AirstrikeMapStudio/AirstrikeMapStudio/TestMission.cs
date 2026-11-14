using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
                    ? "Game: " + Path.GetFileName(detectedExe) + " (" + detectedExe + ")"
                    : "No game executable automatically found beside data folder.",
                ForeColor = exeFound ? Color.DarkGreen : Color.DarkOrange,
            };

            var browseExeBtn = new Button
            {
                Text = "Browse game exe…",
                AutoSize = true,
            };

            exePanel.Controls.Add(exeLabel);
            exePanel.Controls.Add(browseExeBtn);
            flow.Controls.Add(exePanel);

            bool canLaunch = launchGame != null || exeFound;
            var launch = new CheckBox
            {
                Text = "Launch / focus game after assigning",
                AutoSize = true,
                Checked = canLaunch,
                Enabled = canLaunch,
                Margin = new Padding(0, 2, 0, 6),
            };
            flow.Controls.Add(launch);

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
                        exeLabel.Text = "Game: " + Path.GetFileName(detectedExe) + " (" + detectedExe + ")";
                        exeLabel.ForeColor = Color.DarkGreen;
                        launch.Enabled = true;
                        launch.Checked = true;
                    }
                }
            };

            // Buttons
            var buttons = new FlowLayoutPanel { Width = 710, Height = 38, Margin = new Padding(0, 6, 0, 4) };
            var assign = new Button { Text = "Save map and assign", AutoSize = true };
            var launchNow = new Button { Text = "Launch / focus game", AutoSize = true, Enabled = exeFound };
            var restore = new Button { Text = "Restore previous assignment", AutoSize = true };
            var clearLock = new Button { Text = "Clear session lock", AutoSize = true, Visible = false };
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel };

            buttons.Controls.AddRange(new Control[] { assign, launchNow, restore, clearLock, close });
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
                            proc = TestGameLauncher.LaunchProcess(dataFolder, targetExe, out launchResult);
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
                        proc = TestGameLauncher.LaunchProcess(dataFolder, targetExe, out launchResult);
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
    }

    public sealed partial class GlTerrainWindow
    {
        private Action runTestMission;

        public void ConnectTestMission(Action action, Func<bool> active)
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

            // 2. Add dedicated quick-access button to the documentBar
            var docButton = new Button { Text = "Test mission (F6)", AutoSize = true };
            AddDocumentTool(docButton);

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

            Shown += (s, e) => refresh();
            refresh();
        }
    }

    /// <summary>
    /// Locates and launches the game executable. Refuses to launch if an
    /// AirStrike process is already running, focusing the running game window instead.
    /// Supports AirStrike 3D, Gulf Thunder, AirStrike II, and modded executables.
    /// </summary>
    public static class TestGameLauncher
    {
        private static readonly string[] KnownExecutables = new[]
        {
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
                if (parent == null || !parent.Exists) return null;

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

        public static ProcessStartInfo StartInfo(string dataFolder, string explicitExe = null)
        {
            string executable = explicitExe ?? FindExecutable(dataFolder) ?? Executable(dataFolder);
            if (!File.Exists(executable))
                throw new FileNotFoundException(
                    "Game executable was not found beside the selected data folder:\r\n" + executable,
                    executable);

            return new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = true,
            };
        }

        public static string Launch(string dataFolder, string explicitExe = null)
        {
            LaunchProcess(dataFolder, explicitExe, out string message);
            return message;
        }

        public static Process LaunchProcess(string dataFolder, string explicitExe, out string message)
        {
            var info = StartInfo(dataFolder, explicitExe);
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

            var process = Process.Start(info);
            if (process == null)
                throw new IOException("Windows did not return a game process.");

            message = "Game launched (" + Path.GetFileName(info.FileName) + "). Select the assigned mission in-game.";
            return process;
        }
    }
}
