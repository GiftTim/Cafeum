using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 이름 생성기 위젯(사이드바 · 위젯 모음의 [이름 생성기]로 켜고 끈다): 머리 줄 [이름 생성기 · ^], 조건 칩(최소 · 최대 글자, 시작 글자, 개수), [이름 만들기] 단추, 그 밑의 이름 목록.
/// 이름을 누르면 글 쓰던 자리에 들어가고, 우클릭하면 이름 · 한글 발음을 넣거나 복사한다. 배경(머리 줄)을 끌어 옮길 수 있다.
/// </summary>
internal sealed class NameWidget : Control
{
    private List<(string Name, string Korean)> items = new();
    private readonly List<Rectangle> chipRects = new();
    private Rectangle foldRect, makeRect, listRect;
    private int hotChip = -1, hotRow = -1, hotBtn = -1, offset; // hotBtn: 0 이름 만들기 · 1 ^
    private bool expanded = true, dragging;
    private Point dragFrom;
    private Color back = Color.White, fore = Color.Black, accent = Color.SteelBlue;
    private readonly ToolTip tip = new();

    public int MinLen = 4, MaxLen = 8, Count = 10;
    public string Start = "", KStart = "";
    /// <summary>true면 한국어 이름(한글), false면 영어 이름 + 한글 발음.</summary>
    public bool Korean;
    /// <summary>한국어 이름에 성을 붙일까.</summary>
    public bool KSurname = true;

    public event Action<string>? NameChosen;
    /// <summary>조건(글자 수 · 시작 글자 · 개수)을 바꿨다 — 설정에 적는다.</summary>
    public event Action? OptionsChanged;
    public event Action? Moved;
    public event Action? ExpandedChanged;

    private const int VisibleRows = 8;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Expanded { get => expanded; set { if (expanded == value) return; expanded = value; Invalidate(); ExpandedChanged?.Invoke(); } }

    public NameWidget()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false; // 눌러도 글 상자에서 커서를 가져오지 않는다(그래야 눌렀을 때 쓰던 자리에 이름이 들어간다)
        Font = new Font(NovelStore.UiFontFamily, 10f);
        Disposed += (_, _) => tip.Dispose();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int HeaderH => S(54);
    private int RowH => S(34);
    private int Pad => S(16);

    public void SetColors(Color b, Color f, Color a) { if (b == back && f == fore && a == accent) return; back = b; fore = f; accent = a; Invalidate(); }

    public void SetNames(List<(string Name, string Korean)> list) { items = list; offset = 0; Invalidate(); }

