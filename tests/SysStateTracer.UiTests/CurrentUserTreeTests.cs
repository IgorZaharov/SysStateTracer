using SysStateTracer.Controls;
using SysStateTracer.Core.Comparison;
using SysStateTracer.Core.Model;
using SysStateTracer.Theming;

namespace SysStateTracer.UiTests;

/// <summary>HKCU beside HKLM and HKU in the comparison tree, and long values on two lines in the details.</summary>
public class CurrentUserTreeTests
{
    private const string Me = "S-1-5-21-1000";
    private const string Other = "S-1-5-21-2000";

    [Fact]
    public void Tree_OnlyChanges_HkcuAtTheTopLevel_AndNotBelowHku()
    {
        var model = Model(Compare(otherUserChanges: true), showAll: false);

        var hives = Hives(model);
        Assert.Equal(["HKCU", "HKLM", "HKU"], hives.Select(h => model.Data.DisplayName(h.Item)));
        var hkcu = hives[0];
        var hku = hives[2];
        Assert.Equal(@"HKU\" + Me, model.Data.Path(hkcu.Item)); // actions still use the real key
        Assert.Equal("HKCU", model.Data.DisplayPath(hkcu.Item));

        Assert.Equal([Other], model.Children(hku.Id).Select(id => model.Data.DisplayName(model.ItemOf(id))));
        Assert.Equal("  (1)", model.Data.CountText(hku.Item)); // the other user's change only

        var software = model.ItemOf(Assert.Single(model.Children(hkcu.Id)));
        Assert.Equal(@"HKCU\Software", model.Data.DisplayPath(software));
    }

    [Fact]
    public void Tree_OnlyChanges_HkuLeftOut_WhenOnlyHkcuChanged()
    {
        var model = Model(Compare(otherUserChanges: false), showAll: false);

        Assert.Equal(["HKCU", "HKLM"], Hives(model).Select(h => model.Data.DisplayName(h.Item)));
    }

    [Fact]
    public void Tree_ShowAll_HkuStays_WithoutHkcu()
    {
        var model = Model(Compare(otherUserChanges: false), showAll: true);

        var hives = Hives(model);
        Assert.Equal(["HKCU", "HKLM", "HKU"], hives.Select(h => model.Data.DisplayName(h.Item)));
        Assert.True(model.HasChildren(hives[2].Id));
        Assert.Equal([Other], model.Children(hives[2].Id).Select(id => model.Data.DisplayName(model.ItemOf(id))));
    }

    [Fact]
    public void Tree_AnotherUser_HkuUnchanged()
    {
        var data = new ComparisonData(Compare(otherUserChanges: true), new ImportanceMarks(), "S-1-5-21-3000", showAll: false);
        var model = new ComparisonTreeModel(data);

        Assert.Equal(["HKLM", "HKU"], Hives(model).Select(h => model.Data.DisplayName(h.Item)));
    }

    [Fact]
    public void Details_LongValue_WrapsBetweenFolders()
    {
        var path = @"C:\Program Files\Eclipse Adoptium\jdk-17.0.20.101-hotspot\bin;%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\Wbem;C:\Program Files\Microsoft Office\root";

        var lines = DetailsPanel.Wrap(path, UiFonts.Normal, 300);

        Assert.True(lines.Count > DetailsPanel.MaxLines);
        Assert.Equal(path, string.Concat(lines));
        foreach (var line in lines)
        {
            Assert.True(TextRenderer.MeasureText(line, UiFonts.Normal, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width <= 300);
        }

        foreach (var line in lines.Take(lines.Count - 1))
        {
            Assert.True(line[^1] is ';' or '\\' or ' ', line); // broken between folders or words
        }

        Assert.Equal(2, DetailsPanel.Wrap(path, UiFonts.Normal, 700).Count);
        Assert.Equal(["short"], DetailsPanel.Wrap("short", UiFonts.Normal, 400));
    }

    [Fact]
    public void Details_ValueOverThreeLines_Scrolls()
    {
        var longPath = string.Join(';', Enumerable.Range(0, 40).Select(i => $@"C:\Program Files\Vendor {i:D2}\bin"));
        using var panel = new DetailsPanel { Size = new Size(900, 184) };
        panel.ShowItem("Path", "Modified", null, @"System\Path", ("OLDER", "before"), ("NEWER", "after"), [
            new ComparisonData.DetailRow("Variables", "System", "System", false),
            new ComparisonData.DetailRow("Value", "short", longPath, true),
        ]);
        using (var bitmap = new Bitmap(panel.Width, panel.Height))
        {
            panel.DrawToBitmap(bitmap, panel.ClientRectangle); // lays the values out
        }

        var cell = Assert.Single(panel.Cells, c => c.Row == 1 && c.Right);
        Assert.True(cell.Lines > DetailsPanel.MaxLines);
        Assert.False(cell.Track.IsEmpty);
        Assert.True(Assert.Single(panel.Cells, c => c.Row == 1 && !c.Right).Track.IsEmpty); // "short" fits

        Assert.True(panel.ScrollBy(1, right: true, 2));
        Assert.Equal(2, panel.ScrollOf(1, right: true));
        Assert.True(panel.ScrollBy(1, right: true, 1_000));
        Assert.Equal(cell.Lines - DetailsPanel.MaxLines, panel.ScrollOf(1, right: true)); // stops at the last three lines
        Assert.False(panel.ScrollBy(1, right: false, 1));
    }

    private static ComparisonTreeModel Model(DiffResult diff, bool showAll) =>
        new(new ComparisonData(diff, new ImportanceMarks(), Me, showAll));

    private static List<(long Id, ViewItem Item)> Hives(ComparisonTreeModel model) =>
        [.. model.Children(ComparisonTreeModel.CategoryId(DiffArea.Registry)).Select(id => (id, model.ItemOf(id)))];

    private static DiffResult Compare(bool otherUserChanges) =>
        SnapshotComparer.Compare(Build(installed: false, otherUserChanges), Build(installed: true, otherUserChanges));

    private static Snapshot Build(bool installed, bool otherUserChanges)
    {
        var names = new NameTable();
        var reg = new RegTreeBuilder(names);
        var hklm = reg.AddRoot("HKLM", 1);
        var vendor = reg.AddKey(reg.AddKey(hklm, "SOFTWARE", 1), "Contoso", 1);
        reg.AddValue(vendor, "Version", 4, BitConverter.GetBytes(installed ? 2 : 1));
        var hku = reg.AddRoot("HKU", 1);
        var me = reg.AddKey(reg.AddKey(hku, Me, 1), "Software", 1);
        reg.AddValue(me, "Theme", 4, BitConverter.GetBytes(installed ? 1 : 0));
        var other = reg.AddKey(reg.AddKey(hku, Other, 1), "Software", 1);
        reg.AddValue(other, "Theme", 4, BitConverter.GetBytes(installed && otherUserChanges ? 1 : 0));
        var snapshot = new Snapshot(new SnapshotMeta { Name = installed ? "after" : "before" }, names, registry: reg.Build());
        names.Freeze();
        return snapshot;
    }
}
