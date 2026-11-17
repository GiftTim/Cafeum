using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cafeum;

/// <summary>
/// 사이드바 [국어사전]. 위쪽은 사전 찾기, 아래쪽은 내가 저장한 단어를 그룹으로 모아 두는 단어장(Word List)이다.
/// 단어를 입력하고 Enter(또는 보라색 돋보기)를 누르면 위키낱말사전(ko.wiktionary.org)에서 뜻을 가져와 보여 주고,
/// 한 번 찾은 단어는 이 컴퓨터(dictionary-cache.json)에 저장해 두어서 인터넷이 끊겨도 다시 볼 수 있다.
/// 뜻 위의 [단어장에 저장]으로 단어를 그룹에 넣고, 단어장에서는 그룹을 접고 펴고 · 만들고 · 이름을 바꾸고 · 지우고, 단어를 다른 그룹으로 옮긴다.
/// </summary>
internal sealed class DictionaryPanel : Panel
{
    private readonly SplitContainer split;
    private readonly DictSearchField search;
    private readonly Panel resultBar, linkBar;
    private readonly FlowLayoutPanel linkFlow;
    private readonly Label status;
    private readonly LinkLabel stdictLink, naverLink, addLink;
    private readonly RichTextBox result;
    private readonly WordBookView book;
    private LibraryPalette palette = LibraryPalette.From(Themes.All[0]);
    private CancellationTokenSource? cts;
    private string current = "";
    private bool splitReady, handleHot;

    public DictionaryPanel()
    {
        split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = LogicalToDeviceUnits(18), FixedPanel = FixedPanel.None, TabStop = false };

