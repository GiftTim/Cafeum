using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 서재 화면 전용 색 묶음. 읽기 화면 테마(ReaderTheme) 하나에서 바탕 · 옆 막대 · 머리 막대 · 카드 · 강조색을 뽑아서,
/// 라이트 · 세피아 · 초록 · 다크 · 블랙 어느 테마든 같은 짜임새(머리 막대가 가장 진하고, 옆 막대, 본문 순)로 보이게 한다.
/// </summary>
internal sealed class LibraryPalette
{
    public required Color Main { get; init; }       // 책 목록 바탕
    public required Color Side { get; init; }       // 왼쪽 옆 막대
    public required Color Header { get; init; }     // 맨 위 머리 막대
    public required Color Fore { get; init; }
    public required Color Dim { get; init; }        // 작은 안내 글 · 머리글
    public required Color Line { get; init; }       // 구분선 · 테두리
    public required Color Card { get; init; }       // 버튼 · 입력칸 바탕
    public required Color CardHover { get; init; }
    public required Color Accent { get; init; }     // 강조(주 버튼 · 진행 막대)
    public required Color AccentFore { get; init; } // 강조색 위 글자
    public required Color AccentSoft { get; init; } // 선택된 줄 · 고른 정렬 버튼
    public bool Dark { get; init; }

    public static LibraryPalette From(ReaderTheme t)
    {
        Color main = t.Back, fore = t.Fore;
        Color accent = t.Link;
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        return new LibraryPalette
        {
            Main = main,
            Side = Themes.Blend(main, fore, t.Dark ? 0.045f : 0.035f),
            Header = Themes.Blend(main, fore, t.Dark ? 0.075f : 0.06f),
            Fore = fore,
            Dim = Themes.Blend(main, fore, 0.55f),
            Line = Themes.Blend(main, fore, 0.13f),
            Card = Themes.Blend(main, fore, 0.08f),
            CardHover = Themes.Blend(main, fore, 0.15f),
            Accent = accent,
            AccentFore = lum > 0.6 ? Color.Black : Color.White,
            AccentSoft = Themes.Blend(main, accent, t.Dark ? 0.28f : 0.18f),
            Dark = t.Dark,
        };
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
