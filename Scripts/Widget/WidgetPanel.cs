using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 사이드바 [위젯] 아이콘을 누르면 파일 트리 자리에 뜨는 위젯 모음 패널.
/// 검색칸 + 분류 칩 + 위젯 카드 목록. 카드를 누르면 그 위젯을 켜고 끄고(Toggled), 켜진 위젯은 눌린 모양과 ✓로 보인다.
/// 톱니바퀴가 있는 카드는 그 위젯의 설정을 연다(SettingsRequested). 아직 만들지 않은 위젯은 흐리게 "준비 중"으로 둔다.
/// </summary>
internal sealed class WidgetPanel : Panel
{
    private sealed record Def(string Key, string Title, string Desc, string Category, bool Ready, bool HasSettings);

    private const string All = "전체", InUse = "사용 중인 위젯";
    private static readonly string[] Categories = { All, InUse, "분석 및 통계", "자료 참조", "에디터 도구", "시간 · 집중 관리", "기타 · 특수 도구" };

    private static readonly Def[] Defs =
    {
        new("count", "글자 수 세기", "여러 기준으로 글자 수를 실시간 계산하고 목표를 설정합니다.", "분석 및 통계", true, true),
        new("dict", "사전", "낱말 뜻을 찾아보는 사전 창을 따로 띄웁니다.", "자료 참조", true, true),
        new("typewriter", "타입라이터", "쓰고 있는 줄을 화면 가운데에 두어 눈이 움직이지 않게 합니다.", "에디터 도구", true, true),
        new("quote", "빠른 따옴표", "따옴표를 치면 한 쌍을 만들어 주고, 따옴표 앞에서 Enter로 다음 줄로 넘어갑니다.", "에디터 도구", true, true),
        new("spell", "맞춤법 검사기", "자주 틀리는 맞춤법을 내장 사전으로 바로 찾고, 라마(Ollama)가 켜져 있으면 AI로 한 번 더 검사합니다.", "에디터 도구", true, true),
        new("name", "이름 생성기", "글자 수와 시작 글자를 정해 판타지 이름을 만들고, 한글 발음도 함께 보여 줍니다.", "에디터 도구", true, true),
        new("timer", "뽀모도로 타이머", "집중과 휴식 시간을 번갈아 재는 타이머를 화면에 띄웁니다.", "시간 · 집중 관리", true, true),
        new("clock", "시계", "현재 시각과 세계 여러 지역의 시간을 확인합니다.", "시간 · 집중 관리", true, true),
        new("special", "특수문자", "소설에 쓰는 특수문자를 쉽게 입력하고 원하는 문자를 추가합니다.", "기타 · 특수 도구", true, true),
    };

    /// <summary>카드를 눌렀다(키는 사이드바 아이콘과 같다: count · dict · typewriter · quote · timer · special).</summary>
    public event Action<string>? Toggled;
    /// <summary>카드의 톱니바퀴를 눌렀다.</summary>
    public event Action<string>? SettingsRequested;

