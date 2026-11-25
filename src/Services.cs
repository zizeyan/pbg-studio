// SPDX-License-Identifier: GPL-3.0-only
// PbgStudio offline services. C# 5, .NET Framework 4.7.2; no NuGet packages.
// Renderer arguments follow LDView's Help.html (tcobbs/ldview repository).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace PbgStudio
{
    /// <summary>Indexes an unpacked LDraw folder. Build with Scan on a worker thread,
    /// then publish the completed instance to the UI. Never flatten subdirectories.</summary>
    public sealed class PartLibrary
    {
        private Dictionary<string, string> index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new object();
        public string Root { get; private set; }
        public int Count { get; private set; }
        public string VersionStamp { get; private set; }
        public List<string> Warnings { get; private set; }

        public PartLibrary(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("请选择已解压的 LDraw 根目录。");
            Root = Path.GetFullPath(root);
            Warnings = new List<string>();
            VersionStamp = "unscanned";
        }

        public void Scan() { Scan(CancellationToken.None); }

        public void Scan(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("LDraw 目录不存在：" + Root);
            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var metadata = new List<string>();
            var warnings = new List<string>();
            // Official files win when a matching unofficial file also exists.
            string[] sourceDirectories = { "parts", "p", "models", "unofficial/parts", "unofficial/p", "unofficial/models" };
            int sources = 0;
            foreach (string rel in sourceDirectories)
            {
                string folder = FindDirectoryCaseInsensitive(Root, rel);
                if (folder == null) continue;
                sources++;
                foreach (string path in EnumerateSafe(folder, warnings, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string ext = Path.GetExtension(path);
                    if (!ext.Equals(".dat", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".ldr", StringComparison.OrdinalIgnoreCase)) continue;
                    string key = path.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    if (!next.ContainsKey(key)) next.Add(key, path);
                    string rootedKey = path.Substring(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    if (!next.ContainsKey(rootedKey)) next.Add(rootedKey, path);
                    if (files.Add(path))
                    {
                        try
                        {
                            FileInfo info = new FileInfo(path);
                            metadata.Add(rootedKey.ToLowerInvariant() + "|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                        }
                        catch (IOException ex) { warnings.Add(path + "：" + ex.Message); }
                        catch (UnauthorizedAccessException ex) { warnings.Add(path + "：" + ex.Message); }
                    }
                }
            }
            if (sources == 0) throw new InvalidDataException("目录内没有 parts、p 或 models 文件夹。请选择已解压的 LDraw 库根目录，不能选择 complete.zip。");
            metadata.Sort(StringComparer.Ordinal);
            string config = Path.Combine(Root, "LDConfig.ldr");
            if (File.Exists(config))
            {
                var info = new FileInfo(config);
                metadata.Add("LDConfig|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            string stamp = ServiceHash.Sha256(Root.ToLowerInvariant() + "\n" + String.Join("\n", metadata));
            lock (sync)
            {
                index = next;
                Count = files.Count;
                VersionStamp = stamp;
                Warnings = warnings;
            }
        }

        public string Find(string reference)
        {
            string error;
            string key = ImportService.NormalizePartId(reference, out error);
            if (key == null) return null;
            string path;
            lock (sync)
            {
                if (!index.TryGetValue(key.Replace('\\', '/'), out path)) return null;
            }
            return File.Exists(path) ? path : null;
        }

        private static string FindDirectoryCaseInsensitive(string root, string relative)
        {
            string current = root;
            foreach (string piece in relative.Split('/'))
            {
                string direct = Path.Combine(current, piece);
                if (Directory.Exists(direct)) { current = direct; continue; }
                if (!Directory.Exists(current)) return null;
                string match = Directory.GetDirectories(current).FirstOrDefault(x => Path.GetFileName(x).Equals(piece, StringComparison.OrdinalIgnoreCase));
                if (match == null) return null;
                current = match;
            }
            return current;
        }

        private static IEnumerable<string> EnumerateSafe(string root, List<string> warnings, CancellationToken token)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                string dir = pending.Pop();
                string[] children, files;
                try
                {
                    files = Directory.GetFiles(dir);
                    children = Directory.GetDirectories(dir);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw;
                    warnings.Add(dir + "：" + ex.Message);
                    continue;
                }
                foreach (string file in files) yield return file;
                for (int i = children.Length - 1; i >= 0; --i)
                {
                    try
                    {
                        if ((File.GetAttributes(children[i]) & FileAttributes.ReparsePoint) == 0) pending.Push(children[i]);
                        else warnings.Add("未递归扫描链接目录：" + children[i]);
                    }
                    catch (IOException ex) { warnings.Add(children[i] + "：" + ex.Message); }
                    catch (UnauthorizedAccessException ex) { warnings.Add(children[i] + "：" + ex.Message); }
                }
            }
        }
    }

    public sealed class AppSettings
    {
        public string LDrawRoot { get; set; }
        public string LDViewPath { get; set; }
        public string LastProject { get; set; }
        public int TimeoutSeconds { get; set; }
        public string LoadWarning { get; private set; }

        public AppSettings() { LDrawRoot = ""; LDViewPath = ""; LastProject = ""; TimeoutSeconds = 30; LoadWarning = ""; }

        public static AppSettings Load(string dataDir)
        {
            AppSettings result = new AppSettings();
            string path = Path.Combine(dataDir, "settings.xml");
            if (!File.Exists(path)) return result;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 };
                var doc = new XmlDocument { XmlResolver = null };
                using (XmlReader reader = XmlReader.Create(path, settings)) doc.Load(reader);
                result.LDrawRoot = ReadText(doc, "/PbgStudio/LDrawRoot");
                result.LDViewPath = ReadText(doc, "/PbgStudio/LDViewPath");
                result.LastProject = ReadText(doc, "/PbgStudio/LastProject");
                int timeout;
                if (Int32.TryParse(ReadText(doc, "/PbgStudio/TimeoutSeconds"), out timeout) && timeout >= 5 && timeout <= 120) result.TimeoutSeconds = timeout;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is XmlException)) throw;
                result.LoadWarning = "设置文件无法读取，已使用默认设置：" + ex.Message;
            }
            return result;
        }

        public void Save(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            string path = Path.Combine(dataDir, "settings.xml");
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (XmlWriter writer = XmlWriter.Create(temp, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                {
                    writer.WriteStartDocument(); writer.WriteStartElement("PbgStudio");
                    writer.WriteElementString("LDrawRoot", LDrawRoot ?? "");
                    writer.WriteElementString("LDViewPath", LDViewPath ?? "");
                    writer.WriteElementString("LastProject", LastProject ?? "");
                    writer.WriteElementString("TimeoutSeconds", Math.Max(5, Math.Min(120, TimeoutSeconds)).ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndElement(); writer.WriteEndDocument();
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        private static string ReadText(XmlDocument doc, string path) { XmlNode node = doc.SelectSingleNode(path); return node == null ? "" : node.InnerText; }

        public static string AutoDetectLDView()
        {
            var roots = new List<string> { AppDomain.CurrentDomain.BaseDirectory, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LDView") };
            foreach (string env in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
            {
                string value = Environment.GetEnvironmentVariable(env);
                if (!String.IsNullOrEmpty(value)) { roots.Add(Path.Combine(value, "LDView")); roots.Add(Path.Combine(value, "LDraw", "LDView")); }
            }
            foreach (string root in roots)
                foreach (string exe in new[] { "LDView64.exe", "LDView.exe" })
                    if (File.Exists(Path.Combine(root, exe))) return Path.Combine(root, exe);
            return "";
        }

        public static string AutoDetectLDraw()
        {
            var roots = new List<string> { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LDraw") };
            string envRoot = Environment.GetEnvironmentVariable("LDRAWDIR");
            if (!String.IsNullOrEmpty(envRoot)) roots.Add(envRoot);
            foreach (string env in new[] { "ProgramFiles", "ProgramFiles(x86)", "PUBLIC", "USERPROFILE" })
            {
                string value = Environment.GetEnvironmentVariable(env);
                if (!String.IsNullOrEmpty(value)) { roots.Add(Path.Combine(value, "LDraw")); roots.Add(Path.Combine(value, "Documents", "LDraw")); }
            }
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) roots.Add(@"C:\LDraw");
            foreach (string root in roots) if (Directory.Exists(Path.Combine(root, "parts")) && Directory.Exists(Path.Combine(root, "p"))) return root;
            return "";
        }
    }

    public sealed class ThumbnailResult
    {
        public bool Success { get; set; }
        public string ImagePath { get; set; }
        public string Error { get; set; }
        public bool WasCached { get; set; }
        public string Diagnostics { get; set; }
        public static ThumbnailResult Failure(string error) { return new ThumbnailResult { Success = false, Error = error, ImagePath = "", Diagnostics = "" }; }
    }

    /// <summary>Runs one renderer at a time. Direct process invocation, no shell.
    /// A canceled/superseded preview never leaves its renderer running.</summary>
    public sealed class ThumbnailService : IDisposable
    {
        private readonly string cacheDirectory;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool disposed;
        public int TimeoutSeconds { get; set; }
        public int Size { get; set; }
        private const string RenderOptions = "-SaveZoomToFit=1 -AutoCrop=0 -SaveAlpha=1 -DefaultZoom=0.90 -CheckPartTracker=0 -ShowErrorsMessageBox=0 -SaveActualSize=0 -BackgroundColor3=0xF2F5F8 -ProcessLDConfig=1";

        public ThumbnailService(string cacheDirectory)
        {
            this.cacheDirectory = Path.GetFullPath(cacheDirectory);
            TimeoutSeconds = 30; Size = 360;
        }

        public async Task<ThumbnailResult> RenderAsync(string partPath, PartLibrary library, string ldviewExe, CancellationToken cancellationToken)
        {
            if (disposed) return ThumbnailResult.Failure("预览服务已经关闭。");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            {
                bool entered = false;
                try
                {
                    await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                    entered = true;
                    return await Task.Run(() => Render(partPath, library, ldviewExe, linked.Token), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return ThumbnailResult.Failure("预览已取消。"); }
                catch (Exception ex) { return ThumbnailResult.Failure("缩略图生成失败：" + ex.Message); }
                finally { if (entered) gate.Release(); }
            }
        }

        private ThumbnailResult Render(string partPath, PartLibrary library, string executable, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (library == null || !Directory.Exists(library.Root)) return ThumbnailResult.Failure("请先配置并扫描 LDraw 零件库。");
            if (String.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return ThumbnailResult.Failure("请选择 LDView.exe 或 LDView64.exe 后再预览。");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return ThumbnailResult.Failure("请选择 LDView.exe 或 LDView64.exe，不能使用脚本或命令包装器。");
            if (String.IsNullOrWhiteSpace(partPath) || !File.Exists(partPath)) return ThumbnailResult.Failure("零件文件不存在；请检查库路径或重新扫描。");
            string ext = Path.GetExtension(partPath);
            if (!ext.Equals(".dat", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".ldr", StringComparison.OrdinalIgnoreCase)) return ThumbnailResult.Failure("只支持预览 .dat 或 .ldr 零件文件。");
            executable = Path.GetFullPath(executable); partPath = Path.GetFullPath(partPath);
            Directory.CreateDirectory(cacheDirectory);
            int renderSize = Math.Max(64, Math.Min(1024, Size));
            var partInfo = new FileInfo(partPath); var exeInfo = new FileInfo(executable);
            string fingerprint = partPath + "|" + partInfo.Length + "|" + partInfo.LastWriteTimeUtc.Ticks + "|" + library.VersionStamp + "|" + executable + "|" + exeInfo.Length + "|" + exeInfo.LastWriteTimeUtc.Ticks + "|" + renderSize + "|" + RenderOptions;
            string output = Path.Combine(cacheDirectory, ServiceHash.Sha256(fingerprint) + ".png");
            if (ValidatePng(output)) return new ThumbnailResult { Success = true, ImagePath = output, Error = "", WasCached = true, Diagnostics = "" };
            string tokenName = Guid.NewGuid().ToString("N");
            string temp = Path.Combine(cacheDirectory, "render-" + tokenName + ".png");
            string ini = Path.Combine(cacheDirectory, "render-" + tokenName + ".ini");
            File.WriteAllText(ini, "[General]\r\nCheckPartTracker=0\r\nShowErrorsMessageBox=0\r\n", new UTF8Encoding(false));
            string arguments = BuildArguments(partPath, library.Root, temp, ini, renderSize);
            var diagnostics = new StringBuilder();
            var diagnosticSync = new object();
            try
            {
                var info = new ProcessStartInfo(executable, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var process = new Process { StartInfo = info })
                {
                    var outputComplete = new TaskCompletionSource<bool>();
                    var errorComplete = new TaskCompletionSource<bool>();
                    Action<string> collect = delegate(string message)
                    {
                        if (message == null) return;
                        lock (diagnosticSync) { if (diagnostics.Length < 16000) diagnostics.AppendLine(message); }
                    };
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data == null) outputComplete.TrySetResult(true); else collect(args.Data); };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args) { if (args.Data == null) errorComplete.TrySetResult(true); else collect(args.Data); };
                    bool started = false;
                    try
                    {
                        started = process.Start();
                        if (!started) return ThumbnailResult.Failure("LDView 进程未能启动。");
                        process.BeginOutputReadLine(); process.BeginErrorReadLine();
                        var clock = Stopwatch.StartNew();
                        while (!process.WaitForExit(100))
                        {
                            if (token.IsCancellationRequested || clock.Elapsed.TotalSeconds > Math.Max(5, Math.Min(120, TimeoutSeconds)))
                            {
                                StopProcess(process);
                                if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                                return ThumbnailResult.Failure("LDView 超时。请检查 OpenGL/显卡驱动、零件依赖以及 LDView 是否能够单独打开此零件。");
                            }
                        }
                        // A child process can inherit stdout/stderr handles. Never use the
                        // unbounded WaitForExit() overload, which could hang draining them.
                        Task.WhenAll(outputComplete.Task, errorComplete.Task).Wait(1000);
                        token.ThrowIfCancellationRequested();
                        string log;
                        lock (diagnosticSync) log = diagnostics.ToString().Trim();
                        if (process.ExitCode != 0)
                            return new ThumbnailResult { Success = false, ImagePath = "", Error = "LDView 返回错误代码 " + process.ExitCode + (log.Length == 0 ? "。" : "：" + log), Diagnostics = log };
                        if (!ValidatePng(temp))
                            return new ThumbnailResult { Success = false, ImagePath = "", Error = "LDView 未生成有效 PNG。请在 LDView 中打开此零件，确认库路径及 OpenGL 正常。" + (log.Length == 0 ? "" : "\r\n" + log), Diagnostics = log };
                        if (File.Exists(output)) File.Delete(output);
                        File.Move(temp, output);
                        return new ThumbnailResult { Success = true, ImagePath = output, Error = "", WasCached = false, Diagnostics = log };
                    }
                    finally { if (started) StopProcess(process); }
                }
            }
            finally
            {
                TryDelete(temp); TryDelete(ini);
            }
        }

        public static string BuildArguments(string partPath, string libraryRoot, string outputPng, string iniFile, int size)
        {
            return QuoteArgument(partPath) + " " + QuoteArgument("-LDrawDir=" + libraryRoot) + " " + QuoteArgument("-SaveSnapshot=" + outputPng) + " " + QuoteArgument("-IniFile=" + iniFile) + " -SaveWidth=" + size.ToString(CultureInfo.InvariantCulture) + " -SaveHeight=" + size.ToString(CultureInfo.InvariantCulture) + " " + RenderOptions;
        }

        // Windows CommandLineToArgvW/CRT-compatible quoting, including trailing backslashes.
        public static string QuoteArgument(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (value.IndexOf('\0') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) throw new ArgumentException("路径包含非法控制字符。");
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(ch);
            }
            result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
        }

        public static bool ValidatePng(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var info = new FileInfo(path);
                if (info.Length < 24 || info.Length > 30 * 1024 * 1024) return false;
                byte[] signature = new byte[8];
                using (FileStream input = File.OpenRead(path)) { if (input.Read(signature, 0, 8) != 8) return false; }
                byte[] png = { 137, 80, 78, 71, 13, 10, 26, 10 };
                if (!signature.SequenceEqual(png)) return false;
                using (Image image = Image.FromFile(path)) return image.RawFormat.Guid == ImageFormat.Png.Guid && image.Width > 0 && image.Height > 0 && image.Width <= 4096 && image.Height <= 4096;
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is OutOfMemoryException) return false;
                throw;
            }
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        private static void StopProcess(Process process)
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(2000); } }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); /* tasks release gate before process exit; don't dispose it early */ }
    }

    public sealed class ImportEntry
    {
        public int SourceLine { get; set; }
        public string Original { get; set; }
        public string PartId { get; set; }
        public string RawItem { get; set; }
        public bool IsValid { get; set; }
        public string Error { get; set; }
        public bool IsDuplicate { get; set; }
    }

    public sealed class ImportResult
    {
        public List<ImportEntry> Entries { get; private set; }
        public List<string> Warnings { get; private set; }
        public List<string> Header { get; set; }
        public int PartColumn { get; set; }
        public string SourceKind { get; set; }
        public bool HasErrors { get { return Entries.Any(e => !e.IsValid); } }
        public ImportResult() { Entries = new List<ImportEntry>(); Warnings = new List<string>(); Header = new List<string>(); PartColumn = 0; SourceKind = "纯文本"; }
    }

    public static class ImportService
    {
        private sealed class CsvRow { public List<string> Cells = new List<string>(); public int Line; public string Error = ""; }

        /// <summary>format is auto/text/csv/tsv; column -1 recognizes known headers.
        /// IDs are never silently translated between Rebrickable and LDraw.</summary>
        public static ImportResult Parse(string text, string format = "auto", int column = -1)
        {
            var result = new ImportResult();
            if (String.IsNullOrWhiteSpace(text)) return result;
            if (text.Length > 5 * 1024 * 1024) throw new InvalidDataException("单次导入限 5 MB，请分批导入。");
            text = text.TrimStart('\uFEFF');
            format = (format ?? "auto").ToLowerInvariant();
            char delimiter = '\0';
            if (format == "csv") delimiter = ',';
            else if (format == "tsv") delimiter = '\t';
            else if (format != "text")
            {
                string first = "";
                using (var detector = new StringReader(text))
                {
                    string candidate;
                    while ((candidate = detector.ReadLine()) != null)
                        if (!String.IsNullOrWhiteSpace(candidate)) { first = candidate; break; }
                }
                if (first.IndexOf('\t') >= 0) delimiter = '\t';
                else if (first.IndexOf(',') >= 0) delimiter = ',';
            }
            List<CsvRow> rows;
            if (delimiter == '\0')
            {
                rows = new List<CsvRow>(); int line = 0;
                using (var reader = new StringReader(text))
                {
                    string value;
                    while ((value = reader.ReadLine()) != null) { line++; rows.Add(new CsvRow { Line = line, Cells = new List<string> { value } }); }
                }
            }
            else rows = ParseDelimited(text, delimiter);
            rows = rows.Where(x => x.Cells.Any(c => !String.IsNullOrWhiteSpace(c))).ToList();
            if (rows.Count == 0) return result;
            if (rows.Count > 50000) throw new InvalidDataException("单次导入限 50,000 行，请分批导入。");
            string[] normalized = rows[0].Cells.Select(NormalizeHeader).ToArray();
            int ldrawCol = FindHeader(normalized, "ldraw", "ldrawid", "ldrawpart", "ldrawpartid", "ldrawpartnum", "ldrawpartnumber");
            int genericCol = FindHeader(normalized, "partnum", "partnumber", "partid", "part", "filename", "零件号", "零件编号");
            int quantityCol = FindHeader(normalized, "quantity", "qty", "count", "数量");
            int colorCol = FindHeader(normalized, "ldrawcolor", "ldrawcolorid", "ldrawcolour", "ldrawcolourid");
            int ambiguousColorCol = FindHeader(normalized, "color", "colorid", "colour", "colourid");
            bool hasHeader = ldrawCol >= 0 || genericCol >= 0;
            int selectedCol = column >= 0 ? column : ldrawCol >= 0 ? ldrawCol : genericCol >= 0 ? genericCol : 0;
            result.PartColumn = selectedCol;
            if (hasHeader)
            {
                result.Header = rows[0].Cells;
                if (!String.IsNullOrEmpty(rows[0].Error)) result.Warnings.Add("表头存在 CSV 格式问题：" + rows[0].Error);
                rows.RemoveAt(0);
                result.SourceKind = ldrawCol >= 0 && selectedCol == ldrawCol ? "CSV / LDraw 编号" : "CSV / 外部零件编号";
                if (ldrawCol < 0 || selectedCol != ldrawCol)
                    result.Warnings.Add("外部编号（包括 Rebrickable part_num/Part）不保证与 LDraw 文件名相同。本次只追加 .dat，不自动映射；请核对缺失项或提供明确的 LDraw ID 列。");
                if (quantityCol >= 0) result.Warnings.Add("Quantity/数量作为 [count=数量] 保留，不展开为重复零件。");
                if (ambiguousColorCol >= 0 && colorCol < 0) result.Warnings.Add("Color/Color ID 可能是 Rebrickable 或其他平台色号，已忽略。只有明确标注 LDraw Color 的列才写入颜色。");
            }
            else
            {
                quantityCol = colorCol = -1;
                result.SourceKind = delimiter == '\0' ? "纯文本" : delimiter == '\t' ? "TSV" : "CSV";
                if (delimiter != '\0') result.Warnings.Add("没有识别到表头，按第 " + (selectedCol + 1) + " 列读取零件号；其他列不导入。");
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CsvRow row in rows)
            {
                string original = selectedCol < row.Cells.Count ? row.Cells[selectedCol].Trim() : "";
                if (delimiter == '\0' && (original.StartsWith("#") || original.StartsWith(";") || original.StartsWith("//"))) continue;
                var entry = new ImportEntry { SourceLine = row.Line, Original = original, Error = row.Error, PartId = "", RawItem = "" };
                string error; string part = NormalizePartId(original, out error);
                if (!String.IsNullOrEmpty(error)) entry.Error = AppendError(entry.Error, error);
                if (selectedCol >= row.Cells.Count) entry.Error = AppendError(entry.Error, "缺少零件号列。");
                entry.PartId = part ?? ""; entry.RawItem = entry.PartId;
                if (quantityCol >= 0 && quantityCol < row.Cells.Count && !String.IsNullOrWhiteSpace(row.Cells[quantityCol]))
                {
                    int quantity;
                    if (!Int32.TryParse(row.Cells[quantityCol].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out quantity) || quantity < 1 || quantity > 1000000)
                        entry.Error = AppendError(entry.Error, "数量必须是 1–1,000,000 的整数。");
                    else entry.RawItem += ":[count=" + quantity.ToString(CultureInfo.InvariantCulture) + "]";
                }
                if (colorCol >= 0 && colorCol < row.Cells.Count && !String.IsNullOrWhiteSpace(row.Cells[colorCol]))
                {
                    int color;
                    if (!Int32.TryParse(row.Cells[colorCol].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out color) || color < 0)
                        entry.Error = AppendError(entry.Error, "LDraw 色号必须是非负整数。");
                    else entry.RawItem += (entry.RawItem.IndexOf(':') < 0 ? ":" : "") + "[color=" + color.ToString(CultureInfo.InvariantCulture) + "]";
                }
                entry.IsValid = String.IsNullOrEmpty(entry.Error);
                if (entry.IsValid) entry.IsDuplicate = !seen.Add(entry.RawItem);
                result.Entries.Add(entry);
            }
            int duplicates = result.Entries.Count(x => x.IsDuplicate);
            if (duplicates > 0) result.Warnings.Add("检测到 " + duplicates + " 个完全相同的零件条目；是否去重由导入界面决定。");
            return result;
        }

        public static string NormalizePartId(string input, out string error)
        {
            error = "";
            if (String.IsNullOrWhiteSpace(input)) { error = "零件号为空。"; return null; }
            string value = input.Trim().Replace('\\', '/');
            if (value.Length > 240) { error = "零件号过长。"; return null; }
            if (value.StartsWith("/") || value.IndexOf(':') >= 0 || value.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*', '[', ']', ';', '=', '\r', '\n', '\t', '\0' }) >= 0 || value.Any(Char.IsControl))
            { error = "只接受库内相对零件号，不能包含绝对路径、参数或控制字符。"; return null; }
            string[] parts = value.Split('/');
            if (parts.Any(x => String.IsNullOrWhiteSpace(x) || x == "." || x == ".." || x != x.Trim() || x.EndsWith("."))) { error = "零件路径包含空目录、上级目录或非法空格。"; return null; }
            // LDraw names can contain spaces and punctuation such as '-' and '_'.
            // Restrict the extension, not the catalog number, to avoid inventing mappings.
            string extension = Path.GetExtension(value);
            if (extension.Length == 0) value += ".dat";
            else if (!extension.Equals(".dat", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".ldr", StringComparison.OrdinalIgnoreCase))
            { error = "仅支持 .dat、.ldr 或不含扩展名的零件号。"; return null; }
            return value;
        }

        private static string NormalizeHeader(string text) { return Regex.Replace((text ?? "").Trim().TrimStart('\uFEFF').ToLowerInvariant(), @"[\s_\-]", ""); }
        private static int FindHeader(string[] columns, params string[] names) { for (int i = 0; i < columns.Length; i++) if (names.Contains(columns[i])) return i; return -1; }
        private static string AppendError(string old, string next) { return String.IsNullOrEmpty(old) ? next : old + " " + next; }

        private static List<CsvRow> ParseDelimited(string text, char delimiter)
        {
            var result = new List<CsvRow>(); var row = new CsvRow { Line = 1 }; var cell = new StringBuilder();
            bool quoted = false, afterQuote = false; int line = 1;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else { quoted = false; afterQuote = true; }
                    }
                    else
                    {
                        if (ch == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; cell.Append('\n'); line++; }
                        else { cell.Append(ch); if (ch == '\n') line++; }
                    }
                    continue;
                }
                if (ch == delimiter)
                {
                    row.Cells.Add(cell.ToString()); cell.Length = 0; afterQuote = false; continue;
                }
                if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Cells.Add(cell.ToString()); cell.Length = 0; result.Add(row); line++; row = new CsvRow { Line = line }; afterQuote = false; continue;
                }
                if (ch == '"' && cell.Length == 0 && !afterQuote) { quoted = true; continue; }
                if (afterQuote)
                {
                    if (Char.IsWhiteSpace(ch)) continue;
                    row.Error = "CSV 引号闭合后存在多余内容。";
                }
                else if (ch == '"') row.Error = "CSV 字段中的引号未正确转义。";
                cell.Append(ch);
            }
            if (quoted) row.Error = "CSV 引号未闭合。";
            if (cell.Length != 0 || row.Cells.Count != 0 || afterQuote || quoted)
            { row.Cells.Add(cell.ToString()); result.Add(row); }
            return result;
        }
    }

    internal static class ServiceHash
    {
        public static string Sha256(string text)
        {
            using (SHA256 algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }
}
