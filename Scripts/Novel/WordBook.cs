using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.Json;

namespace Cafeum;

/// <summary>단어장 안의 단어 한 개: 단어 · 뜻 한 줄 요약 · 어느 사전에서 찾았는지.</summary>
internal sealed class WordItem
{
    public string Word { get; set; } = "";
    public string Gist { get; set; } = "";
    /// <summary>어느 사전에서 찾은 단어인가(DictKind). 단어장에서 누르면 그 사전으로 열린다. 예전에 넣은 것은 0(국어사전).</summary>
    public int Kind { get; set; }
    public long Added { get; set; }
}

/// <summary>단어를 모아 두는 그룹.</summary>
internal sealed class WordGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool Collapsed { get; set; }
    /// <summary>처음부터 있는 "기본" 그룹: 지울 수 없다(이름은 바꿀 수 있다).</summary>
    public bool IsDefault { get; set; }
    public List<WordItem> Words { get; set; } = new();
}

/// <summary>
/// 내 단어장(앱 데이터 폴더의 wordbook.json). 그룹마다 단어를 모은다. 그룹이 하나도 없으면 "기본" 그룹을 만든다.
/// 읽고 쓰기는 JsonFile이 맡는다: 임시 파일에 쓴 뒤 바꾸고, 깨진 파일은 .corrupt로 옮겨 두며(백업이 있으면 되살린다),
/// 다른 프로그램이 쓰는 중이라 읽지 못한 파일은 다시 읽을 때까지 덮어쓰지 않는다.
/// </summary>
internal static class WordBook
{
    private static List<WordGroup>? groups;
    private static bool loadFailed;     // 파일을 읽지 못한 채 빈 단어장으로 둔 상태(나중에 다시 읽어 본다)
    private static long retryTick;
    private static string FilePath => Path.Combine(AppSettings.DataDir, "wordbook.json");
    public static event Action? Changed;

    public static List<WordGroup> Groups
    {
        get
        {
            if (groups != null)
            {
                // 읽지 못한 채 시작했다면 5초마다 다시 읽어 본다(다른 프로그램이 놓은 뒤에는 원래 단어장이 나타난다).
                if (!loadFailed || Environment.TickCount64 - retryTick < 5000) return groups;
            }
            retryTick = Environment.TickCount64;
            groups = JsonFile.Load<List<WordGroup>>(FilePath) ?? new();
            loadFailed = JsonFile.IsUnreadable(FilePath);
            // "기본" 그룹은 늘 하나 있다: 표시된 것 → 이름이 "기본"인 것 → 없으면 맨 앞에 새로 만든다(예전에 저장한 단어장에도 표시를 달아 준다)
            var def = groups.FirstOrDefault(g => g.IsDefault) ?? groups.FirstOrDefault(g => g.Name == "기본");
            if (def == null) { def = new WordGroup { Name = "기본" }; groups.Insert(0, def); }
            def.IsDefault = true;
            return groups;
        }
    }

    /// <summary>그 사전에서 찾은 그 단어가 들어 있는 그룹(없으면 null).</summary>
    public static WordGroup? GroupOf(int kind, string word)
    {
        string w = DictionaryCache.Normalize(word);
        return Groups.FirstOrDefault(g => g.Words.Any(x => x.Kind == kind && DictionaryCache.Normalize(x.Word) == w));
    }

    public static bool Contains(int kind, string word) => GroupOf(kind, word) != null;

    /// <summary>아직 안 쓴 "그룹 N" 이름(그룹 수 + 1부터 센다). 새 그룹은 이름을 묻지 않고 이 이름으로 만들고, 이름은 나중에 바꾼다.</summary>
    public static string NextGroupName()
    {
        for (int n = Groups.Count + 1; ; n++)
        {
            string name = "그룹 " + n;
            if (!Groups.Any(g => g.Name == name)) return name;
        }
    }

    /// <summary>새 그룹을 맨 끝에 만든다(이름을 안 주면 "그룹 N").</summary>
    public static WordGroup AddGroup(string? name = null)
    {
        var g = new WordGroup { Name = string.IsNullOrWhiteSpace(name) ? NextGroupName() : name.Trim() };
        Groups.Add(g);
        Save();
        return g;
    }

    public static void RenameGroup(WordGroup g, string name) { name = name.Trim(); if (name.Length == 0) return; g.Name = name; Save(); }

    public static void DeleteGroup(WordGroup g)
    {
        if (g.IsDefault) return; // 기본 그룹은 지울 수 없다
        Groups.Remove(g);
        Save();
    }

    public static void ToggleGroup(WordGroup g) { g.Collapsed = !g.Collapsed; Save(); }

    public static void AddWord(string groupId, int kind, string word, string gist)
    {
        var g = Groups.FirstOrDefault(x => x.Id == groupId);
        word = DictionaryCache.Normalize(word);
        if (g == null || word.Length == 0 || Contains(kind, word)) return;
        g.Collapsed = false;
        g.Words.Insert(0, new WordItem { Word = word, Gist = gist, Kind = kind, Added = DateTime.UtcNow.Ticks });
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
        JsonFile.Save(FilePath, Groups); // 실패하면 JsonFile이 기록을 남기고 사용자에게 알린다
        Changed?.Invoke();
    }
}