        // ── 위쪽: 찾기 ──
        search = new DictSearchField { Dock = DockStyle.Top };
        status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), AutoEllipsis = true };
        resultBar = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(30) };
        resultBar.Controls.Add(status);
        result = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, TabStop = false };
        _ = result.Handle; // 아직 화면에 없을 때 Clear · AppendText를 해도 글이 남지 않도록 핸들을 미리 만든다
        linkBar = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(30) };
        stdictLink = MakeLink("표준국어대사전");
        naverLink = MakeLink("네이버 사전");
        addLink = MakeLink("단어장에 넣기");
        addLink.Enabled = false; // 뜻이 나와 있을 때만 눌린다(숨겨서 만들면 글자가 안 들어가는 문제가 있어 늘 보이게 둔다)
        linkFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(8, 4, 0, 0) };
        linkFlow.Controls.AddRange(new Control[] { stdictLink, naverLink, addLink });
        linkBar.Controls.Add(linkFlow);
        // Dock는 나중에 넣은 것부터 자리를 잡는다 — Fill을 먼저, 맨 위에 올 것을 나중에.
        split.Panel1.Controls.AddRange(new Control[] { result, linkBar, resultBar, search });

        // ── 아래쪽: 단어장 ──
        book = new WordBookView { Dock = DockStyle.Fill };
        split.Panel2.Controls.Add(book);
        Controls.Add(split);
        // 위(찾기)와 아래(단어장) 사이의 짧은 선이 끌어서 칸 크기를 바꾸는 손잡이다(위로 끌어올리면 단어장이 커진다)
        split.Paint += (_, e) => PaintHandle(e.Graphics);
        split.MouseMove += (_, e) => { bool over = split.SplitterRectangle.Contains(e.Location); if (over != handleHot) { handleHot = over; split.Invalidate(); } };
        split.MouseLeave += (_, _) => { if (handleHot) { handleHot = false; split.Invalidate(); } };
        split.SplitterMoved += (_, _) => split.Invalidate();

        search.Submit += () => _ = LookupAsync(search.Text, false);
        search.ShiftSubmit += () => _ = LookupAsync(search.Text, true); // Shift+Enter: 저장해 둔 것이 있어도 인터넷에서 새로 받는다
        book.WordChosen += w => { search.Text = w; _ = LookupAsync(w, false); };
        addLink.LinkClicked += (_, _) => SaveCurrent();
        stdictLink.LinkClicked += (_, _) => OpenWeb($"https://stdict.korean.go.kr/search/searchResult.do?pageSize=10&searchKeyword={Uri.EscapeDataString(WordForLink())}");
        naverLink.LinkClicked += (_, _) => OpenWeb($"https://ko.dict.naver.com/#/search?query={Uri.EscapeDataString(WordForLink())}");
        WordBook.Changed += OnBookChanged;
        Disposed += (_, _) => { cts?.Cancel(); cts?.Dispose(); WordBook.Changed -= OnBookChanged; };
        ShowHint();
    }

    private LinkLabel MakeLink(string text) => new() { Text = text, AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 14, 0) };

    private string WordForLink() => current.Length > 0 ? current : search.Text.Trim();

    private static void OpenWeb(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Write("사전 사이트를 열지 못했어요", ex); }
    }

    private void PaintHandle(Graphics g)
    {
        var r = split.SplitterRectangle;
        using (var bg = new SolidBrush(palette.Card)) g.FillRectangle(bg, r);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, half = LogicalToDeviceUnits(46);
        using var pen = new Pen(handleHot ? palette.Accent : Themes.Blend(palette.Card, palette.Accent, 0.65f), LogicalToDeviceUnits(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - half, cy, cx + half, cy);
    }

    /// <summary>사이드바에서 이 화면을 열 때: 입력칸에 커서를 둔다.</summary>
    public void FocusInput() => search.FocusBox();

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (splitReady || Height < LogicalToDeviceUnits(320)) return;
        splitReady = true; // 처음 한 번: 위(찾기) 58% · 아래(단어장) 42%. 그 뒤로는 사용자가 끌어서 바꾼다.
        try
        {
            split.Panel1MinSize = LogicalToDeviceUnits(150);
            split.Panel2MinSize = LogicalToDeviceUnits(170);
            split.SplitterDistance = Math.Clamp((int)(Height * 0.58), split.Panel1MinSize, Math.Max(split.Panel1MinSize, Height - split.Panel2MinSize - split.SplitterWidth));
        }
        catch (Exception ex) { AppLog.Write("사전 칸 나누기를 맞추지 못했어요", ex); }
    }

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = p.Side; ForeColor = p.Fore;
        split.BackColor = p.Line;
        split.Panel1.BackColor = p.Side;
        split.Panel2.BackColor = p.Card;
        search.SetPalette(p);
        resultBar.BackColor = p.Side; linkBar.BackColor = p.Side; linkFlow.BackColor = p.Side;
        status.BackColor = p.Side; status.ForeColor = p.Dim;
        result.BackColor = p.Side; result.ForeColor = p.Fore;
        foreach (var l in new[] { stdictLink, naverLink, addLink }) { l.BackColor = p.Side; l.LinkColor = p.Accent; l.ActiveLinkColor = p.Fore; l.DisabledLinkColor = p.Dim; l.ForeColor = p.Accent; }
        string ff = NovelStore.UiFontFamily;
        status.Font = new Font(ff, 8.5f);
        foreach (var l in new[] { stdictLink, naverLink, addLink }) l.Font = new Font(ff, 9f);
        book.SetPalette(p);
        if (current.Length > 0 && DictionaryCache.TryGet(current, out var item)) Render(item!); else ShowHint();
    }

    // ───────────────────────── 찾기 ─────────────────────────

    private async Task LookupAsync(string raw, bool forceOnline)
    {
        string word = DictionaryCache.Normalize(raw);
        if (word.Length == 0) return;
        cts?.Cancel();
        var my = cts = new CancellationTokenSource();
        current = word;

        if (!forceOnline && DictionaryCache.TryGet(word, out var cached))
        {
            Render(cached!);
            SetStatus("저장해 둔 결과예요 (인터넷 없이 보여 드려요)");
            return;
        }

        SetStatus("찾는 중…");
        try
        {
            var found = await DictionaryClient.FetchAsync(word, my.Token);
            if (my.IsCancellationRequested || IsDisposed) return;
            if (found == null)
            {
                // 사전에 없는 단어. 전에 저장해 둔 것이 있으면 그것을 그대로 둔다.
                if (DictionaryCache.TryGet(word, out var keep)) { Render(keep!); SetStatus("사전에서 새 결과를 찾지 못해 저장해 둔 결과를 보여요"); }
                else ShowMessage($"'{word}'을(를) 사전에서 찾지 못했어요.", "활용한 모양이면 기본형으로 찾아 보세요. (예: 먹었다 → 먹다, 아름다운 → 아름답다)");
                return;
            }
            DictionaryCache.Put(found);
            Render(found);
            SetStatus("인터넷에서 가져와 저장했어요");
        }
        catch (OperationCanceledException) when (my.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (my.IsCancellationRequested || IsDisposed) return;
            AppLog.Write($"사전 조회 실패({word})", ex);
            if (DictionaryCache.TryGet(word, out var offline)) { Render(offline!); SetStatus("인터넷에 연결되지 않아 저장해 둔 결과를 보여요"); }
            else ShowMessage("인터넷에 연결되어 있지 않아요.", "한 번 찾아 본 단어는 인터넷이 없어도 볼 수 있어요. 이 단어는 아직 저장돼 있지 않아서, 연결된 뒤에 다시 찾아 주세요.");
        }
    }

    private void SetStatus(string text) => status.Text = text;

    // ───────────────────────── 단어장에 저장 ─────────────────────────

    private void OnBookChanged() { if (!IsDisposed) UpdateSaveLink(); }

    private void UpdateSaveLink()
    {
        bool has = current.Length > 0 && result.Tag is "result";
        bool saved = has && WordBook.Contains(current);
        addLink.Text = saved ? "✓ 단어장에 있어요" : "단어장에 넣기";
        addLink.Enabled = has && !saved;
    }

    private void SaveCurrent()
    {
        if (current.Length == 0 || WordBook.Contains(current)) return;
        string word = current;
        string gist = DictionaryCache.TryGet(word, out var it) ? DictionaryClient.Gist(it!.Text) : "";
        var groups = WordBook.Groups;
        if (groups.Count == 1) { WordBook.AddWord(groups[0].Id, word, gist); return; }
        var m = new ContextMenuStrip();
        MenuGuard.Attach(m);
        foreach (var g in groups) { var id = g.Id; m.Items.Add(g.Name, null, (_, _) => WordBook.AddWord(id, word, gist)); }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("＋ 새 그룹…", null, (_, _) => BeginInvoke(() =>
        {
            var name = NamePrompt.Ask(FindForm(), palette, "새 그룹", "그룹 이름", "");
            if (name != null) WordBook.AddWord(WordBook.AddGroup(name).Id, word, gist);
        }));
        m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
        m.Show(addLink, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
    }

    // ───────────────────────── 그리기 ─────────────────────────

    private void Add(string text, Font f, Color c)
    {
        result.SelectionStart = result.TextLength;
        result.SelectionLength = 0;
        result.SelectionFont = f;
        result.SelectionColor = c;
        result.AppendText(text);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>결과 칸을 고치는 동안 화면을 멈춰 두었다가 한 번에 그린다(고치는 도중의 반쯤 바뀐 모습이 비치지 않게).</summary>
    private void Batch(Action fill)
    {
        const int WM_SETREDRAW = 0x000B;
        SendMessage(result.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try { fill(); }
        finally { SendMessage(result.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero); result.Invalidate(); }
    }

    private void ShowHint() => Batch(ShowHintCore);
    private void ShowMessage(string title, string detail) => Batch(() => ShowMessageCore(title, detail));
    private void Render(DictionaryEntry item) => Batch(() => RenderCore(item));

    private void ShowHintCore()
    {
        result.Clear();
        result.Tag = "hint";
        using var f = new Font(NovelStore.UiFontFamily, 9.5f);
        Add("단어를 입력하고 Enter를 누르면 뜻이 나와요.\n\n뜻 아래의 [단어장에 넣기]로 단어를 아래 단어장에 모아 두세요. 한 번 찾은 단어는 이 컴퓨터에 저장돼서, 인터넷이 끊겨도 다시 볼 수 있어요.\n\nShift+Enter: 저장해 둔 단어도 인터넷에서 새로 받기", f, palette.Dim);
        SetStatus("");
        UpdateSaveLink();
    }

    private void ShowMessageCore(string title, string detail)
    {
        result.Clear();
        result.Tag = "message";
        string ff = NovelStore.UiFontFamily;
        using var tf = new Font(ff, 11f, FontStyle.Bold);
        using var df = new Font(ff, 9.5f);
        Add(title + "\n\n", tf, palette.Fore);
        Add(detail, df, palette.Dim);
        SetStatus("");
        UpdateSaveLink();
    }

    private void RenderCore(DictionaryEntry item)
    {
        result.Clear();
        result.Tag = "result";
        string ff = NovelStore.UiFontFamily;
        using var title = new Font(ff, 18f, FontStyle.Bold);
        using var h3 = new Font(ff, 11.5f, FontStyle.Bold);
        using var h4 = new Font(ff, 10.5f, FontStyle.Bold);
        using var h5 = new Font(ff, 9f, FontStyle.Bold);
        using var body = new Font(ff, 10f);
        using var small = new Font(ff, 9f);
        Add(item.Word + "\n", title, palette.Fore);
        Add("위키낱말사전 · ko.wiktionary.org\n", small, palette.Dim);
        bool lastBlank = true;
        foreach (var raw in item.Text.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            var m = Regex.Match(line, @"^(={3,6})\s*(.+?)\s*\1$");
            if (m.Success)
            {
                int level = m.Groups[1].Length;
                if (!lastBlank) Add("\n", small, palette.Fore);
                Add(m.Groups[2].Value + "\n", level == 3 ? h3 : level == 4 ? h4 : h5, level == 3 ? palette.Accent : level == 4 ? palette.Fore : palette.Dim);
                lastBlank = true;
                continue;
            }
            if (line.Length == 0) { if (!lastBlank) { Add("\n", small, palette.Fore); lastBlank = true; } continue; }
            bool meta = line.Contains("IPA") || line.StartsWith("어원:") || line.StartsWith("발음");
            Add(line + "\n", meta ? small : body, meta ? palette.Dim : palette.Fore);
            lastBlank = false;
        }
        result.SelectionStart = 0;
        result.ScrollToCaret();
        UpdateSaveLink();
    }
}

// ═══════════════════════════ 찾기 입력칸 ═══════════════════════════

/// <summary>둥근 테두리 입력칸 + 오른쪽 보라색 돋보기 단추. Enter나 단추로 Submit, Shift+Enter는 ShiftSubmit.</summary>
internal sealed class DictSearchField : Control
{
    private readonly TextBox box = new() { BorderStyle = BorderStyle.None, PlaceholderText = "단어 검색..." };
    private LibraryPalette? p;
    private bool hotButton;
    public event Action? Submit;
    public event Action? ShiftSubmit;

    private static readonly Color ButtonColor = Color.FromArgb(0xD8, 0xC4, 0xFF);
    private static readonly Color ButtonHover = PurpleIcons.Purple;

    public override string Text { get => box.Text; set => box.Text = value; }

    public DictSearchField()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(box);
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            if (e.Shift) ShiftSubmit?.Invoke(); else Submit?.Invoke();
        };
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    protected override Size DefaultSize => new(200, 56);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(56); LayoutBox(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }

    private Rectangle Frame => new(S(6), S(7), Math.Max(S(60), Width - S(12)), Math.Max(S(20), Height - S(14)));
    private Rectangle ButtonRect => new(Frame.Right - S(46), Frame.Y + S(3), S(40), Frame.Height - S(6));

    private void LayoutBox()
    {
        box.Font = new Font(NovelStore.UiFontFamily, 11f);
        box.Location = new Point(Frame.X + S(14), Frame.Y + (Frame.Height - box.PreferredHeight) / 2);
        box.Width = Math.Max(20, ButtonRect.X - Frame.X - S(22));
    }

    public void FocusBox() { box.Focus(); box.SelectAll(); }

    public void SetPalette(LibraryPalette pal)
    {
        p = pal;
        BackColor = pal.Side;
        box.BackColor = pal.Card; box.ForeColor = pal.Fore;
        LayoutBox();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool h = ButtonRect.Contains(e.Location);
        if (h != hotButton) { hotButton = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hotButton) { hotButton = false; Cursor = Cursors.Default; Invalidate(); } }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && ButtonRect.Contains(e.Location)) Submit?.Invoke();
        else box.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var pal = p ?? LibraryPalette.From(Themes.All[0]);
        g.Clear(pal.Side);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = LibraryPalette.Rounded(Frame, S(12)))
        {
            using var fill = new SolidBrush(pal.Card); g.FillPath(fill, path);
            using var pen = new Pen(pal.Line); g.DrawPath(pen, path);
        }
        var br = ButtonRect;
        using (var bp = LibraryPalette.Rounded(br, S(9))) { using var bb = new SolidBrush(hotButton ? ButtonHover : ButtonColor); g.FillPath(bb, bp); }
        // 흰 돋보기
        float cx = br.X + br.Width / 2f - S(2), cy = br.Y + br.Height / 2f - S(2), r = S(6);
        using var lp = new Pen(Color.White, S(2) + 0.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(lp, cx - r, cy - r, r * 2, r * 2);
        g.DrawLine(lp, cx + r * 0.75f, cy + r * 0.75f, cx + r * 1.7f, cy + r * 1.7f);
    }
}

