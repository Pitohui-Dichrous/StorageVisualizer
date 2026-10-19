using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using DiskSpace;

internal static class TreemapTests
{
    private static int passed;
    private static int failed;
    private static int assertions;

    [STAThread]
    public static int Main()
    {
        Test("Proportional area and disjoint coverage across 28 seeded data sets", ProportionalGeometry);
        Test("249 individual tiles plus an exact non-actionable remainder", Aggregation);
        Test("Deterministic ordering without changing the source list", StableOrdering);
        Test("Deterministic palette with readable white labels", PaletteContrast);
        Test("Zero, missing, degenerate and extreme-size inputs", EdgeCases);
        Test("Initial arrow navigation ignores the aggregate tile", InitialKeyboardSelection);
        Test("Spatial arrows follow the visible layout", SpatialKeyboardSelection);
        Test("Home and End synchronize selection without duplicate events", SelectionEvents);
        Test("Enter opens directories and leaves files unactivated", KeyboardActivation);
        Test("Mouse selection and double-click activation remain synchronized", MouseEvents);
        Test("Accessible children expose selection and keep aggregates read-only", Accessibility);
        Test("Native accessibility notifications identify the selected child", AccessibilityNotifications);
        Test("Painting survives DPI changes, empty roots and font changes", Painting);
        Console.WriteLine("SUMMARY: " + passed + " passed; " + failed + " failed; " + assertions + " assertions; random seed 741.");
        return failed == 0 ? 0 : 1;
    }

    private static void Test(string name, Action body)
    {
        try { body(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
    }

    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new InvalidOperationException(message);
    }

    private static Entry MakeEntry(string name, long bytes, bool directory)
    {
        return new Entry { Name = name, FullPath = "C:\\Treemap test\\" + name,
            SizeBytes = bytes, IsDirectory = directory };
    }

    private static Entry MakeRoot(int count, bool equalSizes)
    {
        Entry root = MakeEntry("Root", 0, true);
        for (int i = 0; i < count; i++)
        {
            Entry child = MakeEntry("Item " + i.ToString("D4"), equalSizes ? 100 : count - i, i % 2 == 0);
            child.Parent = root;
            root.Children.Add(child);
            root.SizeBytes += child.SizeBytes;
        }
        return root;
    }

    private static void ProportionalGeometry()
    {
        Random random = new Random(741);
        for (int count = 1; count <= 1000; count += 37)
        {
            List<Entry> entries = new List<Entry>();
            long bytes = 0;
            for (int i = 0; i < count; i++)
            {
                long size = random.Next(1, 2000000);
                bytes += size;
                entries.Add(MakeEntry("File " + i, size, false));
            }
            List<TreemapView.TreemapTile> tiles = TreemapView.LayoutEntries(entries, new RectangleF(11, 17, 1000, 700));
            Check(tiles.Count == Math.Min(count, 250), "Unexpected tile count.");
            long tileBytes = 0;
            double area = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                TreemapView.TreemapTile tile = tiles[i];
                tileBytes += tile.SizeBytes;
                double tileArea = (double)tile.Bounds.Width * tile.Bounds.Height;
                area += tileArea;
                Check(!Single.IsNaN(tile.Bounds.X) && !Single.IsNaN(tile.Bounds.Y) &&
                    tile.Bounds.Width >= 0 && tile.Bounds.Height >= 0, "Invalid geometry.");
                Check(tile.Bounds.Left >= 10.9999 && tile.Bounds.Top >= 16.9999 &&
                    tile.Bounds.Right <= 1011.0001 && tile.Bounds.Bottom <= 717.0001, "Tile exceeds bounds.");
                Check(Math.Abs(tileArea / 700000 - (double)tile.SizeBytes / bytes) < 0.000001,
                    "Area does not match its actual byte share.");
                for (int j = 0; j < i; j++)
                {
                    RectangleF intersection = RectangleF.Intersect(tile.Bounds, tiles[j].Bounds);
                    Check(intersection.Width <= 0.0001 || intersection.Height <= 0.0001, "Tiles overlap.");
                }
            }
            Check(tileBytes == bytes, "Some bytes disappeared during aggregation.");
            Check(Math.Abs(area - 700000) < 0.15, "The layout does not cover the full rectangle.");
        }
    }

