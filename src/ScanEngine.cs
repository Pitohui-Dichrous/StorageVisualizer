using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DiskSpace
{
    public sealed class Entry
    {
        public string Name, FullPath;
        public bool IsDirectory;
        public long SizeBytes, FileCount, FolderCount;
        public DateTime LatestWrite;
        public Entry Parent;
        public List<Entry> Children = new List<Entry>();
    }

    public sealed class ScanResult
    {
        public Entry Root;
        public long ErrorCount, SkippedCount;
        public bool Cancelled;
        public List<string> Issues = new List<string>();
        public TimeSpan Elapsed;
    }

    public sealed class ScanProgress
    {
        public long FileCount, FolderCount, Bytes;
        public string CurrentPath;
    }

    // A file's logical length is counted once per directory entry. No contents are opened,
    // no junctions/symbolic links are followed, and neither traversal nor reduction recurses.
    public static class ScanEngine
    {
        private const uint DirectoryAttribute = 0x10;
        private const uint ReparseAttribute = 0x400;
        private const int NoMoreFiles = 18;
        private const int FileNotFound = 2;
        private const int PathNotFound = 3;
        private const int AccessDenied = 5;
        private const int InvalidParameter = 87;
        private const int IssueLimit = 100;

        public static ScanResult Scan(string path, CancellationToken token, Action<ScanProgress> report)
        {
            Stopwatch timer = Stopwatch.StartNew();
            string fullPath = NormalizePath(path);
            string nativePath = NativePath(fullPath);
            AttributeData rootData;
            if (!GetFileAttributesExW(nativePath, 0, out rootData))
                throw PathException(fullPath, Marshal.GetLastWin32Error());
            if ((rootData.Attributes & DirectoryAttribute) == 0)
                throw new IOException("请选择文件夹或磁盘：" + fullPath);

            ScanResult result = new ScanResult();
            result.Root = NewEntry(fullPath, RootName(fullPath), true, rootData.WriteTime, null);
            ProgressReporter progress = new ProgressReporter(report, timer);
            progress.Report(fullPath, true);
            if (token.IsCancellationRequested)
                result.Cancelled = true;
            else if ((rootData.Attributes & ReparseAttribute) != 0)
            {
                result.SkippedCount++;
                AddIssue(result, "已跳过重解析点（链接/联接）：" + fullPath);
            }
            else
            {
                List<Entry> directories = new List<Entry>();
                Stack<Entry> pending = new Stack<Entry>();
                directories.Add(result.Root);
                pending.Push(result.Root);

                while (pending.Count != 0 && !result.Cancelled)
                {
                    if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                    Entry directory = pending.Pop();
                    progress.Report(directory.FullPath, false);

                    // Recheck immediately before enumeration: a directory may have been
                    // replaced by a junction after its parent was enumerated.
                    AttributeData currentData;
                    if (!GetFileAttributesExW(NativePath(directory.FullPath), 0, out currentData))
                    {
                        int attributeError = Marshal.GetLastWin32Error();
                        if (directory == result.Root) throw PathException(directory.FullPath, attributeError);
                        RecordError(result, directory.FullPath, attributeError);
                        continue;
                    }
                    if ((currentData.Attributes & ReparseAttribute) != 0)
                    {
                        result.SkippedCount++;
                        AddIssue(result, "已跳过扫描期间出现的重解析点：" + directory.FullPath);
                        if (directory.Parent != null)
                        {
                            directory.Parent.Children.Remove(directory);
                            progress.FolderCount--;
                        }
                        continue;
                    }

                    FindData data;
                    string pattern = AppendPath(NativePath(directory.FullPath), "*");
                    SafeFindHandle find = FindFirstFileExW(pattern, 1, out data, 0, IntPtr.Zero, 2);
                    int error = find.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                    if (find.IsInvalid && error == InvalidParameter)
                    {
                        find.Dispose();
                        find = FindFirstFileExW(pattern, 0, out data, 0, IntPtr.Zero, 0);
                        error = find.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                    }
                    using (find)
                    {
                        if (find.IsInvalid)
                        {
                            // FindFirstFile reports FILE_NOT_FOUND for an empty directory.
                            // Verify existence again so a concurrently removed directory
                            // does not silently become a successful empty result.
                            if (error == FileNotFound)
                            {
                                AttributeData stillPresent;
                                if (GetFileAttributesExW(NativePath(directory.FullPath), 0, out stillPresent))
                                    continue;
                                error = Marshal.GetLastWin32Error();
                            }
                            if (directory == result.Root) throw PathException(directory.FullPath, error);
                            RecordError(result, directory.FullPath, error);
                            continue;
                        }

                        while (true)
                        {
                            if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                            string name = data.FileName;
                            if (name != "." && name != "..")
                            {
                                string childPath = AppendPath(directory.FullPath, name);
                                if ((data.Attributes & ReparseAttribute) != 0)
                                {
                                    result.SkippedCount++;
                                    AddIssue(result, "已跳过重解析点（链接/联接）：" + childPath);
                                }
                                else
                                {
                                    bool isDirectory = (data.Attributes & DirectoryAttribute) != 0;
                                    Entry child = NewEntry(childPath, name, isDirectory, data.WriteTime, directory);
                                    directory.Children.Add(child);
                                    if (isDirectory)
                                    {
                                        directories.Add(child);
                                        pending.Push(child);
                                        progress.FolderCount++;
                                    }
                                    else
                                    {
                                        child.SizeBytes = FileLength(data.SizeHigh, data.SizeLow);
                                        child.FileCount = 1;
                                        progress.FileCount++;
                                        progress.Bytes = checked(progress.Bytes + child.SizeBytes);
                                    }
                                }
                                progress.Report(childPath, false);
                            }
                            if (!FindNextFileW(find, out data))
                            {
                                int nextError = Marshal.GetLastWin32Error();
                                if (nextError != NoMoreFiles) RecordError(result, directory.FullPath, nextError);
                                break;
                            }
                        }
                    }
                }

                // Reverse discovery order is a postorder because parents are always
                // discovered first. Include partial results when cancelled or denied.
                for (int i = directories.Count - 1; i >= 0; i--)
                {
                    Entry directory = directories[i];
                    for (int j = 0; j < directory.Children.Count; j++)
                    {
                        Entry child = directory.Children[j];
                        directory.SizeBytes = checked(directory.SizeBytes + child.SizeBytes);
                        directory.FileCount = checked(directory.FileCount + child.FileCount);
                        directory.FolderCount = checked(directory.FolderCount + child.FolderCount + (child.IsDirectory ? 1 : 0));
                        if (child.LatestWrite > directory.LatestWrite) directory.LatestWrite = child.LatestWrite;
                    }
                    directory.Children.Sort(EntryComparer.Instance);
                }
            }
            // A request arriving during reduction still makes this a cancelled result.
            timer.Stop();
            result.Elapsed = timer.Elapsed;
            progress.Report(fullPath, true);
            result.Cancelled = result.Cancelled || token.IsCancellationRequested;
            return result;
        }

        private static Entry NewEntry(string path, string name, bool directory, NativeFileTime writeTime, Entry parent)
        {
            Entry entry = new Entry();
            entry.Name = name;
            entry.FullPath = path;
            entry.IsDirectory = directory;
            entry.LatestWrite = LocalDate(writeTime);
            entry.Parent = parent;
            return entry;
        }

        private static long FileLength(uint high, uint low)
        {
            if (high > Int32.MaxValue) throw new IOException("文件长度超出 64 位有符号整数范围。");
            return ((long)high << 32) | low;
        }

        private static DateTime LocalDate(NativeFileTime time)
        {
            long value = ((long)time.High << 32) | time.Low;
            try { return DateTime.FromFileTimeUtc(value).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
        }

        private static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请先选择文件夹或磁盘。", "path");
            string value = path.Replace('/', '\\');
            // Preserve actual leading/trailing filename spaces; only the UI decides
            // whether surrounding input whitespace should be trimmed.
            if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                value = "\\\\" + value.Substring(8);
            else if (value.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(4);
            if (value.StartsWith("\\\\.\\", StringComparison.Ordinal) || value.IndexOfAny(new char[] { '*', '?', '\0' }) >= 0)
                throw new ArgumentException("路径不能包含通配符或设备名称。", "path");
            StringBuilder buffer = new StringBuilder(Math.Max(512, value.Length + 8));
            uint length = GetFullPathNameW(value, (uint)buffer.Capacity, buffer, IntPtr.Zero);
            if (length == 0) throw PathException(value, Marshal.GetLastWin32Error());
            if (length >= buffer.Capacity)
            {
                buffer = new StringBuilder(checked((int)length + 1));
                length = GetFullPathNameW(value, (uint)buffer.Capacity, buffer, IntPtr.Zero);
                if (length == 0 || length >= buffer.Capacity) throw new IOException("无法解析路径：" + value);
            }
            string full = buffer.ToString();
            while (full.Length > 3 && full.EndsWith("\\", StringComparison.Ordinal))
                full = full.Substring(0, full.Length - 1);
            return full;
        }

        private static string NativePath(string fullPath)
        {
            if (fullPath.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + fullPath.Substring(2);
            return "\\\\?\\" + fullPath;
        }

        private static string RootName(string path)
        {
            int separator = path.LastIndexOf('\\');
            return separator >= 0 && separator < path.Length - 1 ? path.Substring(separator + 1) : path;
        }

        private static string AppendPath(string directory, string name)
        {
            return directory.EndsWith("\\", StringComparison.Ordinal) ? directory + name : directory + "\\" + name;
        }

        private static Exception PathException(string path, int error)
        {
            string message = "无法读取路径：" + path + "（" + new Win32Exception(error).Message + "，错误 " + error + "）";
            if (error == AccessDenied) return new UnauthorizedAccessException(message);
            if (error == FileNotFound || error == PathNotFound) return new DirectoryNotFoundException(message);
            return new IOException(message, new Win32Exception(error));
        }

        private static void RecordError(ScanResult result, string path, int error)
        {
            result.ErrorCount++;
            AddIssue(result, "未能完整读取：" + path + "（" + new Win32Exception(error).Message + "，错误 " + error + "）");
        }

        private static void AddIssue(ScanResult result, string text)
        {
            if (result.Issues.Count < IssueLimit) result.Issues.Add(text);
        }

        private sealed class ProgressReporter
        {
            public long FileCount, FolderCount, Bytes;
            private readonly Action<ScanProgress> callback;
            private readonly Stopwatch timer;
            private long lastMilliseconds;

            public ProgressReporter(Action<ScanProgress> callback, Stopwatch timer)
            {
                this.callback = callback;
                this.timer = timer;
                lastMilliseconds = -100;
            }

            public void Report(string path, bool force)
            {
                if (callback == null || (!force && timer.ElapsedMilliseconds - lastMilliseconds < 100)) return;
                lastMilliseconds = timer.ElapsedMilliseconds;
                ScanProgress progress = new ScanProgress();
                progress.FileCount = FileCount;
                progress.FolderCount = FolderCount;
                progress.Bytes = Bytes;
                progress.CurrentPath = path;
                callback(progress);
            }
        }

        private sealed class EntryComparer : IComparer<Entry>
        {
            public static readonly EntryComparer Instance = new EntryComparer();
            public int Compare(Entry x, Entry y)
            {
                int size = y.SizeBytes.CompareTo(x.SizeBytes);
                if (size != 0) return size;
                int name = StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
                return name != 0 ? name : StringComparer.Ordinal.Compare(x.Name, y.Name);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime { public uint Low, High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeData
        {
            public uint Attributes;
            public NativeFileTime CreationTime, AccessTime, WriteTime;
            public uint SizeHigh, SizeLow;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public NativeFileTime CreationTime, AccessTime, WriteTime;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
        }

        private sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeFindHandle() : base(true) { }
            protected override bool ReleaseHandle() { return FindClose(handle); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileAttributesExW(string path, int informationLevel, out AttributeData data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFullPathNameW(string path, uint length, StringBuilder buffer, IntPtr filePart);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFindHandle FindFirstFileExW(string path, int informationLevel, out FindData data, int searchOperation, IntPtr searchFilter, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFileW(SafeFindHandle find, out FindData data);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr find);
    }
}