// ═══════════════════════════ 단어장 (자료) ═══════════════════════════

/// <summary>단어장 안의 단어 한 개: 단어 · 뜻 한 줄 요약.</summary>
internal sealed class WordItem
{
    public string Word { get; set; } = "";
    public string Gist { get; set; } = "";
    public long Added { get; set; }
}

/// <summary>단어를 모아 두는 그룹.</summary>
internal sealed class WordGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
    public List<WordItem> Words { get; set; } = new();
}

/// <summary>
/// 내 단어장(앱 데이터 폴더의 wordbook.json). 그룹마다 단어를 모은다. 그룹이 하나도 없으면 "기본" 그룹을 만든다.
/// 쓰다 끊겨도 예전 파일이 남도록 임시 파일에 쓴 뒤 바꾸고, 파일이 깨져 있으면 빈 단어장으로 시작한다.
/// </summary>
internal static class WordBook
{
    private static List<WordGroup>? groups;
    private static string FilePath => Path.Combine(AppSettings.DataDir, "wordbook.json");
    public static event Action? Changed;

    public static List<WordGroup> Groups
    {
        get
        {
            if (groups != null) return groups;
            groups = new();
            try
            {
                if (File.Exists(FilePath)) groups = JsonSerializer.Deserialize<List<WordGroup>>(File.ReadAllText(FilePath)) ?? new();
            }
            catch (Exception ex) { AppLog.Write("단어장 파일을 읽지 못했어요(빈 단어장으로 시작)", ex); groups = new(); }
            if (groups.Count == 0) groups.Add(new WordGroup { Name = "기본" });
            return groups;
        }
    }

