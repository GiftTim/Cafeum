using System.Drawing.Drawing2D;
using System.Globalization;

namespace Cafeum;

/// <summary>
/// 시계 위젯(플립 시계): 펼치면 날짜와 [시 : 분 : 초] 숫자판이 공항 안내판(스플릿 플랩)처럼 딸깍 넘어가고, 머리의 [^]를 누르면
/// 도시 이름과 시각만 한 줄로 보이는 작은 모양으로 접힌다(다시 누르면 펼친다). 시각은 정한 도시의 시간대로 센다.
/// 머리 · 배경을 끌어 옮길 수 있다.
/// </summary>
internal sealed class ClockWidget : Control
{
    private string zoneId = "Korea Standard Time", city = "Seoul";
    private TimeZoneInfo zone = TimeZoneInfo.Local;
    private bool expanded = true, dragging;
    private Point dragFrom;
    private Rectangle foldRect;
    private bool hotFold;
    private Color back = Color.White, fore = Color.Black, accent = Color.MediumPurple;
    private readonly System.Windows.Forms.Timer tick = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer anim = new() { Interval = 16 };

    // 숫자판 셋(시 · 분 · 초): 지금 값 · 넘어가기 전 값 · 넘기기 시작한 때
    private readonly string[] cur = { "--", "--", "--" }, prev = { "--", "--", "--" };
    private readonly long[] flipAt = new long[3];
    private string dateText = "";
    private const int FlipMs = 280;

    public event Action? Moved;
    public event Action? ExpandedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Expanded { get => expanded; set { if (expanded == value) return; expanded = value; Invalidate(); ExpandedChanged?.Invoke(); } }

