// SPDX-License-Identifier: GPL-3.0-only
// PbgStudio: lossless LDCad part-bin document model.
// C# 5 / .NET Framework 4.7.2; no external packages.
// Format reference: https://www.melkert.net/LDCad/tech/partBin
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PbgStudio
{
    public enum PbgItemKind { Part, GroupReference, Other }

    /// <summary>A snapshot of an item. LineId remains stable until Reload().</summary>
    public sealed class PbgItem
    {
        public int LineId { get; internal set; }
        public int Index { get; internal set; }
        public string Raw { get; internal set; }
        public string Reference { get; internal set; }
        public PbgItemKind Kind { get; internal set; }

        public string GetParameter(string key)
        {
            if (String.IsNullOrEmpty(key)) return String.Empty;
            MatchCollection matches = Regex.Matches(Raw, @"\[([^=\]\r\n]+)=([^\]\r\n]*)\]");
            string value = String.Empty;
            foreach (Match m in matches)
                if (String.Equals(m.Groups[1].Value.Trim(), key, StringComparison.OrdinalIgnoreCase))
                    value = m.Groups[2].Value.Trim();
            return value;
        }

        public static PbgItem Parse(string raw)
        {
            return Parse(raw, 0, 0, false);
        }

        internal static PbgItem Parse(string raw, int lineId, int index, bool toolGroup)
        {
            string content = PbgDocument.WithoutInlineComment(raw ?? String.Empty).Trim();
            int parameterStart = content.IndexOf(":", StringComparison.Ordinal);
            string reference = (parameterStart < 0 ? content : content.Substring(0, parameterStart)).Trim();
            PbgItemKind kind = PbgItemKind.Other;
            if (!toolGroup && reference.Length > 0 && !PbgDocument.IsComment(reference))
            {
                // On Linux, Path.GetExtension does not regard '\\' as a separator.
                // PBG paths use Windows or POSIX separators on either platform.
                string basename = reference.Replace('\\', '/');
                int slash = basename.LastIndexOf('/');
                if (slash >= 0) basename = basename.Substring(slash + 1);
                int dot = basename.LastIndexOf('.');
                if (dot < 0 && reference.IndexOfAny(new char[] { '[', ']', '<', '>', '=', ':', '\t' }) < 0)
                    kind = PbgItemKind.GroupReference;
                else if (dot >= 0 && dot < basename.Length - 1 &&
                    !basename.EndsWith(".pbg", StringComparison.OrdinalIgnoreCase))
                    kind = PbgItemKind.Part;
            }
            return new PbgItem { LineId = lineId, Index = index, Raw = raw ?? String.Empty,
                Reference = reference, Kind = kind };
        }
    }

    public sealed class PbgConflictException : IOException
    {
        public PbgConflictException(string message) : base(message) { }
    }

    /// <summary>
    /// One .pbg document. Unknown lines, sections, comments, BOM and individual EOLs
    /// are retained. Group references are extensionless and root-relative; resolving
    /// them belongs to the workspace, not this file-local AST.
    /// </summary>
    public sealed class PbgDocument
    {
        private sealed class Line
        {
            public int Id;
            public string Text;
            public string Eol;
        }

        private readonly List<Line> _lines = new List<Line>();
        private Encoding _encoding;
        private byte[] _preamble;
        private byte[] _originalBytes;
        private byte[] _originalHash;
        private bool _existedAtLoad;
        private bool _isNew;
        private bool _changed;
        private bool _finalNewline;
        private int _nextLineId = 1;

        public string Path { get; private set; }
        public string EncodingName { get { return _encoding.WebName + (_preamble.Length > 0 ? " + BOM" : ""); } }
        public string NewLine { get; private set; }
        public string Caption { get { return GetOption("caption", System.IO.Path.GetFileNameWithoutExtension(Path)); } }
        public string Kind { get { return GetOption("kind", "basic"); } }
        public bool CanEditItems { get { return String.Equals(Kind, "basic", StringComparison.OrdinalIgnoreCase); } }
        public bool IsDirty { get { return _isNew || (_changed && !BytesEqual(_originalBytes, ToBytes())); } }
        public bool IsNew { get { return _isNew; } }

        public static PbgDocument Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择 .pbg 文件。", "path");
            string full = System.IO.Path.GetFullPath(path);
            byte[] bytes = File.ReadAllBytes(full);
            PbgDocument doc = FromBytes(full, bytes);
            doc._existedAtLoad = true;
            return doc;
        }

        public static PbgDocument FromBytes(string path, byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            PbgDocument doc = new PbgDocument();
            doc.Path = System.IO.Path.GetFullPath(path);
            doc._originalBytes = (byte[])bytes.Clone();
            doc._originalHash = Hash(bytes);
            doc._existedAtLoad = File.Exists(doc.Path);
            string text = doc.Decode(bytes);
            doc.ParseLines(text);
            return doc;
        }

        public static PbgDocument Create(string path, string caption)
        {
            ValidateSingleLine(caption ?? String.Empty, "caption");
            string initial = "[options]\r\nkind=basic\r\ncaption=" + (caption ?? String.Empty) +
                "\r\ndescription=\r\npicture=\r\nmascot=\r\nsortOn=none\r\nsortDesc=false\r\nsortCaseInSens=true\r\n<items>\r\n";
            PbgDocument doc = FromBytes(path, new UTF8Encoding(false, true).GetBytes(initial));
            // Create never adopts or overwrites an existing target by accident.
            if (File.Exists(doc.Path)) throw new IOException("目标文件已经存在：" + doc.Path);
            doc._existedAtLoad = false;
            doc._isNew = true;
            return doc;
        }

        public List<PbgItem> Items
        {
            get
            {
                List<PbgItem> result = new List<PbgItem>();
                bool items = false;
                bool tools = String.Equals(Kind, "tools", StringComparison.OrdinalIgnoreCase);
                foreach (Line line in _lines)
                {
                    string section;
                    char delimiter;
                    if (TryHeader(line.Text, out section, out delimiter))
                    {
                        items = delimiter == '<' && String.Equals(section, "items", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!items || String.IsNullOrWhiteSpace(line.Text) || IsComment(line.Text)) continue;
                    result.Add(PbgItem.Parse(line.Text, line.Id, result.Count, tools));
                }
                return result;
            }
        }

        public string GetOption(string key) { return GetOption(key, String.Empty); }

        public string GetOption(string key, string fallback)
        {
            int index = FindOption(key);
            if (index < 0) return fallback;
            string raw = _lines[index].Text;
            return WithoutInlineComment(raw.Substring(raw.IndexOf('=') + 1)).Trim();
        }

        /// <summary>Changes only the effective option line; preserves its spacing and inline comment.</summary>
        public void SetOption(string key, string value)
        {
            if (String.IsNullOrWhiteSpace(key) || key.IndexOfAny(new char[] { '=', '[', ']', '<', '>', '\r', '\n' }) >= 0)
                throw new ArgumentException("属性名无效。", "key");
            ValidateSingleLine(value ?? String.Empty, "value");
            value = value ?? String.Empty;
            int index = FindOption(key);
            if (index >= 0)
            {
                string raw = _lines[index].Text;
                int equals = raw.IndexOf('=');
                string previous = raw.Substring(equals + 1);
                int start = 0;
                while (start < previous.Length && Char.IsWhiteSpace(previous[start])) start++;
                string content = WithoutInlineComment(previous);
                int end = content.Length;
                while (end > start && Char.IsWhiteSpace(content[end - 1])) end--;
                string prefix = previous.Substring(0, start);
                string suffix = previous.Substring(end);
                // All-whitespace values cannot count the same bytes as prefix and suffix.
                if (end < start) suffix = previous.Substring(start);
                if (value.Length > 0 && suffix.Length > 0 && !Char.IsWhiteSpace(suffix[0]) &&
                    (suffix[0] == '#' || suffix[0] == ';' || suffix.StartsWith("//", StringComparison.Ordinal)))
                    suffix = " " + suffix;
                string updated = raw.Substring(0, equals + 1) + prefix + value + suffix;
                if (updated != raw) { _lines[index].Text = updated; _changed = true; }
                return;
            }
            int optionsHeader = -1;
            int optionsEnd = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                string section;
                char delimiter;
                if (!TryHeader(_lines[i].Text, out section, out delimiter)) continue;
                if (optionsHeader >= 0 && optionsEnd < 0) optionsEnd = i;
                if (delimiter == '[' && String.Equals(section, "options", StringComparison.OrdinalIgnoreCase))
                {
                    optionsHeader = i;
                    optionsEnd = -1;
                }
            }
            if (optionsHeader < 0)
            {
                InsertLine(0, "[options]");
                InsertLine(1, key.Trim() + "=" + value);
            }
            else InsertLine(optionsEnd < 0 ? _lines.Count : optionsEnd, key.Trim() + "=" + value);
            _changed = true;
        }

        public int InsertItem(string raw, int index = -1)
        {
            RequireEditableItems();
            ValidateItem(raw);
            List<PbgItem> items = Items;
            if (index < 0) index = items.Count;
            if (index > items.Count) throw new ArgumentOutOfRangeException("index");
            int lineIndex;
            if (index < items.Count) lineIndex = FindLine(items[index].LineId);
            else if (items.Count > 0) lineIndex = FindLine(items[items.Count - 1].LineId) + 1;
            else lineIndex = EnsureItemsSection();
            Line line = InsertLine(lineIndex, raw.Trim());
            _changed = true;
            return line.Id;
        }

        public void UpdateItem(int lineId, string raw)
        {
            RequireEditableItems();
            ValidateItem(raw);
            FindItem(lineId); // Prevent a caller from replacing a section/option through a line id.
            int index = FindLine(lineId);
            string original = _lines[index].Text;
            int prefixLength = original.Length - original.TrimStart().Length;
            string updated = original.Substring(0, prefixLength) + raw.Trim();
            // Editing a value, unlike replacing raw text, keeps an existing inline comment.
            string originalContent = WithoutInlineComment(original);
            if (originalContent.Length < original.Length && WithoutInlineComment(raw).Length == raw.Length)
            {
                int commentStart = originalContent.Length;
                while (commentStart > 0 && Char.IsWhiteSpace(original[commentStart - 1])) commentStart--;
                updated += original.Substring(commentStart);
            }
            if (updated != original) { _lines[index].Text = updated; _changed = true; }
        }

        public void RemoveItems(IEnumerable<int> lineIds)
        {
            RequireEditableItems();
            if (lineIds == null) throw new ArgumentNullException("lineIds");
            HashSet<int> allowed = new HashSet<int>();
            foreach (PbgItem item in Items) allowed.Add(item.LineId);
            HashSet<int> remove = new HashSet<int>(lineIds);
            foreach (int id in remove)
                if (!allowed.Contains(id)) throw new ArgumentException("指定行不是当前分组中的项目。", "lineIds");
            if (remove.Count == 0) return;
            _lines.RemoveAll(delegate(Line line) { return remove.Contains(line.Id); });
            RestoreFinalNewline();
            _changed = true;
        }

        /// <summary>Moves an item to its desired zero-based final item index. Comments remain in place.</summary>
        public void MoveItem(int lineId, int newIndex)
        {
            RequireEditableItems();
            List<PbgItem> items = Items;
            if (newIndex < 0 || newIndex >= items.Count) throw new ArgumentOutOfRangeException("newIndex");
            PbgItem item = FindItem(lineId);
            if (item.Index == newIndex) return;
            int from = FindLine(lineId);
            Line moving = _lines[from];
            _lines.RemoveAt(from);
            List<PbgItem> remaining = Items;
            int destination = newIndex >= remaining.Count
                ? FindLine(remaining[remaining.Count - 1].LineId) + 1
                : FindLine(remaining[newIndex].LineId);
            if (destination > 0 && _lines[destination - 1].Eol.Length == 0) _lines[destination - 1].Eol = NewLine;
            if (moving.Eol.Length == 0) moving.Eol = NewLine;
            _lines.Insert(destination, moving);
            RestoreFinalNewline();
            _changed = true;
        }

        public string GetText()
        {
            StringBuilder builder = new StringBuilder();
            foreach (Line line in _lines) { builder.Append(line.Text); builder.Append(line.Eol); }
            return builder.ToString();
        }

        public byte[] ToBytes()
        {
            if (!_changed) return (byte[])_originalBytes.Clone();
            byte[] content = _encoding.GetBytes(GetText());
            byte[] result = new byte[_preamble.Length + content.Length];
            Buffer.BlockCopy(_preamble, 0, result, 0, _preamble.Length);
            Buffer.BlockCopy(content, 0, result, _preamble.Length, content.Length);
            return result;
        }

        /// <summary>
        /// Restores an in-memory snapshot for undo/redo while retaining the original
        /// disk hash/existence baseline. Previously returned item LineIds are invalid.
        /// </summary>
        public void ApplyBytes(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            // A failed property validation often restores an unchanged snapshot.
            // Keep stable item ids in that case so existing view tags stay valid.
            if (BytesEqual(ToBytes(), bytes)) return;
            PbgDocument parsed = FromBytes(Path, bytes);
            _lines.Clear();
            _lines.AddRange(parsed._lines);
            _encoding = parsed._encoding;
            _preamble = parsed._preamble;
            _finalNewline = parsed._finalNewline;
            _nextLineId = parsed._nextLineId;
            NewLine = parsed.NewLine;
            _changed = true;
        }

        /// <summary>Throws if the source has changed since it was opened/saved. Never overwrites that change.</summary>
        public void CheckExternalChange()
        {
            bool exists = File.Exists(Path);
            if (exists != _existedAtLoad)
                throw new PbgConflictException("文件已被其他程序创建或删除，请重新打开后再保存：" + Path);
            if (exists && !BytesEqual(Hash(File.ReadAllBytes(Path)), _originalHash))
                throw new PbgConflictException("文件已被其他程序修改。为避免覆盖，已取消保存；请重新打开核对：" + Path);
        }

        /// <summary>
        /// Writes a same-directory temporary file and atomically replaces the target.
        /// Existing targets receive a uniquely named .bak backup. The temporary file is
        /// removed on failure; the original is never deleted as a fallback.
        /// Returns the backup path, or null for a new/no-op save.
        /// </summary>
        public string Save()
        {
            CheckExternalChange();
            if (!IsDirty && _existedAtLoad) return null;
            byte[] bytes = ToBytes();
            string directory = System.IO.Path.GetDirectoryName(Path);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string temp = System.IO.Path.Combine(directory, "." + System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = null;
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                CheckExternalChange();
                if (_existedAtLoad)
                {
                    backup = Path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".bak";
                    File.Replace(temp, Path, backup, true);
                }
                else File.Move(temp, Path); // Fails if another process creates the destination.
                _originalBytes = (byte[])bytes.Clone();
                _originalHash = Hash(bytes);
                _existedAtLoad = true;
                _changed = false;
                _isNew = false;
                return backup;
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        public void Reload()
        {
            PbgDocument fresh = Load(Path);
            _lines.Clear();
            _lines.AddRange(fresh._lines);
            _encoding = fresh._encoding;
            _preamble = fresh._preamble;
            _originalBytes = fresh._originalBytes;
            _originalHash = fresh._originalHash;
            _existedAtLoad = true;
            _changed = false;
            _isNew = false;
            _finalNewline = fresh._finalNewline;
            _nextLineId = fresh._nextLineId;
            NewLine = fresh.NewLine;
        }

        private string Decode(byte[] bytes)
        {
            int skip = 0;
            _preamble = new byte[0];
            if (StartsWith(bytes, new byte[] { 0xef, 0xbb, 0xbf }))
            {
                _encoding = new UTF8Encoding(false, true); skip = 3;
            }
            else if (StartsWith(bytes, new byte[] { 0xff, 0xfe, 0x00, 0x00 }) || StartsWith(bytes, new byte[] { 0x00, 0x00, 0xfe, 0xff }))
                throw new InvalidDataException("暂不支持 UTF-32 编码；请先另存为 UTF-8。原文件未修改。");
            else if (StartsWith(bytes, new byte[] { 0xff, 0xfe }))
            {
                _encoding = new UnicodeEncoding(false, false, true); skip = 2;
            }
            else if (StartsWith(bytes, new byte[] { 0xfe, 0xff }))
            {
                _encoding = new UnicodeEncoding(true, false, true); skip = 2;
            }
            else _encoding = new UTF8Encoding(false, true);
            if (skip > 0)
            {
                _preamble = new byte[skip];
                Buffer.BlockCopy(bytes, 0, _preamble, 0, skip);
            }
            string text;
            try { text = _encoding.GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException)
            {
                if (skip > 0) throw new InvalidDataException("文件编码无效，未进行修复或覆盖。");
                // Chinese Windows legacy files are common. Require a lossless round trip
                // rather than silently introducing U+FFFD replacement characters.
                _encoding = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                try { text = _encoding.GetString(bytes); }
                catch (DecoderFallbackException) { throw new InvalidDataException("文件不是有效 UTF-8 / GB18030 文本，原文件未修改。"); }
                if (!BytesEqual(bytes, _encoding.GetBytes(text)))
                    throw new InvalidDataException("无法无损识别文件编码，原文件未修改。");
            }
            if (text.IndexOf('\0') >= 0) throw new InvalidDataException("文件包含空字符，可能不是 .pbg 文本。原文件未修改。");
            return text;
        }

        private void ParseLines(string text)
        {
            _lines.Clear();
            int start = 0;
            int i = 0;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            string firstEol = null;
            while (i < text.Length)
            {
                if (text[i] != '\r' && text[i] != '\n') { i++; continue; }
                int eolStart = i;
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i += 2;
                else i++;
                string eol = text.Substring(eolStart, i - eolStart);
                _lines.Add(new Line { Id = _nextLineId++, Text = text.Substring(start, eolStart - start), Eol = eol });
                if (firstEol == null) firstEol = eol;
                counts[eol] = counts.ContainsKey(eol) ? counts[eol] + 1 : 1;
                start = i;
            }
            if (start < text.Length) _lines.Add(new Line { Id = _nextLineId++, Text = text.Substring(start), Eol = String.Empty });
            NewLine = firstEol ?? "\r\n";
            foreach (KeyValuePair<string, int> pair in counts)
                if (pair.Value > counts[NewLine]) NewLine = pair.Key;
            _finalNewline = _lines.Count > 0 && _lines[_lines.Count - 1].Eol.Length > 0;
        }

        private int FindOption(string key)
        {
            bool options = false;
            int found = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                string section;
                char delimiter;
                string raw = _lines[i].Text;
                if (TryHeader(raw, out section, out delimiter))
                {
                    options = delimiter == '[' && String.Equals(section, "options", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!options || IsComment(raw)) continue;
                int equals = raw.IndexOf('=');
                if (equals >= 0 && String.Equals(raw.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase)) found = i;
            }
            return found;
        }

        private int EnsureItemsSection()
        {
            int insert = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                string section;
                char delimiter;
                if (TryHeader(_lines[i].Text, out section, out delimiter) && delimiter == '<' &&
                    String.Equals(section, "items", StringComparison.OrdinalIgnoreCase)) insert = i + 1;
            }
            if (insert >= 0) return insert;
            InsertLine(_lines.Count, "<items>");
            return _lines.Count;
        }

        private Line InsertLine(int index, string text)
        {
            if (index > 0 && _lines[index - 1].Eol.Length == 0) _lines[index - 1].Eol = NewLine;
            Line line = new Line { Id = _nextLineId++, Text = text, Eol = NewLine };
            _lines.Insert(index, line);
            RestoreFinalNewline();
            return line;
        }

        private void RestoreFinalNewline()
        {
            if (_lines.Count == 0) return;
            Line last = _lines[_lines.Count - 1];
            if (!_finalNewline) last.Eol = String.Empty;
            else if (last.Eol.Length == 0) last.Eol = NewLine;
        }

        private int FindLine(int lineId)
        {
            for (int i = 0; i < _lines.Count; i++) if (_lines[i].Id == lineId) return i;
            throw new ArgumentException("指定行已不存在；请刷新列表。", "lineId");
        }

        private PbgItem FindItem(int lineId)
        {
            foreach (PbgItem item in Items) if (item.LineId == lineId) return item;
            throw new ArgumentException("指定行不是当前分组中的项目。", "lineId");
        }

        private void RequireEditableItems()
        {
            if (!CanEditItems)
                throw new InvalidOperationException("当前 " + Kind + " 分组的项目由 LDCad 动态生成。本工具保留其内容，仅 basic 分组支持列表编辑。");
        }

        private static void ValidateItem(string raw)
        {
            ValidateSingleLine(raw, "raw");
            string text = (raw ?? String.Empty).Trim();
            string section;
            char delimiter;
            if (text.Length == 0 || IsComment(text) || TryHeader(text, out section, out delimiter))
                throw new ArgumentException("请输入单个零件或分组引用，不能是空行、注释或节标题。", "raw");
        }

        private static void ValidateSingleLine(string value, string name)
        {
            if (value == null || value.IndexOfAny(new char[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("内容必须是单行文本，不能包含换行或空字符。", name);
        }

        private static bool TryHeader(string raw, out string name, out char delimiter)
        {
            string trimmed = WithoutInlineComment(raw).Trim();
            name = String.Empty;
            delimiter = '\0';
            if (trimmed.Length < 3) return false;
            char open = trimmed[0];
            char close = trimmed[trimmed.Length - 1];
            if ((open != '[' || close != ']') && (open != '<' || close != '>')) return false;
            string candidate = trimmed.Substring(1, trimmed.Length - 2);
            // A part's [name=value] parameter is not a section.
            if (candidate.Length == 0 || candidate.IndexOfAny(new char[] { '=', '[', ']', '<', '>' }) >= 0) return false;
            name = candidate.Trim();
            delimiter = open;
            return name.Length > 0;
        }

        internal static bool IsComment(string raw)
        {
            string trimmed = (raw ?? String.Empty).TrimStart();
            return trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal) ||
                trimmed.StartsWith("//", StringComparison.Ordinal);
        }

        internal static string WithoutInlineComment(string raw)
        {
            if (String.IsNullOrEmpty(raw)) return raw ?? String.Empty;
            // Preserve common INI-style inline comments without confusing URL slashes
            // or parameter values. Only whitespace-delimited markers outside [] count.
            int brackets = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '[') brackets++;
                else if (raw[i] == ']') brackets = Math.Max(0, brackets - 1);
                if (brackets != 0 || (i > 0 && !Char.IsWhiteSpace(raw[i - 1]))) continue;
                if (raw[i] == '#' || raw[i] == ';' || (raw[i] == '/' && i + 1 < raw.Length && raw[i + 1] == '/'))
                    return raw.Substring(0, i);
            }
            return raw;
        }

        private static bool StartsWith(byte[] value, byte[] prefix)
        {
            if (value.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (value[i] != prefix[i]) return false;
            return true;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static byte[] Hash(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(bytes);
        }
    }
}
