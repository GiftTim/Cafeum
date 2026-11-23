using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 속성 항목 고르기 팝업. 처음엔 "새 속성 추가하기 ⌄" 입력칸만 보이고, ⌄를 누르거나 글을 적으면 목록이 펼쳐진다(밑이 모자라면 위로).
/// 목록은 즐겨찾기(★)한 항목이 먼저, 그 밑에 나머지. 줄 오른쪽 ★을 누르면 즐겨찾기를 켜고 끈다. 줄을 누르면 Picked, 새 이름이면 Created.
/// </summary>
internal sealed class AttrPopup : Form
{
    private readonly LibraryPalette p;
    private readonly List<string> all;
    private readonly HashSet<string> favs;
    private readonly Func<string, string> glyphOf;
    private readonly TextBox field;
    private readonly List<string> shown = new();
    private int top, hover = -1, sel = -1, fieldScreenTop;
    private bool open, above;
    private const int MaxRows = 8;

    public event Action<string>? Picked;
    public event Action<string>? Created;
    public event Action<string, bool>? FavoriteToggled;

    private const string Hint = "새 속성 추가하기";
    private readonly Label hintLabel;
    /// <summary>적은 이름.</summary>
    private string Query => field.Text.Trim();
    private void ShowHintColor() { hintLabel.Visible = field.Text.Length == 0; }
    private int anchorLeft;

    /// <summary>스크롤 등으로 "+ 속성 추가" 줄이 움직였을 때 새 위치(화면 좌표)를 따라간다.</summary>
    public void MoveAnchor(Rectangle anchor)
    {
        anchorLeft = anchor.Left;
        fieldScreenTop = anchor.Bottom + S(8);
        if (!IsHandleCreated) return;
        var wa = Screen.FromRectangle(anchor).WorkingArea;
        Left = Math.Clamp(anchorLeft, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        if (open) SetOpen(true); else ApplyShape();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int FieldH => S(46);
    private int RowH => S(40);
    private int Gap => S(6);

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }

    public AttrPopup(LibraryPalette palette, IEnumerable<string> choices, HashSet<string> favorites, Func<string, string> glyph)
    {
        p = palette; all = choices.ToList(); favs = new HashSet<string>(favorites, StringComparer.OrdinalIgnoreCase); glyphOf = glyph;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        DoubleBuffered = true;
        Font = new Font(NovelStore.UiFontFamily, 11f);
        BackColor = p.Main;
        field = new TextBox { BorderStyle = BorderStyle.None, Font = Font, BackColor = WikiUi.Surface(p), ForeColor = p.Fore };
        // 안내 글은 입력칸 위에 겹쳐 둔 라벨: 포커스가 있어도 보이고, 글을 치면 사라진다(눌러도 입력칸으로 간다)
        hintLabel = new Label { Text = Hint, AutoSize = false, Font = Font, BackColor = WikiUi.Surface(p), ForeColor = p.Dim, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
        hintLabel.Click += (_, _) => { field.Focus(); if (!open) SetOpen(true); };
        field.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left && !open) SetOpen(true); }; // 칸을 누르면 항목 목록이 펼쳐진다
        Controls.Add(field);
        Controls.Add(hintLabel);
        hintLabel.BringToFront();
        field.TextChanged += (_, _) => { top = 0; sel = -1; Refilter(); if (!open && Query.Length > 0) SetOpen(true); ShowHintColor(); };
        field.KeyDown += (_, e) => OnFieldKey(e);
        field.MouseWheel += (_, e) => Wheel(e.Delta);
        MouseWheel += (_, e) => Wheel(e.Delta);
        Deactivate += (_, _) => Close();
        FormClosed += (_, _) => PickPopup.LastClosedUtc = DateTime.UtcNow;
    }

