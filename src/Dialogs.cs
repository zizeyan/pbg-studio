// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PbgStudio
{
    internal static class Ui
    {
        internal static readonly Color Ink = Color.FromArgb(32, 45, 65);
        internal static readonly Color Muted = Color.FromArgb(102, 116, 136);
        internal static readonly Color Accent = Color.FromArgb(34, 104, 221);
        internal static readonly Color Surface = Color.FromArgb(244, 247, 251);
        internal static Font Font(float size, FontStyle style) { return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point); }
        internal static Button Button(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(78, 30), Padding = new Padding(8, 2, 8, 2), FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(215, 224, 236);
            if (click != null) b.Click += click;
            return b;
        }
        internal static Label Label(string text) { return new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 8, 0, 5) }; }
        internal static void Error(IWin32Window owner, Exception ex) { MessageBox.Show(owner, ex.Message, "PBG Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        internal static DataGridView Grid()
        {
            var g = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AutoGenerateColumns = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, MultiSelect = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false, ColumnHeadersHeight = 36, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing };
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(228, 238, 254);
            g.DefaultCellStyle.SelectionForeColor = Ink;
            g.DefaultCellStyle.ForeColor = Ink;
            g.DefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
            g.ColumnHeadersDefaultCellStyle.BackColor = Surface;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            g.ColumnHeadersDefaultCellStyle.Font = Font(9, FontStyle.Regular);
            g.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            g.RowTemplate.Height = 32;
            g.GridColor = Color.FromArgb(234, 239, 245);
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            return g;
        }
        internal static void Column(DataGridView g, string name, string header, float weight)
        {
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        }
    }

    internal sealed class TextDialog : Form
    {
        readonly TextBox input;
        internal string Value { get { return input.Text; } }
        internal TextDialog(string title, string label, string value, bool multiline)
        {
            Text = title; Font = Ui.Font(9, FontStyle.Regular); StartPosition = FormStartPosition.CenterParent;
            Size = new Size(640, multiline ? 450 : 210); MinimumSize = Size;
            BackColor = Color.White; MinimizeBox = false; MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 3, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(Ui.Label(label), 0, 0);
            input = new TextBox { Text = value, Multiline = multiline, Dock = multiline ? DockStyle.Fill : DockStyle.Top, ScrollBars = multiline ? ScrollBars.Both : ScrollBars.None, AcceptsReturn = multiline, WordWrap = false };
            layout.Controls.Add(input, 0, 1);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
            var ok = Ui.Button("确定", delegate { DialogResult = DialogResult.OK; });
            var cancel = Ui.Button("取消", delegate { DialogResult = DialogResult.Cancel; });
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 2);
            Controls.Add(layout); CancelButton = cancel; if (!multiline) AcceptButton = ok;
        }
    }

    internal sealed class NewGroupDialog : Form
    {
        TextBox caption, reference;
        internal string CaptionValue { get { return caption.Text.Trim(); } }
        internal string ReferenceValue { get { return reference.Text.Trim(); } }
        internal NewGroupDialog(string defaultReference)
        {
            Text = "新建子分组"; Font = Ui.Font(9, FontStyle.Regular); BackColor = Color.White;
            StartPosition = FormStartPosition.CenterParent; Size = new Size(520, 285); FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = MaximizeBox = false;
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 6, ColumnCount = 1 };
            p.Controls.Add(Ui.Label("分组标题（caption）")); caption = new TextBox { Dock = DockStyle.Top, Text = "新分组" }; p.Controls.Add(caption);
            p.Controls.Add(Ui.Label("文件引用 · 不带 .pbg 扩展名，可用 folders/group")); reference = new TextBox { Dock = DockStyle.Top, Text = defaultReference }; p.Controls.Add(reference);
            p.Controls.Add(Ui.Label("保存时创建独立 .pbg，并加入当前分组的 <items>。"));
            var bar = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var ok = Ui.Button("创建", delegate { if (CaptionValue.Length == 0 || ReferenceValue.Length == 0) { MessageBox.Show(this, "请填写标题和文件引用。"); return; } DialogResult = DialogResult.OK; });
            var cancel = Ui.Button("取消", delegate { DialogResult = DialogResult.Cancel; }); bar.Controls.Add(ok); bar.Controls.Add(cancel); p.Controls.Add(bar);
            Controls.Add(p); AcceptButton = ok; CancelButton = cancel;
        }
    }

    internal sealed class SettingsDialog : Form
    {
        readonly TextBox library, viewer;
        internal string LDrawRoot { get { return library.Text.Trim(); } }
        internal string LDViewPath { get { return viewer.Text.Trim(); } }
        internal SettingsDialog(AppSettings settings)
        {
            Text = "路径设置"; Font = Ui.Font(9, FontStyle.Regular); BackColor = Color.White; StartPosition = FormStartPosition.CenterParent;
            Size = new Size(740, 365); MinimumSize = Size; MinimizeBox = MaximizeBox = false;
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 7, ColumnCount = 2 };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            var l1 = Ui.Label("LDraw 零件库根目录（包含 parts 和 p 文件夹）"); p.Controls.Add(l1, 0, 0); p.SetColumnSpan(l1, 2);
            library = new TextBox { Dock = DockStyle.Fill, Text = settings.LDrawRoot ?? "" }; p.Controls.Add(library, 0, 1);
            p.Controls.Add(Ui.Button("浏览…", delegate { using (var d = new FolderBrowserDialog { Description = "选择已解压的 LDraw 根目录", SelectedPath = library.Text }) if (d.ShowDialog(this) == DialogResult.OK) library.Text = d.SelectedPath; }), 1, 1);
            var l2 = Ui.Label("LDView 渲染程序（选择 LDView.exe）"); p.Controls.Add(l2, 0, 2); p.SetColumnSpan(l2, 2);
            viewer = new TextBox { Dock = DockStyle.Fill, Text = settings.LDViewPath ?? "" }; p.Controls.Add(viewer, 0, 3);
            p.Controls.Add(Ui.Button("浏览…", delegate { using (var d = new OpenFileDialog { Filter = "LDView 程序|LDView.exe;ldview.exe|可执行文件|*.exe", FileName = viewer.Text }) if (d.ShowDialog(this) == DialogResult.OK) viewer.Text = d.FileName; }), 1, 3);
            var note = new Label { Text = "只编辑 .pbg 时可以留空。真实缩略图需要本地 LDView 与 LDraw。\r\n库扫描支持 official / unofficial 目录；压缩包请先解压。设置和缓存保存在程序旁的 data 文件夹。", ForeColor = Ui.Muted, AutoSize = true, Margin = new Padding(0, 14, 0, 14) };
            p.Controls.Add(note, 0, 4); p.SetColumnSpan(note, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var ok = Ui.Button("保存并扫描", delegate {
                if (LDrawRoot.Length > 0 && !Directory.Exists(LDrawRoot)) { MessageBox.Show(this, "LDraw 目录不存在。"); return; }
                if (LDViewPath.Length > 0 && !File.Exists(LDViewPath)) { MessageBox.Show(this, "LDView 程序不存在。"); return; }
                if (Environment.OSVersion.Platform == PlatformID.Win32NT && LDViewPath.Length > 0 && !LDViewPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "请选择 LDView.exe。"); return; }
                DialogResult = DialogResult.OK;
            });
            var cancel = Ui.Button("取消", delegate { DialogResult = DialogResult.Cancel; });
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
            buttons.Controls.Add(Ui.Button("自动查找", delegate { string a = AppSettings.AutoDetectLDraw(), b = AppSettings.AutoDetectLDView(); if (!String.IsNullOrEmpty(a)) library.Text = a; if (!String.IsNullOrEmpty(b)) viewer.Text = b; if (String.IsNullOrEmpty(a) && String.IsNullOrEmpty(b)) MessageBox.Show(this, "未在常见位置找到，请手动选择。"); }));
            p.Controls.Add(buttons, 0, 5); p.SetColumnSpan(buttons, 2); Controls.Add(p); AcceptButton = ok; CancelButton = cancel;
        }
    }

    internal sealed class ImportDialog : Form
    {
        readonly TextBox input;
        readonly ComboBox format, column;
        readonly Label summary;
        readonly CheckBox skipDuplicates;
        readonly DataGridView preview;
        readonly Button accept;
        ImportResult result;
        bool changing;
        internal List<string> ItemsToImport { get; private set; }
        internal bool SkipExisting { get { return skipDuplicates.Checked; } }
        internal ImportDialog(string text)
        {
            Text = "批量导入 · 文本 / CSV / Rebrickable"; Font = Ui.Font(9, FontStyle.Regular); BackColor = Color.White;
            StartPosition = FormStartPosition.CenterParent; Size = new Size(910, 730); MinimumSize = new Size(760, 600);
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 6, ColumnCount = 1 };
            p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.RowStyles.Add(new RowStyle(SizeType.Absolute, 180)); p.RowStyles.Add(new RowStyle(SizeType.AutoSize)); p.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); p.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); p.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var tools = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            tools.Controls.Add(Ui.Label("输入格式"));
            format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 }; format.Items.AddRange(new object[] { "自动识别", "纯文本", "CSV", "TSV" }); format.SelectedIndex = 0; tools.Controls.Add(format);
            tools.Controls.Add(Ui.Label("零件编号列")); column = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 185 }; column.Items.Add("自动选择"); column.SelectedIndex = 0; tools.Controls.Add(column);
            tools.Controls.Add(Ui.Button("读取文件…", delegate { using (var d = new OpenFileDialog { Filter = "导入文件|*.csv;*.tsv;*.txt|所有文件|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) { try { input.Text = ReadImportText(d.FileName); ParsePreview(); } catch (Exception ex) { Ui.Error(this, ex); } } }));
            tools.Controls.Add(Ui.Button("识别预览", delegate { ParsePreview(); })); p.Controls.Add(tools, 0, 0);
            input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, WordWrap = false, Font = new Font("Consolas", 10), Text = text ?? "" }; p.Controls.Add(input, 0, 1);
            p.Controls.Add(Ui.Label("有效记录可导入；编号按本地 LDraw 库核验，未找到时会标红。"), 0, 2);
            preview = Ui.Grid(); Ui.Column(preview, "line", "源行", 12); Ui.Column(preview, "item", "将写入的项目", 42); Ui.Column(preview, "status", "识别结果", 46); p.Controls.Add(preview, 0, 3);
            summary = new Label { Dock = DockStyle.Fill, ForeColor = Ui.Muted, Padding = new Padding(0, 10, 0, 3), AutoEllipsis = true }; p.Controls.Add(summary, 0, 4);
            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            accept = Ui.Button("导入有效记录", delegate { ParsePreview(); if (result == null) return; ItemsToImport = result.Entries.Where(e => e.IsValid && (!skipDuplicates.Checked || !e.IsDuplicate)).Select(e => e.RawItem).ToList(); if (ItemsToImport.Count == 0) { MessageBox.Show(this, "没有可导入的记录。"); return; } DialogResult = DialogResult.OK; });
            bottom.Controls.Add(accept); var cancel = Ui.Button("取消", delegate { DialogResult = DialogResult.Cancel; }); bottom.Controls.Add(cancel);
            skipDuplicates = new CheckBox { AutoSize = true, Text = "跳过文本完全相同的重复项目", Checked = true, Margin = new Padding(5, 10, 16, 3) }; bottom.Controls.Add(skipDuplicates); p.Controls.Add(bottom, 0, 5); Controls.Add(p); CancelButton = cancel;
            input.TextChanged += delegate { ResetColumns(); accept.Enabled = false; summary.Text = "内容已变化，请重新识别预览。"; };
            format.SelectedIndexChanged += delegate { if (!changing) { ResetColumns(); ParsePreview(); } };
            column.SelectedIndexChanged += delegate { if (!changing) ParsePreview(); };
            Shown += delegate { ParsePreview(); };
        }
        internal static string ReadImportText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length > 8 * 1024 * 1024) throw new IOException("导入文件超过 8 MB，请分批处理。");
            return PbgDocument.FromBytes(path, bytes).GetText();
        }
        void ResetColumns() { changing = true; column.Items.Clear(); column.Items.Add("自动选择"); column.SelectedIndex = 0; changing = false; }
        void ParsePreview()
        {
            try
            {
                string mode = new[] { "auto", "text", "csv", "tsv" }[format.SelectedIndex];
                result = ImportService.Parse(input.Text, mode, column.SelectedIndex - 1);
                if (column.Items.Count == 1 && result.Header != null && result.Header.Count > 0)
                {
                    changing = true;
                    for (int i = 0; i < result.Header.Count; i++) column.Items.Add((i + 1) + " · " + result.Header[i]);
                    changing = false;
                }
                preview.Rows.Clear();
                foreach (var e in result.Entries.Take(3000))
                {
                    int index = preview.Rows.Add(e.SourceLine, e.IsValid ? e.RawItem : e.Original, e.IsValid ? (e.IsDuplicate ? "重复记录" : "格式有效 · 尚未核验库") : e.Error);
                    if (!e.IsValid) preview.Rows[index].DefaultCellStyle.ForeColor = Color.Firebrick;
                }
                int valid = result.Entries.Count(e => e.IsValid), bad = result.Entries.Count - valid;
                summary.Text = "有效 " + valid + " 条 · 无效 " + bad + " 条" + (result.Entries.Count > 3000 ? " · 预览仅显示前 3000 条" : "") + "\r\n" + String.Join("\r\n", result.Warnings);
                accept.Enabled = valid > 0;
            }
            catch (Exception ex) { result = null; preview.Rows.Clear(); summary.Text = ex.Message; accept.Enabled = false; changing = false; }
        }
    }
}
