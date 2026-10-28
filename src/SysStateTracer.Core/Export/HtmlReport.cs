using System.Globalization;
using System.Text;
using System.Text.Json;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Ignore;

namespace SysStateTracer.Core.Export;

/// <summary>
/// One self-contained HTML file with the comparison (plan Step 37, /compare-snap): a summary, then a
/// collapsible tree in the comparison window's colours. The tree is embedded as data and rendered only
/// when a node is opened, so a report with 100,000 changes opens quickly. No external files or scripts.
/// </summary>
public static class HtmlReport
{
    public static void Write(DiffResult diff, string path, ImportanceMarks? marks = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var text = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Write(diff, text, marks);
    }

    public static void Write(DiffResult diff, TextWriter output, ImportanceMarks? marks = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(output);
        var title = $"{diff.Left.Meta.Name} vs {diff.Right.Meta.Name}";

        output.Write("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>");
        output.Write(Html(title));
        output.Write("</title>\n<style>\n");
        output.Write(Style);
        output.Write("</style></head><body>\n<h1>");
        output.Write(Html(title));
        output.Write("</h1>\n<p class=\"sub\">");
        output.Write(Html(Describe(diff.Left)));
        output.Write(" → ");
        output.Write(Html(Describe(diff.Right)));
        output.Write("</p>\n<table class=\"summary\"><tr><th></th><th class=\"n\">New</th><th class=\"m\">Modified</th><th class=\"d\">Deleted</th></tr>\n");
        SummaryRow(output, "File system", diff.FileSystemCounts);
        SummaryRow(output, "Registry", diff.RegistryCounts);
        SummaryRow(output, "Services", diff.ServicesCounts);
        output.Write("</table>\n<div class=\"tree\" id=\"tree\"><div class=\"hdr\"><span>Name</span><span>");
        output.Write(Html(diff.Left.Meta.Name));
        output.Write("</span><span>");
        output.Write(Html(diff.Right.Meta.Name));
        output.Write("</span></div></div>\n<script type=\"application/json\" id=\"data\">");
        output.Flush();
        WriteData(diff, output, marks);
        output.Write("</script>\n<script>\n");
        output.Write(Script);
        output.Write("</script>\n<p class=\"foot\">Created by ");
        output.Write(Html(AppInfo.Name));
        output.Write(' ');
        output.Write(Html(AppInfo.Version));
        output.Write(" on ");
        output.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        output.Write(".</p>\n</body></html>\n");
    }

    /// <summary>
    /// At most this many rows are added for the contents of new or deleted folders and keys, so a report
    /// stays small enough to open; past it, such folders and keys show their item count only.
    /// </summary>
    public const int MaxContentRows = 250_000;

    /// <summary>
    /// Areas as [name, nodes]; a node is [name, kind, change, left, right, changedItems, descendants, marked],
    /// in depth-first order. A whole new or deleted folder or key is followed by what it holds, read from the
    /// snapshot that has it and without what the Global Ignore list leaves out (GitHub issue #3), so it can be
    /// opened to any depth, as in the comparison window.
    /// </summary>
    private static void WriteData(DiffResult diff, TextWriter output, ImportanceMarks? marks)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            var budget = MaxContentRows;
            json.WriteStartArray();
            foreach (var (area, label) in new[] { (DiffArea.FileSystem, "File system"), (DiffArea.Registry, "Registry"), (DiffArea.Services, "Services") })
            {
                var tree = diff.Tree(area);
                var rows = new List<Row>(tree.Count);
                foreach (var root in tree.Roots())
                {
                    AddNode(diff, tree, area, root, marks, rows, ref budget);
                }

                json.WriteStartArray();
                json.WriteStringValue(label);
                json.WriteStartArray();
                foreach (var row in rows)
                {
                    json.WriteStartArray();
                    json.WriteStringValue(row.Name);
                    json.WriteNumberValue((int)row.Kind);
                    json.WriteNumberValue((int)row.Change);
                    json.WriteStringValue(row.Left);
                    json.WriteStringValue(row.Right);
                    json.WriteNumberValue(row.ChangedItems);
                    json.WriteNumberValue(row.Descendants);
                    json.WriteNumberValue(row.Marked ? 1 : 0);
                    json.WriteEndArray();
                }

                json.WriteEndArray();
                json.WriteEndArray();
            }

