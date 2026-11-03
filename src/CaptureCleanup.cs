using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ScreenLine
{
    internal sealed class CleanupOptions
    {
        public bool Enabled = true;
        public int Days = CleanupRules.DefaultDays;
        public int ThresholdMb = CleanupRules.DefaultThresholdMb;
        public DateTime NowUtc = DateTime.UtcNow;
        public IEnumerable<string> ProtectedFiles;
    }

    internal sealed class CleanupResult
    {
        public int Deleted;
        public long FreedBytes;
        public long RemainingBytes;
        public int ProtectedSkipped;
        public bool ReachedThreshold;
        public bool Skipped;
        public readonly List<string> Failures = new List<string>();
    }

    internal static class CleanupRules
    {
        public const int DefaultDays = 3;
        public const int DefaultThresholdMb = 500;
        public const int MaximumDays = 3650;
        public const int MaximumThresholdMb = 1024 * 1024;

        internal static long ThresholdBytes(int megabytes) { return Math.Max(0, megabytes) * 1024L * 1024L; }

        internal static DateTime Cutoff(DateTime nowUtc, int days)
        {
            return nowUtc.AddDays(-Math.Max(1, Math.Min(MaximumDays, days)));
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double value = bytes / 1024.0;
            if (value < 1024) return value.ToString("0.#") + " KB";
            value /= 1024.0;
            if (value < 1024) return value.ToString("0.#") + " MB";
            return (value / 1024.0).ToString("0.##") + " GB";
        }
    }

    // 只处理 data/Captures 下的文件；当前篮子、桌面挂图与编辑中的图片由调用方传入保护名单。
    internal static class CaptureCleanup
    {
        // 刚落盘或仍在写入的截图不参与超阈值删除，避免清理与导入互相踩踏。
        static readonly TimeSpan WriteGrace = TimeSpan.FromMinutes(2);

        internal static long DirectoryBytes(string directory)
        {
            long total = 0;
            foreach (var file in Scan(directory))
            {
                try { total += file.Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return total;
        }

        internal static CleanupResult Run(string inbox, CleanupOptions options)
        {
            var result = new CleanupResult();
            if (options == null) options = new CleanupOptions();
            long threshold = CleanupRules.ThresholdBytes(options.ThresholdMb);
            var files = Scan(inbox);
            long total = 0;
            foreach (var file in files)
            {
                try { total += file.Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            result.RemainingBytes = total;
            if (!options.Enabled) { result.Skipped = true; result.ReachedThreshold = total <= threshold; return result; }
            if (files.Count == 0) { result.ReachedThreshold = true; return result; }
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (options.ProtectedFiles != null)
                foreach (string path in options.ProtectedFiles)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    try { protectedPaths.Add(Path.GetFullPath(path)); }
                    catch (ArgumentException) { }
                    catch (NotSupportedException) { }
                }
            var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime cutoff = CleanupRules.Cutoff(options.NowUtc, options.Days);
            var remaining = new List<FileInfo>();
            foreach (var file in Ordered(files))
            {
                if (LastWrite(file) > cutoff) remaining.Add(file);
                else if (!Delete(file, inbox, protectedPaths, counted, result)) remaining.Add(file);
            }
            if (result.RemainingBytes > threshold)
            {
                var kept = new List<FileInfo>();
                foreach (var file in Ordered(remaining))
                {
                    if (result.RemainingBytes <= threshold) kept.Add(file);
                    else if (LastWrite(file) > options.NowUtc - WriteGrace) kept.Add(file);
                    else if (!Delete(file, inbox, protectedPaths, counted, result)) kept.Add(file);
                }
            }
            result.ReachedThreshold = result.RemainingBytes <= threshold;
            return result;
        }

        static IEnumerable<FileInfo> Ordered(IEnumerable<FileInfo> files)
        {
            return files.OrderBy(LastWrite).ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase);
        }

        static DateTime LastWrite(FileInfo file)
        {
            try { return file.LastWriteTimeUtc; }
            catch (IOException) { return DateTime.MaxValue; }
            catch (UnauthorizedAccessException) { return DateTime.MaxValue; }
        }

        static bool Delete(FileInfo file, string root, HashSet<string> protectedPaths, HashSet<string> counted, CleanupResult result)
        {
            string path;
            try { path = Path.GetFullPath(file.FullName); }
            catch (Exception) { return false; }
            try
            {
                if (!FileRules.IsInside(path, root)) return false;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            if (protectedPaths.Contains(path))
            {
                if (counted.Add(path)) result.ProtectedSkipped++;
                return false;
            }
            long size = 0;
            try { size = file.Length; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                File.Delete(path);
                result.Deleted++;
                result.FreedBytes += size;
                result.RemainingBytes -= size;
                return true;
            }
            catch (Exception error)
            {
                result.Failures.Add(Path.GetFileName(path) + "：" + error.Message);
                return false;
            }
        }

        static List<FileInfo> Scan(string root)
        {
            var files = new List<FileInfo>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return files;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                try
                {
                    foreach (string child in Directory.GetDirectories(directory))
                    {
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                        pending.Push(child);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                try { files.AddRange(new DirectoryInfo(directory).GetFiles()); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return files;
        }
    }
}
