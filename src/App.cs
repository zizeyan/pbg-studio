// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PbgStudio
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return StartupLog.Run(baseDir, delegate { return RunApplication(baseDir, args); }, ShowStartupError);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int RunApplication(string baseDir, string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { StartupLog.Write("UNHANDLED", Convert.ToString(e.ExceptionObject)); };
            StartupLog.Write("WINFORMS", "Initializing visual styles and exception handlers.");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { StartupLog.Write("UI ERROR", e.Exception.ToString()); ShowStartupError(e.Exception, StartupLog.PathName); };
            string dataDir = Path.Combine(baseDir, "data");
            Directory.CreateDirectory(dataDir);
            string project = args.FirstOrDefault(a => a.EndsWith(".pbg", StringComparison.OrdinalIgnoreCase));
            string smoke = null;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--smoke") smoke = args[i + 1];
            bool safe = args.Any(a => a.Equals("--safe", StringComparison.OrdinalIgnoreCase));
            StartupLog.Write("MAIN FORM", "Creating window; safeMode=" + safe);
            using (var form = new MainForm(baseDir, dataDir, project, smoke, safe))
            {
                StartupLog.Write("MESSAGE LOOP", "Window constructed; entering Application.Run.");
                Application.Run(form);
            }
            StartupLog.Write("EXIT", "Normal exit.");
            return 0;
        }

        static void ShowStartupError(Exception ex, string logPath)
        {
            string message = "PBG Studio 启动或运行时出现错误：\r\n\r\n" + ex.GetType().FullName + "\r\n" + ex.Message;
            message += String.IsNullOrEmpty(logPath) ? "\r\n\r\n无法写入日志，请复制或截图此错误。" : "\r\n\r\n详细日志：\r\n" + logPath + "\r\n\r\n请把日志内容发回，以便定位具体原因。";
            MessageBox.Show(message, "PBG Studio 错误诊断", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal sealed class TreeInfo
    {
        internal PbgDocument Document;
        internal GroupLink Link;
        internal bool Options;
        internal bool Generated;
    }

    internal sealed class MainForm : Form
    {
        readonly string baseDir, dataDir, initialProject, smokePath;
        readonly bool safeMode;
        readonly AppSettings settings;
        readonly ThumbnailService thumbnails;
        readonly Stack<WorkspaceSnapshot> undo = new Stack<WorkspaceSnapshot>(), redo = new Stack<WorkspaceSnapshot>();
        PbgWorkspace workspace;
        PbgDocument current;
        PartLibrary library;
        CancellationTokenSource previewCancel;
        TreeView tree;
        DataGridView grid;
        TextBox source, search, caption, description, mascot, picture, groupId, quickPart;
        ComboBox sort;
        CheckBox descending, insensitive;
        Label workspaceTitle, workspacePath, groupTitle, groupInfo, sourceInfo, propInfo, previewInfo, status, libraryStatus, listStats;
        PictureBox preview;
        Button apply, save, undoButton, redoButton, add, edit, remove, import, up, down, newGroup;
        TabControl tabs;
        bool filling, refreshingTree, generated, propertyDirty, closing, scanning;
        int scanGeneration, previewGeneration, treeCount;
        TreeNode dragNode;
        int dragRow = -1;
        Point dragStart;
        readonly System.Windows.Forms.Timer previewDelay = new System.Windows.Forms.Timer { Interval = 240 };

        internal MainForm(string baseDirectory, string dataDirectory, string project, string screenshot, bool safeStart = false)
        {
            safeMode = safeStart;
            baseDir = baseDirectory; dataDir = dataDirectory; initialProject = project; smokePath = screenshot;
            StartupLog.Write("SETTINGS", safeMode ? "Using temporary defaults; existing settings retained." : "Reading local settings.");
            settings = safeMode ? new AppSettings() : AppSettings.Load(dataDir); thumbnails = new ThumbnailService(Path.Combine(dataDir, "cache")); thumbnails.TimeoutSeconds = settings.TimeoutSeconds;
            Text = "PBG Studio · LDCad 零件框编辑器"; Font = Ui.Font(9, FontStyle.Regular); ForeColor = Ui.Ink; BackColor = Ui.Surface;
            StartupLog.Write("ICON", "Reading optional application icon.");
            string iconPath = Path.Combine(baseDir, "assets", "PbgStudio.ico");
            if (!safeMode && File.Exists(iconPath))
            {
                try { Icon = new Icon(iconPath, 32, 32); }
                catch (Exception ex) { StartupLog.Write("ICON SKIPPED", ex.ToString()); }
            }
            AutoScaleMode = AutoScaleMode.Dpi; Size = new Size(1440, 940); MinimumSize = new Size(1120, 780); StartPosition = FormStartPosition.CenterScreen;
            StartupLog.Write("BUILD UI", "Building controls and layout.");
            BuildUi();
            StartupLog.Write("UI CONSTRUCTED", "Controls and layout created.");
            previewDelay.Tick += delegate { previewDelay.Stop(); PreviewSelected(); };
            Shown += delegate { StartupLog.Write("WINDOW SHOWN", "Main window is visible."); InitializeProject(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!closing && !ConfirmUnsaved()) { e.Cancel = true; return; } closing = true; previewDelay.Stop(); CancelPreview(); thumbnails.Dispose(); try { if (!safeMode) settings.Save(dataDir); } catch (Exception ex) { StartupLog.Write("SETTINGS SAVE WARNING", ex.ToString()); } };
            FormClosed += delegate { if (preview.Image != null) preview.Image.Dispose(); previewDelay.Dispose(); };
            AllowDrop = true;
            DragEnter += delegate(object sender, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += delegate(object sender, DragEventArgs e) { var files = e.Data.GetData(DataFormats.FileDrop) as string[]; if (files != null && files.Length == 1 && files[0].EndsWith(".pbg", StringComparison.OrdinalIgnoreCase)) OpenProject(files[0], true); };
        }

        void BuildUi()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty, Padding = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var menu = new MenuStrip { Dock = DockStyle.Fill, BackColor = Color.White, Font = Font };
            var file = new ToolStripMenuItem("文件");
            file.DropDownItems.Add(MenuItem("打开 .pbg…", Keys.Control | Keys.O, delegate { PickProject(); }));
            file.DropDownItems.Add(MenuItem("新建零件框…", Keys.Control | Keys.N, delegate { NewProject(); }));
            file.DropDownItems.Add(MenuItem("保存全部", Keys.Control | Keys.S, delegate { SaveAll(); }));
            file.DropDownItems.Add(MenuItem("导出当前分组列表…", Keys.Control | Keys.E, delegate { ExportParts(); }));
            file.DropDownItems.Add(MenuItem("重新从磁盘读取", Keys.F5, delegate { ReloadProject(); }));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(MenuItem("打开示例项目", Keys.None, delegate { OpenProject(Path.Combine(baseDir, "examples", "MyParts", "root.pbg"), true); }));
            var editing = new ToolStripMenuItem("编辑");
            editing.DropDownItems.Add(MenuItem("撤销", Keys.Control | Keys.Z, delegate { Undo(); }));
            editing.DropDownItems.Add(MenuItem("重做", Keys.Control | Keys.Y, delegate { Redo(); }));
            editing.DropDownItems.Add(MenuItem("粘贴导入…", Keys.Control | Keys.Shift | Keys.V, delegate { ImportItems(true); }));
            editing.DropDownItems.Add(MenuItem("新建子分组…", Keys.Control | Keys.G, delegate { AddGroup(); }));
            editing.DropDownItems.Add(MenuItem("引用已有分组…", Keys.None, delegate { AddGroupReference(); }));
            var tools = new ToolStripMenuItem("工具"); tools.DropDownItems.Add(MenuItem("路径设置…", Keys.None, delegate { Configure(); })); tools.DropDownItems.Add(MenuItem("重新扫描零件库", Keys.Control | Keys.F5, delegate { ScanLibrary(); }));
            tools.DropDownItems.Add(MenuItem("检查当前零件框", Keys.None, delegate { ShowDiagnostics(); }));
            var help = new ToolStripMenuItem("帮助"); help.DropDownItems.Add(MenuItem("使用说明", Keys.F1, delegate { ShowHelp(); })); help.DropDownItems.Add(MenuItem("关于", Keys.None, delegate { MessageBox.Show(this, "PBG Studio 1.0.3\r\nLDCad 零件框配置编辑器\r\n\r\n原生 Windows 桌面 · 离线运行\r\n保留注释、编码与换行；原子保存与逐次备份。\r\nLDView 与 LDraw 由用户在路径设置中选择。\r\n许可证：GNU GPL v3（GPL-3.0-only），见 LICENSE。\r\n\r\n本工具为独立项目，与 LDCad / LDView 无官方隶属关系。", "关于 PBG Studio"); }));
            menu.Items.AddRange(new ToolStripItem[] { file, editing, tools, help }); MainMenuStrip = menu; layout.Controls.Add(menu, 0, 0);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 6, 12, 4), BackColor = Color.White, WrapContents = false };
            toolbar.Controls.Add(Ui.Button("打开 .pbg", delegate { PickProject(); })); toolbar.Controls.Add(Ui.Button("新建项目", delegate { NewProject(); }));
            save = Ui.Button("保存全部", delegate { SaveAll(); }); save.BackColor = Ui.Accent; save.ForeColor = Color.White; toolbar.Controls.Add(save);
            undoButton = Ui.Button("撤销", delegate { Undo(); }); redoButton = Ui.Button("重做", delegate { Redo(); }); toolbar.Controls.Add(undoButton); toolbar.Controls.Add(redoButton);
            toolbar.Controls.Add(Ui.Button("路径设置", delegate { Configure(); })); toolbar.Controls.Add(Ui.Button("扫描零件库", delegate { ScanLibrary(); }));
            toolbar.Controls.Add(Ui.Button("检查引用", delegate { ShowDiagnostics(); })); layout.Controls.Add(toolbar, 0, 1);
            var banner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(22, 12, 20, 10), BackColor = Ui.Surface };
            banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); banner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
            banner.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); banner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            workspaceTitle = new Label { Text = "PBG Studio", AutoEllipsis = true, Dock = DockStyle.Fill, Font = Ui.Font(16, FontStyle.Bold), ForeColor = Ui.Ink };
            workspacePath = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Ui.Muted };
            libraryStatus = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Ui.Muted, Text = "LDraw 库未配置" };
            banner.Controls.Add(workspaceTitle, 0, 0); banner.Controls.Add(workspacePath, 0, 1); banner.Controls.Add(libraryStatus, 1, 0); banner.SetRowSpan(libraryStatus, 2); layout.Controls.Add(banner, 0, 2);
            var outer = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 5, BackColor = Ui.Surface, Margin = new Padding(14, 0, 14, 4), Size = new Size(1380, 700) };
            outer.SplitterDistance = 245; outer.Panel1MinSize = 185; outer.Panel2MinSize = 760;
            var treePanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.White };
            treePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 41)); treePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); treePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            treePanel.Controls.Add(new Label { Text = "  分组结构", Font = Ui.Font(10, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            tree = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Ui.Ink, HideSelection = false, ShowNodeToolTips = true, ItemHeight = 29, Indent = 18, AllowDrop = true, FullRowSelect = true };
            tree.BeforeSelect += delegate(object sender, TreeViewCancelEventArgs e) { if (!refreshingTree && !CommitProperties(false)) e.Cancel = true; };
            tree.AfterSelect += TreeSelected;
            tree.ItemDrag += delegate(object sender, ItemDragEventArgs e) { dragNode = e.Item as TreeNode; try { if (dragNode != null) tree.DoDragDrop(dragNode, DragDropEffects.Move); } finally { dragNode = null; } };
            tree.DragOver += TreeDragOver; tree.DragDrop += TreeDrop;
            var treeMenu = new ContextMenuStrip(); treeMenu.Items.Add("新建子分组", null, delegate { AddGroup(); }); treeMenu.Items.Add("引用已有分组", null, delegate { AddGroupReference(); }); treeMenu.Items.Add("重命名标题", null, delegate { RenameGroup(); }); treeMenu.Items.Add("移除此分组引用", null, delegate { RemoveGroup(); });
            tree.NodeMouseClick += delegate(object sender, TreeNodeMouseClickEventArgs e) { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; }; tree.ContextMenuStrip = treeMenu;
            treePanel.Controls.Add(tree, 0, 1);
            var treeBottom = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 5, 4), WrapContents = false };
            newGroup = Ui.Button("＋ 子分组", delegate { AddGroup(); }); treeBottom.Controls.Add(newGroup); treeBottom.Controls.Add(Ui.Button("移除引用", delegate { RemoveGroup(); })); treePanel.Controls.Add(treeBottom, 0, 2); outer.Panel1.Controls.Add(treePanel);
            var inner = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 5, BackColor = Ui.Surface, Size = new Size(1120, 700) };
            inner.SplitterDistance = 770; inner.Panel1MinSize = 420; inner.Panel2MinSize = 310;
            BuildCenter(inner.Panel1); BuildProperties(inner.Panel2); outer.Panel2.Controls.Add(inner); layout.Controls.Add(outer, 0, 3);
            status = new Label { Dock = DockStyle.Fill, Padding = new Padding(20, 6, 16, 0), AutoEllipsis = true, ForeColor = Ui.Muted, Text = "就绪" }; layout.Controls.Add(status, 0, 4); Controls.Add(layout);
        }

        ToolStripMenuItem MenuItem(string text, Keys keys, EventHandler click) { var m = new ToolStripMenuItem(text); m.ShortcutKeys = keys; m.Click += click; return m; }

        void BuildCenter(Control host)
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, BackColor = Color.White };
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); p.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); p.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); p.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var head = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 8, 2), ColumnCount = 1, RowCount = 2 };
            head.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); head.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            groupTitle = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Font = Ui.Font(13, FontStyle.Bold) }; groupInfo = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Ui.Muted }; head.Controls.Add(groupTitle); head.Controls.Add(groupInfo); p.Controls.Add(head, 0, 0);
            var searchBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14, 4, 14, 5) }; searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45)); searchBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchBar.Controls.Add(Ui.Label("筛选"), 0, 0); search = new TextBox { Dock = DockStyle.Fill }; search.TextChanged += delegate { RefreshGrid(); }; searchBar.Controls.Add(search, 1, 0); p.Controls.Add(searchBar, 0, 1);
            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 7) };
            var itemsTab = new TabPage("零件与引用") { BackColor = Color.White, Padding = new Padding(5) };
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 77)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(1, 2, 0, 2), WrapContents = true };
            add = Ui.Button("添加零件", delegate { AddPart(); }); edit = Ui.Button("批量编辑", delegate { EditItems(); }); remove = Ui.Button("删除选中", delegate { RemoveItems(); }); import = Ui.Button("批量导入", delegate { ImportItems(false); });
            up = Ui.Button("↑ 上移", delegate { MoveSelected(-1); }); down = Ui.Button("↓ 下移", delegate { MoveSelected(1); });
            actions.Controls.AddRange(new Control[] { add, edit, remove, import, up, down, Ui.Button("导出列表", delegate { ExportParts(); }) }); content.Controls.Add(actions, 0, 0);
            grid = Ui.Grid(); Ui.Column(grid, "position", "#", 7); Ui.Column(grid, "reference", "零件 / 子分组", 31); Ui.Column(grid, "kind", "类型", 14); Ui.Column(grid, "state", "本地状态", 24); Ui.Column(grid, "color", "颜色", 12); Ui.Column(grid, "count", "数量", 12);
            grid.SelectionChanged += delegate { if (!filling) QueuePreview(); };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex < 0) return; var item = grid.Rows[e.RowIndex].Tag as PbgItem; if (item != null && item.Kind == PbgItemKind.GroupReference) { try { SelectPath(workspace.ResolveGroup(item.Reference)); } catch (Exception ex) { Ui.Error(this, ex); } } else EditItems(); };
            grid.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Delete) { RemoveItems(); e.Handled = true; } if (e.Control && e.KeyCode == Keys.C) { CopyItems(); e.Handled = true; } };
            grid.MouseDown += delegate(object sender, MouseEventArgs e) { dragStart = e.Location; dragRow = grid.HitTest(e.X, e.Y).RowIndex; };
            grid.MouseMove += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && dragRow >= 0 && (Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y)) > 12 && grid.SelectedRows.Count == 1 && CanEditItems()) { try { grid.DoDragDrop("PBG_ITEM:" + dragRow, DragDropEffects.Move); } finally { dragRow = -1; } } };
            grid.AllowDrop = true; grid.DragOver += delegate(object sender, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.Text) && ((string)e.Data.GetData(DataFormats.Text)).StartsWith("PBG_ITEM:") && CanEditItems() && search.Text.Length == 0) e.Effect = DragDropEffects.Move; };
            grid.DragDrop += GridDrop;
            content.Controls.Add(grid, 0, 1); itemsTab.Controls.Add(content); tabs.TabPages.Add(itemsTab);
            var sourceTab = new TabPage("原始文件 · 只读") { Padding = new Padding(8) };
            var rawLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 }; rawLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); rawLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sourceInfo = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Muted }; rawLayout.Controls.Add(sourceInfo, 0, 0);
            source = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10), BackColor = Color.White, BorderStyle = BorderStyle.None }; rawLayout.Controls.Add(source, 0, 1); sourceTab.Controls.Add(rawLayout); tabs.TabPages.Add(sourceTab); p.Controls.Add(tabs, 0, 2);
            listStats = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Muted, Padding = new Padding(14, 6, 0, 0) }; p.Controls.Add(listStats, 0, 3); host.Controls.Add(p);
        }

        void BuildProperties(Control host)
        {
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(14, 8, 14, 10) };
            var p = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 0, Padding = Padding.Empty };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddRow(p, new Label { Text = "分组属性", AutoSize = true, Font = Ui.Font(10, FontStyle.Bold), Margin = new Padding(0, 3, 0, 7) });
            propInfo = new Label { AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(330, 0) }; AddRow(p, propInfo);
            caption = Property(p, "标题 · caption"); description = Property(p, "说明 · description");
            var two = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 }; two.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); two.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            two.Controls.Add(Ui.Label("排序 · sortOn"), 0, 0); two.Controls.Add(Ui.Label("序号 · id"), 1, 0);
            sort = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }; sort.Items.AddRange(new object[] { "none", "name", "description", "id", "timeStamp" }); two.Controls.Add(sort, 0, 1);
            groupId = new TextBox { Dock = DockStyle.Fill }; two.Controls.Add(groupId, 1, 1); AddRow(p, two);
            var checks = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 4, 0, 0) }; descending = new CheckBox { Text = "倒序", AutoSize = true }; insensitive = new CheckBox { Text = "忽略大小写", AutoSize = true }; checks.Controls.Add(descending); checks.Controls.Add(insensitive); AddRow(p, checks);
            mascot = Property(p, "代表零件 · mascot"); picture = Property(p, "图片路径 · picture（相对 .pbg）");
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 7, 0, 4) };
            apply = Ui.Button("应用属性", delegate { CommitProperties(true); }); buttons.Controls.Add(apply); buttons.Controls.Add(Ui.Button("选中项作图标", delegate { SetMascot(); })); AddRow(p, buttons);
            AddRow(p, new Label { Text = "零件预览", Font = Ui.Font(10, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 16, 0, 4) });
            var quick = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 }; quick.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); quick.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            quickPart = new TextBox { Dock = DockStyle.Fill }; quick.Controls.Add(quickPart, 0, 0); quick.Controls.Add(Ui.Button("预览", delegate { PreviewReference(quickPart.Text); }), 1, 0); AddRow(p, quick);
            quickPart.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { PreviewReference(quickPart.Text); e.SuppressKeyPress = true; } };
            preview = new PictureBox { Height = 145, Dock = DockStyle.Top, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Ui.Surface, Margin = new Padding(0, 6, 0, 0) }; AddRow(p, preview);
            previewInfo = new Label { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 7, 0, 0), MaximumSize = new Size(320, 0), Text = "选中零件或输入编号查看真实缩略图。" }; AddRow(p, previewInfo);
            AddRow(p, new Label { Text = "预览使用 LDView 默认材质；颜色 / 数量参数按原文保存。", AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(320, 0), Margin = new Padding(0, 12, 0, 0) });
            foreach (var t in new[] { caption, description, mascot, picture, groupId }) t.TextChanged += delegate { PropertyChanged(); };
            sort.SelectedIndexChanged += delegate { PropertyChanged(); }; descending.CheckedChanged += delegate { PropertyChanged(); }; insensitive.CheckedChanged += delegate { PropertyChanged(); };
            scroller.Controls.Add(p); host.Controls.Add(scroller);
        }
        static void AddRow(TableLayoutPanel p, Control c) { p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.Controls.Add(c, 0, p.RowCount++); }
        TextBox Property(TableLayoutPanel p, string label) { var l = Ui.Label(label); l.Margin = new Padding(0, 6, 0, 3); AddRow(p, l); var t = new TextBox { Dock = DockStyle.Top, Margin = new Padding(0, 1, 0, 2) }; AddRow(p, t); return t; }

        void InitializeProject()
        {
            if (safeMode) { RefreshAll(); Status("安全启动：已跳过最近项目、图标和自动扫描；原有设置保持不变。可手动打开 .pbg。"); StartupLog.Write("SAFE READY", "Skipped recent project and automatic library scan."); return; }
            StartupLog.Write("OPEN PROJECT", "Initializing default or recent project.");
            string path = initialProject;
            if (String.IsNullOrEmpty(path) && smokePath == null && File.Exists(settings.LastProject)) path = settings.LastProject;
            if (String.IsNullOrEmpty(path)) path = Path.Combine(baseDir, "examples", "MyParts", "root.pbg");
            if (File.Exists(path)) OpenProject(path, false); else { RefreshAll(); Status("打开一个 .pbg 文件，或新建自己的零件框项目。"); }
            if (!String.IsNullOrEmpty(settings.LoadWarning)) Status(settings.LoadWarning);
            if (smokePath == null && !String.IsNullOrEmpty(settings.LDrawRoot)) ScanLibrary();
            if (smokePath != null)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 600 };
                timer.Tick += delegate { timer.Stop(); try { if (workspace != null) SelectPath(Path.Combine(workspace.RootDirectory, "bricks.pbg")); previewDelay.Stop(); PreviewSelected(); Refresh(); Application.DoEvents(); using (var bmp = new Bitmap(Width, Height)) { using (var graphics = Graphics.FromImage(bmp)) graphics.CopyFromScreen(Location, Point.Empty, Size); bmp.Save(smokePath, ImageFormat.Png); } } finally { timer.Dispose(); closing = true; Close(); } };
                timer.Start();
            }
        }

        void Status(string text) { status.Text = text; }
        void PropertyChanged() { if (filling || current == null || generated) return; propertyDirty = true; apply.Enabled = true; UpdateCommands(); }
        bool CanEditItems() { return current != null && current.CanEditItems && !generated; }

        void RefreshAll()
        {
            RefreshTree(); FillProperties(); RefreshGrid(); UpdateCommands();
        }
        void RefreshTree()
        {
            string selectedPath = current == null ? null : current.Path;
            refreshingTree = true; tree.BeginUpdate(); tree.Nodes.Clear(); treeCount = 0;
            try
            {
                if (workspace == null) return;
                var root = BuildNode(workspace.Root, null, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
                tree.Nodes.Add(root); root.Expand();
                TreeNode selected = FindNode(root, selectedPath) ?? root; tree.SelectedNode = selected;
                for (TreeNode n = selected.Parent; n != null; n = n.Parent) n.Expand();
                var info = selected.Tag as TreeInfo; if (info != null) { current = info.Document; generated = info.Generated; }
            }
            finally { tree.EndUpdate(); refreshingTree = false; }
        }
        TreeNode BuildNode(PbgDocument doc, GroupLink link, bool inheritedGenerated, HashSet<string> ancestors, int depth)
        {
            bool readOnly = inheritedGenerated || (link != null && link.Generated);
            var node = new TreeNode(doc.Caption + (doc.IsDirty ? " *" : "")) { Tag = new TreeInfo { Document = doc, Link = link, Generated = readOnly }, ToolTipText = doc.Path + "\r\nkind=" + doc.Kind };
            treeCount++;
            if (ancestors.Contains(doc.Path)) { node.Text += "  ↻ 循环引用"; node.ForeColor = Color.DarkOrange; return node; }
            if (depth >= 32 || treeCount >= 2500) { node.Nodes.Add("分支过深或过大，请单独打开此文件"); return node; }
            node.Nodes.Add(new TreeNode("[options] 属性") { Tag = new TreeInfo { Document = doc, Link = link, Options = true, Generated = readOnly }, ForeColor = Ui.Muted });
            var next = new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase); next.Add(doc.Path);
            foreach (var child in workspace.Children(doc))
            {
                if (treeCount >= 2500) { node.Nodes.Add("更多分组请单独打开"); break; }
                if (child.Document == null) node.Nodes.Add(new TreeNode(child.Reference + " · 缺失") { ForeColor = Color.Firebrick, ToolTipText = child.Error, Tag = new TreeInfo { Link = child, Generated = readOnly || child.Generated } });
                else node.Nodes.Add(BuildNode(child.Document, child, readOnly, next, depth + 1));
            }
            return node;
        }
        TreeNode FindNode(TreeNode root, string path)
        {
            if (path == null) return null;
            var info = root.Tag as TreeInfo;
            if (info != null && !info.Options && info.Document != null && String.Equals(info.Document.Path, path, StringComparison.OrdinalIgnoreCase)) return root;
            foreach (TreeNode child in root.Nodes) { var n = FindNode(child, path); if (n != null) return n; } return null;
        }
        void SelectPath(string path)
        {
            if (tree.Nodes.Count == 0) return;
            var node = FindNode(tree.Nodes[0], path);
            if (node == null) { RefreshTree(); node = FindNode(tree.Nodes[0], path); }
            if (node == null) { Status("未在当前分组树中找到此引用。"); return; }
            tree.SelectedNode = node; node.EnsureVisible();
        }
        void TreeSelected(object sender, TreeViewEventArgs e)
        {
            if (refreshingTree) return;
            var info = e.Node.Tag as TreeInfo;
            if (info == null || info.Document == null) { current = null; generated = true; propertyDirty = false; FillProperties(); RefreshGrid(); UpdateCommands(); Status(info != null && info.Link != null ? info.Link.Error : "无法打开此分组。"); return; }
            current = info.Document; generated = info.Generated; propertyDirty = false; FillProperties(); RefreshGrid(); UpdateCommands();
        }
        void FillProperties()
        {
            filling = true;
            try
            {
                bool enabled = current != null && !generated;
                foreach (Control c in new Control[] { caption, description, mascot, picture, groupId, sort, descending, insensitive }) c.Enabled = enabled;
                caption.Text = current == null ? "" : current.Caption; description.Text = current == null ? "" : current.GetOption("description"); mascot.Text = current == null ? "" : current.GetOption("mascot"); picture.Text = current == null ? "" : current.GetOption("picture"); groupId.Text = current == null ? "" : current.GetOption("id");
                string s = current == null ? "none" : current.GetOption("sortOn", "name"); if (!sort.Items.Contains(s)) sort.Items.Add(s); sort.SelectedItem = s;
                descending.Checked = current != null && current.GetOption("sortDesc", "false").Equals("true", StringComparison.OrdinalIgnoreCase); insensitive.Checked = current == null || current.GetOption("sortCaseInSens", "true").Equals("true", StringComparison.OrdinalIgnoreCase);
                propInfo.Text = current == null ? "选择一个分组开始编辑" : (generated ? "自动生成文件 · 只读" : "kind=" + current.Kind + (current.CanEditItems ? " · 静态分组" : " · 动态分组，项目列表只读"));
                propertyDirty = false; apply.Enabled = false;
            }
            finally { filling = false; }
        }

        bool CommitProperties(bool repaint)
        {
            if (!propertyDirty || current == null || generated) return true;
            int id;
            if (groupId.Text.Length > 0 && !Int32.TryParse(groupId.Text, out id)) { MessageBox.Show(this, "序号 id 请输入整数，或留空。", "分组属性"); return false; }
            WorkspaceSnapshot snapshot = workspace.Snapshot();
            try
            {
                SetIfChanged("caption", caption.Text, current.Caption); SetIfChanged("description", description.Text, current.GetOption("description")); SetIfChanged("mascot", mascot.Text.Trim(), current.GetOption("mascot")); SetIfChanged("picture", picture.Text.Trim(), current.GetOption("picture")); SetIfChanged("id", groupId.Text.Trim(), current.GetOption("id"));
                SetIfChanged("sortOn", sort.Text, current.GetOption("sortOn", "name")); SetIfChanged("sortDesc", descending.Checked ? "true" : "false", current.GetOption("sortDesc", "false")); SetIfChanged("sortCaseInSens", insensitive.Checked ? "true" : "false", current.GetOption("sortCaseInSens", "true"));
                PushUndo(snapshot); propertyDirty = false;
                if (repaint) RefreshAll(); else { UpdateTreeLabels(tree.Nodes); UpdateCommands(); apply.Enabled = false; }
                return true;
            }
            catch (Exception ex) { workspace.Restore(snapshot); RefreshTree(); RefreshGrid(); propertyDirty = true; apply.Enabled = true; Ui.Error(this, ex); return false; }
        }
        void SetIfChanged(string key, string value, string previous) { if (!String.Equals(value, previous, StringComparison.Ordinal)) current.SetOption(key, value); }

        void UpdateTreeLabels(TreeNodeCollection nodes)
        {
            foreach (TreeNode node in nodes)
            {
                var info = node.Tag as TreeInfo;
                if (info != null && info.Document != null && !info.Options)
                    node.Text = info.Document.Caption + (info.Document.IsDirty ? " *" : "") + (node.Text.Contains("↻") ? "  ↻ 循环引用" : "");
                UpdateTreeLabels(node.Nodes);
            }
        }

        void RefreshGrid()
        {
            if (grid == null) return;
            int previousLine = SelectedItems().Count == 1 ? SelectedItems()[0].LineId : -1;
            filling = true; grid.SuspendLayout(); grid.Rows.Clear();
            try
            {
                if (current == null) { groupTitle.Text = workspace == null ? "打开 .pbg 开始编辑" : "未选择可用分组"; groupInfo.Text = workspace == null ? "也可以从“文件”菜单打开示例项目。" : "请选择有效分组，或移除缺失引用。"; source.Text = ""; sourceInfo.Text = ""; listStats.Text = ""; return; }
                groupTitle.Text = current.Caption; groupInfo.Text = Path.GetFileName(current.Path) + "  ·  " + current.Kind + (generated ? "  ·  自动生成，只读" : "");
                int missing = 0, partCount = 0;
                foreach (var item in current.Items)
                {
                    if (search.Text.Length > 0 && item.Raw.IndexOf(search.Text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string kind = item.Kind == PbgItemKind.Part ? "零件" : item.Kind == PbgItemKind.GroupReference ? "子分组" : "其他";
                    string state = ItemState(item); bool isMissing = state.StartsWith("缺失") || state.StartsWith("引用无效"); if (isMissing) missing++; if (item.Kind == PbgItemKind.Part) partCount++;
                    int row = grid.Rows.Add(item.Index + 1, item.Reference, kind, state, item.GetParameter("color"), item.GetParameter("count")); grid.Rows[row].Tag = item; grid.Rows[row].Cells[1].ToolTipText = item.Raw;
                    if (isMissing) grid.Rows[row].DefaultCellStyle.ForeColor = Color.Firebrick;
                    else if (item.Kind == PbgItemKind.GroupReference) grid.Rows[row].DefaultCellStyle.ForeColor = Ui.Accent;
                }
                source.Text = current.GetText(); sourceInfo.Text = current.EncodingName + " · " + (current.NewLine == "\r\n" ? "CRLF" : current.NewLine == "\n" ? "LF" : "CR") + "（逐行保留）\r\n此视图只读；未知配置和动态规则会原样保留。";
                listStats.Text = "显示 " + grid.Rows.Count + " / " + current.Items.Count + " 项 · 零件 " + partCount + " · 缺失 " + missing;
                grid.ClearSelection();
                if (previousLine > 0) foreach (DataGridViewRow row in grid.Rows) if (((PbgItem)row.Tag).LineId == previousLine) { row.Selected = true; grid.CurrentCell = row.Cells[1]; break; }
                if (grid.SelectedRows.Count == 0 && grid.Rows.Count > 0) { grid.Rows[0].Selected = true; grid.CurrentCell = grid.Rows[0].Cells[1]; }
            }
            finally { grid.ResumeLayout(); filling = false; QueuePreview(); }
        }
        string ItemState(PbgItem item)
        {
            if (item.Kind == PbgItemKind.GroupReference)
            {
                try { string path = workspace.ResolveGroup(item.Reference); return workspace.Find(path) != null || File.Exists(path) ? "已连接" : "缺失分组"; } catch { return "引用无效"; }
            }
            if (item.Kind != PbgItemKind.Part) return "原样保留";
            if (item.GetParameter("sourceInv").Equals("templates", StringComparison.OrdinalIgnoreCase)) return "模板库 · 未检查";
            if (scanning) return "扫描中…";
            return library == null ? "未配置零件库" : library.Find(item.Reference) == null ? "缺失零件" : "已找到";
        }
        List<PbgItem> SelectedItems() { return grid == null ? new List<PbgItem>() : grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag as PbgItem).Where(i => i != null).OrderBy(i => i.Index).ToList(); }
        void UpdateCommands()
        {
            bool has = workspace != null; bool editable = CanEditItems();
            save.Enabled = has && (workspace.IsDirty || propertyDirty); undoButton.Enabled = undo.Count > 0; redoButton.Enabled = redo.Count > 0;
            foreach (Button b in new[] { add, edit, remove, import, up, down, newGroup }) b.Enabled = editable;
            workspaceTitle.Text = has ? workspace.Root.Caption + "  /  PBG Studio" : "PBG Studio";
            workspacePath.Text = has ? workspace.Root.Path : "LDCad 零件框配置编辑器";
            Text = (has && (workspace.IsDirty || propertyDirty) ? "* " : "") + "PBG Studio · " + (has ? workspace.Root.Caption : "LDCad 零件框编辑器");
        }

        void PushUndo(WorkspaceSnapshot snapshot)
        {
            undo.Push(snapshot); redo.Clear();
            if (undo.Count > 50) { var keep = undo.Take(50).Reverse().ToArray(); undo.Clear(); foreach (var s in keep) undo.Push(s); }
        }
        void Change(Action change, string message)
        {
            if (workspace == null || !CommitProperties(false)) return;
            WorkspaceSnapshot before = workspace.Snapshot();
            try { change(); PushUndo(before); RefreshAll(); Status(message); }
            catch (Exception ex) { workspace.Restore(before); RefreshAll(); Ui.Error(this, ex); }
        }
        void Undo()
        {
            if (workspace == null || !CommitProperties(false) || undo.Count == 0) return;
            redo.Push(workspace.Snapshot()); workspace.Restore(undo.Pop()); propertyDirty = false; RefreshAll(); Status("已撤销。修改仍需保存。 ");
        }
        void Redo()
        {
            if (workspace == null || !CommitProperties(false) || redo.Count == 0) return;
            undo.Push(workspace.Snapshot()); workspace.Restore(redo.Pop()); propertyDirty = false; RefreshAll(); Status("已重做。");
        }

        void PickProject() { using (var d = new OpenFileDialog { Filter = "LDCad 零件框|*.pbg", Title = "选择 root.pbg 或任意分组 .pbg", CheckFileExists = true }) if (d.ShowDialog(this) == DialogResult.OK) OpenProject(d.FileName, true); }
        void OpenProject(string path, bool confirm)
        {
            if (confirm && !ConfirmUnsaved()) return;
            try
            {
                var opened = PbgWorkspace.Open(path); workspace = opened; current = opened.Root; generated = false; propertyDirty = false; undo.Clear(); redo.Clear(); search.Text = "";
                settings.LastProject = path; RefreshAll();
                Status(path.StartsWith(Path.Combine(baseDir, "examples"), StringComparison.OrdinalIgnoreCase) ? "当前为附带示例项目。真实缩略图需在“路径设置”选择 LDraw 与 LDView。" : "已打开。修改现有配置前请关闭 LDCad；保存后重新打开 LDCad 读取。 ");
            }
            catch (Exception ex) { Ui.Error(this, ex); }
        }
        void NewProject()
        {
            if (!ConfirmUnsaved()) return;
            using (var d = new SaveFileDialog { Title = "在新的自定义零件框文件夹中创建 root.pbg", FileName = "root.pbg", Filter = "LDCad 零件框|*.pbg", OverwritePrompt = false })
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    try { workspace = PbgWorkspace.Create(d.FileName, "我的零件框"); current = workspace.Root; generated = false; propertyDirty = false; undo.Clear(); redo.Clear(); RefreshAll(); Status("新建项目尚未写入磁盘。请添加零件或子分组后保存。"); }
                    catch (Exception ex) { Ui.Error(this, ex); }
                }
        }
        bool SaveAll()
        {
            if (workspace == null) return true;
            if (!CommitProperties(false)) return false;
            try { var saved = workspace.SaveAll(); settings.LastProject = workspace.Root.Path; if (!safeMode) settings.Save(dataDir); RefreshAll(); Status(saved.Count == 0 ? "没有需要保存的修改。" : "已保存 " + saved.Count + " 个文件。修改前的原件已备份为同目录 .bak 文件。请重启 LDCad 读取。"); return true; }
            catch (Exception ex) { RefreshAll(); Ui.Error(this, ex); return false; }
        }
        bool ConfirmUnsaved()
        {
            if (workspace == null || (!workspace.IsDirty && !propertyDirty)) return true;
            var answer = MessageBox.Show(this, "有尚未保存的修改。是否保存？", "保存修改", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return false;
            return answer != DialogResult.Yes || SaveAll();
        }
        void ReloadProject() { if (workspace != null) OpenProject(workspace.Root.Path, true); }

        void AddGroup()
        {
            if (!CanEditItems()) { Status("请选择可编辑的 basic 静态分组。"); return; }
            string name = "group01"; int n = 1; while (File.Exists(workspace.ResolveGroup(name)) || workspace.Find(workspace.ResolveGroup(name)) != null) name = "group" + (++n).ToString("00");
            using (var d = new NewGroupDialog(name)) if (d.ShowDialog(this) == DialogResult.OK)
            {
                PbgDocument added = null;
                Change(delegate { added = workspace.AddGroup(current, d.ReferenceValue, d.CaptionValue); }, "已新建子分组。父分组 sortOn 已设为 none，保存后使用手动顺序。");
                if (added != null) SelectPath(added.Path);
            }
        }
        void RenameGroup()
        {
            if (current == null || generated) return;
            using (var d = new TextDialog("重命名分组", "修改显示标题 caption，文件引用保持稳定。", current.Caption, false))
                if (d.ShowDialog(this) == DialogResult.OK) Change(delegate { current.SetOption("caption", d.Value); }, "分组标题已修改。");
        }
        void AddGroupReference()
        {
            if (!CanEditItems()) { Status("请选择 basic 静态分组后添加引用。"); return; }
            using (var d = new TextDialog("引用已有分组", "填写相对 root.pbg 的引用，不带扩展名，例如 bricks 或 sorted/bricks。", "", false))
                if (d.ShowDialog(this) == DialogResult.OK)
                    Change(delegate {
                        string reference = d.Value.Trim().Replace('/', '\\'); string path = workspace.ResolveGroup(reference);
                        if (workspace.Find(path) == null && !File.Exists(path)) throw new FileNotFoundException("该分组文件不存在，请使用新建子分组。");
                        current.InsertItem(reference); current.SetOption("sortOn", "none");
                    }, "已加入现有分组引用，sortOn 已设为 none。");
        }
        void RemoveGroup()
        {
            var info = tree.SelectedNode == null ? null : tree.SelectedNode.Tag as TreeInfo;
            if (info == null || info.Link == null || info.Link.LineId < 0 || info.Generated || !info.Link.Parent.CanEditItems) { Status("请选择 basic 父分组下的一个子分组引用；根分组不能移除。"); return; }
            if (MessageBox.Show(this, "从父分组移除“" + info.Link.Reference + "”的引用？\r\n对应 .pbg 文件保留，可通过撤销恢复引用。", "移除分组引用", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            var parent = info.Link.Parent; int line = info.Link.LineId;
            Change(delegate { parent.RemoveItems(new[] { line }); current = parent; }, "已移除分组引用，原 .pbg 文件保留。");
        }
        void AddPart()
        {
            if (!CanEditItems()) return;
            using (var d = new TextDialog("添加零件", "输入 LDraw 零件号，例如 3001 或 3001.dat。多项请使用批量导入。", "", false))
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    string error; string part = ImportService.NormalizePartId(d.Value, out error);
                    if (!String.IsNullOrEmpty(error)) { MessageBox.Show(this, error); return; }
                    Change(delegate { current.InsertItem(part); }, "已添加 " + part + "。 ");
                }
        }
        void EditItems()
        {
            if (!CanEditItems()) return;
            var items = SelectedItems(); if (items.Count == 0) return;
            using (var d = new TextDialog("编辑选中项目", "每行对应一个选中项目，行数保持不变。保留需要的 :[color=…][count=…] 参数。", String.Join("\r\n", items.Select(i => i.Raw)), true))
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    string[] lines = d.Value.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
                    if (lines.Length != items.Count) { MessageBox.Show(this, "行数必须与选中项目数量一致。新增项目请使用批量导入。"); return; }
                    Change(delegate { for (int i = 0; i < items.Count; i++) { ValidateEditedItem(lines[i]); current.UpdateItem(items[i].LineId, lines[i]); } }, "已更新 " + items.Count + " 个项目。");
                }
        }
        void ValidateEditedItem(string raw)
        {
            var item = PbgItem.Parse(raw);
            if (item.Kind == PbgItemKind.GroupReference) workspace.ResolveGroup(item.Reference);
            else if (item.Kind == PbgItemKind.Part)
            {
                string error; ImportService.NormalizePartId(item.Reference, out error); if (!String.IsNullOrEmpty(error)) throw new InvalidDataException(error);
            }
            else throw new InvalidDataException("请输入有效 .dat / .ldr 零件，或不带扩展名的子分组引用。");
        }
        void RemoveItems()
        {
            if (!CanEditItems()) return; var items = SelectedItems(); if (items.Count == 0) return;
            if (items.Count > 1 && MessageBox.Show(this, "删除选中的 " + items.Count + " 个项目？可通过撤销恢复。", "批量删除", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            Change(delegate { current.RemoveItems(items.Select(i => i.LineId)); }, "已删除 " + items.Count + " 个项目。");
        }
        void MoveSelected(int delta)
        {
            if (!CanEditItems()) return;
            if (search.Text.Length > 0) { Status("清空筛选后再排序，避免隐藏项目造成位置歧义。"); return; }
            var selected = SelectedItems(); if (selected.Count == 0) return;
            if (delta < 0 && selected[0].Index == 0 || delta > 0 && selected[selected.Count - 1].Index == current.Items.Count - 1) return;
            int[] ids = selected.Select(i => i.LineId).ToArray();
            Change(delegate { foreach (var item in delta < 0 ? selected : selected.AsEnumerable().Reverse()) { var live = current.Items.First(i => i.LineId == item.LineId); current.MoveItem(item.LineId, live.Index + delta); } current.SetOption("sortOn", "none"); }, "已调整顺序，sortOn 已设为 none。");
            filling = true; grid.ClearSelection(); foreach (DataGridViewRow row in grid.Rows) if (ids.Contains(((PbgItem)row.Tag).LineId)) row.Selected = true; filling = false; QueuePreview();
        }
        void GridDrop(object sender, DragEventArgs e)
        {
            if (!CanEditItems() || search.Text.Length > 0 || dragRow < 0 || dragRow >= grid.Rows.Count) return;
            if (!e.Data.GetDataPresent(DataFormats.Text) || (string)e.Data.GetData(DataFormats.Text) != "PBG_ITEM:" + dragRow) return;
            Point point = grid.PointToClient(new Point(e.X, e.Y)); int target = grid.HitTest(point.X, point.Y).RowIndex;
            if (target < 0 || target == dragRow) return;
            var item = grid.Rows[dragRow].Tag as PbgItem; var other = grid.Rows[target].Tag as PbgItem; dragRow = -1;
            Change(delegate { current.MoveItem(item.LineId, other.Index); current.SetOption("sortOn", "none"); }, "已拖动排序，sortOn 已设为 none。");
        }
        void TreeDragOver(object sender, DragEventArgs e)
        {
            if (dragNode == null || !e.Data.GetDataPresent(typeof(TreeNode)) || !Object.ReferenceEquals(e.Data.GetData(typeof(TreeNode)), dragNode)) return;
            var target = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y))); var a = dragNode == null ? null : dragNode.Tag as TreeInfo; var b = target == null ? null : target.Tag as TreeInfo;
            if (a != null && b != null && a.Link != null && b.Link != null && !a.Options && !b.Options && !a.Generated && !b.Generated && a.Link.Parent == b.Link.Parent && a.Link.LineId >= 0 && b.Link.LineId >= 0 && a.Link.Parent.CanEditItems) e.Effect = DragDropEffects.Move;
        }
        void TreeDrop(object sender, DragEventArgs e)
        {
            if (dragNode == null || !e.Data.GetDataPresent(typeof(TreeNode)) || !Object.ReferenceEquals(e.Data.GetData(typeof(TreeNode)), dragNode)) return;
            var target = tree.GetNodeAt(tree.PointToClient(new Point(e.X, e.Y))); var a = dragNode == null ? null : dragNode.Tag as TreeInfo; var b = target == null ? null : target.Tag as TreeInfo;
            if (a == null || b == null || a.Link == null || b.Link == null || a.Options || b.Options || a.Generated || b.Generated || a.Link.Parent != b.Link.Parent || a.Link.LineId < 0 || b.Link.LineId < 0 || a.Link.LineId == b.Link.LineId || !a.Link.Parent.CanEditItems) return;
            var doc = a.Link.Parent;
            Change(delegate { int index = doc.Items.First(i => i.LineId == b.Link.LineId).Index; doc.MoveItem(a.Link.LineId, index); doc.SetOption("sortOn", "none"); }, "已调整同级分组顺序，父分组 sortOn 已设为 none。"); dragNode = null;
        }
        void ImportItems(bool clipboard)
        {
            if (!CanEditItems()) { Status("请选择 basic 静态分组后导入。"); return; }
            string text = ""; if (clipboard) { try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch (Exception ex) { Ui.Error(this, ex); return; } }
            using (var d = new ImportDialog(text)) if (d.ShowDialog(this) == DialogResult.OK)
            {
                int added = 0, skipped = 0;
                Change(delegate {
                    var existing = new HashSet<string>(current.Items.Select(i => i.Raw.Trim()), StringComparer.OrdinalIgnoreCase);
                    foreach (string raw in d.ItemsToImport) { if (d.SkipExisting && existing.Contains(raw.Trim())) { skipped++; continue; } current.InsertItem(raw); existing.Add(raw.Trim()); added++; }
                }, "导入完成。");
                Status("已导入 " + added + " 项，跳过重复 " + skipped + " 项。缺失警告以当前本地 LDraw 库为准。");
            }
        }
        void CopyItems() { var items = SelectedItems(); if (items.Count > 0) Clipboard.SetText(String.Join(Environment.NewLine, items.Select(i => i.Raw))); }
        void ExportParts()
        {
            if (current == null) return;
            using (var d = new SaveFileDialog { Filter = "纯文本零件列表|*.txt", FileName = Path.GetFileNameWithoutExtension(current.Path) + "-parts.txt" })
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    try { var parts = current.Items.Where(i => i.Kind == PbgItemKind.Part).Select(i => i.Reference).ToArray(); File.WriteAllText(d.FileName, String.Join("\r\n", parts) + (parts.Length > 0 ? "\r\n" : ""), new System.Text.UTF8Encoding(false)); Status("已导出 " + parts.Length + " 个零件编号，包含当前分组全部零件，不受筛选影响。"); }
                    catch (Exception ex) { Ui.Error(this, ex); }
                }
        }
        void SetMascot()
        {
            if (current == null || generated) return; var items = SelectedItems(); if (items.Count != 1) { Status("请选择一个项目作为代表图标。"); return; }
            mascot.Text = items[0].Reference; picture.Text = ""; CommitProperties(true);
        }

        void Configure()
        {
            using (var d = new SettingsDialog(settings)) if (d.ShowDialog(this) == DialogResult.OK)
            {
                settings.LDrawRoot = d.LDrawRoot; settings.LDViewPath = d.LDViewPath;
                try { settings.Save(dataDir); ScanLibrary(); } catch (Exception ex) { Ui.Error(this, ex); }
            }
        }
        async void ScanLibrary()
        {
            int generation = ++scanGeneration; CancelPreview();
            if (String.IsNullOrWhiteSpace(settings.LDrawRoot)) { library = null; scanning = false; libraryStatus.Text = "LDraw 库未配置"; RefreshGrid(); Status("请先在路径设置中选择已解压的 LDraw 根目录。"); return; }
            scanning = true; libraryStatus.Text = "正在扫描 LDraw…"; Status("正在索引零件与依赖文件，可继续编辑。扫描完成后更新缺失提示。"); RefreshGrid();
            try
            {
                var next = new PartLibrary(settings.LDrawRoot); await Task.Run(delegate { next.Scan(); });
                if (closing || generation != scanGeneration) return;
                library = next; scanning = false; libraryStatus.Text = "LDraw 已索引 " + library.Count.ToString("N0") + " 个文件";
                RefreshGrid(); Status(next.Warnings.Count > 0 ? "库扫描完成；" + String.Join("；", next.Warnings.Take(3)) : "零件库扫描完成，缺失项目已标红。库变动后请重新扫描。");
            }
            catch (Exception ex) { if (closing || generation != scanGeneration) return; scanning = false; library = null; libraryStatus.Text = "零件库扫描失败"; RefreshGrid(); Ui.Error(this, ex); }
        }
        void CancelPreview() { previewGeneration++; if (previewCancel != null) { previewCancel.Cancel(); previewCancel.Dispose(); previewCancel = null; } }
        void ClearPreview(string message) { if (preview.Image != null) { var old = preview.Image; preview.Image = null; old.Dispose(); } previewInfo.Text = message; }
        void QueuePreview() { previewDelay.Stop(); CancelPreview(); ClearPreview("选中零件或输入编号查看真实缩略图。"); previewDelay.Start(); }
        void PreviewSelected()
        {
            var selected = SelectedItems();
            if (selected.Count == 1 && selected[0].Kind == PbgItemKind.Part)
            {
                if (selected[0].GetParameter("sourceInv").Equals("templates", StringComparison.OrdinalIgnoreCase)) { CancelPreview(); ClearPreview("此项目来自 LDCad 模板库，当前 LDraw 扫描未包含模板库。"); return; }
                quickPart.Text = selected[0].Reference; PreviewReference(selected[0].Reference);
            }
            else { CancelPreview(); ClearPreview(selected.Count > 1 ? "已选择 " + selected.Count + " 个项目。选中一个零件查看预览。" : "选中零件或输入 .dat 编号查看真实缩略图。"); }
        }
        async void PreviewReference(string input)
        {
            previewDelay.Stop();
            CancelPreview(); int generation = previewGeneration;
            string error; string part = ImportService.NormalizePartId(input, out error);
            if (!String.IsNullOrEmpty(error)) { ClearPreview(error); return; }
            if (scanning) { ClearPreview("等待零件库扫描完成…"); return; }
            if (library == null) { ClearPreview("尚未配置 LDraw 库。打开“路径设置”选择库目录与 LDView.exe。"); return; }
            string file = library.Find(part); if (file == null) { ClearPreview("本地 LDraw 库中未找到 " + part + "。若刚更新过库，请重新扫描。"); return; }
            if (String.IsNullOrEmpty(settings.LDViewPath) || !File.Exists(settings.LDViewPath)) { ClearPreview("已找到 " + part + "。请在路径设置中选择 LDView.exe 生成缩略图。"); return; }
            ClearPreview("正在使用 LDView 渲染 " + part + "…"); previewCancel = new CancellationTokenSource(); var token = previewCancel.Token;
            try
            {
                var result = await thumbnails.RenderAsync(file, library, settings.LDViewPath, token);
                if (closing || generation != previewGeneration || token.IsCancellationRequested) return;
                if (!result.Success) { ClearPreview(result.Error); return; }
                using (var img = Image.FromFile(result.ImagePath)) preview.Image = new Bitmap(img);
                previewInfo.Text = part + (result.WasCached ? " · 已缓存" : " · LDView 渲染完成");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closing && generation == previewGeneration) ClearPreview(ex.Message); }
        }

        void ShowDiagnostics()
        {
            if (workspace == null || !CommitProperties(false)) return; RefreshTree();
            var lines = new List<string>(); int parts = 0, groups = 0, missing = 0;
            foreach (var doc in workspace.Documents.ToArray())
            {
                if (!doc.CanEditItems) continue;
                foreach (var item in doc.Items)
                {
                    if (item.Kind == PbgItemKind.Part) parts++; else if (item.Kind == PbgItemKind.GroupReference) groups++;
                    string state = ItemState(item);
                    if (state.StartsWith("缺失") || state.StartsWith("引用无效")) { missing++; lines.Add(Path.GetFileName(doc.Path) + " · " + item.Reference + " · " + state); }
                }
            }
            string header = "已加载分组 " + workspace.Documents.Count() + " 个；静态零件项 " + parts + "；分组引用 " + groups + "；发现问题 " + missing + "。\r\n";
            if (library == null) header += "零件库未配置：这里只能检查分组引用，零件存在性尚未检查。\r\n";
            header += "动态组规则、模板库、零件内部依赖不在此次完整性检查范围。\r\n\r\n";
            using (var dialog = new TextDialog("引用检查结果", "检查当前已加载的零件框分支", header + String.Join("\r\n", lines.Take(2000)), true)) dialog.ShowDialog(this);
            FillProperties(); UpdateCommands();
        }
        void ShowHelp()
        {
            MessageBox.Show(this, "1. 完整解压后双击 PbgStudio.exe。\r\n2. 打开 LDCad 自定义零件框目录中的 root.pbg。\r\n3. 在“路径设置”选择 LDraw 根目录和 LDView.exe。\r\n4. 左侧管理分组，中间添加 / 编辑 / 导入零件，右侧修改属性。\r\n5. 保存全部后重新打开 LDCad。原文件备份在同目录。\r\n\r\n分组是独立 .pbg；引用相对 root.pbg 且不带扩展名。\r\n拖拽和上移 / 下移会把父分组 sortOn 设为 none。\r\n动态分组只编辑公共属性，生成项目和规则原样保留。\r\n\r\n快捷键：Ctrl+O 打开，Ctrl+S 保存，Ctrl+Z 撤销，Ctrl+Y 重做，Ctrl+Shift+V 粘贴导入，Ctrl+G 新建子分组。\r\n基本用法见软件目录中的 README.md。", "使用说明");
        }
    }
}