    public static bool Contains(string word)
    {
        string w = DictionaryCache.Normalize(word);
        return Groups.Any(g => g.Words.Any(x => DictionaryCache.Normalize(x.Word) == w));
    }

    public static WordGroup AddGroup(string name)
    {
        var g = new WordGroup { Name = name.Trim() };
        Groups.Add(g);
        Save();
        return g;
    }

    public static void RenameGroup(WordGroup g, string name) { name = name.Trim(); if (name.Length == 0) return; g.Name = name; Save(); }

    public static void DeleteGroup(WordGroup g)
    {
        if (Groups.Count <= 1) return; // 마지막 그룹은 지울 수 없다
        Groups.Remove(g);
        Save();
    }

    public static void ToggleGroup(WordGroup g) { g.Collapsed = !g.Collapsed; Save(); }

    public static void AddWord(string groupId, string word, string gist)
    {
        var g = Groups.FirstOrDefault(x => x.Id == groupId);
        word = DictionaryCache.Normalize(word);
        if (g == null || word.Length == 0 || Contains(word)) return;
        g.Collapsed = false;
        g.Words.Insert(0, new WordItem { Word = word, Gist = gist, Added = DateTime.UtcNow.Ticks });
        Save();
    }

    public static void RemoveWord(WordGroup g, WordItem w) { g.Words.Remove(w); Save(); }

