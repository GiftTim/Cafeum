using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 마인드맵 보기(옵시디언 그래프처럼): 문서 · 분류 · 대분류 · 작품이 점으로, 연결이 선으로 놓이고 물리 시뮬레이션으로 서로 밀고 당기며 자리를 잡는다.
/// 점은 연결이 많을수록 크다. 점에 마우스를 올리면 그 점과 이어진 것만 또렷하고 나머지는 흐려진다.
/// 점을 끌어 옮기고(놓으면 다시 자리를 잡는다), 빈 곳을 끌어 화면을 옮기고, 휠로 확대 · 축소, 오른쪽 위 버튼으로 전체 맞추기.
/// 점을 두 번 누르면 문서는 그 문서가, 대분류 · 분류는 그 쪽이 열린다.
/// </summary>
internal sealed class WikiMindMapView : WikiViewBase
{
    private enum NodeKind { Root, Category, Class, Doc }
    private sealed class Node
    {
        public NodeKind Kind;
        public string Label = "";
        public string? DocPath;
        public string? CategoryKey;
        public bool IsTemplate;
        public PointF Pos, Vel;
        public bool Pinned;
        public int Degree;
        /// <summary>작품 점에 표지 그림이 들어갈 때 더 키우는 반지름.</summary>
        public float Extra;
        public readonly List<Node> Links = new();
        public float Radius => Kind switch { NodeKind.Root => 9f + Extra + MathF.Sqrt(Degree) * 1.2f, NodeKind.Category => 3.4f + MathF.Sqrt(Degree) * 0.4f, NodeKind.Class => 2.6f + MathF.Sqrt(Degree) * 0.6f, _ => 3.5f + MathF.Sqrt(Degree) * 1.3f };
    }

    private Bitmap? cover;
    private readonly List<Node> nodes = new();
    private readonly List<(Node A, Node B)> edges = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private float alpha = 1f;
    private float zoom = 1f;
    private PointF pan;
    private bool needFit = true, panning, movedPan;
    private Node? hot, drag;
    private Point downAt, lastMouse;
    private bool dragMoved;
    private Rectangle fitRect;
    private enum Tool { NewDoc, NewClass, NewTemplate, Menu }
    private Tool? hoverTool;
    private bool toolDown;

    /// <summary>점 위에 뜬 도구 막대에서 [문서 만들기]를 골랐다(대분류 키, 분류 이름 — 없으면 null).</summary>
    public event Action<string?, string?>? CreateDocRequested;
    /// <summary>[분류 만들기]를 골랐다(대분류 키, 상위 분류 이름 — 없으면 null).</summary>
    public event Action<string?, string?>? CreateClassRequested;
    /// <summary>[틀 만들기]를 골랐다(대분류 키, 분류 이름 — 없으면 null).</summary>
    public event Action<string?, string?>? CreateTemplateRequested;

    public override bool Wide => true;
    public override int PreferredHeightFor(int width) => S(640);

    public WikiMindMapView()
    {
        timer.Tick += (_, _) => Step();
        Disposed += (_, _) => { timer.Dispose(); cover?.Dispose(); };
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) timer.Stop(); else if (alpha > 0.02f) timer.Start(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (needFit) Invalidate(); }

    protected override void OnData()
    {
        Build();
        // 처음엔 미리 돌려서 어느 정도 자리를 잡은 모습으로 보여 준다
        alpha = 1f;
        for (int i = 0; i < 180; i++) Tick();
        needFit = true;
        if (Visible) timer.Start();
    }

