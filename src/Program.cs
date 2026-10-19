using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("磁盘空间分析器")]
[assembly: AssemblyDescription("便携式只读磁盘空间分析器")]
[assembly: AssemblyProduct("磁盘空间分析器")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace DiskSpace
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) { ShowError(e.Exception); };
            try { Application.Run(new MainForm(args)); }
            catch (Exception e) { ShowError(e); }
        }

        internal static void ShowError(Exception error)
        {
            MessageBox.Show("操作未完成。\r\n\r\n" + error.Message + "\r\n\r\n" + error.GetType().FullName,
                "磁盘空间分析器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static class Display
    {
        internal static readonly Color Ink = Color.FromArgb(30, 41, 59);
        internal static readonly Color Muted = Color.FromArgb(71, 85, 105);
        internal static readonly Color Blue = Color.FromArgb(59, 130, 246);
        internal static readonly Color Line = Color.FromArgb(226, 232, 240);
        internal static string Bytes(long value)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            double number = value;
            int unit = 0;
            while (number >= 1024 && unit < units.Length - 1) { number /= 1024; unit++; }
            return number.ToString(unit == 0 ? "N0" : "N2") + " " + units[unit];
        }
        internal static double Fraction(long bytes, long total) { return total <= 0 ? 0 : (double)bytes / total; }
    }
}
