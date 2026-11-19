using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>위키 문서 화면에서 쓰는 작은 부품들(수정/보기 스위치 · 별점 · 분류 칩 · 고르기 팝업).</summary>
internal static class WikiUi
{
    /// <summary>입력칸 · 팝업 바탕(밝은 테마는 흰색).</summary>
    public static Color Surface(LibraryPalette p) => p.Dark ? Themes.Blend(p.Main, Color.White, 0.06f) : Color.White;

    public const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
}

/// <summary>
/// [✎ | 👁] 수정 ↔ 보기 슬라이드 스위치. 고른 쪽이 밝은 알약으로 올라와 있다.
/// 누르면 바뀌고 EditingChanged가 난다(코드로 Editing을 바꿀 때는 나지 않는다).
/// </summary>
internal sealed class ModeSwitch : Control
{
    private LibraryPalette? palette;
    private bool editing = true;
    private int hover = -1;

    public event Action? EditingChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool Editing { get => editing; set { if (editing == value) return; editing = value; Invalidate(); } }

    public ModeSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = false;
        Size = new Size(LogicalToDeviceUnits(74), LogicalToDeviceUnits(28));
    }

    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = e.X < Width / 2 ? 0 : 1; if (i != hover) { hover = i; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        bool wantEdit = e.X < Width / 2;
        if (wantEdit == editing) return;
        editing = wantEdit;
        Invalidate();
        EditingChanged?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (palette is not { } p) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var all = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = LibraryPalette.Rounded(all, S(8)))
        {
            using var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.06f));
            g.FillPath(b, path);
        }
        int pad = S(3), segW = (Width - pad * 2) / 2;
        var sel = new RectangleF(pad + (editing ? 0 : segW), pad, segW, Height - pad * 2);
        using (var path = LibraryPalette.Rounded(sel, S(6)))
        {
            using var b = new SolidBrush(p.Dark ? Themes.Blend(p.Main, Color.White, 0.14f) : Color.White);
            g.FillPath(b, path);
            using var pen = new Pen(p.Line);
            g.DrawPath(pen, path);
        }
        using var icon = new Font(MainForm.IconFontName(), 11f);
        for (int i = 0; i < 2; i++)
        {
            bool on = (i == 0) == editing;
            var r = new Rectangle(pad + i * segW, pad, segW, Height - pad * 2);
            TextRenderer.DrawText(g, i == 0 ? "" : "", icon, r, on ? p.Fore : hover == i ? p.Fore : p.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>중요도 별 다섯 개. 누른 별까지 채워지고(1~5), 마우스를 올리면 미리 보여 준다.</summary>
internal sealed class StarBox : Control
{
    private LibraryPalette? palette;
    private int value = 1, hover;
    private bool readOnly;

    public event Action<int>? ValueChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public int Value { get => value; set { value = Math.Clamp(value, 0, 5); Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool ReadOnly { get => readOnly; set { readOnly = value; hover = 0; Cursor = value ? Cursors.Default : Cursors.Hand; Invalidate(); } }

    public StarBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = false;
        Size = new Size(5 * StarW + 4 * Gap, LogicalToDeviceUnits(26));
    }

    private int StarW => LogicalToDeviceUnits(22);
    private int Gap => LogicalToDeviceUnits(4);
    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }

    private int IndexAt(int x) { int i = x / (StarW + Gap) + 1; return x < 0 || i > 5 ? 0 : i; }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (readOnly) return; int i = IndexAt(e.X); if (i != hover) { hover = i; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = 0; Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (readOnly || e.Button != MouseButtons.Left) return;
        int i = IndexAt(e.X);
        if (i == 0 || i == value) return;
        value = i;
        Invalidate();
        ValueChanged?.Invoke(i);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (palette is not { } p) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int shown = hover > 0 && !readOnly ? hover : value;
        for (int i = 1; i <= 5; i++)
        {
            float cx = (i - 1) * (StarW + Gap) + StarW / 2f, cy = Height / 2f, r = StarW / 2f - 0.5f;
            var pts = new PointF[10];
            for (int k = 0; k < 10; k++)
            {
                double a = -Math.PI / 2 + k * Math.PI / 5;
                float rr = k % 2 == 0 ? r : r * 0.45f;
                pts[k] = new PointF(cx + (float)(Math.Cos(a) * rr), cy + (float)(Math.Sin(a) * rr) + r * 0.06f);
            }
            bool on = i <= shown;
            using var b = new SolidBrush(on ? (hover > 0 && !readOnly && i > value ? Themes.Blend(p.Main, p.Accent, 0.6f) : p.Accent) : Themes.Blend(p.Main, p.Fore, 0.12f));
            using var pen = new Pen(b.Color, 1.5f) { LineJoin = LineJoin.Round };
            g.FillPolygon(b, pts);
            g.DrawPolygon(pen, pts);
        }
    }
}

/// <summary>
/// 분류 칩 줄: 고른 분류들(오른쪽 ×로 뺀다)과 줄 끝의 [분류 추가 +]. 추가를 누르면 AddRequested(화면 좌표의 버튼 자리)가 나서
/// 받는 쪽이 고르기 팝업을 띄운다. 칩을 우클릭해도 뺄 수 있다.
/// </summary>
internal sealed class ClassStrip : Control
{
    private LibraryPalette? palette;
    private List<string> items = new();
    private readonly List<(Rectangle Rect, string Name)> chips = new();
    private Rectangle addRect;
    private bool hoverAdd, readOnly;
    private int hoverX = -1;

    public event Action<string>? RemoveRequested;
    public event Action<Rectangle>? AddRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool ReadOnly { get => readOnly; set { readOnly = value; Invalidate(); } }
    public int Count => items.Count;

    public ClassStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font(NovelStore.UiFontFamily, 9.5f);
        TabStop = false;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int ChipH => Font.Height + S(10);
    public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
    public void SetItems(List<string> list) { items = list.ToList(); Invalidate(); }

    /// <summary>이 폭에서 칩들이 차지할 높이(보기 모드에서 비어 있으면 0).</summary>
    public int FitHeight(int width)
    {
        chips.Clear();
        addRect = Rectangle.Empty;
        if (readOnly && items.Count == 0) return 0;
        int x = 0, y = 0, h = ChipH, gap = S(6);
        foreach (var t in items)
        {
            int cw = TextRenderer.MeasureText(t, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + S(24) + (readOnly ? 0 : S(18));
            cw = Math.Min(cw, Math.Max(S(50), width));
            if (x > 0 && x + cw > width) { x = 0; y += h + gap; }
            chips.Add((new Rectangle(x, y, cw, h), t));
            x += cw + gap;
        }
        if (!readOnly)
        {
            int aw = TextRenderer.MeasureText("분류 추가", Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + S(46);
            if (x > 0 && x + aw > width) { x = 0; y += h + gap; }
            addRect = new Rectangle(x, y, aw, h);
        }
        return y + h;
    }

    private Rectangle XRect(Rectangle chip) => new(chip.Right - S(22), chip.Y, S(22), chip.Height);
    private int XIndexAt(Point pt) { if (readOnly) return -1; for (int i = 0; i < chips.Count; i++) if (XRect(chips[i].Rect).Contains(pt)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = XIndexAt(e.Location);
        bool ha = !readOnly && addRect.Contains(e.Location);
        if (i != hoverX || ha != hoverAdd) { hoverX = i; hoverAdd = ha; Invalidate(); }
        Cursor = i >= 0 || ha ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverX = -1; hoverAdd = false; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (readOnly) return;
        if (e.Button == MouseButtons.Left)
        {
            int xi = XIndexAt(e.Location);
            if (xi >= 0) { hoverX = -1; RemoveRequested?.Invoke(chips[xi].Name); }
            else if (addRect.Contains(e.Location)) AddRequested?.Invoke(RectangleToScreen(addRect));
        }
        else if (e.Button == MouseButtons.Right)
        {
            foreach (var (rect, name) in chips)
                if (rect.Contains(e.Location))
                {
                    var m = new ContextMenuStrip();
                    MenuGuard.Attach(m);
                    m.Items.Add($"'{name}' 분류 빼기", null, (_, _) => RemoveRequested?.Invoke(name));
                    m.Show(this, e.Location);
                    return;
                }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (palette is not { } p) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = DeviceDpi / 96f;
        for (int i = 0; i < chips.Count; i++)
        {
            var (rect, name) = chips[i];
            using (var path = LibraryPalette.Rounded(new RectangleF(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), S(6)))
            using (var b = new SolidBrush(p.AccentSoft)) g.FillPath(b, path);
            var textRect = new Rectangle(rect.X + S(10), rect.Y, rect.Width - S(20) - (readOnly ? 0 : S(18)), rect.Height);
            TextRenderer.DrawText(g, name, Font, textRect, p.Fore, WikiUi.Flags | TextFormatFlags.Left);
            if (readOnly) continue;
            var xr = XRect(rect);
            float cx = xr.X + xr.Width / 2f, cy = xr.Y + xr.Height / 2f, d = 2.8f * u;
            if (hoverX == i) using (var hb = new SolidBrush(Themes.Blend(p.AccentSoft, p.Fore, 0.15f))) g.FillEllipse(hb, cx - 7 * u, cy - 7 * u, 14 * u, 14 * u);
            using var xp = new Pen(hoverX == i ? p.Fore : p.Dim, Math.Max(1.2f, 1.2f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(xp, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(xp, cx - d, cy + d, cx + d, cy - d);
        }
        if (addRect.Width <= 0) return;
        using (var path = LibraryPalette.Rounded(new RectangleF(addRect.X, addRect.Y, addRect.Width - 1, addRect.Height - 1), S(6)))
        using (var b = new SolidBrush(hoverAdd ? p.AccentSoft : Themes.Blend(p.Main, p.Accent, p.Dark ? 0.14f : 0.1f))) g.FillPath(b, path);
        TextRenderer.DrawText(g, "분류 추가", Font, new Rectangle(addRect.X + S(10), addRect.Y, addRect.Width - S(34), addRect.Height), hoverAdd ? p.Fore : p.Dim, WikiUi.Flags | TextFormatFlags.Left);
        float px = addRect.Right - S(15), py = addRect.Y + addRect.Height / 2f, pd = 4f * u;
        using var pp = new Pen(hoverAdd ? p.Fore : p.Dim, Math.Max(1.3f, 1.3f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pp, px - pd, py, px + pd, py);
        g.DrawLine(pp, px, py - pd, px, py + pd);
    }
}

/// <summary>
/// 고르기 팝업(분류 · 속성 항목): 위에 검색칸, 밑에 고를 수 있는 목록, 목록 끝에 [+ ○○ 추가].
/// 검색칸에 적은 글로 목록을 거르고, 맞는 게 없으면 그 글로 바로 새로 만들 수 있다. 목록이 비어 있으면 "선택할 ○○가 없어요".
/// 줄을 누르거나 Enter면 Picked, 새로 만들면 Created, 줄을 우클릭해 지우면(canDelete일 때) DeleteRequested. 밖을 누르거나 Esc면 닫힌다.
/// </summary>
internal sealed class PickPopup : Form
{
    /// <summary>마지막으로 팝업이 닫힌 때. 팝업을 연 버튼을 눌러 닫은 직후에 같은 버튼 클릭이 다시 열지 않게 쓴다.</summary>
    public static DateTime LastClosedUtc = DateTime.MinValue;
    public static bool JustClosed => (DateTime.UtcNow - LastClosedUtc).TotalMilliseconds < 250;

    private readonly LibraryPalette p;
    private readonly List<string> all;
    private readonly string noun, emptyText, noMatchText, searchHint, createHint;
    private readonly bool canDelete;
    private readonly TextBox search;
    private readonly List<string> shown = new();
    private int top, hover = -1, sel = -1;
    private bool creating;
    private const int MaxRows = 6;

    public event Action<string>? Picked;
    public event Action<string>? Created;
    public event Action<string>? DeleteRequested;

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int SearchH => S(40);
    private int RowH => S(34);

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } } // 그림자
    protected override bool ShowWithoutActivation => false;

    /// <param name="noun">"분류" · "항목" — 추가 단추 글자에 쓴다.</param>
    public PickPopup(LibraryPalette palette, IEnumerable<string> choices, string noun, string emptyText, string noMatchText, bool canDelete = false)
    {
        p = palette;
        all = choices.ToList();
        this.noun = noun; this.emptyText = emptyText; this.noMatchText = noMatchText; this.canDelete = canDelete;
        searchHint = noun + " 검색...";
        createHint = $"새 {noun} 이름";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        KeyPreview = true;
        DoubleBuffered = true;
        Font = new Font(NovelStore.UiFontFamily, 10f);
        BackColor = p.Main;
        search = new TextBox { BorderStyle = BorderStyle.None, Font = Font, PlaceholderText = searchHint, BackColor = WikiUi.Surface(p), ForeColor = p.Fore };
        Controls.Add(search);
        search.TextChanged += (_, _) => { if (!creating) { top = 0; sel = -1; Refilter(); } };
        search.KeyDown += (_, e) => OnSearchKey(e);
        Deactivate += (_, _) => Close();
        Shown += (_, _) => Refilter(); // 화면 배율이 정해진 뒤 크기를 다시 잡는다
        FormClosed += (_, _) => LastClosedUtc = DateTime.UtcNow;
        MouseWheel += (_, e) => { if (!creating && shown.Count > MaxRows) { top = Math.Clamp(top - Math.Sign(e.Delta), 0, shown.Count - MaxRows); Invalidate(); } };
        Refilter();
    }

    /// <summary>버튼(화면 좌표) 바로 밑에 띄운다. 화면 아래가 모자라면 위로.</summary>
    public void ShowBelow(Rectangle anchorScreen, IWin32Window? owner, int minWidth)
    {
        Width = Math.Max(minWidth, S(280));
        Refilter();
        var wa = Screen.FromRectangle(anchorScreen).WorkingArea;
        int x = Math.Clamp(anchorScreen.Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        int y = anchorScreen.Bottom + S(4);
        if (y + Height > wa.Bottom) y = Math.Max(wa.Top, anchorScreen.Top - Height - S(4));
        Location = new Point(x, y);
        Show(owner);
        search.Focus();
    }

    private IEnumerable<string> Filtered()
    {
        string q = search.Text.Trim();
        return q.Length == 0 ? all : all.Where(c => c.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private bool QueryIsNew()
    {
        string q = search.Text.Trim();
        return q.Length > 0 && !all.Any(c => c.Equals(q, StringComparison.OrdinalIgnoreCase));
    }

    private void Refilter()
    {
        shown.Clear();
        if (!creating) shown.AddRange(Filtered());
        int rows = creating ? 0 : Math.Max(1, Math.Min(shown.Count, MaxRows));
        int listH = creating ? 0 : rows * RowH + RowH + S(8); // 줄들 + 추가 줄 + 여백
        int h = SearchH + (creating ? 0 : S(6) + listH) + 2;
        if (Height != h || ClientSize.Width != Width) ClientSize = new Size(Width, h);
        LayoutSearch();
        Invalidate();
    }

    private void LayoutSearch()
    {
        int th = search.PreferredHeight;
        search.SetBounds(S(16), 1 + (SearchH - th) / 2, Width - S(16) - S(44), th);
    }

    private Rectangle SearchRect => new(1, 1, Width - 2, SearchH - 1);
    private Rectangle RowRect(int i) => new(S(6), SearchH + S(6) + S(4) + i * RowH, Width - S(12), RowH);
    private int VisibleRows => Math.Max(1, Math.Min(shown.Count, MaxRows));
    private Rectangle AddRect => RowRect(VisibleRows);

    private int RowAt(Point pt)
    {
        if (creating) return -1;
        for (int i = 0; i < Math.Min(shown.Count - top, MaxRows); i++) if (RowRect(i).Contains(pt)) return top + i;
        return -1;
    }

    private void Pick(string name) { Close(); Picked?.Invoke(name); } // 먼저 닫고 알린다(받는 쪽이 대화상자를 띄워도 팝업이 끼어들지 않게)
    private void Create(string name) { name = name.Trim(); if (name.Length == 0) return; Close(); Created?.Invoke(name); }

    private void OnSearchKey(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; if (creating) { creating = false; search.Clear(); search.PlaceholderText = searchHint; Refilter(); } else Close(); return; }
        if (creating) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Create(search.Text); } return; }
        if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
        {
            e.SuppressKeyPress = true;
            if (shown.Count == 0) return;
            sel = Math.Clamp(sel + (e.KeyCode == Keys.Down ? 1 : -1), 0, shown.Count - 1);
            if (sel < top) top = sel; else if (sel >= top + MaxRows) top = sel - MaxRows + 1;
            Invalidate();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            string q = search.Text.Trim();
            if (sel >= 0 && sel < shown.Count) Pick(shown[sel]);
            else if (shown.FirstOrDefault(c => c.Equals(q, StringComparison.OrdinalIgnoreCase)) is { } exact) Pick(exact);
            else if (shown.Count == 1) Pick(shown[0]);
            else if (QueryIsNew()) Create(q);
        }
    }

    private void StartCreating()
    {
        creating = true;
        search.Clear();
        search.PlaceholderText = createHint;
        Refilter();
        search.Focus();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = RowAt(e.Location);
        if (!creating && AddRect.Contains(e.Location)) i = -2;
        if (i != hover) { hover = i; Invalidate(); }
        Cursor = i != -1 ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (creating) return;
        int i = RowAt(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            if (i >= 0) Pick(shown[i]);
            else if (AddRect.Contains(e.Location)) { if (QueryIsNew()) Create(search.Text); else StartCreating(); }
        }
        else if (e.Button == MouseButtons.Right && canDelete && i >= 0)
        {
            string name = shown[i];
            var m = new ContextMenuStrip();
            MenuGuard.Attach(m);
            m.Items.Add($"'{name}' {noun} 지우기", null, (_, _) => { Close(); DeleteRequested?.Invoke(name); });
            m.Show(this, e.Location);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = DeviceDpi / 96f;
        var surface = WikiUi.Surface(p);
        using (var b = new SolidBrush(surface)) g.FillRectangle(b, SearchRect);
        using (var pen = new Pen(p.Line)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        // 검색칸 오른쪽 ⌄
        float cx = Width - S(24), cy = SearchH / 2f + 1, d = 4f * u;
        using (var cp = new Pen(p.Fore, Math.Max(1.6f, 1.6f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(cp, new[] { new PointF(cx - d, cy - d / 2), new PointF(cx, cy + d / 2), new PointF(cx + d, cy - d / 2) });
        if (creating) return;
        using (var sep = new Pen(p.Line)) g.DrawLine(sep, 1, SearchH + 1, Width - 2, SearchH + 1);
        if (shown.Count == 0)
            TextRenderer.DrawText(g, all.Count == 0 ? emptyText : noMatchText, Font, RowRect(0), p.Dim, WikiUi.Flags | TextFormatFlags.Left);
        for (int i = 0; i < Math.Min(shown.Count - top, MaxRows); i++)
        {
            int idx = top + i;
            var r = RowRect(i);
            if (idx == hover || idx == sel)
                using (var hp = LibraryPalette.Rounded(r, S(6))) using (var hb = new SolidBrush(p.AccentSoft)) g.FillPath(hb, hp);
            TextRenderer.DrawText(g, shown[idx], Font, new Rectangle(r.X + S(10), r.Y, r.Width - S(20), r.Height), p.Fore, WikiUi.Flags | TextFormatFlags.Left);
        }
        // 목록 끝의 추가 줄
        var ar = AddRect;
        if (hover == -2) using (var hp = LibraryPalette.Rounded(ar, S(6))) using (var hb = new SolidBrush(p.AccentSoft)) g.FillPath(hb, hp);
        string q = search.Text.Trim();
        string label = QueryIsNew() ? $"+  '{q}' {noun} 추가" : $"+  {noun} 추가";
        using var bf = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(g, label, bf, new Rectangle(ar.X + S(10), ar.Y, ar.Width - S(20), ar.Height), p.Accent, WikiUi.Flags | TextFormatFlags.Left);
    }
}
