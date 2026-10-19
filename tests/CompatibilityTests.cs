using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using DiskSpace;

// Executes the actual original PowerShell functions in a hidden child process,
// then compares their recorded values against ScanEngine on the same fixture.
internal static class CompatibilityTests
{
    private static int assertions;

    public static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Original source and private test output directory are required."); return 2; }
        string original = Path.GetFullPath(args[0]);
        string output = Path.GetFullPath(args[1]);
        if (!File.Exists(original) || !Directory.Exists(output)) { Console.Error.WriteLine("Test inputs do not exist."); return 2; }
        string fixtures = Path.Combine(output, "compatibility-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        try
        {
            string first = Path.Combine(fixtures, "中文 根目录;带分号");
            string second = Path.Combine(fixtures, "第二个 根目录");
            BuildFixtures(first, second);
            Dictionary<string, string> before = Snapshot(fixtures);
            string sourceHash = HashFile(original);
            string baselinePath = Path.Combine(output, "original-baseline.xml");
            RunOriginal(original, new[] { first, second }, baselinePath);
            XmlDocument baseline = new XmlDocument();
            baseline.Load(baselinePath);
            long allOriginalBytes = 0, allNewBytes = 0, allOriginalFiles = 0, allNewFiles = 0;
            foreach (XmlElement root in baseline.SelectNodes("/baseline/root"))
            {
                string path = root.GetAttribute("path");
                ScanResult modern = ScanEngine.Scan(path, CancellationToken.None, null);
                Equal(0L, modern.ErrorCount, "new scan has no fixture errors");
                Equal(false, modern.Cancelled, "new scan completes");
                XmlElement tree = (XmlElement)root.SelectSingleNode("tree");
                CheckStats(tree, modern.Root.SizeBytes, modern.Root.FileCount, modern.Root.FolderCount, "whole root " + path);
                Equal(0L, Number(tree, "errors"), "original whole-tree errors");

                List<Entry> directFiles = modern.Root.Children.Where(e => !e.IsDirectory).ToList();
                XmlElement direct = (XmlElement)root.SelectSingleNode("direct");
                CheckStats(direct, directFiles.Sum(e => e.SizeBytes), directFiles.Sum(e => e.FileCount), 0, "direct-file grouping");
                Equal(0L, Number(direct, "errors"), "original direct-file errors");

                long rowBytes = 0, rowFiles = 0, rowFolders = 0, folderRows = 0;
                int filesRows = 0;
                foreach (XmlElement row in root.SelectNodes("rows/row"))
                {
                    Equal(0L, Number(row, "errors"), "original row errors");
                    string kind = row.GetAttribute("type");
                    if (kind == "Files")
                    {
                        filesRows++;
                        CheckStats(row, directFiles.Sum(e => e.SizeBytes), directFiles.Sum(e => e.FileCount), 0, "original grouped files row");
                    }
                    else
                    {
                        Equal("Folder", kind, "ordinary row type");
                        string rowPath = row.GetAttribute("path");
                        Entry child = modern.Root.Children.Single(e => e.FullPath.Equals(rowPath, StringComparison.OrdinalIgnoreCase));
                        Equal(true, child.IsDirectory, "folder row maps to directory");
                        Equal(row.GetAttribute("name"), child.Name, "folder name preserved");
                        CheckStats(row, child.SizeBytes, child.FileCount, child.FolderCount, "immediate directory " + child.Name);
                        folderRows++;
                    }
                    rowBytes += Number(row, "bytes");
                    rowFiles += Number(row, "files");
                    rowFolders += Number(row, "folders");
                }
                Equal(1, filesRows, "one original grouped direct-file row");
                Equal(modern.Root.SizeBytes, rowBytes, "original row sizes reconstruct full root");
                Equal(modern.Root.FileCount, rowFiles, "original row files reconstruct full root");
                // Each original folder row excluded that row's own directory. Its sum
                // consequently omits the immediate directories counted by a root scan.
                Equal(modern.Root.FolderCount, rowFolders + folderRows, "explicit original folder-count convention");
                allOriginalBytes += rowBytes; allNewBytes += modern.Root.SizeBytes;
                allOriginalFiles += rowFiles; allNewFiles += modern.Root.FileCount;
                Console.WriteLine("PASS original/new compatibility: " + Path.GetFileName(path));
            }
            Equal(143L, allNewBytes, "independent deterministic multi-root byte oracle");
            Equal(8L, allNewFiles, "independent deterministic multi-root file oracle");
            Equal(allOriginalBytes, allNewBytes, "multiple-root total sizes preserved");
            Equal(allOriginalFiles, allNewFiles, "multiple-root file totals preserved");
            Equal(sourceHash, HashFile(original), "original PowerShell source unchanged");
            Dictionary<string, string> after = Snapshot(fixtures);
            Equal(before.Count, after.Count, "fixture entry count unchanged by both scanners");
            foreach (KeyValuePair<string, string> entry in before)
            {
                string actual;
                Equal(true, after.TryGetValue(entry.Key, out actual), "fixture entry remains present");
                Equal(entry.Value, actual, "fixture content, length and last-write time unchanged");
            }
            Console.WriteLine("PASS: " + assertions + " original/new compatibility and read-only assertions.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
        finally
        {
            string normalized = Path.GetFullPath(fixtures);
            if (!normalized.StartsWith(output.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(normalized).StartsWith("compatibility-fixtures-", StringComparison.Ordinal))
                throw new IOException("Fixture cleanup boundary check failed.");
            Directory.Delete(normalized, true);
        }
    }

    private static void BuildFixtures(string first, string second)
    {
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        Write(Path.Combine(first, "根 文件;甲.bin"), 37);
        Write(Path.Combine(first, "零字节.bin"), 0);
        Write(Path.Combine(first, "=公式,测试.bin"), 9);
        string alpha = Path.Combine(first, "一级 文件夹甲");
        string inner = Path.Combine(alpha, "内层");
        Directory.CreateDirectory(Path.Combine(inner, "更深空层"));
        Write(Path.Combine(alpha, "文件;甲.bin"), 13);
        Write(Path.Combine(inner, "嵌套文件.bin"), 42);
        string beta = Path.Combine(first, "一级乙;带分号");
        Directory.CreateDirectory(Path.Combine(beta, "空 子目录"));
        Write(Path.Combine(beta, "文件乙.bin"), 27);
        Directory.CreateDirectory(Path.Combine(first, "空目录"));
        Write(Path.Combine(second, "直接文件.bin"), 3);
        string secondChild = Path.Combine(second, "子文件夹");
        Directory.CreateDirectory(secondChild);
        Write(Path.Combine(secondChild, "另一文件.bin"), 12);
        // Fixed timestamps make the fixture deterministic, and scanning must preserve them.
        foreach (string directory in Directory.GetDirectories(Path.GetDirectoryName(first), "*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(directory, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    }

    private static void Write(string path, int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 17 + length) % 251);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc));
    }

    private static void RunOriginal(string source, string[] paths, string destination)
    {
        StringBuilder script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine("[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)");
        script.AppendLine("$sourceText = [IO.File]::ReadAllText(" + Ps(source) + ", [Text.Encoding]::UTF8)");
        script.AppendLine("$uiBoundary = $sourceText.IndexOf('# Build UI', [StringComparison]::Ordinal)");
        script.AppendLine("if ($uiBoundary -lt 1) { throw 'Original UI boundary not found.' }");
        script.AppendLine(". ([ScriptBlock]::Create($sourceText.Substring(0, $uiBoundary)))");
        // Only repainting is mocked. The original data functions and Add-ResultRow
        // execute unchanged, including their own error handling and folder counts.
        script.AppendLine("function Update-Ui { param([string]$Text, [int]$Progress = -1, [bool]$Force = $false) }");
        script.AppendLine("$settings = New-Object Xml.XmlWriterSettings");
        script.AppendLine("$settings.Encoding = New-Object Text.UTF8Encoding($false)");
        script.AppendLine("$writer = [Xml.XmlWriter]::Create(" + Ps(destination) + ", $settings)");
        script.AppendLine("function Write-StatsElement($tag, $stats) {");
        script.AppendLine("  $writer.WriteStartElement($tag)");
        script.AppendLine("  $writer.WriteAttributeString('bytes', ([Int64]$stats.SizeBytes).ToString([Globalization.CultureInfo]::InvariantCulture))");
        script.AppendLine("  $writer.WriteAttributeString('files', ([Int64]$stats.FileCount).ToString([Globalization.CultureInfo]::InvariantCulture))");
        script.AppendLine("  $writer.WriteAttributeString('folders', ([Int64]$stats.FolderCount).ToString([Globalization.CultureInfo]::InvariantCulture))");
        script.AppendLine("  $writer.WriteAttributeString('errors', ([Int64]$stats.ErrorCount).ToString([Globalization.CultureInfo]::InvariantCulture))");
        script.AppendLine("}");
        script.AppendLine("try { $writer.WriteStartElement('baseline')");
        foreach (string path in paths)
        {
            script.AppendLine("  $script:StopRequested = $false; $script:Rows = @()");
            script.AppendLine("  $writer.WriteStartElement('root'); $writer.WriteAttributeString('path', " + Ps(path) + ")");
            script.AppendLine("  $tree = Measure-DirectoryTree -RootPath " + Ps(path));
            script.AppendLine("  Write-StatsElement 'tree' $tree; $writer.WriteEndElement()");
            script.AppendLine("  $direct = Measure-DirectFiles -RootPath " + Ps(path));
            script.AppendLine("  Write-StatsElement 'direct' $direct; $writer.WriteEndElement()");
            script.AppendLine("  Measure-OneRoot -RootPath " + Ps(path));
            script.AppendLine("  $writer.WriteStartElement('rows')");
            script.AppendLine("  foreach ($row in $script:Rows) { Write-StatsElement 'row' $row; $writer.WriteAttributeString('name', [string]$row.Name); $writer.WriteAttributeString('type', [string]$row.Type); $writer.WriteAttributeString('path', [string]$row.Path); $writer.WriteEndElement() }");
            script.AppendLine("  $writer.WriteEndElement(); $writer.WriteEndElement()");
        }
        script.AppendLine("  $writer.WriteEndElement() } finally { $writer.Dispose() }");
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
        ProcessStartInfo start = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded);
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8; start.StandardErrorEncoding = Encoding.UTF8;
        using (Process process = Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Actual original PowerShell function run failed. " + output + " " + error);
        }
    }

    private static string Ps(string value) { return "'" + value.Replace("'", "''") + "'"; }
    private static long Number(XmlElement element, string name) { return Int64.Parse(element.GetAttribute(name), CultureInfo.InvariantCulture); }
    private static void CheckStats(XmlElement expected, long bytes, long files, long folders, string description)
    {
        Equal(Number(expected, "bytes"), bytes, description + " bytes");
        Equal(Number(expected, "files"), files, description + " files");
        Equal(Number(expected, "folders"), folders, description + " folders");
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            result.Add(directory, "D|" + Directory.GetLastWriteTimeUtc(directory).Ticks);
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            result.Add(file, "F|" + new FileInfo(file).Length + "|" + File.GetLastWriteTimeUtc(file).Ticks + "|" + HashFile(file));
        return result;
    }

    private static string HashFile(string path)
    {
        using (SHA256 algorithm = SHA256.Create())
        using (FileStream file = File.OpenRead(path)) return Convert.ToBase64String(algorithm.ComputeHash(file));
    }

    private static void Equal<T>(T expected, T actual, string description)
    {
        assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(description + ": expected " + expected + ", got " + actual);
    }
}
