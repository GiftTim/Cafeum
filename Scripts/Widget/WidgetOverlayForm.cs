using System.Drawing.Drawing2D;

namespace Cafeum;

/// <summary>
/// 위젯 모음 덮개: 에피소드 창 위에 얹히는 테두리 없는 창(주 창의 소유 창). 칸 크기를 바꾸지 않고 문서영역 위를 덮으며,
/// 머리의 [×] 왼쪽 투명도 막대로 뒤의 글이 비쳐 보이는 정도를 정한다(창 전체의 Opacity).
/// 자리와 크기는 NovelWorkspace가 주 창을 따라 맞춘다.
/// </summary>
internal sealed class WidgetOverlayForm : Form
{
    public PaneFrame Frame { get; }
    private readonly OpacitySlider slider;

    /// <summary>[×]를 눌렀다(닫기는 받는 쪽이 한다).</summary>
    public event Action? CloseClicked;
    /// <summary>투명도를 바꿨다(20~100, 퍼센트).</summary>
    public event Action<int>? OpacityChanged;

    public WidgetOverlayForm(WidgetPanel panel, int percent)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Font = new Font(NovelStore.UiFontFamily, 10f);
        Frame = new PaneFrame("", "위젯 모음") { Dock = DockStyle.Fill };
        Frame.Glyph = WorkspaceRail.Catalog.First(c => c.Key == "widget").Glyph;
        Frame.Body.Controls.Add(panel);
        panel.Visible = true;
        Frame.CloseRequested += () => CloseClicked?.Invoke();
        Frame.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 1, Name = "edge" }); // 문서영역과 구분하는 오른쪽 선
        Controls.Add(Frame);

        slider = new OpacitySlider { Value = Math.Clamp(percent, 20, 100) };
        Frame.Controls.Add(slider);
        slider.BringToFront();
        slider.ValueChanged += v => { Opacity = v / 100.0; OpacityChanged?.Invoke(v); };
        Opacity = slider.Value / 100.0;
        Frame.SizeChanged += (_, _) => PlaceSlider();
        Shown += (_, _) => PlaceSlider();
    }

    /// <summary>머리 줄의 [×] 왼쪽(맨 오른쪽 · ⋯는 없다)에 놓는다.</summary>
    private void PlaceSlider()
    {
        int h = (int)Math.Round(34 * DeviceDpi / 96.0), w = (int)Math.Round(92 * DeviceDpi / 96.0);
        int right = Frame.Width - (int)Math.Round(36 * DeviceDpi / 96.0);
        slider.SetBounds(right - w, (h - (int)Math.Round(24 * DeviceDpi / 96.0)) / 2, w, (int)Math.Round(24 * DeviceDpi / 96.0));
    }

    public void ApplyTheme(LibraryPalette p)
    {
        BackColor = p.Main;
        Frame.SetPalette(p);
        if (Frame.Controls.Find("edge", false).FirstOrDefault() is Control edge) edge.BackColor = p.Line;
        slider.SetPalette(p);
    }

    /// <summary>눌러도 주 창이 비활성 표시(제목줄 흐림 등)로 바뀌지 않게 하고 싶지만, 안의 검색칸에 글을 칠 수 있게 활성화는 허용한다.</summary>
    protected override bool ShowWithoutActivation => true;
}

/// <summary>투명도 막대: 가는 줄 위에 동그란 손잡이. 왼쪽 끝 20% · 오른쪽 끝 100%. 끌거나 눌러서 정한다.</summary>
internal sealed class OpacitySlider : Control
{
    private LibraryPalette? p;
    private int value = 100;
    private bool dragging;
    private readonly ToolTip tip = new();
    public event Action<int>? ValueChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value { get => value; set { value = Math.Clamp(value, 20, 100); if (this.value == value) return; this.value = value; Invalidate(); } }

    public OpacitySlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Cursor = Cursors.Hand;
        Disposed += (_, _) => tip.Dispose();
    }

    public void SetPalette(LibraryPalette pal) { p = pal; Invalidate(); }
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private int PadX => S(10);

    private int FromX(int x) => Math.Clamp(20 + (int)Math.Round((x - PadX) * 80.0 / Math.Max(1, Width - PadX * 2)), 20, 100);
    private float ToX(int v) => PadX + (v - 20) / 80f * (Width - PadX * 2);

    private void Set(int x)
    {
        int v = FromX(x);
        if (v == value) return;
        value = v;
        ValueChanged?.Invoke(v);
        tip.SetToolTip(this, $"투명도 {v}%");
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { dragging = true; Capture = true; Set(e.X); } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) Set(e.X); else tip.SetToolTip(this, $"투명도 {value}%"); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cy = Height / 2f, x = ToX(value);
        using (var track = new Pen(Themes.Blend(p.Main, p.Fore, 0.18f), Math.Max(2f, S(3)))) { track.StartCap = track.EndCap = LineCap.Round; g.DrawLine(track, PadX, cy, Width - PadX, cy); }
        using (var fill = new Pen(p.Accent, Math.Max(2f, S(3)))) { fill.StartCap = fill.EndCap = LineCap.Round; g.DrawLine(fill, PadX, cy, x, cy); }
        float r = S(7);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x - r, cy - r, r * 2, r * 2);
        using (var pen = new Pen(p.Accent, Math.Max(1.5f, S(2)))) g.DrawEllipse(pen, x - r, cy - r, r * 2, r * 2);
    }
}
