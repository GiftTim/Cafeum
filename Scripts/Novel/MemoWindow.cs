using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 메모 목록 창: 따로 뜨는 창에 [새 메모 · 모두 선택 · 삭제] 도구 줄 + 오른쪽 위 검색칸 + 메모 목록(왼쪽 분류 · 가운데 표 · 오른쪽 미리보기).
/// 소설 메인 창의 [메모] 탭과 같은 메모들이다(같은 파일). 작품마다 하나만 열린다.
/// </summary>
internal sealed class MemoWindow : Form
{
    private static readonly Dictionary<string, MemoWindow> Open = new(StringComparer.OrdinalIgnoreCase);

    private readonly MemoView view = new() { Dock = DockStyle.Fill };
    private readonly MemoActionBar bar = new();
    private readonly Panel head = new() { Dock = DockStyle.Top };
    private readonly Panel searchBox = new();
    private readonly TextBox search = new();
    private LibraryPalette p;

    /// <summary>이 작품의 메모 목록 창을 열고(이미 있으면 앞으로 가져오고) 돌려준다.</summary>
    public static void ShowFor(string novelDir, Form? owner, LibraryPalette pal)
    {
        if (Open.TryGetValue(novelDir, out var w) && !w.IsDisposed) { w.SetPalette(pal); if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal; w.Activate(); return; }
        w = new MemoWindow(novelDir, pal);
        Open[novelDir] = w;
        w.FormClosed += (_, _) => Open.Remove(novelDir);
        if (owner != null) w.Show(owner); else w.Show();
    }

    private MemoWindow(string novelDir, LibraryPalette pal)
    {
        p = pal;
        Text = "메모 목록";
        Font = new Font(NovelStore.UiFontFamily, 10f);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        double k = DeviceDpi / 96.0;
        ClientSize = new Size((int)(1180 * k), (int)(470 * k));
        MinimumSize = new Size((int)(720 * k), (int)(400 * k));

        head.Height = (int)(64 * k);
        bar.Size = new Size(bar.FitWidth(), (int)(36 * k));
        bar.Location = new Point((int)(18 * k), (int)(14 * k));
        searchBox.Height = (int)(40 * k);
        searchBox.Width = (int)(330 * k);
        searchBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        searchBox.Location = new Point(head.Width - searchBox.Width - (int)(18 * k), (int)(12 * k));
        search.BorderStyle = BorderStyle.None;
        search.PlaceholderText = "검색어를 입력하세요";
        search.Font = new Font(NovelStore.UiFontFamily, 10.5f);
        search.SetBounds((int)(38 * k), (int)(11 * k), searchBox.Width - (int)(38 * k) - (int)(14 * k), search.PreferredHeight);
        searchBox.Controls.Add(search);
        searchBox.Paint += PaintSearch;
        searchBox.MouseDown += (_, _) => search.Focus();
        search.GotFocus += (_, _) => searchBox.Invalidate();
        search.LostFocus += (_, _) => searchBox.Invalidate();
        search.TextChanged += (_, _) => view.SetQuery(search.Text.Trim());
        head.Controls.Add(bar);
        head.Controls.Add(searchBox);
        head.Resize += (_, _) => searchBox.Left = head.Width - searchBox.Width - (int)(18 * k);

        bar.NewClicked += view.NewMemo;
        bar.SelectAllClicked += view.SelectAll;
        bar.DeleteClicked += view.DeleteSelected;

        Controls.Add(view);
        Controls.Add(head);
        view.SetNovel(novelDir);
        SetPalette(pal);
    }

    private void SetPalette(LibraryPalette pal)
    {
        p = pal;
        BackColor = head.BackColor = searchBox.BackColor = pal.Main; // 네모 바탕은 주 바탕색, 둥근 칸은 Paint에서 채운다
        search.BackColor = pal.Card;
        search.ForeColor = pal.Fore;
        view.SetPalette(pal);
        bar.SetPalette(pal);
        searchBox.Invalidate();
    }

    private void PaintSearch(object? s, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, searchBox.Width - 1.5f, searchBox.Height - 1.5f);
        using (var path = LibraryPalette.Rounded(r, searchBox.Height / 2f))
        using (var fill = new SolidBrush(p.Card))
        using (var pen = new Pen(search.Focused ? p.Accent : p.Line, search.Focused ? 1.6f : 1f)) { g.FillPath(fill, path); g.DrawPath(pen, path); }
        using var icon = new Font(MainForm.IconFontName(), 11f);
        TextRenderer.DrawText(g, "", icon, new Rectangle((int)(10 * DeviceDpi / 96.0), 0, (int)(26 * DeviceDpi / 96.0), searchBox.Height), p.Dim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
