using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 맞춤법 검사기 위젯: 머리 줄 [검사 아이콘 · 맞춤법 검사 · 개수 · ^] 밑에 [상태 · AI 검사 · 모두 고치기] 줄, 그 밑에 틀린 곳 목록.
/// 목록 줄을 누르면 글의 그 자리로 가고(JumpRequested), 줄 오른쪽 [고치기]를 누르면 추천하는 말로 바꾼다(FixRequested).
/// [^]를 누르면 머리 줄만 남은 작은 창으로 접힌다. 머리 · 빈 곳을 끌어 옮길 수 있다.
/// </summary>
internal sealed class SpellWidget : Control
{
    private List<SpellIssue> issues = new();
    private string status = "";
    private bool aiAvailable, aiBusy, expanded = true, dragging, hotFold, hasChecked;
    private Point dragFrom;
    private int scroll, hotRow = -1, hotBtn = -1; // hotBtn: 0 AI · 1 모두 고치기 · 2 접기 · 3 줄의 [고치기]
    private Color back = Color.White, fore = Color.Black, accent = Color.SteelBlue;
    private readonly List<(Rectangle R, int Index)> fixRects = new();
    private readonly List<(Rectangle R, int Index)> rowRects = new();
    private Rectangle foldRect, aiRect, allRect;

    public event Action<int>? JumpRequested;
    public event Action<int>? FixRequested;
    public event Action? FixAllRequested;
    public event Action? AiRequested;
    public event Action? Moved;
    public event Action? ExpandedChanged;

    private const int MaxRows = 5;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Expanded { get => expanded; set { if (expanded == value) return; expanded = value; Invalidate(); ExpandedChanged?.Invoke(); } }

    public SpellWidget()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false; // 눌러도 글 상자에서 커서를 가져오지 않는다
        Font = new Font(NovelStore.UiFontFamily, 10f);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int HeaderH => S(46);
    private int ToolH => S(44);
    private int RowH => S(58);

    /// <summary>검사 결과를 보여 준다(hasChecked: 한 번이라도 검사했는가 — 아니면 안내 글만).</summary>
    public void SetIssues(List<SpellIssue> list, string statusText, bool checkedOnce)
    {
        issues = list; status = statusText; hasChecked = checkedOnce;
        scroll = Math.Clamp(scroll, 0, MaxScroll());
        Invalidate();
    }

    public void SetAi(bool available, bool busy) { if (aiAvailable == available && aiBusy == busy) return; aiAvailable = available; aiBusy = busy; Invalidate(); }
    public void SetColors(Color b, Color f, Color a) { if (b == back && f == fore && a == accent) return; back = b; fore = f; accent = a; Invalidate(); }

    private int ListH() => issues.Count == 0 ? S(64) : Math.Min(issues.Count, MaxRows) * RowH;
    private int MaxScroll() => Math.Max(0, issues.Count * RowH - ListH());