    private static void Aggregation()
    {
        Entry root = MakeRoot(1000, false);
        List<TreemapView.TreemapTile> tiles = TreemapView.LayoutEntries(root.Children, new RectangleF(0, 0, 1000, 700));
        Check(tiles.Count == 250, "Tile cap changed.");
        for (int i = 0; i < 249; i++) Check(!tiles[i].IsAggregate && tiles[i].Entry == root.Children[i], "Large item was hidden.");
        TreemapView.TreemapTile aggregate = tiles[249];
        Check(aggregate.IsAggregate && aggregate.Entry == null, "Aggregate must not pretend to be a real entry.");
        Check(aggregate.AggregatedCount == 751, "Wrong aggregate count.");
        Check(aggregate.SizeBytes == 282376, "Wrong aggregate byte sum.");
        Check(root.SizeBytes == 500500, "Test fixture byte sum changed.");
    }

    private static void StableOrdering()
    {
        List<Entry> entries = new List<Entry>();
        entries.Add(MakeEntry("z", 100, false));
        entries.Add(MakeEntry("A", 100, false));
        entries.Add(MakeEntry("m", 100, false));
        Entry originalFirst = entries[0];
        List<TreemapView.TreemapTile> first = TreemapView.LayoutEntries(entries, new RectangleF(0, 0, 900, 600));
        Check(entries[0] == originalFirst, "The source child order was modified.");
        entries.Reverse();
        List<TreemapView.TreemapTile> second = TreemapView.LayoutEntries(entries, new RectangleF(0, 0, 900, 600));
        for (int i = 0; i < first.Count; i++)
        {
            Check(first[i].Entry == second[i].Entry, "Equal sizes are not ordered deterministically.");
            Check(first[i].Bounds == second[i].Bounds, "Repeated layout is not stable.");
        }
    }

