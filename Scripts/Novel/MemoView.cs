using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cafeum;

/// <summary>휴지통에 버린 메모 하나(버린 때와 함께). 작품 폴더의 .cafeum-memo-trash.json에 담는다.</summary>
public sealed class MemoTrashEntry
{
    public MemoNote Note { get; set; } = new();
    public DateTime Deleted { get; set; } = DateTime.Now;
}

/// <summary>메모 휴지통. 메모 목록(MemoStore)과 따로 두어서, 휴지통에 있는 메모가 사이드바 목록이나 메모 창에 섞여 보이지 않게 한다.</summary>
internal static class MemoTrash
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static string FileOf(string dir) => NovelFiles.PathOf(dir, NovelFiles.MemoTrashFileName);

    public static List<MemoTrashEntry> Load(string dir) => JsonFile.Load<List<MemoTrashEntry>>(FileOf(dir), Opt) ?? new();
    public static void Save(string dir, List<MemoTrashEntry> list) => JsonFile.Save(FileOf(dir), list, Opt, hidden: true);
}

/// <summary>
/// 소설 메인의 [메모] 탭: 왼쪽에 [전체 · 기본그룹(· 만든 그룹들) | 오늘 · 최근 7일 · 최근 30일 · 휴지통], 가운데 표(제목 · 그룹 · 수정일 · 생성일 · 삭제), 오른쪽에 고른 메모의 미리보기.
/// 사이드바의 메모 목록과 같은 메모들이다(같은 파일). 줄을 두 번 누르면 메모 창이 열린다. 검색은 작품 화면 맨 위의 검색칸이 SetQuery로 넘겨 준다.
/// </summary>
internal sealed class MemoView : Control
{
    private LibraryPalette? p;
    private string? dir;
    private string filter = "all";
    private string query = "";
    private string? selectedId;
    private int offset, hoverRow = -1, hoverSide = -1;
    private bool hoverTrash;
    private readonly HashSet<string> selIds = new(); // 표에서 고른 메모들(모두 선택 · Ctrl로 여러 개)
    private List<MemoTrashEntry> trash = new();
    private readonly RichTextBox preview;
    private ContextMenuStrip? menu;

