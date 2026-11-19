using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 카테고리별 보기: 분류별로 문서를 묶어 위키 구조를 빠르게 훑어본다.
/// 대분류(카테고리) 머리 밑에 분류 폴더(계층), 그 안에 문서가 동그란 카드(아이콘 + 제목)로 놓인다. 분류 없는 문서는 대분류 바로 밑에.
/// 카드 오른쪽 아래 점: 본문이나 요약이 적힌 문서는 강조색, 비어 있으면 흐린 색.
/// </summary>
internal sealed class WikiCardView : WikiViewBase
{
    private abstract record Item(Rectangle R);
    private sealed record Head(Rectangle R, string Text, string Glyph, int Depth, bool Folder, int Count) : Item(R);
    private sealed record Tile(Rectangle R, WikiDoc Doc) : Item(R);

    private readonly List<Item> items = new();
    private int totalHeight = 200;
    private int layoutWidth = -1;
    private int hot = -1;

    protected override void OnData() { layoutWidth = -1; }

    public override int PreferredHeightFor(int width) { DoLayout(width); return totalHeight; }

    private int CircleD => S(68);
    private int TileW => S(88);
    private int TileH => CircleD + S(30);

    private void DoLayout(int width)
    {
        if (layoutWidth == width) return;
        layoutWidth = width;
        items.Clear();
        int y = S(8);
        void Flow(List<WikiDoc> docs, int x0)
        {
            if (docs.Count == 0) return;
            int cx = x0;
            foreach (var d in docs)
            {
                if (cx > x0 && cx + TileW > width) { cx = x0; y += TileH + S(6); }
                items.Add(new Tile(new Rectangle(cx, y, TileW, TileH), d));
                cx += TileW;
            }
            y += TileH + S(8);
        }
        foreach (var cat in WikiCategory.All)
        {
            var folders = Data.Flatten(cat.Key);
            var loose = Data.LooseDocs(cat.Key);
            if (folders.Count == 0 && loose.Count == 0) continue;
            int count = loose.Count + Data.Docs.Count(d => d.Tags.Count > 0 && folders.Any(f => d.Tags.Contains(f.Name, StringComparer.OrdinalIgnoreCase)));
            items.Add(new Head(new Rectangle(0, y, width, S(30)), cat.Label, cat.Glyph, 0, false, count));
            y += S(38);
            foreach (var (name, depth) in folders)
            {
                int ix = depth * S(24);
                var docs = Data.DocsIn(name);
                items.Add(new Head(new Rectangle(ix, y, width - ix, S(26)), name, "", depth, true, docs.Count));
                y += S(30);
                Flow(docs, ix + S(6));
            }
            Flow(loose, S(6));
            y += S(6);
        }
        if (items.Count == 0) y = S(120);
        totalHeight = y + S(8);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); layoutWidth = -1; }

    private int ItemAt(Point pt)
    {
        DoLayout(Width);
        for (int i = 0; i < items.Count; i++) if (items[i].R.Contains(pt)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = ItemAt(e.Location);
        bool clickable = i >= 0 && (items[i] is Tile || (items[i] is Head { Folder: true }));
        int h = clickable && items[i] is Tile ? i : -1;
        if (h != hot) { hot = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = ItemAt(e.Location);
        if (i < 0) return;
        if (items[i] is Tile t)
        {
            if (e.Button == MouseButtons.Left) RaiseDoc(t.Doc.Path);
            else if (e.Button == MouseButtons.Right) RaiseDocMenu(t.Doc.Path, PointToScreen(e.Location));
        }
        else if (items[i] is Head { Folder: true } h && e.Button == MouseButtons.Right) RaiseFolderMenu(h.Text, PointToScreen(e.Location));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var p = P;
        if (p == null) return;
        DoLayout(Width);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var iconFont = new Font(MainForm.IconFontName(), 11f);
        using var bigIcon = new Font(MainForm.IconFontName(), 19f);
        using var head = new Font(Font.FontFamily, 11.5f, FontStyle.Bold);
        using var sub = new Font(Font, FontStyle.Bold);
        using var small = new Font(Font.FontFamily, Font.SizeInPoints * 0.9f);
        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, "아직 위키 문서가 없어요.\n[+ 새 위키 문서]로 인물 · 장소 · 설정을 적어 두세요.", Font, new Rectangle(0, S(40), Width, S(80)), p.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.R.Bottom < 0 || it.R.Top > Height) continue;
            switch (it)
            {
                case Head h when !h.Folder:
                    TextRenderer.DrawText(g, h.Glyph, iconFont, new Rectangle(h.R.X + S(4), h.R.Y, S(28), h.R.Height), p.Accent, Flags | TextFormatFlags.HorizontalCenter);
                    TextRenderer.DrawText(g, h.Text, head, new Rectangle(h.R.X + S(40), h.R.Y, S(300), h.R.Height), p.Fore, Flags);
                    break;
                case Head h:
                {
                    TextRenderer.DrawText(g, h.Glyph, iconFont, new Rectangle(h.R.X + S(4), h.R.Y, S(24), h.R.Height), p.Dim, Flags | TextFormatFlags.HorizontalCenter);
                    int nw = TextRenderer.MeasureText(h.Text, sub, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, h.Text, sub, new Rectangle(h.R.X + S(34), h.R.Y, nw + S(4), h.R.Height), p.Fore, Flags);
                    TextRenderer.DrawText(g, h.Count.ToString(), small, new Rectangle(h.R.X + S(34) + nw + S(10), h.R.Y, S(40), h.R.Height), p.Dim, Flags);
                    break;
                }
                case Tile t:
                {
                    int cx = t.R.X + (t.R.Width - CircleD) / 2, cy = t.R.Y;
                    var circle = new Rectangle(cx, cy, CircleD, CircleD);
                    bool isHot = i == hot;
                    using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, isHot ? 0.2f : 0.12f))) g.FillEllipse(b, circle);
                    TextRenderer.DrawText(g, WikiCategory.Find(t.Doc.Category).Glyph, bigIcon, circle, Themes.Blend(p.Main, p.Fore, 0.55f), Flags | TextFormatFlags.HorizontalCenter);
                    // 오른쪽 아래 점: 적힌 문서 = 강조색, 비어 있으면 흐리게(흰 테두리 안에)
                    int dd = S(16), dx = circle.Right - dd + S(2), dy = circle.Bottom - dd + S(2);
                    using (var wb = new SolidBrush(p.Main)) g.FillEllipse(wb, dx - S(2), dy - S(2), dd + S(4), dd + S(4));
                    using (var db = new SolidBrush(WikiViewData.IsWritten(t.Doc) ? p.Accent : Themes.Blend(p.Main, p.Fore, 0.07f))) g.FillEllipse(db, dx + S(2), dy + S(2), dd - S(4), dd - S(4));
                    TextRenderer.DrawText(g, t.Doc.Title, Font, new Rectangle(t.R.X + S(2), circle.Bottom + S(4), t.R.Width - S(4), S(22)), isHot ? p.Accent : p.Fore,
                        Flags | TextFormatFlags.HorizontalCenter);
                    break;
                }
            }
        }
    }
}
