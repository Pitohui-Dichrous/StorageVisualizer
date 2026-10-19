using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace DiskSpace
{
    public sealed class EntryEventArgs : EventArgs
    {
        public Entry Entry;
        public EntryEventArgs(Entry entry) { Entry = entry; }
    }

    /// <summary>A size-proportional view of the current folder's immediate children.</summary>
    public class TreemapView : Control
    {
        public sealed class TreemapTile
        {
            public Entry Entry;
            public RectangleF Bounds;
            public long SizeBytes;
            public int AggregatedCount;
            internal double Weight;
            public bool IsAggregate { get { return AggregatedCount > 0; } }
        }

        private const int MaximumTiles = 250;
        private static readonly Color[] Palette = new Color[]
        {
            Color.FromArgb(42, 92, 133), Color.FromArgb(70, 109, 87),
            Color.FromArgb(108, 83, 142), Color.FromArgb(148, 89, 55),
            Color.FromArgb(42, 112, 115), Color.FromArgb(147, 68, 99),
            Color.FromArgb(82, 101, 132), Color.FromArgb(128, 99, 40),
            Color.FromArgb(72, 103, 142), Color.FromArgb(105, 93, 137),
            Color.FromArgb(53, 112, 97), Color.FromArgb(139, 82, 79)
        };

        private readonly ToolTip toolTip;
        private Entry root;
        private Entry selectedEntry;
        private List<TreemapTile> tiles = new List<TreemapTile>();
        private bool layoutDirty = true;
        private float layoutScale = 1F;
        private TreemapTile hovered;
        private Font labelFont;
        private Font valueFont;

        public event EventHandler<EntryEventArgs> EntrySelected;
        public event EventHandler<EntryEventArgs> EntryActivated;

        public TreemapView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Color.White;
            AccessibleName = "磁盘空间矩形图";
            AccessibleDescription = "面积表示文件大小。使用方向键选择项目，按 Enter 打开文件夹。";
            toolTip = new ToolTip();
            toolTip.AutomaticDelay = 350;
            toolTip.AutoPopDelay = 15000;
            toolTip.ShowAlways = true;
        }

        public Entry Root
        {
            get { return root; }
            set
            {
                if (Object.ReferenceEquals(root, value)) return;
                root = value;
                selectedEntry = null;
                hovered = null;
                toolTip.SetToolTip(this, null);
                layoutDirty = true;
                Invalidate();
                if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            }
        }

        public Entry SelectedEntry
        {
            get { return selectedEntry; }
            set { SetSelectedEntry(value, false); }
        }

        /// <summary>
        /// Logical rectangles cover the supplied bounds without overlap. Zero-size entries
        /// have no area; after the largest 249 entries all remaining positive sizes are
        /// represented by one non-actionable aggregate tile.
        /// </summary>
        public static List<TreemapTile> LayoutEntries(IList<Entry> entries, RectangleF bounds)
        {
            List<Entry> positive = new List<Entry>();
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i] != null && entries[i].SizeBytes > 0) positive.Add(entries[i]);
            }
            positive.Sort(delegate(Entry a, Entry b)
            {
                int sizeOrder = b.SizeBytes.CompareTo(a.SizeBytes);
                if (sizeOrder != 0) return sizeOrder;
                int pathOrder = StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
                return pathOrder != 0 ? pathOrder : StringComparer.Ordinal.Compare(a.Name, b.Name);
            });
            List<TreemapTile> result = new List<TreemapTile>();
            int individualCount = positive.Count > MaximumTiles ? MaximumTiles - 1 : positive.Count;
            for (int i = 0; i < individualCount; i++)
                result.Add(new TreemapTile { Entry = positive[i], SizeBytes = positive[i].SizeBytes,
                    Weight = positive[i].SizeBytes });
            if (individualCount < positive.Count)
            {
                double weight = 0;
                long size = 0;
                for (int i = individualCount; i < positive.Count; i++)
                {
                    weight += positive[i].SizeBytes;
                    size = positive[i].SizeBytes > Int64.MaxValue - size
                        ? Int64.MaxValue : size + positive[i].SizeBytes;
                }
                result.Add(new TreemapTile { SizeBytes = size, Weight = weight,
                    AggregatedCount = positive.Count - individualCount });
            }
            if (result.Count > 0 && bounds.Width > 0 && bounds.Height > 0)
            {
                Split(result, 0, result.Count, bounds);
            }
            return result;
        }

        private static void Split(List<TreemapTile> items, int start, int end, RectangleF bounds)
        {
            if (end - start == 1) { items[start].Bounds = bounds; return; }
            double total = 0;
            for (int i = start; i < end; i++) total += items[i].Weight;
            double half = total / 2;
            int split = start + 1;
            double left = items[start].Weight;
            double bestLeft = left;
            double bestDistance = Math.Abs(left - half);
            for (int i = start + 2; i < end; i++)
            {
                left += items[i - 1].Weight;
                double distance = Math.Abs(left - half);
                if (distance >= bestDistance) break;
                split = i;
                bestLeft = left;
                bestDistance = distance;
            }
            double fraction = bestLeft / total;
            RectangleF first;
            RectangleF second;
            if (bounds.Width >= bounds.Height)
            {
                float middle = bounds.Left + (float)(bounds.Width * fraction);
                first = RectangleF.FromLTRB(bounds.Left, bounds.Top, middle, bounds.Bottom);
                second = RectangleF.FromLTRB(middle, bounds.Top, bounds.Right, bounds.Bottom);
            }
            else
            {
                float middle = bounds.Top + (float)(bounds.Height * fraction);
                first = RectangleF.FromLTRB(bounds.Left, bounds.Top, bounds.Right, middle);
                second = RectangleF.FromLTRB(bounds.Left, middle, bounds.Right, bounds.Bottom);
            }
            Split(items, start, split, first);
            Split(items, split, end, second);
        }

        private void EnsureLayout(float scale)
        {
            if (!layoutDirty && Math.Abs(layoutScale - scale) < 0.001F) return;
            layoutScale = scale;
            float inset = 4F * scale;
            RectangleF bounds = new RectangleF(inset, inset,
                Math.Max(0, ClientSize.Width - inset * 2), Math.Max(0, ClientSize.Height - inset * 2));
            tiles = LayoutEntries(root == null ? null : root.Children, bounds);
            layoutDirty = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            float scale = Math.Max(0.5F, g.DpiX / 96F);
            EnsureLayout(scale);
            EnsureFonts();
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.None;
            if (tiles.Count == 0)
            {
                string message = root == null ? "选择文件夹，查看空间分布" :
                    (root.Children == null || root.Children.Count == 0 ? "空文件夹" : "暂无可显示的文件大小");
                using (Brush brush = new SolidBrush(Color.FromArgb(116, 125, 139)))
                using (StringFormat format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString(message, Font, brush, ClientRectangle, format);
                }
                return;
            }
            for (int i = 0; i < tiles.Count; i++) DrawTile(g, tiles[i], scale);
            if (Focused && selectedEntry == null)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2));
        }

        private void DrawTile(Graphics g, TreemapTile tile, float scale)
        {
            RectangleF box = tile.Bounds;
            float gap = Math.Min(scale, Math.Min(box.Width, box.Height) / 3F);
            box.Inflate(-gap, -gap);
            if (box.Width <= 0 || box.Height <= 0) return;
            Color color = tile.IsAggregate ? Color.FromArgb(103, 115, 128) : ColorFor(tile.Entry.FullPath);
            using (Brush brush = new SolidBrush(color)) g.FillRectangle(brush, box);
            if (Object.ReferenceEquals(hovered, tile) && !tile.IsAggregate &&
                box.Width > 4F * scale && box.Height > 4F * scale)
            {
                RectangleF hoverOutline = box;
                hoverOutline.Inflate(-scale / 2F, -scale / 2F);
                using (Pen pen = new Pen(Color.FromArgb(170, Color.White), scale))
                    g.DrawRectangle(pen, hoverOutline.X, hoverOutline.Y, hoverOutline.Width, hoverOutline.Height);
            }
            bool selected = !tile.IsAggregate && Object.ReferenceEquals(tile.Entry, selectedEntry);
            if (selected)
            {
                float width = Math.Min(3F * scale, Math.Min(box.Width, box.Height) / 3F);
                if (width > 0)
                {
                    RectangleF outline = box;
                    outline.Inflate(-width / 2, -width / 2);
                    using (Pen pen = new Pen(Color.FromArgb(255, 215, 110), width))
                        g.DrawRectangle(pen, outline.X, outline.Y, outline.Width, outline.Height);
                }
                if (Focused && box.Width > 12F * scale && box.Height > 12F * scale)
                {
                    Rectangle focus = Rectangle.Round(box);
                    focus.Inflate(-(int)(5F * scale), -(int)(5F * scale));
                    ControlPaint.DrawFocusRectangle(g, focus, Color.White, color);
                }
            }
            if (box.Width < 65F * scale || box.Height < 38F * scale) return;
            float padding = 9F * scale;
            RectangleF textArea = new RectangleF(box.X + padding, box.Y + padding,
                Math.Max(0, box.Width - padding * 2), Math.Max(0, box.Height - padding * 2));
            string name = tile.IsAggregate ? "其他 " + tile.AggregatedCount.ToString("N0") + " 项" : tile.Entry.Name;
            name = (name ?? String.Empty).Replace('\r', ' ').Replace('\n', ' ');
            using (Brush brush = new SolidBrush(Color.White))
            using (StringFormat format = new StringFormat(StringFormatFlags.NoWrap))
            {
                format.Trimming = StringTrimming.EllipsisCharacter;
                float lineHeight = labelFont.GetHeight(g);
                RectangleF nameArea = new RectangleF(textArea.X, textArea.Y, textArea.Width,
                    Math.Min(lineHeight + scale * 2, textArea.Height));
                if (nameArea.Width > 0 && nameArea.Height >= lineHeight * 0.8F)
                    g.DrawString(name, labelFont, brush, nameArea, format);
                float nextY = nameArea.Y + lineHeight + 4F * scale;
                float valueHeight = valueFont.GetHeight(g);
                if (nextY + valueHeight <= box.Bottom - 5F * scale)
                    g.DrawString(FormatBytes(tile.SizeBytes), valueFont, brush,
                        new RectangleF(textArea.X, nextY, textArea.Width, valueHeight + scale), format);
            }
        }

        private static Color ColorFor(string path)
        {
            uint hash = 2166136261;
            string value = path ?? String.Empty;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= Char.ToUpperInvariant(value[i]);
                hash *= 16777619;
            }
            return Palette[hash % Palette.Length];
        }

        private void EnsureFonts()
        {
            if (labelFont != null) return;
            labelFont = new Font(Font.FontFamily, Math.Max(8.5F, Font.Size), FontStyle.Bold, GraphicsUnit.Point);
            valueFont = new Font(Font.FontFamily, Math.Max(8F, Font.Size - 0.5F), FontStyle.Regular, GraphicsUnit.Point);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double value = bytes;
            int index = 0;
            while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
            return value.ToString(index == 0 ? "N0" : "0.##", CultureInfo.CurrentCulture) + " " + units[index];
        }

        protected override void OnResize(EventArgs e)
        {
            layoutDirty = true;
            hovered = null;
            if (toolTip != null) toolTip.SetToolTip(this, null);
            base.OnResize(e);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            DisposeFonts();
            base.OnFontChanged(e);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            EnsureLayout(layoutScale);
            TreemapTile hit = HitTest(e.Location);
            if (Object.ReferenceEquals(hit, hovered)) return;
            hovered = hit;
            Cursor = hit != null && !hit.IsAggregate ? Cursors.Hand : Cursors.Default;
            string tip = null;
            if (hit != null)
            {
                tip = hit.IsAggregate ? "其他 " + hit.AggregatedCount.ToString("N0") + " 项\n" +
                    FormatBytes(hit.SizeBytes) + "（" + hit.SizeBytes.ToString("N0") + " 字节）\n" +
                    "较小项目已合并显示；可在文件列表中逐项查看。" :
                    hit.Entry.Name + "\n" + FormatBytes(hit.SizeBytes) + "（" +
                    hit.SizeBytes.ToString("N0") + " 字节）\n" + hit.Entry.FullPath;
            }
            toolTip.SetToolTip(this, tip);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = null;
            Cursor = Cursors.Default;
            toolTip.SetToolTip(this, null);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            EnsureLayout(layoutScale);
            TreemapTile tile = HitTest(e.Location);
            if (tile != null && !tile.IsAggregate) SetSelectedEntry(tile.Entry, true);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            TreemapTile tile = HitTest(e.Location);
            if (tile != null && !tile.IsAggregate && tile.Entry.IsDirectory) ActivateEntry(tile.Entry);
        }

        private TreemapTile HitTest(Point point)
        {
            for (int i = 0; i < tiles.Count; i++)
                if (tiles[i].Bounds.Contains(point)) return tiles[i];
            return null;
        }

        private void SetSelectedEntry(Entry value, bool notify)
        {
            if (Object.ReferenceEquals(selectedEntry, value)) return;
            selectedEntry = value;
            Invalidate();
            if (IsHandleCreated)
            {
                EnsureLayout(layoutScale);
                int index = tiles.FindIndex(delegate(TreemapTile t)
                    { return t.Entry != null && Object.ReferenceEquals(t.Entry, value); });
                // WinForms converts this zero-based index to a native MSAA child ID.
                if (index >= 0) AccessibilityNotifyClients(AccessibleEvents.Selection, index);
            }
            EventHandler<EntryEventArgs> handler = EntrySelected;
            if (notify && handler != null) handler(this, new EntryEventArgs(value));
        }

        private void ActivateEntry(Entry entry)
        {
            EventHandler<EntryEventArgs> handler = EntryActivated;
            if (handler != null) handler(this, new EntryEventArgs(entry));
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.Enter) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            EnsureLayout(layoutScale);
            if (e.KeyCode == Keys.Enter && selectedEntry != null && selectedEntry.IsDirectory)
            {
                ActivateEntry(selectedEntry);
                e.Handled = true;
                return;
            }
            if (e.KeyCode != Keys.Left && e.KeyCode != Keys.Right && e.KeyCode != Keys.Up &&
                e.KeyCode != Keys.Down && e.KeyCode != Keys.Home && e.KeyCode != Keys.End) return;
            TreemapTile current = tiles.Find(delegate(TreemapTile t)
                { return t.Entry != null && Object.ReferenceEquals(t.Entry, selectedEntry); });
            TreemapTile target = null;
            if (current == null || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
            {
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i].IsAggregate) continue;
                    target = tiles[i];
                    if (e.KeyCode != Keys.End) break;
                }
            }
            else
            {
                PointF origin = Center(current.Bounds);
                double bestScore = Double.MaxValue;
                for (int i = 0; i < tiles.Count; i++)
                {
                    TreemapTile candidate = tiles[i];
                    if (candidate.IsAggregate || Object.ReferenceEquals(candidate, current)) continue;
                    PointF center = Center(candidate.Bounds);
                    double dx = center.X - origin.X;
                    double dy = center.Y - origin.Y;
                    double forward = e.KeyCode == Keys.Left ? -dx : e.KeyCode == Keys.Right ? dx :
                        e.KeyCode == Keys.Up ? -dy : dy;
                    if (forward <= 0.01) continue;
                    double cross = e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ? Math.Abs(dy) : Math.Abs(dx);
                    double score = forward + cross * 2.5 + cross * cross / Math.Max(1, forward);
                    if (score < bestScore) { bestScore = score; target = candidate; }
                }
            }
            if (target != null) SetSelectedEntry(target.Entry, true);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private static PointF Center(RectangleF box)
        {
            return new PointF(box.X + box.Width / 2, box.Y + box.Height / 2);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TreemapAccessibleObject(this);
        }

        private sealed class TreemapAccessibleObject : ControlAccessibleObject
        {
            private readonly TreemapView owner;
            public TreemapAccessibleObject(TreemapView owner) : base(owner) { this.owner = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.List; } }
            public override int GetChildCount() { owner.EnsureLayout(owner.layoutScale); return owner.tiles.Count; }
            public override AccessibleObject GetChild(int index)
            {
                owner.EnsureLayout(owner.layoutScale);
                return index >= 0 && index < owner.tiles.Count ? new TileAccessibleObject(owner, owner.tiles[index], this) : null;
            }
            public override AccessibleObject GetSelected()
            {
                owner.EnsureLayout(owner.layoutScale);
                for (int i = 0; i < owner.tiles.Count; i++)
                    if (owner.tiles[i].Entry != null && Object.ReferenceEquals(owner.tiles[i].Entry, owner.selectedEntry))
                        return GetChild(i);
                return null;
            }
            public override AccessibleObject GetFocused() { return owner.Focused ? GetSelected() : null; }
        }

        private sealed class TileAccessibleObject : AccessibleObject
        {
            private readonly TreemapView owner;
            private readonly TreemapTile tile;
            private readonly AccessibleObject parent;
            public TileAccessibleObject(TreemapView owner, TreemapTile tile, AccessibleObject parent)
            { this.owner = owner; this.tile = tile; this.parent = parent; }
            public override AccessibleObject Parent { get { return parent; } }
            public override AccessibleRole Role { get { return AccessibleRole.ListItem; } }
            public override Rectangle Bounds { get { return owner.RectangleToScreen(Rectangle.Round(tile.Bounds)); } }
            public override string Name
            {
                get { return (tile.IsAggregate ? "其他 " + tile.AggregatedCount.ToString("N0") + " 项" : tile.Entry.Name) +
                    "，" + FormatBytes(tile.SizeBytes) + "，" + tile.SizeBytes.ToString("N0") + " 字节"; }
                set { }
            }
            public override string Description { get { return tile.IsAggregate ? "合并显示的较小项目" : tile.Entry.FullPath; } }
            public override string DefaultAction { get { return tile.IsAggregate ? String.Empty : tile.Entry.IsDirectory ? "打开" : "选择"; } }
            public override AccessibleStates State
            {
                get
                {
                    if (tile.IsAggregate) return AccessibleStates.ReadOnly;
                    AccessibleStates state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                    if (Object.ReferenceEquals(tile.Entry, owner.selectedEntry))
                        state |= AccessibleStates.Selected | (owner.Focused ? AccessibleStates.Focused : AccessibleStates.None);
                    return state;
                }
            }
            public override void Select(AccessibleSelection flags)
            {
                if (tile.IsAggregate) return;
                if ((flags & AccessibleSelection.TakeFocus) != 0) owner.Focus();
                owner.SetSelectedEntry(tile.Entry, true);
            }
            public override void DoDefaultAction()
            {
                if (tile.IsAggregate) return;
                owner.Focus();
                owner.SetSelectedEntry(tile.Entry, true);
                if (tile.Entry.IsDirectory) owner.ActivateEntry(tile.Entry);
            }
        }

        private void DisposeFonts()
        {
            if (labelFont != null) { labelFont.Dispose(); labelFont = null; }
            if (valueFont != null) { valueFont.Dispose(); valueFont = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { toolTip.Dispose(); DisposeFonts(); }
            base.Dispose(disposing);
        }
    }
}