    public MemoView()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        preview = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, Visible = false, DetectUrls = false, TabStop = false, Font = new Font(NovelStore.UiFontFamily, 10.5f) };
        Controls.Add(preview);
        MemoStore.Changed += OnStoreChanged;
        Disposed += (_, _) => { MemoStore.Changed -= OnStoreChanged; menu?.Dispose(); };
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Main; Invalidate(); }

    // ───────── 자료 ─────────

    private List<MemoNote> Notes => dir == null ? new() : MemoStore.Get(dir);

    /// <summary>작품이 바뀌면 그 작품의 메모를 보여 준다. 예전 [메모] 탭의 메모(작품 파일의 Memo)가 남아 있으면 첫 메모로 옮겨 온다.</summary>
    public void SetNovel(string novelDir)
    {
        if (string.Equals(dir, novelDir, StringComparison.OrdinalIgnoreCase)) return;
        dir = novelDir;
        MigrateOldMemo(novelDir);
        trash = MemoTrash.Load(novelDir);
        selectedId = null; offset = 0; filter = "all";
        ShowSelected();
        Invalidate();
    }

    private static void MigrateOldMemo(string novelDir)
    {
        try
        {
            string? old = NovelStore.LoadMeta(novelDir).Memo;
            if (string.IsNullOrWhiteSpace(old)) return;
            using var rtb = new RichTextBox { Text = old };
            var n = MemoNote.Create(0);
            n.Text = old; n.Rtf = rtb.Rtf;
            MemoStore.Get(novelDir).Insert(0, n);
            if (!MemoStore.Save(novelDir)) return; // 새 메모 파일에 못 썼으면 옛 메모를 지우지 않는다(다음에 다시 시도)
            NovelStore.SaveMemo(novelDir, null); // 내용은 새 메모로 옮겼으니 옛 자리는 비운다
        }
        catch (Exception ex) { AppLog.Write("예전 메모를 옮기지 못했어요", ex); }
    }

    private void OnStoreChanged(string novelDir)
    {
        if (dir == null || !string.Equals(novelDir, dir, StringComparison.OrdinalIgnoreCase) || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnStoreChanged(novelDir)); return; }
        if (Selected == null && selectedId != null) selectedId = null;
        ShowSelected();
        ClampOffset();
        Invalidate();
    }

    public void SetQuery(string q) { if (q == query) return; query = q; offset = 0; Invalidate(); }

    private MemoNote? Selected => selectedId == null ? null : Notes.FirstOrDefault(n => n.Id == selectedId) ?? trash.FirstOrDefault(t => t.Note.Id == selectedId)?.Note;

    private static string TitleOf(MemoNote n)
    {
        if (!string.IsNullOrWhiteSpace(n.Title)) return n.Title!.Trim();
        foreach (var line in n.Text.Split('\n')) { var t = line.Trim(); if (t.Length > 0) return t; }
        return "(제목 없음)";
    }

    private static string GroupOf(MemoNote n) => string.IsNullOrEmpty(n.Group) ? "기본그룹" : n.Group!;

    /// <summary>왼쪽 목록의 항목들: (키, 이름, 아이콘, 구분선 위에 둘지).</summary>
    private List<(string Key, string Label, string Glyph, bool Gap)> Side()
    {
        var l = new List<(string, string, string, bool)> { ("all", "전체", "\uE8FD", false), ("grp", "기본그룹", "\uE8B7", false) };
        if (dir != null) foreach (var g in MemoStore.Groups(dir)) l.Add(("g:" + g, g, "\uE8B7", false));
        l.Add(("add", "그룹 추가하기", "\uE710", false));
        l.Add(("today", "오늘", "\uE787", true));
        l.Add(("w7", "최근 7일", "\uE787", false));
        l.Add(("m30", "최근 30일", "\uE787", false));
        l.Add(("trash", "휴지통", "\uE74D", false));
        return l;
    }

    private bool InFilter(MemoNote m, string f) => f switch
    {
        "grp" => string.IsNullOrEmpty(m.Group) || !MemoStore.Groups(dir ?? "").Contains(m.Group),
        "today" => m.Modified.Date == DateTime.Today,
        "w7" => m.Modified >= DateTime.Today.AddDays(-6),
        "m30" => m.Modified >= DateTime.Today.AddDays(-29),
        _ when f.StartsWith("g:", StringComparison.Ordinal) => m.Group == f[2..],
        _ => true,
    };

    private int CountOf(string f) => f == "trash" ? trash.Count : Notes.Count(m => InFilter(m, f));

    private bool Matches(MemoNote m) =>
        query.Length == 0 || TitleOf(m).Contains(query, StringComparison.CurrentCultureIgnoreCase) || m.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private List<MemoNote> Rows() =>
        (filter == "trash" ? trash.Select(t => t.Note) : Notes.Where(m => InFilter(m, filter))).Where(Matches).OrderByDescending(m => m.Modified).ToList();

    // ───────── 일 ─────────

    /// <summary>새 메모: 메모 창이 열려 이름 칸부터 쓴다(사이드바의 [새 메모]와 같다). 지금 보는 그룹에 넣는다.</summary>
    public void NewMemo()
    {
        if (dir == null) return;
        string? group = filter.StartsWith("g:", StringComparison.Ordinal) ? filter[2..] : null;
        StickyNotes.NewNote(dir, null, group);
        if (filter == "trash") { filter = "all"; Invalidate(); }
    }

    private void MoveToTrash(MemoNote m)
    {
        if (dir == null) return;
        trash.Insert(0, new MemoTrashEntry { Note = m, Deleted = DateTime.Now });
        MemoTrash.Save(dir, trash);          // 휴지통에 먼저 적어 둔 뒤에
        StickyNotes.Delete((IWin32Window?)FindForm() ?? this, dir, m, ask: false); // 목록에서 빼고(열린 메모 창도 닫는다)
        if (selectedId == m.Id) selectedId = null;
        ShowSelected();
        Invalidate();
    }

    private void Restore(MemoNote m)
    {
        if (dir == null) return;
        trash.RemoveAll(t => t.Note.Id == m.Id);
        m.Open = false;
        MemoStore.Get(dir).Insert(0, m);
        MemoStore.Save(dir);
        MemoTrash.Save(dir, trash);
        if (selectedId == m.Id) selectedId = null;
        ShowSelected();
        Invalidate();
    }

    private void DeleteForever(MemoNote m)
    {
        if (dir == null) return;
        if (MessageBox.Show(FindForm(), "이 메모를 완전히 지울까요? 되돌릴 수 없어요.", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        trash.RemoveAll(t => t.Note.Id == m.Id);
        MemoTrash.Save(dir, trash);
        if (selectedId == m.Id) selectedId = null;
        ShowSelected();
        Invalidate();
    }

    private void OpenWindow(MemoNote m)
    {
        if (dir == null || filter == "trash") return;
        StickyNotes.Open(dir, m, null);
    }

    private void Select(MemoNote? m, bool add = false)
    {
        if (add && m != null)
        {
            if (!selIds.Remove(m.Id)) selIds.Add(m.Id);
            selectedId = selIds.Contains(m.Id) ? m.Id : selIds.FirstOrDefault();
            ShowSelected();
            Invalidate();
            return;
        }
        selIds.Clear();
        if (m != null) selIds.Add(m.Id);
        if (m?.Id == selectedId) { Invalidate(); return; }
        selectedId = m?.Id;
        ShowSelected();
        Invalidate();
    }

    private void ShowSelected()
    {
        var m = Selected;
        if (m != null)
        {
            var (body, _, _) = StickyColors.Of(m.Color);
            preview.BackColor = body;
            preview.ForeColor = StickyColors.Text(m.Color);
            try { if (!string.IsNullOrEmpty(m.Rtf)) preview.Rtf = m.Rtf; else preview.Text = m.Text; }
            catch { preview.Text = m.Text; }
        }
        preview.Visible = m != null;
        LayoutParts();
    }

    // ───────── 자리 ─────────

    private int SideW => S(184);
    private int Gap => S(16);
    private int PreviewW => Width < S(840) ? S(180) : Math.Clamp((int)(Width * 0.27), S(220), S(300));
    private Rectangle TableRect => new(SideW + Gap, 0, Math.Max(S(120), Width - SideW - PreviewW - Gap * 2), BodyH);
    private Rectangle PreviewRect => new(Width - PreviewW, 0, PreviewW, BodyH);
    private int HeaderH => S(40);
    private int RowH => S(44);
    private int TitleH => S(44);
    private int BodyH => Math.Max(10, Height);

    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutParts(); }

    private void LayoutParts()
    {
        var r = PreviewRect;
        preview.Bounds = new Rectangle(r.X + S(16), r.Y + S(16) + TitleH, Math.Max(10, r.Width - S(16) - S(3)), Math.Max(10, r.Height - S(32) - TitleH));
        preview.RightMargin = Math.Max(1, preview.Width - SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi)); // 스크롤바가 없어도 그 폭만큼 오른쪽을 비워 둔다(있을 때와 글이 접히는 자리가 같게)
        ClampOffset();
    }

    private void ClampOffset()
    {
        int max = Math.Max(0, Rows().Count * RowH - (BodyH - HeaderH));
        offset = Math.Clamp(offset, 0, max);
    }

    /// <summary>왼쪽 목록이 다 보이는 데 필요한 높이(그룹이 많으면 늘어난다) — 모자라면 휴지통이 잘려 안 보인다.</summary>
    public int PreferredHeight => Math.Max(S(420), SideItem(Side().Count - 1).Bottom + S(16));

    private Rectangle SideItem(int i)
    {
        var side = Side();
        int gaps = 0;
        for (int k = 0; k <= i && k < side.Count; k++) if (side[k].Gap) gaps++;
        return new Rectangle(S(6), S(8) + i * S(38) + gaps * S(14), SideW - S(12), S(34));
    }

    private Rectangle RowRect(int i) => new(TableRect.X, HeaderH + i * RowH - offset, TableRect.Width, RowH);
    private Rectangle TrashRect(Rectangle row) => new(row.Right - S(36), row.Y + (row.Height - S(26)) / 2, S(26), S(26));

    private int SideAt(Point pt) { int n = Side().Count; for (int i = 0; i < n; i++) if (SideItem(i).Contains(pt)) return i; return -1; }

    private int RowAt(Point pt)
    {
        if (!TableRect.Contains(pt) || pt.Y < HeaderH) return -1;
        int i = (pt.Y - HeaderH + offset) / RowH;
        return i >= 0 && i < Rows().Count ? i : -1;
    }

    /// <summary>지금 보이는 목록의 메모를 모두 고른다.</summary>
    public void SelectAll() { foreach (var m in Rows()) selIds.Add(m.Id); Invalidate(); }

    /// <summary>고른 메모들을 휴지통으로(휴지통 안이면 완전히 지운다). 고른 게 없으면 미리보기에 떠 있는 메모.</summary>
    public void DeleteSelected()
    {
        var targets = Rows().Where(m => selIds.Contains(m.Id)).ToList();
        if (targets.Count == 0 && Selected is { } one && Rows().Contains(one)) targets.Add(one);
        if (targets.Count == 0 || dir == null) return;
        if (filter == "trash")
        {
            if (MessageBox.Show(FindForm(), $"메모 {targets.Count}개를 완전히 지울까요? 되돌릴 수 없어요.", AppShared.AppDisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            foreach (var m in targets) trash.RemoveAll(t => t.Note.Id == m.Id);
            MemoTrash.Save(dir, trash);
        }
        else
        {
            foreach (var m in targets) trash.Insert(0, new MemoTrashEntry { Note = m, Deleted = DateTime.Now });
            MemoTrash.Save(dir, trash);
            foreach (var m in targets) StickyNotes.Delete((IWin32Window?)FindForm() ?? this, dir, m, ask: false);
        }
        selIds.Clear(); selectedId = null;
        ShowSelected();
        Invalidate();
    }

    /// <summary>[그룹 추가하기]: 이름을 받아 새 메모 그룹을 만든다(사이드바 목록에도 나타난다).</summary>
    private void AddGroup()
    {
        if (dir == null) return;
        using var dlg = new NovelInputDialog("그룹 추가하기", "그룹 이름", "");
        if (dlg.ShowDialogOver(FindForm()) != DialogResult.OK || dlg.FirstValue.Trim().Length == 0) return;
        string made = MemoStore.CreateGroup(dir);
        string final = MemoStore.RenameGroup(dir, made, dlg.FirstValue);
        filter = "g:" + final; offset = 0; selIds.Clear(); selectedId = null;
        ShowSelected();
        Invalidate();
    }

    // ───────── 입력 ─────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int r = RowAt(e.Location), s = SideAt(e.Location);
        bool t = r >= 0 && TrashRect(RowRect(r)).Contains(e.Location);
        if (r != hoverRow || s != hoverSide || t != hoverTrash) { hoverRow = r; hoverSide = s; hoverTrash = t; Invalidate(); }
        Cursor = r >= 0 || s >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverRow = hoverSide = -1; hoverTrash = false; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!TableRect.Contains(e.Location)) return;
        offset -= Math.Sign(e.Delta) * RowH;
        ClampOffset();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int s = SideAt(e.Location);
        if (s >= 0 && e.Button == MouseButtons.Right && Side()[s].Key.StartsWith("g:", StringComparison.Ordinal)) { ShowGroupMenu(Side()[s].Key[2..]); return; }
        if (s >= 0 && e.Button == MouseButtons.Left)
        {
            if (Side()[s].Key == "add") { AddGroup(); return; }
            filter = Side()[s].Key; offset = 0; selIds.Clear();
            if (Selected is { } cur && !Rows().Contains(cur)) { selectedId = null; ShowSelected(); }
            Invalidate();
            return;
        }
        int r = RowAt(e.Location);
        if (r < 0) return;
        var m = Rows()[r];
        if (e.Button == MouseButtons.Right) { Select(m); ShowRowMenu(m); return; }
        if (e.Button != MouseButtons.Left) return;
        if (TrashRect(RowRect(r)).Contains(e.Location)) { if (filter == "trash") DeleteForever(m); else MoveToTrash(m); }
        else Select(m, add: (ModifierKeys & Keys.Control) != 0);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        int r = RowAt(e.Location);
        if (r < 0 || TrashRect(RowRect(r)).Contains(e.Location)) return;
        var m = Rows()[r];
        BeginInvoke(() => OpenWindow(m));
    }

    /// <summary>만든 그룹을 우클릭: 이름 바꾸기 · 그룹 지우기(기본그룹은 바꾸거나 지울 수 없어서 메뉴가 없다).</summary>
    private void ShowGroupMenu(string group)
    {
        if (dir == null) return;
        string d = dir;
        menu?.Dispose();
        var cm = menu = new ContextMenuStrip();
        MenuGuard.Attach(cm);
        cm.Items.Add("이름 바꾸기…", null, (_, _) => BeginInvoke(() =>
        {
            using var dlg = new NovelInputDialog("그룹 이름 바꾸기", "그룹 이름", group);
            if (dlg.ShowDialogOver(FindForm()) != DialogResult.OK) return;
            string renamed = MemoStore.RenameGroup(d, group, dlg.FirstValue);
            if (filter == "g:" + group) filter = "g:" + renamed; // 보고 있던 그룹이면 새 이름으로 이어서
            Invalidate();
        }));
        cm.Items.Add(new ToolStripSeparator());
        cm.Items.Add("그룹 지우기…", null, (_, _) => BeginInvoke(() =>
        {
            if (MessageBox.Show(FindForm(), $"'{group}' 그룹을 지울까요?\n그룹 안의 메모는 지워지지 않고 기본그룹으로 나와요.", "메모 그룹 지우기", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (filter == "g:" + group) { filter = "all"; selectedId = null; ShowSelected(); }
            MemoStore.DeleteGroup(d, group);
            Invalidate();
        }));
        cm.Show(Cursor.Position);
    }

    private void ShowRowMenu(MemoNote m)
    {
        menu?.Dispose();
        var cm = menu = new ContextMenuStrip();
        MenuGuard.Attach(cm);
        if (filter == "trash")
        {
            cm.Items.Add("복원", null, (_, _) => BeginInvoke(() => Restore(m)));
            cm.Items.Add("완전히 삭제", null, (_, _) => BeginInvoke(() => DeleteForever(m)));
        }
        else
        {
            cm.Items.Add("메모 열기", null, (_, _) => BeginInvoke(() => OpenWindow(m)));
            cm.Items.Add("삭제", null, (_, _) => BeginInvoke(() => MoveToTrash(m)));
        }
        cm.Show(Cursor.Position);
    }

    // ───────── 그리기 ─────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const TextFormatFlags F = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        using var icon = new Font(MainForm.IconFontName(), 10.5f);
        using var small = new Font(Font.FontFamily, 9.5f);
        var line = Themes.Blend(p.Main, p.Fore, 0.10f);

        // 왼쪽 목록
        var side = Side();
        for (int i = 0; i < side.Count; i++)
        {
            var r = SideItem(i);
            bool sel = side[i].Key == filter;
            if (side[i].Gap) using (var pen = new Pen(line)) g.DrawLine(pen, S(14), r.Y - S(8), SideW - S(14), r.Y - S(8));
            if (sel) using (var path = LibraryPalette.Rounded(r, S(8))) using (var b = new SolidBrush(Themes.Blend(p.Main, p.Accent, p.Dark ? 0.22f : 0.14f))) g.FillPath(b, path);
            else if (i == hoverSide) using (var path = LibraryPalette.Rounded(r, S(8))) using (var b = new SolidBrush(p.CardHover)) g.FillPath(b, path);
            if (side[i].Key == "add")
            {
                TextRenderer.DrawText(g, side[i].Glyph, icon, new Rectangle(r.X + S(10), r.Y, S(22), r.Height), i == hoverSide ? p.Fore : p.Dim, F | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, side[i].Label, Font, new Rectangle(r.X + S(40), r.Y, r.Width - S(46), r.Height), i == hoverSide ? p.Fore : p.Dim, F);
                continue;
            }
            TextRenderer.DrawText(g, side[i].Glyph, icon, new Rectangle(r.X + S(10), r.Y, S(22), r.Height), sel ? p.Accent : p.Dim, F | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, $"{side[i].Label} ({CountOf(side[i].Key)})", Font, new Rectangle(r.X + S(40), r.Y, r.Width - S(46), r.Height), sel ? p.Accent : p.Fore, F);
        }

        // 가운데 표
        var tr = TableRect;
        int cDelete = S(60), cDate = S(108), cGroup = S(104);
        int xDelete = tr.Right - cDelete, xCreated = xDelete - cDate, xModified = xCreated - cDate, xGroup = xModified - cGroup;
        using (var path = LibraryPalette.Rounded(new Rectangle(tr.X, 0, tr.Width, HeaderH), S(8)))
        using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, p.Dark ? 0.10f : 0.05f))) g.FillPath(b, path);
        void Head(string t, int x, int w, bool left = false) => TextRenderer.DrawText(g, t, small, new Rectangle(x, 0, w, HeaderH), p.Dim, F | (left ? 0 : TextFormatFlags.HorizontalCenter));
        Head("제목", tr.X + S(16), Math.Max(10, xGroup - tr.X - S(24)), left: true);
        Head("그룹", xGroup, cGroup); Head(filter == "trash" ? "삭제일" : "수정일", xModified, cDate); Head("생성일", xCreated, cDate); Head(filter == "trash" ? "완전삭제" : "삭제", xDelete - S(12), cDelete + S(12));

        var rows = Rows();
        var clip = g.Clip;
        g.SetClip(new Rectangle(tr.X, HeaderH, tr.Width, Math.Max(0, BodyH - HeaderH)));
        for (int i = 0; i < rows.Count; i++)
        {
            var r = RowRect(i);
            if (r.Bottom < HeaderH || r.Y > BodyH) continue;
            var m = rows[i];
            bool sel = m.Id == selectedId || selIds.Contains(m.Id);
            if (sel) using (var b = new SolidBrush(Themes.Blend(p.Main, p.Accent, p.Dark ? 0.16f : 0.09f))) g.FillRectangle(b, r);
            else if (i == hoverRow) using (var b = new SolidBrush(p.CardHover)) g.FillRectangle(b, r);
            using (var pen = new Pen(line)) g.DrawLine(pen, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);
            TextRenderer.DrawText(g, TitleOf(m), Font, new Rectangle(r.X + S(16), r.Y, Math.Max(10, xGroup - r.X - S(24)), r.Height), filter == "trash" ? p.Dim : p.Fore, F);
            TextRenderer.DrawText(g, GroupOf(m), small, new Rectangle(xGroup, r.Y, cGroup, r.Height), p.Dim, F | TextFormatFlags.HorizontalCenter);
            DateTime shown = filter == "trash" ? trash.FirstOrDefault(t => t.Note.Id == m.Id)?.Deleted ?? m.Modified : m.Modified;
            TextRenderer.DrawText(g, shown.ToString("yyyy-MM-dd"), small, new Rectangle(xModified, r.Y, cDate, r.Height), p.Dim, F | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, m.Created.ToString("yyyy-MM-dd"), small, new Rectangle(xCreated, r.Y, cDate, r.Height), p.Dim, F | TextFormatFlags.HorizontalCenter);
            var tb = TrashRect(r);
            if (i == hoverRow && hoverTrash) using (var path = LibraryPalette.Rounded(tb, S(6))) using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.12f))) g.FillPath(b, path);
            TextRenderer.DrawText(g, "\uE74D", icon, tb, hoverRow == i && hoverTrash ? p.Fore : p.Dim, F | TextFormatFlags.HorizontalCenter);
        }
        g.Clip = clip;
        if (rows.Count == 0)
        {
            string msg = query.Length > 0 ? $"'{query}'에 맞는 메모가 없어요." : filter == "trash" ? "휴지통이 비었어요." : "메모가 없어요. [새 메모]로 만들어 보세요.";
            TextRenderer.DrawText(g, msg, Font, new Rectangle(tr.X, HeaderH + S(30), tr.Width, S(30)), p.Dim, F | TextFormatFlags.HorizontalCenter);
        }

        // 오른쪽 미리보기(메모지: 고른 메모의 색)
        var pr = PreviewRect;
        var sel0 = Selected;
        Color paper = sel0 != null ? StickyColors.Of(sel0.Color).Body : Color.FromArgb(0xFF, 0xF7, 0xD1);
        using (var path = LibraryPalette.Rounded(new Rectangle(pr.X, pr.Y, pr.Width - 1, pr.Height - 1), S(12)))
        using (var b = new SolidBrush(paper)) g.FillPath(b, path);
        if (sel0 == null)
            TextRenderer.DrawText(g, "메모를 선택해주세요", small, pr, Themes.Blend(paper, Color.Black, 0.45f), F | TextFormatFlags.HorizontalCenter);
        else
        {
            using var bold = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            TextRenderer.DrawText(g, TitleOf(sel0), bold, new Rectangle(pr.X + S(16), pr.Y + S(14), pr.Width - S(32), TitleH - S(8)), StickyColors.Text(sel0.Color), F);
        }
    }
}