            json.WriteEndArray();
        }

        output.Write(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    private sealed class Row(string name, DiffItemKind kind, ChangeKind change, string left, string right, bool marked)
    {
        public string Name { get; } = name;

        public DiffItemKind Kind { get; } = kind;

        public ChangeKind Change { get; } = change;

        public string Left { get; } = left;

        public string Right { get; } = right;

        public bool Marked { get; } = marked;

        public int ChangedItems { get; set; }

        public int Descendants { get; set; }
    }

    private static void AddNode(DiffResult diff, DiffTree tree, DiffArea area, int node, ImportanceMarks? marks, List<Row> rows, ref int budget)
    {
        var item = new DiffItem(area, node);
        var row = new Row(
            NodeName(diff, tree, node),
            tree.Kind(node),
            tree.Change(node),
            DiffItems.Column(diff, item, right: false),
            DiffItems.Column(diff, item, right: true),
            marks?.IsMarked(item) == true) { ChangedItems = tree.ChangedItems(node) };
        var at = rows.Count;
        rows.Add(row);
        if (tree.Descendants(node) > 0)
        {
            foreach (var child in tree.Children(node))
            {
                AddNode(diff, tree, area, child, marks, rows, ref budget);
            }
        }
        else if (tree.Change(node) is ChangeKind.New or ChangeKind.Deleted && tree.Kind(node) is DiffItemKind.Folder or DiffItemKind.Key && budget > 0)
        {
            var onRight = tree.Right(node) >= 0;
            var snapshot = onRight ? diff.Right : diff.Left;
            var index = onRight ? tree.Right(node) : tree.Left(node);
            var path = diff.FullPath(tree, node);
            if (area == DiffArea.FileSystem)
            {
                AddFolderContents(diff, snapshot, index, path, onRight, row.Change, row.Marked, rows, ref budget);
            }
            else
            {
                AddKeyContents(diff, snapshot, index, path, onRight, row.Change, row.Marked, rows, ref budget);
            }
        }

        row.Descendants = rows.Count - at - 1;
    }

    /// <summary>Folders first, then files, as Explorer shows them.</summary>
    private static void AddFolderContents(
        DiffResult diff, Model.Snapshot snapshot, int folder, string path, bool onRight, ChangeKind change, bool marked, List<Row> rows, ref int budget)
    {
        var fs = snapshot.FileSystem;
        var ignore = diff.Ignore.RuleCount > 0 ? diff.Ignore : null;
        foreach (var directories in new[] { true, false })
        {
            for (var c = folder + 1; c <= folder + fs.Descendants[folder]; c += fs.Descendants[c] + 1)
            {
                if (fs.IsDirectory(c) != directories || budget <= 0)
                {
                    continue;
                }

                var name = snapshot.Names.GetString(fs.Name[c]);
                var childPath = path + "\\" + name;
                if (ignore?.IsIgnoredEntry(IgnoreTarget.FileSystem, childPath) == true)
                {
                    continue;
                }

                var text = directories
                    ? string.Create(CultureInfo.CurrentCulture, $"{ItemFormat.Time(fs.LastWriteTime[c])} · {DiffItems.Count(fs.ChildFolders[c], "folder")}, {DiffItems.Count(fs.ChildFiles[c], "file")}")
                    : $"{ItemFormat.Time(fs.LastWriteTime[c])} · {ItemFormat.Size(fs.Size[c])}";
                var row = new Row(name, directories ? DiffItemKind.Folder : DiffItemKind.File, change, onRight ? string.Empty : text, onRight ? text : string.Empty, marked);
                var at = rows.Count;
                rows.Add(row);
                budget--;
                if (directories)
                {
                    AddFolderContents(diff, snapshot, c, childPath, onRight, change, marked, rows, ref budget);
                }

                row.Descendants = rows.Count - at - 1;
                row.ChangedItems = 1 + row.Descendants;
            }
        }
    }

    /// <summary>Subkeys first, then values, as Regedit shows them.</summary>
    private static void AddKeyContents(
        DiffResult diff, Model.Snapshot snapshot, int key, string path, bool onRight, ChangeKind change, bool marked, List<Row> rows, ref int budget)
    {
        var reg = snapshot.Registry;
        var ignore = diff.Ignore.RuleCount > 0 ? diff.Ignore : null;
        for (var c = key + 1; c <= key + reg.Descendants[key] && budget > 0; c += reg.Descendants[c] + 1)
        {
            var name = snapshot.Names.GetString(reg.Name[c]);
            var childPath = path + "\\" + name;
            if (ignore?.IsIgnoredEntry(IgnoreTarget.Registry, childPath) == true)
            {
                continue;
            }

            var keys = 0;
            for (var k = c + 1; k <= c + reg.Descendants[c]; k += reg.Descendants[k] + 1)
            {
                keys++;
            }

            var text = string.Create(CultureInfo.CurrentCulture, $"{ItemFormat.Time(reg.LastWriteTime[c])} · {DiffItems.Count(keys, "key")}, {DiffItems.Count(reg.KeyValueCount[c], "value")}");
            var row = new Row(name, DiffItemKind.Key, change, onRight ? string.Empty : text, onRight ? text : string.Empty, marked);
            var at = rows.Count;
            rows.Add(row);
            budget--;
            AddKeyContents(diff, snapshot, c, childPath, onRight, change, marked, rows, ref budget);
            row.Descendants = rows.Count - at - 1;
            row.ChangedItems = 1 + row.Descendants;
        }

        for (var v = reg.FirstValue[key]; v < reg.FirstValue[key] + reg.KeyValueCount[key] && budget > 0; v++)
        {
            var name = snapshot.Names.GetString(reg.ValueName[v]);
            if (ignore?.IsIgnoredEntry(IgnoreTarget.Registry, path + "\\" + name) == true)
            {
                continue;
            }

            var text = $"{ItemFormat.ValueType(reg.ValueDataType[v])} · {ItemFormat.ValueData(reg, v)}";
            rows.Add(new Row(name.Length == 0 ? "(Default)" : name, DiffItemKind.Value, change, onRight ? string.Empty : text, onRight ? text : string.Empty, marked) { ChangedItems = 1 });
            budget--;
        }
    }

    private static string NodeName(DiffResult diff, DiffTree tree, int node)
    {
        var name = diff.Name(tree, node);
        return tree.Kind(node) switch
        {
            DiffItemKind.Value when name.Length == 0 => "(Default)",
            DiffItemKind.Folder when tree.Parent(node) < 0 => name + "\\",
            _ => name,
        };
    }

    private static void SummaryRow(TextWriter output, string label, DiffCounts counts) =>
        output.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"<tr><td>{label}</td><td class=\"n\">{counts.New:N0}</td><td class=\"m\">{counts.Modified:N0}</td><td class=\"d\">{counts.Deleted:N0}</td></tr>\n"));

    private static string Describe(Model.Snapshot snapshot) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.Meta.Name} ({snapshot.Meta.ComputerName}, {snapshot.Meta.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm})");

    private static string Html(string text) => System.Net.WebUtility.HtmlEncode(text);

    private const string Style = """
        :root { --bg:#ffffff; --fg:#1f2328; --muted:#656d76; --line:#d0d7de; --hover:#f3f4f6;
                --new:#1a7f37; --new-bg:#dafbe1; --mod:#9a6700; --mod-bg:#fff8c5; --del:#cf222e; --del-bg:#ffebe9; }
        @media (prefers-color-scheme: dark) {
          :root { --bg:#0d1117; --fg:#e6edf3; --muted:#8d96a0; --line:#30363d; --hover:#161b22;
                  --new:#3fb950; --new-bg:#12261e; --mod:#d29922; --mod-bg:#272115; --del:#f85149; --del-bg:#2d1517; }
        }
        body { margin:0; padding:16px 24px; background:var(--bg); color:var(--fg); font:14px/1.45 "Segoe UI", system-ui, sans-serif; }
        h1 { font-size:20px; margin:0 0 4px; }
        .sub, .foot { color:var(--muted); margin:0 0 16px; }
        .summary { border-collapse:collapse; margin-bottom:20px; }
        .summary th, .summary td { padding:4px 14px; border-bottom:1px solid var(--line); text-align:right; }
        .summary td:first-child { text-align:left; }
        th.n, td.n { color:var(--new); } th.m, td.m { color:var(--mod); } th.d, td.d { color:var(--del); }
        .tree { border:1px solid var(--line); border-radius:6px; overflow:auto; }
        .hdr, .row { display:grid; grid-template-columns:minmax(260px,2fr) minmax(200px,1.5fr) minmax(200px,1.5fr); gap:12px; padding:3px 10px; }
        .hdr { font-weight:600; border-bottom:1px solid var(--line); position:sticky; top:0; background:var(--bg); }
        .row { cursor:default; white-space:nowrap; }
        .row:hover { background:var(--hover); }
        .row span { overflow:hidden; text-overflow:ellipsis; }
        .row .nm::before { content:"\25B8"; display:inline-block; width:14px; color:var(--muted); }
        .row.open .nm::before { content:"\25BE"; }
        .row.leaf .nm::before { content:""; }
        .row .cnt { color:var(--muted); }
        .c1 { background:var(--new-bg); } .c1 .nm { color:var(--new); }
        .c2 { background:var(--del-bg); } .c2 .nm { color:var(--del); }
        .c3 { background:var(--mod-bg); } .c3 .nm { color:var(--mod); }
        .mk { opacity:.55; text-decoration:line-through; }
        .area { font-weight:600; }
        """;

    // Rows are created only when their parent is opened.
    private const string Script = """
        (function () {
          var areas = JSON.parse(document.getElementById('data').textContent);
          var tree = document.getElementById('tree');
          var glyph = ['', '+ ', '− ', '~ '];
          function row(cls, depth, name, left, right, count) {
            var r = document.createElement('div');
            r.className = 'row ' + cls;
            var nm = document.createElement('span');
            nm.className = 'nm';
            nm.style.paddingLeft = (depth * 18) + 'px';
            nm.textContent = name;
            if (count) { var c = document.createElement('span'); c.className = 'cnt'; c.textContent = ' (' + count.toLocaleString() + ')'; nm.appendChild(c); }
            var a = document.createElement('span'); a.textContent = left;
            var b = document.createElement('span'); b.textContent = right;
            r.appendChild(nm); r.appendChild(a); r.appendChild(b);
            return r;
          }
          function children(nodes, i) {
            var list = [], end = i + nodes[i][6];
            for (var c = i + 1; c <= end; c += nodes[c][6] + 1) list.push(c);
            return list;
          }
          function addNodes(nodes, list, depth, after) {
            var anchor = after;
            list.forEach(function (i) {
              var n = nodes[i];
              var r = row('c' + n[2] + (n[7] ? ' mk' : '') + (n[6] ? '' : ' leaf'), depth, glyph[n[2]] + n[0], n[3], n[4], (n[6] || ((n[2] === 1 || n[2] === 2) && n[5] > 1)) ? n[5] : 0);
              r.dataset.depth = depth;
              if (n[6]) r.onclick = function () { toggle(nodes, i, r); };
              anchor.after(r);
              anchor = r;
            });
            return anchor;
          }
          function toggle(nodes, i, r) {
            var depth = +r.dataset.depth;
            if (r.classList.contains('open')) {
              while (r.nextSibling && r.nextSibling.dataset && +r.nextSibling.dataset.depth > depth) r.nextSibling.remove();
              r.classList.remove('open');
            } else {
              addNodes(nodes, children(nodes, i), depth + 1, r);
              r.classList.add('open');
            }
          }
          var last = tree.firstChild;
          areas.forEach(function (area) {
            var nodes = area[1];
            var roots = [];
            for (var i = 0; i < nodes.length; i += nodes[i][6] + 1) roots.push(i);
            var total = roots.reduce(function (s, i) { return s + nodes[i][5]; }, 0);
            var head = row('area' + (roots.length ? '' : ' leaf'), 0, area[0], '', '', total);
            head.dataset.depth = 0;
            last.after(head);
            last = head;
            if (roots.length) {
              head.onclick = function () {
                if (head.classList.contains('open')) {
                  while (head.nextSibling && head.nextSibling.dataset && +head.nextSibling.dataset.depth > 0) head.nextSibling.remove();
                  head.classList.remove('open');
                } else { addNodes(nodes, roots, 1, head); head.classList.add('open'); }
              };
            }
          });
        })();
        """;
}
