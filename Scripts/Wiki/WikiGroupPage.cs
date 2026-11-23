using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 대분류 · 분류 한 곳을 문서처럼 보여 주는 쪽(마인드맵에서 점을 두 번 눌러 연다).
/// 맨 위에 작품 › 대분류 › 분류 경로와 큰 제목, 분류는 요약 · 대분류 · 상위 분류 줄, 그 밑에 「하위 문서」를 첫 글자(ㄱ ㄴ ㄷ …)별 묶음으로 보여 준다.
/// 문서 줄은 그 문서를, 분류 줄은 그 분류의 쪽을 연다.
/// </summary>
internal sealed class WikiGroupPage : Control
{
    private sealed record Hit(Rectangle R, Action Act);

    private LibraryPalette? p;
    private WikiViewData data = new();
    private string kind = "category", key = "";
    private readonly List<Hit> hits = new();
    private readonly HashSet<string> collapsed = new();
    private readonly VScrollBar bar = new() { Dock = DockStyle.Right, Visible = false };
    private int offset, total, hover = -1;
    private bool listCollapsed, propsCollapsed, syncing;
    // 분류 쪽의 속성 입력 도구(위키 문서의 속성과 같은 것들)
    private readonly ComboBox categoryBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
    private readonly StarBox stars = new() { Visible = false };
    private readonly TextBox summaryBox = new() { Visible = false, BorderStyle = BorderStyle.None };
    private readonly List<TextBox> attrBoxes = new();
    private string? focusAttr;
    private Rectangle summaryRow;   // 요약 줄(마우스를 올리면 칸 바탕과 ×가 보이는 자리)
    private bool hoverSummary;
    private readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 80 };
    private readonly ClassStrip classes = new() { Visible = false };
    private readonly TagStrip tagStrip = new() { Visible = false };
    private readonly Dictionary<Control, Rectangle> propRects = new();

    /// <summary>경로 맨 앞의 작품 이름을 눌렀다(작품 화면으로).</summary>
    public event Action? BackRequested;
    public event Action<string>? DocChosen;
    /// <summary>다른 대분류("category", 키) · 분류("class", 이름) 쪽을 열라는 요청.</summary>
    public event Action<string, string>? GroupChosen;
    /// <summary>분류의 속성을 고쳤다(분류 이름, 요약, 대분류 키, 상위 분류들, 중요도 — 안 고친 것은 null).</summary>
    public event Action<string, string?, string?, List<string>?, int?>? ClassEdited;
    /// <summary>분류의 속성 표를 고쳤다(분류 이름, 새 속성 목록).</summary>
    public event Action<string, List<(string Key, string Value)>>? ClassAttrsEdited;
    /// <summary>분류의 태그를 고쳤다(분류 이름, 새 태그 목록).</summary>
    public event Action<string, List<string>>? ClassTagsEdited;

    public string Kind => kind;
    public string Key => key;
    /// <summary>칸 머리에 적을 제목.</summary>
    public string TitleText => kind == "category" ? "대분류: " + WikiCategory.Find(key).Label : "분류: " + key;

    public WikiGroupPage()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(NovelStore.UiFontFamily, 11f);
        bar.ValueChanged += (_, _) => { offset = bar.Value; PlaceControls(); Invalidate(); };
        Controls.Add(bar);
        foreach (var c in WikiCategory.All) categoryBox.Items.Add(c.Label);
        var ff = NovelStore.UiFontFamily;
        categoryBox.Font = summaryBox.Font = new Font(ff, 9.5f);
        Controls.AddRange(new Control[] { categoryBox, stars, summaryBox, classes, tagStrip });
        tagStrip.TagSubmitted += t => { var cur = data.InfoOf(key)?.Tags ?? new(); if (!cur.Contains(t, StringComparer.OrdinalIgnoreCase)) ClassTagsEdited?.Invoke(key, cur.Append(t).ToList()); };
        tagStrip.RemoveRequested += t => ClassTagsEdited?.Invoke(key, (data.InfoOf(key)?.Tags ?? new()).Where(x => !x.Equals(t, StringComparison.OrdinalIgnoreCase)).ToList());
        categoryBox.SelectedIndexChanged += (_, _) => { if (!syncing && categoryBox.SelectedIndex >= 0) ClassEdited?.Invoke(key, null, WikiCategory.All[categoryBox.SelectedIndex].Key, null, null); };
        stars.ValueChanged += v => { if (!syncing) ClassEdited?.Invoke(key, null, null, null, v); };
        summaryBox.PlaceholderText = "비어 있음";
        summaryBox.Enter += (_, _) => ApplySummaryLook();
        summaryBox.Leave += (_, _) => { CommitSummary(); ApplySummaryLook(); };
        hoverTimer.Tick += (_, _) =>
        {
            if (!Visible || kind != "class" || summaryRow.IsEmpty) { if (hoverSummary) { hoverSummary = false; ApplySummaryLook(); Invalidate(); } return; }
            var pt = PointToClient(Cursor.Position);
            bool h = ClientRectangle.Contains(pt) && summaryRow.Contains(new Point(pt.X, pt.Y + offset));
            if (h != hoverSummary) { hoverSummary = h; ApplySummaryLook(); Invalidate(); }
        };
        hoverTimer.Start();
        summaryBox.TextChanged += (_, _) => Invalidate(); // 끝의 ×가 글이 있을 때만 보이게
        summaryBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitSummary(); } };
        classes.AddRequested += PickParent;
        classes.RemoveRequested += name => ClassEdited?.Invoke(key, null, null, data.ParentsOf(key).Where(x => !x.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList(), null);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void SetPalette(LibraryPalette pal)
    {
        p = pal; BackColor = pal.Main;
        stars.SetPalette(pal); classes.SetPalette(pal); tagStrip.SetPalette(pal);
        categoryBox.BackColor = Themes.Blend(pal.Main, pal.Fore, pal.Dark ? 0.08f : 0.03f);
        summaryBox.BackColor = pal.Main; // 입력칸 테두리 없이 글만 보이게
        summaryBox.ForeColor = categoryBox.ForeColor = pal.Fore;
        foreach (var b in attrBoxes) { b.BackColor = pal.Main; b.ForeColor = pal.Fore; }
        ApplySummaryLook();
        Invalidate();
    }

    protected override void Dispose(bool disposing) { if (disposing) hoverTimer.Dispose(); base.Dispose(disposing); }

    /// <summary>요약 칸: 평소엔 바탕 없이 글만, 마우스를 올렸거나 쓰는 중이면 옅은 바탕.</summary>
    private void ApplySummaryLook()
    {
        if (p == null) return;
        summaryBox.BackColor = hoverSummary || summaryBox.Focused ? Themes.Blend(p.Main, p.Fore, p.Dark ? 0.10f : 0.06f) : p.Main;
    }

    private void CommitSummary()
    {
        if (syncing || kind != "class") return;
        string now = summaryBox.Text.Trim();
        if (now != (data.InfoOf(key)?.Summary ?? "")) ClassEdited?.Invoke(key, now, null, null, null);
    }

    /// <summary>[분류 추가]: 이 분류의 상위로 넣을 분류를 고른다(자기 자신과 그 아래 분류는 뺀다).</summary>
    private void PickParent(Rectangle anchor)
    {
        if (p == null || PickPopup.JustClosed) return;
        var banned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        void Down(string n) { foreach (var c in data.Children(n)) if (banned.Add(c)) Down(c); }
        Down(key);
        var mine = data.ParentsOf(key);
        var choices = data.Parents.Keys.Where(c => !banned.Contains(c) && !mine.Contains(c, StringComparer.OrdinalIgnoreCase)).OrderBy(c => c, NaturalStringComparer.Instance).ToList();
        var pop = new PickPopup(p, choices, "분류", "선택할 분류가 없어요", "맞는 분류가 없어요", canDelete: false);
        pop.Picked += name => ClassEdited?.Invoke(key, null, null, mine.Append(name).ToList(), null);
        pop.Created += name =>
        {
            try { WikiStore.AddClass(data.NovelDir, name); } catch (Exception ex) { AppLog.Write("분류 목록을 저장하지 못했어요", ex); }
            ClassEdited?.Invoke(key, null, null, mine.Append(name).ToList(), null);
        };
        pop.ShowBelow(anchor, FindForm(), S(300));
    }

    private TextBox NewAttrBox(string key, string value)
    {
        var b = new TextBox { BorderStyle = BorderStyle.None, Font = summaryBox.Font, Text = value, Tag = key, Visible = false, BackColor = p?.Main ?? BackColor, ForeColor = p?.Fore ?? ForeColor };
        b.Leave += (_, _) => CommitAttrs();
        b.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitAttrs(); } };
        b.TextChanged += (_, _) => Invalidate();
        return b;
    }

    private List<(string Key, string Value)> CurrentAttrs() => attrBoxes.Select(b => ((string)b.Tag!, b.Text.Trim())).ToList();

    private void CommitAttrs()
    {
        if (syncing || kind != "class") return;
        var now = CurrentAttrs();
        var cur = data.InfoOf(key)?.Attributes ?? new();
        if (!now.SequenceEqual(cur)) ClassAttrsEdited?.Invoke(key, now);
    }

    /// <summary>[속성 추가하기]: 이 작품에서 쓰인 속성 이름 중에서 고르거나 새로 만든다. 만든 뒤에는 바로 값을 적는다.</summary>
    private void PickAttribute(Rectangle anchor)
    {
        if (p == null || PickPopup.JustClosed) return;
        var mine = CurrentAttrs();
        var choices = WikiStore.AllAttributeNames(data.NovelDir).Where(n => !mine.Any(a => a.Key.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        string dir = data.NovelDir;
        var pop = new AttrPopup(p, choices, WikiStore.AttributeFavorites(dir), WikiStore.AttributeGlyph);
        void Add(string raw)
        {
            string name = raw.Replace(":", " ").Replace("：", " ").Trim();
            if (name.Length == 0 || mine.Any(a => a.Key.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
            focusAttr = name;
            mine.Add((name, ""));
            ClassAttrsEdited?.Invoke(key, mine);
        }
        pop.Picked += Add;
        pop.Created += Add;
        pop.FavoriteToggled += (n, f) => { try { WikiStore.SetAttributeFavorite(dir, n, f); } catch (Exception ex) { AppLog.Write("속성 즐겨찾기를 저장하지 못했어요", ex); } };
        pop.ShowAt(anchor, FindForm(), Math.Max(S(300), anchor.Width));
    }

    /// <summary>입력 도구에 지금 분류의 값을 넣는다.</summary>
    private void SyncControls()
    {
        if (kind != "class") return;
        syncing = true;
        try
        {
            var info = data.InfoOf(key);
            categoryBox.SelectedIndex = Array.FindIndex(WikiCategory.All, c => c.Key == data.CategoryOf(key));
            categoryBox.Enabled = data.ParentsOf(key).Count == 0; // 상위 분류가 있으면 맨 위 분류의 대분류를 따른다
            stars.Value = info?.Importance ?? 1;
            if (!summaryBox.Focused) summaryBox.Text = info?.Summary ?? "";
            classes.SetItems(data.ParentsOf(key));
            tagStrip.SetTags(info?.Tags ?? new());
            foreach (var b in attrBoxes) { Controls.Remove(b); b.Dispose(); }
            attrBoxes.Clear();
            foreach (var (k, v) in info?.Attributes ?? new()) { var b = NewAttrBox(k, v); attrBoxes.Add(b); Controls.Add(b); }
        }
        finally { syncing = false; }
        if (focusAttr != null) { var fb = attrBoxes.FirstOrDefault(b => ((string)b.Tag!).Equals(focusAttr, StringComparison.OrdinalIgnoreCase)); focusAttr = null; if (fb != null) BeginInvoke(() => { fb.Focus(); }); }
    }

    /// <summary>입력 도구를 쪽 안의 자리(스크롤만큼 올려서)에 놓는다. 접혀 있거나 분류 쪽이 아니면 숨긴다.</summary>
    private void PlaceControls()
    {
        foreach (var c in new Control[] { categoryBox, stars, summaryBox, classes, tagStrip }.Concat(attrBoxes))
        {
            if (kind == "class" && propRects.TryGetValue(c, out var r))
            {
                c.Bounds = new Rectangle(r.X, r.Y - offset, r.Width, r.Height);
                if (!c.Visible) c.Visible = true;
            }
            else if (c.Visible) c.Visible = false;
        }
    }

    /// <summary>보여 줄 곳을 정한다(자료는 지금 위키에서 새로 만든 것).</summary>
    public void Open(WikiViewData d, string kind, string key)
    {
        data = d; this.kind = kind; this.key = key;
        collapsed.Clear(); listCollapsed = propsCollapsed = false; offset = 0;
        SyncControls();
        Relayout();
    }

    /// <summary>위키가 바뀌었을 때 자료만 바꿔 다시 그린다(스크롤 위치는 그대로).</summary>
    public void SetData(WikiViewData d) { data = d; SyncControls(); Relayout(); }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

    private void Relayout()
    {
        if (!IsHandleCreated && Width <= 0) return;
        using var g = CreateGraphics();
        total = Render(g, false);
        bool need = total > ClientSize.Height && ClientSize.Height > 0;
        bar.Visible = need;
        if (need)
        {
            offset = Math.Clamp(offset, 0, total - ClientSize.Height);
            bar.Maximum = total; bar.LargeChange = ClientSize.Height; bar.SmallChange = S(60);
            if (bar.Value != offset) bar.Value = offset;
        }
        else offset = 0;
        PlaceControls();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!bar.Visible) return;
        int v = Math.Clamp(offset - Math.Sign(e.Delta) * S(80), 0, total - ClientSize.Height);
        offset = v; bar.Value = v; PlaceControls(); Invalidate();
    }

    // ───────── 내용 ─────────

    private sealed record Item(string Label, string SortKey, string? DocPath, string? ClassName, WikiCategory Cat);

    private List<Item> Items()
    {
        var items = new List<Item>();
        if (kind == "category")
        {
            foreach (var d in data.Docs.Where(x => x.Category == key)) items.Add(new Item(d.Title, d.Title, d.Path, null, WikiCategory.Find(key)));
            foreach (var c in data.Parents.Keys.Where(c => data.CategoryOf(c) == key)) items.Add(new Item("분류:" + c, c, null, c, WikiCategory.Find(key)));
        }
        else
        {
            var cat = WikiCategory.Find(data.CategoryOf(key));
            foreach (var d in data.DocsIn(key)) items.Add(new Item(d.Title, d.Title, d.Path, null, WikiCategory.Find(d.Category)));
            foreach (var c in data.Children(key)) items.Add(new Item("분류:" + c, c, null, c, cat));
        }
        return items;
    }

    private static readonly char[] Cho = "ㄱㄱㄴㄷㄷㄹㅁㅂㅂㅅㅅㅇㅈㅈㅊㅋㅌㅍㅎ".ToCharArray();

    /// <summary>묶음 이름: 한글은 첫 자음(ㄲ은 ㄱ에 합친다), 영문은 대문자, 그 밖은 #.</summary>
    private static string Initial(string s)
    {
        s = s.TrimStart();
        if (s.Length == 0) return "#";
        char c = s[0];
        if (c >= 0xAC00 && c <= 0xD7A3) return Cho[(c - 0xAC00) / 588].ToString();
        if (c >= 0x3131 && c <= 0x314E) return c switch { 'ㄲ' => "ㄱ", 'ㄸ' => "ㄷ", 'ㅃ' => "ㅂ", 'ㅆ' => "ㅅ", 'ㅉ' => "ㅈ", _ => c.ToString() };
        if (char.IsLetter(c)) return char.ToUpperInvariant(c).ToString();
        return "#";
    }

    private static int InitialOrder(string ini)
    {
        const string ko = "ㄱㄴㄷㄹㅁㅂㅅㅇㅈㅊㅋㅌㅍㅎ";
        int i = ko.IndexOf(ini[0]);
        if (i >= 0) return i;
        return char.IsLetter(ini[0]) ? 100 + ini[0] : 1000;
    }

    private int drawOffset;
    /// <summary>글자는 그래픽 변환을 따르지 않으니 스크롤만큼 직접 올려서 그린다.</summary>
    private void Txt(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags fl) { r.Y -= drawOffset; TextRenderer.DrawText(g, s, f, r, c, fl); }

    private void Link(Rectangle r, Action act) => hits.Add(new Hit(r, act));

    /// <summary>쪽 전체를 그린다(draw=false면 높이와 눌리는 자리만 잰다). 높이를 돌려준다.</summary>
    private int Render(Graphics g, bool draw)
    {
        hits.Clear();
        drawOffset = draw ? offset : 0;
        var pal = p;
        int W = ClientSize.Width - (bar.Visible ? bar.Width : 0);
        int colW = Math.Min(W - S(96), S(1140));
        int left = Math.Max(S(24), (W - colW) / 2);
        if (colW < S(200)) colW = Math.Max(S(120), W - S(48));
        int right = left + colW;
        Color fore = pal?.Fore ?? ForeColor, dim = pal?.Dim ?? Color.Gray, accent = pal?.Accent ?? Color.MediumPurple, main = pal?.Main ?? BackColor;
        Color line = Themes.Blend(main, fore, 0.14f), chip = Themes.Blend(main, fore, 0.07f);
        using var small = new Font(Font.FontFamily, 10f);
        using var big = new Font(Font.FontFamily, 30f, FontStyle.Bold);
        using var h2 = new Font(Font.FontFamily, 18f, FontStyle.Bold);
        using var icon = new Font(MainForm.IconFontName(), 12f);
        using var itemIcon = new Font(MainForm.IconFontName(), 10f);
        using var iconBig = new Font(MainForm.IconFontName(), 30f);
        const TextFormatFlags F = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        if (draw)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(0, -offset);
            using var grad = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, W), S(250)), Themes.Blend(main, accent, pal?.Dark == true ? 0.14f : 0.10f), main, 90f);
            g.FillRectangle(grad, 0, 0, W, S(250));
        }

        // 경로: 작품 › (대분류) › (상위 분류 …) › 지금 곳
        int y = S(28);
        var crumbs = new List<(string Text, Action? Act)> { (data.NovelName, () => BackRequested?.Invoke()) };
        if (kind == "category") crumbs.Add(("대분류:" + WikiCategory.Find(key).Label, null));
        else
        {
            string catKey = data.CategoryOf(key);
            string c0 = catKey;
            crumbs.Add((WikiCategory.Find(catKey).Label, () => GroupChosen?.Invoke("category", c0)));
            var chain = new List<string>();
            string cur = key;
            for (int guard = 0; guard < 16; guard++) { var ps = data.ParentsOf(cur); if (ps.Count == 0) break; cur = ps[0]; chain.Insert(0, cur); }
            foreach (var up in chain) { string u = up; crumbs.Add((u, () => GroupChosen?.Invoke("class", u))); }
            crumbs.Add((key, null));
        }
        int x = left;
        for (int i = 0; i < crumbs.Count; i++)
        {
            var (text, act) = crumbs[i];
            int w = TextRenderer.MeasureText(text, small, Size.Empty, TextFormatFlags.NoPadding).Width;
            var r = new Rectangle(x, y, w + S(2), S(24));
            if (draw) Txt(g, text, small, r, act == null ? fore : dim, F);
            if (act != null) Link(r, act);
            x += w + S(8);
            if (i < crumbs.Count - 1)
            {
                if (draw) Txt(g, "›", small, new Rectangle(x, y, S(12), S(24)), dim, F | TextFormatFlags.HorizontalCenter);
                x += S(20);
            }
        }

        // 제목: 아이콘 상자 + "대분류: 등장인물"
        y += S(40);
        int box = S(84);
        var cat = WikiCategory.Find(kind == "category" ? key : data.CategoryOf(key));
        string head = kind == "category" ? "대분류:" : "분류:", name = kind == "category" ? cat.Label : key;
        if (draw)
        {
            Shapes.FillRounded(g, new Rectangle(left, y, box, box), S(14), chip);
            Txt(g, kind == "category" ? cat.Glyph : "\uE8B7", iconBig, new Rectangle(left, y, box, box), dim, F | TextFormatFlags.HorizontalCenter);
            int hw = TextRenderer.MeasureText(head, big, Size.Empty, TextFormatFlags.NoPadding).Width;
            var tr = new Rectangle(left + box + S(24), y, right - left - box - S(24), box);
            Txt(g, head, big, tr, dim, F);
            Txt(g, " " + name, big, new Rectangle(tr.X + hw, tr.Y, Math.Max(S(40), tr.Width - hw), tr.Height), fore, F);
        }
        y += box + S(40);

        // 분류 쪽의 속성 줄: 위키 문서의 속성처럼 — 요약 한 줄, [대분류 · 분류 · 중요도] 한 줄, 속성 표, [속성 추가하기], [문서 속성 접기]
        propRects.Clear();
        summaryRow = Rectangle.Empty;
        if (kind == "class")
        {
            int labelW = S(104), vx = left + labelW, valW = Math.Max(S(160), right - vx);
            void Label(int lx, int ly, string label, string glyph)
            {
                if (!draw) return;
                Txt(g, glyph, icon, new Rectangle(lx, ly, S(24), S(44)), dim, F | TextFormatFlags.HorizontalCenter);
                Txt(g, label, Font, new Rectangle(lx + S(32), ly, S(68), S(44)), dim, F);
            }
            void Cross(Rectangle after, Action act)
            {
                var cr = new Rectangle(after.Right + S(12), after.Y + after.Height / 2 - S(12), S(24), S(24));
                if (draw)
                {
                    float cx = cr.X + cr.Width / 2f, cy = cr.Y + cr.Height / 2f, d = 4f * DeviceDpi / 96f;
                    using var pen = new Pen(fore, Math.Max(1.2f, 1.3f * DeviceDpi / 96f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    Shapes.Cross(g, pen, cx, cy, d);
                }
                Link(cr, act);
            }
            if (!propsCollapsed)
            {
                // 요약: 마우스를 올리면 칸 바탕과 끝의 ×가 보인다
                int sh = summaryBox.PreferredHeight, rowH = Math.Max(S(44), sh + S(12));
                Label(left, y, "요약", "");
                var sr = propRects[summaryBox] = new Rectangle(vx, y + (rowH - sh) / 2, valW - S(60), sh);
                summaryRow = new Rectangle(left, y, right - left, rowH);
                y += rowH;

                // 대분류 · 분류 · 중요도를 한 줄에
                int grp = (right - left) / 3, lw = S(104), pw = Math.Max(S(80), grp - lw - S(16));
                int h1 = categoryBox.Height, hs = stars.Height, h3 = Math.Max(classes.FitHeight(pw), S(26));
                Label(left, y, "대분류", "");
                propRects[categoryBox] = new Rectangle(left + lw, y + (S(44) - h1) / 2, Math.Min(pw, S(200)), h1);
                Label(left + grp, y, "분류", "");
                propRects[classes] = new Rectangle(left + grp + lw, y + (S(44) - Math.Min(h3, S(26))) / 2, pw, h3);
                Label(left + grp * 2, y, "중요도", "");
                propRects[stars] = new Rectangle(left + grp * 2 + lw, y + (S(44) - hs) / 2, stars.Width, hs);
                y += Math.Max(S(44), h3 + S(12));

                // 태그
                int th = Math.Max(tagStrip.FitHeight(valW), S(26)), trow = Math.Max(S(44), th + S(12));
                Label(left, y, "태그", "\uE8EC");
                propRects[tagStrip] = new Rectangle(vx, y + (trow - th) / 2, valW, th);
                y += trow + S(8);
            }
            var tr0 = new Rectangle(left, y, S(220), S(40));
            if (draw)
            {
                DrawChevron(g, new Rectangle(left, y, S(24), S(40)), propsCollapsed, dim, up: !propsCollapsed);
                Txt(g, propsCollapsed ? "문서 속성 펼치기" : "문서 속성 접기", Font, new Rectangle(left + S(32), y, S(190), S(40)), dim, F);
            }
            Link(tr0, () => { propsCollapsed = !propsCollapsed; Relayout(); });
            y += S(60);
        }
        else y += S(8);

        // 하위 문서
        var items = Items();
        var headRect = new Rectangle(left, y, S(260), S(44));
        if (draw)
        {
            DrawChevron(g, new Rectangle(left, y, S(24), S(44)), !listCollapsed, fore);
            Txt(g, "하위 문서", h2, new Rectangle(left + S(32), y, S(160), S(44)), fore, F);
            int hw = TextRenderer.MeasureText("하위 문서", h2, Size.Empty, TextFormatFlags.NoPadding).Width;
            Txt(g, $"{items.Count}개 문서", small, new Rectangle(left + S(32) + hw + S(14), y, S(140), S(44)), dim, F);
        }
        Link(headRect, () => { listCollapsed = !listCollapsed; Relayout(); });
        y += S(52);
        if (draw) { using var pen = new Pen(line); g.DrawLine(pen, left, y, right, y); }
        y += S(14);
        if (listCollapsed) return y + S(60);
        if (items.Count == 0)
        {
            if (draw) Txt(g, "아직 하위 문서가 없어요.", Font, new Rectangle(left, y + S(14), right - left, S(30)), dim, F);
            return y + S(90);
        }

        var groups = items.GroupBy(i => Initial(i.SortKey)).OrderBy(gr => InitialOrder(gr.Key)).ToList();
        int cols = Math.Clamp(colW / S(280), 1, 3);
        int gap = S(32), cw = (colW - gap * (cols - 1)) / cols;
        var colY = new int[cols];
        for (int c = 0; c < cols; c++) colY[c] = y;
        int gi = 0;
        foreach (var grp in groups)
        {
            int c = gi % cols; gi++;
            int gx = left + c * (cw + gap), gy = colY[c];
            string gk = grp.Key;
            bool closed = collapsed.Contains(gk);
            var hr = new Rectangle(gx, gy, cw, S(40));
            if (draw)
            {
                DrawChevron(g, new Rectangle(gx, gy, S(22), S(40)), !closed, fore);
                Txt(g, gk, Font, new Rectangle(gx + S(30), gy, S(30), S(40)), accent, F | TextFormatFlags.HorizontalCenter);
                Txt(g, $"{grp.Count()}개", Font, new Rectangle(gx + S(66), gy, cw - S(66), S(40)), dim, F);
                using var pen = new Pen(line); g.DrawLine(pen, gx, gy + S(44), gx + cw, gy + S(44));
            }
            Link(hr, () => { if (!collapsed.Remove(gk)) collapsed.Add(gk); Relayout(); });
            gy += S(56);
            if (!closed)
                foreach (var it in grp.OrderBy(i => i.SortKey, NaturalStringComparer.Instance))
                {
                    var r = new Rectangle(gx, gy, cw, S(40));
                    int idx = hits.Count;
                    if (draw)
                    {
                        if (hover == idx) Shapes.FillRounded(g, r, S(8), chip);
                        int av = S(30);
                        using (var b = new SolidBrush(Themes.Blend(main, fore, 0.12f))) g.FillEllipse(b, gx + S(4), gy + (S(40) - av) / 2, av, av);
                        Txt(g, it.DocPath != null ? it.Cat.Glyph : "\uE8B7", itemIcon, new Rectangle(gx + S(4), gy, av, S(40)), dim, F | TextFormatFlags.HorizontalCenter);
                        Txt(g, it.Label, Font, new Rectangle(gx + S(46), gy, cw - S(52), S(40)), fore, F);
                    }
                    string? dp = it.DocPath, cn = it.ClassName;
                    Link(r, () => { if (dp != null) DocChosen?.Invoke(dp); else if (cn != null) GroupChosen?.Invoke("class", cn); });
                    gy += S(44);
                }
            colY[c] = gy + S(20);
        }
        return colY.Max() + S(40);
    }

    private void DrawChevron(Graphics g, Rectangle r, bool down, Color c, bool up = false)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = DeviceDpi / 96f;
        using var pen = new Pen(c, Math.Max(1.6f, 1.8f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        Shapes.Chevron(g, pen, cx, cy, 5 * u, 2.5f * u, up ? Arrow.Up : down ? Arrow.Down : Arrow.Right);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (p == null) return;
        Render(e.Graphics, true);
    }

    private int HitAt(Point pt)
    {
        var q = new Point(pt.X, pt.Y + offset);
        for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(q)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = HitAt(e.Location);
        if (i != hover) { hover = i; Invalidate(); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        int i = HitAt(e.Location);
        if (i < 0) return;
        var act = hits[i].Act;
        BeginInvoke(act);
    }
}