    /// <summary>입력칸을 anchor(화면 좌표) 자리에 띄운다.</summary>
    public void ShowAt(Rectangle anchor, IWin32Window? owner, int minWidth)
    {
        Width = Math.Max(minWidth, S(280));
        fieldScreenTop = anchor.Bottom + S(8); // "+ 속성 추가" 줄(속성 상자) 바로 밑
        var wa = Screen.FromRectangle(anchor).WorkingArea;
        int x = Math.Clamp(anchor.Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        Location = new Point(x, fieldScreenTop);
        Refilter();
        ApplyShape();
        Show(owner);
        field.Focus();
        ShowHintColor();
    }

    private bool QueryIsNew() { string q = Query; return q.Length > 0 && !all.Any(c => c.Equals(q, StringComparison.OrdinalIgnoreCase)); }
    private int RowCount => shown.Count + (QueryIsNew() ? 1 : 0);
    private int VisibleRows => Math.Max(1, Math.Min(RowCount, MaxRows));
    private int ListH => VisibleRows * RowH + S(12);

    private void Refilter()
    {
        string q = Query;
        shown.Clear();
        var src = q.Length == 0 ? all : all.Where(c => c.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        shown.AddRange(src.Where(favs.Contains).OrderBy(n => n, NaturalStringComparer.Instance));
        shown.AddRange(src.Where(n => !favs.Contains(n)).OrderBy(n => n, NaturalStringComparer.Instance));
        top = Math.Clamp(top, 0, Math.Max(0, RowCount - MaxRows));
        if (open) ApplyShape();
        Invalidate();
    }

    private void SetOpen(bool value)
    {
        open = value;
        if (open)
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int need = ListH + Gap;
            above = wa.Bottom - (fieldScreenTop + FieldH) < need && fieldScreenTop - wa.Top >= need;
        }
        ApplyShape();
        Invalidate();
    }

    private Rectangle FieldRect => new(0, open && above ? ListH + Gap : 0, Width, FieldH);
    private Rectangle ListRect => above ? new Rectangle(0, 0, Width, ListH) : new Rectangle(0, FieldH + Gap, Width, ListH);

    private void ApplyShape()
    {
        int h = open ? FieldH + Gap + ListH : FieldH;
        ClientSize = new Size(Width, h);
        Top = open && above ? fieldScreenTop - ListH - Gap : fieldScreenTop;
        var fr = FieldRect;
        int th = field.PreferredHeight;
        field.SetBounds(S(16), fr.Y + (fr.Height - th) / 2, Width - S(16) - S(48), th);
        hintLabel.SetBounds(field.Left + S(6), field.Top, field.Width - S(6), field.Height); // 맨 앞 몇 px은 비워 입력칸의 커서가 보이게
        // 닫혀 있을 땐 둥근 입력칸 모양으로만 보이게(모서리 밖이 비치지 않게)
        // 창 모양을 둥근 입력칸 + 둥근 목록(둘 사이 틈은 투명)으로 잘라 모서리 · 그림자가 그 모양을 따르게 한다
        using var rf = LibraryPalette.Rounded(new RectangleF(fr.X, fr.Y, fr.Width, fr.Height), S(14));
        var region = new Region(rf);
        if (open) { var lr = ListRect; using var rl = LibraryPalette.Rounded(new RectangleF(lr.X, lr.Y, lr.Width, lr.Height), S(14)); region.Union(rl); }
        Region = region;
    }

    private Rectangle ChevronRect => new(Width - S(44), FieldRect.Y, S(44), FieldH);
    private Rectangle RowRect(int i) => new(S(6), ListRect.Y + S(6) + i * RowH, Width - S(12) - S(10), RowH);
    private Rectangle StarRect(int i) { var r = RowRect(i); return new Rectangle(r.Right - S(44), r.Y, S(44), r.Height); }

    private int RowAt(Point pt)
    {
        if (!open) return -1;
        for (int i = 0; i < Math.Min(RowCount - top, MaxRows); i++) if (RowRect(i).Contains(pt)) return top + i;
        return -1;
    }

    private void Wheel(int delta)
    {
        if (!open || RowCount <= MaxRows) return;
        top = Math.Clamp(top - Math.Sign(delta), 0, RowCount - MaxRows);
        Invalidate();
    }

    private void Pick(string name) { Close(); Picked?.Invoke(name); }
    private void Create(string name) { name = name.Trim(); if (name.Length == 0) return; Close(); Created?.Invoke(name); }

    private void OnFieldKey(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; if (open) SetOpen(false); else Close(); return; }
        if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
        {
            e.SuppressKeyPress = true;
            if (!open) { SetOpen(true); return; }
            if (RowCount == 0) return;
            sel = Math.Clamp(sel + (e.KeyCode == Keys.Down ? 1 : -1), 0, RowCount - 1);
            if (sel < top) top = sel; else if (sel >= top + MaxRows) top = sel - MaxRows + 1;
            Invalidate();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            string q = Query;
            if (sel >= 0 && sel < shown.Count) Pick(shown[sel]);
            else if (sel == shown.Count && QueryIsNew()) Create(q);
            else if (shown.FirstOrDefault(c => c.Equals(q, StringComparison.OrdinalIgnoreCase)) is { } exact) Pick(exact);
            else if (shown.Count == 1) Pick(shown[0]);
            else if (QueryIsNew()) Create(q);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = RowAt(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
        Cursor = i >= 0 || ChevronRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (ChevronRect.Contains(e.Location)) { SetOpen(!open); field.Focus(); return; }
        if (FieldRect.Contains(e.Location)) { field.Focus(); if (!open) SetOpen(true); return; }
        int i = RowAt(e.Location);
        if (i < 0) return;
        if (i >= shown.Count) { Create(Query); return; }
        if (StarRect(i - top).Contains(e.Location))
        {
            string name = shown[i];
            bool now = !favs.Contains(name);
            if (now) favs.Add(name); else favs.Remove(name);
            FavoriteToggled?.Invoke(name, now);
            Refilter();
            field.Focus();
            return;
        }
        Pick(shown[i]);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = DeviceDpi / 96f;
        var surface = WikiUi.Surface(p);
        // 입력칸
        var fr = FieldRect;
        using (var path = LibraryPalette.Rounded(new RectangleF(fr.X, fr.Y, fr.Width - 1, fr.Height - 1), S(14)))
        {
            using (var b = new SolidBrush(surface)) g.FillPath(b, path);
            using var pen = new Pen(p.Line);
            g.DrawPath(pen, path);
        }
        float cx = Width - S(24), cy = fr.Y + fr.Height / 2f, d = 4f * u;
        using (var cp = new Pen(p.Fore, Math.Max(1.6f, 1.6f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            Shapes.Chevron(g, cp, cx, cy, d, d / 2, open ? Arrow.Up : Arrow.Down);
        if (!open) return;
        // 목록
        var lr = ListRect;
        using (var path = LibraryPalette.Rounded(new RectangleF(lr.X, lr.Y, lr.Width - 1, lr.Height - 1), S(14)))
        {
            using (var b = new SolidBrush(surface)) g.FillPath(b, path);
            using var pen = new Pen(p.Line);
            g.DrawPath(pen, path);
        }
        using var icon = new Font(MainForm.IconFontName(), 12f);
        using var star = new Font(MainForm.IconFontName(), 11f);
        if (RowCount == 0) TextRenderer.DrawText(g, "맞는 항목이 없어요", Font, RowRect(0), p.Dim, WikiUi.Flags | TextFormatFlags.Left);
        for (int i = 0; i < Math.Min(RowCount - top, MaxRows); i++)
        {
            int idx = top + i;
            var r = RowRect(i);
            if (idx == hover || idx == sel)
                using (var hp = LibraryPalette.Rounded(r, S(8))) using (var hb = new SolidBrush(p.AccentSoft)) g.FillPath(hb, hp);
            if (idx >= shown.Count)
            {
                using var bf = new Font(Font, FontStyle.Bold);
                TextRenderer.DrawText(g, $"+  '{Query}' 속성 추가", bf, new Rectangle(r.X + S(12), r.Y, r.Width - S(24), r.Height), p.Accent, WikiUi.Flags | TextFormatFlags.Left);
                continue;
            }
            string name = shown[idx];
            TextRenderer.DrawText(g, glyphOf(name), icon, new Rectangle(r.X + S(8), r.Y, S(28), r.Height), p.Accent, WikiUi.Flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, name, Font, new Rectangle(r.X + S(48), r.Y, r.Width - S(100), r.Height), p.Fore, WikiUi.Flags | TextFormatFlags.Left);
            bool fav = favs.Contains(name);
            if (fav || idx == hover)
                TextRenderer.DrawText(g, fav ? "" : "", star, StarRect(i), fav ? p.Accent : p.Dim, WikiUi.Flags | TextFormatFlags.HorizontalCenter);
        }
        // 스크롤 막대
        if (RowCount > MaxRows)
        {
            int trackH = MaxRows * RowH, trackY = lr.Y + S(6);
            int thumbH = Math.Max(S(24), trackH * MaxRows / RowCount);
            int thumbY = trackY + (trackH - thumbH) * top / (RowCount - MaxRows);
            using var tp = LibraryPalette.Rounded(new RectangleF(lr.Right - S(10), thumbY, S(5), thumbH), S(3));
            using var tb = new SolidBrush(Themes.Blend(surface, p.Fore, 0.2f));
            g.FillPath(tb, tp);
        }
    }
}