    private LibraryPalette? palette;
    private readonly Panel top = new();
    private readonly Panel searchBox = new();
    private readonly TextBox search;
    private readonly List<ChipButton> chips = new();
    private readonly Panel list = new();
    private readonly Label empty;
    private readonly List<WidgetCard> cards = new();
    private readonly HashSet<string> active = new();
    private string category = All;

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public WidgetPanel()
    {
        DoubleBuffered = true;
        string ff = NovelStore.UiFontFamily;
        Font = new Font(ff, 10f);

        search = new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = "위젯 검색…", Font = new Font(ff, 10.5f) };
        searchBox.Controls.Add(search);
        searchBox.Paint += PaintSearch;
        searchBox.MouseDown += (_, _) => search.Focus();
        search.GotFocus += (_, _) => searchBox.Invalidate();
        search.LostFocus += (_, _) => searchBox.Invalidate();
        search.TextChanged += (_, _) => Rebuild();
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && search.TextLength > 0) { search.Clear(); e.SuppressKeyPress = true; } };

        top.Dock = DockStyle.Top;
        top.Controls.Add(searchBox);
        foreach (var c in Categories)
        {
            var chip = new ChipButton(c);
            chip.Click += (_, _) => { category = c; foreach (var x in chips) x.Selected = x.Text == c; Rebuild(); };
            chips.Add(chip);
            top.Controls.Add(chip);
        }
        chips[0].Selected = true;
        top.Resize += (_, _) => LayoutTop();

        list.Dock = DockStyle.Fill;
        list.AutoScroll = true;
        list.ClientSizeChanged += (_, _) => LayoutCards();
        empty = new Label { AutoSize = false, TextAlign = ContentAlignment.TopCenter, Visible = false, Text = "맞는 위젯이 없어요." };
        list.Controls.Add(empty);

        Controls.Add(list);
        Controls.Add(top);

        foreach (var d in Defs)
        {
            var card = new WidgetCard(d);
            card.Clicked += () => { if (d.Ready) Toggled?.Invoke(d.Key); };
            card.GearClicked += () => SettingsRequested?.Invoke(d.Key);
            cards.Add(card);
            list.Controls.Add(card);
        }
        Rebuild();
    }

    /// <summary>켜져 있는 위젯을 눌린 모양으로 맞춘다(바뀐 게 있을 때만 다시 그린다).</summary>
    public void SetActive(string key, bool on)
    {
        if (!(on ? active.Add(key) : active.Remove(key))) return;
        foreach (var c in cards.Where(c => c.Key == key)) { c.On = on; c.Invalidate(); }
        if (category == InUse) Rebuild();
    }

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = list.BackColor = top.BackColor = p.Main;
        searchBox.BackColor = p.Card;
        search.BackColor = p.Card; search.ForeColor = p.Fore;
        empty.ForeColor = p.Dim; empty.BackColor = p.Main;
        foreach (var c in chips) c.SetPalette(p);
        foreach (var c in cards) c.SetPalette(p);
        searchBox.Invalidate();
        LayoutTop();
    }

    // ───────── 배치 ─────────

    private void LayoutTop()
    {
        int pad = S(14), w = top.ClientSize.Width;
        if (w <= 0) return;
        int searchH = S(36);
        searchBox.SetBounds(pad, S(12), w - pad * 2, searchH);
        search.Font = new Font(NovelStore.UiFontFamily, 10.5f);
        search.SetBounds(S(34), (searchH - search.PreferredHeight) / 2, searchBox.Width - S(34) - S(12), search.PreferredHeight);

        int x = pad, y = searchBox.Bottom + S(12), h = S(30), gap = S(6);
        foreach (var chip in chips)
        {
            int cw = chip.PreferredWidth;
            if (x + cw > w - pad && x > pad) { x = pad; y += h + gap; }
            chip.SetBounds(x, y, cw, h);
            x += cw + gap;
        }
        top.Height = y + h + S(12);
    }

    private void LayoutCards()
    {
        int pad = S(14), gap = S(10), w = list.ClientSize.Width - pad * 2;
        if (w <= 0) return;
        int y = pad + list.AutoScrollPosition.Y; // 스크롤한 만큼 어긋난 채로 다시 놓으면 카드가 제자리로 튀어 클릭이 빗나간다
        foreach (var c in cards.Where(c => c.Visible))
        {
            c.SetBounds(pad, y, w, S(92));
            y += c.Height + gap;
        }
        empty.SetBounds(pad, pad, w, S(40));
        list.AutoScrollMinSize = new Size(0, y - list.AutoScrollPosition.Y + pad);
    }

    private void Rebuild()
    {
        string q = search.Text.Trim();
        int shown = 0;
        foreach (var c in cards)
        {
            var d = c.Def;
            bool ok = category switch { All => true, InUse => active.Contains(d.Key), _ => d.Category == category };
            if (ok && q.Length > 0) ok = d.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || d.Desc.Contains(q, StringComparison.CurrentCultureIgnoreCase);
            c.Visible = ok;
            if (ok) shown++;
        }
        empty.Visible = shown == 0;
        empty.Text = category == InUse && q.Length == 0 ? "켜 둔 위젯이 없어요." : "맞는 위젯이 없어요.";
        LayoutCards();
    }

    private void PaintSearch(object? s, PaintEventArgs e)
    {
        var p = palette; if (p == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, searchBox.Width - 1.5f, searchBox.Height - 1.5f);
        using (var path = LibraryPalette.Rounded(r, S(10)))
        using (var pen = new Pen(search.Focused ? p.Accent : p.Line)) g.DrawPath(pen, path);
        using var icon = new Font(MainForm.IconFontName(), 11f);
        TextRenderer.DrawText(g, "", icon, new Rectangle(S(8), 0, S(24), searchBox.Height), p.Dim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    // ───────── 분류 칩 ─────────

    private sealed class ChipButton : Control
    {
        private LibraryPalette? p;
        private bool hover, selected;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get => selected; set { if (selected == value) return; selected = value; Invalidate(); } }
        public ChipButton(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Text = text; Cursor = Cursors.Hand;
            Font = new Font(NovelStore.UiFontFamily, 9.5f);
        }
        public void SetPalette(LibraryPalette pal) { p = pal; Invalidate(); }
        public int PreferredWidth => TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, 100), TextFormatFlags.NoPadding).Width + (int)Math.Round(20 * DeviceDpi / 96.0);
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? BackColor);
            if (p == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = LibraryPalette.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f))
            {
                if (selected) { using var b = new SolidBrush(p.Accent); g.FillPath(b, path); }
                else if (hover) { using var b = new SolidBrush(p.CardHover); g.FillPath(b, path); }
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, selected ? p.AccentFore : p.Fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    // ───────── 위젯 카드 ─────────

    private sealed class WidgetCard : Control
    {
        public readonly Def Def;
        public string Key => Def.Key;
        public bool On;
        private LibraryPalette? p;
        private bool hover, gearHover;
        private readonly ToolTip tip = new();
        public event Action? Clicked, GearClicked;

        public WidgetCard(Def def)
        {
            Def = def;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font(NovelStore.UiFontFamily, 10f);
            Cursor = def.Ready ? Cursors.Hand : Cursors.Default;
            SetStyle(ControlStyles.Selectable, false); TabStop = false; // 포커스를 받으면 목록이 그 카드를 보이게 스크롤을 맨 위로 되돌려 버린다
            if (!def.Ready) tip.SetToolTip(this, "아직 준비 중이에요");
        }

        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
        public void SetPalette(LibraryPalette pal) { p = pal; Invalidate(); }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        private Rectangle GearRect => new(Width - S(34), S(6), S(28), S(28));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool gh = Def.HasSettings && GearRect.Contains(e.Location);
            if (gh != gearHover) { gearHover = gh; Invalidate(); }
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = gearHover = false; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (Def.HasSettings && GearRect.Contains(e.Location)) GearClicked?.Invoke();
            else Clicked?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? BackColor);
            if (p == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool dim = !Def.Ready;
            Color fore = dim ? p.Dim : p.Fore;

            // 카드 바탕: 켜진 위젯은 눌린 모양(강조색 테두리 + 옅은 강조 바탕)
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = LibraryPalette.Rounded(rect, S(12)))
            {
                Color bg = On ? p.AccentSoft : hover && Def.Ready ? p.CardHover : p.Main;
                using (var b = new SolidBrush(bg)) g.FillPath(b, path);
                using var pen = new Pen(On ? p.Accent : p.Line, On ? 1.6f : 1f);
                g.DrawPath(pen, path);
            }

            // 아이콘
            int pad = S(14), icon = S(26);
            var ir = new Rectangle(pad, S(14), icon, icon);
            DrawIcon(g, ir, On ? p.Accent : dim ? p.Dim : p.Fore, p.Main);

            // 이름 + ✓
            using var titleFont = new Font(NovelStore.UiFontFamily, 10.5f, FontStyle.Bold);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            int tx = ir.Right + S(12);
            var titleR = new Rectangle(tx, S(12), Width - tx - S(44), S(30));
            TextRenderer.DrawText(g, Def.Title, titleFont, titleR, fore, flags);
            if (On)
            {
                int w = TextRenderer.MeasureText(g, Def.Title, titleFont, titleR.Size, flags).Width;
                Icons.Check(g, new Rectangle(tx + Math.Min(w, titleR.Width) + S(8), S(12) + S(8), S(14), S(14)), p.Accent);
            }
            else if (dim)
            {
                using var small = new Font(NovelStore.UiFontFamily, 8.5f);
                int w = TextRenderer.MeasureText(g, Def.Title, titleFont, titleR.Size, flags).Width;
                TextRenderer.DrawText(g, "준비 중", small, new Rectangle(tx + Math.Min(w, titleR.Width) + S(8), S(12), S(60), S(30)), p.Dim, flags);
            }

            // 톱니바퀴
            if (Def.HasSettings)
            {
                using var gf = new Font(MainForm.IconFontName(), 11f);
                var gr = GearRect;
                if (gearHover) { using var hb = new SolidBrush(p.CardHover); using var hp = LibraryPalette.Rounded(gr, S(6)); g.FillPath(hb, hp); }
                TextRenderer.DrawText(g, "", gf, gr, gearHover ? p.Fore : p.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // 설명
            using var descFont = new Font(NovelStore.UiFontFamily, 9f);
            TextRenderer.DrawText(g, Def.Desc, descFont, new Rectangle(pad, S(48), Width - pad * 2, Height - S(48) - S(10)), p.Dim,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private void DrawIcon(Graphics g, Rectangle r, Color c, Color bg)
        {
            float u = DeviceDpi / 96f;
            switch (Def.Key)
            {
                case "typewriter": Icons.Typewriter(g, r, c, bg, u); return;
                case "quote": Icons.Quote(g, r, c, u); return;
                case "count":
                    {
                        int cs = r.Width;
                        using var cb = PurpleIcons.CountDoc(cs * 3, c);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(cb, r);
                        return;
                    }
                case "spell":
                    {
                        using var f = new Font(NovelStore.UiFontFamily, 13f * 0.75f * u, FontStyle.Bold, GraphicsUnit.Point);
                        TextRenderer.DrawText(g, "가", f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        using var pen = new Pen(c, Math.Max(1.4f, 1.5f * u));
                        g.DrawLine(pen, r.X + r.Width * 0.18f, r.Bottom - 2 * u, r.Right - r.Width * 0.18f, r.Bottom - 2 * u);
                        return;
                    }
                case "name":
                    {
                        using var f = new Font(NovelStore.UiFontFamily, 11f * 0.75f * u, FontStyle.Bold, GraphicsUnit.Point);
                        TextRenderer.DrawText(g, "Aa", f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        return;
                    }
                case "special":
                    {
                        using var f = new Font("Segoe UI", 15f * 0.75f * u, FontStyle.Bold, GraphicsUnit.Point);
                        TextRenderer.DrawText(g, "Ω", f, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        return;
                    }
            }
            string glyph = Def.Key switch
            {
                "clock" => "",
                _ => WorkspaceRail.Catalog.FirstOrDefault(x => x.Key == Def.Key).Glyph ?? "",
            };
            using var icon = new Font(MainForm.IconFontName(), 15f);
            TextRenderer.DrawText(g, glyph, icon, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