    /// <summary>지금 모양에서 위젯이 차지할 크기.</summary>
    public Size DesiredSize() => new(S(344), expanded ? HeaderH + ToolH + ListH() + S(14) : S(48));

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width > 2 && Height > 2)
        {
            using var path = Rounded(new Rectangle(0, 0, Width, Height), S(12));
            Region?.Dispose();
            Region = new Region(path);
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ───────── 입력 ─────────

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!expanded || issues.Count <= MaxRows) return;
        scroll = Math.Clamp(scroll - Math.Sign(e.Delta) * RowH, 0, MaxScroll());
        Invalidate();
    }

    private int HitFix(Point pt) { foreach (var (r, i) in fixRects) if (r.Contains(pt)) return i; return -1; }
    private int HitRow(Point pt) { foreach (var (r, i) in rowRects) if (r.Contains(pt)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging && Parent != null)
        {
            var np = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y);
            np.X = Math.Clamp(np.X, 0, Math.Max(0, Parent.ClientSize.Width - Width));
            np.Y = Math.Clamp(np.Y, 0, Math.Max(0, Parent.ClientSize.Height - Height));
            var old = Bounds;
            Location = np;
            Parent.Invalidate(old, true);
            Parent.Update();
            return;
        }
        int row = HitRow(e.Location), fx = HitFix(e.Location);
        int b = foldRect.Contains(e.Location) ? 2 : aiRect.Contains(e.Location) ? 0 : allRect.Contains(e.Location) ? 1 : fx >= 0 ? 3 : -1;
        if (row != hotRow || b != hotBtn) { hotRow = row; hotBtn = b; Invalidate(); }
        Cursor = b >= 0 || row >= 0 ? Cursors.Hand : Cursors.SizeAll;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotRow = hotBtn = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HitRow(e.Location) < 0 && !foldRect.Contains(e.Location) && !aiRect.Contains(e.Location) && !allRect.Contains(e.Location)) { dragging = true; dragFrom = e.Location; Capture = true; }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragging)
        {
            bool moved = Math.Abs(e.X - dragFrom.X) + Math.Abs(e.Y - dragFrom.Y) > S(2);
            dragging = false; Capture = false;
            if (moved) Moved?.Invoke();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (foldRect.Contains(e.Location)) { Expanded = !expanded; return; }
        if (!expanded) return;
        if (aiRect.Contains(e.Location)) { AiRequested?.Invoke(); return; }
        if (allRect.Contains(e.Location) && issues.Count > 0) { FixAllRequested?.Invoke(); return; }
        int fx = HitFix(e.Location);
        if (fx >= 0) { FixRequested?.Invoke(fx); return; }
        int row = HitRow(e.Location);
        if (row >= 0) JumpRequested?.Invoke(row);
    }

    // ───────── 그리기 ─────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        bool dark = back.GetBrightness() < 0.4f;
        Color card = dark ? Themes.Blend(back, Color.White, 0.07f) : Color.White;
        Color line = Themes.Blend(card, fore, dark ? 0.22f : 0.14f);
        Color dim = Themes.Blend(card, fore, 0.55f);
        Color red = Color.FromArgb(0xD6, 0x45, 0x45), green = Color.FromArgb(0x2E, 0x9E, 0x62);
        const TextFormatFlags C = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        float u = DeviceDpi / 96f;
        fixRects.Clear(); rowRects.Clear();

        using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(12)))
        {
            using var b = new SolidBrush(card); g.FillPath(b, path);
            using var pen = new Pen(line); g.DrawPath(pen, path);
        }

        // 머리: [A✓] 맞춤법 검사 (개수) ········ [^]
        int hh = expanded ? HeaderH : Height;
        int ib = S(24);
        var ir = new Rectangle(S(14), (hh - ib) / 2, ib, ib);
        using (var path = Rounded(ir, S(5))) using (var b = new SolidBrush(Themes.Blend(card, fore, 0.62f))) g.FillPath(b, path);
        using (var af = new Font(Font.FontFamily, 9f, FontStyle.Bold)) TextRenderer.DrawText(g, "가", af, ir, card, C);
        using (var tf = new Font(Font.FontFamily, 11.5f)) TextRenderer.DrawText(g, "맞춤법 검사", tf, new Rectangle(ir.Right + S(10), 0, S(130), hh), dim, L);
        if (issues.Count > 0)
        {
            string n = issues.Count.ToString();
            using var nf = new Font(Font.FontFamily, 9f, FontStyle.Bold);
            int nw = Math.Max(S(22), TextRenderer.MeasureText(n, nf, Size.Empty, TextFormatFlags.NoPadding).Width + S(12));
            var nr = new Rectangle(ir.Right + S(10) + S(104), (hh - S(20)) / 2, nw, S(20));
            using (var path = Rounded(nr, S(10))) using (var b = new SolidBrush(red)) g.FillPath(b, path);
            TextRenderer.DrawText(g, n, nf, nr, Color.White, C);
        }
        int bs = S(28);
        foldRect = new Rectangle(Width - S(12) - bs, (hh - bs) / 2, bs, bs);
        using (var pen = new Pen(hotBtn == 2 ? fore : dim, Math.Max(1.6f, 1.8f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float cx = foldRect.X + foldRect.Width / 2f, cy = foldRect.Y + foldRect.Height / 2f, d = 5f * u;
            if (expanded) g.DrawLines(pen, new[] { new PointF(cx - d, cy + d / 2), new PointF(cx, cy - d / 2), new PointF(cx + d, cy + d / 2) });
            else g.DrawLines(pen, new[] { new PointF(cx - d, cy - d / 2), new PointF(cx, cy + d / 2), new PointF(cx + d, cy - d / 2) });
        }
        if (!expanded) { aiRect = allRect = Rectangle.Empty; return; }

        // 도구 줄: 상태 ········ [AI 검사] [모두 고치기]
        int ty = HeaderH;
        using (var sf = new Font(Font.FontFamily, 9.5f)) TextRenderer.DrawText(g, status, sf, new Rectangle(S(16), ty, S(120), ToolH), aiBusy ? accent : dim, L);
        using var bf = new Font(Font.FontFamily, 9f, FontStyle.Bold);
        int bh = S(28), by = ty + (ToolH - bh) / 2;
        allRect = new Rectangle(Width - S(14) - S(78), by, S(78), bh);
        aiRect = new Rectangle(allRect.Left - S(8) - S(74), by, S(74), bh);
        void Pill(Rectangle r, string text, bool enabled, bool hot, bool primary)
        {
            using var path = Rounded(r, S(8));
            if (primary && enabled) { using var b = new SolidBrush(hot ? Themes.Blend(accent, Color.White, 0.12f) : accent); g.FillPath(b, path); }
            else { using var b = new SolidBrush(hot && enabled ? Themes.Blend(card, fore, 0.06f) : card); g.FillPath(b, path); using var pen = new Pen(line); g.DrawPath(pen, path); }
            TextRenderer.DrawText(g, text, bf, r, primary && enabled ? Color.White : enabled ? fore : Themes.Blend(card, fore, 0.3f), C);
        }
        Pill(aiRect, aiBusy ? "중지" : "AI 검사", aiAvailable || aiBusy, hotBtn == 0, true);
        Pill(allRect, "모두 고치기", issues.Count > 0, hotBtn == 1, false);
        using (var pen = new Pen(line)) g.DrawLine(pen, S(12), ty + ToolH - 1, Width - S(12), ty + ToolH - 1);

        // 목록
        int ly = ty + ToolH;
        var clip = new Rectangle(0, ly, Width, ListH());
        if (issues.Count == 0)
        {
            string msg = hasChecked ? "틀린 곳이 없어요 ✓" : "글을 쓰면 틀린 곳을 바로 찾아요";
            TextRenderer.DrawText(g, msg, Font, clip, dim, C);
            return;
        }
        var saved = g.Save();
        g.SetClip(clip);
        using var wf = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
        using var wst = new Font(Font.FontFamily, 10.5f, FontStyle.Bold | FontStyle.Strikeout);
        using var nf2 = new Font(Font.FontFamily, 8.5f);
        using var ff = new Font(Font.FontFamily, 9f, FontStyle.Bold);
        for (int i = 0; i < issues.Count; i++)
        {
            var r = new Rectangle(S(8), ly + i * RowH - scroll, Width - S(16), RowH);
            if (r.Bottom < ly || r.Top > ly + ListH()) continue;
            rowRects.Add((r, i));
            if (i == hotRow) using (var hb = new SolidBrush(Themes.Blend(card, fore, 0.05f))) using (var hp = Rounded(r, S(8))) g.FillPath(hb, hp);
            var it = issues[i];
            int fixW = S(56);
            var fr = new Rectangle(r.Right - S(8) - fixW, r.Y + (r.Height - S(28)) / 2, fixW, S(28));
            fixRects.Add((fr, i));
            // 줄 1: 틀린 말(빨강, 취소선) → 추천(초록)
            int tx = r.X + S(10), w1 = TextRenderer.MeasureText(it.Wrong, wst, Size.Empty, TextFormatFlags.NoPadding).Width;
            int room = fr.Left - tx - S(8);
            int w1c = Math.Min(w1, room / 2);
            TextRenderer.DrawText(g, it.Wrong, wst, new Rectangle(tx, r.Y + S(6), w1c, S(24)), red, L);
            TextRenderer.DrawText(g, "→", wf, new Rectangle(tx + w1c + S(4), r.Y + S(6), S(18), S(24)), dim, C);
            TextRenderer.DrawText(g, it.Suggest, wf, new Rectangle(tx + w1c + S(24), r.Y + S(6), Math.Max(S(20), room - w1c - S(24)), S(24)), green, L);
            // 줄 2: 이유 (AI가 찾은 것은 표시)
            string note = (it.Source == 1 ? "AI · " : "") + it.Note;
            TextRenderer.DrawText(g, note, nf2, new Rectangle(tx, r.Y + S(30), room, S(20)), dim, L);
            // [고치기]
            bool hotFix = hotBtn == 3 && HitFix(PointToClient(Cursor.Position)) == i;
            using (var path = Rounded(fr, S(8))) { using var b = new SolidBrush(hotFix ? Themes.Blend(accent, Color.White, 0.12f) : Themes.Blend(card, accent, 0.12f)); g.FillPath(b, path); }
            TextRenderer.DrawText(g, "고치기", ff, fr, hotFix ? Color.White : accent, C);
        }
        g.Restore(saved);
        if (issues.Count > MaxRows) // 오른쪽 가는 스크롤 표시
        {
            int th = clip.Height, thumb = Math.Max(S(20), th * MaxRows / issues.Count);
            int ty2 = ly + (MaxScroll() == 0 ? 0 : (th - thumb) * scroll / MaxScroll());
            using var sb = new SolidBrush(Themes.Blend(card, fore, 0.2f));
            using var sp = Rounded(new Rectangle(Width - S(6), ty2, S(3), thumb), S(1));
            g.FillPath(sb, sp);
        }
    }
}
