using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 특수문자 위젯(사이드바의 [특수문자] 아이콘으로 켜고 끈다): 머리 줄 [A 특수문자 · + · ^]와 그 밑의 특수문자 칸들.
/// 칸을 누르면 글 쓰던 자리에 그 문자가 들어간다. [+]는 특수문자 · 설명을 받아 칸을 더하고, [^]는 머리 줄만 남은 작은 창으로 접는다(다시 누르면 펼친다).
/// 배경(머리 줄)을 끌어 옮길 수 있고, 직접 더한 칸은 우클릭으로 지운다.
/// </summary>
internal sealed class SpecialCharWidget : Control
{
    /// <summary>처음부터 들어 있는 특수문자(문자, 설명).</summary>
    public static readonly (string Ch, string Desc)[] Defaults =
    {
        ("—", "줄표(긴 선)"), ("·", "가운뎃점"), ("〈", "홑화살괄호 열기"), ("〉", "홑화살괄호 닫기"), ("《", "겹화살괄호 열기"), ("》", "겹화살괄호 닫기"), ("「", "홑낫표 열기"),
        ("」", "홑낫표 닫기"), ("『", "겹낫표 열기"), ("』", "겹낫표 닫기"), ("…", "줄임표"), ("?", "물음표"), ("¿", "거꾸로 물음표"), ("¡", "거꾸로 느낌표"),
        ("└", "꺾인 선"), ("†", "단검표"), ("“", "큰따옴표 열기"), ("”", "큰따옴표 닫기"), ("‘", "작은따옴표 열기"), ("’", "작은따옴표 닫기"), ("☆", "빈 별"),
        ("★", "찬 별"), ("♡", "빈 하트"), ("♥", "찬 하트"),
    };

    private sealed record Cell(string Ch, string Desc, bool Custom);
    private List<Cell> cells = new();
    private readonly List<(Rectangle R, int Index)> hits = new();
    private Rectangle plusRect, foldRect;
    private int hot = -1, hotBtn = -1; // hot: 칸 번호 · hotBtn: 0 + · 1 ^
    private bool expanded = true, dragging;
    private Point dragFrom;
    private Color back = Color.White, fore = Color.Black, accent = Color.SteelBlue;
    private readonly ToolTip tip = new();

    public event Action<string>? CharChosen;
    public event Action? AddRequested;
    /// <summary>직접 더한 칸을 지우자고 했다(문자).</summary>
    public event Action<string>? RemoveRequested;
    public event Action? Moved;
    public event Action? ExpandedChanged;

    private const int Cols = 7;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Expanded { get => expanded; set { if (expanded == value) return; expanded = value; Invalidate(); ExpandedChanged?.Invoke(); } }

    public SpecialCharWidget()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false; // 눌러도 글 상자에서 커서를 가져오지 않는다(그래야 눌렀을 때 쓰던 자리에 문자가 들어간다)
        Font = new Font(NovelStore.UiFontFamily, 10f);
        Disposed += (_, _) => tip.Dispose();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int HeaderH => S(54);
    /// <summary>칸 크기 단계: 0 작게 · 1 중간 · 2 크게.</summary>
    private int sizeLevel = 1;
    private int CellS => S(sizeLevel switch { 0 => 34, 2 => 60, _ => 46 });
    private float CellFont => sizeLevel switch { 0 => 11f, 2 => 18f, _ => 14f };
    private int Gap => S(sizeLevel == 0 ? 4 : 6);
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SizeLevel { get => sizeLevel; set { value = Math.Clamp(value, 0, 2); if (sizeLevel == value) return; sizeLevel = value; Invalidate(); } }

    public void SetItems(IEnumerable<(string Ch, string Desc, bool Custom)> list)
    {
        cells = list.Select(x => new Cell(x.Ch, x.Desc, x.Custom)).ToList();
        Invalidate();
    }

    public void SetColors(Color b, Color f, Color a) { if (b == back && f == fore && a == accent) return; back = b; fore = f; accent = a; Invalidate(); }

