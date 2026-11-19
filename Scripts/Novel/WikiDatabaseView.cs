using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 데이터베이스 뷰: 제목 · 요약 · 대분류 · 분류 · 중요도 · 태그를 표 형태로 편집한다.
/// 맨 위 대분류 칩으로 거르고, 요약은 칸을 눌러 바로 적고, 대분류 · 중요도는 눌러서 고르고, 분류 · 태그는 칩에서 추가 · 삭제한다.
/// 문서 말고 만들어 둔 분류도 "분류: 이름" 줄로 나온다(요약 · 대분류 · 상위 분류를 고칠 수 있다).
/// </summary>
internal sealed class WikiDatabaseView : WikiViewBase
{
    private sealed record Row(WikiDoc? Doc, WikiStore.ClassInfo? Cls)
    {
        public bool IsClass => Cls != null;
        public string Name => Doc?.Title ?? Cls!.Name;
    }

    private readonly HScrollBar hbar = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly TextBox editor;
    private List<Row> rows = new();
    private string? filter;
    private int scrollX, hoverRow = -1, hoverCol = -1, hoverChip = -1, editRow = -1;

    public override bool Wide => true;

    /// <summary>(문서 경로, 분류 이름, 새 요약) — 문서면 경로, 분류면 이름이 채워진다.</summary>
    public event Action<string?, string?, string>? SummaryEdited;
    public event Action<string?, string?, string>? CategoryEdited;
    /// <summary>분류 칸이 바뀌었다(문서면 소속 분류들, 분류면 상위 분류들 전체).</summary>
    public event Action<string?, string?, List<string>>? ClassesEdited;
    public event Action<string, int>? ImportanceEdited;
    public event Action<string, List<string>>? TagsEdited;
    public event Action? AddRequested;

    private static readonly string[] ColNames = { "문서", "요약", "대분류", "분류", "중요도", "태그" };
    private static readonly string[] ColGlyphs = { "", "", "", "", "", "" };
    // 열 폭: 화면이 넓으면 기본 폭(남는 폭은 열들에 나눠 채움), 좁으면 최소 폭까지 줄여서 가로 스크롤 없이 한눈에 보이게 한다
    private static readonly int[] BaseW = { 210, 250, 140, 250, 140, 180 };
    private static readonly int[] MinW = { 140, 120, 100, 140, 100, 110 };
    private int[] ColWFor(int width)
    {
        int sumBase = BaseW.Sum(), sumMin = MinW.Sum();
        double avail = width / (DeviceDpi / 96.0);
        var w = new int[BaseW.Length];
        for (int i = 0; i < w.Length; i++)
        {
            double v;
            if (avail >= sumBase) v = BaseW[i] + (avail - sumBase) * BaseW[i] / sumBase; // 남는 폭을 열 폭에 비례해 나눈다
            else if (avail >= sumMin) v = MinW[i] + (avail - sumMin) / (sumBase - sumMin) * (BaseW[i] - MinW[i]);
            else v = MinW[i];
            w[i] = S((int)Math.Round(v));
        }
        return w;
    }
    private int[] ColW => ColWFor(Width);
    private int TotalW => ColW.Sum();
    private int ChipsH => S(42);
    private int HeadH => S(34);
    private int RowH => S(38);
    private int AddH => S(36);
    private int RowsTop => ChipsH + HeadH;

