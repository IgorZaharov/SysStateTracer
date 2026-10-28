using System.Security.Principal;
using Microsoft.Win32;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Config;
using SysStateTracer.Core.Ignore;
using SysStateTracer.Core.Scanning.Fs;
using SysStateTracer.Theming;

namespace SysStateTracer.Controls;

/// <summary>What a node of the New Snapshot scope tree stands for.</summary>
internal enum ScopeKind
{
    Category,
    Volume,
    Folder,
    Hive,
    Key,
    Services,
    Drivers,
}

/// <summary>Data of one scope tree node.</summary>
internal sealed class ScopeItem(ScopeKind kind, string path)
{
    public ScopeKind Kind { get; } = kind;

    /// <summary>"C:\", "C:\Users", "HKLM", "HKLM\SOFTWARE", "HKU\S-1-5-21-…"; empty for categories and services.</summary>
    public string Path { get; } = path;

    public bool Ignored { get; set; }

    public bool ChildrenLoaded { get; set; }

    /// <summary>Check state: <see cref="ScopeTree.Unchecked"/>, <see cref="ScopeTree.Checked"/> or <see cref="ScopeTree.Mixed"/>.</summary>
    public int State { get; set; }

    public IgnoreTarget? Target => Kind switch
    {
        ScopeKind.Volume or ScopeKind.Folder => IgnoreTarget.FileSystem,
        ScopeKind.Hive or ScopeKind.Key => IgnoreTarget.Registry,
        _ => null,
    };

    /// <summary>Rule line for leaving this item out: "FS:C:\Temp", "REG:HKLM\SOFTWARE\X".</summary>
    public string? RuleLine => Target is { } target ? IgnoreRuleParser.Format(target, Path) : null;
}

/// <summary>
/// The scope tree of W3 (plan §10.3): drives, registry hives and services with tri-state checkboxes;
/// sub-folders and sub-keys are read when a node is expanded. Items in the Global Ignore list start
/// unchecked and are labelled "(ignored)". Unchecking something inside an included drive or hive
/// records it in <see cref="SnapshotScope.Excluded"/>.
/// </summary>
internal sealed class ScopeTree : TreeView, IThemed
{
    public const int Unchecked = 0;
    public const int Checked = 1;
    public const int Mixed = 2;

    private const string Placeholder = "…";
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImageList _stateImages = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private Palette _palette = ThemeManager.Current;
    private IgnoreMatcher _ignore = IgnoreMatcher.Empty;
    private TreeNode? _fileSystem;
    private TreeNode? _registry;
    private TreeNode? _services;

    public ScopeTree()
    {
        CheckBoxes = false;
        ImageList = _stateImages;
        ShowLines = false;
        HideSelection = false;
        FullRowSelect = true;
        ItemHeight = 24;
        BorderStyle = BorderStyle.None;
        Font = UiFonts.Normal;
        DrawStateImages();
    }

    /// <summary>Raised when an ignored item is clicked: it cannot be included while it is in the Global Ignore list.</summary>
    public event EventHandler<ScopeItem>? IgnoredItemClicked;

    /// <summary>Raised whenever a check state changes.</summary>
    public event EventHandler? ScopeChanged;

    public void ApplyTheme(Palette palette)
    {
        _palette = palette;
        BackColor = palette.Background;
        ForeColor = palette.Text;
        if (IsHandleCreated)
        {
            NativeUi.SetExplorerTheme(Handle, palette.IsDark);
        }

        DrawStateImages();
        RecolorNodes(Nodes);
    }

    /// <summary>Fills the tree from the saved scope.</summary>
    public void Load(SnapshotScope scope, IReadOnlyList<VolumeInfo> volumes)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(volumes);
        _excluded.Clear();
        _excluded.UnionWith(scope.Excluded);