    /// <summary>지금 모양(펼침 · 접힘)에서 위젯이 차지할 크기.</summary>
    public Size DesiredSize()
    {
        int w = S(16) * 2 + Cols * CellS + (Cols - 1) * Gap;
        if (!expanded || cells.Count == 0) return new Size(w, HeaderH + (expanded && cells.Count == 0 ? S(12) : 0));
        int rows = (cells.Count + Cols - 1) / Cols;
        return new Size(w, HeaderH + S(6) + rows * CellS + (rows - 1) * Gap + S(18));
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

    private void Layout()
    {
        hits.Clear();
        int bs = S(28);
        foldRect = new Rectangle(Width - S(16) - bs, (HeaderH - bs) / 2, bs, bs);
        plusRect = new Rectangle(foldRect.Left - S(8) - bs, foldRect.Top, bs, bs);
        if (!expanded) return;
        int x0 = S(16), y0 = HeaderH + S(6);
        for (int i = 0; i < cells.Count; i++)
            hits.Add((new Rectangle(x0 + (i % Cols) * (CellS + Gap), y0 + (i / Cols) * (CellS + Gap), CellS, CellS), i));
    }

    // ───────── 입력 ─────────

    private int CellAt(Point pt) { foreach (var (r, i) in hits) if (r.Contains(pt)) return i; return -1; }

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
        int c = CellAt(e.Location), b = plusRect.Contains(e.Location) ? 0 : foldRect.Contains(e.Location) ? 1 : -1;
        if (c != hot || b != hotBtn)
        {
            hot = c; hotBtn = b; Invalidate();
            tip.SetToolTip(this, c >= 0 ? $"{cells[c].Ch}  {cells[c].Desc}" : b == 0 ? "특수문자 추가" : b == 1 ? (expanded ? "작게 접기" : "펼치기") : "");
        }
        Cursor = c >= 0 || b >= 0 ? Cursors.Hand : Cursors.SizeAll;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = hotBtn = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Layout();
        if (e.Button == MouseButtons.Left && CellAt(e.Location) < 0 && !plusRect.Contains(e.Location) && !foldRect.Contains(e.Location)) { dragging = true; dragFrom = e.Location; Capture = true; }
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
        int c = CellAt(e.Location);
        if (e.Button == MouseButtons.Left)
        {
            if (c >= 0) CharChosen?.Invoke(cells[c].Ch);
            else if (plusRect.Contains(e.Location)) AddRequested?.Invoke();
            else if (foldRect.Contains(e.Location)) Expanded = !expanded;
        }
        else if (e.Button == MouseButtons.Right && c >= 0 && cells[c].Custom)
        {
            string ch = cells[c].Ch;
            var m = new ContextMenuStrip();
            MenuGuard.Attach(m);
            m.Items.Add($"'{ch}' 지우기", null, (_, _) => BeginInvoke(() => RemoveRequested?.Invoke(ch)));
            m.Closed += (_, _) => BeginInvoke(m.Dispose);
            m.Show(Cursor.Position);
        }
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

        using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(14)))
        {
            using var b = new SolidBrush(card); g.FillPath(b, path);
            using var pen = new Pen(line); g.DrawPath(pen, path);
        }

        // 머리 줄: [A] 특수문자 ······ [+] [^]
        int ib = S(24);
        var ir = new Rectangle(S(16), (HeaderH - ib) / 2, ib, ib);
        using (var path = Rounded(ir, S(5))) using (var b = new SolidBrush(Themes.Blend(card, fore, 0.62f))) g.FillPath(b, path);
        using (var af = new Font(Font.FontFamily, 10.5f, FontStyle.Bold)) TextRenderer.DrawText(g, "A", af, ir, card, C);
        using (var tf = new Font(Font.FontFamily, 11.5f)) TextRenderer.DrawText(g, "특수문자", tf, new Rectangle(ir.Right + S(12), 0, Width / 2, HeaderH), dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        float u = DeviceDpi / 96f;
        using (var pen = new Pen(hotBtn == 0 ? fore : dim, Math.Max(1.6f, 1.8f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = plusRect.X + plusRect.Width / 2f, cy = plusRect.Y + plusRect.Height / 2f, d = 6f * u;
            g.DrawLine(pen, cx - d, cy, cx + d, cy); g.DrawLine(pen, cx, cy - d, cx, cy + d);
        }
        using (var pen = new Pen(hotBtn == 1 ? fore : dim, Math.Max(1.6f, 1.8f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float cx = foldRect.X + foldRect.Width / 2f, cy = foldRect.Y + foldRect.Height / 2f, d = 5f * u;
            // 펼쳐 있으면 ^ (눌러서 접는다), 접혀 있으면 v (눌러서 편다)
            if (expanded) g.DrawLines(pen, new[] { new PointF(cx - d, cy + d / 2), new PointF(cx, cy - d / 2), new PointF(cx + d, cy + d / 2) });
            else g.DrawLines(pen, new[] { new PointF(cx - d, cy - d / 2), new PointF(cx, cy + d / 2), new PointF(cx + d, cy - d / 2) });
        }
        if (!expanded) return;
        if (cells.Count == 0)
        {
            TextRenderer.DrawText(g, "[+]로 특수문자를 더해 보세요", Font, new Rectangle(0, HeaderH - S(6), Width, S(26)), dim, C);
            return;
        }

        // 칸들
        using var cf = new Font(Font.FontFamily, CellFont);
        foreach (var (r, i) in hits)
        {
            using (var path = Rounded(r, S(8)))
            {
                using (var b = new SolidBrush(i == hot ? Themes.Blend(card, accent, 0.14f) : card)) g.FillPath(b, path);
                using var pen = new Pen(i == hot ? accent : line); g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, cells[i].Ch, cf, r, fore, C);
        }
    }
}

/// <summary>"특수문자 추가" 창: 특수문자 · 설명을 받아 [추가]로 더한다. 오른쪽 위 [×](또는 Esc)를 누르면 아무것도 더하지 않고 닫힌다.</summary>
internal sealed class SpecialCharDialog : Form
{
    private readonly TextBox chBox, descBox;
    private readonly Button add;
    private Rectangle closeRect;
    private bool hoverClose;

    public string Chars => chBox.Text.Trim();
    public string Desc => descBox.Text.Trim();

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public SpecialCharDialog()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.White;
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
        ClientSize = new Size(S(480), S(300)); // 이 창은 직접 크기를 정하니 화면 배율에 맞춰 직접 키운다
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; } };

        Label L(string s, int y) { var l = new Label { Text = s, AutoSize = true, Location = new Point(S(24), S(y)), Font = new Font(Font, FontStyle.Bold), BackColor = Color.White }; Controls.Add(l); return l; }
        L("특수문자", 76);
        chBox = new TextBox { Location = new Point(S(24), S(102)), Size = new Size(S(432), S(30)), PlaceholderText = "특수문자를 입력하세요", MaxLength = 8 };
        L("설명", 148);
        descBox = new TextBox { Location = new Point(S(24), S(174)), Size = new Size(S(432), S(30)), PlaceholderText = "특수문자에 대한 설명을 입력하세요", MaxLength = 40 };
        add = new Button { Text = "추가", Location = new Point(S(364), S(236)), Size = new Size(S(92), S(40)), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, Font = new Font(Font, FontStyle.Bold), Cursor = Cursors.Hand };
        add.FlatAppearance.BorderSize = 0;
        Controls.AddRange(new Control[] { chBox, descBox, add });
        AcceptButton = add;
        add.Click += (_, _) => { if (Chars.Length > 0) DialogResult = DialogResult.OK; };
        chBox.TextChanged += (_, _) => SyncAdd();
        SyncAdd();
        Shown += (_, _) => { Activate(); chBox.Focus(); };
        Load += (_, _) =>
        {
            using var path = new GraphicsPath();
            int d = S(24);
            path.AddArc(0, 0, d, d, 180, 90); path.AddArc(Width - d, 0, d, d, 270, 90); path.AddArc(Width - d, Height - d, d, d, 0, 90); path.AddArc(0, Height - d, d, d, 90, 90);
            path.CloseFigure();
            Region = new Region(path);
        };
    }

    /// <summary>문자를 넣어야 [추가]가 진해진다(비었을 땐 회색).</summary>
    private void SyncAdd()
    {
        bool ok = Chars.Length > 0;
        add.BackColor = ok ? Color.FromArgb(0x1B, 0x1B, 0x1D) : Color.FromArgb(0x92, 0x92, 0x94);
        add.Enabled = true; // 눌러도 비었으면 아무 일 없게 두고 색만 바꾼다
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = closeRect.Contains(e.Location);
        if (h != hoverClose) { hoverClose = h; Invalidate(); }
        Cursor = h ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && closeRect.Contains(e.Location)) DialogResult = DialogResult.Cancel; }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var tf = new Font(Font.FontFamily, 12f, FontStyle.Bold)) TextRenderer.DrawText(g, "특수문자 추가", tf, new Rectangle(S(24), S(20), S(300), S(32)), Color.FromArgb(0x1B, 0x1B, 0x1D), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        closeRect = new Rectangle(Width - S(24) - S(28), S(20), S(28), S(28));
        float cx = closeRect.X + closeRect.Width / 2f, cy = closeRect.Y + closeRect.Height / 2f, d = 6f * DeviceDpi / 96f;
        using var pen = new Pen(hoverClose ? Color.Black : Color.FromArgb(0x30, 0x30, 0x32), Math.Max(1.8f, 2f * DeviceDpi / 96f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d); g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
    }
}
