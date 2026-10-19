using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DiskSpace
{
    internal sealed class VisibleEntry
    {
        internal Entry Entry;
        internal int Depth;
        internal VisibleEntry(Entry entry, int depth) { Entry = entry; Depth = depth; }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox pathBox = new TextBox();
        private readonly Button scanButton = new Button(), chooseButton = new Button(), drivesButton = new Button();
        private readonly Button upButton = new Button(), refreshButton = new Button(), copyButton = new Button();
        private readonly Button openButton = new Button(), exportButton = new Button(), issuesButton = new Button();
        private readonly TextBox locationBox = new TextBox();
        private readonly Label sizeLabel = new Label(), filesLabel = new Label(), foldersLabel = new Label();
        private readonly Label statusLabel = new Label(), selectionLabel = new Label();
        private readonly DiskCapacityView capacity = new DiskCapacityView();
        private readonly DataGridView grid = new DataGridView();
        private readonly TreemapView treemap = new TreemapView();
        private readonly TabControl tabs = new TabControl();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly ToolTip tips = new ToolTip();
        private readonly System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer();
        private readonly HashSet<Entry> expanded = new HashSet<Entry>();
        private readonly List<VisibleEntry> visible = new List<VisibleEntry>();
        private Entry scanRoot, current, selected;
        private List<ScanResult> results = new List<ScanResult>();
        private CancellationTokenSource cancellation;
        private volatile ScanProgress pendingProgress;
        private string[] lastPaths;
        private bool busy, changingSelection, closePending, ascending;
        private int sortColumn = 1;
        private readonly string[] startupPaths;

        internal MainForm(string[] args)
        {
            SuspendLayout();
            startupPaths = args;
            Text = "磁盘空间分析器";
            Font = new Font("Microsoft YaHei UI", 9F);
            ForeColor = Display.Ink;
            BackColor = Color.FromArgb(246, 248, 251);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(1120, 720);
            MinimumSize = new Size(860, 580);
            StartPosition = FormStartPosition.CenterScreen;
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DiskSpace.app.ico"))
                using (Icon icon = new Icon(stream)) Icon = (Icon)icon.Clone();
            }
            catch { Icon = SystemIcons.Application; }
            BuildUi();
            WireEvents();
            pathBox.Text = args.Length > 0 ? string.Join(";", args.Select(QuotePath)) : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            SetBusy(false);
            statusLabel.Text = "就绪";
            // Controls are created at 96-DPI design sizes. Scale once only after
            // the complete hierarchy exists (dynamic WinForms layout otherwise
            // consumes the initial autoscale before the children are added).
            float initialDpi;
            using (Graphics graphics = CreateGraphics()) initialDpi = graphics.DpiX;
            if (initialDpi != 96F) Scale(new SizeF(initialDpi / 96F, initialDpi / 96F));
            grid.RowTemplate.Height = (int)Math.Round(36 * initialDpi / 96F);
            grid.ColumnHeadersHeight = grid.RowTemplate.Height;
            int[] columnWidths = { 340, 130, 85, 180, 95, 95 };
            for (int i = 1; i < columnWidths.Length; i++) grid.Columns[i].Width = (int)Math.Round(columnWidths[i] * initialDpi / 96F);
            AutoScaleDimensions = new SizeF(initialDpi, initialDpi);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(true);
            Shown += delegate { if (startupPaths.Length > 0) StartScan(startupPaths); };
        }

        private void BuildUi()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 10), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            Controls.Add(layout);

            var input = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Margin = Padding.Empty };
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            input.Controls.Add(new Label { Text = "路径", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            pathBox.Dock = DockStyle.Fill;
            pathBox.Margin = new Padding(0, 8, 8, 8);
            pathBox.AccessibleName = "扫描路径";
            pathBox.AutoCompleteMode = AutoCompleteMode.None;
            input.Controls.Add(pathBox, 1, 0);
            StyleButton(chooseButton, "", "选择文件夹", "folder"); input.Controls.Add(chooseButton, 2, 0);
            StyleButton(drivesButton, "磁盘", "选择磁盘", null); input.Controls.Add(drivesButton, 3, 0);
            StyleButton(scanButton, "扫描", "扫描", null);
            scanButton.BackColor = Color.FromArgb(37, 99, 235); scanButton.ForeColor = Color.White;
            scanButton.FlatAppearance.BorderSize = 0; input.Controls.Add(scanButton, 4, 0);
            layout.Controls.Add(input, 0, 0);

            var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, BackColor = Color.White, Margin = new Padding(0, 8, 0, 8), Padding = new Padding(16, 0, 16, 0) };
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 23));
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 41));
            stats.Controls.Add(MakeMetric("总大小", sizeLabel), 0, 0);
            stats.Controls.Add(MakeMetric("文件", filesLabel), 1, 0);
            stats.Controls.Add(MakeMetric("文件夹", foldersLabel), 2, 0);
            capacity.Dock = DockStyle.Fill; stats.Controls.Add(capacity, 3, 0);
            layout.Controls.Add(stats, 0, 1);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 8 };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
            StyleButton(upButton, "", "返回上级", "up"); actions.Controls.Add(upButton, 0, 0);
            StyleButton(refreshButton, "", "重新扫描", "refresh"); actions.Controls.Add(refreshButton, 1, 0);
            locationBox.ReadOnly = true; locationBox.BorderStyle = BorderStyle.None; locationBox.BackColor = BackColor;
            locationBox.Dock = DockStyle.Fill; locationBox.Margin = new Padding(12, 12, 8, 0); locationBox.AccessibleName = "当前目录";
            locationBox.Text = "暂无数据"; actions.Controls.Add(locationBox, 2, 0);
            StyleButton(copyButton, "", "复制路径", "copy"); actions.Controls.Add(copyButton, 3, 0);
            StyleButton(openButton, "打开", "在资源管理器打开", null); actions.Controls.Add(openButton, 4, 0);
            StyleButton(exportButton, "导出", "导出 CSV", null); actions.Controls.Add(exportButton, 5, 0);
            StyleButton(issuesButton, "详情", "扫描详情", null); actions.Controls.Add(issuesButton, 6, 0);
            layout.Controls.Add(actions, 0, 2);

            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0, 6, 0, 0); tabs.Padding = new Point(18, 7);
            var rankPage = new TabPage("排行") { BackColor = Color.White, Padding = new Padding(1) };
            var mapPage = new TabPage("树图") { BackColor = Color.White, Padding = new Padding(6) };
            tabs.TabPages.Add(rankPage); tabs.TabPages.Add(mapPage);
            ConfigureGrid(); rankPage.Controls.Add(grid);
            treemap.Dock = DockStyle.Fill; treemap.Font = Font; treemap.AccessibleName = "文件空间树图"; mapPage.Controls.Add(treemap);
            layout.Controls.Add(tabs, 0, 3);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            progress.Dock = DockStyle.Fill; progress.Margin = new Padding(0, 12, 10, 7); progress.Style = ProgressBarStyle.Marquee; progress.MarqueeAnimationSpeed = 24; progress.Visible = false;
            statusLabel.Dock = DockStyle.Fill; statusLabel.TextAlign = ContentAlignment.MiddleLeft; statusLabel.ForeColor = Display.Muted; statusLabel.AccessibleRole = AccessibleRole.StatusBar;
            selectionLabel.Dock = DockStyle.Fill; selectionLabel.TextAlign = ContentAlignment.MiddleRight; selectionLabel.AutoEllipsis = true; selectionLabel.ForeColor = Display.Muted;
            footer.Controls.Add(progress, 0, 0); footer.Controls.Add(statusLabel, 1, 0); footer.Controls.Add(selectionLabel, 2, 0);
            layout.Controls.Add(footer, 0, 4);
            tips.SetToolTip(pathBox, "多个路径用分号分隔；含分号的路径加双引号");
            tips.SetToolTip(sizeLabel, "逻辑文件大小；占比相对于当前目录");
            tips.SetToolTip(capacity, "系统报告的磁盘容量；可用空间受当前用户配额影响");
        }

        private Control MakeMetric(string title, Label value)
        {
            var box = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Padding = new Padding(0, 10, 0, 8), Margin = Padding.Empty };
            box.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); box.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            box.Controls.Add(new Label { Text = title, ForeColor = Display.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            value.Text = "—"; value.Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold); value.Dock = DockStyle.Fill; value.TextAlign = ContentAlignment.MiddleLeft; value.AutoEllipsis = true;
            box.Controls.Add(value, 0, 1); return box;
        }

        private void StyleButton(Button button, string text, string name, string icon)
        {
            button.Text = text; button.AccessibleName = name; button.Dock = DockStyle.Fill;
            button.Margin = new Padding(3, 6, 3, 6); button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Display.Line; button.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 241, 250);
            button.BackColor = Color.White; button.UseVisualStyleBackColor = false;
            tips.SetToolTip(button, name);
            if (icon != null) button.Paint += delegate(object sender, PaintEventArgs e) { DrawIcon(e.Graphics, button, icon); };
        }

        private void DrawIcon(Graphics graphics, Control button, string icon)
        {
            float scale = graphics.DpiX / 96F;
            var state = graphics.Save();
            graphics.TranslateTransform(button.Width / 2F - 9 * scale, button.Height / 2F - 9 * scale); graphics.ScaleTransform(scale, scale);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(button.Enabled ? Display.Muted : Color.Silver, 1.6F))
            {
                pen.LineJoin = LineJoin.Round; pen.StartCap = pen.EndCap = LineCap.Round;
                if (icon == "folder") graphics.DrawPolygon(pen, new PointF[] { new PointF(1, 5), new PointF(1, 3), new PointF(7, 3), new PointF(9, 5), new PointF(17, 5), new PointF(16, 15), new PointF(2, 15), new PointF(1, 5) });
                else if (icon == "up") { graphics.DrawLines(pen, new PointF[] { new PointF(3, 8), new PointF(9, 2), new PointF(15, 8) }); graphics.DrawLine(pen, 9, 2, 9, 16); }
                else if (icon == "refresh") { graphics.DrawArc(pen, 3, 3, 12, 12, 40, 290); graphics.DrawLines(pen, new PointF[] { new PointF(16, 3), new PointF(16, 8), new PointF(11, 8) }); }
                else { graphics.DrawRectangle(pen, 6, 6, 10, 11); graphics.DrawLines(pen, new PointF[] { new PointF(3, 12), new PointF(2, 12), new PointF(2, 1), new PointF(12, 1), new PointF(12, 3) }); }
            }
            graphics.Restore(state);
        }

        private void ConfigureGrid()
        {
            grid.Dock = DockStyle.Fill; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.VirtualMode = true; grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false; grid.AutoGenerateColumns = false; grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252); grid.ColumnHeadersDefaultCellStyle.ForeColor = Display.Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(248, 250, 252);
            grid.ColumnHeadersDefaultCellStyle.Font = Font; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; grid.ColumnHeadersHeight = 36;
            grid.DefaultCellStyle.Padding = new Padding(8, 0, 8, 0); grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(231, 240, 255); grid.DefaultCellStyle.SelectionForeColor = Display.Ink;
            grid.DefaultCellStyle.ForeColor = Display.Ink; grid.DefaultCellStyle.BackColor = Color.White;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(241, 245, 249);
            grid.RowTemplate.Height = 36; grid.ShowCellToolTips = true; grid.AccessibleName = "文件夹占用排行";
            string[] names = { "名称", "大小", "占比", "分布", "文件", "文件夹" };
            int[] widths = { 340, 130, 85, 180, 95, 95 };
            for (int i = 0; i < names.Length; i++)
            {
                var column = new DataGridViewTextBoxColumn { HeaderText = names[i], Width = widths[i], SortMode = i == 3 ? DataGridViewColumnSortMode.NotSortable : DataGridViewColumnSortMode.Programmatic };
                if (i == 0) { column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; column.MinimumWidth = 210; }
                if (i != 0 && i != 3) { column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight; }
                grid.Columns.Add(column);
            }
            grid.Columns[1].HeaderCell.SortGlyphDirection = SortOrder.Descending;
            grid.CellValueNeeded += GridValueNeeded; grid.CellPainting += GridPainting;
            grid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            {
                if (e.RowIndex < 0 || e.RowIndex >= visible.Count) return;
                Entry entry = visible[e.RowIndex].Entry;
                e.ToolTipText = entry.FullPath + "\r\n" + entry.SizeBytes.ToString("N0") + " B";
            };
            grid.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (visible.Count == 0) TextRenderer.DrawText(e.Graphics, busy ? "扫描中" : current == null ? "暂无数据" : "空文件夹", Font,
                    new Rectangle(0, grid.ColumnHeadersHeight, grid.Width, Math.Max(0, grid.Height - grid.ColumnHeadersHeight)), Display.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
            typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(grid, true, null);
        }

        private void WireEvents()
        {
            chooseButton.Click += delegate
            {
                using (var dialog = new FolderBrowserDialog { Description = "选择文件夹", ShowNewFolderButton = false })
                {
                    if (Directory.Exists(pathBox.Text)) dialog.SelectedPath = pathBox.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Text = dialog.SelectedPath;
                }
            };
            scanButton.Click += delegate { if (busy) CancelScan(); else StartScan(ParsePaths(pathBox.Text)); };
            pathBox.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter && !busy) { e.SuppressKeyPress = true; StartScan(ParsePaths(pathBox.Text)); } };
            drivesButton.Click += delegate { ShowDrives(); };
            upButton.Click += delegate { NavigateUp(); };
            refreshButton.Click += delegate { if (lastPaths != null) StartScan(lastPaths); };
            copyButton.Click += delegate { CopyPath(); };
            openButton.Click += delegate { OpenPath(); };
            exportButton.Click += delegate { ExportCsv(); };
            issuesButton.Click += delegate { ShowIssues(); };
            grid.SelectionChanged += delegate
            {
                if (!changingSelection && grid.CurrentCell != null && grid.CurrentCell.RowIndex < visible.Count) SelectEntry(visible[grid.CurrentCell.RowIndex].Entry, false);
            };
            grid.CellMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.RowIndex >= 0 && e.RowIndex < visible.Count && e.ColumnIndex == 0)
                {
                    var row = visible[e.RowIndex]; float unit = grid.DeviceDpi / 96F;
                    int x = (int)((12 + row.Depth * 18) * unit);
                    if (e.X >= x - 8 * unit && e.X <= x + 12 * unit) Toggle(row.Entry);
                }
                if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                {
                    grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0];
                    var menu = new ContextMenuStrip();
                    menu.Items.Add("复制路径", null, delegate { CopyPath(); }); menu.Items.Add("打开位置", null, delegate { OpenPath(); });
                    menu.Closed += delegate { menu.Dispose(); }; menu.Show(Cursor.Position);
                }
            };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && e.RowIndex < visible.Count && visible[e.RowIndex].Entry.IsDirectory) Navigate(visible[e.RowIndex].Entry);
            };
            grid.ColumnHeaderMouseClick += delegate(object sender, DataGridViewCellMouseEventArgs e)
            {
                if (e.ColumnIndex == 3) return;
                ascending = sortColumn == e.ColumnIndex ? !ascending : e.ColumnIndex == 0;
                sortColumn = e.ColumnIndex;
                foreach (DataGridViewColumn column in grid.Columns) column.HeaderCell.SortGlyphDirection = SortOrder.None;
                grid.Columns[sortColumn].HeaderCell.SortGlyphDirection = ascending ? SortOrder.Ascending : SortOrder.Descending;
                RefreshRows();
            };
            grid.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Right && selected != null && selected.IsDirectory) { expanded.Add(selected); RefreshRows(); e.Handled = true; }
                else if (e.KeyCode == Keys.Left && selected != null && expanded.Remove(selected)) { RefreshRows(); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter && selected != null && selected.IsDirectory) { Navigate(selected); e.Handled = true; }
            };
            treemap.EntrySelected += delegate(object sender, EntryEventArgs e) { SelectEntry(e.Entry, true); };
            treemap.EntryActivated += delegate(object sender, EntryEventArgs e) { if (e.Entry.IsDirectory) Navigate(e.Entry); };
            progressTimer.Interval = 150;
            progressTimer.Tick += delegate
            {
                ScanProgress report = pendingProgress;
                if (report == null) return;
                sizeLabel.Text = Display.Bytes(report.Bytes); filesLabel.Text = report.FileCount.ToString("N0"); foldersLabel.Text = report.FolderCount.ToString("N0");
                selectionLabel.Text = report.CurrentPath; tips.SetToolTip(selectionLabel, report.CurrentPath);
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                closePending = true;
                if (cancellation != null) cancellation.Cancel();
                // Scanning uses a background thread and holds no writable handles.
                // Let the message loop end even if a filesystem request is stalled.
            };
        }

        private void GridValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex >= visible.Count) return;
            Entry entry = visible[e.RowIndex].Entry;
            switch (e.ColumnIndex)
            {
                case 0: e.Value = entry.Name; break;
                case 1: e.Value = Display.Bytes(entry.SizeBytes); break;
                case 2: e.Value = Display.Fraction(entry.SizeBytes, current.SizeBytes).ToString("P2"); break;
                case 4: e.Value = entry.FileCount.ToString("N0"); break;
                case 5: e.Value = entry.IsDirectory ? entry.FolderCount.ToString("N0") : "—"; break;
            }
        }

        private void GridPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= visible.Count || (e.ColumnIndex != 0 && e.ColumnIndex != 3)) return;
            e.PaintBackground(e.CellBounds, true); var row = visible[e.RowIndex]; Entry entry = row.Entry;
            float scale = e.Graphics.DpiX / 96F;
            if (e.ColumnIndex == 3)
            {
                var rect = new Rectangle(e.CellBounds.X + (int)(12 * scale), e.CellBounds.Y + (int)(14 * scale), Math.Max(0, e.CellBounds.Width - (int)(24 * scale)), (int)(6 * scale));
                using (var brush = new SolidBrush(Color.FromArgb(229, 235, 244))) e.Graphics.FillRectangle(brush, rect);
                int fill = (int)Math.Round(rect.Width * Display.Fraction(entry.SizeBytes, current.SizeBytes));
                if (fill > 0) { rect.Width = fill; using (var brush = new SolidBrush(Display.Blue)) e.Graphics.FillRectangle(brush, rect); }
            }
            else
            {
                int x = e.CellBounds.X + (int)((12 + row.Depth * 18) * scale), y = e.CellBounds.Y + e.CellBounds.Height / 2;
                if (entry.IsDirectory && entry.Children.Count > 0)
                {
                    using (var pen = new Pen(Display.Muted, 1.3F * scale))
                    {
                        if (expanded.Contains(entry)) e.Graphics.DrawLines(pen, new PointF[] { new PointF(x - 3 * scale, y - 2 * scale), new PointF(x + scale, y + 2 * scale), new PointF(x + 5 * scale, y - 2 * scale) });
                        else e.Graphics.DrawLines(pen, new PointF[] { new PointF(x - scale, y - 4 * scale), new PointF(x + 3 * scale, y), new PointF(x - scale, y + 4 * scale) });
                    }
                }
                x += (int)(14 * scale);
                using (var pen = new Pen(entry.IsDirectory ? Color.FromArgb(58, 112, 190) : Color.FromArgb(100, 116, 139), 1.3F * scale))
                {
                    if (entry.IsDirectory)
                    {
                        using (var brush = new SolidBrush(Color.FromArgb(224, 236, 253))) e.Graphics.FillRectangle(brush, x, y - 5 * scale, 15 * scale, 11 * scale);
                        e.Graphics.DrawRectangle(pen, x, y - 5 * scale, 15 * scale, 11 * scale); e.Graphics.DrawLine(pen, x, y - 7 * scale, x + 6 * scale, y - 7 * scale);
                    }
                    else { e.Graphics.DrawRectangle(pen, x + 2 * scale, y - 7 * scale, 11 * scale, 14 * scale); e.Graphics.DrawLine(pen, x + 5 * scale, y - 2 * scale, x + 10 * scale, y - 2 * scale); }
                }
                x += (int)(24 * scale);
                TextRenderer.DrawText(e.Graphics, entry.Name, Font, new Rectangle(x, e.CellBounds.Y, Math.Max(0, e.CellBounds.Right - x - (int)(8 * scale)), e.CellBounds.Height), Display.Ink,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border); e.Handled = true;
        }

        internal static string[] ParsePaths(string text)
        {
            string input = text.Trim();
            // A real directory containing a semicolon is a single path.
            if (Directory.Exists(input)) return new[] { input };
            var paths = new List<string>(); var item = new StringBuilder(); bool quoted = false;
            foreach (char c in input)
            {
                if (c == '"') quoted = !quoted;
                else if ((c == ';' || c == '\r' || c == '\n') && !quoted) { if (item.ToString().Trim().Length > 0) paths.Add(item.ToString().Trim()); item.Clear(); }
                else item.Append(c);
            }
            if (quoted) throw new ArgumentException("路径的双引号未闭合。");
            if (item.ToString().Trim().Length > 0) paths.Add(item.ToString().Trim());
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string QuotePath(string path) { return path.IndexOf(';') >= 0 ? "\"" + path + "\"" : path; }

        private async void StartScan(string[] paths)
        {
            if (busy) return;
            if (paths.Length == 0) { MessageBox.Show(this, "请输入文件夹或磁盘路径。", "路径", MessageBoxButtons.OK, MessageBoxIcon.Information); pathBox.Focus(); return; }
            lastPaths = paths.ToArray(); pathBox.Text = string.Join(";", paths.Select(QuotePath));
            var previousRoot = scanRoot; var previousCurrent = current;
            pendingProgress = null; cancellation = new CancellationTokenSource(); var token = cancellation.Token;
            results = new List<ScanResult>(); SetBusy(true); statusLabel.Text = "扫描中";
            sizeLabel.Text = "—"; filesLabel.Text = "—"; foldersLabel.Text = "—";
            selectionLabel.Text = ""; capacity.SetPath(paths.Length == 1 ? paths[0] : null); progressTimer.Start();
            try
            {
                var newResults = await Task.Run(delegate
                {
                    var found = new List<ScanResult>();
                    long priorFiles = 0, priorFolders = 0, priorBytes = 0;
                    foreach (string path in paths)
                    {
                        if (token.IsCancellationRequested) break;
                        try
                        {
                            ScanResult result = ScanEngine.Scan(path, token, delegate(ScanProgress report)
                            {
                                pendingProgress = new ScanProgress { FileCount = priorFiles + report.FileCount, FolderCount = priorFolders + report.FolderCount, Bytes = priorBytes + report.Bytes, CurrentPath = report.CurrentPath };
                            });
                            found.Add(result); priorFiles += result.Root.FileCount; priorFolders += result.Root.FolderCount; priorBytes += result.Root.SizeBytes;
                        }
                        catch (Exception error)
                        {
                            if (!(error is IOException) && !(error is UnauthorizedAccessException) && !(error is ArgumentException) && !(error is System.ComponentModel.Win32Exception)) throw;
                            found.Add(new ScanResult { Root = new Entry { Name = path, FullPath = path, IsDirectory = true }, ErrorCount = 1, Issues = new List<string> { path + "\r\n" + error.Message } });
                        }
                    }
                    return found;
                });
                if (closePending || IsDisposed) return;
                progressTimer.Stop(); results = newResults;
                if (!token.IsCancellationRequested && results.Count > 0 && !(results.Count == 1 && results[0].ErrorCount > 0 && results[0].Root.Children.Count == 0 && results[0].Root.FileCount == 0))
                {
                    scanRoot = CombineRoots(results); expanded.Clear(); Navigate(scanRoot);
                    long errors = results.Sum(r => r.ErrorCount), skipped = results.Sum(r => r.SkippedCount);
                    statusLabel.Text = errors > 0 ? "完成 · 有跳过" : "完成";
                    selectionLabel.Text = "耗时 " + results.Sum(r => r.Elapsed.TotalSeconds).ToString("N1") + " 秒" + (skipped > 0 ? " · 跳过 " + skipped.ToString("N0") + " 项" : "");
                }
                else
                {
                    scanRoot = previousRoot; current = previousCurrent;
                    if (current != null) UpdateMetrics(); else { sizeLabel.Text = filesLabel.Text = foldersLabel.Text = "—"; }
                    statusLabel.Text = token.IsCancellationRequested ? "已取消" : "扫描失败";
                    selectionLabel.Text = token.IsCancellationRequested ? "" : results.Count > 0 && results[0].Issues.Count > 0 ? results[0].Issues[0] : "路径不可用";
                    tips.SetToolTip(selectionLabel, selectionLabel.Text);
                    if (!token.IsCancellationRequested && !closePending) ShowIssues();
                }
            }
            catch (Exception error) { if (!closePending && !IsDisposed) { progressTimer.Stop(); statusLabel.Text = "扫描失败"; Program.ShowError(error); if (current != null) UpdateMetrics(); } }
            finally
            {
                pendingProgress = null; cancellation.Dispose(); cancellation = null;
                if (!closePending && !IsDisposed) { progressTimer.Stop(); SetBusy(false); grid.Invalidate(); }
            }
        }

        private static Entry CombineRoots(List<ScanResult> scans)
        {
            if (scans.Count == 1) return scans[0].Root;
            var root = new Entry { Name = "多个目录", FullPath = "", IsDirectory = true };
            foreach (var result in scans)
            {
                result.Root.Name = result.Root.FullPath; result.Root.Parent = root; root.Children.Add(result.Root);
                root.SizeBytes += result.Root.SizeBytes; root.FileCount += result.Root.FileCount; root.FolderCount += result.Root.FolderCount + 1;
            }
            root.Children.Sort(delegate(Entry left, Entry right) { int size = right.SizeBytes.CompareTo(left.SizeBytes); return size == 0 ? StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name) : size; });
            return root;
        }

        private void SetBusy(bool value)
        {
            busy = value; scanButton.Text = value ? "取消" : "扫描"; scanButton.AccessibleName = scanButton.Text; scanButton.Enabled = true;
            chooseButton.Enabled = drivesButton.Enabled = pathBox.Enabled = !value;
            refreshButton.Enabled = !value && lastPaths != null;
            upButton.Enabled = !value && current != null && current.Parent != null;
            copyButton.Enabled = openButton.Enabled = !value && TargetEntry() != null && TargetEntry().FullPath.Length > 0;
            exportButton.Enabled = !value && current != null;
            issuesButton.Enabled = !value && results.Count > 0;
            grid.Enabled = treemap.Enabled = !value; progress.Visible = value;
        }

        private void CancelScan() { if (cancellation != null) { cancellation.Cancel(); statusLabel.Text = "正在取消"; scanButton.Enabled = false; } }
        private void NavigateUp() { if (current != null && current.Parent != null) Navigate(current.Parent); }
        private void Navigate(Entry entry)
        {
            if (!entry.IsDirectory) return;
            current = entry; selected = null; expanded.Clear();
            selectionLabel.Text = "";
            locationBox.Text = entry.FullPath.Length > 0 ? entry.FullPath : entry.Name; tips.SetToolTip(locationBox, locationBox.Text);
            treemap.Root = entry; RefreshRows(); UpdateMetrics(); SetBusy(busy);
        }
        private void UpdateMetrics()
        {
            sizeLabel.Text = Display.Bytes(current.SizeBytes); filesLabel.Text = current.FileCount.ToString("N0"); foldersLabel.Text = current.FolderCount.ToString("N0");
            capacity.SetPath(current.FullPath);
        }

        private IEnumerable<Entry> Ordered(List<Entry> entries)
        {
            if (!ascending && (sortColumn == 1 || sortColumn == 2)) return entries;
            IOrderedEnumerable<Entry> ordered;
            if (sortColumn == 0) ordered = ascending ? entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase) : entries.OrderByDescending(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
            else if (sortColumn == 4) ordered = ascending ? entries.OrderBy(e => e.FileCount) : entries.OrderByDescending(e => e.FileCount);
            else if (sortColumn == 5) ordered = ascending ? entries.OrderBy(e => e.FolderCount) : entries.OrderByDescending(e => e.FolderCount);
            else ordered = ascending ? entries.OrderBy(e => e.SizeBytes) : entries.OrderByDescending(e => e.SizeBytes);
            return ordered.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        }
        private void RefreshRows()
        {
            changingSelection = true;
            try
            {
                visible.Clear();
                if (current != null)
                {
                    var stack = new Stack<VisibleEntry>();
                    foreach (var entry in Ordered(current.Children).Reverse()) stack.Push(new VisibleEntry(entry, 0));
                    while (stack.Count > 0)
                    {
                        var row = stack.Pop(); visible.Add(row);
                        if (expanded.Contains(row.Entry)) foreach (var child in Ordered(row.Entry.Children).Reverse()) stack.Push(new VisibleEntry(child, row.Depth + 1));
                    }
                }
                grid.RowCount = visible.Count;
                int index = visible.FindIndex(row => row.Entry == selected);
                if (index >= 0) grid.CurrentCell = grid.Rows[index].Cells[0]; else grid.ClearSelection();
                grid.Invalidate();
            }
            finally { changingSelection = false; }
        }
        private void Toggle(Entry entry) { if (!entry.IsDirectory || entry.Children.Count == 0) return; if (!expanded.Remove(entry)) expanded.Add(entry); RefreshRows(); }
        private void SelectEntry(Entry entry, bool selectGrid)
        {
            selected = entry; treemap.SelectedEntry = entry;
            if (selectGrid)
            {
                int index = visible.FindIndex(row => row.Entry == entry);
                if (index >= 0) { changingSelection = true; grid.CurrentCell = grid.Rows[index].Cells[0]; changingSelection = false; }
            }
            selectionLabel.Text = entry.Name + "  ·  " + Display.Bytes(entry.SizeBytes);
            tips.SetToolTip(selectionLabel, entry.FullPath);
            copyButton.Enabled = openButton.Enabled = !busy && entry.FullPath.Length > 0;
        }
        private Entry TargetEntry() { return selected ?? current; }
        private void CopyPath()
        {
            var entry = TargetEntry(); if (entry == null || entry.FullPath.Length == 0) return;
            try { Clipboard.SetText(entry.FullPath); statusLabel.Text = "已复制"; } catch (ExternalException) { MessageBox.Show(this, "剪贴板正被占用，请重试。", "复制", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        private void OpenPath()
        {
            var entry = TargetEntry(); if (entry == null || entry.FullPath.Length == 0) return;
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + entry.FullPath + "\"") { UseShellExecute = false }); }
            catch (Exception error) { Program.ShowError(error); }
        }

        private void ShowDrives()
        {
            var menu = new ContextMenuStrip(); var paths = new List<string>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string path = drive.RootDirectory.FullName; paths.Add(path);
                    menu.Items.Add(path + "  " + drive.VolumeLabel, null, delegate { pathBox.Text = path; });
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (paths.Count > 1) { menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("全部磁盘", null, delegate { StartScan(paths.ToArray()); }); }
            if (paths.Count == 0) menu.Items.Add("暂无磁盘").Enabled = false;
            menu.Closed += delegate { menu.Dispose(); }; menu.Show(drivesButton, new Point(0, drivesButton.Height));
        }

        private void ShowIssues()
        {
            var text = new StringBuilder();
            foreach (var result in results)
            {
                text.AppendLine(result.Root.FullPath); text.AppendLine("无法读取：" + result.ErrorCount + "    已跳过：" + result.SkippedCount);
                foreach (string issue in result.Issues) text.AppendLine(issue);
                if (result.ErrorCount + result.SkippedCount > result.Issues.Count) text.AppendLine("仅列出前 100 条详情。");
                text.AppendLine();
            }
            using (var dialog = new Form { Text = "扫描详情", Font = Font, AutoScaleMode = AutoScaleMode.None, StartPosition = FormStartPosition.CenterParent, Size = new Size(720, 430), MinimizeBox = false, MaximizeBox = false })
            using (var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Text = text.ToString(), BackColor = Color.White })
            { dialog.Controls.Add(box); ScaleDialog(dialog); dialog.ShowDialog(this); }
        }

        private void ExportCsv()
        {
            if (current == null) return;
            string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "磁盘占用-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv");
            using (var dialog = new Form { Text = "导出 CSV", Font = Font, AutoScaleMode = AutoScaleMode.None, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(620, 114), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false })
            using (var exportPath = new TextBox { Text = defaultPath, Left = 20, Top = 24, Width = 580, AccessibleName = "保存路径" })
            using (var save = new Button { Text = "保存", Left = 438, Top = 66, Width = 78, DialogResult = DialogResult.OK })
            using (var cancel = new Button { Text = "取消", Left = 524, Top = 66, Width = 78, DialogResult = DialogResult.Cancel })
            {
                dialog.Controls.Add(exportPath); dialog.Controls.Add(save); dialog.Controls.Add(cancel); dialog.AcceptButton = save; dialog.CancelButton = cancel;
                ScaleDialog(dialog);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string destination = Path.GetFullPath(exportPath.Text.Trim().Trim('"'));
                    if (File.Exists(destination)) { MessageBox.Show(this, "文件已存在，请使用其他名称。", "导出", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                    using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                    {
                        writer.WriteLine("名称,类型,字节数,大小,占比,文件数,文件夹数,修改时间,路径");
                        foreach (var entry in current.Children.OrderByDescending(e => e.SizeBytes))
                            writer.WriteLine(string.Join(",", new[] { Csv(entry.Name), Csv(entry.IsDirectory ? "文件夹" : "文件"), entry.SizeBytes.ToString(CultureInfo.InvariantCulture), Csv(Display.Bytes(entry.SizeBytes)), (Display.Fraction(entry.SizeBytes, current.SizeBytes) * 100).ToString("F4", CultureInfo.InvariantCulture), entry.FileCount.ToString(), entry.FolderCount.ToString(), Csv(entry.LatestWrite == DateTime.MinValue ? "" : entry.LatestWrite.ToString("yyyy-MM-dd HH:mm:ss")), Csv(entry.FullPath) }));
                    }
                    statusLabel.Text = "已导出";
                }
                catch (Exception error) { Program.ShowError(error); }
            }
        }
        private static string Csv(string value)
        {
            // Neutralize spreadsheet formulas in names controlled by the filesystem.
            if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private void ScaleDialog(Form dialog)
        {
            float factor = DeviceDpi / 96F;
            if (factor != 1F) dialog.Scale(new SizeF(factor, factor));
            dialog.AutoScaleDimensions = new SizeF(DeviceDpi, DeviceDpi);
            dialog.AutoScaleMode = AutoScaleMode.Dpi;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.Up) || keyData == (Keys.Alt | Keys.Left)) { if (!busy) NavigateUp(); return true; }
            if (keyData == Keys.F5) { if (!busy && lastPaths != null) StartScan(lastPaths); return true; }
            if (keyData == Keys.Escape && busy) { CancelScan(); return true; }
            if (keyData == (Keys.Control | Keys.L)) { pathBox.Focus(); pathBox.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.C) && (grid.ContainsFocus || treemap.ContainsFocus)) { CopyPath(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { progressTimer.Dispose(); tips.Dispose(); if (cancellation != null) cancellation.Cancel(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class DiskCapacityView : Control
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
        private ulong total, available;
        private bool valid;
        private string drive = "磁盘";
        internal DiskCapacityView() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); AccessibleName = "磁盘容量"; }
        internal void SetPath(string path)
        {
            valid = false; drive = "磁盘";
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    string full = Path.GetFullPath(path);
                    if (!full.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        string root = Path.GetPathRoot(full); ulong free;
                        valid = GetDiskFreeSpaceEx(root, out available, out total, out free) && total > 0;
                        drive = root.TrimEnd('\\');
                    }
                }
                catch (ArgumentException) { } catch (IOException) { }
            }
            AccessibleDescription = valid ? drive + " 已用 " + Display.Bytes((long)(total - available)) + " 可用 " + Display.Bytes((long)available) : "磁盘容量不可用";
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); float scale = e.Graphics.DpiX / 96F; int pad = (int)(20 * scale); int width = Width - pad;
            if (!valid) { TextRenderer.DrawText(e.Graphics, "磁盘  ·  不可用", Font, new Rectangle(pad, 0, Math.Max(0, width), Height), Display.Muted, TextFormatFlags.VerticalCenter); return; }
            double ratio = (double)(total - available) / total;
            Color color = ratio < .8 ? Color.FromArgb(22, 163, 74) : ratio < .9 ? Color.FromArgb(245, 158, 11) : Color.FromArgb(220, 38, 38);
            string state = ratio < .8 ? "正常" : ratio < .9 ? "偏紧" : "紧张";
            TextRenderer.DrawText(e.Graphics, drive + "  ·  " + state + "  " + ratio.ToString("P1"), Font, new Rectangle(pad, (int)(10 * scale), width, (int)(24 * scale)), Display.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            var bar = new Rectangle(pad, (int)(39 * scale), Math.Max(0, width - (int)(8 * scale)), (int)(5 * scale));
            using (var brush = new SolidBrush(Display.Line)) e.Graphics.FillRectangle(brush, bar);
            bar.Width = (int)(bar.Width * ratio); using (var brush = new SolidBrush(color)) e.Graphics.FillRectangle(brush, bar);
            TextRenderer.DrawText(e.Graphics, "已用 " + Display.Bytes((long)(total - available)) + "    可用 " + Display.Bytes((long)available), Font,
                new Rectangle(pad, (int)(50 * scale), width, (int)(22 * scale)), Display.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