    public WikiDatabaseView()
    {
        editor = new TextBox { BorderStyle = BorderStyle.None, Visible = false };
        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndEdit(true); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(false); }
        };
        editor.LostFocus += (_, _) => EndEdit(true);
        Controls.Add(editor);
        Controls.Add(hbar);
        hbar.ValueChanged += (_, _) => { scrollX = hbar.Value; PlaceEditor(); Invalidate(); };
    }

    protected override void OnPalette() { if (P != null) { editor.BackColor = WikiUi.Surface(P); editor.ForeColor = P.Fore; } }

    protected override void OnData()
    {
        EndEdit(false);
        var all = Data.Docs.Select(d => new Row(d, null)).Concat(Data.Classes.OrderBy(c => c.Name, NaturalStringComparer.Instance).Select(c => new Row(null, c)));
        rows = all.Where(r => filter == null || CategoryKeyOf(r) == filter).ToList();
        SyncScroll();
    }

    private string CategoryKeyOf(Row r) => r.Doc?.Category ?? Data.CategoryOf(r.Cls!.Name);

    public override int PreferredHeightFor(int width) => RowsTop + rows.Count * RowH + AddH + (ColWFor(width).Sum() > width ? hbar.Height : 0) + S(10);

    private void SyncScroll()
    {
        int max = Math.Max(0, TotalW - Width);
        hbar.Visible = max > 0 && Width > 0;
        if (hbar.Visible) { hbar.Maximum = TotalW; hbar.LargeChange = Math.Max(1, Width); hbar.SmallChange = S(40); }
        scrollX = Math.Clamp(scrollX, 0, max);
        if (hbar.Visible && hbar.Value != scrollX) hbar.Value = Math.Min(scrollX, hbar.Maximum - hbar.LargeChange + 1);
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); SyncScroll(); }

    // ───────── 자리 계산 ─────────

    private int ColX(int c) { int x = -scrollX; for (int i = 0; i < c; i++) x += ColW[i]; return x; }
    private Rectangle CellRect(int row, int col) => new(ColX(col), RowsTop + row * RowH, ColW[col], RowH);
    private Rectangle AddRowRect => new(0, RowsTop + rows.Count * RowH, Math.Max(Width, 1), AddH);

    private List<Rectangle> FilterChipRects()
    {
        var list = new List<Rectangle>();
        using var f = new Font(Font.FontFamily, Font.SizeInPoints * 0.9f);
        int x = S(6);
        foreach (var c in WikiCategory.All)
        {
            int w = S(32) + TextRenderer.MeasureText(c.Label, f, Size.Empty, TextFormatFlags.NoPadding).Width + S(8);
            list.Add(new Rectangle(x, S(7), w, S(28)));
            x += w + S(8);
        }
        return list;
    }

    private (List<(Rectangle R, string Text)> Chips, Rectangle Add) ChipLayout(Rectangle cell, IReadOnlyList<string> names, string addLabel, bool canAdd)
    {
        var chips = new List<(Rectangle, string)>();
        using var f = new Font(Font.FontFamily, Font.SizeInPoints * 0.88f);
        int x = cell.X + S(8), h = S(24), y = cell.Y + (cell.Height - h) / 2;
        foreach (var n in names)
        {
            int w = S(28) + TextRenderer.MeasureText(n, f, Size.Empty, TextFormatFlags.NoPadding).Width + S(20);
            chips.Add((new Rectangle(x, y, w, h), n));
            x += w + S(4);
        }
        Rectangle add = Rectangle.Empty;
        if (canAdd)
        {
            int w = names.Count == 0 ? TextRenderer.MeasureText(addLabel, f, Size.Empty, TextFormatFlags.NoPadding).Width + S(32) : h;
            add = new Rectangle(x, y, w, h);
        }
        return (chips, add);
    }

    private List<string> ClassNamesOf(Row r) => r.Doc != null ? r.Doc.Tags : r.Cls!.Parents;

    private bool CategoryEditable(Row r) => r.Doc != null || r.Cls!.Parents.Count == 0;

    // ───────── 마우스 ─────────

    private (int Row, int Col) CellAt(Point pt)
    {
        if (pt.Y < RowsTop) return (-1, -1);
        int r = (pt.Y - RowsTop) / RowH;
        if (r < 0 || r >= rows.Count) return (-1, -1);
        for (int c = 0; c < ColW.Length; c++) { var cr = CellRect(r, c); if (pt.X >= cr.X && pt.X < cr.Right) return (r, c); }
        return (-1, -1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (r, c) = CellAt(e.Location);
        if (r != hoverRow || c != hoverCol) { hoverRow = r; hoverCol = c; Invalidate(); }
        bool hand = r >= 0 && c >= 0 && !(rows[r].IsClass && (c == 4 || c == 5));
        if (e.Y < ChipsH && FilterChipRects().Any(x => x.Contains(e.Location))) hand = true;
        if (AddRowRect.Contains(e.Location)) hand = true;
        Cursor = hand ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverRow = hoverCol = -1; Invalidate(); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Shift) != 0 && hbar.Visible)
        {
            hbar.Value = Math.Clamp(hbar.Value - Math.Sign(e.Delta) * S(80), 0, Math.Max(0, hbar.Maximum - hbar.LargeChange + 1));
            return;
        }
        base.OnMouseWheel(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        EndEdit(true);
        var pt = e.Location;
        if (pt.Y < ChipsH)
        {
            var rects = FilterChipRects();
            for (int i = 0; i < rects.Count; i++)
                if (rects[i].Contains(pt) && e.Button == MouseButtons.Left)
                {
                    string key = WikiCategory.All[i].Key;
                    filter = filter == key ? null : key;
                    OnData();
                    Parent?.PerformLayout();
                    Invalidate();
                    return;
                }
            return;
        }
        if (AddRowRect.Contains(pt) && e.Button == MouseButtons.Left) { AddRequested?.Invoke(); return; }
        var (r, c) = CellAt(pt);
        if (r < 0 || c < 0) return;
        var row = rows[r];
        var cell = CellRect(r, c);
        if (e.Button == MouseButtons.Right)
        {
            if (row.Doc != null) RaiseDocMenu(row.Doc.Path, PointToScreen(pt));
            else RaiseFolderMenu(row.Cls!.Name, PointToScreen(pt));
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        switch (c)
        {
            case 0:
                if (row.Doc != null) RaiseDoc(row.Doc.Path);
                break;
            case 1:
                BeginEdit(r);
                break;
            case 2:
                if (CategoryEditable(row)) PickCategory(row, PointToScreen(new Point(cell.X, cell.Bottom)));
                break;
            case 3:
            {
                var names = ClassNamesOf(row);
                var lay = ChipLayout(cell, names, "분류 추가", true);
                foreach (var (rect, text) in lay.Chips)
                {
                    var xr = new Rectangle(rect.Right - S(20), rect.Y, S(20), rect.Height);
                    if (xr.Contains(pt)) { RaiseClasses(row, names.Where(n => !n.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList()); return; }
                }
                if (lay.Add.Contains(pt)) PickClass(row, PointToScreen(lay.Add.Location));
                break;
            }
            case 4:
                if (row.Doc != null) PickImportance(row.Doc, PointToScreen(new Point(cell.X, cell.Bottom)));
                break;
            case 5:
            {
                if (row.Doc == null) break;
                var lay = ChipLayout(cell, row.Doc.Labels, "태그 추가", true);
                foreach (var (rect, text) in lay.Chips)
                {
                    var xr = new Rectangle(rect.Right - S(20), rect.Y, S(20), rect.Height);
                    if (xr.Contains(pt)) { TagsEdited?.Invoke(row.Doc.Path, row.Doc.Labels.Where(n => !n.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList()); return; }
                }
                if (lay.Add.Contains(pt)) AddTag(row.Doc);
                break;
            }
        }
    }

    private void RaiseClasses(Row row, List<string> list) => ClassesEdited?.Invoke(row.Doc?.Path, row.Cls?.Name, list);

    private void PickCategory(Row row, Point screen)
    {
        var m = new ContextMenuStrip();
        MenuGuard.Attach(m);
        foreach (var cat in WikiCategory.All)
        {
            var it = new ToolStripMenuItem(cat.Label) { Checked = CategoryKeyOf(row) == cat.Key };
            string key = cat.Key;
            it.Click += (_, _) => CategoryEdited?.Invoke(row.Doc?.Path, row.Cls?.Name, key);
            m.Items.Add(it);
        }
        m.Show(screen);
    }

    private void PickImportance(WikiDoc d, Point screen)
    {
        var m = new ContextMenuStrip();
        MenuGuard.Attach(m);
        for (int v = 5; v >= 1; v--)
        {
            int val = v;
            var it = new ToolStripMenuItem($"{v}   {WikiViewData.ImportanceLabel(d.Category, v)}") { Checked = d.Importance == v };
            it.Click += (_, _) => ImportanceEdited?.Invoke(d.Path, val);
            m.Items.Add(it);
        }
        m.Show(screen);
    }

    private void PickClass(Row row, Point anchorScreen)
    {
        if (P == null || PickPopup.JustClosed) return;
        var have = ClassNamesOf(row);
        var choices = Data.Parents.Keys.Where(k => !have.Contains(k, StringComparer.OrdinalIgnoreCase) && !(row.IsClass && k.Equals(row.Cls!.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => k, NaturalStringComparer.Instance).ToList();
        var pop = new PickPopup(P, choices, "분류", "선택할 분류가 없어요", "맞는 분류가 없어요");
        void Add(string name) => RaiseClasses(row, have.Concat(new[] { name }).ToList());
        pop.Picked += Add;
        pop.Created += Add;
        pop.ShowBelow(new Rectangle(anchorScreen.X, anchorScreen.Y, S(30), S(30)), FindForm(), S(280));
    }

    private void AddTag(WikiDoc d)
    {
        using var dlg = new NovelInputDialog("태그 추가", "태그 이름", "");
        if (dlg.ShowDialogOver(FindForm()) != DialogResult.OK) return;
        string t = dlg.FirstValue.Trim();
        if (t.Length == 0 || d.Labels.Contains(t, StringComparer.OrdinalIgnoreCase)) return;
        TagsEdited?.Invoke(d.Path, d.Labels.Concat(new[] { t }).ToList());
    }

    // ───────── 요약 칸 바로 고치기 ─────────

    private void BeginEdit(int r)
    {
        EndEdit(true);
        editRow = r;
        editor.Font = Font;
        editor.Text = rows[r].Doc?.Summary ?? rows[r].Cls!.Summary;
        PlaceEditor();
        editor.Visible = true;
        editor.Focus();
        editor.SelectAll();
    }

    private void PlaceEditor()
    {
        if (editRow < 0 || editRow >= rows.Count) return;
        var cell = CellRect(editRow, 1);
        editor.SetBounds(cell.X + S(12), cell.Y + (cell.Height - editor.PreferredHeight) / 2, cell.Width - S(24), editor.PreferredHeight);
    }

    private void EndEdit(bool commit)
    {
        if (editRow < 0) return;
        int r = editRow;
        editRow = -1;
        editor.Visible = false;
        if (!commit || r >= rows.Count) return;
        string text = editor.Text.Replace("\r", "").Replace("\n", " ").Trim();
        var row = rows[r];
        string old = row.Doc?.Summary ?? row.Cls!.Summary;
        if (text != old) SummaryEdited?.Invoke(row.Doc?.Path, row.Cls?.Name, text);
    }

    // ───────── 그리기 ─────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var p = P;
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var icon = new Font(MainForm.IconFontName(), 10f);
        using var small = new Font(Font.FontFamily, Font.SizeInPoints * 0.88f);
        using var bold = new Font(Font, FontStyle.Bold);
        var line = Themes.Blend(p.Main, p.Fore, 0.1f);
        var surface = WikiUi.Surface(p);

        // 대분류 거르기 칩
        var chipRects = FilterChipRects();
        for (int i = 0; i < chipRects.Count; i++)
        {
            var cat = WikiCategory.All[i];
            bool on = filter == cat.Key;
            FillRounded(g, chipRects[i], S(14), on ? p.AccentSoft : surface, on ? p.Accent : line);
            TextRenderer.DrawText(g, cat.Glyph, icon, new Rectangle(chipRects[i].X + S(6), chipRects[i].Y, S(22), chipRects[i].Height), on ? p.Accent : p.Fore, Flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, cat.Label, small, new Rectangle(chipRects[i].X + S(30), chipRects[i].Y, chipRects[i].Width - S(34), chipRects[i].Height), p.Fore, Flags);
        }

        // 표 머리
        var headBg = new Rectangle(0, ChipsH, Width, HeadH);
        using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.035f))) g.FillRectangle(b, headBg);
        using var linePen = new Pen(line);
        g.DrawLine(linePen, 0, ChipsH, Width, ChipsH);
        g.DrawLine(linePen, 0, RowsTop, Width, RowsTop);
        g.SetClip(new Rectangle(0, ChipsH, Width, Height - ChipsH));
        for (int c = 0; c < ColW.Length; c++)
        {
            int x = ColX(c);
            TextRenderer.DrawText(g, ColGlyphs[c], icon, new Rectangle(x + S(10), ChipsH, S(22), HeadH), p.Dim, Flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, ColNames[c], bold, new Rectangle(x + S(36), ChipsH, ColW[c] - S(42), HeadH), p.Fore, Flags);
        }

        // 줄들
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            int y = RowsTop + r * RowH;
            if (y > Height) break;
            if (r == hoverRow) using (var hb = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.025f))) g.FillRectangle(hb, 0, y, Width, RowH);
            g.DrawLine(linePen, 0, y + RowH, Width, y + RowH);
            string catKey = CategoryKeyOf(row);
            var cat = WikiCategory.Find(catKey);
            // 문서 칸: 아이콘 + 제목(분류 줄은 "분류: 이름")
            var c0 = CellRect(r, 0);
            using (var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.1f))) g.FillEllipse(b, c0.X + S(8), c0.Y + (RowH - S(26)) / 2, S(26), S(26));
            TextRenderer.DrawText(g, row.IsClass ? "" : cat.Glyph, icon, new Rectangle(c0.X + S(8), c0.Y, S(26), RowH), p.Fore, Flags | TextFormatFlags.HorizontalCenter);
            if (row.IsClass)
            {
                int pw = TextRenderer.MeasureText("분류:", Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, "분류:", Font, new Rectangle(c0.X + S(42), c0.Y, pw + S(4), RowH), p.Dim, Flags);
                TextRenderer.DrawText(g, row.Name, bold, new Rectangle(c0.X + S(42) + pw + S(6), c0.Y, c0.Width - S(48) - pw, RowH), p.Fore, Flags);
            }
            else TextRenderer.DrawText(g, row.Name, bold, new Rectangle(c0.X + S(42), c0.Y, c0.Width - S(48), RowH), p.Fore, Flags);
            // 요약
            var c1 = CellRect(r, 1);
            string sum = row.Doc?.Summary ?? row.Cls!.Summary;
            if (editRow != r)
                TextRenderer.DrawText(g, sum.Length > 0 ? sum.Replace("\n", " ") : "비어 있음", Font, new Rectangle(c1.X + S(10), c1.Y, c1.Width - S(20), RowH), sum.Length > 0 ? p.Fore : Themes.Blend(p.Main, p.Fore, 0.35f), Flags);
            // 대분류
            var c2 = CellRect(r, 2);
            TextRenderer.DrawText(g, cat.Glyph, icon, new Rectangle(c2.X + S(8), c2.Y, S(22), RowH), CategoryEditable(row) ? p.Fore : p.Dim, Flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, cat.Label, Font, new Rectangle(c2.X + S(34), c2.Y, c2.Width - S(40), RowH), CategoryEditable(row) ? p.Fore : p.Dim, Flags);
            // 분류 칩
            DrawChips(g, p, CellRect(r, 3), ClassNamesOf(row), "분류 추가", "", icon, small, true);
            // 중요도
            var c4 = CellRect(r, 4);
            if (row.Doc != null)
            {
                int d = S(18), cx = c4.X + S(10), cy = c4.Y + (RowH - d) / 2;
                using (var pen = new Pen(p.Accent, Math.Max(1.4f, DeviceDpi / 96f * 1.4f))) g.DrawEllipse(pen, cx, cy, d, d);
                TextRenderer.DrawText(g, Math.Clamp(row.Doc.Importance, 1, 5).ToString(), small, new Rectangle(cx, cy, d, d), p.Accent, Flags | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(g, WikiViewData.ImportanceLabel(row.Doc.Category, row.Doc.Importance), Font, new Rectangle(cx + d + S(8), c4.Y, c4.Width - d - S(28), RowH), p.Fore, Flags);
            }
            else TextRenderer.DrawText(g, "—", Font, new Rectangle(c4.X + S(10), c4.Y, c4.Width - S(20), RowH), Themes.Blend(p.Main, p.Fore, 0.35f), Flags);
            // 태그 칩
            if (row.Doc != null) DrawChips(g, p, CellRect(r, 5), row.Doc.Labels, "태그 추가", "", icon, small, false);
        }
        // 추가하기 줄
        var ar = AddRowRect;
        if (rows.Count == 0) TextRenderer.DrawText(g, "맞는 문서가 없어요.", Font, new Rectangle(S(16), RowsTop, Width, RowH), p.Dim, Flags);
        int ay = RowsTop + rows.Count * RowH + (rows.Count == 0 ? RowH : 0);
        g.ResetClip();
        TextRenderer.DrawText(g, "+", new Font(Font.FontFamily, 13f), new Rectangle(S(10), ay, S(28), AddH), p.Dim, Flags | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, "추가하기", Font, new Rectangle(S(42), ay, S(200), AddH), p.Fore, Flags);
    }

    private void DrawChips(Graphics g, LibraryPalette p, Rectangle cell, IReadOnlyList<string> names, string addLabel, string glyph, Font icon, Font small, bool folder)
    {
        g.SetClip(Rectangle.Intersect(cell, new Rectangle(0, RowsTop, Width, Height)));
        var lay = ChipLayout(cell, names, addLabel, true);
        foreach (var (rect, text) in lay.Chips)
        {
            FillRounded(g, rect, S(6), p.AccentSoft);
            if (folder) TextRenderer.DrawText(g, glyph, icon, new Rectangle(rect.X + S(5), rect.Y, S(20), rect.Height), p.Fore, Flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, text, small, new Rectangle(rect.X + (folder ? S(26) : S(8)), rect.Y, rect.Width - (folder ? S(26) : S(8)) - S(18), rect.Height), p.Fore, Flags);
            float cx = rect.Right - S(11), cy = rect.Y + rect.Height / 2f, d = S(2);
            using var xp = new Pen(p.Dim, Math.Max(1.2f, DeviceDpi / 96f * 1.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(xp, cx - d, cy - d, cx + d, cy + d);
            g.DrawLine(xp, cx - d, cy + d, cx + d, cy - d);
        }
        if (!lay.Add.IsEmpty)
        {
            FillRounded(g, lay.Add, S(7), p.AccentSoft);
            if (names.Count == 0)
            {
                TextRenderer.DrawText(g, addLabel, small, new Rectangle(lay.Add.X + S(10), lay.Add.Y, lay.Add.Width - S(34), lay.Add.Height), p.Dim, Flags);
                float px = lay.Add.Right - S(15), py = lay.Add.Y + lay.Add.Height / 2f, pd = S(4);
                using var pp = new Pen(p.Dim, Math.Max(1.3f, DeviceDpi / 96f * 1.3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pp, px - pd, py, px + pd, py);
                g.DrawLine(pp, px, py - pd, px, py + pd);
            }
            else
            {
                float px = lay.Add.X + lay.Add.Width / 2f, py = lay.Add.Y + lay.Add.Height / 2f, pd = S(5);
                using var pp = new Pen(p.Fore, Math.Max(1.4f, DeviceDpi / 96f * 1.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pp, px - pd, py, px + pd, py);
                g.DrawLine(pp, px, py - pd, px, py + pd);
            }
        }
        g.ResetClip();
        g.SetClip(new Rectangle(0, ChipsH, Width, Height - ChipsH));
    }
}
