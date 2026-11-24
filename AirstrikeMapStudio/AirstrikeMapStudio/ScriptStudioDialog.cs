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
    /// <summary>
    /// Comprehensive standalone desktop Script Studio dialog for AirStrike 3D
    /// compiled .scr ("RCSL") bytecode files. Provides a script library browser,
    /// C-like decompilation, editable assembly, instant F5 assembly &amp; saving,
    /// assembly diff view, engine API reference, and one-click map object assignment.
    /// </summary>
    public sealed class ScriptStudioDialog : Form
    {
        private readonly Assets assets;
        private readonly GlTerrainControl preview;
        private readonly Action<Placement> applyPlacement;

        private string currentFullPath;
        private string currentRelPath;
        private string originalDiskAsm = "";
        private ScrFile currentScr;

        private readonly List<ScriptItem> allScripts = new List<ScriptItem>();

        // UI Controls
        private readonly TextBox searchBox = new TextBox();
        private readonly ComboBox filterCombo = new ComboBox();
        private readonly ListBox scriptList = new ListBox();

        private readonly Label scriptTitleLabel = new Label();
        private readonly Label scriptStatusLabel = new Label();
        private readonly Button assembleBtn = new Button();
        private readonly Button assignToUnitBtn = new Button();
        private readonly Button newCloneBtn = new Button();
        private readonly Button exportSourceBtn = new Button();
        private readonly Button exportAsmBtn = new Button();
        private readonly Button openExternalToolBtn = new Button();

        private readonly TabControl editorTabs = new TabControl();
        private readonly TextBox sourceBox;
        private readonly TextBox asmBox;
        private readonly TextBox diffBox;
        private readonly ListView funcListView = new ListView();
        private readonly TextBox infoBox;
        private readonly TextBox objDefBox;

        public static Dictionary<string, string> KnownApiDocs => EngineBuiltinCatalog.KnownApiDocs;

        private readonly ComboBox targetEngineCombo = new ComboBox();
        private readonly TextBox searchApiBox = new TextBox();
        private readonly ComboBox apiScopeCombo = new ComboBox();
        private readonly ComboBox apiEngineFilterCombo = new ComboBox();
        private readonly ListBox autoCompleteList = new ListBox();
        private readonly ToolTip autoCompleteTip = new ToolTip { InitialDelay = 100, ReshowDelay = 100, AutoPopDelay = 12000 };

        private TextUndoManager sourceUndo;
        private TextUndoManager asmUndo;
        private bool lastEditedFromSource = true;

        public sealed class TextUndoManager
        {
            private sealed class Snapshot
            {
                public string Text;
                public int SelectionStart;
                public int SelectionLength;
            }

            private readonly Stack<Snapshot> undoStack = new Stack<Snapshot>();
            private readonly Stack<Snapshot> redoStack = new Stack<Snapshot>();
            private readonly TextBox box;
            private bool isUpdating;
            private DateTime lastChange = DateTime.MinValue;
            private Snapshot lastSnapshot;

            public TextUndoManager(TextBox target)
            {
                box = target;
                lastSnapshot = Capture();
                undoStack.Push(lastSnapshot);

                box.TextChanged += (s, e) =>
                {
                    if (isUpdating) return;
                    DateTime now = DateTime.Now;
                    if ((now - lastChange).TotalMilliseconds > 500 && undoStack.Count > 0 && lastSnapshot.Text != undoStack.Peek().Text)
                    {
                        PushSnapshot(lastSnapshot);
                    }
                    lastSnapshot = Capture();
                    lastChange = now;
                    redoStack.Clear();
                };
            }

            private Snapshot Capture() => new Snapshot
            {
                Text = box?.Text ?? "",
                SelectionStart = box?.SelectionStart ?? 0,
                SelectionLength = box?.SelectionLength ?? 0
            };

            public void PushSnapshot() => PushSnapshot(Capture());

            private void PushSnapshot(Snapshot snap)
            {
                if (box == null || snap == null) return;
                if (undoStack.Count > 0 && undoStack.Peek().Text == snap.Text) return;
                undoStack.Push(snap);
                if (undoStack.Count > 80)
                {
                    var arr = undoStack.ToArray();
                    undoStack.Clear();
                    for (int i = Math.Min(arr.Length - 1, 70); i >= 0; i--)
                        undoStack.Push(arr[i]);
                }
            }

            public void ResetHistory()
            {
                undoStack.Clear();
                redoStack.Clear();
                lastSnapshot = Capture();
                undoStack.Push(lastSnapshot);
            }

            public bool CanUndo => undoStack.Count > 1 || (box != null && undoStack.Count > 0 && (box.Text ?? "") != undoStack.Peek().Text);
            public bool CanRedo => redoStack.Count > 0;

            public void Undo()
            {
                if (!CanUndo) return;
                isUpdating = true;
                try
                {
                    string currentText = box.Text ?? "";
                    redoStack.Push(Capture());
                    if (currentText == undoStack.Peek().Text && undoStack.Count > 1)
                        undoStack.Pop();
                    var prev = undoStack.Peek();
                    box.Text = prev.Text;
                    box.SelectionStart = Math.Max(0, Math.Min(box.TextLength, prev.SelectionStart));
                    box.SelectionLength = Math.Max(0, Math.Min(box.TextLength - box.SelectionStart, prev.SelectionLength));
                    box.ScrollToCaret();
                    lastSnapshot = Capture();
                }
                finally
                {
                    isUpdating = false;
                }
            }

            public void Redo()
            {
                if (redoStack.Count == 0) return;
                isUpdating = true;
                try
                {
                    var next = redoStack.Pop();
                    undoStack.Push(next);
                    box.Text = next.Text;
                    box.SelectionStart = Math.Max(0, Math.Min(box.TextLength, next.SelectionStart));
                    box.SelectionLength = Math.Max(0, Math.Min(box.TextLength - box.SelectionStart, next.SelectionLength));
                    box.ScrollToCaret();
                    lastSnapshot = Capture();
                }
                finally
                {
                    isUpdating = false;
                }
            }
        }

        public class ScriptItem
        {
            public string RelativePath;
            public string FullPath;
            public string FileName;
            public bool CallsWaypoints;
            public int InstructionCount;
            public long FileSize;
            public bool IsUsedOnMap;
            public string Note = "";

            public override string ToString()
            {
                string wp = CallsWaypoints ? "[WP] " : "     ";
                string mapTag = IsUsedOnMap ? " ★ " : " ";
                return wp + FileName + mapTag + "(" + InstructionCount + " ops)";
            }
        }

        public ScriptStudioDialog(Assets assets, GlTerrainControl preview = null, Action<Placement> applyPlacement = null, string initialScriptPath = null)
        {
            this.assets = assets;
            this.preview = preview;
            this.applyPlacement = applyPlacement;

            Text = "Script Studio — AirStrike 3D Script Editor & Decompiler (.scr)";
            Width = 1180;
            Height = 760;
            MinimumSize = new Size(880, 560);
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;

            var codeFont = new Font("Consolas", 10f);
            Disposed += (s, e) => codeFont.Dispose();

            TextBox MakeBox(bool readOnly) => new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = readOnly,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsTab = true,
                Font = codeFont,
                BackColor = readOnly ? Color.FromArgb(248, 250, 252) : Color.White,
            };

            sourceBox = MakeBox(false);
            asmBox = MakeBox(false);
            diffBox = MakeBox(true);
            infoBox = MakeBox(true);
            objDefBox = MakeBox(true);

            Action<TextBox> enableCtrlA = box =>
            {
                box.KeyDown += (s, e) =>
                {
                    if (e.Control && e.KeyCode == Keys.A)
                    {
                        box.SelectAll();
                        e.SuppressKeyPress = true;
                    }
                };
            };
            enableCtrlA(sourceBox);
            enableCtrlA(asmBox);
            enableCtrlA(diffBox);
            enableCtrlA(infoBox);
            enableCtrlA(objDefBox);

            // Pre-declare IDE actions for top toolbar and shortcuts
            Action<bool> showFindBar = null;
            Action<TextBox> goToLine = null;

            // ── Top Toolbar ───────────────────────────────────────────────────
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 68,
                BackColor = Color.FromArgb(243, 244, 246),
                Padding = new Padding(10, 6, 10, 6),
            };

            scriptTitleLabel.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
            scriptTitleLabel.ForeColor = Color.FromArgb(30, 41, 59);
            scriptTitleLabel.AutoSize = true;
            scriptTitleLabel.Location = new Point(10, 8);
            scriptTitleLabel.Text = "No script selected";

            scriptStatusLabel.Font = new Font("Segoe UI", 9f);
            scriptStatusLabel.ForeColor = Color.FromArgb(100, 116, 139);
            scriptStatusLabel.AutoSize = true;
            scriptStatusLabel.Location = new Point(12, 36);
            scriptStatusLabel.Text = "Select or open a script from the library.";

            var actionsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 56,
                WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 10, 0, 0),
            };

            assembleBtn.Text = "Compile && Save (F5)";
            assembleBtn.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            assembleBtn.AutoSize = true;
            assembleBtn.Height = 32;
            assembleBtn.BackColor = Color.FromArgb(220, 252, 231);
            assembleBtn.Click += (s, e) => AssembleAndSave();

            targetEngineCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            targetEngineCombo.Font = new Font("Segoe UI", 9f);
            targetEngineCombo.Width = 220;
            targetEngineCombo.Height = 32;
            targetEngineCombo.Items.Add("Target: Auto-Detect");
            targetEngineCombo.Items.Add("Target: AirStrike 3D [85 Builtins]");
            targetEngineCombo.Items.Add("Target: Sequel / OpenAirStrike [101]");
            targetEngineCombo.SelectedIndex = 0;
            targetEngineCombo.SelectedIndexChanged += (s, e) =>
            {
                CheckScriptCompatibility();
                PopulateApiList();
            };

            var moreToolsBtn = new Button
            {
                Text = "Tools ▾",
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Height = 32,
            };

            var moreToolsMenu = new ContextMenuStrip();
            var assignItem = moreToolsMenu.Items.Add("Assign to Selected Map Unit", null, (s, e) => AssignToCurrentMapUnit());
            assignItem.Enabled = preview != null && preview.SelectedIndex >= 0;
            moreToolsMenu.Items.Add("New / Clone Script…", null, (s, e) => ShowNewCloneDialog());
            moreToolsMenu.Items.Add(new ToolStripSeparator());
            moreToolsMenu.Items.Add("Find in Script… (Ctrl+F)", null, (s, e) => { if (showFindBar != null) showFindBar(false); });
            moreToolsMenu.Items.Add("Find && Replace… (Ctrl+H)", null, (s, e) => { if (showFindBar != null) showFindBar(true); });
            moreToolsMenu.Items.Add("Go to Line… (Ctrl+G)", null, (s, e) =>
            {
                TextBox active = editorTabs.SelectedIndex == 1 ? asmBox : sourceBox;
                if (goToLine != null) goToLine(active);
            });
            moreToolsMenu.Items.Add(new ToolStripSeparator());
            moreToolsMenu.Items.Add("Compile Source to ASM View", null, (s, e) => CompileSourceToAsmPreview());
            moreToolsMenu.Items.Add("Export Decompiled C (.c)…", null, (s, e) => ExportFile(false));
            moreToolsMenu.Items.Add("Export Assembly (.asm)…", null, (s, e) => ExportFile(true));
            moreToolsMenu.Items.Add(new ToolStripSeparator());
            moreToolsMenu.Items.Add("Open in External ScriptStudio.py…", null, (s, e) => LaunchExternalScriptStudio());

            moreToolsBtn.Click += (s, e) =>
            {
                assignItem.Enabled = preview != null && preview.SelectedIndex >= 0;
                moreToolsMenu.Show(moreToolsBtn, 0, moreToolsBtn.Height);
            };

            var sidebarToggleBtn = new Button
            {
                Text = "📁 Scripts (Ctrl+B)",
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Height = 32,
            };

            actionsFlow.Controls.AddRange(new Control[]
            {
                assembleBtn, targetEngineCombo, moreToolsBtn, sidebarToggleBtn
            });

            topPanel.Controls.Add(scriptTitleLabel);
            topPanel.Controls.Add(scriptStatusLabel);
            topPanel.Controls.Add(actionsFlow);
            Controls.Add(topPanel);

            // ── Main Split View ───────────────────────────────────────────────
            // FixedPanel.Panel1 ensures the script list sidebar remains at a modest
            // fixed width (~270px), allocating 75% to 85%+ of window space directly
            // to the code editor IDE.
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                SplitterWidth = 6,
            };

            sidebarToggleBtn.Click += (s, e) =>
            {
                split.Panel1Collapsed = !split.Panel1Collapsed;
                sidebarToggleBtn.Text = split.Panel1Collapsed ? "📁 Show Scripts (Ctrl+B)" : "📁 Scripts (Ctrl+B)";
            };

            // ── Left Sidebar (Script Library Explorer) ────────────────────────
            var sidebar = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

            var sidebarHeaderPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 24,
            };
            var searchHeader = new Label
            {
                Text = "SCRIPT EXPLORER",
                AutoSize = true,
                Dock = DockStyle.Left,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(71, 85, 105),
                Padding = new Padding(0, 3, 0, 0),
            };
            var collapseBtn = new Button
            {
                Text = "«",
                Dock = DockStyle.Right,
                Width = 24,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            };
            collapseBtn.FlatAppearance.BorderSize = 0;
            var collapseTip = new ToolTip();
            collapseTip.SetToolTip(collapseBtn, "Collapse script explorer / maximize editor (Ctrl+B)");
            collapseBtn.Click += (s, e) =>
            {
                split.Panel1Collapsed = true;
                sidebarToggleBtn.Text = "📁 Show Scripts (Ctrl+B)";
            };
            sidebarHeaderPanel.Controls.Add(searchHeader);
            sidebarHeaderPanel.Controls.Add(collapseBtn);

            searchBox.Dock = DockStyle.Top;
            searchBox.Font = new Font("Segoe UI", 9.5f);
            searchBox.TextChanged += (s, e) => FilterScriptList();

            filterCombo.Dock = DockStyle.Top;
            filterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            filterCombo.Items.Add("All scripts");
            filterCombo.Items.Add("Waypoint / Route units only");
            filterCombo.Items.Add("Used on this map");
            filterCombo.SelectedIndex = 0;
            filterCombo.SelectedIndexChanged += (s, e) => FilterScriptList();

            scriptList.Dock = DockStyle.Fill;
            scriptList.Font = new Font("Consolas", 8.5f);
            scriptList.IntegralHeight = false;
            scriptList.HorizontalScrollbar = true;
            scriptList.SelectedIndexChanged += (s, e) =>
            {
                if (scriptList.SelectedItem is ScriptItem item)
                    LoadScript(item.FullPath, item.RelativePath);
            };

            var sidebarBottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                Padding = new Padding(0, 4, 0, 0),
            };
            var refreshBtn = new Button { Text = "Refresh", AutoSize = true };
            refreshBtn.Click += (s, e) => ScanScripts();
            var openAnyBtn = new Button { Text = "Open .scr…", AutoSize = true };
            openAnyBtn.Click += (s, e) => OpenExternalScrFile();
            sidebarBottom.Controls.AddRange(new Control[] { refreshBtn, openAnyBtn });

            sidebar.Controls.Add(scriptList);
            sidebar.Controls.Add(sidebarBottom);
            sidebar.Controls.Add(filterCombo);
            sidebar.Controls.Add(searchBox);
            sidebar.Controls.Add(sidebarHeaderPanel);

            sidebarHeaderPanel.SendToBack();
            searchBox.SendToBack();
            filterCombo.SendToBack();
            sidebarBottom.SendToBack();
            scriptList.BringToFront();

            split.Panel1.Controls.Add(sidebar);

            // ── Right Editor Area (Tabs + IDE Status Strip) ───────────────────
            var tabSource = new TabPage("Decompiled Source (C-like)");
            tabSource.Controls.Add(sourceBox);

            var tabAsm = new TabPage("Assembly (.asm — Editable)");
            tabAsm.Controls.Add(asmBox);

            var tabDiff = new TabPage("Assembly Diff (Disk vs Edit)");
            tabDiff.Controls.Add(diffBox);

            var tabFuncs = new TabPage("Engine API & Functions");

            var apiTopPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                Padding = new Padding(4),
                BackColor = Color.FromArgb(243, 244, 246),
            };

            searchApiBox.Dock = DockStyle.Left;
            searchApiBox.Width = 220;
            searchApiBox.Font = new Font("Segoe UI", 9f);
            searchApiBox.Text = "";
            var searchLbl = new Label { Text = "Search API:", AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 6, 4, 0), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };

            apiScopeCombo.Dock = DockStyle.Left;
            apiScopeCombo.Width = 240;
            apiScopeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            apiScopeCombo.Font = new Font("Segoe UI", 9f);
            apiScopeCombo.Items.Add("All 101 Engine Builtins (API Reference)");
            apiScopeCombo.Items.Add("Functions Used in Current Script");
            apiScopeCombo.SelectedIndex = 0;

            apiEngineFilterCombo.Dock = DockStyle.Left;
            apiEngineFilterCombo.Width = 220;
            apiEngineFilterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            apiEngineFilterCombo.Font = new Font("Segoe UI", 9f);
            apiEngineFilterCombo.Items.Add("All Engine Builtins (101)");
            apiEngineFilterCombo.Items.Add("Original AS3D Only (85)");
            apiEngineFilterCombo.Items.Add("Sequel Only (16 Builtins)");
            apiEngineFilterCombo.SelectedIndex = 0;

            var insertApiBtn = new Button
            {
                Dock = DockStyle.Right,
                Text = "Insert Function",
                AutoSize = true,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };

            searchApiBox.TextChanged += (s, e) => PopulateApiList();
            apiScopeCombo.SelectedIndexChanged += (s, e) => PopulateApiList();
            apiEngineFilterCombo.SelectedIndexChanged += (s, e) => PopulateApiList();

            apiTopPanel.Controls.Add(insertApiBtn);
            apiTopPanel.Controls.Add(apiEngineFilterCombo);
            apiTopPanel.Controls.Add(apiScopeCombo);
            apiTopPanel.Controls.Add(searchApiBox);
            apiTopPanel.Controls.Add(searchLbl);

            funcListView.Dock = DockStyle.Fill;
            funcListView.View = View.Details;
            funcListView.FullRowSelect = true;
            funcListView.GridLines = true;
            funcListView.Columns.Add("Function Name", 160);
            funcListView.Columns.Add("Engine Compatibility", 140);
            funcListView.Columns.Add("Category", 90);
            funcListView.Columns.Add("Calls", 50);
            funcListView.Columns.Add("Signature", 230);
            funcListView.Columns.Add("Description", 400);

            tabFuncs.Controls.Add(funcListView);
            tabFuncs.Controls.Add(apiTopPanel);

            Action insertSelectedApi = () =>
            {
                if (funcListView.SelectedItems.Count == 0) return;
                string fnName = funcListView.SelectedItems[0].Text;
                var b = EngineBuiltinCatalog.Find(fnName);
                string snippet = b != null ? b.Name + "()" : fnName + "()";
                TextBox target = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (!target.ReadOnly)
                {
                    target.Paste(snippet);
                    target.Focus();
                }
            };
            funcListView.DoubleClick += (s, e) => insertSelectedApi();
            insertApiBtn.Click += (s, e) => insertSelectedApi();

            var tabInfo = new TabPage("String Table & RCSL Info");
            tabInfo.Controls.Add(infoBox);

            var tabObj = new TabPage("Object Definitions (.obj)");
            tabObj.Controls.Add(objDefBox);

            editorTabs.Dock = DockStyle.Fill;
            editorTabs.TabPages.AddRange(new[] { tabSource, tabAsm, tabDiff, tabFuncs, tabInfo, tabObj });

            var editorContainer = new Panel { Dock = DockStyle.Fill };

            autoCompleteList.Font = new Font("Consolas", 9f);
            autoCompleteList.Visible = false;
            autoCompleteList.Width = 420;
            autoCompleteList.Height = 170;
            autoCompleteList.IntegralHeight = false;
            autoCompleteList.BorderStyle = BorderStyle.FixedSingle;
            autoCompleteList.BackColor = Color.White;

            Action<TextBox, bool> triggerAutocomplete = (box, force) =>
            {
                int cursor = box.SelectionStart;
                if (cursor < 0 || cursor > box.TextLength) { autoCompleteList.Visible = false; return; }

                int start = cursor;
                while (start > 0 && (char.IsLetterOrDigit(box.Text[start - 1]) || box.Text[start - 1] == '_'))
                    start--;

                string prefix = box.Text.Substring(start, cursor - start);
                if (prefix.Length < 2 && !force)
                {
                    autoCompleteList.Visible = false;
                    return;
                }

                int engFilter = targetEngineCombo.SelectedIndex == 1 ? 1 : 0;
                var matches = EngineBuiltinCatalog.Filter(prefix, engFilter).ToList();
                if (matches.Count == 0 && prefix.Length >= 2)
                {
                    matches = EngineBuiltinCatalog.Filter(prefix, 0).ToList();
                }

                if (matches.Count == 0)
                {
                    autoCompleteList.Visible = false;
                    return;
                }

                autoCompleteList.BeginUpdate();
                autoCompleteList.Items.Clear();
                foreach (var m in matches)
                {
                    string tag = m.IsSequelOnly ? "[Sequel]" : "[AS3D]";
                    autoCompleteList.Items.Add(string.Format(CultureInfo.InvariantCulture, "{0,-24} {1,-9} {2}", m.Name, tag, m.Category));
                }
                autoCompleteList.EndUpdate();
                autoCompleteList.SelectedIndex = 0;

                Point pt = box.GetPositionFromCharIndex(start);
                int posX = Math.Max(10, Math.Min(box.Width - autoCompleteList.Width - 20, pt.X));
                int posY = Math.Max(10, Math.Min(box.Height - autoCompleteList.Height - 10, pt.Y + 24));
                autoCompleteList.Location = new Point(posX, posY);
                autoCompleteList.Visible = true;
                autoCompleteList.BringToFront();

                if (matches.Count > 0)
                {
                    autoCompleteTip.Show(matches[0].TooltipText, editorContainer, posX + autoCompleteList.Width + 5, posY, 5000);
                }
            };

            Action<TextBox> commitAutocomplete = box =>
            {
                if (!autoCompleteList.Visible || autoCompleteList.SelectedIndex < 0) return;
                string itemStr = autoCompleteList.SelectedItem.ToString();
                string fnName = itemStr.Split(' ')[0];
                var b = EngineBuiltinCatalog.Find(fnName);
                if (b == null) return;

                int cursor = box.SelectionStart;
                int start = cursor;
                while (start > 0 && (char.IsLetterOrDigit(box.Text[start - 1]) || box.Text[start - 1] == '_'))
                    start--;

                box.Select(start, cursor - start);
                box.SelectedText = b.Name + "()";
                box.SelectionStart = start + b.Name.Length + 1;
                box.SelectionLength = 0;

                autoCompleteList.Visible = false;
                Point pt = box.GetPositionFromCharIndex(box.SelectionStart);
                autoCompleteTip.Show(b.Signature + "\r\n" + b.EngineTarget + " | " + b.Description, box, pt.X, pt.Y - 45, 6000);
            };

            Action<TextBox> checkParamHint = box =>
            {
                int cursor = box.SelectionStart;
                if (cursor <= 0 || cursor > box.TextLength) return;
                if (box.Text[cursor - 1] == '(')
                {
                    int start = cursor - 1;
                    while (start > 0 && char.IsWhiteSpace(box.Text[start - 1])) start--;
                    int endName = start;
                    while (start > 0 && (char.IsLetterOrDigit(box.Text[start - 1]) || box.Text[start - 1] == '_'))
                        start--;
                    string fnName = box.Text.Substring(start, endName - start);
                    var b = EngineBuiltinCatalog.Find(fnName);
                    if (b != null)
                    {
                        Point pt = box.GetPositionFromCharIndex(cursor);
                        autoCompleteTip.Show("▶ " + b.Signature + "\r\n[" + b.EngineTarget + "] " + b.Description, box, pt.X, Math.Max(0, pt.Y - 45), 7000);
                    }
                }
            };

            Action<TextBox> setupIntellisense = box =>
            {
                box.KeyDown += (s, e) =>
                {
                    if (autoCompleteList.Visible)
                    {
                        if (e.KeyCode == Keys.Down)
                        {
                            if (autoCompleteList.SelectedIndex < autoCompleteList.Items.Count - 1)
                                autoCompleteList.SelectedIndex++;
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            return;
                        }
                        if (e.KeyCode == Keys.Up)
                        {
                            if (autoCompleteList.SelectedIndex > 0)
                                autoCompleteList.SelectedIndex--;
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            return;
                        }
                        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab)
                        {
                            commitAutocomplete(box);
                            e.Handled = true;
                            e.SuppressKeyPress = true;
                            return;
                        }
                        if (e.KeyCode == Keys.Escape)
                        {
                            autoCompleteList.Visible = false;
                            autoCompleteTip.Hide(editorContainer);
                            e.Handled = true;
                            return;
                        }
                    }
                    else if (e.Control && e.KeyCode == Keys.Space)
                    {
                        triggerAutocomplete(box, true);
                        e.Handled = true;
                        e.SuppressKeyPress = true;
                    }
                };

                box.KeyUp += (s, e) =>
                {
                    if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab || e.KeyCode == Keys.Escape)
                        return;

                    if (char.IsLetterOrDigit((char)e.KeyValue) || e.KeyCode == Keys.Back)
                    {
                        triggerAutocomplete(box, false);
                    }
                    else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
                    {
                        autoCompleteList.Visible = false;
                    }
                };

                box.KeyPress += (s, e) =>
                {
                    if (e.KeyChar == '(')
                    {
                        BeginInvoke((Action)(() => checkParamHint(box)));
                    }
                };

                box.MouseDown += (s, e) =>
                {
                    autoCompleteList.Visible = false;
                };
            };

            setupIntellisense(sourceBox);
            setupIntellisense(asmBox);

            autoCompleteList.DoubleClick += (s, e) =>
            {
                TextBox active = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                commitAutocomplete(active);
            };

            autoCompleteList.SelectedIndexChanged += (s, e) =>
            {
                if (autoCompleteList.SelectedIndex >= 0 && autoCompleteList.SelectedItem != null)
                {
                    string itemStr = autoCompleteList.SelectedItem.ToString();
                    string fnName = itemStr.Split(' ')[0];
                    var b = EngineBuiltinCatalog.Find(fnName);
                    if (b != null)
                    {
                        Point p = autoCompleteList.Location;
                        autoCompleteTip.Show(b.TooltipText, editorContainer, p.X + autoCompleteList.Width + 5, p.Y, 5000);
                    }
                }
            };

            editorContainer.Controls.Add(autoCompleteList);
            autoCompleteList.BringToFront();

            // ── Floating In-Editor Find & Replace Bar ───────────────────────────
            var findPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 38,
                BackColor = Color.FromArgb(238, 242, 246),
                Visible = false,
                Padding = new Padding(6, 4, 6, 4),
            };

            var lblFind = new Label { Text = "Find:", AutoSize = true, Location = new Point(8, 10), Font = new Font("Segoe UI", 9f) };
            var findInput = new TextBox { Width = 150, Location = new Point(46, 7), Font = new Font("Segoe UI", 9f) };
            var btnFindNext = new Button { Text = "Next (↓)", Width = 64, Height = 25, Location = new Point(202, 6), Font = new Font("Segoe UI", 8.5f) };
            var btnFindPrev = new Button { Text = "Prev (↑)", Width = 64, Height = 25, Location = new Point(270, 6), Font = new Font("Segoe UI", 8.5f) };
            var chkMatchCase = new CheckBox { Text = "Match Case", AutoSize = true, Location = new Point(340, 9), Font = new Font("Segoe UI", 8.5f) };

            var lblReplace = new Label { Text = "Replace:", AutoSize = true, Location = new Point(434, 10), Font = new Font("Segoe UI", 9f) };
            var replaceInput = new TextBox { Width = 130, Location = new Point(490, 7), Font = new Font("Segoe UI", 9f) };
            var btnReplace = new Button { Text = "Replace", Width = 62, Height = 25, Location = new Point(626, 6), Font = new Font("Segoe UI", 8.5f) };
            var btnReplaceAll = new Button { Text = "All", Width = 40, Height = 25, Location = new Point(692, 6), Font = new Font("Segoe UI", 8.5f) };
            var findStatusLabel = new Label { Text = "", AutoSize = true, Location = new Point(738, 10), Font = new Font("Segoe UI", 8.5f), ForeColor = Color.FromArgb(71, 85, 105) };

            var btnCloseFind = new Button
            {
                Text = "✕",
                Width = 26,
                Height = 25,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(findPanel.Width - 32, 6),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            findPanel.Controls.AddRange(new Control[]
            {
                lblFind, findInput, btnFindNext, btnFindPrev, chkMatchCase,
                lblReplace, replaceInput, btnReplace, btnReplaceAll,
                findStatusLabel, btnCloseFind
            });

            Action<bool> doFind = reverse =>
            {
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                string query = findInput.Text;
                if (string.IsNullOrEmpty(query)) return;

                StringComparison comp = chkMatchCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                string allText = activeBox.Text;
                if (string.IsNullOrEmpty(allText)) { findStatusLabel.Text = "No text"; return; }

                int startPos = activeBox.SelectionStart;
                int foundIdx = -1;

                if (!reverse)
                {
                    int searchFrom = startPos + activeBox.SelectionLength;
                    if (searchFrom < allText.Length)
                        foundIdx = allText.IndexOf(query, searchFrom, comp);
                    if (foundIdx < 0)
                        foundIdx = allText.IndexOf(query, 0, comp);
                }
                else
                {
                    int searchFrom = startPos - 1;
                    if (searchFrom >= 0)
                        foundIdx = allText.LastIndexOf(query, searchFrom, comp);
                    if (foundIdx < 0)
                        foundIdx = allText.LastIndexOf(query, allText.Length - 1, comp);
                }

                if (foundIdx >= 0)
                {
                    activeBox.Select(foundIdx, query.Length);
                    activeBox.ScrollToCaret();
                    activeBox.Focus();
                    findStatusLabel.Text = "Found";
                    findStatusLabel.ForeColor = Color.FromArgb(22, 101, 52);
                }
                else
                {
                    findStatusLabel.Text = "No match";
                    findStatusLabel.ForeColor = Color.FromArgb(185, 28, 28);
                }
            };

            btnFindNext.Click += (s, e) => doFind(false);
            btnFindPrev.Click += (s, e) => doFind(true);

            btnReplace.Click += (s, e) =>
            {
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (activeBox.ReadOnly) return;
                string query = findInput.Text;
                if (string.IsNullOrEmpty(query)) return;
                StringComparison comp = chkMatchCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                if (activeBox.SelectionLength > 0 && activeBox.SelectedText.Equals(query, comp))
                {
                    activeBox.SelectedText = replaceInput.Text;
                }
                doFind(false);
            };

            btnReplaceAll.Click += (s, e) =>
            {
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (activeBox.ReadOnly) return;
                string query = findInput.Text;
                if (string.IsNullOrEmpty(query)) return;
                StringComparison comp = chkMatchCase.Checked ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                string text = activeBox.Text;
                int count = 0;
                int idx = 0;
                while ((idx = text.IndexOf(query, idx, comp)) >= 0)
                {
                    count++;
                    idx += query.Length;
                }
                if (count > 0)
                {
                    (activeBox == asmBox ? asmUndo : sourceUndo)?.PushSnapshot();
                    if (comp == StringComparison.Ordinal)
                        activeBox.Text = text.Replace(query, replaceInput.Text);
                    else
                        activeBox.Text = Regex.Replace(text, Regex.Escape(query), replaceInput.Text.Replace("$", "$$"), RegexOptions.IgnoreCase);
                    findStatusLabel.Text = string.Format("{0} replaced", count);
                    findStatusLabel.ForeColor = Color.FromArgb(22, 101, 52);
                }
                else
                {
                    findStatusLabel.Text = "0 replaced";
                    findStatusLabel.ForeColor = Color.FromArgb(100, 116, 139);
                }
            };

            btnCloseFind.Click += (s, e) =>
            {
                findPanel.Visible = false;
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                activeBox.Focus();
            };

            KeyEventHandler findKeyHandler = (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    doFind(e.Shift);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    findPanel.Visible = false;
                    TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                    activeBox.Focus();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            findInput.KeyDown += findKeyHandler;
            replaceInput.KeyDown += findKeyHandler;

            showFindBar = withReplace =>
            {
                findPanel.Visible = true;
                lblReplace.Visible = withReplace;
                replaceInput.Visible = withReplace;
                btnReplace.Visible = withReplace;
                btnReplaceAll.Visible = withReplace;

                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (activeBox.SelectionLength > 0 && !activeBox.SelectedText.Contains("\n"))
                {
                    findInput.Text = activeBox.SelectedText;
                }
                findInput.Focus();
                findInput.SelectAll();
            };

            goToLine = box =>
            {
                using (var dlg = new Form
                {
                    Text = "Go to Line",
                    Width = 270,
                    Height = 140,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    MaximizeBox = false,
                    MinimizeBox = false,
                })
                {
                    int totalLines = box.Lines.Length;
                    int currentLine = box.GetLineFromCharIndex(box.SelectionStart) + 1;
                    var lbl = new Label { Text = string.Format("Line number (1 - {0}):", totalLines), Location = new Point(15, 15), AutoSize = true };
                    var num = new NumericUpDown { Minimum = 1, Maximum = Math.Max(1, totalLines), Value = currentLine, Location = new Point(18, 40), Width = 130 };
                    var btnGo = new Button { Text = "Go", Location = new Point(160, 38), Width = 75, Height = 26, DialogResult = DialogResult.OK };
                    dlg.Controls.AddRange(new Control[] { lbl, num, btnGo });
                    dlg.AcceptButton = btnGo;
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        int targetLine = (int)num.Value - 1;
                        int charIdx = box.GetFirstCharIndexFromLine(targetLine);
                        if (charIdx >= 0)
                        {
                            box.SelectionStart = charIdx;
                            box.SelectionLength = 0;
                            box.ScrollToCaret();
                            box.Focus();
                        }
                    }
                }
            };

            Action<TextBox> toggleComment = box =>
            {
                int selStart = box.SelectionStart;
                int selLen = box.SelectionLength;
                int startLine = box.GetLineFromCharIndex(selStart);
                int endLine = box.GetLineFromCharIndex(selStart + Math.Max(0, selLen - 1));

                var lines = box.Lines;
                if (lines == null || lines.Length == 0) return;

                bool isAsm = (box == asmBox);
                string token = isAsm ? ";" : "//";
                string prefix = isAsm ? "; " : "// ";

                bool hasNonEmpty = false;
                bool allCommented = true;
                for (int i = startLine; i <= endLine && i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (trimmed.Length > 0)
                    {
                        hasNonEmpty = true;
                        if (!trimmed.StartsWith(token))
                        {
                            allCommented = false;
                            break;
                        }
                    }
                }
                if (!hasNonEmpty) allCommented = false;

                for (int i = startLine; i <= endLine && i < lines.Length; i++)
                {
                    if (allCommented)
                    {
                        int idx = lines[i].IndexOf(token);
                        if (idx >= 0)
                        {
                            int tlen = token.Length;
                            int removeLen = (idx + tlen < lines[i].Length && lines[i][idx + tlen] == ' ') ? tlen + 1 : tlen;
                            lines[i] = lines[i].Remove(idx, removeLen);
                        }
                    }
                    else
                    {
                        int indent = 0;
                        while (indent < lines[i].Length && (lines[i][indent] == ' ' || lines[i][indent] == '\t')) indent++;
                        lines[i] = lines[i].Insert(indent, prefix);
                    }
                }

                box.Lines = lines;
                int newCharIndex = box.GetFirstCharIndexFromLine(startLine);
                int endCharIndex = endLine < lines.Length - 1 ? box.GetFirstCharIndexFromLine(endLine + 1) : box.TextLength;
                box.SelectionStart = newCharIndex >= 0 ? newCharIndex : selStart;
                box.SelectionLength = Math.Max(0, endCharIndex - box.SelectionStart);
                box.ScrollToCaret();
            };

            Action<TextBox, bool> handleIndent = (box, outdent) =>
            {
                int selStart = box.SelectionStart;
                int selLen = box.SelectionLength;
                int startLine = box.GetLineFromCharIndex(selStart);
                int endLine = box.GetLineFromCharIndex(selStart + Math.Max(0, selLen - 1));

                if (startLine == endLine && selLen == 0 && !outdent)
                {
                    box.SelectedText = "    ";
                    return;
                }

                var lines = box.Lines;
                if (lines == null || lines.Length == 0) return;

                for (int i = startLine; i <= endLine && i < lines.Length; i++)
                {
                    if (outdent)
                    {
                        if (lines[i].StartsWith("    ")) lines[i] = lines[i].Substring(4);
                        else if (lines[i].StartsWith("\t")) lines[i] = lines[i].Substring(1);
                        else lines[i] = lines[i].TrimStart(' ');
                    }
                    else
                    {
                        lines[i] = "    " + lines[i];
                    }
                }
                box.Lines = lines;
                int newStart = box.GetFirstCharIndexFromLine(startLine);
                int newEnd = endLine < lines.Length - 1 ? box.GetFirstCharIndexFromLine(endLine + 1) : box.TextLength;
                box.SelectionStart = newStart >= 0 ? newStart : selStart;
                box.SelectionLength = Math.Max(0, newEnd - box.SelectionStart);
            };

            Action<TextBox> duplicateLine = box =>
            {
                int lineIndex = box.GetLineFromCharIndex(box.SelectionStart);
                var lines = box.Lines;
                if (lines == null || lineIndex < 0 || lineIndex >= lines.Length) return;
                string lineText = lines[lineIndex];
                int charIndex = box.GetFirstCharIndexFromLine(lineIndex);
                box.Select(charIndex + lineText.Length, 0);
                box.SelectedText = "\r\n" + lineText;
            };

            Action<TextBox, bool> moveLine = (box, moveUp) =>
            {
                int lineIndex = box.GetLineFromCharIndex(box.SelectionStart);
                var lines = box.Lines;
                if (lines == null || lineIndex < 0 || lineIndex >= lines.Length) return;
                if (moveUp && lineIndex > 0)
                {
                    string temp = lines[lineIndex];
                    lines[lineIndex] = lines[lineIndex - 1];
                    lines[lineIndex - 1] = temp;
                    box.Lines = lines;
                    int newChar = box.GetFirstCharIndexFromLine(lineIndex - 1);
                    if (newChar >= 0) box.SelectionStart = newChar;
                }
                else if (!moveUp && lineIndex < lines.Length - 1)
                {
                    string temp = lines[lineIndex];
                    lines[lineIndex] = lines[lineIndex + 1];
                    lines[lineIndex + 1] = temp;
                    box.Lines = lines;
                    int newChar = box.GetFirstCharIndexFromLine(lineIndex + 1);
                    if (newChar >= 0) box.SelectionStart = newChar;
                }
            };

            sourceUndo = new TextUndoManager(sourceBox);
            asmUndo = new TextUndoManager(asmBox);

            Action<TextBox, TextUndoManager> setupEditorShortcuts = (box, undoMgr) =>
            {
                box.KeyDown += (s, e) =>
                {
                    if (e.Control && e.KeyCode == Keys.Z)
                    {
                        if (e.Shift) undoMgr.Redo();
                        else undoMgr.Undo();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.Y)
                    {
                        undoMgr.Redo();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && (e.KeyCode == Keys.OemQuestion || e.KeyCode == Keys.Divide))
                    {
                        undoMgr.PushSnapshot();
                        toggleComment(box);
                        undoMgr.PushSnapshot();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.KeyCode == Keys.Tab)
                    {
                        undoMgr.PushSnapshot();
                        handleIndent(box, e.Shift);
                        undoMgr.PushSnapshot();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.D)
                    {
                        undoMgr.PushSnapshot();
                        duplicateLine(box);
                        undoMgr.PushSnapshot();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Alt && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
                    {
                        undoMgr.PushSnapshot();
                        moveLine(box, e.KeyCode == Keys.Up);
                        undoMgr.PushSnapshot();
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.F)
                    {
                        showFindBar(false);
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.H)
                    {
                        showFindBar(true);
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                    if (e.Control && e.KeyCode == Keys.G)
                    {
                        goToLine(box);
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        return;
                    }
                };
            };

            setupEditorShortcuts(sourceBox, sourceUndo);
            setupEditorShortcuts(asmBox, asmUndo);

            var editorFooter = new StatusStrip
            {
                Dock = DockStyle.Bottom,
                BackColor = Color.FromArgb(241, 245, 249),
                SizingGrip = false,
            };
            var statusCursor = new ToolStripStatusLabel { Text = "Ln 1, Col 1", AutoSize = false, Width = 110 };
            var statusDocInfo = new ToolStripStatusLabel { Text = "0 lines • 0 chars", AutoSize = false, Width = 150 };
            var statusMode = new ToolStripStatusLabel { Text = "C-Source Mode", AutoSize = false, Width = 120 };
            var statusHelp = new ToolStripStatusLabel
            {
                Text = "F5: Compile & Save | Ctrl+F: Find | Ctrl+/: Comment | Tab: Indent | Ctrl+Z/Y: Undo/Redo | Ctrl+G: Line | Ctrl+B: Sidebar",
                Spring = true,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(100, 116, 139)
            };

            editorFooter.Items.AddRange(new ToolStripItem[] { statusCursor, statusDocInfo, statusMode, statusHelp });

            Action updateCursorInfo = () =>
            {
                TextBox activeBox = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                if (editorTabs.SelectedTab == tabDiff) activeBox = diffBox;
                else if (editorTabs.SelectedTab == tabInfo) activeBox = infoBox;
                else if (editorTabs.SelectedTab == tabObj) activeBox = objDefBox;

                int index = activeBox.SelectionStart;
                int line = activeBox.GetLineFromCharIndex(index);
                int firstChar = activeBox.GetFirstCharIndexFromLine(line);
                int col = Math.Max(0, index - firstChar);
                statusCursor.Text = string.Format(CultureInfo.InvariantCulture, "Ln {0}, Col {1}", line + 1, col + 1);
                statusDocInfo.Text = string.Format(CultureInfo.InvariantCulture, "{0} lines • {1:N0} chars", activeBox.Lines.Length, activeBox.TextLength);

                if (editorTabs.SelectedTab == tabSource)
                    statusMode.Text = "C-Source Mode";
                else if (editorTabs.SelectedTab == tabAsm)
                    statusMode.Text = "ASM Mode";
                else
                    statusMode.Text = "View Mode";
            };

            sourceBox.KeyUp += (s, e) => updateCursorInfo();
            sourceBox.MouseUp += (s, e) => updateCursorInfo();
            sourceBox.TextChanged += (s, e) => { if (sourceBox.Focused) lastEditedFromSource = true; updateCursorInfo(); };
            asmBox.KeyUp += (s, e) => updateCursorInfo();
            asmBox.MouseUp += (s, e) => updateCursorInfo();
            asmBox.TextChanged += (s, e) => { if (asmBox.Focused) lastEditedFromSource = false; updateCursorInfo(); };

            editorTabs.SelectedIndexChanged += (s, e) =>
            {
                updateCursorInfo();
                if (editorTabs.SelectedTab == tabDiff)
                    UpdateDiff();
                else if (editorTabs.SelectedTab == tabSource)
                {
                    assembleBtn.Text = "Compile && Save (F5)";
                    assembleBtn.BackColor = Color.FromArgb(220, 252, 231);
                }
                else if (editorTabs.SelectedTab == tabAsm)
                {
                    assembleBtn.Text = "Assemble && Save (F5)";
                    assembleBtn.BackColor = Color.FromArgb(224, 231, 255);
                }
            };

            editorContainer.Controls.Add(findPanel);
            editorContainer.Controls.Add(editorTabs);
            editorContainer.Controls.Add(editorFooter);
            editorFooter.SendToBack();
            editorTabs.BringToFront();
            findPanel.BringToFront();

            split.Panel2.Controls.Add(editorContainer);
            Controls.Add(split);
            split.BringToFront();

            // Set and preserve sidebar width to ~270px so the editor occupies the majority of the IDE
            Action applyLayout = () =>
            {
                try
                {
                    if (split.Width > 450)
                    {
                        split.Panel1MinSize = 160;
                        split.Panel2MinSize = 250;
                        int maxDistance = split.Width - split.SplitterWidth - split.Panel2MinSize;
                        int desired = Math.Min(300, Math.Max(180, (int)(split.Width * 0.23f)));
                        if (desired >= split.Panel1MinSize && desired <= maxDistance)
                            split.SplitterDistance = desired;
                    }
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }
            };
            Load += (s, e) => applyLayout();
            Shown += (s, e) =>
            {
                applyLayout();
                updateCursorInfo();
            };

            // ── Shortcuts ─────────────────────────────────────────────────────
            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.B)
                {
                    split.Panel1Collapsed = !split.Panel1Collapsed;
                    sidebarToggleBtn.Text = split.Panel1Collapsed ? "📁 Show Scripts (Ctrl+B)" : "📁 Scripts (Ctrl+B)";
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F5 && assembleBtn.Enabled)
                {
                    AssembleAndSave();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.S && assembleBtn.Enabled)
                {
                    AssembleAndSave();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.O)
                {
                    OpenExternalScrFile();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.F)
                {
                    showFindBar(false);
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.H)
                {
                    showFindBar(true);
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.G)
                {
                    TextBox active = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                    goToLine(active);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape && findPanel.Visible)
                {
                    findPanel.Visible = false;
                    TextBox active = editorTabs.SelectedTab == tabAsm ? asmBox : sourceBox;
                    active.Focus();
                    e.Handled = true;
                }
            };

            // Initial scan & selection
            ScanScripts(initialScriptPath);
        }

        private void ScanScripts(string preselectPath = null)
        {
            allScripts.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Determine scripts used on current map
            var mapUsedScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (preview != null && preview.CurrentMap != null)
            {
                foreach (var obj in preview.CurrentMap.Objects)
                {
                    if (!string.IsNullOrEmpty(obj.Script))
                        mapUsedScripts.Add(obj.Script);
                    if (preview.PlacedObjectNames() != null && obj.Type < preview.CurrentMap.Types.Count)
                    {
                        string name = preview.CurrentMap.Types[obj.Type];
                        if (assets != null && assets.Definitions.TryGetValue(name, out var d) && !string.IsNullOrEmpty(d.Script))
                            mapUsedScripts.Add(d.Script);
                    }
                }
            }

            string root = assets?.Root ?? "";
            if (Directory.Exists(root))
            {
                foreach (string full in Directory.GetFiles(root, "*.scr", SearchOption.AllDirectories))
                {
                    if (full.EndsWith(".scr.bak", StringComparison.OrdinalIgnoreCase)) continue;

                    string rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        ? full.Substring(root.Length).TrimStart('\\', '/')
                        : full;

                    if (!seen.Add(rel)) continue;

                    var item = new ScriptItem
                    {
                        RelativePath = rel,
                        FullPath = full,
                        FileName = Path.GetFileName(full),
                        FileSize = new FileInfo(full).Length,
                        IsUsedOnMap = mapUsedScripts.Contains(rel),
                    };

                    try
                    {
                        var scr = ScrFile.Parse(File.ReadAllBytes(full));
                        item.CallsWaypoints = scr.CallsWaypointFunctions;
                        item.InstructionCount = scr.Code.Count;
                    }
                    catch
                    {
                        item.InstructionCount = 0;
                    }

                    allScripts.Add(item);
                }
            }

            // Sort: map used first, then alphabetical
            allScripts.Sort((a, b) =>
            {
                if (a.IsUsedOnMap != b.IsUsedOnMap) return b.IsUsedOnMap.CompareTo(a.IsUsedOnMap);
                return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });

            FilterScriptList();

            // Handle preselection
            if (!string.IsNullOrEmpty(preselectPath))
            {
                for (int i = 0; i < scriptList.Items.Count; i++)
                {
                    if (scriptList.Items[i] is ScriptItem it &&
                        (it.RelativePath.Equals(preselectPath, StringComparison.OrdinalIgnoreCase)
                         || it.FileName.Equals(Path.GetFileName(preselectPath), StringComparison.OrdinalIgnoreCase)))
                    {
                        scriptList.SelectedIndex = i;
                        return;
                    }
                }
            }

            if (preview != null && preview.SelectedIndex >= 0)
            {
                var draft = preview.SelectedDraft();
                string candidate = !string.IsNullOrEmpty(draft?.Script) ? draft.Script : null;
                if (candidate == null && assets != null)
                {
                    string tName = preview.PlacedObjectNames()[preview.SelectedIndex];
                    if (assets.Definitions.TryGetValue(tName, out var def))
                        candidate = def.Script;
                }

                if (!string.IsNullOrEmpty(candidate))
                {
                    for (int i = 0; i < scriptList.Items.Count; i++)
                    {
                        if (scriptList.Items[i] is ScriptItem it &&
                            (it.RelativePath.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                             || it.FileName.Equals(Path.GetFileName(candidate), StringComparison.OrdinalIgnoreCase)))
                        {
                            scriptList.SelectedIndex = i;
                            return;
                        }
                    }
                }
            }

            if (scriptList.Items.Count > 0)
                scriptList.SelectedIndex = 0;
        }

        private void FilterScriptList()
        {
            scriptList.BeginUpdate();
            scriptList.Items.Clear();

            string query = searchBox.Text.Trim();
            int filter = filterCombo.SelectedIndex;

            foreach (var item in allScripts)
            {
                if (filter == 1 && !item.CallsWaypoints) continue;
                if (filter == 2 && !item.IsUsedOnMap) continue;

                if (query.Length > 0 &&
                    item.FileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    item.RelativePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                scriptList.Items.Add(item);
            }

            scriptList.EndUpdate();
        }

        private void LoadScript(string fullPath, string relPath)
        {
            currentFullPath = fullPath;
            currentRelPath = relPath;

            scriptTitleLabel.Text = relPath;

            if (!File.Exists(fullPath))
            {
                scriptStatusLabel.Text = "File not found: " + fullPath;
                sourceBox.Text = "// File missing on disk:\r\n// " + fullPath;
                asmBox.Text = "";
                diffBox.Text = "No file on disk.";
                infoBox.Text = "Missing file.";
                objDefBox.Text = "";
                assembleBtn.Enabled = false;
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(fullPath);
                currentScr = ScrFile.Parse(bytes);

                originalDiskAsm = currentScr.Disassemble().Replace("\r\n", "\n").Replace("\n", "\r\n");
                asmBox.Text = originalDiskAsm;
                sourceBox.Text = currentScr.DecompileToSource().Replace("\r\n", "\n").Replace("\n", "\r\n");
                infoBox.Text = currentScr.BuildInfoSummary(fullPath);
                lastEditedFromSource = true;

                sourceUndo?.ResetHistory();
                asmUndo?.ResetHistory();

                // Populate Engine API tab & check compatibility
                PopulateApiList();
                CheckScriptCompatibility();

                // Populate Object definitions tab
                var defsSummary = new StringBuilder();
                if (assets != null)
                {
                    var matching = assets.Definitions.Values.Where(d =>
                        !string.IsNullOrEmpty(d.Script) &&
                        (d.Script.Equals(relPath, StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileName(d.Script).Equals(Path.GetFileName(relPath), StringComparison.OrdinalIgnoreCase))).ToList();

                    if (matching.Count > 0)
                    {
                        defsSummary.AppendLine("// Objects using this script as their default in data/objects:");
                        foreach (var m in matching)
                        {
                            defsSummary.AppendLine("// ── " + m.Name + " (" + Path.GetFileName(m.Source) + ") ──");
                            defsSummary.AppendLine(m.Name + " {");
                            if (!string.IsNullOrEmpty(m.Model))  defsSummary.AppendLine("  model  \"" + m.Model + "\"");
                            if (!string.IsNullOrEmpty(m.Skin))   defsSummary.AppendLine("  skin   \"" + m.Skin + "\"");
                            defsSummary.AppendLine("  script \"" + m.Script + "\"");
                            defsSummary.AppendLine("}");
                            defsSummary.AppendLine();
                        }
                    }
                    else
                    {
                        defsSummary.AppendLine("// No loaded .obj definitions set this script as their default.");
                        defsSummary.AppendLine("// It may be assigned via map overrides or triggered by other scripts.");
                    }
                }
                objDefBox.Text = defsSummary.ToString();

                string wpTag = currentScr.CallsWaypointFunctions ? " • [Waypoints: YES]" : "";
                scriptStatusLabel.Text = string.Format(CultureInfo.InvariantCulture,
                    "{0:N0} bytes • {1} instructions • {2} slots • Active: {3}{4}",
                    bytes.Length, currentScr.Code.Count, currentScr.Slots, currentScr.ActiveEntryRoles(), wpTag);

                assembleBtn.Enabled = true;
                UpdateDiff();
            }
            catch (Exception ex)
            {
                scriptStatusLabel.Text = "Parse error: " + ex.Message;
                sourceBox.Text = "// Failed to parse RCSL: " + ex.Message;
                asmBox.Text = "";
                diffBox.Text = ex.ToString();
                infoBox.Text = ex.ToString();
                assembleBtn.Enabled = false;
            }
        }

        private void CompileSourceToAsmPreview()
        {
            if (string.IsNullOrEmpty(sourceBox.Text)) return;
            try
            {
                var built = ScrFile.CompileSource(sourceBox.Text);
                asmBox.Text = built.Disassemble().Replace("\r\n", "\n").Replace("\n", "\r\n");
                infoBox.Text = built.BuildInfoSummary(currentFullPath);
                UpdateDiff();
                lastEditedFromSource = false;
                editorTabs.SelectedIndex = 1; // switch to tabAsm
                scriptStatusLabel.Text = "Compiled C source -> " + built.Code.Count + " instructions (preview in ASM tab).";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "C-Source Compilation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AssembleAndSave()
        {
            if (string.IsNullOrEmpty(currentFullPath) || !assembleBtn.Enabled) return;

            bool fromSource = editorTabs.SelectedIndex == 0
                || (editorTabs.SelectedIndex != 1 && lastEditedFromSource);
            try
            {
                ScrFile built;
                if (fromSource)
                {
                    built = ScrFile.CompileSource(sourceBox.Text);
                    asmBox.Text = built.Disassemble().Replace("\r\n", "\n").Replace("\n", "\r\n");
                }
                else
                {
                    built = ScrFile.Assemble(asmBox.Text);
                    sourceBox.Text = built.DecompileToSource().Replace("\r\n", "\n").Replace("\n", "\r\n");
                }

                byte[] bytes = built.Build();

                string bak = currentFullPath + ".bak";
                if (File.Exists(currentFullPath) && !File.Exists(bak))
                    File.Copy(currentFullPath, bak);

                File.WriteAllBytes(currentFullPath, bytes);

                originalDiskAsm = asmBox.Text;
                infoBox.Text = built.BuildInfoSummary(currentFullPath);
                scriptStatusLabel.Text = (fromSource ? "Compiled C-source" : "Assembled") + " & saved " + bytes.Length + " bytes to " + Path.GetFileName(currentFullPath) + " (backup at .bak)";
                UpdateDiff();

                MessageBox.Show(this,
                    string.Format("Successfully {0} and saved:\r\n{1}\r\n({2} bytes, {3} instructions)",
                        fromSource ? "compiled from C-like source" : "assembled",
                        currentFullPath,
                        bytes.Length,
                        built.Code.Count),
                    "Script Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, fromSource ? "C-Source Compilation Error" : "Assembly / Build Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateDiff()
        {
            diffBox.Text = ComputeDiff(originalDiskAsm, asmBox.Text);
        }

        private static string ComputeDiff(string orig, string edit)
        {
            if (string.IsNullOrEmpty(orig) && string.IsNullOrEmpty(edit))
                return "No content to compare.";
            if (orig == edit)
                return "Disk script and editor content are identical (Exact match — 0 changes).";

            var oLines = (orig ?? "").Replace("\r\n", "\n").Split('\n');
            var eLines = (edit ?? "").Replace("\r\n", "\n").Split('\n');

            var sb = new StringBuilder();
            sb.AppendLine("── Assembly Diff: Disk Original vs Current Editor ──");
            sb.AppendLine("  ( - ) Removed from disk version");
            sb.AppendLine("  ( + ) Added / changed in editor");
            sb.AppendLine();

            int i = 0, j = 0;
            int count = 0;
            while (i < oLines.Length || j < eLines.Length)
            {
                if (i < oLines.Length && j < eLines.Length && oLines[i] == eLines[j])
                {
                    sb.AppendLine("    " + oLines[i]);
                    i++; j++;
                }
                else
                {
                    count++;
                    int matchJ = -1;
                    for (int k = j; k < Math.Min(j + 10, eLines.Length); k++)
                    {
                        if (i < oLines.Length && oLines[i] == eLines[k]) { matchJ = k; break; }
                    }
                    if (matchJ >= 0)
                    {
                        while (j < matchJ) { sb.AppendLine(" +  " + eLines[j]); j++; }
                    }
                    else
                    {
                        if (i < oLines.Length) { sb.AppendLine(" -  " + oLines[i]); i++; }
                        if (j < eLines.Length) { sb.AppendLine(" +  " + eLines[j]); j++; }
                    }
                }
            }

            if (count == 0) return "Disk script and editor content are identical (Exact match — 0 changes).";
            return sb.ToString();
        }

        private void AssignToCurrentMapUnit()
        {
            if (preview == null || preview.SelectedIndex < 0 || applyPlacement == null || string.IsNullOrEmpty(currentRelPath))
            {
                MessageBox.Show(this, "Select an object on the map first to assign this script.", "Assign Script");
                return;
            }

            var draft = preview.SelectedDraft();
            if (draft == null) return;

            string objName = preview.PlacedObjectNames()[preview.SelectedIndex];
            draft.Script = currentRelPath;
            applyPlacement(draft);

            MessageBox.Show(this,
                string.Format("Successfully assigned map override script:\r\n'{0}'\r\nto Object #{1} ({2}).",
                    currentRelPath, preview.SelectedIndex, objName),
                "Script Assigned", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowNewCloneDialog()
        {
            using (var form = new Form
            {
                Text = "New Script / Clone Script",
                Width = 460,
                Height = 240,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent,
            })
            {
                var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), FlowDirection = FlowDirection.TopDown, WrapContents = false };
                layout.Controls.Add(new Label { Text = "Script template:", AutoSize = true });

                var templateCombo = new ComboBox { Width = 410, DropDownStyle = ComboBoxStyle.DropDownList };
                templateCombo.Items.Add("Clone currently open script (" + (string.IsNullOrEmpty(currentRelPath) ? "none" : Path.GetFileName(currentRelPath)) + ")");
                templateCombo.Items.Add("Waypoint Patrol Enemy (MoveToNextWP template)");
                templateCombo.Items.Add("Turret / Auto-targeting AI (LockTarget template)");
                templateCombo.Items.Add("Empty / Minimal RCSL template");
                templateCombo.SelectedIndex = 0;
                layout.Controls.Add(templateCombo);

                layout.Controls.Add(new Label { Text = "Relative file path (e.g. scripts\\custom_bot.scr):", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
                var pathBox = new TextBox
                {
                    Width = 410,
                    Text = "scripts\\custom_" + (string.IsNullOrEmpty(currentRelPath) ? "unit.scr" : Path.GetFileName(currentRelPath)),
                };
                layout.Controls.Add(pathBox);

                var btnPanel = new FlowLayoutPanel { Width = 410, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
                var cancel = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
                var create = new Button { Text = "Create", Width = 80 };
                btnPanel.Controls.Add(cancel);
                btnPanel.Controls.Add(create);
                layout.Controls.Add(btnPanel);

                form.AcceptButton = create;
                form.CancelButton = cancel;

                create.Click += (s, e) =>
                {
                    string targetRel = pathBox.Text.Trim();
                    if (string.IsNullOrEmpty(targetRel))
                    {
                        MessageBox.Show(form, "Please enter a valid relative path.");
                        return;
                    }
                    if (!targetRel.EndsWith(".scr", StringComparison.OrdinalIgnoreCase))
                        targetRel += ".scr";

                    string root = assets?.Root ?? "";
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    {
                        MessageBox.Show(form, "Load a game data folder first.");
                        return;
                    }

                    string full = Path.Combine(root, targetRel);
                    if (File.Exists(full))
                    {
                        if (MessageBox.Show(form, "File already exists. Overwrite?", "File exists", MessageBoxButtons.YesNo) != DialogResult.Yes)
                            return;
                    }

                    string templateAsm = "";
                    int choice = templateCombo.SelectedIndex;
                    if (choice == 0 && !string.IsNullOrEmpty(asmBox.Text))
                        templateAsm = asmBox.Text;
                    else if (choice == 1)
                        templateAsm = ".code\r\n.slots 16\r\n.entry @0 L_init\r\n.entry @1 L_main\r\n.func MoveToNextWP\r\n.func GetWaypointDelay\r\n.var float 2 1.5f\r\n\r\nL_init:\r\n    RETURN\r\n\r\nL_main:\r\n    PUSHF v2\r\n    CALL MoveToNextWP\r\n    RETURN\r\n    END\r\n";
                    else if (choice == 2)
                        templateAsm = ".code\r\n.slots 16\r\n.entry @0 L_init\r\n.entry @1 L_main\r\n.func LockTarget\r\n.func IsValidTarget\r\n.func G_UseMissile\r\n\r\nL_init:\r\n    RETURN\r\n\r\nL_main:\r\n    CALL LockTarget\r\n    CALL IsValidTarget\r\n    JZ L_wait\r\n    CALL G_UseMissile\r\nL_wait:\r\n    RETURN\r\n    END\r\n";
                    else
                        templateAsm = ".code\r\n.slots 16\r\n.entry @0 L_init\r\n.entry @1 L_main\r\n\r\nL_init:\r\n    RETURN\r\n\r\nL_main:\r\n    RETURN\r\n    END\r\n";

                    try
                    {
                        var scr = ScrFile.Assemble(templateAsm);
                        string dir = Path.GetDirectoryName(full);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(full, scr.Build());

                        form.Close();
                        ScanScripts(targetRel);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(form, ex.Message, "Error creating script");
                    }
                };

                form.ShowDialog(this);
            }
        }

        private void ExportFile(bool isAsm)
        {
            if (string.IsNullOrEmpty(currentFullPath)) return;

            using (var sfd = new SaveFileDialog
            {
                Title = isAsm ? "Export Script Assembly" : "Export Decompiled C-like Source",
                Filter = isAsm ? "Assembly Files (*.asm)|*.asm|Text Files (*.txt)|*.txt|All Files (*.*)|*.*"
                               : "C Source Files (*.c)|*.c|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                FileName = Path.ChangeExtension(Path.GetFileName(currentFullPath), isAsm ? ".asm" : ".c"),
            })
            {
                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllText(sfd.FileName, isAsm ? asmBox.Text : sourceBox.Text);
                        MessageBox.Show(this, "Exported successfully to:\r\n" + sfd.FileName, "Exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, ex.Message, "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void OpenExternalScrFile()
        {
            using (var ofd = new OpenFileDialog
            {
                Title = "Open AirStrike 3D Compiled Script (.scr)",
                Filter = "Compiled Scripts (*.scr)|*.scr|All Files (*.*)|*.*",
            })
            {
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    string root = assets?.Root ?? "";
                    string rel = (!string.IsNullOrEmpty(root) && ofd.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        ? ofd.FileName.Substring(root.Length).TrimStart('\\', '/')
                        : ofd.FileName;

                    LoadScript(ofd.FileName, rel);
                }
            }
        }

        private void PopulateApiList()
        {
            funcListView.BeginUpdate();
            funcListView.Items.Clear();

            var funcCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (currentScr != null)
            {
                foreach (var instr in currentScr.Code)
                {
                    int op = instr.Item1 & 0xFF;
                    if (op == 0x1C || op == 0x1D) // CALL or CALLP
                    {
                        int fIdx = instr.Item2;
                        if (fIdx >= 0 && fIdx < currentScr.Funcs.Count)
                        {
                            string fName = currentScr.Funcs[fIdx];
                            funcCalls[fName] = funcCalls.ContainsKey(fName) ? funcCalls[fName] + 1 : 1;
                        }
                    }
                }
            }

            string search = searchApiBox != null ? searchApiBox.Text.Trim() : "";
            int scope = apiScopeCombo != null ? apiScopeCombo.SelectedIndex : 0;
            int engine = apiEngineFilterCombo != null ? apiEngineFilterCombo.SelectedIndex : 0;

            bool isOriginalTarget = targetEngineCombo != null && (targetEngineCombo.SelectedIndex == 1 ||
                (targetEngineCombo.SelectedIndex == 0 && IsOriginalGameFolder(assets?.Root)));

            var itemsToShow = new List<Tuple<string, EngineBuiltin, int>>();

            if (scope == 1) // Used in script only
            {
                if (currentScr != null)
                {
                    foreach (var fn in currentScr.Funcs)
                    {
                        var b = EngineBuiltinCatalog.Find(fn);
                        if (engine == 1 && b != null && b.IsSequelOnly) continue;
                        if (engine == 2 && (b == null || !b.IsSequelOnly)) continue;
                        if (!string.IsNullOrEmpty(search))
                        {
                            if (fn.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                                (b == null || (b.Description.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                                               b.Category.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)))
                                continue;
                        }
                        int calls = funcCalls.ContainsKey(fn) ? funcCalls[fn] : 0;
                        itemsToShow.Add(Tuple.Create(fn, b, calls));
                    }
                }
            }
            else // All 101 builtins
            {
                var builtins = EngineBuiltinCatalog.Filter(search, engine);
                foreach (var b in builtins)
                {
                    int calls = funcCalls.ContainsKey(b.Name) ? funcCalls[b.Name] : 0;
                    itemsToShow.Add(Tuple.Create(b.Name, b, calls));
                }
            }

            foreach (var item in itemsToShow)
            {
                string fn = item.Item1;
                var b = item.Item2;
                int calls = item.Item3;

                var row = new ListViewItem(fn);
                string compat = b != null ? (b.IsSequelOnly ? "Sequel [AS2/Gulf]" : "Original [AS3D]") : "User Callback";
                string category = b != null ? b.Category : "Custom";
                string sig = b != null ? b.Signature : fn + "(...)";
                string desc = b != null ? b.Description : "User-defined or callback function";

                row.SubItems.Add(compat);
                row.SubItems.Add(category);
                row.SubItems.Add(calls.ToString());
                row.SubItems.Add(sig);
                row.SubItems.Add(desc);

                if (b != null && b.IsSequelOnly)
                {
                    if (isOriginalTarget && calls > 0)
                    {
                        row.ForeColor = Color.DarkRed;
                        row.BackColor = Color.FromArgb(254, 242, 242);
                    }
                    else
                    {
                        row.ForeColor = Color.FromArgb(120, 20, 140);
                    }
                }
                else
                {
                    row.ForeColor = Color.FromArgb(20, 80, 40);
                }

                funcListView.Items.Add(row);
            }

            funcListView.EndUpdate();
        }

        private void CheckScriptCompatibility()
        {
            if (currentScr == null) return;
            var sequelCalls = new List<string>();
            foreach (var fn in currentScr.Funcs)
            {
                if (EngineBuiltinCatalog.SequelOnlyNames.Contains(fn))
                    sequelCalls.Add(fn);
            }

            bool isOriginalTarget = targetEngineCombo != null && (targetEngineCombo.SelectedIndex == 1 ||
                (targetEngineCombo.SelectedIndex == 0 && IsOriginalGameFolder(assets?.Root)));

            if (sequelCalls.Count > 0)
            {
                string callList = string.Join(", ", sequelCalls.Distinct());
                if (isOriginalTarget)
                {
                    scriptStatusLabel.Text = "⚠ Compatibility Warning: Script calls Sequel-only builtins (" + callList + "). Will fail on original AirStrike3D.exe!";
                    scriptStatusLabel.ForeColor = Color.DarkRed;
                }
                else
                {
                    scriptStatusLabel.Text = "ℹ Sequel builtins active: " + callList + " (Supported in AS2, Gulf Thunder & OpenAirStrike).";
                    scriptStatusLabel.ForeColor = Color.FromArgb(112, 26, 117);
                }
            }
            else
            {
                scriptStatusLabel.Text = "✔ 100% Compatible with all engines (Original AirStrike 3D, AS2, Gulf & OpenAirStrike).";
                scriptStatusLabel.ForeColor = Color.FromArgb(22, 101, 52);
            }
        }

        public static bool IsOriginalGameFolder(string dataFolder)
        {
            if (string.IsNullOrEmpty(dataFolder)) return true;
            string lower = dataFolder.ToLowerInvariant();
            if (lower.Contains("gulf") || lower.Contains("airstrike2") || lower.Contains("airstrike 2") || lower.Contains("as3d2") || lower.Contains("airstrike3dii"))
                return false;
            try
            {
                var parent = Directory.GetParent(dataFolder);
                if (parent != null && (File.Exists(Path.Combine(parent.FullName, "as3d2.exe")) || File.Exists(Path.Combine(parent.FullName, "AirStrike3D II - Gulf.exe"))))
                    return false;
            }
            catch (Exception logEx) { AppLog.Warn(logEx); }
            return true;
        }

        private void LaunchExternalScriptStudio()
        {
            if (string.IsNullOrEmpty(currentFullPath)) return;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string exeCandidate = Path.Combine(baseDir, "ScriptStudio", "dist", "ScriptStudio.exe");
            string exeCandidate2 = Path.Combine(baseDir, "ScriptStudio.exe");
            string pyCandidate = Path.Combine(baseDir, "ScriptStudio", "scrstudio.py");

            try
            {
                if (File.Exists(exeCandidate))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exeCandidate, Arguments = "\"" + currentFullPath + "\"", UseShellExecute = true });
                }
                else if (File.Exists(exeCandidate2))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exeCandidate2, Arguments = "\"" + currentFullPath + "\"", UseShellExecute = true });
                }
                else if (File.Exists(pyCandidate))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "python", Arguments = "\"" + pyCandidate + "\" \"" + currentFullPath + "\"", WorkingDirectory = Path.GetDirectoryName(pyCandidate), UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show(this, "External Python ScriptStudio launcher was not found. You can edit, decompile, and assemble scripts right here inside this window!", "Script Studio");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not launch external tool: " + ex.Message + "\r\nAll editing and assembling works natively in this window.", "Script Studio");
            }
        }
    }
}