    private static double Linear(double value)
    {
        value /= 255;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static void PaletteContrast()
    {
        MethodInfo colorFor = typeof(TreemapView).GetMethod("ColorFor", BindingFlags.Static | BindingFlags.NonPublic);
        Check(colorFor != null, "Palette helper is unavailable.");
        HashSet<int> colors = new HashSet<int>();
        for (int i = 0; i < 1000; i++)
        {
            string path = "C:\\TEST\\file " + i;
            Color color = (Color)colorFor.Invoke(null, new object[] { path });
            Color again = (Color)colorFor.Invoke(null, new object[] { path.ToLowerInvariant() });
            Check(color == again, "Colors changed with path case.");
            double luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            Check(1.05 / (luminance + 0.05) >= 4.5, "White label contrast falls below 4.5:1.");
            colors.Add(color.ToArgb());
        }
        Check(colors.Count >= 8, "Too few palette colors are reachable.");
    }

    private static void EdgeCases()
    {
        RectangleF bounds = new RectangleF(0, 0, 1000, 700);
        Check(TreemapView.LayoutEntries(null, bounds).Count == 0, "Missing input should be empty.");
        List<Entry> empty = new List<Entry> { null, MakeEntry("zero", 0, false), MakeEntry("negative", -1, false) };
        Check(TreemapView.LayoutEntries(empty, bounds).Count == 0, "Non-positive sizes should have no area.");
        List<Entry> skew = new List<Entry> { MakeEntry("huge", Int64.MaxValue, false) };
        for (int i = 0; i < 249; i++) skew.Add(MakeEntry("tiny " + i, 1, false));
        foreach (TreemapView.TreemapTile tile in TreemapView.LayoutEntries(skew, bounds))
            Check(!Single.IsNaN(tile.Bounds.X) && !Single.IsNaN(tile.Bounds.Y) &&
                !Single.IsNaN(tile.Bounds.Width) && !Single.IsNaN(tile.Bounds.Height), "Extreme size caused NaN geometry.");
        foreach (TreemapView.TreemapTile tile in TreemapView.LayoutEntries(skew, RectangleF.Empty))
            Check(tile.Bounds.IsEmpty, "A zero-area surface should not produce positive geometry.");
    }

    private sealed class TestView : TreemapView
    {
        public KeyEventArgs Press(Keys key)
        {
            KeyEventArgs args = new KeyEventArgs(key);
            OnKeyDown(args);
            return args;
        }
        public void Mouse(Point point, bool twice)
        {
            MouseEventArgs args = new MouseEventArgs(MouseButtons.Left, twice ? 2 : 1, point.X, point.Y, 0);
            OnMouseDown(args);
            if (twice) OnMouseDoubleClick(args);
        }
        public void PaintAt(Graphics graphics) { OnPaint(new PaintEventArgs(graphics, ClientRectangle)); }
    }

    private static TestView CreateView(Entry root)
    {
        TestView view = new TestView();
        view.Size = new Size(1000, 700);
        view.Root = root;
        return view;
    }

    private static void InitialKeyboardSelection()
    {
        Entry root = MakeRoot(300, true);
        using (TestView view = CreateView(root))
        {
            int events = 0;
            view.EntrySelected += delegate { events++; };
            view.Press(Keys.Right);
            Check(view.SelectedEntry == root.Children[0], "An initial arrow should select the first real tile.");
            Check(events == 1, "Initial selection should raise exactly one event.");
            view.SelectedEntry = null;
            Check(view.AccessibilityObject.GetSelected() == null, "A cleared selection must not select the aggregate.");
            view.Press(Keys.Down);
            Check(view.SelectedEntry == root.Children[0], "An arrow after clearing should select the first real tile.");
            Check(events == 2, "External clearing should not emit a user-selection event.");
        }
    }

    private static void SpatialKeyboardSelection()
    {
        Entry root = MakeRoot(4, true);
        using (TestView view = CreateView(root))
        {
            view.SelectedEntry = root.Children[0];
            view.Press(Keys.Right);
            Check(view.SelectedEntry == root.Children[2], "Right did not move to the right-hand tile.");
            view.Press(Keys.Down);
            Check(view.SelectedEntry == root.Children[3], "Down did not move down.");
            view.Press(Keys.Left);
            Check(view.SelectedEntry == root.Children[1], "Left did not move left.");
            view.Press(Keys.Up);
            Check(view.SelectedEntry == root.Children[0], "Up did not move up.");
        }
    }

    private static void SelectionEvents()
    {
        Entry root = MakeRoot(4, true);
        using (TestView view = CreateView(root))
        {
            int events = 0;
            Entry eventEntry = null;
            view.EntrySelected += delegate(object sender, EntryEventArgs args) { events++; eventEntry = args.Entry; };
            view.SelectedEntry = root.Children[0];
            view.SelectedEntry = root.Children[0];
            Check(events == 0, "External list-to-map synchronization caused an event loop.");
            KeyEventArgs home = view.Press(Keys.Home);
            Check(home.Handled && home.SuppressKeyPress && events == 0, "Home reselected the same entry.");
            view.Press(Keys.End);
            Check(events == 1 && eventEntry == root.Children[3] && view.SelectedEntry == eventEntry,
                "End did not synchronize the selected entry exactly once.");
            view.Press(Keys.End);
            Check(events == 1, "Repeated End duplicated the selection event.");
            view.Press(Keys.Home);
            Check(events == 2 && eventEntry == root.Children[0], "Home did not return to the first tile.");
        }
    }

    private static void KeyboardActivation()
    {
        Entry root = MakeRoot(2, true);
        using (TestView view = CreateView(root))
        {
            int activated = 0;
            Entry opened = null;
            view.EntryActivated += delegate(object sender, EntryEventArgs args) { activated++; opened = args.Entry; };
            view.SelectedEntry = root.Children[0];
            KeyEventArgs enter = view.Press(Keys.Enter);
            Check(enter.Handled && activated == 1 && opened == root.Children[0], "Directory Enter did not activate once.");
            view.SelectedEntry = root.Children[1];
            view.Press(Keys.Enter);
            Check(activated == 1, "Enter activated a file as a directory.");
        }
    }

    private static Point TileCenter(Entry root, int index)
    {
        RectangleF bounds = TreemapView.LayoutEntries(root.Children, new RectangleF(4, 4, 992, 692))[index].Bounds;
        return new Point((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
    }

    private static void MouseEvents()
    {
        Entry root = MakeRoot(2, true);
        using (TestView view = CreateView(root))
        {
            int selected = 0;
            int activated = 0;
            view.EntrySelected += delegate { selected++; };
            view.EntryActivated += delegate { activated++; };
            view.Mouse(TileCenter(root, 0), false);
            Check(selected == 1 && view.SelectedEntry == root.Children[0], "Mouse selection did not synchronize once.");
            view.Mouse(TileCenter(root, 0), true);
            Check(selected == 1 && activated == 1, "Directory double-click duplicated selection or activation.");
            view.Mouse(TileCenter(root, 1), true);
            Check(selected == 2 && activated == 1 && view.SelectedEntry == root.Children[1], "File double-click activation is wrong.");
        }
    }

    private static void Accessibility()
    {
        Entry root = MakeRoot(300, true);
        using (TestView view = CreateView(root))
        {
            int selected = 0;
            int activated = 0;
            view.EntrySelected += delegate { selected++; };
            view.EntryActivated += delegate { activated++; };
            AccessibleObject accessible = view.AccessibilityObject;
            Check(accessible.Role == AccessibleRole.List && accessible.GetChildCount() == 250, "Accessible list has wrong children.");
            Check(accessible.GetSelected() == null, "There is a phantom initial accessible selection.");
            AccessibleObject child = accessible.GetChild(0);
            Check(child.Name.Contains("100") && child.Description == root.Children[0].FullPath, "Accessible item information is incomplete.");
            child.Select(AccessibleSelection.TakeSelection);
            child.Select(AccessibleSelection.TakeSelection);
            Check(selected == 1 && view.SelectedEntry == root.Children[0], "Accessible selection duplicated events.");
            Check((accessible.GetSelected().State & AccessibleStates.Selected) != 0, "Accessible selected state is missing.");
            child.DoDefaultAction();
            Check(activated == 1 && selected == 1, "Accessible activation should open the selected directory once.");
            AccessibleObject aggregate = accessible.GetChild(249);
            Check((aggregate.State & AccessibleStates.ReadOnly) != 0 && aggregate.DefaultAction.Length == 0,
                "Aggregate is not exposed as read-only.");
            aggregate.Select(AccessibleSelection.TakeSelection);
            aggregate.DoDefaultAction();
            Check(selected == 1 && activated == 1 && view.SelectedEntry == root.Children[0], "Aggregate performed an action.");
        }
    }

    private static void Painting()
    {
        Entry root = MakeRoot(30, false);
        root.Children[0].Name = "中文目录与 long folder name 0123456789";
        using (TestView view = CreateView(root))
        {
            foreach (float dpi in new float[] { 96, 144, 192 })
            {
                using (Bitmap bitmap = new Bitmap(1000, 700))
                {
                    bitmap.SetResolution(dpi, dpi);
                    using (Graphics graphics = Graphics.FromImage(bitmap)) view.PaintAt(graphics);
                    Check(bitmap.GetPixel(20, 20).ToArgb() != Color.White.ToArgb(), "A positive tile was not painted at DPI " + dpi + ".");
                }
            }
            view.Root = MakeRoot(0, true);
            using (Bitmap bitmap = new Bitmap(800, 500)) view.DrawToBitmap(bitmap, new Rectangle(0, 0, 800, 500));
            view.Root = null;
            using (Font font = new Font("Segoe UI", 11))
            {
                view.Font = font;
                using (Bitmap bitmap = new Bitmap(800, 500)) view.DrawToBitmap(bitmap, new Rectangle(0, 0, 800, 500));
            }
            Check(true, "Empty and changed-font drawing completed.");
        }
    }

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr window,
        int objectId, int childId, uint threadId, uint timestamp);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    private static void PumpNotifications()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 50) { Application.DoEvents(); Thread.Sleep(1); }
    }

