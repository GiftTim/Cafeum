using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 여러 화면이 같이 쓰는 그림 아이콘. 글꼴 글리프로 못 내는 모양을 직접 그리며, 색과 배율(u = 화면 배율)은 부르는 쪽이 넘긴다.
/// </summary>
internal static class Icons
{
    /// <summary>타입라이터 아이콘: 위로 나온 종이(줄이 적힌 채워진 네모) · 롤러 막대 · 자판 본체(키 점들과 스페이스 막대).</summary>
    public static void Typewriter(Graphics g, Rectangle r, Color c, Color paperBg, float u)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        var cap = LineCap.Round;
        using var pen = new Pen(c, Math.Max(1.3f, 1.5f * u)) { StartCap = cap, EndCap = cap, LineJoin = LineJoin.Round };
        using var fill = new SolidBrush(c);
        // 종이: 채운 둥근 네모 + 안쪽에 배경색 글줄 둘
        var paper = new RectangleF(cx - 5f * u, cy - 10f * u, 10f * u, 6f * u);
        using (var pp = LibraryPalette.Rounded(paper, (int)(1.5f * u))) g.FillPath(fill, pp);
        using (var bg = new Pen(paperBg, Math.Max(1.1f, 1.2f * u)) { StartCap = cap, EndCap = cap })
        {
            g.DrawLine(bg, cx - 3f * u, cy - 8.3f * u, cx + 3f * u, cy - 8.3f * u);
            g.DrawLine(bg, cx - 3f * u, cy - 6f * u, cx + 0.5f * u, cy - 6f * u);
        }
        // 롤러: 가로 막대 + 양끝 손잡이
        g.DrawLine(pen, cx - 7f * u, cy - 2.6f * u, cx + 7f * u, cy - 2.6f * u);
        g.DrawLine(pen, cx - 9f * u, cy - 2.6f * u, cx - 7f * u, cy - 2.6f * u);
        g.DrawLine(pen, cx + 7f * u, cy - 2.6f * u, cx + 9f * u, cy - 2.6f * u);
        // 본체: 아래가 넓은 사다리꼴
        var body = new[] { new PointF(cx - 7.5f * u, cy - 0.6f * u), new PointF(cx + 7.5f * u, cy - 0.6f * u), new PointF(cx + 9f * u, cy + 9f * u), new PointF(cx - 9f * u, cy + 9f * u) };
        g.DrawPolygon(pen, body);
        // 키 점들과 스페이스 막대
        float d = 1.5f * u;
        foreach (var x in new[] { -4.5f, -1.5f, 1.5f, 4.5f }) g.FillEllipse(fill, cx + x * u - d / 2, cy + 2.2f * u, d, d);
        foreach (var x in new[] { -3f, 0f, 3f }) g.FillEllipse(fill, cx + x * u - d / 2, cy + 4.6f * u, d, d);
        g.DrawLine(pen, cx - 4f * u, cy + 7.3f * u, cx + 4f * u, cy + 7.3f * u);
    }

    /// <summary>위젯 아이콘: 얇은 선의 네모 셋(왼쪽 위 · 왼쪽 아래 · 오른쪽 아래), 오른쪽 위는 빈칸.</summary>
    public static void Widget(Graphics g, Rectangle r, Color c, float u)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        using var pen = new Pen(c, Math.Max(1f, 1.05f * u)) { LineJoin = LineJoin.Round }; // 얇은 선
        void Box(float x, float y) { using var bp = LibraryPalette.Rounded(new RectangleF(cx + x * u, cy + y * u, 7f * u, 7f * u), 1f * u); g.DrawPath(pen, bp); }
        Box(-8f, -8f);  // 왼쪽 위
        Box(-8f, 1f);   // 왼쪽 아래
        Box(1f, 1f);    // 오른쪽 아래 (오른쪽 위는 비워 둔다)
    }

    /// <summary>메모 아이콘: 얇은 선의 네모 안에 + (눌러서 새 메모를 만든다).</summary>
    public static void Memo(Graphics g, Rectangle r, Color c, float u)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, h = 7f * u, k = 3f * u, f = 3.5f * u;
        using var pen = new Pen(c, Math.Max(1f, 1.05f * u)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 왼쪽 위 모서리를 살짝 접은 네모(접은 자리에 작은 삼각 선)
        g.DrawLines(pen, new[] { new PointF(cx - h + f, cy - h), new PointF(cx + h, cy - h), new PointF(cx + h, cy + h), new PointF(cx - h, cy + h), new PointF(cx - h, cy - h + f), new PointF(cx - h + f, cy - h) });
        g.DrawLines(pen, new[] { new PointF(cx - h + f, cy - h), new PointF(cx - h + f, cy - h + f), new PointF(cx - h, cy - h + f) });
        Shapes.Plus(g, pen, cx, cy, k);
    }

    /// <summary>빠른 따옴표 아이콘: 왼쪽 위 “ 와 오른쪽 아래 ” 를 ┐ └ 모양 선으로 이은 모양.</summary>
    public static void Quote(Graphics g, Rectangle r, Color c, float u)
    {
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        var cap = LineCap.Round;
        using var pen = new Pen(c, Math.Max(1.3f, 1.5f * u)) { StartCap = cap, EndCap = cap, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[] { new PointF(cx + 0.5f * u, cy - 5f * u), new PointF(cx + 8f * u, cy - 5f * u), new PointF(cx + 8f * u, cy - 1f * u) });   // ┐
        g.DrawLines(pen, new[] { new PointF(cx - 8f * u, cy + 1f * u), new PointF(cx - 8f * u, cy + 5f * u), new PointF(cx - 0.5f * u, cy + 5f * u) });   // └
        using var f = new Font("Georgia", 13f * u * 0.75f, FontStyle.Bold, GraphicsUnit.Point);
        var fl = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, "“", f, new Rectangle((int)(cx - 9f * u), (int)(cy - 11f * u), (int)(9f * u), (int)(12f * u)), c, fl);
        TextRenderer.DrawText(g, "”", f, new Rectangle((int)(cx + 0f * u), (int)(cy - 1f * u), (int)(9f * u), (int)(12f * u)), c, fl);
    }

    /// <summary>체크 표시(✓): 사각 영역 r 안에 끝이 둥근 꺾은선으로.</summary>
    public static void Check(Graphics g, Rectangle r, Color c)
    {
        using var pen = new Pen(c, Math.Max(1.6f, r.Width / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(r.X + r.Width * 0.12f, r.Y + r.Height * 0.55f),
            new PointF(r.X + r.Width * 0.40f, r.Y + r.Height * 0.82f),
            new PointF(r.X + r.Width * 0.88f, r.Y + r.Height * 0.20f),
        });
    }
}
