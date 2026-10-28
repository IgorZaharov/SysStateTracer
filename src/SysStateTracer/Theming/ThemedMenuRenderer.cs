namespace SysStateTracer.Theming;

/// <summary>Context menus and drop-downs in the palette's colours.</summary>
internal sealed class ThemedMenuRenderer(Palette palette) : ToolStripProfessionalRenderer(new Colors(palette))
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? palette.Text : Palette.Blend(palette.SecondaryText, palette.Surface, 0.35);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? palette.SecondaryText : palette.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var bounds = e.ImageRectangle;
        using var font = UiFonts.Glyph(9F);
        TextRenderer.DrawText(e.Graphics, "\uE73E", font, bounds, palette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private sealed class Colors(Palette p) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => p.Surface;

        public override Color ImageMarginGradientBegin => p.Surface;

        public override Color ImageMarginGradientMiddle => p.Surface;

        public override Color ImageMarginGradientEnd => p.Surface;

        public override Color MenuBorder => p.Border;

        public override Color MenuItemBorder => p.Hover;

        public override Color MenuItemSelected => p.Hover;

        public override Color MenuItemSelectedGradientBegin => p.Hover;

        public override Color MenuItemSelectedGradientEnd => p.Hover;

        public override Color MenuItemPressedGradientBegin => p.Pressed;

        public override Color MenuItemPressedGradientMiddle => p.Pressed;

        public override Color MenuItemPressedGradientEnd => p.Pressed;

        public override Color SeparatorDark => p.Border;

        public override Color SeparatorLight => p.Surface;

        public override Color ToolStripBorder => p.Border;

        public override Color ToolStripGradientBegin => p.Surface;

        public override Color ToolStripGradientMiddle => p.Surface;

        public override Color ToolStripGradientEnd => p.Surface;

        public override Color MenuStripGradientBegin => p.Surface;

        public override Color MenuStripGradientEnd => p.Surface;

        public override Color CheckBackground => p.Selection;

        public override Color CheckSelectedBackground => p.Selection;

        public override Color CheckPressedBackground => p.Selection;

        public override Color ButtonSelectedHighlight => p.Hover;

        public override Color ButtonSelectedBorder => p.Hover;
    }
}
