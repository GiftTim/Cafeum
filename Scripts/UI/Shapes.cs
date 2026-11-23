using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>꺾쇠(V 모양)가 가리키는 방향.</summary>
internal enum Arrow { Down, Up, Left, Right }

/// <summary>
/// 여러 화면이 같이 쓰는 도형 그리기(꺾쇠 · × · + · 둥근 네모). 도형의 점 자리만 정하고, 펜(색 · 굵기 · 끝 모양)은 부르는 쪽이 만들어 넘긴다 —
/// 그래서 화면마다 다른 굵기와 색을 그대로 쓰면서, 같은 점 계산을 파일마다 복사해 두지 않아도 된다.
/// </summary>
internal static class Shapes
{
    /// <summary>
    /// 꺾쇠 한 개: 가운데 (cx, cy)를 중심으로 한 V 모양 선 두 개. halfLong은 끝에서 끝까지 절반(펼친 쪽), halfShort는 꼭짓점까지의 절반 깊이.
    /// Down은 ⌄, Up은 ⌃, Left는 ‹, Right는 ›.
    /// </summary>
    public static void Chevron(Graphics g, Pen pen, float cx, float cy, float halfLong, float halfShort, Arrow dir)
    {
        float L = halfLong, S = halfShort;
        PointF[] pts = dir switch
        {
            Arrow.Down => new[] { new PointF(cx - L, cy - S), new PointF(cx, cy + S), new PointF(cx + L, cy - S) },
            Arrow.Up => new[] { new PointF(cx - L, cy + S), new PointF(cx, cy - S), new PointF(cx + L, cy + S) },
            Arrow.Left => new[] { new PointF(cx + S, cy - L), new PointF(cx - S, cy), new PointF(cx + S, cy + L) },
            _ => new[] { new PointF(cx - S, cy - L), new PointF(cx + S, cy), new PointF(cx - S, cy + L) },
        };
        g.DrawLines(pen, pts);
    }

    /// <summary>× 모양: (cx, cy)를 중심으로 한 변 절반이 half인 정사각형의 두 대각선.</summary>
    public static void Cross(Graphics g, Pen pen, float cx, float cy, float half)
    {
        g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
        g.DrawLine(pen, cx - half, cy + half, cx + half, cy - half);
    }

    /// <summary>+ 모양: (cx, cy)를 중심으로 가로 · 세로 선 절반 길이가 half.</summary>
    public static void Plus(Graphics g, Pen pen, float cx, float cy, float half)
    {
        g.DrawLine(pen, cx - half, cy, cx + half, cy);
        g.DrawLine(pen, cx, cy - half, cx, cy + half);
    }

    /// <summary>둥근 네모 칠하기(line을 주면 테두리도). 오른쪽 · 아래 끝이 한 칸 안쪽에 오게 그린다.</summary>
    public static void FillRounded(Graphics g, Rectangle r, int radius, Color fill, Color? line = null)
    {
        using var path = LibraryPalette.Rounded(new RectangleF(r.X, r.Y, r.Width - 1, r.Height - 1), radius);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (line != null) using (var pen = new Pen(line.Value)) g.DrawPath(pen, path);
    }
}
