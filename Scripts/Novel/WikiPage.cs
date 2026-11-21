using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Cafeum;

/// <summary>
/// 위키 문서 화면(Muvel의 위키 문서처럼):
/// [‹ 돌아가기]                                         [⋯]
/// 작품 › 등장인물 › 이민혁
/// (●) 이민혁                     ← 카테고리 아이콘이 든 동그라미 + 큰 제목
/// ⌄ 문서 속성 펼치기             ← 카테고리 · 중요도(별 5개) · 설명 · 분류(목록에서 고르기) · 태그
/// [목차 ─ ## 제목들]  [속성 표 ─ 항목 · 값, 항목은 목록에서 고르거나 새로 추가]
/// 본문(## · ### · #### 로 시작하는 줄이 목차가 된다. 번호는 1. · 1.1 · 1.1.1)
/// [수정 | 보기] 스위치(⋯ 왼쪽)로 고치는 모드와 읽기만 하는 모드를 오간다.
/// 고치면 잠깐 뒤 저절로 저장한다.
/// </summary>
internal sealed class WikiPage : Panel
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int EM_SETMARGINS = 0xD3, EC_LEFTMARGIN = 1, EC_RIGHTMARGIN = 2;

    private readonly Panel topBar, head;
    private readonly PillButton backButton, moreButton;
    private readonly ModeSwitch modeSwitch;
    private readonly Label crumbLabel, titleLabel, propsToggle;
    private readonly Panel props;
    private readonly List<(Label Label, Control Ctl)> propRows = new();
    private readonly ComboBox categoryBox;
    private readonly StarBox stars;
    private readonly TextBox summaryBox;
    private readonly Panel summaryHost; // 요약 값 칸: 흰 띠 안에 테두리 없는 입력칸(비어 있으면 "비어 있음")
    private Label? summaryLabel;
    private readonly ClassStrip classes;
    private readonly TagStrip tagStrip;
    private readonly TocBox toc;
    private readonly AttrBox attrs;
    private readonly HeadingEditor body;
    private readonly Label bodyHint;
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 1000 };
    private LibraryPalette? palette;
    private WikiDoc? doc;
    private string novelName = "", novelDir = "";
    private bool loading, propsOpen, editable = true, layingOut;

    /// <summary>새로 여는 위키 문서가 처음부터 보기 모드인가(스위치를 마지막으로 둔 쪽을 이어 간다).</summary>
    private static bool defaultViewMode;
    /// <summary>보기 모드(읽기만 한다)인가.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool ViewMode { get => !editable; set { if (value == !editable) return; defaultViewMode = value; SetEditable(!value); ViewModeChanged?.Invoke(value); } }
    /// <summary>수정/보기가 바뀌었다(칸 머리의 스위치가 따라가도록).</summary>
    public event Action<bool>? ViewModeChanged;

    public event Action? BackRequested;
    /// <summary>경로의 대분류를 눌렀다(대분류 키) — 그 대분류의 쪽을 열라는 요청.</summary>
    public event Action<string>? CategoryRequested;
    /// <summary>[‹ 돌아가기] · [⋯] 줄을 보일지(워크스페이스 칸 안에서는 칸 머리가 그 일을 한다).</summary>