    private void Build()
    {
        nodes.Clear(); edges.Clear();
        var rng = new Random(7);
        Node New(NodeKind kind, string label, string? path = null)
        {
            var n = new Node { Kind = kind, Label = label, DocPath = path, Pos = new PointF((float)(rng.NextDouble() - 0.5) * 300, (float)(rng.NextDouble() - 0.5) * 300) };
            nodes.Add(n);
            return n;
        }
        void Link(Node a, Node b) { if (a == b || a.Links.Contains(b)) return; a.Links.Add(b); b.Links.Add(a); a.Degree++; b.Degree++; edges.Add((a, b)); }

        var root = New(NodeKind.Root, Data.NovelName);
        cover?.Dispose();
        cover = Data.NovelDir.Length > 0 ? NovelStore.LoadCover(Data.NovelDir) : null; // 가운데 작품 점에 올릴 표지 사진
        if (cover != null) root.Extra = 14f;
        root.Pos = PointF.Empty; root.Pinned = true; // 가운데 작품 점은 움직이지 않게 고정(사진이 흔들려 보이지 않게)
        var cats = new Dictionary<string, Node>();
        foreach (var c in WikiCategory.All) { var n = New(NodeKind.Category, c.Label); n.CategoryKey = c.Key; cats[c.Key] = n; Link(root, n); }
        var classes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Data.Parents.Keys) { classes[name] = New(NodeKind.Class, name); classes[name].CategoryKey = Data.CategoryOf(name); }
        foreach (var name in Data.Parents.Keys)
        {
            var ps = Data.ParentsOf(name);
            if (ps.Count == 0) Link(cats[Data.CategoryOf(name)], classes[name]);
            else foreach (var par in ps) Link(classes[par], classes[name]);
        }
        foreach (var d in Data.Docs)
        {
            var n = New(NodeKind.Doc, d.Title, d.Path);
            n.CategoryKey = d.Category;
            var mine = d.Tags.Where(classes.ContainsKey).ToList();
            if (mine.Count == 0) Link(cats[d.Category], n);
            else foreach (var t in mine) Link(classes[t], n);
        }
        // 템플릿: 자기 대분류에 이어 "템플릿:이름" 점으로 둔다(눌러서 열 수 있다)
        foreach (var t in Data.Templates)
        {
            var n = New(NodeKind.Doc, "템플릿:" + t.Title, t.Path);
            n.CategoryKey = cats.ContainsKey(t.Category) ? t.Category : "none";
            n.IsTemplate = true;
            Link(cats[n.CategoryKey], n);
        }
        // 대분류 가까이에서 시작하게 해서 처음 모양이 덜 어지럽게
        int ci = 0;
        foreach (var c in cats.Values)
        {
            double a = ci++ * Math.PI * 2 / cats.Count;
            c.Pos = new PointF((float)Math.Cos(a) * 340, (float)Math.Sin(a) * 340);
        }
    }

    // ───────── 물리 시뮬레이션 ─────────

    private void Tick()
    {
        int n = nodes.Count;
        if (n == 0) return;
        float k = Math.Max(alpha, 0.05f);
        // 서로 밀어낸다
        for (int i = 0; i < n; i++)
        {
            var a = nodes[i];
            for (int j = i + 1; j < n; j++)
            {
                var b = nodes[j];
                float dx = a.Pos.X - b.Pos.X, dy = a.Pos.Y - b.Pos.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > 490000f) continue; // 먼 점끼리는 건너뛴다
                if (d2 < 1f) { dx = (i - j) * 0.5f; dy = 0.7f; d2 = dx * dx + dy * dy; }
                float f = 9000f / d2;
                float inv = 1f / MathF.Sqrt(d2);
                float fx = dx * inv * f, fy = dy * inv * f;
                a.Vel = new PointF(a.Vel.X + fx, a.Vel.Y + fy);
                b.Vel = new PointF(b.Vel.X - fx, b.Vel.Y - fy);
            }
        }
        // 이어진 점끼리 당긴다
        foreach (var (a, b) in edges)
        {
            float dx = b.Pos.X - a.Pos.X, dy = b.Pos.Y - a.Pos.Y;
            float d = MathF.Sqrt(dx * dx + dy * dy) + 0.01f;
            float rest = a.Kind == NodeKind.Root || b.Kind == NodeKind.Root ? 230f : 100f;
            float f = (d - rest) * 0.03f;
            float fx = dx / d * f, fy = dy / d * f;
            a.Vel = new PointF(a.Vel.X + fx, a.Vel.Y + fy);
            b.Vel = new PointF(b.Vel.X - fx, b.Vel.Y - fy);
        }
        // 가운데로 모으고, 속도를 줄이며 옮긴다
        foreach (var a in nodes)
        {
            if (a.Pinned) { a.Vel = PointF.Empty; continue; }
            float vx = (a.Vel.X - a.Pos.X * 0.007f) * 0.82f, vy = (a.Vel.Y - a.Pos.Y * 0.007f) * 0.82f;
            vx = Math.Clamp(vx, -18f, 18f) * k; vy = Math.Clamp(vy, -18f, 18f) * k;
            a.Vel = new PointF(vx / Math.Max(k, 0.05f), vy / Math.Max(k, 0.05f));
            a.Pos = new PointF(a.Pos.X + vx, a.Pos.Y + vy);
        }
        alpha *= 0.985f;
    }

    private void Step()
    {
        if (!Visible || IsDisposed) { timer.Stop(); return; }
        Tick();
        if (needFit && alpha < 0.3f) { }
        Invalidate();
        if (alpha < 0.02f && drag == null) timer.Stop();
    }

    private void Reheat() { alpha = Math.Max(alpha, 0.5f); if (!timer.Enabled) timer.Start(); }

    // ───────── 화면 ─────────

    private void Fit()
    {
        needFit = false;
        if (nodes.Count == 0 || Width <= 0 || Height <= 0) return;
        float minX = nodes.Min(n => n.Pos.X), maxX = nodes.Max(n => n.Pos.X), minY = nodes.Min(n => n.Pos.Y), maxY = nodes.Max(n => n.Pos.Y);
        float bw = Math.Max(60f, maxX - minX + 80), bh = Math.Max(60f, maxY - minY + 60);
        zoom = Math.Clamp(Math.Min((Width - S(40)) / bw, (Height - S(40)) / bh), 0.15f, 2.2f);
        pan = new PointF(Width / 2f - (minX + maxX) / 2f * zoom, Height / 2f - (minY + maxY) / 2f * zoom);
    }

    private PointF ToScreen(PointF w) => new(pan.X + w.X * zoom, pan.Y + w.Y * zoom);
    private PointF ToWorld(Point s) => new((s.X - pan.X) / zoom, (s.Y - pan.Y) / zoom);

    private Node? NodeAt(Point pt)
    {
        Node? best = null;
        float bd = float.MaxValue;
        foreach (var n in nodes)
        {
            var s = ToScreen(n.Pos);
            float r = Math.Max(S(7), n.Radius * zoom + S(3));
            float d = (s.X - pt.X) * (s.X - pt.X) + (s.Y - pt.Y) * (s.Y - pt.Y);
            if (d <= r * r && d < bd) { best = n; bd = d; }
        }
        return best;
    }

    private float NodePx(Node n) => Math.Max(S(3), n.Radius * zoom * (DeviceDpi / 96f) * 0.9f);
    private Rectangle NodeRect(Node n) { var s = ToScreen(n.Pos); float r = NodePx(n); return new Rectangle((int)(s.X - r), (int)(s.Y - r), (int)(r * 2), (int)(r * 2)); }

    /// <summary>마우스가 올라간 점 위에 뜨는 도구 막대: 문서 점은 [≡]만, 분류는 [문서 · 분류 · 틀 · ≡], 대분류 · 작품은 [문서 · 분류 · 틀].</summary>
    private (Rectangle Pill, List<(Rectangle R, Tool T)> Buttons)? ToolbarLayout()
    {
        if (hot == null || drag != null || panning) return null;
        var tools = hot.Kind switch
        {
            NodeKind.Doc => new[] { Tool.Menu },
            NodeKind.Class => new[] { Tool.NewDoc, Tool.NewClass, Tool.NewTemplate, Tool.Menu },
            _ => new[] { Tool.NewDoc, Tool.NewClass, Tool.NewTemplate },
        };
        int bw = S(34), h = S(34), pad = S(3);
        int w = tools.Length == 1 ? h : tools.Length * bw + pad * 2;
        var s = ToScreen(hot.Pos);
        int x = (int)s.X - w / 2, y = (int)(s.Y - NodePx(hot)) - h - S(18);
        x = Math.Clamp(x, S(4), Math.Max(S(4), Width - w - S(4)));
        y = Math.Max(S(4), y);
        var pill = new Rectangle(x, y, w, h);
        var list = new List<(Rectangle, Tool)>();
        for (int i = 0; i < tools.Length; i++)
            list.Add((tools.Length == 1 ? new Rectangle(x + S(3), y + S(3), h - S(6), h - S(6)) : new Rectangle(x + pad + i * bw, y + S(2), bw, h - S(4)), tools[i]));
        return (pill, list);
    }

    private bool InToolbarZone(Point pt, out Tool? tool)
    {
        tool = null;
        var tb = ToolbarLayout();
        if (tb == null || hot == null) return false;
        var zone = Rectangle.Union(tb.Value.Pill, NodeRect(hot));
        zone.Inflate(S(6), S(4));
        if (!zone.Contains(pt)) return false;
        foreach (var (r, t) in tb.Value.Buttons) if (r.Contains(pt)) tool = t;
        return true;
    }

    private void RunTool(Tool t, Point at)
    {
        var n = hot;
        if (n == null) return;
        string? cls = n.Kind == NodeKind.Class ? n.Label : null;
        switch (t)
        {
            case Tool.NewDoc: CreateDocRequested?.Invoke(n.CategoryKey, cls); break;
            case Tool.NewClass: CreateClassRequested?.Invoke(n.CategoryKey, cls); break;
            case Tool.NewTemplate: CreateTemplateRequested?.Invoke(n.CategoryKey, cls); break;
            case Tool.Menu:
                if (n.Kind == NodeKind.Doc && n.DocPath != null) RaiseDocMenu(n.DocPath, PointToScreen(at));
                else if (n.Kind == NodeKind.Class) RaiseFolderMenu(n.Label, PointToScreen(at));
                break;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        if (InToolbarZone(e.Location, out var tdown) && tdown != null) { toolDown = true; return; } // 도구 막대 단추: 끌기를 시작하지 않는다
        downAt = lastMouse = e.Location;
        dragMoved = false;
        var n = NodeAt(e.Location);
        if (n != null) { drag = n; n.Pinned = true; }
        else { panning = true; movedPan = false; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (drag != null && e.Button == MouseButtons.Left)
        {
            if (Math.Abs(e.X - downAt.X) > 4 || Math.Abs(e.Y - downAt.Y) > 4) dragMoved = true;
            if (dragMoved) { drag.Pos = ToWorld(e.Location); Reheat(); Invalidate(); Cursor = Cursors.SizeAll; }
            return;
        }
        if (panning)
        {
            if (Math.Abs(e.X - downAt.X) > 4 || Math.Abs(e.Y - downAt.Y) > 4) movedPan = true;
            if (movedPan) { pan = new PointF(pan.X + e.X - lastMouse.X, pan.Y + e.Y - lastMouse.Y); lastMouse = e.Location; Invalidate(); Cursor = Cursors.SizeAll; }
            return;
        }
        // 점 위에 뜬 도구 막대(와 점 사이)에 있으면 그 점을 그대로 가리킨 걸로 친다
        if (InToolbarZone(e.Location, out var tool))
        {
            if (tool != hoverTool) { hoverTool = tool; Invalidate(); }
            Cursor = tool != null ? Cursors.Hand : Cursors.Default;
            return;
        }
        hoverTool = null;
        var h = NodeAt(e.Location);
        if (h != hot) { hot = h; Invalidate(); }
        Cursor = fitRect.Contains(e.Location) || h != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (drag == null) { hot = null; hoverTool = null; Invalidate(); } }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (toolDown)
        {
            toolDown = false;
            if (e.Button == MouseButtons.Left && InToolbarZone(e.Location, out var tup) && tup != null) RunTool(tup.Value, e.Location);
            return;
        }
        var d = drag;
        bool wasMoved = dragMoved || movedPan;
        drag = null; panning = false; dragMoved = false; movedPan = false;
        if (d != null && d.Kind != NodeKind.Root) d.Pinned = false; // 작품(가운데) 점은 늘 제자리에 둔다
        if (wasMoved) { if (d != null) Reheat(); return; }
        if (fitRect.Contains(e.Location) && e.Button == MouseButtons.Left) { Fit(); Invalidate(); return; }
        var n = d ?? NodeAt(e.Location);
        if (n == null) return;
        if (n.Kind == NodeKind.Doc && n.DocPath != null)
        {
            if (e.Button == MouseButtons.Right) RaiseDocMenu(n.DocPath, PointToScreen(e.Location)); // 문서는 두 번 눌러야 열린다
        }
        else if (n.Kind == NodeKind.Class && e.Button == MouseButtons.Right) RaiseFolderMenu(n.Label, PointToScreen(e.Location));
    }

    /// <summary>점을 두 번 누르면: 문서는 그 문서, 분류 · 대분류는 그 쪽을 연다.</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        var n = NodeAt(e.Location);
        if (n == null) return;
        switch (n.Kind)
        {
            case NodeKind.Doc when n.DocPath != null: { string dp = n.DocPath; BeginInvoke(() => RaiseDoc(dp)); break; }
            case NodeKind.Class: { string cn = n.Label; BeginInvoke(() => RaiseClass(cn)); break; }
            case NodeKind.Category when n.CategoryKey != null: { string ck = n.CategoryKey; BeginInvoke(() => RaiseCategory(ck)); break; }
        }
    }

    /// <summary>휠로 확대 · 축소(커서 아래 점이 그대로 있게). 옵시디언 그래프처럼 이 보기 위에서는 휠이 쪽이 아니라 그래프를 움직인다.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        float nz = Math.Clamp(zoom * (e.Delta > 0 ? 1.12f : 1f / 1.12f), 0.1f, 4f);
        float wx = (e.X - pan.X) / zoom, wy = (e.Y - pan.Y) / zoom;
        zoom = nz;
        pan = new PointF(e.X - wx * zoom, e.Y - wy * zoom);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var p = P;
        if (p == null) return;
        if (needFit) Fit();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = BackColor;
        var focusSet = hot == null ? null : new HashSet<Node>(hot.Links) { hot };
        Color Dimmed(Color c) => focusSet == null ? c : Themes.Blend(bg, c, 0.22f);

        // 이음선
        using var edgePen = new Pen(Themes.Blend(bg, p.Fore, 0.16f), Math.Max(1f, DeviceDpi / 96f));
        using var edgeDim = new Pen(Themes.Blend(bg, p.Fore, 0.05f), Math.Max(1f, DeviceDpi / 96f));
        using var edgeHot = new Pen(p.Accent, Math.Max(1.5f, DeviceDpi / 96f * 1.6f));
        foreach (var (a, b) in edges)
        {
            var sa = ToScreen(a.Pos); var sb = ToScreen(b.Pos);
            if (Math.Max(sa.X, sb.X) < -20 || Math.Min(sa.X, sb.X) > Width + 20 || Math.Max(sa.Y, sb.Y) < -20 || Math.Min(sa.Y, sb.Y) > Height + 20) continue;
            bool on = hot != null && (a == hot || b == hot);
            g.DrawLine(on ? edgeHot : (focusSet == null ? edgePen : edgeDim), sa, sb);
        }

        // 점
        var cDoc = Themes.Blend(bg, p.Fore, 0.45f);
        var cClass = p.Accent;
        var cCat = Themes.Blend(p.Accent, p.Fore, 0.35f);
        float fs = Math.Clamp(6.5f * Math.Min(zoom, 1.2f) + 1f, 6f, 9f);
        using var label = new Font(Font.FontFamily, fs);
        using var rootFont = new Font(Font.FontFamily, fs + 1f, FontStyle.Bold);
        bool showAll = zoom >= 0.7f;
        // 흐린 점 먼저, 또렷한 점 나중에
        foreach (var pass in new[] { false, true })
            foreach (var n in nodes)
            {
                bool inFocus = focusSet == null || focusSet.Contains(n);
                if (inFocus != pass) continue;
                var s = ToScreen(n.Pos);
                float r = Math.Max(S(3), n.Radius * zoom * (DeviceDpi / 96f) * 0.9f);
                if (s.X < -50 || s.X > Width + 50 || s.Y < -50 || s.Y > Height + 50) continue;
                Color baseColor = n.Kind switch { NodeKind.Root => p.Accent, NodeKind.Category => cCat, NodeKind.Class => cClass, _ => n.IsTemplate ? p.Accent : cDoc }; // 템플릿 점은 강조색
                if (n == hot) baseColor = p.Accent;
                switch (n.Kind)
                {
                    case NodeKind.Category: // 대분류: 네모, 가운데를 비운다(테두리만)
                    {
                        using var fill = new SolidBrush(bg);
                        g.FillRectangle(fill, s.X - r, s.Y - r, r * 2, r * 2);
                        using var pen = new Pen(Dimmed(baseColor), Math.Max(1.5f, DeviceDpi / 96f * 1.8f));
                        g.DrawRectangle(pen, s.X - r, s.Y - r, r * 2, r * 2);
                        break;
                    }
                    case NodeKind.Class: // 분류: 같은 네모, 가운데를 채우고 조금 작게
                    {
                        float q = r * 0.78f;
                        using var b = new SolidBrush(Dimmed(baseColor));
                        g.FillRectangle(b, s.X - q, s.Y - q, q * 2, q * 2);
                        break;
                    }
                    case NodeKind.Root when cover != null: // 작품: 표지 사진을 동그랗게 오려 올린다
                    {
                        using var circle = new GraphicsPath();
                        circle.AddEllipse(s.X - r, s.Y - r, r * 2, r * 2);
                        var saved = g.Save();
                        g.SetClip(circle, CombineMode.Intersect);
                        int side = Math.Min(cover.Width, cover.Height);
                        var src = new Rectangle((cover.Width - side) / 2, (cover.Height - side) / 3, side, side); // 세로로 긴 표지는 위쪽 가운데를 보여 준다
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(cover, new Rectangle((int)MathF.Round(s.X - r), (int)MathF.Round(s.Y - r), (int)MathF.Round(r * 2), (int)MathF.Round(r * 2)), src, GraphicsUnit.Pixel);
                        g.Restore(saved);
                        using var ring = new Pen(Dimmed(p.Accent), Math.Max(2f, DeviceDpi / 96f * 2.2f));
                        g.DrawEllipse(ring, s.X - r, s.Y - r, r * 2, r * 2);
                        break;
                    }
                    default: // 작품 · 문서: 동그라미
                    {
                        using var b = new SolidBrush(Dimmed(baseColor));
                        g.FillEllipse(b, s.X - r, s.Y - r, r * 2, r * 2);
                        break;
                    }
                }
                if (n.Kind == NodeKind.Doc && n == hot) using (var ring = new Pen(Color.FromArgb(120, p.Accent), S(3))) g.DrawEllipse(ring, s.X - r - S(2), s.Y - r - S(2), r * 2 + S(4), r * 2 + S(4));
                bool lab = n == hot || n.Kind == NodeKind.Root || (n.Kind == NodeKind.Category && zoom >= 0.3f);
                if (!lab) continue;
                var f = n.Kind == NodeKind.Root ? rootFont : label;
                var sz = TextRenderer.MeasureText(n.Label, f, Size.Empty, TextFormatFlags.NoPadding);
                Color lc = n == hot ? p.Accent : Dimmed(Themes.Blend(bg, p.Fore, n.Kind == NodeKind.Doc ? 0.6f : 0.75f));
                // 제목이 길면 …로 줄인다(작품은 조금 더 길게)
                int maxW = n.Kind == NodeKind.Root ? S(170) : S(110);
                int lw = Math.Min(sz.Width, maxW);
                TextRenderer.DrawText(g, n.Label, f, new Rectangle((int)(s.X - lw / 2f), (int)(s.Y + r + S(2)), lw + S(2), sz.Height + S(2)), lc,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }

        // 마우스를 올린 점 위의 도구 막대
        var tbl = ToolbarLayout();
        if (tbl != null) DrawToolbar(g, p, tbl.Value.Pill, tbl.Value.Buttons);

        // 전체 맞추기 버튼(오른쪽 위)
        int bs = S(46);
        fitRect = new Rectangle(Width - bs - S(20), S(14), bs, bs);
        using (var path = LibraryPalette.Rounded(new RectangleF(fitRect.X, fitRect.Y, fitRect.Width - 1, fitRect.Height - 1), bs / 2))
        using (var b = new SolidBrush(Themes.Blend(bg, p.Fore, p.Dark ? 0.12f : 0.06f))) g.FillPath(b, path);
        float cx = fitRect.X + fitRect.Width / 2f, cy = fitRect.Y + fitRect.Height / 2f, a2 = S(9), l = S(5);
        using (var pen = new Pen(p.Fore, Math.Max(1.5f, DeviceDpi / 96f * 1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
            {
                g.DrawLine(pen, cx + sx * a2, cy + sy * a2, cx + sx * (a2 - l), cy + sy * a2);
                g.DrawLine(pen, cx + sx * a2, cy + sy * a2, cx + sx * a2, cy + sy * (a2 - l));
            }
            g.DrawEllipse(pen, cx - S(3), cy - S(3), S(6), S(6));
        }
        if (nodes.Count <= 1)
            TextRenderer.DrawText(g, "아직 위키 문서가 없어요.", Font, new Rectangle(0, Height / 2 + S(30), Width, S(30)), p.Dim, Flags | TextFormatFlags.HorizontalCenter);
    }

    private static string ToolName(Tool t) => t switch { Tool.NewDoc => "문서 만들기", Tool.NewClass => "분류 만들기", Tool.NewTemplate => "틀 만들기", _ => "메뉴" };

    private void DrawToolbar(Graphics g, LibraryPalette p, Rectangle pill, List<(Rectangle R, Tool T)> buttons)
    {
        // 그림자(옅은 겹)와 흰 알약 바탕
        for (int i = 3; i >= 1; i--)
        {
            var sr = Rectangle.Inflate(pill, S(i), S(i));
            using var sp = LibraryPalette.Rounded(new RectangleF(sr.X, sr.Y + S(2), sr.Width - 1, sr.Height - 1), pill.Height / 2 + S(i));
            using var sb = new SolidBrush(Color.FromArgb(10, 0, 0, 0));
            g.FillPath(sb, sp);
        }
        using (var path = LibraryPalette.Rounded(new RectangleF(pill.X, pill.Y, pill.Width - 1, pill.Height - 1), pill.Height / 2))
        {
            using var b = new SolidBrush(p.Dark ? Themes.Blend(p.Main, p.Fore, 0.14f) : Color.White);
            g.FillPath(b, path);
            using var pen = new Pen(p.Line);
            g.DrawPath(pen, path);
        }
        foreach (var (r, t) in buttons)
        {
            bool on = hoverTool == t;
            if (on)
            {
                using var path = LibraryPalette.Rounded(new RectangleF(r.X, r.Y, r.Width - 1, r.Height - 1), Math.Min(r.Width, r.Height) / 2);
                using var b = new SolidBrush(Themes.Blend(p.Main, p.Fore, 0.1f));
                g.FillPath(b, path);
            }
            DrawToolIcon(g, t, r, p.Fore);
        }
        // 마우스가 올라간 단추의 이름표(검은 상자)
        if (hoverTool is Tool ht)
        {
            var hr = buttons.First(b => b.T == ht).R;
            using var tf = new Font(Font.FontFamily, 9f, FontStyle.Bold);
            var sz = TextRenderer.MeasureText(ToolName(ht), tf, Size.Empty, TextFormatFlags.NoPadding);
            var tip = new Rectangle(hr.X + hr.Width / 2 - (sz.Width + S(20)) / 2, pill.Bottom + S(6), sz.Width + S(20), sz.Height + S(10));
            tip.X = Math.Clamp(tip.X, S(4), Math.Max(S(4), Width - tip.Width - S(4)));
            using var tp = LibraryPalette.Rounded(new RectangleF(tip.X, tip.Y, tip.Width - 1, tip.Height - 1), S(8));
            using (var tb = new SolidBrush(Color.FromArgb(18, 18, 18))) g.FillPath(tb, tp);
            TextRenderer.DrawText(g, ToolName(ht), tf, tip, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>도구 막대의 선 아이콘: 문서+ · 폴더+ · 퍼즐(틀) · ≡.</summary>
    private void DrawToolIcon(Graphics g, Tool t, Rectangle r, Color c)
    {
        float u = DeviceDpi / 96f * 0.72f, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using var pen = new Pen(c, Math.Max(1.3f, 1.8f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        switch (t)
        {
            case Tool.NewDoc:
            {
                var pts = new[] { new PointF(cx - 6f * u, cy - 9f * u), new PointF(cx + 2f * u, cy - 9f * u), new PointF(cx + 7f * u, cy - 4f * u), new PointF(cx + 7f * u, cy + 9f * u), new PointF(cx - 6f * u, cy + 9f * u) };
                g.DrawPolygon(pen, pts);
                g.DrawLine(pen, cx + 2f * u, cy - 9f * u, cx + 2f * u, cy - 4f * u);
                g.DrawLine(pen, cx + 2f * u, cy - 4f * u, cx + 7f * u, cy - 4f * u);
                g.DrawLine(pen, cx - 2f * u, cy + 2f * u, cx + 3f * u, cy + 2f * u);
                g.DrawLine(pen, cx + 0.5f * u, cy - 0.5f * u, cx + 0.5f * u, cy + 4.5f * u);
                break;
            }
            case Tool.NewClass:
            {
                var pts = new[] { new PointF(cx - 9f * u, cy + 6f * u), new PointF(cx - 9f * u, cy - 7f * u), new PointF(cx - 3f * u, cy - 7f * u), new PointF(cx - 1f * u, cy - 4.5f * u), new PointF(cx + 8f * u, cy - 4.5f * u), new PointF(cx + 8f * u, cy + 6f * u) };
                g.DrawPolygon(pen, pts);
                g.DrawLine(pen, cx + 0.5f * u, cy + 0f * u, cx + 0.5f * u, cy + 5f * u);
                g.DrawLine(pen, cx - 2f * u, cy + 2.5f * u, cx + 3f * u, cy + 2.5f * u);
                break;
            }
            case Tool.NewTemplate: // 퍼즐 조각
            {
                g.DrawRectangle(pen, cx - 6f * u, cy - 4f * u, 12f * u, 12f * u);
                using var bg = new SolidBrush(P?.Dark == true ? Themes.Blend(P.Main, P.Fore, 0.14f) : Color.White);
                g.FillEllipse(bg, cx - 3f * u, cy - 9f * u, 6f * u, 6f * u); g.DrawEllipse(pen, cx - 3f * u, cy - 9f * u, 6f * u, 6f * u);
                g.FillEllipse(bg, cx + 3f * u, cy - 0.5f * u, 6f * u, 6f * u); g.DrawEllipse(pen, cx + 3f * u, cy - 0.5f * u, 6f * u, 6f * u);
                g.DrawLine(pen, cx - 3f * u, cy - 4f * u, cx + 3f * u, cy - 4f * u);
                break;
            }
            default:
                for (int k = -1; k <= 1; k++) g.DrawLine(pen, cx - 7f * u, cy + k * 5f * u, cx + 7f * u, cy + k * 5f * u);
                break;
        }
    }
}