    public ClockWidget()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Font = new Font(NovelStore.UiFontFamily, 10f);
        tick.Tick += (_, _) => Update_();
        anim.Tick += (_, _) => { Invalidate(); if (!Animating()) anim.Stop(); };
        Disposed += (_, _) => { tick.Dispose(); anim.Dispose(); };
        SetZone(zoneId, city);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    /// <summary>시계 위치(시간대 아이디, 보여 줄 도시 이름)를 바꾼다. 못 찾는 시간대면 이 컴퓨터의 시간대.</summary>
    public void SetZone(string id, string cityName)
    {
        zoneId = id; city = string.IsNullOrWhiteSpace(cityName) ? id : cityName;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Local; }
        for (int i = 0; i < 3; i++) { cur[i] = prev[i] = "--"; flipAt[i] = 0; }
        Update_(animate: false);
        Invalidate();
    }

    public void SetColors(Color b, Color f, Color a) { if (b == back && f == fore && a == accent) return; back = b; fore = f; accent = a; Invalidate(); }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { Update_(animate: false); tick.Start(); } else { tick.Stop(); anim.Stop(); }
    }

    /// <summary>지금 시각을 읽어 바뀐 숫자판을 넘긴다.</summary>
    private void Update_(bool animate = true)
    {
        var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, zone);
        string[] next = { now.ToString("HH"), now.ToString("mm"), now.ToString("ss") };
        bool any = false;
        for (int i = 0; i < 3; i++)
        {
            if (cur[i] == next[i]) continue;
            prev[i] = cur[i] == "--" || !animate ? next[i] : cur[i];
            cur[i] = next[i];
            flipAt[i] = animate && prev[i] != cur[i] ? Environment.TickCount64 : 0;
            any = true;
        }
        string d = now.ToString("yyyy. M. d.", CultureInfo.InvariantCulture) + " (" + now.ToString("ddd", CultureInfo.GetCultureInfo("ko-KR")) + ")";
        if (d != dateText) { dateText = d; any = true; }
        if (any) { Invalidate(); if (Animating() && !anim.Enabled) anim.Start(); }
    }

    private bool Animating() { long t = Environment.TickCount64; for (int i = 0; i < 3; i++) if (flipAt[i] != 0 && t - flipAt[i] < FlipMs) return true; return false; }

    /// <summary>지금 모양에서 위젯이 차지할 크기.</summary>
    public Size DesiredSize() => new(S(300), S(expanded ? 164 : 46));

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
        bool h = foldRect.Contains(e.Location);
        if (h != hotFold) { hotFold = h; Invalidate(); }
        Cursor = h ? Cursors.Hand : Cursors.SizeAll;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotFold = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && !foldRect.Contains(e.Location)) { dragging = true; dragFrom = e.Location; Capture = true; }
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
        if (e.Button == MouseButtons.Left && foldRect.Contains(e.Location)) Expanded = !expanded;
    }

    // ───────── 그리기 ─────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        bool dark = back.GetBrightness() < 0.4f;
        Color card = dark ? Themes.Blend(back, Color.White, 0.07f) : Color.White;
        Color line = Themes.Blend(card, fore, dark ? 0.22f : 0.14f);
        Color dim = Themes.Blend(card, fore, 0.55f);
        Color ink = Themes.Blend(card, fore, 0.80f);
        const TextFormatFlags C = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(12)))
        {
            using var b = new SolidBrush(card); g.FillPath(b, path);
            using var pen = new Pen(line); g.DrawPath(pen, path);
        }

        float u = DeviceDpi / 96f;
        using var icon = new Font(MainForm.IconFontName(), 12f);
        int hh = expanded ? S(40) : Height;
        int bs = S(28);
        foldRect = new Rectangle(Width - S(14) - bs, (hh - bs) / 2, bs, bs);
        // 접기 · 펴기 화살표: 펼쳐 있으면 ^, 접혀 있으면 v
        using (var pen = new Pen(hotFold ? fore : dim, Math.Max(1.6f, 1.8f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float cx = foldRect.X + foldRect.Width / 2f, cy = foldRect.Y + foldRect.Height / 2f, d = 5f * u;
            if (expanded) g.DrawLines(pen, new[] { new PointF(cx - d, cy + d / 2), new PointF(cx, cy - d / 2), new PointF(cx + d, cy + d / 2) });
            else g.DrawLines(pen, new[] { new PointF(cx - d, cy - d / 2), new PointF(cx, cy + d / 2), new PointF(cx + d, cy - d / 2) });
        }

        if (!expanded)
        {
            // 작은 모양: [시계 아이콘] 도시 ········ 시:분:초 [v]
            TextRenderer.DrawText(g, "", icon, new Rectangle(S(14), 0, S(24), Height), Themes.Blend(card, accent, 0.9f), C);
            using var bold = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, city, Font, new Rectangle(S(46), 0, S(110), Height), dim, L | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, $"{cur[0]}:{cur[1]}:{cur[2]}", bold, new Rectangle(S(150), 0, foldRect.Left - S(150), Height), dim, C);
            return;
        }

        // 머리: [시계 아이콘] 시계 ········ [^]
        TextRenderer.DrawText(g, "", icon, new Rectangle(S(16), 0, S(24), hh), dim, C);
        using (var tf = new Font(Font.FontFamily, 11.5f)) TextRenderer.DrawText(g, "시계", tf, new Rectangle(S(48), 0, S(120), hh), dim, L);

        // 몸통: 옅은 판 안에 날짜 + 숫자판
        var body = new Rectangle(S(12), hh + S(2), Width - S(24), Height - hh - S(14));
        using (var path = Rounded(body, S(8))) using (var b = new SolidBrush(Themes.Blend(card, fore, dark ? 0.06f : 0.035f))) g.FillPath(b, path);
        using (var df = new Font(Font.FontFamily, 10.5f)) TextRenderer.DrawText(g, dateText, df, new Rectangle(body.X, body.Y + S(6), body.Width, S(24)), dim, C);

        int tw = S(62), th = S(64), gap = S(20);
        int totalW = tw * 3 + gap * 2;
        int x0 = body.X + (body.Width - totalW) / 2, ty = body.Y + S(34);
        using var digitFont = new Font(NovelStore.UiFontFamily, 26f, FontStyle.Bold);
        long now = Environment.TickCount64;
        for (int i = 0; i < 3; i++)
        {
            var r = new Rectangle(x0 + i * (tw + gap), ty, tw, th);
            double t = flipAt[i] == 0 ? 1 : Math.Min(1, (now - flipAt[i]) / (double)FlipMs);
            DrawFlip(g, r, digitFont, ink, card, line, cur[i], prev[i], t);
            if (i < 2) // 시 · 분 · 초 사이 쌍점
            {
                float cx = r.Right + gap / 2f, cy = ty + th / 2f, rad = S(3);
                using var db = new SolidBrush(ink);
                g.FillEllipse(db, cx - rad, cy - S(11) - rad, rad * 2, rad * 2);
                g.FillEllipse(db, cx - rad, cy + S(11) - rad, rad * 2, rad * 2);
            }
        }
    }

    /// <summary>숫자판 하나: 위 · 아래 반쪽. t(0→1)에 따라 위쪽 반쪽이 접혀 내려가고 아래쪽 반쪽이 펴지며 딸깍 넘어간다.</summary>
    private void DrawFlip(Graphics g, Rectangle r, Font font, Color ink, Color card, Color line, string now, string old, double t)
    {
        int mid = r.Y + r.Height / 2;
        var top = new Rectangle(r.X, r.Y, r.Width, r.Height / 2);
        var bottom = new Rectangle(r.X, mid, r.Width, r.Height - r.Height / 2);
        using (var path = Rounded(r, S(7))) using (var b = new SolidBrush(card)) g.FillPath(b, path);

        void Half(string text, Rectangle half, float scaleY, bool isTop)
        {
            var saved = g.Save();
            g.TranslateTransform(0, mid);
            g.ScaleTransform(1, Math.Max(0.001f, scaleY));
            g.TranslateTransform(0, -mid);
            g.SetClip(half, CombineMode.Intersect);
            using var br = new SolidBrush(ink);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip, Trimming = StringTrimming.None };
            g.DrawString(text, font, br, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
            g.Restore(saved);
        }

        if (t >= 1 || old == now) { Half(now, top, 1, true); Half(now, bottom, 1, false); }
        else
        {
            Half(now, top, 1, true);     // 뒤에 깔린 위쪽은 새 숫자
            Half(old, bottom, 1, false); // 뒤에 깔린 아래쪽은 옛 숫자
            if (t < 0.5) Half(old, top, (float)(1 - t * 2), true);        // 옛 위쪽 반쪽이 가운데로 접혀 내려간다
            else Half(now, bottom, (float)(t * 2 - 1), false);            // 새 아래쪽 반쪽이 가운데에서 펴진다
        }
        // 가운데 가름선
        using (var lp = new Pen(Themes.Blend(card, Color.Black, 0.22f), Math.Max(1f, DeviceDpi / 96f))) g.DrawLine(lp, r.X, mid, r.Right, mid);
        using (var path = Rounded(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), S(7))) using (var pen = new Pen(Themes.Blend(card, Color.Black, 0.07f))) g.DrawPath(pen, path);
    }
}

