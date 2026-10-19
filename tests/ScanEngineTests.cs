using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using DiskSpace;
using Microsoft.Win32.SafeHandles;

internal static class ScanEngineTests
{
    private static string fixtures;
    private static int passed;
    private static int skipped;

    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("A private test output directory is required."); return 2; }
        string testOutput = Path.GetFullPath(args[0]);
        if (!Directory.Exists(testOutput)) { Console.Error.WriteLine("Test output directory does not exist."); return 2; }
        fixtures = Path.Combine(testOutput, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        try
        {
            Test("Unicode, spaces, semicolons, nested and empty directories", BasicTree);
            Test("Logical lengths above 4 GiB without allocating 4 GiB", LargeSparseFile);
            Test("Numeric ordering and deterministic tie ordering", Sorting);
            Test("Extended Unicode paths and deep iterative traversal", LongDeepPaths);
            Test("Junction cycle and root reparse point are skipped", JunctionCycle);
            Test("Hardlink names each count, file symbolic links are skipped", Links);
            Test("Denied descendants remain explicit; denied roots fail", DeniedAccess);
            Test("Actual in-flight cancellation yields marked partial results", Cancellation);
            Test("Missing or invalid roots produce useful failures", InvalidRoots);
            Console.WriteLine("PASS: " + passed + " tests; optional capability skips: " + skipped + ".");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
        finally
        {
            // The only removal target is our just-created GUID fixture directory.
            if (!Path.GetFullPath(fixtures).StartsWith(testOutput + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup boundary check failed.");
            RemoveFixtureTree(fixtures);
            if (Directory.Exists(fixtures)) throw new IOException("Fixture cleanup did not finish: " + fixtures);
        }
    }

    private static void Test(string name, Action body)
    {
        body();
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static string NewDirectory(string name)
    {
        string path = Path.Combine(fixtures, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ScanResult Scan(string path) { return ScanEngine.Scan(path, CancellationToken.None, null); }

    private static void BasicTree()
    {
        string path = NewDirectory("中文目录 with spaces;测试");
        File.WriteAllBytes(Path.Combine(path, "文件 空格;name.bin"), new byte[11]);
        string nested = Path.Combine(path, "嵌套");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(Path.Combine(nested, "empty"));
        File.WriteAllBytes(Path.Combine(nested, "another.bin"), new byte[29]);
        DateTime future = new DateTime(2030, 3, 2, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(nested, "another.bin"), future);
        ScanResult result = Scan(path);
        Equal(40L, result.Root.SizeBytes, "total bytes");
        Equal(2L, result.Root.FileCount, "files");
        Equal(2L, result.Root.FolderCount, "folders exclude root");
        Equal(0L, result.ErrorCount, "no errors");
        Equal(0L, result.SkippedCount, "no skips");
        Assert(!result.Cancelled, "complete scan");
        Assert(result.Root.Parent == null, "root parent");
        Entry branch = FindChild(result.Root, "嵌套");
        Assert(branch.Parent == result.Root, "parent identity");
        Equal(29L, branch.SizeBytes, "nested bytes");
        Equal(0L, FindChild(branch, "empty").SizeBytes, "empty directory retained");
        Equal(future.ToLocalTime(), result.Root.LatestWrite, "latest descendant write time");
        Assert(result.Elapsed >= TimeSpan.Zero, "elapsed time");
    }

    private static void LargeSparseFile()
    {
        string path = NewDirectory("large");
        const long length = 5L * 1024 * 1024 * 1024 + 37;
        using (FileStream file = new FileStream(Path.Combine(path, "sparse.bin"), FileMode.CreateNew, FileAccess.Write))
        {
            int returned;
            if (!DeviceIoControl(file.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                throw new IOException("Test volume does not support sparse files; refusing to allocate 5 GiB.");
            file.SetLength(length);
        }
        ScanResult result = Scan(path);
        Equal(length, result.Root.SizeBytes, "64-bit logical length");
        Equal(length, result.Root.Children[0].SizeBytes, "64-bit file length");
        Equal(1L, result.Root.FileCount, "one sparse file");
        Equal(0L, result.ErrorCount, "large file readable");
    }

    private static void Sorting()
    {
        string path = NewDirectory("sorting");
        File.WriteAllBytes(Path.Combine(path, "nine"), new byte[9]);
        File.WriteAllBytes(Path.Combine(path, "two"), new byte[2]);
        File.WriteAllBytes(Path.Combine(path, "ten"), new byte[10]);
        File.WriteAllBytes(Path.Combine(path, "alpha"), new byte[10]);
        File.WriteAllBytes(Path.Combine(path, "Zebra"), new byte[10]);
        Directory.CreateDirectory(Path.Combine(path, "empty"));
        ScanResult result = Scan(path);
        string[] expected = { "alpha", "ten", "Zebra", "nine", "two", "empty" };
        for (int i = 0; i < expected.Length; i++) Equal(expected[i], result.Root.Children[i].Name, "numeric sort row " + i);
        ScanResult again = Scan(path);
        for (int i = 0; i < expected.Length; i++) Equal(result.Root.Children[i].Name, again.Root.Children[i].Name, "repeat sort");
    }

    private static void LongDeepPaths()
    {
        string path = NewDirectory("long");
        string nested = path;
        const int depth = 155;
        for (int i = 0; i < depth; i++)
        {
            nested += "\\中文层级" + i.ToString("D3");
            if (!CreateDirectoryW(Native(nested), IntPtr.Zero)) throw new IOException("Cannot create long fixture; error " + Marshal.GetLastWin32Error());
        }
        using (SafeFileHandle file = CreateFileW(Native(nested + "\\末尾 文件;测试.bin"), 0x40000000, 7, IntPtr.Zero, 1, 0x80, IntPtr.Zero))
        {
            if (file.IsInvalid) throw new IOException("Cannot create long-path file; error " + Marshal.GetLastWin32Error());
            uint written;
            byte[] data = new byte[31];
            if (!WriteFile(file, data, (uint)data.Length, out written, IntPtr.Zero)) throw new IOException("Cannot write long-path file.");
        }
        Assert(nested.Length > 260, "fixture really exceeds MAX_PATH");
        ScanResult result = Scan(path);
        Equal(31L, result.Root.SizeBytes, "long path bytes");
        Equal((long)depth, result.Root.FolderCount, "deep tree count");
        Equal(1L, result.Root.FileCount, "deep file count");
        Equal(0L, result.ErrorCount, "long path no errors");
        Entry cursor = result.Root;
        for (int i = 0; i < depth; i++) cursor = cursor.Children[0];
        Equal("末尾 文件;测试.bin", cursor.Children[0].Name, "deep filename");
        ScanResult fromLongRoot = Scan(nested);
        Equal(31L, fromLongRoot.Root.SizeBytes, "long root accepted");
        ScanResult fromExtendedRoot = Scan(Native(nested));
        Equal(31L, fromExtendedRoot.Root.SizeBytes, "extended root accepted");
    }

    private static void JunctionCycle()
    {
        string path = NewDirectory("junction");
        File.WriteAllBytes(Path.Combine(path, "content.bin"), new byte[17]);
        string junction = Path.Combine(path, "cycle");
        ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + junction + "\" \"" + path + "\"");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using (Process process = Process.Start(start))
        {
            process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Cannot create junction fixture: " + error);
        }
        ScanResult result = Scan(path);
        Equal(17L, result.Root.SizeBytes, "cycle is not traversed");
        Equal(1L, result.SkippedCount, "junction skip count");
        Equal(0L, result.Root.FolderCount, "skipped junction not counted");
        Equal(1, result.Root.Children.Count, "junction excluded from tree");
        Assert(result.Issues.Count == 1 && result.Issues[0].Contains(junction), "junction issue includes path");
        ScanResult root = Scan(junction);
        Equal(1L, root.SkippedCount, "root junction skipped");
        Equal(0L, root.Root.SizeBytes, "root junction no traversal");
    }

    private static void Links()
    {
        string path = NewDirectory("links");
        string original = Path.Combine(path, "original.bin");
        File.WriteAllBytes(original, new byte[43]);
        if (!CreateHardLinkW(Path.Combine(path, "hardlink.bin"), original, IntPtr.Zero))
            throw new IOException("Cannot create hardlink fixture; error " + Marshal.GetLastWin32Error());
        string symbolic = Path.Combine(path, "symbolic.bin");
        bool created = CreateSymbolicLinkW(symbolic, original, 2);
        if (!created) created = CreateSymbolicLinkW(symbolic, original, 0);
        ScanResult result = Scan(path);
        Equal(86L, result.Root.SizeBytes, "each hardlink name counted");
        Equal(2L, result.Root.FileCount, "two ordinary file names");
        if (created) Equal(1L, result.SkippedCount, "file symbolic link skipped");
        else { skipped++; Console.WriteLine("SKIP file-symlink creation needs Developer Mode or elevation; junction skip is tested separately."); }
    }

    private static void DeniedAccess()
    {
        string path = NewDirectory("denied");
        string blocked = Path.Combine(path, "blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllBytes(Path.Combine(path, "visible.bin"), new byte[7]);
        File.WriteAllBytes(Path.Combine(blocked, "hidden.bin"), new byte[19]);
        string previous = Directory.GetAccessControl(blocked).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        DirectorySecurity restricted = Directory.GetAccessControl(blocked);
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User;
        restricted.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ListDirectory, AccessControlType.Deny));
        Directory.SetAccessControl(blocked, restricted);
        try
        {
            ScanResult result = Scan(path);
            Equal(7L, result.Root.SizeBytes, "denied data not invented");
            Equal(1L, result.ErrorCount, "denied descendant error");
            Equal(1L, result.Root.FolderCount, "discovered denied folder retained");
            Assert(result.Issues.Count == 1 && result.Issues[0].Contains(blocked), "denial issue includes path");
            bool denied = false;
            try { Scan(blocked); } catch (UnauthorizedAccessException) { denied = true; }
            Assert(denied, "denied root throws UnauthorizedAccessException");
        }
        finally
        {
            DirectorySecurity restore = new DirectorySecurity();
            restore.SetSecurityDescriptorSddlForm(previous, AccessControlSections.Access);
            Directory.SetAccessControl(blocked, restore);
        }
    }

    private static void Cancellation()
    {
        string path = NewDirectory("cancel");
        const int count = 4000;
        for (int i = 0; i < count; i++)
        {
            string nested = Path.Combine(path, "folder" + i.ToString("D5"));
            Directory.CreateDirectory(nested);
            using (File.Create(Path.Combine(nested, "file"))) { }
        }
        using (CancellationTokenSource cancellation = new CancellationTokenSource())
        {
            int callbacks = 0;
            ScanResult result = ScanEngine.Scan(path, cancellation.Token, delegate(ScanProgress progress)
            {
                if (Interlocked.Increment(ref callbacks) == 1) cancellation.CancelAfter(1);
            });
            Assert(result.Cancelled, "in-flight scan marked cancelled");
            Assert(result.Root.FileCount < count, "partial traversal retained");
            Assert(callbacks >= 2, "initial and final progress delivered");
            long countedFiles = 0;
            foreach (Entry entry in result.Root.Children) countedFiles += entry.FileCount;
            Equal(countedFiles, result.Root.FileCount, "partial tree internally consistent");
            cancellation.Cancel();
            ScanResult preCancelled = ScanEngine.Scan(path, cancellation.Token, null);
            Assert(preCancelled.Cancelled && preCancelled.Root.FileCount == 0, "pre-cancelled scan does no traversal");
        }
    }

    private static void InvalidRoots()
    {
        bool missing = false;
        try { Scan(Path.Combine(fixtures, "does not exist")); } catch (DirectoryNotFoundException error) { missing = error.Message.Contains("does not exist"); }
        Assert(missing, "missing root carries path");
        bool wildcard = false;
        try { Scan(fixtures + "\\*"); } catch (ArgumentException) { wildcard = true; }
        Assert(wildcard, "wildcard rejected");
        string file = Path.Combine(NewDirectory("not-directory"), "file.bin");
        File.WriteAllBytes(file, new byte[0]);
        bool nonDirectory = false;
        try { Scan(file); } catch (IOException) { nonDirectory = true; }
        Assert(nonDirectory, "file root rejected");
    }

    private static Entry FindChild(Entry parent, string name)
    {
        foreach (Entry entry in parent.Children) if (entry.Name == name) return entry;
        throw new Exception("Missing child: " + name);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + ": expected " + expected + ", got " + actual);
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Native(string path) { return path.StartsWith("\\\\") ? "\\\\?\\UNC\\" + path.Substring(2) : "\\\\?\\" + path; }

    private static void RemoveFixtureTree(string path)
    {
        Stack<KeyValuePair<string, bool>> work = new Stack<KeyValuePair<string, bool>>();
        work.Push(new KeyValuePair<string, bool>(path, false));
        while (work.Count > 0)
        {
            KeyValuePair<string, bool> current = work.Pop();
            string native = Native(current.Key);
            uint attributes = GetFileAttributesW(native);
            if (attributes == UInt32.MaxValue) continue;
            if ((attributes & 0x10) == 0)
            {
                if (!DeleteFileW(native)) throw new IOException("Cannot remove fixture file: " + current.Key);
                continue;
            }
            if (current.Value || (attributes & 0x400) != 0)
            {
                if (!RemoveDirectoryW(native)) throw new IOException("Cannot remove fixture directory: " + current.Key);
                continue;
            }
            work.Push(new KeyValuePair<string, bool>(current.Key, true));
            CleanupFindData data;
            IntPtr find = FindFirstFileW(native + "\\*", out data);
            if (find == new IntPtr(-1)) continue;
            try
            {
                do
                {
                    if (data.FileName != "." && data.FileName != "..")
                        work.Push(new KeyValuePair<string, bool>(current.Key + "\\" + data.FileName, false));
                } while (FindNextFileW(find, out data));
            }
            finally { FindClose(find); }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct CleanupFindData
    {
        public uint Attributes;
        public long Creation, Access, Write;
        public uint High, Low, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alternate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle file, byte[] data, uint count, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateHardLinkW(string newFile, string existing, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool DeleteFileW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool RemoveDirectoryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr FindFirstFileW(string path, out CleanupFindData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool FindNextFileW(IntPtr find, out CleanupFindData data);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FindClose(IntPtr find);
}