/// <summary>
/// 단어장 화면: 그룹 줄(눌러서 접고 펴기)과 그 그룹의 단어들(휠로 스크롤). 단어를 누르면 WordChosen, 우클릭하면 [다른 그룹으로 옮기기 · 삭제],
/// 그룹 줄 우클릭은 [이름 바꾸기 · 삭제 · 새 그룹]. Filter에 글자를 넣으면 그 글자가 든 단어와 그 단어가 있는 그룹만 보인다(어느 그룹에 있는지 바로 보이게).
/// </summary>
internal sealed class WordBookView : Control
{
    private sealed record Row(WordGroup Group, WordItem? Word, Rectangle R);

    private LibraryPalette? p;
    private readonly List<Row> rows = new();
    private readonly List<(Rectangle R, string Text)> hints = new();
    private int scroll, hot = -1, contentH;
    private string filter = "";
    public event Action<WordItem>? WordChosen;

    public WordBookView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        WordBook.Changed += Redraw;
        Disposed += (_, _) => WordBook.Changed -= Redraw;
    }

    private void Redraw() { if (!IsDisposed) Invalidate(); }
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Main; Invalidate(); }

    /// <summary>찾는 글자(비면 전부 보인다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Filter
    {
        get => filter;
        set { value = DictionaryCache.Normalize(value); if (value == filter) return; filter = value; scroll = 0; Invalidate(); }
    }

    private bool Matches(WordItem w) => filter.Length == 0 || w.Word.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || w.Gist.Contains(filter, StringComparison.CurrentCultureIgnoreCase);

    // ── 행 배치 ──
    private void LayoutRows()
    {
        rows.Clear(); hints.Clear();
        int y = S(6) - scroll, w = Math.Max(S(40), Width - S(16));
        foreach (var g in WordBook.Groups)
        {
            var words = g.Words.Where(Matches).ToList();
            if (filter.Length > 0 && words.Count == 0) continue; // 찾는 중에는 그 글자가 든 단어가 있는 그룹만 보인다
            rows.Add(new Row(g, null, new Rectangle(S(8), y, w, S(36))));
            y += S(40);
            if (g.Collapsed && filter.Length == 0) continue; // 찾는 중에는 접어 둔 그룹도 펴서 보인다
            if (words.Count == 0) { hints.Add((new Rectangle(S(42), y, Math.Max(10, w - S(40)), S(26)), "아직 저장한 단어가 없어요")); y += S(30); continue; }
            foreach (var wd in words) { rows.Add(new Row(g, wd, new Rectangle(S(8), y, w, S(32)))); y += S(32); }
            y += S(4);
        }
        contentH = y + scroll - S(6);
    }
    private int MaxScroll() => Math.Max(0, contentH + S(8) - Height);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        LayoutRows();
        int ns = Math.Clamp(scroll - e.Delta / 120 * S(48), 0, MaxScroll());
        if (ns != scroll) { scroll = ns; Invalidate(); }
    }

    private int HitRow(Point pt) { LayoutRows(); return rows.FindIndex(r => r.R.Contains(pt)); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitRow(e.Location);
        if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int h = HitRow(e.Location);
        if (h < 0) { if (e.Button == MouseButtons.Right) EmptyMenu(e.Location); return; }
        var row = rows[h];
        if (e.Button == MouseButtons.Right) { if (row.Word == null) GroupMenu(row.Group, e.Location); else WordMenu(row.Group, row.Word, e.Location); return; }
        if (e.Button != MouseButtons.Left) return;
        if (row.Word == null) { if (filter.Length == 0) WordBook.ToggleGroup(row.Group); }
        else WordChosen?.Invoke(row.Word);
    }

    // ── 메뉴 · 입력 ──
    private bool scrollToEnd;

    /// <summary>[+ Group]: 이름을 묻지 않고 "그룹 N"을 바로 더하고, 목록 맨 끝으로 내려 보여 준다(이름은 우클릭 > 이름 바꾸기로 나중에).</summary>
    public void NewGroup()
    {
        scrollToEnd = true;
        WordBook.AddGroup();
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
        m.Items.Add("새 그룹", null, (_, _) => BeginInvoke(NewGroup));
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
        var del = new ToolStripMenuItem("그룹 삭제") { Enabled = !g.IsDefault }; // 기본 그룹은 지울 수 없다
        del.Click += (_, _) => BeginInvoke(() =>
        {
            string msg = g.Words.Count == 0 ? $"'{g.Name}' 그룹을 지울까요?" : $"'{g.Name}' 그룹과 그 안의 단어 {g.Words.Count}개를 지울까요?\n되돌릴 수 없어요.";
            if (MessageBox.Show(FindForm(), msg, "그룹 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) WordBook.DeleteGroup(g);
        });
        m.Items.Add(del);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("새 그룹", null, (_, _) => BeginInvoke(NewGroup));
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
        g.Clear(pal.Main);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        LayoutRows();
        if (scrollToEnd) { scrollToEnd = false; scroll = MaxScroll(); }
        scroll = Math.Clamp(scroll, 0, MaxScroll());
        LayoutRows();
        string ff = NovelStore.UiFontFamily;
        const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        Color dim = Themes.Blend(pal.Main, pal.Fore, 0.55f);
        Color groupFill = Themes.Blend(pal.Main, pal.Dark ? pal.Card : pal.Side, 0.7f);

        using var gf = new Font(ff, 11f);
        using var wf = new Font(ff, 10.5f);
        using var wb = new Font(ff, 10.5f, FontStyle.Bold);
        using var sf = new Font(ff, 9f);

        if (filter.Length > 0 && rows.Count == 0)
        {
            TextRenderer.DrawText(g, $"'{filter}'은(는) 단어장에 없어요", gf, new Rectangle(S(18), S(10), Math.Max(10, Width - S(36)), S(30)), dim, left);
            return;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var (grp, word, r) = rows[i];
            if (r.Bottom < 0 || r.Top > Height) continue;
            if (word == null)
            {
                using (var path = LibraryPalette.Rounded(r, S(8))) { using var fb = new SolidBrush(i == hot ? pal.CardHover : groupFill); g.FillPath(fb, path); }
                DrawListIcon(g, new Rectangle(r.X + S(16), r.Y + (r.Height - S(16)) / 2, S(18), S(16)), dim);
                var nameSize = TextRenderer.MeasureText(g, grp.Name, gf, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int nameW = Math.Min(nameSize.Width, Math.Max(S(20), r.Width - S(110)));
                TextRenderer.DrawText(g, grp.Name, gf, new Rectangle(r.X + S(44), r.Y, nameW, r.Height), pal.Fore, left);
                DrawChevron(g, r.X + S(44) + nameW + S(14), r.Y + r.Height / 2, !grp.Collapsed || filter.Length > 0, dim);
                TextRenderer.DrawText(g, grp.Words.Count.ToString(), sf, new Rectangle(r.Right - S(40), r.Y, S(30), r.Height), dim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            else
            {
                if (i == hot) { using var path = LibraryPalette.Rounded(r, S(8)); using var hb = new SolidBrush(pal.CardHover); g.FillPath(hb, path); }
                bool hit = filter.Length > 0 && word.Word.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
                var font = hit ? wb : wf;
                var wsz = TextRenderer.MeasureText(g, word.Word, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                int ww = Math.Min(wsz.Width, r.Width / 2);
                TextRenderer.DrawText(g, word.Word, font, new Rectangle(r.X + S(34), r.Y, ww, r.Height), hit ? pal.Accent : pal.Fore, left); // 찾은 단어는 강조색 · 굵게
                if (word.Gist.Length > 0)
                    TextRenderer.DrawText(g, word.Gist, sf, new Rectangle(r.X + S(34) + ww + S(10), r.Y, Math.Max(0, r.Width - S(34) - ww - S(20)), r.Height), dim, left);
            }
        }
        foreach (var (hr, text) in hints) if (hr.Bottom > 0 && hr.Top < Height) TextRenderer.DrawText(g, text, sf, hr, dim, left);
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

/// <summary>그룹 이름 같은 한 줄을 묻는 작은 창. 확인하면 이름(앞뒤 공백 제거, 비면 null), 취소하면 null.</summary>
internal static class NamePrompt
{
    public static string? Ask(IWin32Window? owner, LibraryPalette p, string title, string label, string initial)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None,
            BackColor = p.Side, ForeColor = p.Fore, Font = new Font(NovelStore.UiFontFamily, 10f),
        };
        int S(int v) => (int)Math.Round(v * f.DeviceDpi / 96.0); // 모든 자리와 크기를 화면 배율에 맞춰 곱한다
        f.ClientSize = new Size(S(340), S(148));
        var lb = new Label { Text = label, Left = S(16), Top = S(16), Width = S(308), Height = S(28), AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = p.Dim };
        var box = new TextBox { Left = S(16), Top = S(54), Width = S(308), Text = initial, BackColor = p.Card, ForeColor = p.Fore, BorderStyle = BorderStyle.FixedSingle, MaxLength = 40 };
        var ok = new Button { Text = "확인", Left = S(154), Top = S(104), Width = S(80), Height = S(32), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, BackColor = p.Card, ForeColor = p.Fore };
        var cancel = new Button { Text = "취소", Left = S(244), Top = S(104), Width = S(80), Height = S(32), DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat, BackColor = p.Card, ForeColor = p.Fore };
        f.Controls.AddRange(new Control[] { lb, box, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Shown += (_, _) => { f.Activate(); box.Focus(); box.SelectAll(); };
        if (f.ShowDialog(owner) != DialogResult.OK) return null;
        string s = box.Text.Trim();
        return s.Length == 0 ? null : s;
    }
}
