using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>
/// 타입라이터의 하이라이트 줄(띠)과 "쓰는 문단 밖 흐리게"를 그리는, 클릭이 그대로 통과되는 반투명 창.
/// 글 상자(RichEdit) 위에 직접 덧칠하면 상자가 글자를 다시 칠할 때마다 덧칠이 지워져 띠가 끊기므로, 상자와 따로 있는 창에 그려서 서로 간섭하지 않게 한다.
/// 글 칸(보이는 영역) 크기의 비트맵에 [흐림(쓰는 문단은 뚫림) + 띠]를 그려 UpdateLayeredWindow로 얹는다. 마우스 · 키 입력은 모두 아래 창으로 간다.
/// </summary>
internal sealed class TypewriterOverlay : Form
{
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    /// <summary>쓰는 문단 밖을 덮는 흐림의 진하기(0~255).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int DimAlpha { get; set; } = 145;
    /// <summary>커서 줄 띠의 진하기(0~255).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int BandAlpha { get; set; } = 34;
    /// <summary>쓰는 문단 밖을 흐리게 할지(끄면 커서 줄 띠만 그린다).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Dim { get; set; } = true;

    public TypewriterOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = false;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; } // 마우스는 아래 창으로
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    /// <summary>화면 영역 vp에 맞춰 그린다. band · para는 vp 기준 좌표(para = 또렷하게 둘 문단, band = 커서 줄).</summary>
    public void Render(Form owner, Rectangle vp, Rectangle band, Rectangle para, Color back, Color fore)
    {
        if (vp.Width <= 0 || vp.Height <= 0) return;
        if (!Visible) Show(owner);
        using var bmp = new Bitmap(vp.Width, vp.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            if (Dim)
            {
                g.Clear(Color.FromArgb(DimAlpha, back));
                // 쓰는 문단은 뚫어서 아래 글이 그대로 보이게
                g.CompositingMode = CompositingMode.SourceCopy;
                var hole = Rectangle.Intersect(para, new Rectangle(0, 0, vp.Width, vp.Height));
                if (hole.Width > 0 && hole.Height > 0) using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, hole);
            }
            else g.Clear(Color.Transparent); // 흐림 없이 커서 줄 띠만
            // 커서 줄 띠
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int r = Math.Max(2, Math.Min(8, band.Height / 3));
            using var path = new GraphicsPath();
            path.AddArc(band.X, band.Y, r * 2, r * 2, 180, 90);
            path.AddArc(band.Right - r * 2, band.Y, r * 2, r * 2, 270, 90);
            path.AddArc(band.Right - r * 2, band.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(band.X, band.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var bb = new SolidBrush(Color.FromArgb(BandAlpha, fore));
            g.FillPath(bb, path);
        }
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hBmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { W = vp.Width, H = vp.Height };
            var dst = new POINT { X = vp.X, Y = vp.Y };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER · 픽셀마다 알파
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2); // ULW_ALPHA
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