        BeginUpdate();
        Nodes.Clear();
        _fileSystem = AddNode(Nodes, new ScopeItem(ScopeKind.Category, string.Empty), "File system", Unchecked);
        foreach (var volume in volumes)
        {
            var included = scope.FileSystemRoots is null ||
                scope.FileSystemRoots.Any(r => string.Equals(r.TrimEnd('\\'), volume.RootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            var size = volume.TotalBytes > 0 ? $" · {ItemFormat.Size(volume.TotalBytes)}" : string.Empty;
            var label = string.IsNullOrEmpty(volume.Label) ? "Local Disk" : volume.Label;
            AddNode(_fileSystem.Nodes, new ScopeItem(ScopeKind.Volume, volume.RootPath), $"{volume.RootPath}  {label} · {volume.FileSystem}{size}", StartState(included, volume.RootPath, IgnoreTarget.FileSystem), lazy: true);
        }

        // Folders saved as roots of their own (e.g. by editing the settings for /new-snap) stay as such.
        foreach (var folder in scope.FileSystemRoots ?? [])
        {
            if (!volumes.Any(v => string.Equals(v.RootPath.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                AddNode(_fileSystem.Nodes, new ScopeItem(ScopeKind.Folder, folder), $"{folder}  (folder)", StartState(true, folder, IgnoreTarget.FileSystem), lazy: true);
            }
        }

        _registry = AddNode(Nodes, new ScopeItem(ScopeKind.Category, string.Empty), "Registry", Unchecked);
        foreach (var (hive, label) in new[] { ("HKLM", "HKEY_LOCAL_MACHINE"), ("HKU", "HKEY_USERS (each user; yours is HKEY_CURRENT_USER)") })
        {
            var included = scope.RegistryRoots.Any(r => string.Equals(r, hive, StringComparison.OrdinalIgnoreCase));
            AddNode(_registry.Nodes, new ScopeItem(ScopeKind.Hive, hive), label, StartState(included, hive, IgnoreTarget.Registry), lazy: true);
        }

        _services = AddNode(Nodes, new ScopeItem(ScopeKind.Category, string.Empty), "Services", Unchecked);
        AddNode(_services.Nodes, new ScopeItem(ScopeKind.Services, string.Empty), "Windows services (as in services.msc)", scope.IncludeServices ? Checked : Unchecked);
        AddNode(_services.Nodes, new ScopeItem(ScopeKind.Drivers, string.Empty), "Kernel and file-system drivers", scope.IncludeDrivers ? Checked : Unchecked);

        foreach (TreeNode category in Nodes)
        {
            UpdateFromChildren(category);
            category.Expand();
        }

        EndUpdate();
    }

    /// <summary>Applies the Global Ignore list: matching items become unchecked and "(ignored)".</summary>
    public void SetIgnore(IgnoreMatcher ignore)
    {
        _ignore = ignore ?? IgnoreMatcher.Empty;
        BeginUpdate();
        foreach (var node in AllNodes(Nodes).Where(n => !IsPlaceholder(n)).ToList())
        {
            var item = (ScopeItem)node.Tag!;
            var ignored = item.Target is { } target && _ignore.IsIgnored(target, item.Path);
            if (ignored != item.Ignored)
            {
                item.Ignored = ignored;
                if (ignored)
                {
                    SetState(node, Unchecked, cascade: true);
                }

                Label(node);
            }
        }

        foreach (TreeNode category in Nodes)
        {
            UpdateTreeStates(category);
        }

        EndUpdate();
    }

    /// <summary>The scope the tree shows now, for saving and for the snapshot.</summary>
    public SnapshotScope ToScope()
    {
        var scope = new SnapshotScope { RegistryRoots = [], Excluded = [] };
        if (_fileSystem is not null)
        {
            var roots = _fileSystem.Nodes.Cast<TreeNode>().ToList();
            var volumes = roots.Where(r => ((ScopeItem)r.Tag!).Kind == ScopeKind.Volume).ToList();
            var included = roots.Where(v => StateOf(v) != Unchecked).ToList();
            scope.FileSystemRoots = included.Count == roots.Count && volumes.Count == roots.Count && roots.Count > 0
                ? null
                : [.. included.Select(v => ((ScopeItem)v.Tag!).Path)];
            foreach (var volume in included)
            {
                CollectExclusions(volume, scope.Excluded);
            }
        }

        if (_registry is not null)
        {
            foreach (TreeNode hive in _registry.Nodes)
            {
                if (StateOf(hive) != Unchecked)
                {
                    scope.RegistryRoots.Add(((ScopeItem)hive.Tag!).Path);
                    CollectExclusions(hive, scope.Excluded);
                }
            }
        }

        if (_services is not null)
        {
            scope.IncludeServices = StateOf(_services.Nodes[0]) == Checked;
            scope.IncludeDrivers = StateOf(_services.Nodes[1]) == Checked;
        }

        return scope;
    }

    /// <summary>Checked, Unchecked or Mixed.</summary>
    public static int StateOf(TreeNode node) => node.Tag is ScopeItem item ? item.State : Unchecked;

    /// <summary>True when nothing at all is checked.</summary>
    public bool IsEmpty => Nodes.Cast<TreeNode>().All(n => StateOf(n) == Unchecked);

    /// <summary>Toggles a node as a click on its checkbox does (tests and keyboard).</summary>
    public void Toggle(TreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var item = (ScopeItem)node.Tag!;
        var target = StateOf(node) == Checked ? Unchecked : Checked;
        if (item.Ignored && target == Checked)
        {
            IgnoredItemClicked?.Invoke(this, item);
            return;
        }

        SetState(node, target, cascade: true);
        UpdateFromChildren(node);
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            UpdateFromChildren(parent);
        }

        ScopeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeUi.SetExplorerTheme(Handle, _palette.IsDark);
    }

    protected override void OnNodeMouseClick(TreeNodeMouseClickEventArgs e)
    {
        base.OnNodeMouseClick(e);
        if (Enabled && e.Node is { } node && e.Button == MouseButtons.Left && HitTest(e.Location).Location == TreeViewHitTestLocations.Image)
        {
            Toggle(node);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && SelectedNode is { } node)
        {
            Toggle(node);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnBeforeExpand(TreeViewCancelEventArgs e)
    {
        base.OnBeforeExpand(e);
        if (e.Node is { Tag: ScopeItem { ChildrenLoaded: false } item } node)
        {
            LoadChildren(node, item);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stateImages.Dispose();
        }

        base.Dispose(disposing);
    }

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in AllNodes(node.Nodes))
            {
                yield return child;
            }
        }
    }

    private static bool IsPlaceholder(TreeNode node) => node.Tag is null;

    /// <summary>Sets the state and shows it as the node's checkbox picture.</summary>
    private static void Show(TreeNode node, int state)
    {
        ((ScopeItem)node.Tag!).State = state;
        node.ImageIndex = state;
        node.SelectedImageIndex = state;
    }

    private int StartState(bool included, string path, IgnoreTarget target)
    {
        if (!included || _ignore.IsIgnored(target, path))
        {
            return Unchecked;
        }

        var rule = IgnoreRuleParser.Format(target, path);
        if (_excluded.Contains(rule))
        {
            return Unchecked;
        }

        var prefix = rule.TrimEnd('\\') + "\\";
        return _excluded.Any(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ? Mixed : Checked;
    }

    private TreeNode AddNode(TreeNodeCollection parent, ScopeItem item, string text, int state, bool lazy = false)
    {
        var node = new TreeNode(text) { Tag = item };
        Show(node, state);
        node.Name = item.Path;
        item.ChildrenLoaded = !lazy;
        if (item.Target is { } target)
        {
            item.Ignored = _ignore.IsIgnored(target, item.Path);
        }

        parent.Add(node);
        node.ToolTipText = item.Path;
        Label(node);
        if (lazy)
        {
            node.Nodes.Add(new TreeNode(Placeholder));
        }

        return node;
    }

    private void Label(TreeNode node)
    {
        var item = (ScopeItem)node.Tag!;
        const string Suffix = "  (ignored)";
        var text = node.Text.EndsWith(Suffix, StringComparison.Ordinal) ? node.Text[..^Suffix.Length] : node.Text;
        node.Text = item.Ignored ? text + Suffix : text;
        node.ForeColor = item.Ignored ? _palette.SecondaryText : _palette.Text;
        node.NodeFont = item.Kind == ScopeKind.Category ? UiFonts.Bold : null;
    }

    private void RecolorNodes(TreeNodeCollection nodes)
    {
        foreach (var node in AllNodes(nodes))
        {
            if (!IsPlaceholder(node))
            {
                node.ForeColor = ((ScopeItem)node.Tag!).Ignored ? _palette.SecondaryText : _palette.Text;
            }
        }
    }

    private void LoadChildren(TreeNode node, ScopeItem item)
    {
        item.ChildrenLoaded = true;
        BeginUpdate();
        node.Nodes.Clear();
        var parentState = StateOf(node);
        foreach (var (childPath, label, kind) in ReadChildren(item))
        {
            var target = kind is ScopeKind.Folder ? IgnoreTarget.FileSystem : IgnoreTarget.Registry;
            var state = parentState == Unchecked ? Unchecked : StartState(true, childPath, target);
            AddNode(node.Nodes, new ScopeItem(kind, childPath), label, state, lazy: true);
        }

        UpdateFromChildren(node);
        EndUpdate();
    }

    private static IEnumerable<(string Path, string Label, ScopeKind Kind)> ReadChildren(ScopeItem item)
    {
        switch (item.Kind)
        {
            case ScopeKind.Volume or ScopeKind.Folder:
            {
                string[] folders;
                try
                {
                    folders = [.. Directory.EnumerateDirectories(item.Path, "*", new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        RecurseSubdirectories = false,
                    })];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    folders = [];
                }

                Array.Sort(folders, StringComparer.OrdinalIgnoreCase);
                return folders.Select(f => (f, System.IO.Path.GetFileName(f), ScopeKind.Folder));
            }

            case ScopeKind.Hive or ScopeKind.Key:
            {
                var names = RegistryChildren(item.Path);
                var currentSid = WindowsIdentity.GetCurrent().User?.Value;
                return names.Select(n =>
                {
                    var path = item.Path + "\\" + n;
                    var label = item.Kind == ScopeKind.Hive && item.Path == "HKU" ? UserLabel(n, currentSid) : n;
                    return (path, label, ScopeKind.Key);
                });
            }

            default:
                return [];
        }
    }

    private static List<string> RegistryChildren(string path)
    {
        var slash = path.IndexOf('\\', StringComparison.Ordinal);
        var hive = slash < 0 ? path : path[..slash];
        var relative = slash < 0 ? null : path[(slash + 1)..];
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == "HKU" ? RegistryHive.Users : RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = relative is null ? root : root.OpenSubKey(relative);
            if (key is null)
            {
                return [];
            }

            var names = key.GetSubKeyNames().ToList();
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static string UserLabel(string sid, string? currentSid)
    {
        if (string.Equals(sid, currentSid, StringComparison.OrdinalIgnoreCase))
        {
            return $"{sid}  (you: HKEY_CURRENT_USER)";
        }

        if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
        {
            return sid;
        }

        try
        {
            var account = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            return $"{sid}  ({account})";
        }
        catch (SystemException)
        {
            return sid; // not a SID, or no account name for it
        }
    }

    private void SetState(TreeNode node, int state, bool cascade)
    {
        var item = (ScopeItem)node.Tag!;
        Show(node, item.Ignored ? Unchecked : state);
        if (!cascade)
        {
            return;
        }

        foreach (TreeNode child in node.Nodes)
        {
            if (!IsPlaceholder(child))
            {
                SetState(child, state, cascade: true);
            }
        }
    }

    /// <summary>Recomputes a node from its loaded children: all checked, all unchecked, or mixed.</summary>
    private void UpdateFromChildren(TreeNode node)
    {
        var children = node.Nodes.Cast<TreeNode>().Where(c => !IsPlaceholder(c)).ToList();
        if (children.Count == 0)
        {
            return;
        }

        // Ignored children are left out anyway, so they do not make their parent mixed.
        var anyIncluded = children.Any(c => StateOf(c) != Unchecked);
        var allChecked = children.All(c => StateOf(c) == Checked || ((ScopeItem)c.Tag!).Ignored);
        Show(node, !anyIncluded ? Unchecked : allChecked ? Checked : Mixed);
    }

    private void UpdateTreeStates(TreeNode node)
    {
        foreach (TreeNode child in node.Nodes)
        {
            if (!IsPlaceholder(child))
            {
                UpdateTreeStates(child);
            }
        }

        UpdateFromChildren(node);
    }

    /// <summary>Unchecked items inside an included drive, hive, folder or key (not those already in the Global Ignore list).</summary>
    private void CollectExclusions(TreeNode node, List<string> excluded)
    {
        var item = (ScopeItem)node.Tag!;
        if (!item.ChildrenLoaded)
        {
            // Not expanded in this session: keep what was saved for anything inside it.
            if (StateOf(node) == Mixed && item.RuleLine is { } rule)
            {
                var prefix = rule.TrimEnd('\\') + "\\";
                excluded.AddRange(_excluded.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            }

            return;
        }

        foreach (TreeNode child in node.Nodes)
        {
            var childItem = (ScopeItem)child.Tag!;
            if (StateOf(child) == Unchecked)
            {
                if (!childItem.Ignored && childItem.RuleLine is { } line)
                {
                    excluded.Add(line);
                }
            }
            else if (StateOf(child) == Mixed)
            {
                CollectExclusions(child, excluded);
            }
        }
    }

    private void DrawStateImages()
    {
        var p = _palette;
        _stateImages.Images.Clear();
        for (var state = 0; state < 3; state++)
        {
            var bitmap = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var box = new Rectangle(1, 1, 13, 13);
                using var path = ThemedButton.Rounded(box, 3);
                if (state == Unchecked)
                {
                    using var fill = new SolidBrush(p.Input);
                    using var pen = new Pen(p.SecondaryText);
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
                else
                {
                    using var fill = new SolidBrush(p.Accent);
                    g.FillPath(fill, path);
                    using var mark = new Pen(p.AccentText, 1.8f);
                    if (state == Checked)
                    {
                        g.DrawLines(mark, [new PointF(4, 8), new PointF(6.5f, 10.5f), new PointF(11.5f, 5)]);
                    }
                    else
                    {
                        g.DrawLine(mark, 4.5f, 8, 10.5f, 8);
                    }
                }
            }

            _stateImages.Images.Add(bitmap);
        }
    }
}