[System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowTopBar { get => topBar.Visible; set { topBar.Visible = value; LayoutScroll(); } }
    /// <summary>칸 머리의 ⋯에서 부르는 문서 메뉴(이름 바꾸기 · 탐색기 · 휴지통).</summary>
    public void ShowMenuAt(Control anchor, Point at) { if (doc == null) return; ShowMoreMenu(); moreMenu?.Show(anchor, at); }
    /// <summary>저장했거나 이름 · 카테고리가 바뀌었다(목록을 다시 그리라고).</summary>
    public event Action? Changed;
    public string? DocPath => doc?.Path;

    public WikiPage()
    {
        string ff = NovelStore.UiFontFamily;
        DoubleBuffered = true;

        backButton = new PillButton("‹  돌아가기") { Compact = true, Font = new Font(ff, 9.5f) };
        backButton.Click += (_, _) => BackRequested?.Invoke();
        moreButton = new PillButton("⋯") { Compact = true, Font = new Font(ff, 10f, FontStyle.Bold) };
        moreButton.Click += (_, _) => ShowMoreMenu();
        topBar = new Panel { Dock = DockStyle.Top };
        modeSwitch = new ModeSwitch();
        modeSwitch.EditingChanged += () => ViewMode = !modeSwitch.Editing;
        topBar.Controls.AddRange(new Control[] { backButton, modeSwitch, moreButton });
        topBar.Layout += (_, _) =>
        {
            topBar.Height = backButton.Height + S(20);
            backButton.Location = new Point(S(16), S(10));
            moreButton.Location = new Point(topBar.ClientSize.Width - moreButton.Width - S(16), S(10));
            modeSwitch.Location = new Point(moreButton.Left - modeSwitch.Width - S(8), S(10) + (backButton.Height - modeSwitch.Height) / 2); // ⋯ 왼쪽
        };

        crumbLabel = new Label { AutoSize = true, Font = new Font(ff, 9.5f), UseMnemonic = false };
        crumbLabel.MouseMove += (_, e) => { int h = CrumbAt(e.Location); if (h != crumbHot) { crumbHot = h; crumbLabel.Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; } };
        crumbLabel.MouseLeave += (_, _) => { crumbHot = -1; crumbLabel.Cursor = Cursors.Default; };
        crumbLabel.MouseClick += (_, e) => { if (e.Button != MouseButtons.Left) return; int h = CrumbAt(e.Location); if (h == 0) BackRequested?.Invoke(); else if (h == 1 && doc != null) CategoryRequested?.Invoke(doc.Category); };
        titleLabel = new Label { AutoSize = false, AutoEllipsis = true, Font = new Font(ff, 24f, FontStyle.Bold), UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
        titleLabel.DoubleClick += (_, _) => { if (editable) Rename(); };
        propsToggle = new Label { AutoSize = true, Font = new Font(ff, 9.5f), Cursor = Cursors.Hand, UseMnemonic = false };
        propsToggle.Click += (_, _) => { propsOpen = !propsOpen; SyncPropsToggle(); head.PerformLayout(); };

        categoryBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Font = new Font(ff, 9.5f) };
        foreach (var c in WikiCategory.All) categoryBox.Items.Add(c.Label);
        stars = new StarBox();
        summaryBox = new TextBox { Font = new Font(ff, 9.5f), BorderStyle = BorderStyle.None, PlaceholderText = "비어 있음" };
        summaryHost = new Panel { Cursor = Cursors.IBeam };
        summaryHost.Controls.Add(summaryBox);
        summaryHost.Resize += (_, _) => LayoutSummaryHost();
        summaryHost.Click += (_, _) => { if (editable) summaryBox.Focus(); };
        classes = new ClassStrip();
        tagStrip = new TagStrip();
        props = new Panel { Visible = false };
        void Row(string label, Control c)
        {
            var l = new IconLabel { Text = label, Font = new Font(ff, 9.5f) };
            propRows.Add((l, c));
            props.Controls.Add(l);
            props.Controls.Add(c);
        }
        Row("대분류", categoryBox);
        Row("중요도", stars);
        Row("요약", summaryHost);
        summaryLabel = propRows[^1].Label;
        Row("분류", classes);
        Row("태그", tagStrip);
        props.Layout += (_, _) => { if (!layingOut) head.PerformLayout(); }; // 태그 입력칸이 열리고 닫힐 때 높이를 다시 잡는다
        categoryBox.SelectedIndexChanged += (_, _) => { if (!loading && doc != null) { doc.Category = WikiCategory.All[Math.Max(0, categoryBox.SelectedIndex)].Key; SyncCrumb(); head.Invalidate(); SaveSoon(); } };
        stars.ValueChanged += v => { if (!loading && doc != null) { doc.Importance = v; SaveSoon(); } };
        summaryBox.TextChanged += (_, _) => { if (!loading && doc != null) { doc.Summary = summaryBox.Text.Trim(); SaveSoon(); } };
        classes.AddRequested += PickClass;
        classes.RemoveRequested += name => { if (doc == null || !editable) return; doc.Tags.RemoveAll(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)); classes.SetItems(doc.Tags); head.PerformLayout(); SaveSoon(); };
        tagStrip.TagSubmitted += t => { if (doc == null || !editable || doc.Labels.Contains(t, StringComparer.OrdinalIgnoreCase)) return; doc.Labels.Add(t); tagStrip.SetTags(doc.Labels); head.PerformLayout(); SaveSoon(); };
        tagStrip.RemoveRequested += t => { if (doc == null || !editable) return; doc.Labels.RemoveAll(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)); tagStrip.SetTags(doc.Labels); head.PerformLayout(); SaveSoon(); };

        toc = new TocBox();
        toc.HeadingChosen += offset => { headOffset = head.Height; LayoutScroll(); body.Focus(); body.Select(Math.Min(offset, body.TextLength), 0); body.ScrollToCaret(); };
        attrs = new AttrBox();
        attrs.AddRequested += PickAttribute;
        attrs.ValueChanged += (i, v) => { if (doc == null || !editable || i < 0 || i >= doc.Attributes.Count) return; doc.Attributes[i] = (doc.Attributes[i].Key, v); attrs.SetItems(doc.Attributes); head.PerformLayout(); SaveSoon(); };
        attrs.RowMoved += (from, to) =>
        {
            if (doc == null || !editable || from < 0 || from >= doc.Attributes.Count) return;
            to = Math.Clamp(to, 0, doc.Attributes.Count - 1);
            if (to == from) return;
            var it = doc.Attributes[from];
            doc.Attributes.RemoveAt(from);
            doc.Attributes.Insert(to, it); // 속성을 끌어서 순서를 바꾼다
            attrs.SetItems(doc.Attributes);
            SaveSoon();
        };
        attrs.ColorChanged += hex => { if (doc == null || !editable) return; doc.AttrColor = hex; attrs.AttrColor = hex; SaveSoon(); };
        attrs.RemoveRequested += i => { if (doc == null || !editable || i < 0 || i >= doc.Attributes.Count) return; doc.Attributes.RemoveAt(i); attrs.SetItems(doc.Attributes); head.PerformLayout(); SaveSoon(); };

        head = new Panel(); // 위치는 LayoutScroll이 잡는다(본문과 함께 위로 밀려 올라간다)
        head.Controls.AddRange(new Control[] { crumbLabel, titleLabel, propsToggle, props, toc, attrs });
        head.Layout += (_, _) => LayoutHead();
        head.Paint += PaintHead;
        head.MouseMove += (_, e) => head.Cursor = editable && doc != null && PhotoRect().Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        head.MouseClick += (_, e) => OnHeadClick(e);

        // 본문: "## " · "### "를 치면 그 줄이 바로 제목으로 바뀌는 글 상자(파일에는 ## · ###로 저장)
        body = new HeadingEditor();
        body.WheelIntercept += delta => ScrollHead(delta);
        body.HeadingsChanged += heads => { if (!loading) SetToc(heads); }; // 항목을 넣고 빼면 목차 번호도 바로
        body.ContentChanged += () => { if (loading || doc == null) return; bodyHint.Visible = editable && body.TextLength == 0 && !body.PendingHeading; SaveSoon(); };
        body.SelectionChanged += (_, _) => { if (!loading && doc != null) bodyHint.Visible = editable && body.TextLength == 0 && !body.PendingHeading; };
        body.HandleCreated += (_, _) => ApplyBodyMargin();
        body.SizeChanged += (_, _) => ApplyBodyMargin();
        bodyHint = new Label { Text = "## · ### · #### 를 치고 띄어 쓰면 항목 · 부항목 · 세부항목이 돼요. 위의 목차에 번호(1. · 1.1 · 1.1.1)와 함께 모여요.", AutoSize = true, UseMnemonic = false, Cursor = Cursors.IBeam, Font = new Font(ff, 11f) };
        bodyHint.Click += (_, _) => body.Focus();
        body.Controls.Add(bodyHint);

        Controls.Add(body);
        Controls.Add(head);
        Controls.Add(topBar);
        topBar.BringToFront(); // 위로 밀려 올라간 머리 부분이 위 줄을 덮지 않게

        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        Disposed += (_, _) => { saveTimer.Dispose(); photo?.Dispose(); };
        VisibleChanged += (_, _) => { if (!Visible) Save(); };
        SyncPropsToggle();
        if (defaultViewMode) { editable = false; ApplyEditable(); modeSwitch.Editing = false; }
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int ColW => Math.Min(ClientSize.Width - S(48) * 2, S(800));
    private int ColLeft => Math.Max(S(24), (ClientSize.Width - ColW) / 2);

    private void SyncPropsToggle() { propsToggle.Text = propsOpen ? "⌃  문서 속성 접기" : "⌄  문서 속성 펼치기"; props.Visible = propsOpen; }

    /// <summary>머리에 보이는 이름: 템플릿은 앞에 "템플릿: "이 붙는다.</summary>
    private static string TitleText(WikiDoc d) => WikiStore.IsTemplatePath(d.Path) ? "템플릿: " + d.Title : d.Title;

    private int crumbHot = -1;

    /// <summary>경로 글자에서 x 자리가 [작품 이름](0) · [대분류](1)인지, 아니면 -1(문서 이름은 누를 곳이 아니다).</summary>
    private int CrumbAt(Point pt)
    {
        if (doc == null) return -1;
        string sep = "  ›  ";
        const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        int W(string s) => TextRenderer.MeasureText(s, crumbLabel.Font, Size.Empty, F).Width;
        string a = novelName, b = WikiCategory.Find(doc.Category).Label;
        int a0 = 0, a1 = W(a);
        int b0 = a1 + W(sep) - W(""), b1 = b0 + W(b);
        // 글자 사이 공백은 측정에서 줄어들 수 있어 넉넉하게(바깥으로 2px)
        if (pt.X >= a0 - 2 && pt.X <= a1 + 2) return 0;
        if (pt.X >= b0 - 2 && pt.X <= b1 + 2) return 1;
        return -1;
    }

    private void SyncCrumb()
    {
        if (doc == null) return;
        crumbLabel.Text = $"{novelName}  ›  {WikiCategory.Find(doc.Category).Label}  ›  {doc.Title}";
    }

    /// <summary>속성 칸(카테고리 · 중요도 · 설명 · 분류 · 태그) 줄들을 차례로 놓고 전체 높이를 돌려준다. 보기 모드에서 빈 줄은 감춘다.</summary>
    private void LayoutSummaryHost()
    {
        int th = summaryBox.PreferredHeight;
        summaryBox.SetBounds(S(10), Math.Max(0, (summaryHost.Height - th) / 2), Math.Max(10, summaryHost.Width - S(20)), th);
    }

    /// <summary>요약 줄 앞의 작은 아이콘(글줄이 든 카드 모양).</summary>
    private Bitmap SummaryIcon(Color c)
    {
        int n = S(18);
        var bmp = new Bitmap(n, n);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(c, Math.Max(1.2f, n / 13f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        float u = n / 18f;
        g.DrawRectangle(pen, 2f * u, 3f * u, 14f * u, 12f * u);
        g.DrawLine(pen, 4.5f * u, 6.5f * u, 13.5f * u, 6.5f * u);
        g.DrawLine(pen, 4.5f * u, 9.5f * u, 8.5f * u, 9.5f * u);
        g.DrawLine(pen, 4.5f * u, 12f * u, 8.5f * u, 12f * u);
        g.DrawRectangle(pen, 10.5f * u, 9.5f * u, 3f * u, 3f * u);
        return bmp;
    }

    private int LayoutProps(int w)
    {
        int lw = S(78), gap = S(24), lx = lw + S(8);
        int y = 0;
        (Label L, Control C) Pair(Control c) { var r = propRows.First(x => x.Ctl == c); return (r.Label, r.Ctl); }
        void Place(Label l, Control c, int x, int rowY, int rowH, int h, int cwid)
        {
            l.Location = new Point(x, rowY + (rowH - l.Height) / 2);
            c.SetBounds(x + lx, rowY + (rowH - h) / 2, cwid, h);
        }
        // 1) 요약
        var (sl, sc) = Pair(summaryHost);
        bool sumShow = editable || summaryBox.Text.Length > 0;
        sl.Visible = sc.Visible = sumShow;
        if (sumShow)
        {
            int sh = Math.Max(S(34), summaryBox.PreferredHeight + S(14));
            int srh = Math.Max(sh, sl.Height) + S(10);
            Place(sl, sc, 0, y, srh, sh, Math.Max(S(120), w - lx));
            y += srh;
        }
        // 2) 대분류 · 분류 · 중요도: 넓으면 한 줄에, 좁으면 차례로
        var (cl, cc) = Pair(categoryBox);
        var (ml, mc) = Pair(classes);
        var (tl, tc) = Pair(stars);
        cl.Visible = cc.Visible = tl.Visible = tc.Visible = true;
        bool oneLine = w >= S(760);
        int catG = lx + categoryBox.Width, starG = lx + stars.Width;
        int clsW = oneLine ? Math.Max(S(120), w - catG - starG - 2 * gap - lx) : Math.Max(S(120), w - lx);
        int clsH = classes.FitHeight(clsW);
        bool clsShow = clsH > 0;
        ml.Visible = mc.Visible = clsShow;
        if (clsShow) classes.Width = clsW;
        if (oneLine)
        {
            int rh = Math.Max(Math.Max(categoryBox.Height, stars.Height), Math.Max(clsShow ? clsH : 0, cl.Height)) + S(10);
            int x = 0;
            Place(cl, cc, x, y, rh, categoryBox.Height, categoryBox.Width); x += catG + gap;
            if (clsShow) { Place(ml, mc, x, y, rh, clsH, clsW); x += lx + clsW + gap; }
            Place(tl, tc, x, y, rh, stars.Height, stars.Width);
            y += rh;
        }
        else
        {
            int r1 = Math.Max(categoryBox.Height, cl.Height) + S(10);
            Place(cl, cc, 0, y, r1, categoryBox.Height, categoryBox.Width); y += r1;
            if (clsShow) { int r2 = Math.Max(clsH, ml.Height) + S(10); Place(ml, mc, 0, y, r2, clsH, clsW); y += r2; }
            int r3 = Math.Max(stars.Height, tl.Height) + S(10);
            Place(tl, tc, 0, y, r3, stars.Height, stars.Width); y += r3;
        }
        // 3) 태그
        var (gl, gc) = Pair(tagStrip);
        int tagW = Math.Max(S(120), w - lx);
        int tagH = tagStrip.FitHeight(tagW);
        bool tagShow = tagH > 0;
        gl.Visible = gc.Visible = tagShow;
        if (tagShow)
        {
            tagStrip.Width = tagW;
            int trh = Math.Max(tagH, gl.Height) + S(10);
            Place(gl, gc, 0, y, trh, tagH, tagW);
            y += trh;
        }
        return y;
    }

    /// <summary>속성 줄 이름 앞의 작은 선 아이콘: 요약 · 대분류 · 분류 · 중요도 · 태그.</summary>
    private Bitmap RowIcon(string key, Color c)
    {
        if (key == "요약") return SummaryIcon(c);
        int n = S(18);
        var bmp = new Bitmap(n, n);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = n / 18f;
        using var pen = new Pen(c, Math.Max(1.2f, n / 13f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        switch (key)
        {
            case "대분류": // 점 · 줄 셋
                for (int k = 0; k < 3; k++)
                {
                    float yy = (4f + k * 5f) * u;
                    g.DrawLine(pen, 2f * u, yy, 2.5f * u, yy);
                    g.DrawLine(pen, 6f * u, yy, 16f * u, yy);
                }
                break;
            case "분류": // 폴더
                g.DrawLines(pen, new[] { new PointF(2f * u, 14.5f * u), new PointF(2f * u, 4f * u), new PointF(7f * u, 4f * u), new PointF(8.8f * u, 6f * u), new PointF(16f * u, 6f * u), new PointF(16f * u, 14.5f * u), new PointF(2f * u, 14.5f * u) });
                break;
            case "중요도": // 별(테두리)
            {
                var pts = new PointF[10];
                float cx = 9f * u, cy = 9.5f * u;
                for (int k = 0; k < 10; k++)
                {
                    double ang = -Math.PI / 2 + k * Math.PI / 5;
                    float rr = (k % 2 == 0 ? 7.5f : 3.2f) * u;
                    pts[k] = new PointF(cx + (float)Math.Cos(ang) * rr, cy + (float)Math.Sin(ang) * rr);
                }
                g.DrawPolygon(pen, pts);
                break;
            }
            default: // 태그
                g.DrawPolygon(pen, new[] { new PointF(2.5f * u, 3f * u), new PointF(9f * u, 3f * u), new PointF(15.5f * u, 9.5f * u), new PointF(9.5f * u, 15.5f * u), new PointF(2.5f * u, 8.5f * u) });
                g.DrawEllipse(pen, 5f * u, 5.5f * u, 2f * u, 2f * u);
                break;
        }
        return bmp;
    }

    private void LayoutHead()
    {
        if (layingOut) return;
        layingOut = true;
        try
        {
            int x = ColLeft, w = Math.Max(S(200), ColW), y = S(14);
            crumbLabel.Location = new Point(x, y);
            y = crumbLabel.Bottom + S(14);
            int circle = S(56);
            titleLabel.SetBounds(x + circle + S(18), y, w - circle - S(18), circle);
            y += circle + S(16);
            propsToggle.Location = new Point(x, y);
            y = propsToggle.Bottom + S(10);
            if (props.Visible) { int ph = LayoutProps(w); props.SetBounds(x, y, w, ph); y = props.Bottom + S(10); }
            y += S(16);
            attrs.Visible = editable || attrs.Count > 0; // 보기 모드에서 속성이 하나도 없으면 상자를 감춘다
            int half = (w - S(24)) / 2;
            if (!attrs.Visible)
            {
                toc.SetBounds(x, y, half >= S(240) ? half : w, toc.PreferredHeight(half >= S(240) ? half : w));
                y = toc.Bottom + S(24);
            }
            else if (half >= S(240))
            {
                toc.SetBounds(x, y, half, toc.PreferredHeight(half));
                int aw = (int)(half * 0.8); // 속성창 폭은 전의 80%(사진 폭과 같다), 오른쪽 끝에 맞춘다
                attrs.SetBounds(x + w - aw, y, aw, attrs.PreferredHeight(aw));
                y = Math.Max(toc.Bottom, attrs.Bottom) + S(24);
            }
            else
            {
                // 칸이 좁으면(워크스페이스에서 나눠 볼 때) 목차 밑에 속성 상자를 쌓는다
                toc.SetBounds(x, y, w, toc.PreferredHeight(w));
                int aw2 = (int)(w * 0.8);
                attrs.SetBounds(x, toc.Bottom + S(12), aw2, attrs.PreferredHeight(aw2));
                y = attrs.Bottom + S(24);
            }
            if (head.Height != y) head.Height = y;
        }
        finally { layingOut = false; }
        LayoutScroll();
    }

    /// <summary>머리(속성 · 목차)가 위로 밀려 올라간 높이(px). 본문을 내리면 먼저 머리가 올라가고, 다 올라간 뒤에 본문이 스크롤된다.</summary>
    private int headOffset;
    private bool scrollLaying;
    private AttrPopup? attrPop;

    private void LayoutScroll()
    {
        if (scrollLaying || head == null || body == null) return;
        scrollLaying = true;
        try
        {
            int top = topBar.Visible ? topBar.Height : 0, w = ClientSize.Width;
            if (head.Width != w) head.Width = w; // 폭이 바뀌면 머리 배치가 다시 돌아 높이가 달라질 수 있다
            headOffset = Math.Clamp(headOffset, 0, head.Height);
            int bodyTop = top + head.Height - headOffset;
            head.SetBounds(0, top - headOffset, w, head.Height);
            body.SetBounds(0, bodyTop, w, Math.Max(0, ClientSize.Height - bodyTop));
        }
        finally { scrollLaying = false; }
        if (attrPop != null && !attrPop.IsDisposed) attrPop.MoveAnchor(attrs.AddAnchorScreen); // 속성 추가 창이 "+ 속성 추가" 밑을 따라다니게
    }

    /// <summary>휠로 delta만큼 굴렸다. 머리가 아직 덜 올라갔으면(내릴 때) 또는 본문이 맨 위이고 머리가 올라가 있으면(올릴 때) 머리를 움직이고 true를 돌려준다.</summary>
    private bool ScrollHead(int delta)
    {
        int step = (int)Math.Round(Math.Abs(delta) / 120.0 * S(60));
        if (step <= 0) return false;
        if (delta < 0 && headOffset < head.Height) headOffset += step;
        else if (delta > 0 && headOffset > 0 && body.ScrollY <= 0) headOffset -= step;
        else return false;
        LayoutScroll();
        return true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!ScrollHead(e.Delta)) base.OnMouseWheel(e);
    }

    private Bitmap? photo;

    /// <summary>문서 사진을 바꾼다: 이름 옆 동그라미와 속성 상자(이름 밑)가 같은 그림을 쓴다. 옛 그림은 쓰는 곳에서 떼어 낸 뒤 버린다.</summary>
    private void SetPhotoImage(Bitmap? next)
    {
        attrs.Photo = null;
        photo?.Dispose();
        photo = next;
        attrs.Photo = photo;
        head.PerformLayout(); // 속성 상자 높이가 사진만큼 달라진다
        head.Invalidate();
    }

    /// <summary>이름 옆 동그라미(문서 사진 자리).</summary>
    private Rectangle PhotoRect() => new(ColLeft, titleLabel.Top, S(56), S(56));

    private void OnHeadClick(MouseEventArgs e)
    {
        if (doc == null || !editable || !PhotoRect().Contains(e.Location)) return;
        if (e.Button == MouseButtons.Left) ChoosePhoto();
        else if (e.Button == MouseButtons.Right)
        {
            var m = new ContextMenuStrip();
            MenuGuard.Attach(m);
            m.Items.Add("사진 바꾸기…", null, (_, _) => BeginInvoke(ChoosePhoto));
            if (photo != null) m.Items.Add("사진 지우기", null, (_, _) => BeginInvoke(() => { if (doc == null) return; WikiStore.DeletePhoto(doc.Path); SetPhotoImage(null); Changed?.Invoke(); }));
            m.Closed += (_, _) => BeginInvoke(m.Dispose);
            m.Show(Cursor.Position);
        }
    }

    /// <summary>동그라미를 누르면 그림 파일을 골라 이 문서의 사진으로 넣는다.</summary>
    private void ChoosePhoto()
    {
        if (doc == null) return;
        using var dlg = new OpenFileDialog { Title = "문서 사진 고르기", Filter = "그림 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif|모든 파일|*.*" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            using var cropDlg = new PhotoCropDialog(dlg.FileName);
            if (cropDlg.ShowDialogOver(FindForm()) != DialogResult.OK) return;
            WikiStore.SetPhoto(doc.Path, dlg.FileName, cropDlg.CropRect);
            SetPhotoImage(WikiStore.LoadPhoto(doc.Path));
            Changed?.Invoke(); // 마인드맵 등 다른 보기에도 사진이 나타나게
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), $"사진을 넣지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void PaintHead(object? sender, PaintEventArgs e)
    {
        var p = palette;
        if (p == null || doc == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int circle = S(56);
        var r = new Rectangle(ColLeft, titleLabel.Top, circle, circle);
        using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, p.Dark ? 0.14f : 0.08f))) g.FillEllipse(b, r);
        if (photo != null)
        {
            using var clip = new GraphicsPath();
            clip.AddEllipse(r);
            var saved = g.Save();
            g.SetClip(clip, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(photo, r);
            g.Restore(saved);
            return;
        }
        using var f = new Font(MainForm.IconFontName(), 18f);
        TextRenderer.DrawText(g, WikiCategory.Find(doc.Category).Glyph, f, r, Themes.Blend(p.Main, p.Fore, 0.6f), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void ApplyBodyMargin()
    {
        body.SetColumnWidth(ColW);
        bodyHint.Location = new Point(body.SideMargin, 2);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); head?.PerformLayout(); head?.Invalidate(); LayoutScroll(); }
    protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); LayoutScroll(); }

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = topBar.BackColor = head.BackColor = p.Main;
        foreach (var l in new[] { crumbLabel, titleLabel, propsToggle }) l.BackColor = p.Main;
        props.BackColor = p.Main;
        summaryHost.BackColor = summaryBox.BackColor = WikiUi.Surface(p);
        summaryBox.ForeColor = p.Fore;
        foreach (var (pl, _) in propRows) if (pl is IconLabel il) { var old = il.Icon; il.Icon = RowIcon(il.Text, p.Dim); old?.Dispose(); }
        foreach (Control c in props.Controls) if (c is Label pl) { pl.BackColor = p.Main; pl.ForeColor = p.Dim; }
        stars.SetPalette(p); classes.SetPalette(p); tagStrip.SetPalette(p); modeSwitch.BackColor = p.Main; modeSwitch.SetPalette(p);
        crumbLabel.ForeColor = propsToggle.ForeColor = p.Dim;
        titleLabel.ForeColor = p.Fore;
        bool recolor = body.ForeColor != p.Fore && doc != null;
        body.BackColor = bodyHint.BackColor = p.Main;
        body.ForeColor = p.Fore;
        if (recolor) { loading = true; try { body.LoadText(body.ToText(out _)); } finally { loading = false; } } // 이미 쓴 글자 색도 새 테마로
        bodyHint.ForeColor = Themes.Blend(p.Main, p.Fore, 0.4f);
        backButton.SetPalette(p);
        moreButton.SetPalette(p);
        toc.SetPalette(p);
        attrs.SetPalette(p);
        head.Invalidate();
    }

    /// <summary>위키 문서를 연다(고치던 문서가 있으면 먼저 저장).</summary>
    public void Open(string path, string novelDir)
    {
        Save();
        try { doc = WikiStore.Load(path); }
        catch (Exception ex) { MessageBox.Show(FindForm(), $"위키 문서를 열지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); doc = null; return; }
        novelName = Path.GetFileName(novelDir);
        this.novelDir = novelDir;
        loading = true;
        try
        {
            titleLabel.Text = TitleText(doc);
            SyncCrumb();
            categoryBox.SelectedIndex = Array.FindIndex(WikiCategory.All, c => c.Key == doc.Category);
            stars.Value = Math.Clamp(doc.Importance, 1, 5);
            summaryBox.Text = doc.Summary;
            classes.SetItems(doc.Tags);
            tagStrip.SetTags(doc.Labels);
            photo?.Dispose();
            SetPhotoImage(WikiStore.LoadPhoto(path)); // 이름 옆 동그라미 · 속성 상자의 사진
            attrs.AttrColor = doc.AttrColor;
            body.LoadText(doc.Body);
            bodyHint.Visible = editable && body.TextLength == 0;
            attrs.Title = doc.Title;
            attrs.SetItems(doc.Attributes);
            body.ToText(out var heads);
            SetToc(heads);
        }
        finally { loading = false; }
        headOffset = 0;
        head.PerformLayout();
        head.Invalidate();
        body.ScrollToTop();
        body.Focus();
        // 화면에 자리 잡은 뒤에도 한 번 더(처음 열 때 맨 아래로 내려가 있던 것). 창이 아직 없으면 생길 때.
        // 화면 배치(목차 · 속성 상자 높이)가 다 잡힌 뒤 글 상자가 아래로 밀려 있는 일이 있어, 잠깐 뒤에 한 번 더 맨 위로(그새 커서를 옮겼으면 그대로 둔다).
        var settle = new System.Windows.Forms.Timer { Interval = 300 };
        int ticks = 0;
        settle.Tick += (_, _) => { if (++ticks >= 3 || body.SelectionStart != 0) { settle.Dispose(); return; } if (body.IsHandleCreated) body.ScrollToTop(); };
        settle.Start();
    }

    private void SetToc(List<(int, string, int)> heads)
    {
        int before = toc.Count;
        toc.SetItems(heads);
        if (toc.Count != before) head.PerformLayout();
    }

    private void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

    /// <summary>고친 내용을 파일에 쓴다(저장하지 못하면 쓰던 내용을 복구 폴더에 남기고 알린다).</summary>
    public void Save()
    {
        saveTimer.Stop();
        if (doc == null || loading) return;
        string? now = null;
        try
        {
            doc.Body = body.ToText(out var heads);
            SetToc(heads);
            now = doc.Serialize();
            string? old = File.Exists(doc.Path) ? File.ReadAllText(doc.Path) : null;
            if (old == now) return;
            WikiStore.Save(doc);
            Changed?.Invoke();
        }
        catch (Exception ex) { ReportSaveFailure(ex, now); }
    }

    private DateTime lastSaveFailNotice;

    private void ReportSaveFailure(Exception ex, string? content)
    {
        AppLog.Write($"위키 문서를 저장하지 못했어요: {doc?.Path}", ex);
        // 쓰던 내용은 매번 복구 폴더에 덮어써 두고(닫아 버려도 남도록), 안내창은 1분에 한 번만 띄운다
        string? copy = content == null || doc == null ? null : SafeFile.WriteRescueCopy($"위키-{doc.Title}-저장실패.txt", content);
        if (DateTime.UtcNow - lastSaveFailNotice < TimeSpan.FromMinutes(1)) return;
        lastSaveFailNotice = DateTime.UtcNow;
        string msg = $"'{doc?.Title}' 위키 문서를 저장하지 못했어요. ({ex.Message})" + (copy != null ? $"\n\n쓰던 내용은 여기에 따로 남겨 두었어요:\n{copy}" : "");
        try { MessageBox.Show(FindForm(), msg, AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
    }

    /// <summary>수정 ↔ 보기: 입력 부분을 잠그거나 풀고 화면을 다시 잡는다.</summary>
    private void SetEditable(bool value)
    {
        if (editable == value) return;
        if (!value) Save(); // 보기로 넘어가기 전에 고친 것을 저장
        editable = value;
        ApplyEditable();
        modeSwitch.Editing = value;
    }

    private void ApplyEditable()
    {
        body.ReadOnly = !editable;
        if (palette != null) { body.BackColor = palette.Main; body.ForeColor = palette.Fore; } // ReadOnly가 바뀌면 바탕색이 회색으로 돌아가는 일을 막는다
        bodyHint.Visible = editable && doc != null && body.TextLength == 0 && !body.PendingHeading;
        categoryBox.Enabled = editable;
        stars.ReadOnly = !editable;
        summaryBox.ReadOnly = !editable;
        classes.ReadOnly = !editable;
        tagStrip.ReadOnly = !editable;
        attrs.ReadOnly = !editable;
        head.PerformLayout();
        head.Invalidate();
    }

    /// <summary>분류 칩의 [분류 추가]: 이 작품의 분류 목록에서 고르는 팝업. 목록에 없으면 새로 만들 수도 있다.</summary>
    private void PickClass(Rectangle anchor)
    {
        if (doc == null || !editable || palette == null || PickPopup.JustClosed) return;
        var choices = WikiStore.KnownClasses(novelDir).Where(c => !doc.Tags.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var pop = new PickPopup(palette, choices, "분류", "선택할 분류가 없어요", "맞는 분류가 없어요", canDelete: true);
        pop.Picked += AddClass;
        pop.Created += name =>
        {
            try { WikiStore.AddClass(novelDir, name); } catch (Exception ex) { AppLog.Write("분류 목록을 저장하지 못했어요", ex); }
            AddClass(name);
        };
        pop.DeleteRequested += name =>
        {
            if (MessageBox.Show(FindForm(), $"'{name}' 분류를 지울까요?\n이 분류를 쓰던 다른 위키 문서에서도 빠져요.", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                Save();
                WikiStore.DeleteClass(novelDir, name);
                doc.Tags.RemoveAll(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
                classes.SetItems(doc.Tags);
                head.PerformLayout();
                Changed?.Invoke();
            }
            catch (Exception ex) { MessageBox.Show(FindForm(), $"분류를 지우지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        pop.ShowBelow(anchor, FindForm(), S(300));
    }

    private void AddClass(string name)
    {
        if (doc == null || doc.Tags.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        doc.Tags.Add(name);
        classes.SetItems(doc.Tags);
        head.PerformLayout();
        SaveSoon();
    }

    /// <summary>속성 상자의 [속성 추가]: 이 작품 위키에서 쓰인 항목 이름들 중에서 고르거나 새 항목을 만든다. 만든 뒤에는 바로 값을 적을 수 있다.</summary>
    private void PickAttribute(Rectangle anchor)
    {
        if (doc == null || !editable || palette == null || PickPopup.JustClosed) return;
        var choices = WikiStore.AllAttributeNames(novelDir).Where(n => !doc.Attributes.Any(a => a.Key.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        string dir = novelDir;
        var pop = new AttrPopup(palette, choices, WikiStore.AttributeFavorites(dir), WikiStore.AttributeGlyph);
        pop.Picked += AddAttribute;
        pop.Created += AddAttribute;
        pop.FavoriteToggled += (n, f) => { try { WikiStore.SetAttributeFavorite(dir, n, f); } catch (Exception ex) { AppLog.Write("속성 즐겨찾기를 저장하지 못했어요", ex); } };
        attrPop = pop;
        pop.FormClosed += (_, _) => { if (attrPop == pop) attrPop = null; };
        pop.ShowAt(anchor, FindForm(), Math.Max(S(300), anchor.Width));
    }

    private void AddAttribute(string rawName)
    {
        if (doc == null) return;
        string name = rawName.Replace(":", " ").Replace("：", " ").Trim();
        if (name.Length == 0) return;
        int i = doc.Attributes.FindIndex(a => a.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0) { doc.Attributes.Add((name, "")); i = doc.Attributes.Count - 1; }
        attrs.SetItems(doc.Attributes);
        head.PerformLayout();
        SaveSoon();
        attrs.BeginEdit(i); // 항목을 만든 바로 뒤에 값을 적는다
    }

    /// <summary>이 문서의 틀(카테고리 · 분류 · 태그 · 속성 이름 · 본문)을 템플릿으로 저장한다. 새 위키 문서를 만들 때 고를 수 있다.</summary>
    private void SaveAsTemplate()
    {
        if (doc == null) return;
        Save();
        using var dlg = new NovelInputDialog("템플릿으로 저장", "템플릿 이름", doc.Title);
        if (dlg.ShowDialogOver(FindForm()) != DialogResult.OK || dlg.FirstValue.Length == 0) return;
        try
        {
            if (WikiStore.TemplateNames(novelDir).Contains(dlg.FirstValue, StringComparer.OrdinalIgnoreCase) &&
                MessageBox.Show(FindForm(), $"'{dlg.FirstValue}' 템플릿이 이미 있어요. 덮어쓸까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            WikiStore.SaveTemplate(novelDir, dlg.FirstValue, doc);
        }
        catch (Exception ex) { MessageBox.Show(FindForm(), $"템플릿을 저장하지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private ContextMenuStrip? moreMenu;

    private void ShowMoreMenu()
    {
        if (doc == null) return;
        moreMenu?.Dispose();
        var m = moreMenu = new ContextMenuStrip();
        MenuGuard.Attach(m);
        if (editable) m.Items.Add("이름 바꾸기…", null, (_, _) => BeginInvoke(Rename));
        if (editable) m.Items.Add("템플릿으로 저장…", null, (_, _) => BeginInvoke(SaveAsTemplate));
        var templates = WikiStore.TemplateNames(novelDir);
        if (templates.Count > 0)
        {
            var del = new ToolStripMenuItem("템플릿 지우기");
            foreach (var t in templates)
            {
                string name = t;
                del.DropDownItems.Add(name, null, (_, _) =>
                {
                    if (MessageBox.Show(FindForm(), $"'{name}' 템플릿을 휴지통으로 보낼까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    try { WikiStore.DeleteTemplate(novelDir, name); }
                    catch (Exception ex) { MessageBox.Show(FindForm(), $"템플릿을 지우지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                });
            }
            m.Items.Add(del);
        }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("탐색기에서 보기", null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{doc.Path}\""));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("휴지통으로 보내기", null, (_, _) => BeginInvoke(Delete));
        if (topBar.Visible) m.Show(moreButton, new Point(moreButton.Width - m.PreferredSize.Width, moreButton.Height)); // 칸 안에서는 ShowMenuAt이 칸 머리 ⋯ 밑에 띄운다
    }

    private void Rename()
    {
        if (doc == null) return;
        Save();
        using var dlg = new NovelInputDialog("위키 문서 이름 바꾸기", "제목", doc.Title);
        if (dlg.ShowDialogOver(FindForm()) != DialogResult.OK) return;
        try { WikiStore.Rename(doc, dlg.FirstValue); titleLabel.Text = TitleText(doc); attrs.Title = doc.Title; attrs.Invalidate(); SyncCrumb(); Changed?.Invoke(); }
        catch (Exception ex) { MessageBox.Show(FindForm(), $"이름을 바꾸지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void Delete()
    {
        if (doc == null) return;
        if (MessageBox.Show(FindForm(), $"'{doc.Title}' 위키 문서를 휴지통으로 보낼까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        saveTimer.Stop();
        try { WikiStore.Delete(doc.Path); }
        catch (Exception ex) { MessageBox.Show(FindForm(), $"삭제하지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        doc = null;
        Changed?.Invoke();
        BackRequested?.Invoke();
    }

    /// <summary>둥근 테두리 상자(목차 · 속성)의 바탕 그리기.</summary>
    /// <summary>속성 줄 이름표: 앞에 작은 아이콘, 그 옆에 이름(둘이 겹치지 않게 직접 나란히 그린다).</summary>
    private sealed class IconLabel : Label
    {
        private Bitmap? icon;
        public IconLabel()
        {
            AutoSize = false;
            UseMnemonic = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Bitmap? Icon { get => icon; set { icon = value; Fit(); Invalidate(); } }
        private int Gap => (int)Math.Round(8 * DeviceDpi / 96.0);
        private void Fit()
        {
            var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int iw = icon?.Width ?? 0;
            Size = new Size(iw + (iw > 0 ? Gap : 0) + t.Width + 2, Math.Max(t.Height, icon?.Height ?? 0) + 2);
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int x = 0;
            if (icon != null) { g.DrawImage(icon, 0, (Height - icon.Height) / 2); x = icon.Width + Gap; }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Max(0, Width - x), Height), ForeColor,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        }
    }

    private abstract class BoxBase : Control
    {
        protected LibraryPalette? palette;
        protected BoxBase()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font(NovelStore.UiFontFamily, 9.5f);
        }
        public void SetPalette(LibraryPalette p) { palette = p; Invalidate(); }
        protected int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        protected int RowH => Font.Height + S(10);
        protected void PaintFrame(Graphics g, LibraryPalette p, bool dashed)
        {
            g.Clear(Parent?.BackColor ?? p.Main);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = LibraryPalette.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(10));
            using (var b = new SolidBrush(p.Dark ? Themes.Blend(p.Main, Color.White, 0.03f) : Color.White)) g.FillPath(b, path);
            using var pen = new Pen(dashed ? Themes.Blend(p.Line, p.Accent, 0.5f) : p.Line) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            g.DrawPath(pen, path);
        }
    }

    /// <summary>목차 상자: "목차" 머리 밑에 ## · ### 제목들. 누르면 본문의 그 줄로.</summary>
    private sealed class TocBox : BoxBase
    {
        private List<(int Level, string Text, int Offset)> items = new();
        private int hover = -1;
        public event Action<int>? HeadingChosen;
        public int Count => items.Count;

        public TocBox() { }
        public void SetItems(List<(int, string, int)> list)
        {
            bool same = list.Count == items.Count && list.Zip(items).All(z => z.First.Equals(z.Second));
            if (same) return;
            items = list; Invalidate();
        }
        public int PreferredHeight(int width) => S(16) + RowH + Math.Max(2, items.Count) * RowH + S(10);

        private int IndexAt(int y) { int i = (y - S(16) - RowH) / RowH; return y < S(16) + RowH || i >= items.Count ? -1 : i; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = IndexAt(e.Y); if (i != hover) { hover = i; Invalidate(); } Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); int i = IndexAt(e.Y); if (i >= 0) HeadingChosen?.Invoke(items[i].Offset); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = palette;
            if (p == null) return;
            var g = e.Graphics;
            PaintFrame(g, p, false);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            int x = S(16), y = S(12), w = Width - S(32);
            using (var bf = new Font(Font, FontStyle.Bold)) TextRenderer.DrawText(g, "목차", bf, new Rectangle(x, y, w, RowH), p.Fore, flags);
            y += RowH + S(4);
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "문서에 제목이 없어요", Font, new Rectangle(x, y, w, RowH), p.Dim, flags);
                using var sf = new Font(Font.FontFamily, Font.SizeInPoints * 0.9f);
                TextRenderer.DrawText(g, "ⓘ  ## 제목, ### 소제목, #### 세부 제목으로 나눠 보세요", sf, new Rectangle(x, y + RowH, w, RowH), p.Accent, flags);
                return;
            }
            for (int i = 0; i < items.Count; i++)
            {
                var (lvl, text, _) = items[i];
                int ix = x + (lvl == 3 ? S(16) : lvl == 4 ? S(32) : 0);
                TextRenderer.DrawText(g, text, Font, new Rectangle(ix, y, w - (ix - x), RowH), i == hover ? p.Accent : lvl >= 3 ? p.Dim : p.Fore, flags);
                y += RowH;
            }
        }
    }

    /// <summary>
    /// 속성 표: 맨 위에 문서 이름, 밑으로 [아이콘 항목 이름 | 값] 줄들, 맨 끝에 [+ 속성 추가].
    /// 값 칸을 누르면 그 자리에서 글을 적는다(Enter · 다른 곳 누르기로 확정, Esc로 취소). 항목 이름을 우클릭하면 지운다.
    /// 비어 있으면 점선 상자(누르면 항목 고르기). [속성 추가]는 AddRequested로 알리고, 항목 고르기 팝업은 쓰는 쪽이 띄운다.
    /// </summary>
    private sealed class AttrBox : BoxBase
    {
        private static readonly string[] EnglishKeys = { "영문명", "영어 이름", "영어이름", "영문 이름", "English name", "Name (EN)" };
        private static readonly (string Name, string Hex)[] Swatches =
        {
            ("기본", ""), ("노랑", "#FFF18F"), ("분홍", "#FFD6E7"), ("살구", "#FFE0C2"), ("초록", "#D5F2D0"), ("하늘", "#CFE8FF"), ("보라", "#DCD3FF"), ("회색", "#E4E4E7"),
        };

        private List<(string Key, string Value)> items = new();
        private int hover = -1; // 0..n-1 = 줄, n = 추가 줄
        private int editing = -1;
        private bool readOnly;
        private readonly TextBox editor;
        private readonly Font titleFont;
        private Bitmap? photo;
        private string attrColor = "";
        private int dragIndex = -1, dropIndex = -1; // 끌고 있는 줄 · 놓을 자리(줄 사이 번호)
        private bool dragging;
        private Point downAt;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public string Title { get; set; } = "";
        public int Count => items.Count;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool ReadOnly { get => readOnly; set { readOnly = value; CancelEdit(); Invalidate(); } }
        /// <summary>이름 밑에 보일 문서 사진(없으면 null).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Bitmap? Photo { get => photo; set { photo = value; Invalidate(); } }
        /// <summary>상자 색(#RRGGBB, 비어 있으면 기본).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public string AttrColor { get => attrColor; set { attrColor = value ?? ""; Invalidate(); } }
        public event Action<Rectangle>? AddRequested;
        public event Action<int, string>? ValueChanged;
        public event Action<int>? RemoveRequested;
        /// <summary>속성을 끌어서 순서를 바꿨다(옮긴 줄, 새 자리).</summary>
        public event Action<int, int>? RowMoved;
        /// <summary>상자 색을 골랐다(#RRGGBB, 기본이면 빈 글).</summary>
        public event Action<string>? ColorChanged;

        public AttrBox()
        {
            titleFont = new Font(Font.FontFamily, Font.SizeInPoints + 1.5f, FontStyle.Bold);
            editor = new TextBox { BorderStyle = BorderStyle.None, Visible = false, Font = Font };
            editor.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndEdit(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(false); }
            };
            editor.LostFocus += (_, _) => EndEdit(true);
            Controls.Add(editor);
        }

        protected override void Dispose(bool disposing) { if (disposing) titleFont.Dispose(); base.Dispose(disposing); }

        private int RowHt => (int)Math.Round((Font.Height + S(16)) * 0.8); // 속성 줄 높이(전의 80%)
        private bool Empty => items.Count == 0 && photo == null;

        /// <summary>영문명 속성의 값(이름 밑에 보인다). 없으면 빈 글.</summary>
        private string EnglishName => items.FirstOrDefault(it => EnglishKeys.Contains(it.Key.Trim(), StringComparer.OrdinalIgnoreCase)).Value?.Trim() ?? "";

        private int TitleH => (int)Math.Round((titleFont.Height + S(12)) * 0.8);
        private int EngH => EnglishName.Length > 0 ? (int)Math.Round((Font.Height + S(6)) * 0.8) : 0;
        private int HeaderTextH => TitleH + EngH;
        private int PhotoSide(int w) => Math.Max(0, w - S(6)); // 사진은 상자 폭을 채운다(상자 폭 자체를 줄였다)
        private int PhotoHFor(int w) => photo == null ? 0 : PhotoSide(w) + S(8);
        private int HeaderH => HeaderTextH + PhotoHFor(Width);

        public void SetItems(List<(string, string)> list) { items = list.ToList(); if (editing >= items.Count) CancelEdit(); Invalidate(); }
        public int PreferredHeight(int width) => Empty ? S(16) + RowH * 3 + S(10) : HeaderTextH + PhotoHFor(width) + RowHt * (items.Count + 1);

        private int LabelW => items.Count == 0 ? Width / 3 : Math.Clamp(items.Max(it => TextRenderer.MeasureText(it.Key, Font, Size.Empty, TextFormatFlags.NoPadding).Width) + S(60), Width / 4, Width / 2);
        private Rectangle RowRect(int i) => new(0, HeaderH + RowHt * i, Width, RowHt);
        private Rectangle ValueRect(int i) { var r = RowRect(i); return new Rectangle(LabelW, r.Y, Width - LabelW, r.Height); }
        private Rectangle AddRow => RowRect(items.Count);
        private Rectangle SwatchRect => new(Width - S(28), (TitleH - S(14)) / 2 + S(2), S(14), S(14));
        /// <summary>[속성 추가] 줄(비어 있으면 상자 전체)의 화면 좌표.</summary>
        public Rectangle AddAnchorScreen => RectangleToScreen(Empty ? new Rectangle(0, 0, Width, Height) : AddRow);
        private int XW => readOnly ? 0 : S(34); // 줄 오른쪽 끝의 ✕(지우기) 칸 폭
        private Rectangle XRect(int i) { var r = RowRect(i); return new Rectangle(r.Right - XW, r.Y, XW, r.Height); }
        private int hoverX = -1;

        private Color BoxColorFor(LibraryPalette p)
        {
            if (attrColor.Length > 0)
            {
                try { var c = ColorTranslator.FromHtml(attrColor); return p.Dark ? Themes.Blend(c, p.Main, 0.55f) : c; } catch { }
            }
            return p.AccentSoft;
        }
        private static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

        private int IndexAt(Point pt)
        {
            if (Empty) return readOnly ? -1 : 0;
            for (int i = 0; i < items.Count; i++) if (RowRect(i).Contains(pt)) return i;
            return !readOnly && AddRow.Contains(pt) ? items.Count : -1;
        }

        /// <summary>i번째 줄의 값 칸을 입력칸으로 바꿔 바로 적을 수 있게 한다.</summary>
        public void BeginEdit(int i)
        {
            if (readOnly || i < 0 || i >= items.Count) return;
            EndEdit(true);
            editing = i;
            var r = ValueRect(i);
            editor.Text = items[i].Value;
            editor.BackColor = palette != null ? WikiUi.Surface(palette) : Color.White;
            editor.ForeColor = palette?.Fore ?? Color.Black;
            editor.SetBounds(r.X + S(12), r.Y + (r.Height - editor.PreferredHeight) / 2, r.Width - S(24) - XW, editor.PreferredHeight);
            editor.Visible = true;
            editor.Focus();
            editor.SelectAll();
            Invalidate();
        }

        private void CancelEdit() { editing = -1; editor.Visible = false; }

        private void EndEdit(bool commit)
        {
            if (editing < 0) return;
            int i = editing;
            string text = editor.Text.Replace("\r", "").Replace("\n", " ").Trim();
            editing = -1;
            editor.Visible = false;
            if (commit && i < items.Count && items[i].Value != text) ValueChanged?.Invoke(i, text);
            Invalidate();
        }

        /// <summary>머리의 색 단추 · 머리 우클릭: 상자 색 목록(기본 · 여러 색 · 직접 고르기).</summary>
        private void ShowColorMenu(Point screen)
        {
            if (readOnly) return;
            var m = new ContextMenuStrip();
            MenuGuard.Attach(m);
            foreach (var (name, hex) in Swatches)
            {
                var bmp = new Bitmap(16, 16);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using var b = new SolidBrush(hex.Length > 0 ? ColorTranslator.FromHtml(hex) : palette?.AccentSoft ?? Color.Lavender);
                    g.FillEllipse(b, 1, 1, 13, 13);
                    using var pen = new Pen(Color.FromArgb(90, 0, 0, 0));
                    g.DrawEllipse(pen, 1, 1, 13, 13);
                }
                string h = hex;
                var it = new ToolStripMenuItem(name, bmp) { Checked = string.Equals(h, attrColor, StringComparison.OrdinalIgnoreCase) };
                it.Click += (_, _) => ColorChanged?.Invoke(h);
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("직접 고르기…", null, (_, _) => BeginInvoke(() =>
            {
                using var dlg = new ColorDialog { FullOpen = true, Color = attrColor.Length > 0 ? ColorTranslator.FromHtml(attrColor) : Color.White };
                if (dlg.ShowDialogOver(FindForm()) == DialogResult.OK) ColorChanged?.Invoke(ColorTranslator.ToHtml(Color.FromArgb(dlg.Color.R, dlg.Color.G, dlg.Color.B)));
            }));
            m.Closed += (_, _) => BeginInvoke(m.Dispose);
            m.Show(screen);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (readOnly || e.Button != MouseButtons.Left || Empty) return;
            int i = IndexAt(e.Location);
            // 속성 앞부분(아이콘 · 이름 칸)을 누른 채 위아래로 끌면 순서가 바뀐다
            if (i >= 0 && i < items.Count && e.X < LabelW) { dragIndex = i; dropIndex = -1; dragging = false; downAt = e.Location; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragIndex >= 0 && e.Button == MouseButtons.Left)
            {
                if (!dragging && Math.Abs(e.Y - downAt.Y) > S(4)) dragging = true;
                if (dragging)
                {
                    int t = (e.Y - HeaderH + RowHt / 2) / RowHt;
                    dropIndex = Math.Clamp(t, 0, items.Count);
                    Cursor = Cursors.Hand;
                    Invalidate();
                    return;
                }
            }
            int i = IndexAt(e.Location);
            int hx = !readOnly && i >= 0 && i < items.Count && XRect(i).Contains(e.Location) ? i : -1;
            if (i != hover || hx != hoverX) { hover = i; hoverX = hx; Invalidate(); }
            bool onLabel = !readOnly && i >= 0 && i < items.Count && e.X < LabelW;
            bool onSwatch = !readOnly && !Empty && SwatchRect.Contains(e.Location);
            Cursor = onLabel ? Cursors.Hand : onSwatch || (i >= 0 && !(i < items.Count && e.X < LabelW)) ? Cursors.Hand : Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; hoverX = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                int from = dragIndex, to = dropIndex;
                dragging = false; dragIndex = -1; dropIndex = -1;
                Invalidate();
                if (from >= 0 && to >= 0 && to != from && to != from + 1) RowMoved?.Invoke(from, to > from ? to - 1 : to);
                return;
            }
            dragIndex = -1;
            if (readOnly) return;
            // 머리: 색 단추 · 우클릭 = 상자 색 고르기
            if (!Empty && e.Y < HeaderTextH && ((e.Button == MouseButtons.Left && SwatchRect.Contains(e.Location)) || e.Button == MouseButtons.Right)) { ShowColorMenu(PointToScreen(new Point(e.X, HeaderTextH))); return; }
            int i = IndexAt(e.Location);
            if (i < 0) return;
            if (i == items.Count) { if (e.Button == MouseButtons.Left) AddRequested?.Invoke(AddAnchorScreen); return; }
            if (e.Button == MouseButtons.Left)
            {
                if (XRect(i).Contains(e.Location))
                {
                    // ✕: 값이 적혀 있으면 한 번 묻고 지운다
                    if (items[i].Value.Length == 0 || MessageBox.Show(FindForm(), $"'{items[i].Key}' 속성을 지울까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) RemoveRequested?.Invoke(i);
                }
                else if (e.X >= LabelW) BeginEdit(i);
            }
            else if (e.Button == MouseButtons.Right && e.X < LabelW && MessageBox.Show(FindForm(), $"'{items[i].Key}' 속성을 지울까요?", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) RemoveRequested?.Invoke(i);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = palette;
            if (p == null) return;
            var g = e.Graphics;
            var flags = WikiUi.Flags;
            if (Empty)
            {
                PaintFrame(g, p, true);
                using var big = new Font(Font.FontFamily, 14f);
                TextRenderer.DrawText(g, "+", big, new Rectangle(0, Height / 2 - S(26), Width, S(24)), p.Accent, flags | TextFormatFlags.HorizontalCenter);
                using var bf0 = new Font(Font, FontStyle.Bold);
                TextRenderer.DrawText(g, "속성 추가하기", bf0, new Rectangle(0, Height / 2, Width, S(22)), p.Accent, flags | TextFormatFlags.HorizontalCenter);
                return;
            }
            g.Clear(Parent?.BackColor ?? p.Main);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var surface = WikiUi.Surface(p);
            var lav = BoxColorFor(p);
            var fore = Luma(lav) < 0.4 ? Color.White : p.Fore;       // 짙은 색 위에서는 글자를 흰색으로
            var dim = Themes.Blend(lav, fore, 0.55f);
            var grid = Themes.Blend(lav, fore, 0.1f);
            int lw = LabelW;
            using var clip = LibraryPalette.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), S(10));
            g.SetClip(clip);
            using (var b = new SolidBrush(lav)) g.FillRectangle(b, 0, 0, Width, Height);
            using var bold = new Font(Font, FontStyle.Bold);
            using var icon = new Font(MainForm.IconFontName(), 10f);
            // 머리: 문서 이름(크게) + 영문명(있으면 그 밑에) + 오른쪽 색 단추
            TextRenderer.DrawText(g, Title, titleFont, new Rectangle(S(28), 0, Width - S(56), TitleH), fore, flags | TextFormatFlags.HorizontalCenter);
            string eng = EnglishName;
            if (eng.Length > 0) TextRenderer.DrawText(g, eng, Font, new Rectangle(S(28), TitleH - S(4), Width - S(56), EngH), fore, flags | TextFormatFlags.HorizontalCenter);
            if (!readOnly)
            {
                var sw = SwatchRect;
                using (var sb = new SolidBrush(Themes.Blend(lav, fore, 0.18f))) g.FillEllipse(sb, sw);
                using (var sp = new Pen(dim)) g.DrawEllipse(sp, sw);
            }
            // 이름 밑 사진
            if (photo != null)
            {
                int side = PhotoSide(Width);
                var pr = new Rectangle((Width - side) / 2, HeaderTextH + S(3), side, side);
                var old = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(photo, pr);
                g.InterpolationMode = old;
            }
            using var gridPen = new Pen(grid);
            for (int i = 0; i < items.Count; i++)
            {
                var r = RowRect(i);
                var vr = ValueRect(i);
                using (var b = new SolidBrush(i == hover && !readOnly && !dragging ? Themes.Blend(surface, p.Accent, 0.06f) : surface)) g.FillRectangle(b, vr.X, vr.Y, vr.Width, vr.Height - 1);
                g.DrawLine(gridPen, 0, r.Y, Width, r.Y);
                TextRenderer.DrawText(g, WikiStore.AttributeGlyph(items[i].Key), icon, new Rectangle(S(12), r.Y, S(20), r.Height), dim, flags | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, items[i].Key, bold, new Rectangle(S(38), r.Y, lw - S(46), r.Height), fore, flags);
                if (editing != i) TextRenderer.DrawText(g, items[i].Value, Font, new Rectangle(vr.X + S(12), vr.Y, vr.Width - S(24) - XW, vr.Height), p.Fore, flags);
                if (!readOnly)
                {
                    var xr = XRect(i);
                    float cx = xr.X + xr.Width / 2f, cy = xr.Y + xr.Height / 2f, d = S(4);
                    using var xp = new Pen(hoverX == i ? p.Accent : p.Dim, Math.Max(1.4f, DeviceDpi / 96f * 1.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(xp, cx - d, cy - d, cx + d, cy + d);
                    g.DrawLine(xp, cx - d, cy + d, cx + d, cy - d);
                }
                if (dragging && i == dragIndex) using (var gh = new SolidBrush(Color.FromArgb(70, fore))) g.FillRectangle(gh, 0, r.Y, Width, r.Height); // 끌고 있는 줄은 흐리게
            }
            if (!readOnly)
            {
                var ar = AddRow;
                g.DrawLine(gridPen, 0, ar.Y, Width, ar.Y);
                if (hover == items.Count) using (var hb = new SolidBrush(Themes.Blend(lav, fore, 0.06f))) g.FillRectangle(hb, ar);
                TextRenderer.DrawText(g, "+  속성 추가", bold, ar, hover == items.Count ? p.Accent : fore, flags | TextFormatFlags.HorizontalCenter);
            }
            // 끌어서 놓을 자리 표시선
            if (dragging && dropIndex >= 0)
            {
                int ly = HeaderH + RowHt * dropIndex;
                using var lp = new Pen(p.Accent, S(2));
                g.DrawLine(lp, S(4), ly, Width - S(4), ly);
            }
            g.ResetClip();
        }
    }
}