    private static void AccessibilityNotifications()
    {
        Entry root = MakeRoot(300, true);
        using (TestView view = CreateView(root))
        {
            IntPtr targetWindow = view.Handle;
            List<int> children = new List<int>();
            WinEventCallback callback = delegate(IntPtr hookHandle, uint eventType, IntPtr window,
                int objectId, int childId, uint threadId, uint timestamp)
            {
                if (window == targetWindow && objectId == -4) children.Add(childId);
            };
            IntPtr hook = SetWinEventHook(0x8006, 0x8006, IntPtr.Zero, callback,
                (uint)Process.GetCurrentProcess().Id, 0, 0);
            Check(hook != IntPtr.Zero, "The test could not observe accessibility events: " + Marshal.GetLastWin32Error());
            try
            {
                view.SelectedEntry = root.Children[0];
                PumpNotifications();
                Check(children.Count == 1 && children[0] == 1, "First-tile selection announced the wrong native child ID.");
                view.SelectedEntry = root.Children[1];
                PumpNotifications();
                Check(children.Count == 2 && children[1] == 2, "Second-tile selection announced the wrong native child ID.");
                view.SelectedEntry = root.Children[1];
                PumpNotifications();
                Check(children.Count == 2, "Same-entry selection duplicated native notifications.");
                view.SelectedEntry = null;
                PumpNotifications();
                Check(children.Count == 2, "Clearing selection announced the aggregate as selected.");
            }
            finally { UnhookWinEvent(hook); GC.KeepAlive(callback); }
        }
    }
}
