using System.Globalization;
using System.Text;
using System.Text.Json;
using SysStateTracer.Core.Comparison;

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

    /// <summary>Areas as [name, nodes]; a node is [name, kind, change, left, right, changedItems, descendants, marked].</summary>
    private static void WriteData(DiffResult diff, TextWriter output, ImportanceMarks? marks)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (var (area, label) in new[] { (DiffArea.FileSystem, "File system"), (DiffArea.Registry, "Registry"), (DiffArea.Services, "Services") })
            {
                var tree = diff.Tree(area);
                json.WriteStartArray();
                json.WriteStringValue(label);
                json.WriteStartArray();
                for (var n = 0; n < tree.Count; n++)
                {
                    var item = new DiffItem(area, n);
                    json.WriteStartArray();
                    json.WriteStringValue(NodeName(diff, tree, n));
                    json.WriteNumberValue((int)tree.Kind(n));
                    json.WriteNumberValue((int)tree.Change(n));
                    json.WriteStringValue(DiffItems.Column(diff, item, right: false));
                    json.WriteStringValue(DiffItems.Column(diff, item, right: true));
                    json.WriteNumberValue(tree.ChangedItems(n));
                    json.WriteNumberValue(tree.Descendants(n));
                    json.WriteNumberValue(marks?.IsMarked(item) == true ? 1 : 0);
                    json.WriteEndArray();
                }

                json.WriteEndArray();
                json.WriteEndArray();
            }

            json.WriteEndArray();
        }

        output.Write(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
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
