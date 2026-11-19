using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>소설 메인 위키 탭의 네 가지 보기가 함께 쓰는 자료(문서 · 분류 계층 · 대분류).</summary>
internal sealed class WikiViewData
{
    public string NovelName = "";
    public string NovelDir = "";
    public List<WikiDoc> Docs = new();
    /// <summary>만들어 둔 분류(요약 · 대분류 포함).</summary>
    public List<WikiStore.ClassInfo> Classes = new();
    /// <summary>분류 → 상위 분류들(문서에만 쓰인 분류도 맨 위로 들어 있다).</summary>
    public Dictionary<string, List<string>> Parents = new(StringComparer.OrdinalIgnoreCase);

    public static WikiViewData Build(string novelDir, List<WikiDoc> docs)
    {
        var d = new WikiViewData { NovelDir = novelDir, NovelName = Path.GetFileName(novelDir), Docs = docs };
        d.Classes = WikiStore.ClassInfos(novelDir);
        foreach (var kv in WikiStore.ClassParents(novelDir)) d.Parents[kv.Key] = kv.Value;
        foreach (var t in docs.SelectMany(x => x.Tags)) d.Parents.TryAdd(t, new List<string>());
        return d;
    }

    public List<string> ParentsOf(string cls) =>
        Parents.TryGetValue(cls, out var ps) ? ps.Where(p => !p.Equals(cls, StringComparison.OrdinalIgnoreCase) && Parents.ContainsKey(p)).ToList() : new();