/// <summary>메모 탭의 도구 줄: [새 메모](강조색) [모두 선택] [삭제](옅은 테두리). 작품 화면의 도구 줄 오른쪽 끝에 놓인다.</summary>
internal sealed class MemoActionBar : Control
{
    private LibraryPalette? p;
    private int hover = -1;
    private static readonly string[] Texts = { "새 메모", "모두 선택", "삭제" };
    private static readonly string[] Glyphs = { "\uE70F", "\uE73E", "\uE74D" };
    public event Action? NewClicked, SelectAllClicked, DeleteClicked;

    public MemoActionBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font(NovelStore.UiFontFamily, 10f, FontStyle.Bold);
        Cursor = Cursors.Hand;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    public void SetPalette(LibraryPalette pal) { p = pal; Invalidate(); }

    private int WidthOf(int i) => i == 0 ? S(112) : i == 1 ? S(122) : S(88);
    /// <summary>세 버튼이 차지하는 폭.</summary>
    public int FitWidth() => WidthOf(0) + WidthOf(1) + WidthOf(2) + S(20);
    private Rectangle Rect(int i) { int x = 0; for (int k = 0; k < i; k++) x += WidthOf(k) + S(10); return new Rectangle(x, 0, WidthOf(i), Height); }
    private int At(Point pt) { for (int i = 0; i < 3; i++) if (Rect(i).Contains(pt)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = At(e.Location); if (i != hover) { hover = i; Invalidate(); } Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        switch (At(e.Location)) { case 0: NewClicked?.Invoke(); break; case 1: SelectAllClicked?.Invoke(); break; case 2: DeleteClicked?.Invoke(); break; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        const TextFormatFlags F = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        using var icon = new Font(MainForm.IconFontName(), 10.5f);
        for (int i = 0; i < 3; i++)
        {
            var br = Rect(i);
            bool hot = hover == i;
            using var path = LibraryPalette.Rounded(new Rectangle(br.X, br.Y, br.Width - 1, br.Height - 1), S(8));
            if (i == 0)
            {
                using var fill = new SolidBrush(hot ? Themes.Blend(p.Accent, Color.White, 0.12f) : p.Accent);
                g.FillPath(fill, path);
                TextRenderer.DrawText(g, Glyphs[0], icon, new Rectangle(br.X + S(12), br.Y, S(24), br.Height), Color.White, F | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, Texts[0], Font, new Rectangle(br.X + S(40), br.Y, br.Width - S(46), br.Height), Color.White, F);
            }
            else
            {
                using var fill = new SolidBrush(hot ? p.CardHover : Themes.Blend(p.Main, p.Fore, p.Dark ? 0.07f : 0.035f));
                g.FillPath(fill, path);
                using var pen = new Pen(p.Line); g.DrawPath(pen, path);
                TextRenderer.DrawText(g, Glyphs[i], icon, new Rectangle(br.X + S(10), br.Y, S(24), br.Height), p.Dim, F | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, Texts[i], Font, new Rectangle(br.X + S(40), br.Y, br.Width - S(46), br.Height), p.Fore, F);
            }
        }
    }
}