/// <summary>시계 위치: 도시 · 나라 이름과 시간대(Windows 시간대 아이디).</summary>
internal static class ClockPlaces
{
    public sealed record Place(string Country, string KoCity, string City, string ZoneId);

    public static readonly Place[] Curated =
    {
        new("대한민국", "서울", "Seoul", "Korea Standard Time"), new("일본", "도쿄", "Tokyo", "Tokyo Standard Time"),
        new("중국", "베이징", "Beijing", "China Standard Time"), new("대만", "타이베이", "Taipei", "Taipei Standard Time"),
        new("홍콩", "홍콩", "Hong Kong", "China Standard Time"), new("싱가포르", "싱가포르", "Singapore", "Singapore Standard Time"),
        new("태국", "방콕", "Bangkok", "SE Asia Standard Time"), new("베트남", "하노이", "Hanoi", "SE Asia Standard Time"),
        new("인도", "뉴델리", "New Delhi", "India Standard Time"), new("아랍에미리트", "두바이", "Dubai", "Arabian Standard Time"),
        new("튀르키예", "이스탄불", "Istanbul", "Turkey Standard Time"), new("러시아", "모스크바", "Moscow", "Russian Standard Time"),
        new("영국", "런던", "London", "GMT Standard Time"), new("프랑스", "파리", "Paris", "Romance Standard Time"),
        new("독일", "베를린", "Berlin", "W. Europe Standard Time"), new("이탈리아", "로마", "Rome", "W. Europe Standard Time"),
        new("이집트", "카이로", "Cairo", "Egypt Standard Time"), new("미국", "뉴욕", "New York", "Eastern Standard Time"),
        new("미국", "시카고", "Chicago", "Central Standard Time"), new("미국", "덴버", "Denver", "Mountain Standard Time"),
        new("미국", "로스앤젤레스", "Los Angeles", "Pacific Standard Time"), new("미국", "하와이", "Honolulu", "Hawaiian Standard Time"),
        new("캐나다", "밴쿠버", "Vancouver", "Pacific Standard Time"), new("브라질", "상파울루", "São Paulo", "E. South America Standard Time"),
        new("호주", "시드니", "Sydney", "AUS Eastern Standard Time"), new("뉴질랜드", "오클랜드", "Auckland", "New Zealand Standard Time"),
        new("협정 세계시", "UTC", "UTC", "UTC"),
    };

