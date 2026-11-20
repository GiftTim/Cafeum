using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 간단히 보기: 위키 문서를 간단한 목록으로 빠르게 살펴본다.
/// 맨 위 「작품」 관련 문서 띠 밑에, 대분류마다 [아이콘 이름 | 문서 제목들 · … / 분류 폴더들] 한 줄씩.
/// </summary>
internal sealed class WikiSimpleView : WikiViewBase
{
    private sealed record Hit(Rectangle R, string Text, string? DocPath, bool Folder);
    private sealed record RowBox(Rectangle R, WikiCategory Cat);

    private readonly List<Hit> hits = new();
    private readonly List<RowBox> rows = new();
    private int totalHeight = 200, layoutWidth = -1, hover = -1;

    protected override void OnData() { layoutWidth = -1; }
    protected override void OnResize(EventArgs e) { base.OnResize(e); layoutWidth = -1; }
    public override int PreferredHeightFor(int width) { DoLayout(width); return totalHeight; }

    private int BandH => S(40);
    private int LabelW => S(140);

    private void DoLayout(int width)
    {
        if (layoutWidth == width) return;
        layoutWidth = width;
        hits.Clear(); rows.Clear();
        using var sub = new Font(Font, FontStyle.Regular);
        int y = S(4) + BandH;
        int textW = Math.Max(S(120), width - LabelW - S(36));
        int lineH = Font.Height + S(6);
        foreach (var cat in WikiCategory.All)
        {
            var docs = Data.Docs.Where(d => d.Category == cat.Key).ToList();
            var folders = Data.Flatten(cat.Key);
            if (docs.Count == 0 && folders.Count == 0) continue;
            int top = y, cx = LabelW + S(12), cy = y + S(8);
            // 문서 제목들: "제목 · 제목 · …" (줄이 차면 다음 줄로)
            int dot = TextRenderer.MeasureText(" · ", Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int x = cx;
            for (int i = 0; i < docs.Count; i++)
            {
                int w = TextRenderer.MeasureText(docs[i].Title, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                if (x > cx && x + w > LabelW + S(12) + textW) { x = cx; cy += lineH; }
                hits.Add(new Hit(new Rectangle(x, cy, w + S(2), lineH - S(4)), docs[i].Title, docs[i].Path, false));
                x += w + dot;
            }
            if (docs.Count > 0) cy += lineH;
            // 분류 폴더들
            if (folders.Count > 0)
            {
                x = cx;
                int rowTop = cy;
                foreach (var (name, depth) in folders)
                {
                    int w = S(20) + TextRenderer.MeasureText(name, Font, Size.Empty, TextFormatFlags.NoPadding).Width + S(10);
                    if (x > cx && x + w > LabelW + S(12) + textW) { x = cx; cy += lineH; }
                    hits.Add(new Hit(new Rectangle(x, cy, w, lineH - S(4)), name, null, true));
                    x += w + S(10);
                }
                cy += lineH;
            }
            int h = Math.Max(S(48), cy - top + S(4));
            rows.Add(new RowBox(new Rectangle(0, top, width, h), cat));
            y = top + h;
        }
        // 맨 아래 템플릿 칸: 템플릿 보관함의 틀 제목들(없으면 안내). 누르면 열어서 고친다.
        {
            var tcat = new WikiCategory("template", "템플릿", "\uE8C8");
            int top = y, cx = LabelW + S(12), cy = y + S(8);
            int dot = TextRenderer.MeasureText(" · ", Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int x = cx;
            foreach (var t in Data.Templates)
            {
                int w = TextRenderer.MeasureText(t.Title, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                if (x > cx && x + w > LabelW + S(12) + textW) { x = cx; cy += lineH; }
                hits.Add(new Hit(new Rectangle(x, cy, w + S(2), lineH - S(4)), t.Title, t.Path, false));
                x += w + dot;
            }
            cy += lineH;
            int h = Math.Max(S(48), cy - top + S(4));
            rows.Add(new RowBox(new Rectangle(0, top, width, h), tcat));
            y = top + h;
        }
        totalHeight = Math.Max(y + S(8), S(140));
    }

    private int HitAt(Point pt)
    {
        DoLayout(Width);
        for (int i = 0; i < hits.Count; i++) if (hits[i].R.Contains(pt)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = HitAt(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = HitAt(e.Location);
        if (i < 0) return;
        var h = hits[i];
        if (h.DocPath != null)
        {
            if (e.Button == MouseButtons.Left) RaiseDoc(h.DocPath);
            else if (e.Button == MouseButtons.Right) RaiseDocMenu(h.DocPath, PointToScreen(e.Location));
        }
        else if (h.Folder && e.Button == MouseButtons.Right) RaiseFolderMenu(h.Text, PointToScreen(e.Location));
        else if (h.Folder && e.Button == MouseButtons.Left) RaiseClass(h.Text); // 분류를 누르면 그 분류의 쪽
    }

    /// <summary>왼쪽의 대분류 이름 칸을 두 번 누르면 그 대분류의 쪽을 연다.</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        DoLayout(Width);
        foreach (var row in rows)
            if (new Rectangle(0, row.R.Y, LabelW, row.R.Height).Contains(e.Location)) { string key = row.Cat.Key; BeginInvoke(() => RaiseCategory(key)); return; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var p = P;
        if (p == null) return;
        DoLayout(Width);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bold = new Font(Font.FontFamily, Font.SizeInPoints, FontStyle.Bold);
        using var head = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        using var icon = new Font(MainForm.IconFontName(), 11f);
        using var small = new Font(Font.FontFamily, Font.SizeInPoints * 0.92f);
        var line = Themes.Blend(p.Main, p.Fore, 0.12f);
        // 맨 위 띠
        var band = new Rectangle(0, S(4), Width, BandH);
        using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.035f))) g.FillRectangle(b, band);
        TextRenderer.DrawText(g, $"『{Data.NovelName}』 관련 문서", head, band, p.Fore, Flags | TextFormatFlags.HorizontalCenter);
        if (rows.Count == 0)
        {
            TextRenderer.DrawText(g, "아직 위키 문서가 없어요.", Font, new Rectangle(0, band.Bottom + S(30), Width, S(30)), p.Dim, Flags | TextFormatFlags.HorizontalCenter);
            return;
        }
        using var linePen = new Pen(line);
        foreach (var r in rows)
        {
            using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.03f))) g.FillRectangle(b, 0, r.R.Y, LabelW, r.R.Height);
            g.DrawLine(linePen, 0, r.R.Y, Width, r.R.Y);
            g.DrawLine(linePen, 0, r.R.Bottom, Width, r.R.Bottom);
            TextRenderer.DrawText(g, r.Cat.Glyph, icon, new Rectangle(S(14), r.R.Y, S(24), r.R.Height), p.Fore, Flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, r.Cat.Label, bold, new Rectangle(S(46), r.R.Y, LabelW - S(52), r.R.Height), p.Fore, Flags);
        }
        if (Data.Templates.Count == 0 && rows.Count > 0 && rows[^1].Cat.Key == "template")
            TextRenderer.DrawText(g, "아직 템플릿이 없어요. [새 위키 문서 ▸ 템플릿]으로 만들어 보세요.", Font, new Rectangle(LabelW + S(12), rows[^1].R.Y + S(4), Width - LabelW - S(24), Font.Height + S(8)), p.Dim, Flags);
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            if (h.Folder)
            {
                TextRenderer.DrawText(g, "", icon, new Rectangle(h.R.X, h.R.Y, S(20), h.R.Height), (i == hover ? p.Accent : p.Dim), Flags | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, h.Text, small, new Rectangle(h.R.X + S(24), h.R.Y, h.R.Width - S(24), h.R.Height), (i == hover ? p.Accent : p.Dim), Flags);
            }
            else
            {
                bool hot = i == hover;
                using var f = new Font(Font, hot ? FontStyle.Underline : FontStyle.Regular);
                TextRenderer.DrawText(g, h.Text, f, h.R, hot ? p.Accent : p.Fore, Flags);
                // 제목 사이 점(마지막 제외)
                if (i + 1 < hits.Count && !hits[i + 1].Folder && hits[i + 1].R.Y == h.R.Y)
                    TextRenderer.DrawText(g, "·", Font, new Rectangle(h.R.Right + S(4), h.R.Y, S(12), h.R.Height), p.Dim, Flags | TextFormatFlags.HorizontalCenter);
            }
        }
    }
}
