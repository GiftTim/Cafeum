using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Cafeum;

/// <summary>
/// 사전 창: 작업하던 화면 위에 따로 떠 있는 작은 창(왼쪽 아이콘 막대의 [사전]으로 켜고 끈다).
/// 위 탭으로 [국어사전 · 한영 사전 · 영한 사전]을 오가며 단어를 찾고(위키낱말사전, 한 번 찾은 단어는 이 컴퓨터에 저장돼서 인터넷이 끊겨도 다시 볼 수 있다),
/// 오른쪽 위 [단어장] 탭은 내가 넣어 둔 단어를 그룹별로 보여 준다. 단어장에서 단어를 찾으면 그 단어가 어느 그룹에 있는지 바로 보인다.
/// 머리 줄의 ⌄를 누르면 머리 줄만 남게 접힌다. 머리 줄을 끌면 옮기고, 가장자리를 끌면 크기를 바꾼다. 놓은 자리와 크기, 탭은 기억한다.
/// </summary>
internal sealed class DictionaryWindow : Form
{
    private const int MinW = 320, MinH = 260; // 96dpi 기준(화면 배율에 맞춰 곱한다)

    private readonly DictChrome chrome;
    private readonly Panel body, dictView, bookView, groupBar, bottomBar, resultHost;
    private readonly DictSearchField search;
    private readonly RichTextBox result;
    private readonly Label status;
    private readonly TextButton addButton;
    private readonly GroupButton groupButton;
    private readonly WordBookView book;
    private LibraryPalette palette;
    private CancellationTokenSource? cts;
    private int tab;                    // 0 국어사전 · 1 한영 · 2 영한 · 3 단어장
    private bool collapsed, restoring;
    private int expandedHeight;
    private string dictText = "", bookText = "";
    private string current = "";
    private int gen; // 찾기 차례 번호: 탭을 바꾸거나 새로 찾으면 올라가서, 늦게 도착한 옛 결과는 그리지 않는다

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000; /* CS_DROPSHADOW */ return cp; } }
    protected override bool ShowWithoutActivation => false;

    public DictionaryWindow(LibraryPalette pal)
    {
        palette = pal;
        Text = "사전";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        DoubleBuffered = true;
        Icon = AppShared.LoadAppIcon();
        Size = new Size(340, 398);
        MinimumSize = new Size(MinW, MinH);
        Padding = new Padding(4); // 가장자리 띠: 여기를 끌어 크기를 바꾼다(WndProc)

        chrome = new DictChrome();
        search = new DictSearchField { Dock = DockStyle.Top };
        groupButton = new GroupButton { Size = new Size(112, 32) };
        groupBar = new Panel { Dock = DockStyle.Top, Height = 50, Visible = false };
        groupBar.Controls.Add(groupButton);
        groupBar.Resize += (_, _) => groupButton.Location = new Point(groupBar.Width - groupButton.Width - 8, (groupBar.Height - groupButton.Height) / 2);

        status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), AutoEllipsis = true };
        addButton = new TextButton { Dock = DockStyle.Right, Width = 170, Text = "단어장에 넣기", Enabled = false };
        bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        bottomBar.Controls.AddRange(new Control[] { status, addButton });
        result = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, TabStop = false };
        resultHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 12, 0) }; // 글 양옆 여백
        resultHost.Controls.Add(result);
        _ = result.Handle; // 아직 화면에 없을 때 Clear · AppendText를 해도 글이 남지 않도록 핸들을 미리 만든다
        dictView = new Panel { Dock = DockStyle.Fill };
        dictView.Controls.AddRange(new Control[] { resultHost, bottomBar }); // Dock는 나중에 넣은 것부터 자리를 잡는다 — Fill을 먼저

        book = new WordBookView { Dock = DockStyle.Fill };
        bookView = new Panel { Dock = DockStyle.Fill, Visible = false };
        bookView.Controls.Add(book);

        body = new Panel { Dock = DockStyle.Fill };
        body.Controls.AddRange(new Control[] { dictView, bookView, groupBar, search });
        Controls.AddRange(new Control[] { body, chrome });

        chrome.TabChanged += SetTab;
        chrome.ToggleRequested += ToggleCollapse;
        chrome.CloseRequested += Close;
        search.Submit += () => { if (tab < 3) _ = LookupAsync(search.Text, false); };
        search.ShiftSubmit += () => { if (tab < 3) _ = LookupAsync(search.Text, true); }; // Shift+Enter: 저장해 둔 것이 있어도 인터넷에서 새로 받는다
        search.TextEdited += () => { if (!restoring && tab == 3) book.Filter = search.Text; };
        groupButton.Click += (_, _) => book.NewGroup();
        addButton.Click += (_, _) => SaveCurrent();
        book.WordChosen += w => { dictText = w.Word; SetTab(Math.Clamp(w.Kind, 0, 2)); }; // 단어장에서 누른 단어는 그 사전 탭으로 열린다
        WordBook.Changed += OnBookChanged;
        ResizeEnd += (_, _) => SaveState();
        FormClosing += (_, _) => { SaveState(); cts?.Cancel(); };
        FormClosed += (_, _) => { WordBook.Changed -= OnBookChanged; cts?.Dispose(); };

        LoadState();
        ApplyTheme(pal);
        ShowHint();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    // ───────────────────────── 위치 · 접기 · 기억 ─────────────────────────

    private string SavedState => AppShared.Settings.DictWindow;

    private void LoadState()
    {
        Load += (_, _) =>
        {
            MinimumSize = new Size(S(MinW), S(MinH));
            Padding = new Padding(S(4));
            if (string.IsNullOrEmpty(SavedState)) Size = new Size(S(340), S(398)); // 저장된 크기가 없을 때의 기본 크기
        };
        var parts = (SavedState ?? "").Split(',');
        if (parts.Length >= 6 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y) && int.TryParse(parts[2], out int w) && int.TryParse(parts[3], out int h)
            && int.TryParse(parts[5], out int t))
        {
            tab = Math.Clamp(t, 0, 3);
            Load += (_, _) =>
            {
                var r = new Rectangle(x, y, Math.Max(S(MinW), w), Math.Max(S(MinH), h));
                if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) { Bounds = r; expandedHeight = r.Height; }
                else PlaceNearOwner();
                if (parts[4] == "1") ToggleCollapse();
            };
        }
        else Load += (_, _) => PlaceNearOwner();
        Load += (_, _) => { chrome.Tab = tab; ApplyTabView(); };
    }

    private void PlaceNearOwner()
    {
        var wa = Screen.FromControl(Owner ?? (Control)this).WorkingArea;
        var o = Owner;
        int x = o != null ? o.Right - Width - S(24) : wa.Right - Width - S(24);
        int y = o != null ? o.Top + S(90) : wa.Top + S(90);
        Location = new Point(Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width)), Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
        expandedHeight = Height;
    }

    private void SaveState()
    {
        try
        {
            int h = collapsed ? expandedHeight : Height;
            AppShared.Settings.DictWindow = $"{Left},{Top},{Width},{h},{(collapsed ? 1 : 0)},{tab}";
            AppShared.Settings.Save();
        }
        catch (Exception ex) { AppLog.Write("사전 창 위치를 저장하지 못했어요", ex); }
    }

    /// <summary>머리 줄만 남게 접고(다시 누르면 펼친다).</summary>
    private void ToggleCollapse()
    {
        if (!collapsed)
        {
            expandedHeight = Height;
            collapsed = true;
            body.Visible = false;
            chrome.Collapsed = true;
            MinimumSize = new Size(S(MinW), chrome.Height + Padding.Vertical);
            Height = chrome.Height + Padding.Vertical;
        }
        else
        {
            collapsed = false;
            chrome.Collapsed = false;
            MinimumSize = new Size(S(MinW), S(MinH));
            Height = Math.Max(S(MinH), expandedHeight);
            body.Visible = true;
        }
        SaveState();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084 /* WM_NCHITTEST */)
        {
            base.WndProc(ref m);
            if ((int)m.Result == 1 /* HTCLIENT */)
            {
                long lp = m.LParam.ToInt64();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int g = Padding.Left; bool l = pt.X < g, r = pt.X >= Width - g, t = pt.Y < g && !collapsed, b = pt.Y >= Height - g && !collapsed;
                if (l || r || t || b)
                    m.Result = (IntPtr)(t ? (l ? 13 : r ? 14 : 12) : b ? (l ? 16 : r ? 17 : 15) : l ? 10 : 11);
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(palette.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    // ───────────────────────── 테마 ─────────────────────────

    public void ApplyTheme(LibraryPalette p)
    {
        palette = p;
        BackColor = p.Main; ForeColor = p.Fore;
        chrome.SetPalette(p);
        foreach (var c in new Control[] { body, dictView, bookView, groupBar, bottomBar, resultHost }) c.BackColor = p.Main;
        search.SetPalette(p);
        status.BackColor = p.Main; status.ForeColor = p.Dim; status.Font = new Font(NovelStore.UiFontFamily, 8.5f);
        result.BackColor = p.Main; result.ForeColor = p.Fore;
        addButton.SetPalette(p);
        groupButton.SetPalette(p);
        book.SetPalette(p);
        Invalidate();
        if (current.Length > 0 && DictionaryCache.TryGet(Kind, current, out var item)) Render(item!); else ShowHint();
    }

    // ───────────────────────── 탭 ─────────────────────────

    private int Kind => Math.Min(tab, 2);

    private void SetTab(int t)
    {
        if (t == tab && chrome.Tab == t && IsHandleCreated) { return; }
        cts?.Cancel();
        gen++;
        if (tab == 3) bookText = search.Text; else dictText = search.Text;
        tab = t;
        chrome.Tab = t;
        ApplyTabView();
        if (!collapsed) SaveState();
    }

    /// <summary>탭에 맞게 화면을 바꾼다(단어장이면 단어장 · [+ Group] · 찾기 / 사전이면 찾기 · 뜻 · [단어장에 넣기]).</summary>
    private void ApplyTabView()
    {
        restoring = true;
        search.Text = tab == 3 ? bookText : dictText;
        restoring = false;
        bool isBook = tab == 3;
        dictView.Visible = !isBook;
        bookView.Visible = isBook;
        groupBar.Visible = isBook;
        current = "";
        chrome.Title = isBook ? "단어장" : "사전";
        if (isBook) { book.Filter = search.Text; return; }
        ShowHint();
        if (search.Text.Trim().Length > 0) _ = LookupAsync(search.Text, false); // 탭을 바꾸면 같은 단어를 그 사전에서 바로 다시 찾는다
    }

    // ───────────────────────── 찾기 ─────────────────────────

    private async Task LookupAsync(string raw, bool forceOnline)
    {
        string word = DictionaryCache.Normalize(raw);
        if (word.Length == 0 || tab == 3) return;
        int kind = Kind;
        cts?.Cancel();
        var my = cts = new CancellationTokenSource();
        int myGen = ++gen;
        current = word;

        if (!forceOnline && DictionaryCache.TryGet(kind, word, out var cached))
        {
            Render(cached!);
            SetStatus("저장해 둔 결과예요 (인터넷 없이 보여 드려요)");
            return;
        }

        SetStatus("찾는 중…");
        DictionaryEntry? found = null;
        Exception? error = null;
        try { found = await DictionaryClient.FetchAsync(kind, word, my.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (my.IsCancellationRequested) { return; }
        catch (Exception ex) { error = ex; }

        // 받은 뒤의 그리기는 늘 화면 스레드에서 한다(어느 스레드에서 돌아오든 결과 칸에 두 곳에서 동시에 쓰지 않도록)
        void Apply()
        {
            if (my.IsCancellationRequested || IsDisposed || myGen != gen) return; // 그 사이에 탭을 바꾸었거나 새로 찾았다
            if (error != null)
            {
                AppLog.Write($"사전 조회 실패({DictKind.Name(kind)}, {word})", error);
                if (DictionaryCache.TryGet(kind, word, out var offline)) { Render(offline!); SetStatus("인터넷에 연결되지 않아 저장해 둔 결과를 보여요"); }
                else ShowMessage("인터넷에 연결되어 있지 않아요.", "한 번 찾아 본 단어는 인터넷이 없어도 볼 수 있어요. 이 단어는 아직 저장돼 있지 않아서, 연결된 뒤에 다시 찾아 주세요.");
                return;
            }
            if (found == null)
            {
                // 사전에 없는 단어. 전에 저장해 둔 것이 있으면 그것을 그대로 둔다.
                if (DictionaryCache.TryGet(kind, word, out var keep)) { Render(keep!); SetStatus("사전에서 새 결과를 찾지 못해 저장해 둔 결과를 보여요"); }
                else ShowMessage($"'{word}'을(를) {DictKind.Name(kind)}에서 찾지 못했어요.", NotFoundHint(kind));
                return;
            }
            DictionaryCache.Put(found);
            Render(found);
            SetStatus("인터넷에서 가져와 저장했어요");
        }
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke((Action)Apply); } catch (InvalidOperationException) { } } else Apply();
    }

    private static string NotFoundHint(int kind) => kind switch
    {
        DictKind.KoEn => "한국어 단어를 기본형으로 찾아 보세요. (예: 먹었다 → 먹다)",
        DictKind.EnKo => "영어 단어를 기본형으로 찾아 보세요. (예: running → run, ate → eat)",
        _ => "활용한 모양이면 기본형으로 찾아 보세요. (예: 먹었다 → 먹다, 아름다운 → 아름답다)",
    };

    private void SetStatus(string text) => status.Text = text;

    // ───────────────────────── 단어장에 넣기 ─────────────────────────

    private void OnBookChanged() { if (!IsDisposed) UpdateAddButton(); }

    /// <summary>뜻이 나와 있을 때만 눌린다. 이미 단어장에 있으면 어느 그룹에 있는지 적어 준다.</summary>
    private void UpdateAddButton()
    {
        bool has = tab < 3 && current.Length > 0 && result.Tag is "result";
        var grp = has ? WordBook.GroupOf(Kind, current) : null;
        addButton.Text = grp != null ? $"✓ 단어장 · {grp.Name}" : "단어장에 넣기";
        addButton.Enabled = has && grp == null;
    }

    private void SaveCurrent()
    {
        if (current.Length == 0 || tab == 3 || WordBook.Contains(Kind, current)) return;
        string word = current;
        int kind = Kind;
        string gist = DictionaryCache.TryGet(kind, word, out var it) ? DictionaryClient.Gist(it!.Text) : "";
        var groups = WordBook.Groups;
        if (groups.Count == 1) { WordBook.AddWord(groups[0].Id, kind, word, gist); return; }
        var m = new ContextMenuStrip();
        MenuGuard.Attach(m);
        foreach (var g in groups) { var id = g.Id; m.Items.Add(g.Name, null, (_, _) => WordBook.AddWord(id, kind, word, gist)); }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("＋ 새 그룹", null, (_, _) => WordBook.AddWord(WordBook.AddGroup().Id, kind, word, gist)); // 이름은 나중에(그룹을 우클릭 > 이름 바꾸기)
        m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
        m.Show(addButton, new Point(addButton.Width, 0), ToolStripDropDownDirection.AboveLeft);
    }

    // ───────────────────────── 그리기 ─────────────────────────

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>결과 칸을 고치는 동안 화면을 멈춰 두었다가 한 번에 그린다(고치는 도중의 반쯤 바뀐 모습이 비치지 않게).</summary>
    private void Batch(Action fill)
    {
        const int WM_SETREDRAW = 0x000B;
        SendMessage(result.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try { fill(); }
        finally
        {
            SendMessage(result.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            result.SelectionStart = 0; result.SelectionLength = 0; result.ScrollToCaret();
            result.Invalidate();
        }
    }

    private void Add(string text, Font f, Color c)
    {
        result.SelectionStart = result.TextLength;
        result.SelectionLength = 0;
        result.SelectionFont = f;
        result.SelectionColor = c;
        result.AppendText(text);
    }

    private void ShowHint() => Batch(() =>
    {
        result.Clear();
        result.Tag = "hint";
        using var f = new Font(NovelStore.UiFontFamily, 10f);
        string first = Kind switch
        {
            DictKind.KoEn => "한국어 단어를 입력하고 Enter를 누르면 영어 뜻이 나와요.",
            DictKind.EnKo => "영어 단어를 입력하고 Enter를 누르면 한국어 뜻이 나와요.",
            _ => "단어를 입력하고 Enter를 누르면 뜻이 나와요.",
        };
        Add(first + "\n\n뜻 아래의 [단어장에 넣기]로 단어를 단어장에 모아 두세요. 한 번 찾은 단어는 이 컴퓨터에 저장돼서, 인터넷이 끊겨도 다시 볼 수 있어요.", f, palette.Dim);
        SetStatus("");
        UpdateAddButton();
    });

    private void ShowMessage(string title, string detail) => Batch(() =>
    {
        result.Clear();
        result.Tag = "message";
        string ff = NovelStore.UiFontFamily;
        using var tf = new Font(ff, 11f, FontStyle.Bold);
        using var df = new Font(ff, 9.5f);
        Add(title + "\n\n", tf, palette.Fore);
        Add(detail, df, palette.Dim);
        SetStatus("");
        UpdateAddButton();
    });

    private void Render(DictionaryEntry item) => Batch(() =>
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
        Add(DictKind.Info(item.Kind).Source + "\n", small, palette.Dim);
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
            bool meta = line.Contains("IPA") || line.StartsWith("어원:") || line.StartsWith("발음") || line.StartsWith("Etymology") || line.StartsWith("Pronunciation");
            Add(line + "\n", meta ? small : body, meta ? palette.Dim : palette.Fore);
            lastBlank = false;
        }
        result.SelectionStart = 0;
        result.ScrollToCaret();
        UpdateAddButton();
    });
}

// ═══════════════════════════ 머리 줄 + 탭 ═══════════════════════════

/// <summary>사전 창 맨 위: [책 아이콘 · 제목 ········ ⌄ ×] 머리 줄과 그 밑 탭 줄([국어사전 · 한영 사전 · 영한 사전 ···· 단어장]).</summary>
internal sealed class DictChrome : Control
{
    private static readonly string[] Names = { "국어사전", "한영 사전", "영한 사전", "단어장" };
    private LibraryPalette? p;
    private int hot = -1; // 0~3 탭 · 10 접기 · 11 닫기
    private int tab;
    private bool collapsed;
    private string title = "사전";
    private readonly Rectangle[] tabRects = new Rectangle[4];
    private Rectangle toggleRect, closeRect;

    public event Action<int>? TabChanged;
    public event Action? ToggleRequested;
    public event Action? CloseRequested;

    public DictChrome()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int HeaderH => S(38);
    private int TabsH => S(36);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }
    private void Fit() => Height = collapsed ? HeaderH : HeaderH + TabsH;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Tab { get => tab; set { if (tab == value) return; tab = value; Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Collapsed { get => collapsed; set { collapsed = value; Fit(); Invalidate(); } }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Title { get => title; set { title = value; Invalidate(); } }

    public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Main; Invalidate(); }

    private void LayoutParts(Graphics g, Font tabFont)
    {
        toggleRect = new Rectangle(Width - S(76), 0, S(34), HeaderH);
        closeRect = new Rectangle(Width - S(42), 0, S(34), HeaderH);
        int x = S(24), y = HeaderH;
        for (int i = 0; i < 3; i++)
        {
            int w = TextRenderer.MeasureText(g, Names[i], tabFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            tabRects[i] = new Rectangle(x - S(4), y, w + S(8), TabsH);
            x += w + S(20);
        }
        int bw = TextRenderer.MeasureText(g, Names[3], tabFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        tabRects[3] = new Rectangle(Width - S(20) - bw - S(4), y, bw + S(8), TabsH);
    }

    private int HitAt(Point pt)
    {
        if (toggleRect.Contains(pt)) return 10;
        if (closeRect.Contains(pt)) return 11;
        if (!collapsed) for (int i = 0; i < 4; i++) if (tabRects[i].Contains(pt)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitAt(e.Location);
        if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int h = HitAt(e.Location);
        if (h == 10) { ToggleRequested?.Invoke(); return; }
        if (h == 11) { CloseRequested?.Invoke(); return; }
        if (h >= 0) { TabChanged?.Invoke(h); return; }
        if (e.Y < HeaderH && FindForm() is Form f) { ReleaseCapture(); SendMessage(f.Handle, 0x00A1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero); } // 머리 줄을 끌어 창을 옮긴다
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && e.Y < HeaderH && HitAt(e.Location) < 0) ToggleRequested?.Invoke(); // 머리 줄을 두 번 누르면 접고 편다
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var pal = p ?? LibraryPalette.From(Themes.All[0]);
        g.Clear(pal.Main);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        string ff = NovelStore.UiFontFamily;
        Color dim = Themes.Blend(pal.Main, pal.Fore, 0.55f);
        using var tabFont = new Font(ff, 10.5f);
        using var tabBold = new Font(ff, 10.5f, FontStyle.Bold);
        LayoutParts(g, tabFont);

        // 머리 줄: 책 아이콘 · 제목 · 접기(⌄ / 접혀 있으면 ⌃) · 닫기(×)
        using (var icon = new Font(MainForm.IconFontName(), 12f))
            TextRenderer.DrawText(g, "", icon, new Rectangle(S(14), 0, S(26), HeaderH), pal.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using (var tf = new Font(ff, 11f))
            TextRenderer.DrawText(g, title, tf, new Rectangle(S(46), 0, Math.Max(10, Width - S(130)), HeaderH), dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        DrawChevron(g, toggleRect, !collapsed, hot == 10 ? pal.Fore : dim);
        DrawClose(g, closeRect, hot == 11 ? pal.Fore : dim);
        using (var lp = new Pen(pal.Line)) g.DrawLine(lp, 0, HeaderH - 1, Width, HeaderH - 1);
        if (collapsed) return;

        // 탭 줄: 선택한 탭은 굵게 · 짙게, 나머지는 흐리게(마우스를 올리면 짙게)
        for (int i = 0; i < 4; i++)
        {
            bool sel = i == tab;
            TextRenderer.DrawText(g, Names[i], sel ? tabBold : tabFont, tabRects[i], sel || hot == i ? pal.Fore : dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    private void DrawChevron(Graphics g, Rectangle r, bool down, Color c)
    {
        using var pen = new Pen(c, S(1) + 0.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, w = S(7), h = S(4);
        if (down) g.DrawLines(pen, new[] { new PointF(cx - w, cy - h), new PointF(cx, cy + h), new PointF(cx + w, cy - h) });
        else g.DrawLines(pen, new[] { new PointF(cx - w, cy + h), new PointF(cx, cy - h), new PointF(cx + w, cy + h) });
    }

    private void DrawClose(Graphics g, Rectangle r, Color c)
    {
        using var pen = new Pen(c, S(1) + 0.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, a = S(6);
        g.DrawLine(pen, cx - a, cy - a, cx + a, cy + a);
        g.DrawLine(pen, cx - a, cy + a, cx + a, cy - a);
    }
}

// ═══════════════════════════ 작은 부품들 ═══════════════════════════

/// <summary>둥근 테두리 입력칸 + 오른쪽 보라색 돋보기 단추. Enter나 단추로 Submit, Shift+Enter는 ShiftSubmit, 글자가 바뀌면 TextEdited.</summary>
internal sealed class DictSearchField : Control
{
    private readonly TextBox box = new() { BorderStyle = BorderStyle.None, PlaceholderText = "단어 검색..." };
    private LibraryPalette? p;
    private bool hotButton;
    public event Action? Submit;
    public event Action? ShiftSubmit;
    public event Action? TextEdited;

    private static readonly Color ButtonColor = Color.FromArgb(0xD8, 0xC4, 0xFF);
    private static readonly Color ButtonHover = PurpleIcons.Purple;

    public override string Text { get => box.Text; set => box.Text = value; }

    public DictSearchField()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(box);
        box.TextChanged += (_, _) => TextEdited?.Invoke();
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            if (e.Shift) ShiftSubmit?.Invoke(); else Submit?.Invoke();
        };
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    protected override Size DefaultSize => new(200, 56);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(58); LayoutBox(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }

    private Rectangle Frame => new(S(8), S(7), Math.Max(S(60), Width - S(16)), Math.Max(S(20), Height - S(14)));
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
        BackColor = pal.Main;
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
        g.Clear(pal.Main);
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

/// <summary>글자만 있는 단추(오른쪽 맞춤). 눌릴 때는 짙게, 마우스를 올리면 강조색, 눌 수 없을 때는 흐리게.</summary>
internal sealed class TextButton : Control
{
    private LibraryPalette? p;
    private bool hover;

    public TextButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        TabStop = false;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Main; Font = new Font(NovelStore.UiFontFamily, 10.5f); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnClick(EventArgs e) { if (Enabled) base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var pal = p ?? LibraryPalette.From(Themes.All[0]);
        g.Clear(pal.Main);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        Color c = !Enabled ? Themes.Blend(pal.Main, pal.Fore, 0.5f) : hover ? pal.Accent : pal.Fore;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - S(16), Height), c, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>[+ Group] 단추(둥근 테두리, 필기체 글씨).</summary>
internal sealed class GroupButton : Control
{
    private LibraryPalette? p;
    private bool hover;
    private static readonly string ScriptFace = PickScript();

    private static string PickScript()
    {
        using var fc = new InstalledFontCollection();
        foreach (var n in new[] { "Segoe Script", "Lucida Handwriting", "Brush Script MT" })
            if (fc.Families.Any(f => f.Name == n)) return n;
        return NovelStore.UiFontFamily;
    }

    public GroupButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Size = new Size(S(112), S(32)); }
    public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Main; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var pal = p ?? LibraryPalette.From(Themes.All[0]);
        g.Clear(pal.Main);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var r = new Rectangle(S(1), S(1), Width - S(3), Height - S(3));
        using (var path = LibraryPalette.Rounded(r, S(7)))
        {
            if (hover) { using var hb = new SolidBrush(pal.CardHover); g.FillPath(hb, path); }
            using var pen = new Pen(Themes.Blend(pal.Main, pal.Fore, 0.45f), S(1) + 0.6f); g.DrawPath(pen, path);
        }
        using (var pp = new Pen(pal.Accent, S(1) + 0.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float pcx = r.X + S(16), pcy = r.Y + r.Height / 2f, arm = S(6);
            g.DrawLine(pp, pcx - arm, pcy, pcx + arm, pcy);
            g.DrawLine(pp, pcx, pcy - arm, pcx, pcy + arm);
        }
        using var gf = new Font(ScriptFace, 10.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, "Group", gf, new Rectangle(r.X + S(30), r.Y, r.Width - S(34), r.Height), pal.Accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }
}