    public List<string> Children(string? parent) =>
        Parents.Keys.Where(k => parent == null ? ParentsOf(k).Count == 0 : ParentsOf(k).Contains(parent, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k, NaturalStringComparer.Instance).ToList();

    public WikiStore.ClassInfo? InfoOf(string cls) => Classes.FirstOrDefault(c => c.Name.Equals(cls, StringComparison.OrdinalIgnoreCase));

    /// <summary>분류의 대분류: 맨 위 분류가 고른 대분류를 따라간다(문서에만 쓰인 분류는 미분류).</summary>
    public string CategoryOf(string cls)
    {
        string cur = cls;
        for (int guard = 0; guard < 16; guard++)
        {
            var ps = ParentsOf(cur);
            if (ps.Count == 0) return InfoOf(cur)?.Category ?? "none";
            cur = ps[0];
        }
        return "none";
    }

    public List<string> RootClasses(string categoryKey) => Children(null).Where(c => CategoryOf(c) == categoryKey).ToList();
    public List<WikiDoc> DocsIn(string cls) => Docs.Where(d => d.Tags.Contains(cls, StringComparer.OrdinalIgnoreCase)).ToList();
    public List<WikiDoc> LooseDocs(string categoryKey) => Docs.Where(d => d.Category == categoryKey && d.Tags.Count == 0).ToList();
    public static bool IsWritten(WikiDoc d) => !string.IsNullOrWhiteSpace(d.Body) || d.Summary.Length > 0;

    /// <summary>분류 폴더들을 위에서 아래로(계층 순서, 깊이와 함께) 편다.</summary>
    public List<(string Name, int Depth)> Flatten(string categoryKey)
    {
        var list = new List<(string, int)>();
        void Walk(string n, int depth, HashSet<string> path)
        {
            if (!path.Add(n)) return;
            list.Add((n, depth));
            foreach (var c in Children(n)) Walk(c, depth + 1, new HashSet<string>(path, StringComparer.OrdinalIgnoreCase));
        }
        foreach (var r in RootClasses(categoryKey)) Walk(r, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>중요도의 이름: 등장인물은 주인공 … 단역, 나머지는 핵심 설정 … 참고 설정.</summary>
    public static string ImportanceLabel(string categoryKey, int v)
    {
        v = Math.Clamp(v, 1, 5);
        string[] chars = { "단역", "엑스트라", "조연", "주요 인물", "주인공" };
        string[] other = { "참고 설정", "보조 설정", "일반 설정", "주요 설정", "핵심 설정" };
        return (categoryKey == "character" ? chars : other)[v - 1];
    }
}

/// <summary>위키 보기 네 가지의 공통 바탕: 팔레트 · 자료 · 문서를 눌렀을 때의 알림.</summary>
internal abstract class WikiViewBase : Control
{
    protected LibraryPalette? P;
    protected WikiViewData Data = new();

    public event Action<string>? DocChosen;
    public event Action<string, Point>? DocMenuRequested;
    public event Action<string, Point>? FolderMenuRequested;

    protected void RaiseDoc(string path) => DocChosen?.Invoke(path);
    protected void RaiseDocMenu(string path, Point screen) => DocMenuRequested?.Invoke(path, screen);
    protected void RaiseFolderMenu(string name, Point screen) => FolderMenuRequested?.Invoke(name, screen);

    /// <summary>쪽 전체 폭을 쓰는 보기인가(표 · 마인드맵). 아니면 본문 칸 폭.</summary>
    public virtual bool Wide => false;
    /// <summary>이 폭에서 필요한 높이(쪽 전체가 스크롤되므로 목록은 다 펼친다).</summary>
    public abstract int PreferredHeightFor(int width);

    protected WikiViewBase()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
    }

    protected int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void SetPalette(LibraryPalette p) { P = p; BackColor = p.Main; OnPalette(); Invalidate(); }
    public void SetData(WikiViewData d) { Data = d; OnData(); Invalidate(); }
    protected virtual void OnPalette() { }
    protected abstract void OnData();

    protected static readonly TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;

    protected static void FillRounded(Graphics g, Rectangle r, int radius, Color fill, Color? line = null)
    {
        using var path = LibraryPalette.Rounded(new RectangleF(r.X, r.Y, r.Width - 1, r.Height - 1), radius);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (line != null) using (var pen = new Pen(line.Value)) g.DrawPath(pen, path);
    }
}

/// <summary>위키 탭 맨 위의 보기 전환 막대: [카테고리별 보기 | 간단히 보기 | 데이터베이스 뷰 | 마인드맵 보기]. 고른 쪽만 이름이 보인다.</summary>
internal sealed class WikiViewBar : Control
{
    public static readonly string[] Names = { "카테고리별 보기", "간단히 보기", "데이터베이스 뷰", "마인드맵 보기" };
    private LibraryPalette? p;
    private int selected, hover = -1;
    public event Action<int>? SelectedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Selected { get => selected; set { if (selected == value) return; selected = value; Invalidate(); } }

    public WikiViewBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        Cursor = Cursors.Hand;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    public void SetPalette(LibraryPalette pal) { p = pal; Invalidate(); }

    private int SegW(int i) => i == selected ? S(46) + TextRenderer.MeasureText(Names[i], Font, Size.Empty, TextFormatFlags.NoPadding).Width : S(44);
    public int FitWidth() { int w = S(6); for (int i = 0; i < 4; i++) w += SegW(i); return w; }
    public int FitHeight() => S(38);

    private Rectangle SegRect(int i)
    {
        int x = S(3);
        for (int k = 0; k < i; k++) x += SegW(k);
        return new Rectangle(x, S(3), SegW(i), Height - S(6));
    }

    private int At(Point pt) { for (int i = 0; i < 4; i++) if (SegRect(i).Contains(pt)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = At(e.Location); if (i != hover) { hover = i; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = At(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0 && i != selected) { selected = i; Invalidate(); SelectedChanged?.Invoke(i); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = LibraryPalette.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), S(8)))
        {
            using var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, p.Dark ? 0.08f : 0.04f));
            g.FillPath(b, path);
            using var pen = new Pen(p.Line);
            g.DrawPath(pen, path);
        }
        for (int i = 0; i < 4; i++)
        {
            var r = SegRect(i);
            bool sel = i == selected;
            if (sel)
            {
                using var path = LibraryPalette.Rounded(new RectangleF(r.X, r.Y, r.Width - 1, r.Height - 1), S(7));
                using var b = new SolidBrush(p.Dark ? Themes.Blend(p.Main, p.Fore, 0.16f) : Color.White);
                g.FillPath(b, path);
                using var pen = new Pen(p.Line);
                g.DrawPath(pen, path);
            }
            else if (i > 0 && i - 1 != selected)
            {
                using var sep = new Pen(p.Line);
                g.DrawLine(sep, r.X, r.Y + S(8), r.X, r.Bottom - S(8));
            }
            var ic = new Rectangle(r.X + S(sel ? 12 : 12), r.Y, S(20), r.Height);
            Color col = sel ? p.Accent : (i == hover ? p.Fore : p.Dim);
            DrawIcon(g, i, ic, col);
            if (sel) TextRenderer.DrawText(g, Names[i], Font, new Rectangle(r.X + S(38), r.Y, r.Width - S(42), r.Height), p.Fore,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>작은 선 아이콘 네 가지(카테고리 · 목록 · 데이터베이스 · 마인드맵).</summary>
    private void DrawIcon(Graphics g, int kind, Rectangle r, Color c)
    {
        float u = DeviceDpi / 96f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using var pen = new Pen(c, Math.Max(1.2f, 1.3f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (kind)
        {
            case 0: // 네모 둘 + 줄 둘
                for (int k = 0; k < 2; k++)
                {
                    float y = cy - 5.5f * u + k * 8f * u;
                    g.DrawRectangle(pen, cx - 8f * u, y - 2f * u, 4f * u, 4f * u);
                    g.DrawLine(pen, cx - 2f * u, y - 1.5f * u, cx + 8f * u, y - 1.5f * u);
                    g.DrawLine(pen, cx - 2f * u, y + 1.5f * u, cx + 5f * u, y + 1.5f * u);
                }
                break;
            case 1: // 점 · 줄 셋
                for (int k = 0; k < 3; k++)
                {
                    float y = cy - 5f * u + k * 5f * u;
                    g.DrawLine(pen, cx - 8f * u, y, cx - 7.5f * u, y);
                    g.DrawLine(pen, cx - 3f * u, y, cx + 8f * u, y);
                }
                break;
            case 2: // 데이터베이스 통
                g.DrawEllipse(pen, cx - 7f * u, cy - 8f * u, 14f * u, 5f * u);
                g.DrawLine(pen, cx - 7f * u, cy - 5.5f * u, cx - 7f * u, cy + 5.5f * u);
                g.DrawLine(pen, cx + 7f * u, cy - 5.5f * u, cx + 7f * u, cy + 5.5f * u);
                g.DrawArc(pen, cx - 7f * u, cy + 3f * u, 14f * u, 5f * u, 0, 180);
                g.DrawArc(pen, cx - 7f * u, cy - 1.2f * u, 14f * u, 5f * u, 0, 180);
                break;
            default: // 마인드맵: 가운데 점과 이어진 작은 동그라미들
                g.DrawLine(pen, cx - 5f * u, cy + 3f * u, cx, cy);
                g.DrawLine(pen, cx, cy, cx + 5f * u, cy - 4f * u);
                g.DrawLine(pen, cx, cy, cx + 5f * u, cy + 4f * u);
                g.DrawEllipse(pen, cx - 8f * u, cy + 1f * u, 4f * u, 4f * u);
                g.DrawEllipse(pen, cx - 1.7f * u, cy - 1.7f * u, 3.4f * u, 3.4f * u);
                g.DrawEllipse(pen, cx + 4.5f * u, cy - 7f * u, 4f * u, 4f * u);
                g.DrawEllipse(pen, cx + 4.5f * u, cy + 3f * u, 4f * u, 4f * u);
                break;
        }
    }
}
