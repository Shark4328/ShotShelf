using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ScreenLine
{
    internal enum ExportScope { Selected, All }
    internal enum ExportFormat { Folder, Zip }

    internal sealed class ExportRequest
    {
        public ExportScope Scope;
        public ExportFormat Format;
        public string Destination;
        public string[] Files;
    }

    internal sealed class ExportResult
    {
        public string Output;
        public int Total;
        public int Succeeded;
        public string Error;
        public readonly List<string> Failures = new List<string>();
        public readonly List<string> FailureReasons = new List<string>();
    }

    internal static class ExportRules
    {
        public const string Prefix = "ShotShelf-导出-";

        internal static string FolderName(DateTime now) { return Prefix + now.ToString("yyyyMMdd-HHmmss"); }

        // 只保留文件名本身，去掉路径分隔符与 Windows 不允许的字符。
        internal static string CleanName(string name)
        {
            string clean = Path.GetFileName(name ?? "");
            foreach (char invalid in Path.GetInvalidFileNameChars()) clean = clean.Replace(invalid, '_');
            clean = clean.Trim().TrimEnd('.');
            return clean.Length == 0 ? "图片" : clean;
        }

        internal static string UniquePath(string directory, string name)
        {
            string candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
            for (int index = 2; index < 10000; index++)
            {
                candidate = Path.Combine(directory, stem + " (" + index + ")" + extension);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
            return Path.Combine(directory, stem + " (" + Guid.NewGuid().ToString("N").Substring(0, 8) + ")" + extension);
        }

        internal static string UniqueEntry(ICollection<string> used, string name)
        {
            if (!used.Contains(name)) { used.Add(name); return name; }
            string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
            for (int index = 2; index < 10000; index++)
            {
                string candidate = stem + " (" + index + ")" + extension;
                if (!used.Contains(candidate)) { used.Add(candidate); return candidate; }
            }
            string fallback = stem + " (" + Guid.NewGuid().ToString("N").Substring(0, 8) + ")" + extension;
            used.Add(fallback); return fallback;
        }
    }

    // 复制与打包都在后台线程执行；调用方负责把进度切回 UI 线程。
    internal static class ExportService
    {
        internal static ExportResult Run(ExportRequest request, Action<int, int> progress)
        {
            var result = new ExportResult { Total = request.Files == null ? 0 : request.Files.Length };
            if (result.Total == 0) { result.Error = "没有可导出的图片。"; return result; }
            string root;
            try
            {
                root = Path.GetFullPath(request.Destination);
                Directory.CreateDirectory(root);
            }
            catch (Exception error)
            {
                result.Error = "无法写入所选位置：" + error.Message;
                return result;
            }
            string name = ExportRules.FolderName(DateTime.Now);
            try
            {
                if (request.Format == ExportFormat.Folder) CopyToFolder(root, name, request.Files, result, progress);
                else WriteZip(root, name, request.Files, result, progress);
            }
            catch (Exception error)
            {
                result.Error = "导出失败：" + error.Message;
            }
            if (result.Succeeded == 0)
            {
                RemoveEmpty(root, result.Output);
                result.Output = null;
                if (result.Error == null) result.Error = "没有可导出的图片，文件可能已被清理。";
            }
            return result;
        }

        static void CopyToFolder(string root, string name, string[] files, ExportResult result, Action<int, int> progress)
        {
            string folder = ExportRules.UniquePath(root, name);
            Directory.CreateDirectory(folder);
            result.Output = folder;
            int done = 0;
            foreach (string path in files)
            {
                string display = SafeFileName(path);
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("文件已被移动或清理");
                    string copy = ExportRules.UniquePath(folder, ExportRules.CleanName(display));
                    File.Copy(path, copy, false);
                    result.Succeeded++;
                }
                catch (Exception error)
                {
                    result.Failures.Add(display); result.FailureReasons.Add(error.Message);
                }
                done += 1;
                if (progress != null) progress(done, files.Length);
            }
        }

        static void WriteZip(string root, string name, string[] files, ExportResult result, Action<int, int> progress)
        {
            string zip = ExportRules.UniquePath(root, name + ".zip");
            string temporary = zip + ".tmp";
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int done = 0;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    foreach (string path in files)
                    {
                        string display = SafeFileName(path);
                        string staged = null;
                        try
                        {
                            if (!File.Exists(path)) throw new FileNotFoundException("文件已被移动或清理");
                            // 先把源文件完整读到暂存文件；读取失败时还不会创建 ZIP 条目。
                            staged = Path.Combine(Path.GetTempPath(), "ShotShelf-export-" + Guid.NewGuid().ToString("N") + ".tmp");
                            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                input.CopyTo(output);
                            string entryName = ExportRules.UniqueEntry(used, ExportRules.CleanName(display));
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                            try
                            {
                                DateTime written = File.GetLastWriteTime(path);
                                if (written.Year >= 1980 && written.Year <= 2107) entry.LastWriteTime = written;
                            }
                            catch (Exception) { }
                            using (var input = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var output = entry.Open()) input.CopyTo(output);
                            result.Succeeded++;
                        }
                        catch (Exception error)
                        {
                            result.Failures.Add(display); result.FailureReasons.Add(error.Message);
                        }
                        finally { TryDelete(staged); }
                        done += 1;
                        if (progress != null) progress(done, files.Length);
                    }
                }
                if (result.Succeeded > 0)
                {
                    File.Move(temporary, zip);
                    result.Output = zip;
                    return;
                }
                TryDelete(temporary);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }

        static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path); }
            catch (ArgumentException) { return path; }
        }

        static void TryDelete(string path)
        {
            try { if (path != null && File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void RemoveEmpty(string root, string output)
        {
            if (output == null) return;
            try
            {
                if (Directory.Exists(output) && !Directory.EnumerateFileSystemEntries(output).Any()) Directory.Delete(output);
                else if (File.Exists(output)) File.Delete(output);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