    /// <summary>지금 모양(펼침 · 접힘)에서 위젯이 차지할 크기.</summary>
    public Size DesiredSize()
    {
        int w = S(380);
        if (!expanded) return new Size(w, HeaderH);
        int rows = Math.Min(VisibleRows, items.Count);
        int h = HeaderH + S(6) + S(32) + S(10) + S(40) + S(14) + (rows > 0 ? rows * RowH + S(4) : S(28)) + S(12);
        return new Size(w, h);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width > 2 && Height > 2)
        {
            using var path = Rounded(new Rectangle(0, 0, Width, Height), S(14));
            Region?.Dispose();
            Region = new Region(path);
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private string[] ChipTexts => Korean
        ? new[] { "한국어", KSurname ? "성 포함" : "이름만", KStart.Length == 0 ? "시작 없음" : $"시작 {KStart}", $"개수 {Count}", "자료" }
        : new[] { "영어", $"최소 {MinLen}", $"최대 {MaxLen}", Start.Length == 0 ? "시작 없음" : $"시작 {Start}", $"개수 {Count}" };

    private void Layout()
    {
        int bs = S(28);
        foldRect = new Rectangle(Width - Pad - bs, (HeaderH - bs) / 2, bs, bs);
        chipRects.Clear();
        if (!expanded) { makeRect = listRect = Rectangle.Empty; return; }
        int y = HeaderH + S(6), gap = S(5), cw = (Width - Pad * 2 - gap * 4) / 5;
        for (int i = 0; i < 5; i++) chipRects.Add(new Rectangle(Pad + i * (cw + gap), y, cw, S(32)));
        y += S(32) + S(10);
        makeRect = new Rectangle(Pad, y, Width - Pad * 2, S(40));
        y += S(40) + S(14);
        listRect = new Rectangle(Pad, y, Width - Pad * 2, Math.Max(0, Height - y - S(12)));
    }

    private int RowAt(Point pt)
    {
        if (!listRect.Contains(pt)) return -1;
        int i = (pt.Y - listRect.Y) / RowH + offset;
        return i >= 0 && i < items.Count ? i : -1;
    }

    // ───────── 일 ─────────

    public void Generate()
    {
        SetNames(Korean
            ? KoreanNameGenerator.Make(KSurname, KStart, Count).Select(n => (n, "")).ToList()
            : NameGenerator.Make(MinLen, MaxLen, Start, Count));
        ExpandedChanged?.Invoke(); // 목록 줄 수에 맞춰 크기를 다시 잡는다
    }

    private void PickMenu(int chip)
    {
        var m = new ContextMenuStrip();
        MenuGuard.Attach(m);
        if (chip == 0)
        {
            foreach (bool ko in new[] { false, true })
            {
                bool k = ko;
                m.Items.Add(new ToolStripMenuItem(k ? "한국어 이름" : "영어 이름 (+ 한글 발음)", null, (_, _) => BeginInvoke(() => { Korean = k; items = new(); OptionsChanged?.Invoke(); ExpandedChanged?.Invoke(); Invalidate(); })) { Checked = Korean == k });
            }
        }
        else if (Korean && chip == 1)
        {
            foreach (bool sur in new[] { true, false })
            {
                bool v = sur;
                m.Items.Add(new ToolStripMenuItem(v ? "성 포함 (많이 쓰는 성이 더 자주)" : "이름만", null, (_, _) => BeginInvoke(() => { KSurname = v; OptionsChanged?.Invoke(); Invalidate(); })) { Checked = KSurname == v });
            }
        }
        else if (Korean && chip == 2)
        {
            m.Items.Add(new ToolStripMenuItem("없음", null, (_, _) => BeginInvoke(() => { KStart = ""; OptionsChanged?.Invoke(); Invalidate(); })) { Checked = KStart.Length == 0 });
            foreach (string c in KoreanNameGenerator.Starts)
            {
                string s = c;
                m.Items.Add(new ToolStripMenuItem(s, null, (_, _) => BeginInvoke(() => { KStart = s; OptionsChanged?.Invoke(); Invalidate(); })) { Checked = KStart == s });
            }
        }
        else if (Korean && chip == 4)
        {
            NameDb.EnsureLoaded();
            m.Items.Add(new ToolStripMenuItem($"지금 쓰는 자료: {(NameDb.Source == NameDb.UserPath ? "내 엑셀 파일" : "기본 자료")} (성 {NameDb.Surnames.Length:N0}개 · 이름 {NameDb.Names.Length:N0}줄)") { Enabled = false });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("자료 엑셀 파일 열기 (고쳐서 저장하면 반영)", null, (_, _) => BeginInvoke(() =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(NameDb.EnsureUserCopy()) { UseShellExecute = true }); }
                catch (Exception ex) { AppLog.Write("이름 자료 엑셀 파일을 열지 못했어요", ex); }
            }));
            m.Items.Add("자료 다시 읽기", null, (_, _) => BeginInvoke(() => { NameDb.Reload(); Invalidate(); }));
        }
        else if (chip == 1 || chip == 2)
        {
            for (int n = 3; n <= 20; n++)
            {
                int v = n;
                var it = new ToolStripMenuItem($"{v}글자", null, (_, _) => BeginInvoke(() =>
                {
                    if (chip == 1) { MinLen = v; if (MaxLen < v) MaxLen = v; } else { MaxLen = v; if (MinLen > v) MinLen = v; }
                    OptionsChanged?.Invoke(); Invalidate();
                })) { Checked = (chip == 1 ? MinLen : MaxLen) == v };
                m.Items.Add(it);
            }
        }
        else if (!Korean && chip == 3)
        {
            m.Items.Add(new ToolStripMenuItem("없음", null, (_, _) => BeginInvoke(() => { Start = ""; OptionsChanged?.Invoke(); Invalidate(); })) { Checked = Start.Length == 0 });
            foreach (var grp in new[] { "ABCDEF", "GHIJKL", "MNOPQR", "STUVWXYZ" })
            {
                var sub = new ToolStripMenuItem($"{grp[0]} – {grp[^1]}");
                foreach (char c in grp)
                {
                    string s = c.ToString();
                    sub.DropDownItems.Add(new ToolStripMenuItem(s, null, (_, _) => BeginInvoke(() => { Start = s; OptionsChanged?.Invoke(); Invalidate(); })) { Checked = Start == s });
                }
                m.Items.Add(sub);
            }
        }
        else
        {
            foreach (int n in new[] { 5, 10, 15, 20, 30 })
            {
                int v = n;
                m.Items.Add(new ToolStripMenuItem($"{v}개", null, (_, _) => BeginInvoke(() => { Count = v; OptionsChanged?.Invoke(); Invalidate(); })) { Checked = Count == v });
            }
        }
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(this, new Point(chipRects[chip].Left, chipRects[chip].Bottom + S(2)));
    }

    private void RowMenu(int i)
    {
        var (name, kor) = items[i];
        var m = new ContextMenuStrip();
        MenuGuard.Attach(m);
        m.Items.Add($"'{name}' 넣기", null, (_, _) => BeginInvoke(() => NameChosen?.Invoke(name)));
        if (kor.Length > 0)
        {
            m.Items.Add($"'{kor}' 넣기", null, (_, _) => BeginInvoke(() => NameChosen?.Invoke(kor)));
            m.Items.Add(new ToolStripSeparator());
        }
        m.Items.Add("이름 복사", null, (_, _) => Copy(name));
        if (kor.Length > 0) m.Items.Add("한글 발음 복사", null, (_, _) => Copy(kor));
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        m.Show(Cursor.Position);
    }

    private static void Copy(string s) { try { Clipboard.SetText(s); } catch (Exception ex) { AppLog.Write("이름을 클립보드에 복사하지 못했어요", ex); } }

    // ───────── 입력 ─────────

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
        Layout();
        int chip = chipRects.FindIndex(r => r.Contains(e.Location));
        int row = RowAt(e.Location);
        int btn = makeRect.Contains(e.Location) ? 0 : foldRect.Contains(e.Location) ? 1 : -1;
        if (chip != hotChip || row != hotRow || btn != hotBtn)
        {
            hotChip = chip; hotRow = row; hotBtn = btn; Invalidate();
            tip.SetToolTip(this, row >= 0 ? $"{items[row].Name}  {items[row].Korean}" : btn == 1 ? (expanded ? "작게 접기" : "펼치기") : "");
        }
        Cursor = chip >= 0 || row >= 0 || btn >= 0 ? Cursors.Hand : Cursors.SizeAll;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotChip = hotRow = hotBtn = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Layout();
        bool onThing = chipRects.Any(r => r.Contains(e.Location)) || makeRect.Contains(e.Location) || foldRect.Contains(e.Location) || RowAt(e.Location) >= 0;
        if (e.Button == MouseButtons.Left && !onThing) { dragging = true; dragFrom = e.Location; Capture = true; }
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
        Layout();
        int chip = chipRects.FindIndex(r => r.Contains(e.Location)), row = RowAt(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            if (chip >= 0) PickMenu(chip);
            else if (makeRect.Contains(e.Location)) Generate();
            else if (foldRect.Contains(e.Location)) Expanded = !expanded;
            else if (row >= 0) NameChosen?.Invoke(items[row].Name);
        }
        else if (e.Button == MouseButtons.Right && row >= 0) RowMenu(row);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, items.Count - VisibleRows);
        if (max == 0) return;
        offset = Math.Clamp(offset - Math.Sign(e.Delta), 0, max);
        Invalidate();
    }

    // ───────── 그리기 ─────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Layout();
        bool dark = back.GetBrightness() < 0.4f;
        Color card = dark ? Themes.Blend(back, Color.White, 0.07f) : Color.White;
        Color line = Themes.Blend(card, fore, dark ? 0.22f : 0.14f);
        Color dim = Themes.Blend(card, fore, 0.55f);
        const TextFormatFlags C = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags Lft = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

        using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(14)))
        {
            using var b = new SolidBrush(card); g.FillPath(b, path);
            using var pen = new Pen(line); g.DrawPath(pen, path);
        }

        // 머리 줄: [Aa] 이름 생성기 ······ [^]
        int ib = S(24);
        var ir = new Rectangle(Pad, (HeaderH - ib) / 2, ib, ib);
        using (var path = Rounded(ir, S(5))) using (var b = new SolidBrush(Themes.Blend(card, fore, 0.62f))) g.FillPath(b, path);
        using (var af = new Font(Font.FontFamily, 9.5f, FontStyle.Bold)) TextRenderer.DrawText(g, "Aa", af, ir, card, C);
        using (var tf = new Font(Font.FontFamily, 11.5f)) TextRenderer.DrawText(g, "이름 생성기", tf, new Rectangle(ir.Right + S(12), 0, Width / 2, HeaderH), dim, Lft);
        float u = DeviceDpi / 96f;
        using (var pen = new Pen(hotBtn == 1 ? fore : dim, Math.Max(1.6f, 1.8f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float cx = foldRect.X + foldRect.Width / 2f, cy = foldRect.Y + foldRect.Height / 2f, d = 5f * u;
            Shapes.Chevron(g, pen, cx, cy, d, d / 2, expanded ? Arrow.Up : Arrow.Down); // 펼쳐 있으면 ^ (눌러서 접는다)
        }
        if (!expanded) return;

        // 조건 칩
        var texts = ChipTexts;
        using (var cf = new Font(Font.FontFamily, 9f))
            for (int i = 0; i < chipRects.Count; i++)
            {
                var r = chipRects[i];
                using var path = Rounded(r, S(8));
                using (var b = new SolidBrush(i == hotChip ? Themes.Blend(card, accent, 0.14f) : card)) g.FillPath(b, path);
                using (var pen = new Pen(i == hotChip ? accent : line)) g.DrawPath(pen, path);
                TextRenderer.DrawText(g, texts[i], cf, r, fore, C);
            }

        // [이름 만들기]
        using (var path = Rounded(makeRect, S(10)))
        using (var b = new SolidBrush(hotBtn == 0 ? Themes.Blend(accent, Color.White, 0.12f) : accent)) g.FillPath(b, path);
        using (var mf = new Font(Font.FontFamily, 11f, FontStyle.Bold)) TextRenderer.DrawText(g, "이름 만들기", mf, makeRect, Color.White, C);

        // 이름 목록
        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, "[이름 만들기]를 눌러 보세요", Font, new Rectangle(listRect.X, listRect.Y - S(4), listRect.Width, S(28)), dim, C);
            return;
        }
        using var nf = new Font(Font.FontFamily, 11.5f, FontStyle.Bold);
        using var kf = new Font(Font.FontFamily, 10f);
        int rows = Math.Min(VisibleRows, items.Count);
        for (int k = 0; k < rows; k++)
        {
            int i = offset + k;
            if (i >= items.Count) break;
            var r = new Rectangle(listRect.X, listRect.Y + k * RowH, listRect.Width, RowH);
            if (i == hotRow) { using var path = Rounded(Rectangle.Inflate(r, -S(2), -S(2)), S(7)); using var b = new SolidBrush(Themes.Blend(card, accent, 0.12f)); g.FillPath(b, path); }
            TextRenderer.DrawText(g, items[i].Name, nf, new Rectangle(r.X + S(10), r.Y, r.Width / 2, r.Height), fore, Lft);
            TextRenderer.DrawText(g, items[i].Korean, kf, new Rectangle(r.X + r.Width / 2, r.Y, r.Width / 2 - S(10), r.Height), dim, Lft);
        }
        if (items.Count > VisibleRows) // 더 있으면 오른쪽에 가는 막대
        {
            int th = Math.Max(S(20), listRect.Height * VisibleRows / items.Count), ty = listRect.Y + (listRect.Height - th) * offset / Math.Max(1, items.Count - VisibleRows);
            using var sp = Rounded(new Rectangle(Width - S(8), ty, S(3), th), S(1)); using var sb = new SolidBrush(Themes.Blend(card, fore, 0.25f)); g.FillPath(sb, sp);
        }
    }
}
