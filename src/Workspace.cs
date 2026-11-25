// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PbgStudio
{
    public sealed class GroupLink
    {
        public PbgDocument Parent;
        public PbgDocument Document;
        public int LineId = -1;
        public string Reference;
        public string Error;
        public bool Generated;
    }

    public sealed class WorkspaceSnapshot
    {
        internal Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    }

    // All group references are rooted at the sub-bin root.pbg, never at a child file.
    public sealed class PbgWorkspace
    {
        readonly Dictionary<string, PbgDocument> documents = new Dictionary<string, PbgDocument>(StringComparer.OrdinalIgnoreCase);
        public string RootDirectory { get; private set; }
        public PbgDocument Root { get; private set; }
        public IEnumerable<PbgDocument> Documents { get { return documents.Values; } }
        public bool IsDirty { get { return documents.Values.Any(d => d.IsDirty); } }

        public static PbgWorkspace Open(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            var w = new PbgWorkspace();
            string parent = System.IO.Path.GetDirectoryName(path);
            string found = parent;
            var scan = new DirectoryInfo(parent);
            while (scan != null)
            {
                if (File.Exists(System.IO.Path.Combine(scan.FullName, "root.pbg"))) { found = scan.FullName; break; }
                scan = scan.Parent;
            }
            w.RootDirectory = found;
            w.Root = w.Load(path);
            return w;
        }

        public static PbgWorkspace Create(string path, string caption)
        {
            if (File.Exists(path)) throw new IOException("目标文件已经存在，请选择其他位置。");
            var w = new PbgWorkspace();
            w.RootDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            w.Root = PbgDocument.Create(System.IO.Path.GetFullPath(path), caption);
            w.documents[w.Root.Path] = w.Root;
            return w;
        }

        public PbgDocument Load(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            PbgDocument d;
            if (documents.TryGetValue(path, out d)) return d;
            if (documents.Count >= 2000) throw new IOException("引用文件超过 2000 个，请单独打开目标分支。");
            d = PbgDocument.Load(path);
            documents.Add(path, d);
            return d;
        }

        public string ResolveGroup(string reference)
        {
            if (String.IsNullOrWhiteSpace(reference)) throw new IOException("子分组引用为空。");
            PbgItem parsed = PbgItem.Parse(reference);
            if (parsed.Kind != PbgItemKind.GroupReference || !String.Equals(parsed.Reference, reference, StringComparison.Ordinal) || reference.Any(c => Char.IsControl(c)))
                throw new IOException("子分组文件名不能包含注释符、控制字符或属性标记。");
            if (reference.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|', '\r', '\n' }) >= 0 ||
                reference.StartsWith("/") || reference.StartsWith("\\"))
                throw new IOException("子分组必须使用当前零件框内的相对路径。");
            string relative = reference.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar);
            if (relative.Split(System.IO.Path.DirectorySeparatorChar).Any(p => p == ".." || p == "." || p.Length == 0))
                throw new IOException("子分组引用不允许包含 ..、. 或空路径段。");
            foreach (string segment in relative.Split(System.IO.Path.DirectorySeparatorChar))
            {
                string stem = segment.Split('.')[0].ToUpperInvariant();
                if (segment.EndsWith(" ") || segment.EndsWith(".") || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem))
                    throw new IOException("子分组文件名含有 Windows 保留名称、结尾空格或结尾句点。");
            }
            if (System.IO.Path.HasExtension(relative)) throw new IOException("子分组引用不带 .pbg 扩展名。");
            string resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(RootDirectory, relative + ".pbg"));
            EnsureInsideRoot(resolved);
            return resolved;
        }

        void EnsureInsideRoot(string path)
        {
            string prefix = RootDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!System.IO.Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("引用位置超出当前零件框目录。");
            // Reparse points can escape the selected root despite a normal-looking relative path.
            DirectoryInfo dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(path));
            while (dir != null && dir.FullName.Length >= RootDirectory.Length)
            {
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("引用经过链接目录，请直接打开真实目录中的 root.pbg。");
                dir = dir.Parent;
            }
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("不通过链接文件修改分组，请打开真实文件。");
        }

        public List<GroupLink> Children(PbgDocument document)
        {
            var links = new List<GroupLink>();
            if (document.Kind.Equals("basic", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in document.Items.Where(i => i.Kind == PbgItemKind.GroupReference))
                {
                    var link = new GroupLink { Parent = document, LineId = item.LineId, Reference = item.Reference };
                    try { link.Document = Load(ResolveGroup(item.Reference)); }
                    catch (Exception ex) { link.Error = ex.Message; }
                    links.Add(link);
                }
            }
            else if (document.Kind.Equals("dirList", StringComparison.OrdinalIgnoreCase) || document.Kind.Equals("catList", StringComparison.OrdinalIgnoreCase))
            {
                bool category = document.Kind.Equals("catList", StringComparison.OrdinalIgnoreCase);
                string folder = document.GetOption(category ? "storeGroupsIn" : "listGroupsIn", "");
                if (String.IsNullOrWhiteSpace(folder)) return links;
                try
                {
                    if (folder.StartsWith("/") || folder.StartsWith("\\") || folder.IndexOf(':') >= 0) throw new IOException("自动分组目录必须使用相对路径。");
                    string baseDir = category ? System.IO.Path.GetDirectoryName(document.Path) : RootDirectory;
                    string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, folder.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
                    EnsureInsideRoot(System.IO.Path.Combine(full, "check.pbg"));
                    if (!Directory.Exists(full)) throw new DirectoryNotFoundException("自动分组目录不存在：" + folder);
                    foreach (string file in Directory.GetFiles(full, "*.pbg").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(2000))
                    {
                        var link = new GroupLink { Parent = document, Reference = System.IO.Path.GetFileNameWithoutExtension(file), Generated = category };
                        try { EnsureInsideRoot(file); link.Document = Load(file); } catch (Exception ex) { link.Error = ex.Message; }
                        links.Add(link);
                    }
                }
                catch (Exception ex) { links.Add(new GroupLink { Parent = document, Reference = folder, Error = ex.Message }); }
            }
            return links;
        }

        public PbgDocument AddGroup(PbgDocument parent, string reference, string caption)
        {
            if (!parent.CanEditItems || !parent.Kind.Equals("basic", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("只能在 basic 静态分组中添加子分组。");
            string path = ResolveGroup(reference);
            if (File.Exists(path) || documents.ContainsKey(path)) throw new IOException("这个分组文件已经存在，请换一个文件名。");
            var doc = PbgDocument.Create(path, caption);
            documents.Add(path, doc);
            parent.InsertItem(reference.Replace('/', '\\'));
            parent.SetOption("sortOn", "none");
            return doc;
        }

        public PbgDocument Find(string path)
        {
            PbgDocument d;
            return documents.TryGetValue(path, out d) ? d : null;
        }

        public WorkspaceSnapshot Snapshot()
        {
            var snapshot = new WorkspaceSnapshot();
            foreach (var d in documents.Values) snapshot.Files[d.Path] = d.ToBytes();
            return snapshot;
        }

        public void Restore(WorkspaceSnapshot snapshot)
        {
            // Undo changes bytes, never the disk baseline used by conflict checks.
            foreach (string path in documents.Keys.ToArray())
                if (!snapshot.Files.ContainsKey(path) && documents[path].IsNew) documents.Remove(path);
            foreach (var pair in snapshot.Files)
            {
                PbgDocument d;
                if (!documents.TryGetValue(pair.Key, out d))
                {
                    d = PbgDocument.Create(pair.Key, "");
                    documents.Add(pair.Key, d);
                }
                d.ApplyBytes(pair.Value);
            }
        }

        public List<string> SaveAll()
        {
            var dirty = new List<PbgDocument>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in documents.Values.Where(d => d.IsDirty).OrderBy(d => d == Root ? 1 : 0).ThenBy(d => d.IsNew ? 0 : 1))
                AppendSaveOrder(document, seen, dirty);
            foreach (var d in dirty)
            {
                EnsureInsideRoot(d.Path);
                d.CheckExternalChange();
                if (File.Exists(d.Path) && (File.GetAttributes(d.Path) & FileAttributes.ReadOnly) != 0)
                    throw new IOException("文件为只读：" + d.Path);
            }
            var saved = new List<string>();
            foreach (var d in dirty)
            {
                try { d.Save(); saved.Add(d.Path); }
                catch (Exception ex)
                {
                    throw new IOException("保存未全部完成。已保存 " + saved.Count + " 个文件；失败文件：" + d.Path + "\r\n" + ex.Message + "\r\n未成功保存的修改仍保留在编辑器中。", ex);
                }
            }
            return saved;
        }

        void AppendSaveOrder(PbgDocument document, HashSet<string> seen, List<PbgDocument> ordered)
        {
            if (!document.IsDirty || !seen.Add(document.Path)) return;
            if (document.CanEditItems)
            {
                foreach (var item in document.Items.Where(i => i.Kind == PbgItemKind.GroupReference))
                {
                    PbgDocument child = null;
                    try { child = Find(ResolveGroup(item.Reference)); } catch (IOException) { }
                    if (child != null) AppendSaveOrder(child, seen, ordered);
                }
            }
            ordered.Add(document);
        }
    }
}
