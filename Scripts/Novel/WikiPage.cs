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
        titleLabel = new Label { AutoSize = false, AutoEllipsis = true, Font = new Font(ff, 24f, FontStyle.Bold), UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
        titleLabel.DoubleClick += (_, _) => { if (editable) Rename(); };
        propsToggle = new Label { AutoSize = true, Font = new Font(ff, 9.5f), Cursor = Cursors.Hand, UseMnemonic = false };
        propsToggle.Click += (_, _) => { propsOpen = !propsOpen; SyncPropsToggle(); head.PerformLayout(); };

        categoryBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Font = new Font(ff, 9.5f) };
        foreach (var c in WikiCategory.All) categoryBox.Items.Add(c.Label);
        stars = new StarBox();
        summaryBox = new TextBox { Font = new Font(ff, 9.5f) };
        classes = new ClassStrip();
        tagStrip = new TagStrip();
        props = new Panel { Visible = false };
        void Row(string label, Control c)
        {
            var l = new Label { Text = label, AutoSize = true, Font = new Font(ff, 9.5f), UseMnemonic = false };
            propRows.Add((l, c));
            props.Controls.Add(l);
            props.Controls.Add(c);
        }
        Row("카테고리", categoryBox);
        Row("중요도", stars);
        Row("설명", summaryBox);
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
        attrs.ValueChanged += (i, v) => { if (doc == null || !editable || i < 0 || i >= doc.Attributes.Count) return; doc.Attributes[i] = (doc.Attributes[i].Key, v); attrs.SetItems(doc.Attributes); SaveSoon(); };
        attrs.RemoveRequested += i => { if (doc == null || !editable || i < 0 || i >= doc.Attributes.Count) return; doc.Attributes.RemoveAt(i); attrs.SetItems(doc.Attributes); head.PerformLayout(); SaveSoon(); };

        head = new Panel(); // 위치는 LayoutScroll이 잡는다(본문과 함께 위로 밀려 올라간다)
        head.Controls.AddRange(new Control[] { crumbLabel, titleLabel, propsToggle, props, toc, attrs });
        head.Layout += (_, _) => LayoutHead();
        head.Paint += PaintHead;

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
        Disposed += (_, _) => saveTimer.Dispose();
        VisibleChanged += (_, _) => { if (!Visible) Save(); };
        SyncPropsToggle();
        if (defaultViewMode) { editable = false; ApplyEditable(); modeSwitch.Editing = false; }
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int ColW => Math.Min(ClientSize.Width - S(48) * 2, S(800));
    private int ColLeft => Math.Max(S(24), (ClientSize.Width - ColW) / 2);

    private void SyncPropsToggle() { propsToggle.Text = propsOpen ? "⌃  문서 속성 접기" : "⌄  문서 속성 펼치기"; props.Visible = propsOpen; }

    private void SyncCrumb()
    {
        if (doc == null) return;
        crumbLabel.Text = $"{novelName}  ›  {WikiCategory.Find(doc.Category).Label}  ›  {doc.Title}";
    }

    /// <summary>속성 칸(카테고리 · 중요도 · 설명 · 분류 · 태그) 줄들을 차례로 놓고 전체 높이를 돌려준다. 보기 모드에서 빈 줄은 감춘다.</summary>
    private int LayoutProps(int w)
    {
        int labelW = S(78), x = labelW + S(8), cw = Math.Max(S(120), w - x), y = 0;
        foreach (var (label, ctl) in propRows)
        {
            int h;
            bool show = true;
            if (ctl == classes) { h = classes.FitHeight(cw); show = h > 0; if (show) classes.Width = cw; }
            else if (ctl == tagStrip) { h = tagStrip.FitHeight(cw); show = h > 0; if (show) tagStrip.Width = cw; }
            else if (ctl == summaryBox) { h = summaryBox.PreferredHeight; show = editable || summaryBox.Text.Length > 0; }
            else h = ctl.Height;
            label.Visible = ctl.Visible = show;
            if (!show) continue;
            int rh = Math.Max(h, label.Height) + S(10);
            label.Location = new Point(0, y + (rh - label.Height) / 2);
            int cwid = ctl == categoryBox ? categoryBox.Width : ctl == stars ? stars.Width : cw;
            ctl.SetBounds(x, y + (rh - h) / 2, cwid, h);
            y += rh;
        }
        return y;
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
                attrs.SetBounds(x + half + S(24), y, half, attrs.PreferredHeight(half));
                y = Math.Max(toc.Bottom, attrs.Bottom) + S(24);
            }
            else
            {
                // 칸이 좁으면(워크스페이스에서 나눠 볼 때) 목차 밑에 속성 상자를 쌓는다
                toc.SetBounds(x, y, w, toc.PreferredHeight(w));
                attrs.SetBounds(x, toc.Bottom + S(12), w, attrs.PreferredHeight(w));
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

    private void PaintHead(object? sender, PaintEventArgs e)
    {
        var p = palette;
        if (p == null || doc == null) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int circle = S(56);
        var r = new Rectangle(ColLeft, titleLabel.Top, circle, circle);
        using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, p.Dark ? 0.14f : 0.08f))) g.FillEllipse(b, r);
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
            titleLabel.Text = doc.Title;
            SyncCrumb();
            categoryBox.SelectedIndex = Array.FindIndex(WikiCategory.All, c => c.Key == doc.Category);
            stars.Value = Math.Clamp(doc.Importance, 1, 5);
            summaryBox.Text = doc.Summary;
            classes.SetItems(doc.Tags);
            tagStrip.SetTags(doc.Labels);
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
        try { WikiStore.Rename(doc, dlg.FirstValue); titleLabel.Text = doc.Title; attrs.Title = doc.Title; attrs.Invalidate(); SyncCrumb(); Changed?.Invoke(); }
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
        private List<(string Key, string Value)> items = new();
        private int hover = -1; // 0..n-1 = 줄, n = 추가 줄
        private int editing = -1;
        private bool readOnly;
        private readonly TextBox editor;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public string Title { get; set; } = "";
        public int Count => items.Count;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public bool ReadOnly { get => readOnly; set { readOnly = value; CancelEdit(); Invalidate(); } }
        public event Action<Rectangle>? AddRequested;
        public event Action<int, string>? ValueChanged;
        public event Action<int>? RemoveRequested;

        public AttrBox()
        {
            editor = new TextBox { BorderStyle = BorderStyle.None, Visible = false, Font = Font };
            editor.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndEdit(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(false); }
            };
            editor.LostFocus += (_, _) => EndEdit(true);
            Controls.Add(editor);
        }

        private int RowHt => Font.Height + S(16);
        public void SetItems(List<(string, string)> list) { items = list.ToList(); if (editing >= items.Count) CancelEdit(); Invalidate(); }
        public int PreferredHeight(int width) => items.Count == 0 ? S(16) + RowH * 3 + S(10) : RowHt * (items.Count + 2);

        private int LabelW => items.Count == 0 ? 0 : Math.Clamp(items.Max(it => TextRenderer.MeasureText(it.Key, Font, Size.Empty, TextFormatFlags.NoPadding).Width) + S(60), Width / 4, Width / 2);
        private Rectangle RowRect(int i) => new(0, RowHt * (i + 1), Width, RowHt);
        private Rectangle ValueRect(int i) { var r = RowRect(i); return new Rectangle(LabelW, r.Y, Width - LabelW, r.Height); }
        private Rectangle AddRow => RowRect(items.Count);
        /// <summary>[속성 추가] 줄(비어 있으면 상자 전체)의 화면 좌표.</summary>
        public Rectangle AddAnchorScreen => RectangleToScreen(items.Count == 0 ? new Rectangle(0, 0, Width, Height) : AddRow);
        private int XW => readOnly ? 0 : S(34); // 줄 오른쪽 끝의 ✕(지우기) 칸 폭
        private Rectangle XRect(int i) { var r = RowRect(i); return new Rectangle(r.Right - XW, r.Y, XW, r.Height); }
        private int hoverX = -1;

        private int IndexAt(Point pt)
        {
            if (items.Count == 0) return readOnly ? -1 : 0;
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            int hx = !readOnly && i >= 0 && i < items.Count && XRect(i).Contains(e.Location) ? i : -1;
            if (i != hover || hx != hoverX) { hover = i; hoverX = hx; Invalidate(); }
            Cursor = i >= 0 && !(i < items.Count && e.X < LabelW) ? Cursors.Hand : Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; hoverX = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (readOnly) return;
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
            if (items.Count == 0)
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
            var lav = p.AccentSoft;
            var grid = Themes.Blend(lav, p.Fore, 0.1f);
            int lw = LabelW;
            using var clip = LibraryPalette.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), S(10));
            g.SetClip(clip);
            using (var b = new SolidBrush(lav)) g.FillRectangle(b, 0, 0, Width, Height);
            using var bold = new Font(Font, FontStyle.Bold);
            using var icon = new Font(MainForm.IconFontName(), 10f);
            // 머리: 문서 이름
            TextRenderer.DrawText(g, Title, bold, new Rectangle(S(10), 0, Width - S(20), RowHt), p.Fore, flags | TextFormatFlags.HorizontalCenter);
            using var gridPen = new Pen(grid);
            for (int i = 0; i < items.Count; i++)
            {
                var r = RowRect(i);
                var vr = ValueRect(i);
                using (var b = new SolidBrush(i == hover && !readOnly ? Themes.Blend(surface, p.Accent, 0.06f) : surface)) g.FillRectangle(b, vr.X, vr.Y, vr.Width, vr.Height - 1);
                g.DrawLine(gridPen, 0, r.Y, Width, r.Y);
                TextRenderer.DrawText(g, WikiStore.AttributeGlyph(items[i].Key), icon, new Rectangle(S(12), r.Y, S(20), r.Height), p.Dim, flags | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, items[i].Key, bold, new Rectangle(S(38), r.Y, lw - S(46), r.Height), p.Fore, flags);
                if (editing != i) TextRenderer.DrawText(g, items[i].Value, Font, new Rectangle(vr.X + S(12), vr.Y, vr.Width - S(24) - XW, vr.Height), p.Fore, flags);
                if (!readOnly)
                {
                    var xr = XRect(i);
                    float cx = xr.X + xr.Width / 2f, cy = xr.Y + xr.Height / 2f, d = S(4);
                    using var xp = new Pen(hoverX == i ? p.Accent : p.Dim, Math.Max(1.4f, DeviceDpi / 96f * 1.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(xp, cx - d, cy - d, cx + d, cy + d);
                    g.DrawLine(xp, cx - d, cy + d, cx + d, cy - d);
                }
            }
            if (!readOnly)
            {
                var ar = AddRow;
                g.DrawLine(gridPen, 0, ar.Y, Width, ar.Y);
                if (hover == items.Count) using (var hb = new SolidBrush(Themes.Blend(lav, p.Fore, 0.06f))) g.FillRectangle(hb, ar);
                TextRenderer.DrawText(g, "+  속성 추가", bold, ar, hover == items.Count ? p.Accent : p.Fore, flags | TextFormatFlags.HorizontalCenter);
            }
            g.ResetClip();
        }
    }
}