    public static void MoveWord(WordGroup from, WordItem w, WordGroup to)
    {
        if (from == to) return;
        from.Words.Remove(w);
        to.Words.Insert(0, w);
        to.Collapsed = false;
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Groups));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { AppLog.Write("단어장을 저장하지 못했어요", ex); }
        Changed?.Invoke();
    }
}

// ═══════════════════════════ 단어장 (화면) ═══════════════════════════

/// <summary>
/// 단어장 화면: 위에 "Word List" 제목과 [+ Group] 단추(고정), 그 밑에 그룹 줄(눌러서 접고 펴기)과 그 그룹의 단어들(휠로 스크롤).
/// 단어를 누르면 WordChosen, 우클릭하면 [다른 그룹으로 옮기기 · 삭제], 그룹 줄 우클릭은 [이름 바꾸기 · 삭제 · 새 그룹].
/// </summary>
internal sealed class WordBookView : Control
{
    private sealed record Row(WordGroup Group, WordItem? Word, Rectangle R);

    private LibraryPalette? p;
    private readonly List<Row> rows = new();
    private Rectangle addRect;
    private int scroll, hot = -1, contentH;
    private bool hotAdd;
    public event Action<string>? WordChosen;

    public WordBookView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        WordBook.Changed += Redraw;
        Disposed += (_, _) => WordBook.Changed -= Redraw;
    }

    private void Redraw() { if (!IsDisposed) Invalidate(); }
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int HeaderH => S(92);
    public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Card; Invalidate(); }

    private static string ScriptFont()
    {
        using var fc = new InstalledFontCollection();
        foreach (var n in new[] { "Segoe Script", "Lucida Handwriting", "Brush Script MT" })
            if (fc.Families.Any(f => f.Name == n)) return n;
        return NovelStore.UiFontFamily;
    }
    private static readonly string TitleFont = ScriptFont();

    // ── 행 배치 ──
    private void LayoutRows()
    {
        rows.Clear();
        int y = HeaderH - scroll, w = Width - S(16);
        foreach (var g in WordBook.Groups)
        {
            rows.Add(new Row(g, null, new Rectangle(S(8), y, w, S(36))));
            y += S(40);
            if (g.Collapsed) continue;
            if (g.Words.Count == 0) { y += S(30); continue; }
            foreach (var wd in g.Words) { rows.Add(new Row(g, wd, new Rectangle(S(8), y, w, S(32)))); y += S(32); }
            y += S(4);
        }
        contentH = y + scroll - HeaderH;
    }
    private int MaxScroll() => Math.Max(0, contentH + S(8) - (Height - HeaderH));

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        LayoutRows();
        int ns = Math.Clamp(scroll - e.Delta / 120 * S(48), 0, MaxScroll());
        if (ns != scroll) { scroll = ns; Invalidate(); }
    }

    private int HitRow(Point pt)
    {
        if (pt.Y < HeaderH) return -1;
        LayoutRows();
        return rows.FindIndex(r => r.R.Contains(pt));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool add = addRect.Contains(e.Location);
        int h = add ? -1 : HitRow(e.Location);
        if (h != hot || add != hotAdd) { hot = h; hotAdd = add; Cursor = add || h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; hotAdd = false; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (addRect.Contains(e.Location) && e.Button == MouseButtons.Left) { NewGroup(); return; }
        int h = HitRow(e.Location);
        if (h < 0) { if (e.Button == MouseButtons.Right) EmptyMenu(e.Location); return; }
        var row = rows[h];
        if (e.Button == MouseButtons.Right) { if (row.Word == null) GroupMenu(row.Group, e.Location); else WordMenu(row.Group, row.Word, e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        if (row.Word == null) WordBook.ToggleGroup(row.Group); else WordChosen?.Invoke(row.Word.Word);
    }

    // ── 메뉴 · 입력 ──
    private void NewGroup()
    {
        if (p == null) return;
        var name = NamePrompt.Ask(FindForm(), p, "새 그룹", "그룹 이름", "");
        if (name != null) WordBook.AddGroup(name);
    }

    private ContextMenuStrip NewMenu()
    {
        var m = new ContextMenuStrip();
        MenuGuard.Attach(m);
        m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
        return m;
    }

    private void EmptyMenu(Point at)
    {
        var m = NewMenu();
        m.Items.Add("새 그룹…", null, (_, _) => BeginInvoke(NewGroup));
        m.Show(this, at);
    }

    private void GroupMenu(WordGroup g, Point at)
    {
        var m = NewMenu();
        m.Items.Add("이름 바꾸기…", null, (_, _) => BeginInvoke(() =>
        {
            if (p == null) return;
            var name = NamePrompt.Ask(FindForm(), p, "그룹 이름 바꾸기", "그룹 이름", g.Name);
            if (name != null) WordBook.RenameGroup(g, name);
        }));
        var del = new ToolStripMenuItem("그룹 삭제") { Enabled = WordBook.Groups.Count > 1 }; // 마지막 그룹은 지울 수 없다
        del.Click += (_, _) => BeginInvoke(() =>
        {
            string msg = g.Words.Count == 0 ? $"'{g.Name}' 그룹을 지울까요?" : $"'{g.Name}' 그룹과 그 안의 단어 {g.Words.Count}개를 지울까요?\n되돌릴 수 없어요.";
            if (MessageBox.Show(FindForm(), msg, "그룹 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) WordBook.DeleteGroup(g);
        });
        m.Items.Add(del);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("새 그룹…", null, (_, _) => BeginInvoke(NewGroup));
        m.Show(this, at);
    }

    private void WordMenu(WordGroup g, WordItem w, Point at)
    {
        var m = NewMenu();
        var move = new ToolStripMenuItem("다른 그룹으로 옮기기") { Enabled = WordBook.Groups.Count > 1 };
        foreach (var to in WordBook.Groups.Where(x => x != g)) { var dest = to; move.DropDownItems.Add(to.Name, null, (_, _) => WordBook.MoveWord(g, w, dest)); }
        m.Items.Add(move);
        m.Items.Add("단어장에서 삭제", null, (_, _) => WordBook.RemoveWord(g, w));
        m.Show(this, at);
    }

    // ── 그리기 ──
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var pal = p ?? LibraryPalette.From(Themes.All[0]);
        g.Clear(pal.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        LayoutRows();
        scroll = Math.Clamp(scroll, 0, MaxScroll());
        LayoutRows();
        string ff = NovelStore.UiFontFamily;
        const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        Color dim = Themes.Blend(pal.Card, pal.Fore, 0.55f);
        Color groupFill = Themes.Blend(pal.Card, pal.Side, 0.6f);

        // 1) 그룹 줄 · 단어 줄 (글자는 영역 밖으로도 그려질 수 있어서, 아래에서 머리 영역을 다시 덮는다)
        using var gf = new Font(ff, 11f);
        using var wf = new Font(ff, 10.5f);
        using var sf = new Font(ff, 9f);
        for (int i = 0; i < rows.Count; i++)
        {
            var (grp, word, r) = rows[i];
            if (r.Bottom < HeaderH || r.Top > Height) continue;
            if (word == null)
            {
                using (var path = LibraryPalette.Rounded(r, S(8))) { using var fb = new SolidBrush(i == hot ? pal.CardHover : groupFill); g.FillPath(fb, path); }
                DrawListIcon(g, new Rectangle(r.X + S(16), r.Y + (r.Height - S(16)) / 2, S(18), S(16)), dim);
                var nameSize = TextRenderer.MeasureText(g, grp.Name, gf, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int nameW = Math.Min(nameSize.Width, Math.Max(S(20), r.Width - S(110)));
                TextRenderer.DrawText(g, grp.Name, gf, new Rectangle(r.X + S(44), r.Y, nameW, r.Height), pal.Fore, left);
                DrawChevron(g, r.X + S(44) + nameW + S(14), r.Y + r.Height / 2, !grp.Collapsed, dim);
                TextRenderer.DrawText(g, grp.Words.Count.ToString(), sf, new Rectangle(r.Right - S(40), r.Y, S(30), r.Height), dim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                if (i == hot) { using var path = LibraryPalette.Rounded(r, S(8)); using var hb = new SolidBrush(pal.CardHover); g.FillPath(hb, path); }
                var wsz = TextRenderer.MeasureText(g, word.Word, wf, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int ww = Math.Min(wsz.Width, r.Width / 2);
                TextRenderer.DrawText(g, word.Word, wf, new Rectangle(r.X + S(34), r.Y, ww, r.Height), pal.Fore, left);
                if (word.Gist.Length > 0)
                    TextRenderer.DrawText(g, word.Gist, sf, new Rectangle(r.X + S(34) + ww + S(10), r.Y, Math.Max(0, r.Width - S(34) - ww - S(20)), r.Height), dim, left);
            }
        }
        // 빈 그룹 안내
        {
            int y = HeaderH - scroll;
            foreach (var grp in WordBook.Groups)
            {
                y += S(40);
                if (grp.Collapsed) continue;
                if (grp.Words.Count == 0)
                {
                    if (y > HeaderH - S(30) && y < Height) TextRenderer.DrawText(g, "아직 저장한 단어가 없어요", sf, new Rectangle(S(42), y, Math.Max(10, Width - S(60)), S(26)), dim, left);
                    y += S(30);
                    continue;
                }
                y += grp.Words.Count * S(32) + S(4);
            }
        }

        // 2) 머리(Word List · + Group)를 바탕으로 덮고 다시 그린다: 위로 넘친 줄이 제목 위에 겹쳐 보이지 않게
        using (var cover = new SolidBrush(pal.Card)) g.FillRectangle(cover, 0, 0, Width, HeaderH);
        using (var tf = new Font(TitleFont, 15f, FontStyle.Bold))
            TextRenderer.DrawText(g, "Word List", tf, new Rectangle(0, S(14), Width, S(34)), pal.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        addRect = new Rectangle(Width - S(8) - S(104), S(52), S(104), S(32));
        using (var ap = LibraryPalette.Rounded(addRect, S(7)))
        {
            if (hotAdd) { using var hb = new SolidBrush(pal.CardHover); g.FillPath(hb, ap); }
            using var pen = new Pen(Themes.Blend(pal.Card, pal.Fore, 0.45f), S(1) + 0.6f); g.DrawPath(pen, ap);
        }
        using (var pp = new Pen(pal.Accent, S(1) + 0.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float pcx = addRect.X + S(16), pcy = addRect.Y + addRect.Height / 2f, arm = S(6);
            g.DrawLine(pp, pcx - arm, pcy, pcx + arm, pcy);
            g.DrawLine(pp, pcx, pcy - arm, pcx, pcy + arm);
        }
        using (var gtf = new Font(TitleFont, 10.5f, FontStyle.Bold))
            TextRenderer.DrawText(g, "Group", gtf, new Rectangle(addRect.X + S(30), addRect.Y, addRect.Width - S(34), addRect.Height), pal.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    /// <summary>목록 아이콘(점 + 가로줄 셋).</summary>
    private void DrawListIcon(Graphics g, Rectangle r, Color c)
    {
        using var pen = new Pen(c, S(1) + 0.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float x0 = r.X, x1 = r.X + r.Width * 0.32f, x2 = r.Right;
        for (int k = 0; k < 3; k++)
        {
            float y = r.Y + r.Height * (0.15f + k * 0.35f);
            g.DrawLine(pen, x0, y, x0 + S(1), y);
            g.DrawLine(pen, x1 + (k == 0 ? 0 : S(4)), y, x2, y);
        }
    }

    private void DrawChevron(Graphics g, int cx, int cy, bool down, Color c)
    {
        using var pen = new Pen(c, S(1) + 0.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float w = S(5), h = S(3);
        if (down) g.DrawLines(pen, new[] { new PointF(cx - w, cy - h), new PointF(cx, cy + h), new PointF(cx + w, cy - h) });
        else g.DrawLines(pen, new[] { new PointF(cx - h, cy - w), new PointF(cx + h, cy), new PointF(cx - h, cy + w) });
    }
}

// ═══════════════════════════ 이름 묻는 작은 창 ═══════════════════════════

/// <summary>그룹 이름 같은 한 줄을 묻는 작은 창. 확인하면 이름(앞뒤 공백 제거, 비면 null), 취소하면 null.</summary>
internal static class NamePrompt
{
    public static string? Ask(IWin32Window? owner, LibraryPalette p, string title, string label, string initial)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96f, 96f),
            ClientSize = new Size(340, 118), BackColor = p.Side, ForeColor = p.Fore, Font = new Font(NovelStore.UiFontFamily, 10f),
        };
        var lb = new Label { Text = label, Left = 16, Top = 14, Width = 308, Height = 22, ForeColor = p.Dim };
        var box = new TextBox { Left = 16, Top = 38, Width = 308, Text = initial, BackColor = p.Card, ForeColor = p.Fore, BorderStyle = BorderStyle.FixedSingle, MaxLength = 40 };
        var ok = new Button { Text = "확인", Left = 154, Top = 76, Width = 80, Height = 30, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = p.Card, ForeColor = p.Fore };
        var cancel = new Button { Text = "취소", Left = 244, Top = 76, Width = 80, Height = 30, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = p.Card, ForeColor = p.Fore };
        f.Controls.AddRange(new Control[] { lb, box, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Shown += (_, _) => { f.Activate(); box.Focus(); box.SelectAll(); };
        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        string s = box.Text.Trim();
        return s.Length == 0 ? null : s;
    }
}

// ═══════════════════════════ 사전 자료 · 저장 · 가져오기 ═══════════════════════════

/// <summary>저장해 둔 사전 결과 한 건.</summary>
internal sealed class DictionaryEntry
{
    public string Word { get; set; } = "";
    public string Text { get; set; } = "";
    public long Used { get; set; }
}

/// <summary>
/// 한 번 찾은 사전 결과를 이 컴퓨터(dictionary-cache.json)에 저장해 둔다. 인터넷이 끊겨도 저장된 단어는 볼 수 있다.
/// 최대 3000단어까지 두고, 넘치면 가장 오래 안 본 것부터 지운다. 파일이 깨져 있으면 빈 상태로 시작한다.
/// </summary>
internal static class DictionaryCache
{
    private const int MaxEntries = 3000;
    private static readonly object gate = new();
    private static Dictionary<string, DictionaryEntry>? map;
    private static string FilePath => Path.Combine(AppSettings.DataDir, "dictionary-cache.json");

    /// <summary>앞뒤 공백을 자르고 유니코드를 한 모양으로 맞춘다(같은 글자가 다른 키로 저장되지 않게).</summary>
    public static string Normalize(string? w) => (w ?? "").Trim().Normalize(NormalizationForm.FormC);

    private static Dictionary<string, DictionaryEntry> Map()
    {
        if (map != null) return map;
        map = new Dictionary<string, DictionaryEntry>();
        try
        {
            if (File.Exists(FilePath))
                foreach (var e in JsonSerializer.Deserialize<List<DictionaryEntry>>(File.ReadAllText(FilePath)) ?? new())
                    if (!string.IsNullOrEmpty(e.Word) && !string.IsNullOrEmpty(e.Text)) map[Normalize(e.Word)] = e;
        }
        catch (Exception ex) { AppLog.Write("사전 저장 파일을 읽지 못했어요(빈 상태로 시작)", ex); map = new(); }
        return map;
    }

    public static bool TryGet(string word, out DictionaryEntry? item)
    {
        lock (gate)
        {
            if (Map().TryGetValue(Normalize(word), out var e)) { e.Used = DateTime.UtcNow.Ticks; item = e; return true; }
            item = null; return false;
        }
    }

    public static void Put(DictionaryEntry e)
    {
        lock (gate)
        {
            var m = Map();
            e.Word = Normalize(e.Word);
            e.Used = DateTime.UtcNow.Ticks;
            m[e.Word] = e;
            if (m.Count > MaxEntries)
                foreach (var old in m.Values.OrderBy(x => x.Used).Take(m.Count - MaxEntries).ToList()) m.Remove(old.Word);
            Save(m);
        }
    }

    private static void Save(Dictionary<string, DictionaryEntry> m)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m.Values.ToList()));
            File.Move(tmp, FilePath, true); // 쓰다 끊겨도 예전 파일이 남도록 임시 파일에 쓴 뒤 바꾼다
        }
        catch (Exception ex) { AppLog.Write("사전 결과를 저장하지 못했어요", ex); }
    }
}

/// <summary>위키낱말사전(ko.wiktionary.org)에서 한국어 항목을 가져온다(열쇠가 필요 없는 공개 API).</summary>
internal static class DictionaryClient
{
    private static readonly HttpClient http = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Cafeum/{Updater.Current} (https://github.com/{Updater.RepoSlug})"); // 위키미디어는 프로그램 이름이 있는 User-Agent를 요구한다
        return c;
    }

    /// <summary>한국어 항목의 글. 사전에 없거나 한국어 항목이 없으면 null. 인터넷이 안 되면 예외.</summary>
    public static async Task<DictionaryEntry?> FetchAsync(string word, CancellationToken ct)
    {
        string url = "https://ko.wiktionary.org/w/api.php?action=query&prop=extracts&explaintext=1&exsectionformat=wiki&redirects=1&format=json&titles=" + Uri.EscapeDataString(word);
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages)) return null;
        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.TryGetProperty("missing", out _)) return null;
            string text = page.Value.TryGetProperty("extract", out var ex) ? ex.GetString() ?? "" : "";
            string? korean = KoreanSection(text);
            if (korean == null) return null;
            return new DictionaryEntry { Word = word, Text = korean };
        }
        return null;
    }

    /// <summary>"== 한국어 ==" 부터 다음 언어 제목("== 일본어 ==" 등) 앞까지(= 두 개짜리 제목만 언어 구분이다).</summary>
    internal static string? KoreanSection(string text)
    {
        text = text.Replace("\r", "");
        var start = Regex.Match(text, @"^==\s*한국어\s*==\s*$", RegexOptions.Multiline);
        if (!start.Success) return null;
        string rest = text[(start.Index + start.Length)..];
        var next = Regex.Match(rest, @"^==\s*[^=].*?\s*==\s*$", RegexOptions.Multiline);
        string section = (next.Success ? rest[..next.Index] : rest).Trim();
        section = Regex.Replace(section, @"\n{3,}", "\n\n");
        return section.Length == 0 ? null : section;
    }

    /// <summary>단어장에 같이 적어 둘 한 줄 뜻: 제목 · 발음 · 어원 줄을 건너뛴 첫 줄(앞의 "1." 번호는 뗀다).</summary>
    internal static string Gist(string text)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || Regex.IsMatch(line, @"^={3,6}") || line.Contains("IPA") || line.StartsWith("어원") || line.StartsWith("발음") || line.StartsWith("활용") || line.StartsWith("(표준어")) continue;
            line = Regex.Replace(line, @"^\d+\.\s*", "");
            return line.Length > 60 ? line[..60] + "…" : line;
        }
        return "";
    }
}
