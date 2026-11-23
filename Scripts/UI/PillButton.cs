using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>모서리가 둥근 납작한 버튼. Primary는 강조색으로 채우고, Selected는 정렬처럼 여럿 중 고른 것을 표시한다.</summary>
internal sealed class PillButton : Control
{
    private LibraryPalette? palette;
    private bool hover, down;
    private bool primary, selected;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Primary { get => primary; set { primary = value; Invalidate(); } }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Selected { get => selected; set { selected = value; Invalidate(); } }
    /// <summary>둥근 모서리 바깥을 칠할 색. 비우면 부모 바탕색 — 검색칸처럼 부모와 다른 색 위에 얹을 때만 정한다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? Surface { get; set; }
    /// <summary>작은 버튼(검색칸 안에 넣는 것). 안쪽 여백을 줄인다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Compact { get; set; }

    /// <summary>글자색을 따로 정할 때(소설 삭제하기의 붉은 글자 등).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color? TextColor { get; set; }
    /// <summary>글자 앞에 붙는 작은 아이콘(아이콘 글꼴의 글자 하나).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string? Glyph { get => glyph; set { glyph = value; if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); } }
    private string? glyph;
    /// <summary>아이콘 글꼴 대신 그려 둔 그림을 아이콘 자리에 쓴다(보라색 책 모양 등).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Image? GlyphImage { get; set; }
    /// <summary>글자색에 맞춰 아이콘 그림을 그려 주는 함수(크기, 색). 위키 · 메모 아이콘과 같은 색이 되게 한다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Func<int, Color, Bitmap>? GlyphFactory { get; set; }
    private Bitmap? factoryBmp;
    private Color factoryColor;
    private bool muted;
    /// <summary>주 버튼을 옅게 보여 "아직 누를 수 없다"를 나타낸다(눌림 자체는 막지 않으니 쓰는 쪽이 처리한다).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Muted { get => muted; set { muted = value; Invalidate(); } }

    public PillButton(string text)
    {
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        AutoSize = true;
        Margin = new Padding(0, 0, 6, 0);
    }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        if (glyph != null) t.Width += Text.Length == 0 ? S(18) : S(26); // 글자 없이 아이콘만이면 간격은 없다
        return Compact ? new Size(t.Width + S(12) * 2, t.Height + S(7) * 2) : new Size(t.Width + S(14) * 2, t.Height + S(8) * 2);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    // 이 버튼 위에서 누른 키만 뗄 때 클릭으로 친다(창에서 Enter로 확인하고 나면 포커스가 옮겨 온 버튼에 '뗌'만 전달돼 저절로 눌리던 문제)
    private bool keyDownHere;
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) keyDownHere = true;
    }
    protected override void OnLostFocus(EventArgs e) { keyDownHere = false; Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode is Keys.Space or Keys.Enter && keyDownHere) { keyDownHere = false; OnClick(EventArgs.Empty); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { factoryBmp?.Dispose(); factoryBmp = null; }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(Surface ?? Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 검색칸 안(Surface가 정해진 경우)은 바탕이 이미 카드색이라, 안 고른 버튼을 카드색으로 칠하면 칸에 묻혀 안 보인다.
        // 그래서 안 고른 버튼은 본문 바탕색 + 진한 테두리로 칸과 구분되게 한다.
        bool inField = Surface != null;
        Color fill = !Enabled ? p.Card
            : primary && muted ? Themes.Blend(p.Accent, p.Main, 0.55f)
            : primary ? (down ? Themes.Blend(p.Accent, Color.Black, 0.15f) : hover ? Themes.Blend(p.Accent, Color.White, 0.12f) : p.Accent)
            : selected ? p.AccentSoft
            : down || hover ? p.CardHover
            : inField ? p.Main : p.Card;
        Color border = primary && Enabled ? fill
            : selected ? Themes.Blend(p.AccentSoft, p.Accent, 0.5f)
            : inField ? Themes.Blend(p.Line, p.Dim, 0.45f) : p.Line;
        Color text = !Enabled ? p.Dim : primary ? p.AccentFore : TextColor ?? p.Fore;

        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = LibraryPalette.Rounded(r, S(8)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var pen = new Pen(Focused && ShowFocusCues ? p.Accent : border)) g.DrawPath(pen, path);
        }
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        if (glyph == null)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, flags | TextFormatFlags.HorizontalCenter);
            return;
        }
        int tw = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        using var icon = new Font(MainForm.IconFontName(), Font.SizeInPoints * 0.95f);
        // 아이콘의 실제 폭을 재서 [아이콘 + 간격 + 글자] 덩어리를 버튼 가운데에 놓는다(가는 아이콘이 왼쪽으로 치우쳐 보이지 않게)
        if (GlyphFactory != null)
        {
            Color ic = primary ? text : TextColor ?? p.Dim;
            if (factoryBmp == null || factoryColor != ic) { factoryBmp?.Dispose(); factoryBmp = GlyphFactory(S(18) * 3, ic); factoryColor = ic; }
        }
        var gi = GlyphFactory != null ? factoryBmp : GlyphImage;
        int isz = gi != null ? S(18) : TextRenderer.MeasureText(glyph, icon, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        int gap = Text.Length == 0 ? 0 : S(8);
        int x = (Width - (isz + gap + tw)) / 2;
        if (gi != null)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(gi, new Rectangle(x, (Height - isz) / 2, isz, isz));
        }
        else
            TextRenderer.DrawText(g, glyph, icon, new Rectangle(x, 0, isz, Height), primary ? text : TextColor ?? p.Dim, flags | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + isz + gap, 0, tw + S(4), Height), text, flags);
    }
}