    /// <summary>현재 UTC 차이를 "UTC+09:00" 꼴로.</summary>
    public static string OffsetText(string zoneId)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            var off = tz.GetUtcOffset(DateTime.UtcNow);
            return "UTC" + (off < TimeSpan.Zero ? "-" : "+") + off.ToString(@"hh\:mm");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return ""; }
    }

    /// <summary>이 컴퓨터에서 찾을 수 있는 도시들(나라별로 정리된 것).</summary>
    public static List<Place> Available()
    {
        var list = new List<Place>();
        foreach (var p in Curated)
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(p.ZoneId); list.Add(p); } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return list;
    }
}

/// <summary>시계 위치 고르기 창: 나라 · 도시를 검색해 고르고, 목록에 없으면 이 컴퓨터의 모든 시간대에서 고른다. 더블클릭 또는 [선택]으로 정한다.</summary>
internal sealed class ClockZoneDialog : Form
{
    private sealed record Item(string Text, string ZoneId, string City);
    private readonly TextBox search;
    private readonly ListBox list;
    private readonly List<Item> all = new();

    public string ZoneId { get; private set; } = "";
    public string City { get; private set; } = "";

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public ClockZoneDialog(string currentZoneId)
    {
        Text = "시계 위치";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font(NovelStore.UiFontFamily, 10f);
        ClientSize = new Size(S(420), S(520));

        foreach (var p in ClockPlaces.Available()) all.Add(new Item($"{p.Country} · {p.KoCity}   {ClockPlaces.OffsetText(p.ZoneId)}", p.ZoneId, p.City));
        var known = new HashSet<string>(ClockPlaces.Curated.Select(c => c.ZoneId));
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            if (!known.Contains(tz.Id)) all.Add(new Item(tz.DisplayName, tz.Id, tz.StandardName.Length > 0 ? tz.StandardName : tz.Id));

        search = new TextBox { Location = new Point(S(14), S(14)), Size = new Size(S(392), S(28)), PlaceholderText = "나라 · 도시 · 시간대 검색" };
        list = new ListBox { Location = new Point(S(14), S(52)), Size = new Size(S(392), S(404)), IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "선택", Location = new Point(S(296), S(466)), Size = new Size(S(110), S(36)) };
        var cancel = new Button { Text = "취소", Location = new Point(S(176), S(466)), Size = new Size(S(110), S(36)), DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { search, list, ok, cancel });
        CancelButton = cancel;
        Themes.ApplyChrome(this, AppShared.LibraryTheme);

        void Fill()
        {
            string q = search.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var it in all.Where(i => q.Length == 0 || i.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.City.Contains(q, StringComparison.CurrentCultureIgnoreCase))) list.Items.Add(it);
            list.EndUpdate();
            list.DisplayMember = nameof(Item.Text);
            int i0 = list.Items.Cast<Item>().ToList().FindIndex(i => i.ZoneId == currentZoneId);
            if (q.Length == 0 && i0 >= 0) list.SelectedIndex = i0; else if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        void Accept() { if (list.SelectedItem is Item it) { ZoneId = it.ZoneId; City = it.City; DialogResult = DialogResult.OK; } }
        search.TextChanged += (_, _) => Fill();
        list.DoubleClick += (_, _) => Accept();
        ok.Click += (_, _) => Accept();
        AcceptButton = ok;
        Fill();
        Shown += (_, _) => { Activate(); search.Focus(); };
    }
}
