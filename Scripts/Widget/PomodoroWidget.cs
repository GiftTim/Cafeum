using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace Cafeum;

/// <summary>
/// 떠 있는 뽀모도로 타이머: 머리 줄(이름 · 최소화 · 닫기) + 고리 + [시작/일시정지] [리셋] [목록 설정] [위치 고정] + 볼륨 막대.
/// 사이드바의 타이머 아이콘으로 켜고 끈다. 배경을 끌어서 옮길 수 있고(고정하면 안 움직인다), 이름을 누르면 다음 타이머로 넘어간다.
/// 최소화하면 작은 동그라미가 된다: 한 번 클릭 = 정지/재생, 두 번 클릭 = 원래 크기.
/// </summary>
internal sealed class PomodoroWidget : Control
{
    private LibraryPalette pal;
    private Rectangle nameRect, minRect, closeRect, playRect, resetRect, listRect, muteRect, sliderRect;
    private int hot = -1; // 0 이름 · 1 최소화 · 2 닫기 · 3 시작 · 4 리셋 · 5 목록 설정 · 6 고정 · 7 음소거 · 8 볼륨 막대
    private Point dragFrom;
    private bool dragging, sliding, dragMoved;
    private string banner = "";
    private readonly System.Windows.Forms.Timer bannerTimer = new() { Interval = 2500 };
    private double preMute = 1;

    public event Action? CloseRequested;
    public event Action? Moved;
    /// <summary>작은 동그라미 ↔ 전체 모양. 크기가 바뀌면 놓은 쪽(작업 공간)이 자리를 다시 잡는다.</summary>
    public event Action? ModeChanged;
    private readonly System.Windows.Forms.Timer clickTimer = new() { Interval = SystemInformation.DoubleClickTime };

    public PomodoroWidget(LibraryPalette p)
    {
        pal = p;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        Font = new Font(NovelStore.UiFontFamily, 9.5f);
        clickTimer.Tick += (_, _) => { clickTimer.Stop(); Pomodoro.Toggle(); };
        bannerTimer.Tick += (_, _) => { bannerTimer.Stop(); banner = ""; Invalidate(); };
        Pomodoro.Tick += OnCoreChanged;
        Pomodoro.StateChanged += OnCoreChanged;
        Pomodoro.Banner += ShowBanner;
        Disposed += (_, _) =>
        {
            Pomodoro.Tick -= OnCoreChanged;
            Pomodoro.StateChanged -= OnCoreChanged;
            Pomodoro.Banner -= ShowBanner;
            bannerTimer.Dispose(); clickTimer.Dispose();
        };
    }

    private static bool IsBar => Pomodoro.S.MiniStyle == "bar";

    public static Size SizeFor(int dpi) => Pomodoro.S.Mini
        ? (IsBar ? new((int)(296 * dpi / 96.0), (int)(38 * dpi / 96.0)) : new((int)(46 * dpi / 96.0), (int)(46 * dpi / 96.0)))
        : new((int)(244 * dpi / 96.0), (int)(300 * dpi / 96.0));

    private void SetMini(bool mini)
    {
        if (Pomodoro.S.Mini == mini) return;
        Pomodoro.S.Mini = mini;
        Pomodoro.Save();
        hot = -1;
        ModeChanged?.Invoke();
        Invalidate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, ref RECT rc, IntPtr rgn, uint flags);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void SetPalette(LibraryPalette p) { pal = p; Invalidate(); }

    private void OnCoreChanged() { if (IsHandleCreated) Invalidate(); }

    private void ShowBanner(string text)
    {
        banner = text;
        bannerTimer.Stop(); bannerTimer.Start();
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width > 2 && Height > 2)
        {
            using var path = new GraphicsPath();
            if (Pomodoro.S.Mini && IsBar) path.AddPath(LibraryPalette.Rounded(new RectangleF(0, 0, Width, Height), Height / 2f), false);
            else if (Pomodoro.S.Mini) path.AddEllipse(0, 0, Width, Height);
            else path.AddPath(LibraryPalette.Rounded(new RectangleF(0, 0, Width, Height), S(14)), false);
            Region?.Dispose();
            Region = new Region(path);
        }
        LayoutParts();
    }

    private void LayoutParts()
    {
        int w = Width;
        closeRect = Rectangle.Empty; // 닫기 단추는 없다(사이드바의 타이머 아이콘으로 끈다)
        nameRect = new Rectangle(S(12), S(6), Math.Max(S(20), w - S(24)), S(28));
        int bw = S(50), bh = S(40), gap = S(8), by = S(214);
        int x = (w - (bw * 4 + gap * 3)) / 2;
        playRect = new Rectangle(x, by, bw, bh);
        resetRect = new Rectangle(playRect.Right + gap, by, bw, bh);
        listRect = new Rectangle(resetRect.Right + gap, by, bw, bh);
        minRect = new Rectangle(listRect.Right + gap, by, bw, bh); // 네 번째 단추: 작게 줄이기
        muteRect = new Rectangle(S(16), S(270), S(22), S(20));
        sliderRect = new Rectangle(muteRect.Right + S(8), S(270), w - muteRect.Right - S(8) - S(20), S(20));
    }

    private Rectangle barTrack;
    private (int W, int H, int Dpi) barKey; // 막대 자리를 마지막으로 계산했을 때의 크기 · 배율(같으면 다시 재지 않는다)
    private long lastBarClick;

    private void LayoutBar()
    {
        var key = (Width, Height, DeviceDpi);
        if (key == barKey) return; // 마우스를 움직이거나 그릴 때마다 글꼴을 만들어 글자 폭을 재지 않도록
        barKey = key;
        int h = Height;
        barTrack = new Rectangle(S(14), h / 2 - S(4) / 2, Math.Max(S(20), Width - S(28)), S(4));
    }

    private int HitAt(Point p) => Pomodoro.S.Mini ? -1 :
        nameRect.Contains(p) ? 0 : minRect.Contains(p) ? 1 : closeRect.Contains(p) ? 2 :
        playRect.Contains(p) ? 3 : resetRect.Contains(p) ? 4 : listRect.Contains(p) ? 5 :
        muteRect.Contains(p) ? 6 : sliderRect.Contains(p) ? 7 : -1;

    private void SlideTo(int x)
    {
        int pad = S(7);
        double v = (x - sliderRect.X - pad) / (double)Math.Max(1, sliderRect.Width - pad * 2);
        Pomodoro.SetVolume(Math.Round(Math.Clamp(v, 0, 1) * 100) / 100);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (sliding) { SlideTo(e.X); return; }
        if (dragging && Parent != null)
        {
            var np = new Point(Left + e.X - dragFrom.X, Top + e.Y - dragFrom.Y);
            np.X = Math.Clamp(np.X, 0, Math.Max(0, Parent.ClientSize.Width - Width));
            np.Y = Math.Clamp(np.Y, 0, Math.Max(0, Parent.ClientSize.Height - Height));
            var old = Bounds;
            if (np == Location) return;
            dragMoved = true;
            // 옮길 때 윈도우가 옛 그림을 그대로 복사해 가면 뒤쪽 칸에 잔상이 남는다 → 복사 없이 옮기고, 지나간 자리를 뒤쪽 칸들까지 바로 다시 그리게 한다
            SetWindowPos(Handle, IntPtr.Zero, np.X, np.Y, Width, Height, 0x0004 | 0x0010 | 0x0200); // NOZORDER | NOACTIVATE | NOCOPYBITS
            var rc = new RECT { L = old.Left, T = old.Top, R = old.Right, B = old.Bottom };
            RedrawWindow(Parent.Handle, ref rc, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100); // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW
            return;
        }
        int h = HitAt(e.Location);
        if (h != hot) { hot = h; Invalidate(); }
        Cursor = h >= 0 ? Cursors.Hand : Cursors.SizeAll;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int h = HitAt(e.Location);
        BringToFront(); // 늘 가장 앞에
        if (h == 7) { sliding = true; Capture = true; SlideTo(e.X); return; }
        if ((h < 0 || (h == 0 && !Pomodoro.S.Mini)) && !(Pomodoro.S.Locked && Pomodoro.S.Mini && IsBar)) { dragging = true; dragMoved = false; dragFrom = e.Location; Capture = true; }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (sliding) { sliding = false; Capture = false; return; }
        if (dragging)
        {
            bool moved = dragMoved; // 위젯이 마우스를 따라 움직이므로 좌표 차이로는 알 수 없다
            dragging = false; Capture = false;
            if (moved) { Moved?.Invoke(); return; }
            if (!Pomodoro.S.Mini && HitAt(e.Location) != 0) return; // 머리 줄은 끌지 않고 눌렀을 때만 다음 타이머로
        }
        if (e.Button != MouseButtons.Left) return;
        if (Pomodoro.S.Mini)
        {
            if (clickTimer.Enabled) { clickTimer.Stop(); SetMini(false); } // 두 번 연속: 원래 크기로
            else clickTimer.Start();                                        // 한 번: 잠시 기다렸다가 정지/재생
            return;
        }
        switch (HitAt(e.Location))
        {
            case 0: Pomodoro.CycleMode(); break;
            case 1: SetMini(true); break;
            case 2: CloseRequested?.Invoke(); break;
            case 3: Pomodoro.Toggle(); break;
            case 4: Pomodoro.Reset(); break;
            case 5: OpenSettings(); break;
            case 6: ToggleMute(); break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Pomodoro.SetVolume(Pomodoro.S.Volume + (e.Delta > 0 ? 0.05 : -0.05));
        Invalidate();
    }

    private void ToggleMute()
    {
        if (Pomodoro.S.Volume > 0) { preMute = Pomodoro.S.Volume; Pomodoro.SetVolume(0); }
        else Pomodoro.SetVolume(preMute > 0 ? preMute : 1);
        Invalidate();
    }

    private void OpenSettings()
    {
        var keep = Pomodoro.Current;
        using var dlg = new PomodoroSettingsDialog(pal);
        dlg.ShowDialogOver(FindForm());
        Pomodoro.SettingsChanged(keep);
        if (Pomodoro.S.Mini) ModeChanged?.Invoke(); // 최소화 모양(동그라미 · 막대)을 바꿨을 수 있다
        Invalidate();
    }

    private static string Format(double ms)
    {
        int sec = (int)Math.Ceiling(Math.Max(0, ms) / 1000.0);
        return $"{sec / 60:00}:{sec % 60:00}";
    }

    private void DrawRing(Graphics g, RectangleF r, float stroke, Color accent, Color track)
    {
        using (var tp = new Pen(track, stroke)) g.DrawEllipse(tp, r);
        double frac = Pomodoro.TotalMs > 0 ? Math.Clamp(Pomodoro.RemainingMs / Pomodoro.TotalMs, 0, 1) : 1;
        if (frac * 360 < 0.5) return; // 끝나기 직전의 아주 짧은 호: GDI+가 0.1° 안팎보다 짧은 호를 둥근 끝으로 그리면 Out of memory를 던진다(보이지도 않는다)
        using var ap = new Pen(accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        bool right = Pomodoro.S.OverlayDrainDirection != "left";
        if (frac >= 0.9999) g.DrawEllipse(ap, r);
        else g.DrawArc(ap, r, -90, (float)((right ? -1 : 1) * frac * 360));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = pal;
        g.Clear(Parent?.BackColor ?? p.Main);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var def = Pomodoro.Current;
        Color accent = def.ColorValue;
        Color ink = p.Fore, dim = p.Dim;
        Color face = Themes.Blend(p.Main, p.Fore, p.Dark ? 0.06f : 0.0f);
        Color track = Themes.Blend(p.Main, p.Fore, p.Dark ? 0.16f : 0.11f);
        if (Pomodoro.S.Mini) { if (IsBar) PaintBar(g, p, accent, face, track); else PaintMini(g, p, accent, face, track); return; }

        using (var path = LibraryPalette.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), S(14)))
        {
            using var bg = new SolidBrush(face);
            using var pen = new Pen(Themes.Blend(p.Main, p.Fore, 0.22f));
            g.FillPath(bg, path);
            g.DrawPath(pen, path);
        }

        using var icon = new Font(MainForm.IconFontName(), 11f);
        const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        void Glyph(string s, Rectangle r, int idx) => TextRenderer.DrawText(g, s, icon, r, hot == idx ? ink : dim, mid);
        void Btn(string s, Rectangle r, int idx, bool on = false)
        {
            using var bp = LibraryPalette.Rounded(r, S(8));
            using var bb = new SolidBrush(hot == idx ? p.CardHover : p.Card);
            using var bl = new Pen(p.Line);
            g.FillPath(bb, bp); g.DrawPath(bl, bp);
            TextRenderer.DrawText(g, s, icon, r, on ? accent : ink, mid);
        }

        // 머리 줄: 이름(가운데, 대문자) · 최소화 · 닫기
        using (var bold = new Font(Font.FontFamily, 10.5f, FontStyle.Bold))
            TextRenderer.DrawText(g, (string.IsNullOrEmpty(banner) ? def.Name : banner).ToUpperInvariant(), bold, nameRect, hot == 0 && banner == "" ? accent : dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        using (var ul = new Pen(ink)) g.DrawLine(ul, S(12), S(38), Width - S(12), S(38));

        // 고리 + 시간
        int ringSize = S(160);
        var ring = new Rectangle((Width - ringSize) / 2, S(46), ringSize, ringSize);
        float stroke = S(16);
        DrawRing(g, new RectangleF(ring.X + stroke / 2, ring.Y + stroke / 2, ring.Width - stroke, ring.Height - stroke), stroke, accent, track);
        using (var big = new Font(Font.FontFamily, 27f, FontStyle.Bold))
            TextRenderer.DrawText(g, Format(Pomodoro.RemainingMs), big, ring, ink, mid);

        // 버튼 넷
        Btn(Pomodoro.State == PomoState.Running ? "" : "", playRect, 3);
        Btn("", resetRect, 4);
        Btn("", listRect, 5);
        Btn("", minRect, 1);
        { // 작게 줄이기 아이콘: 테두리 네모 + 안쪽 화살표 + 오른쪽 아래 채운 네모
            var m = minRect; int cx = m.X + m.Width / 2, cy = m.Y + m.Height / 2, q = S(5);
            using var ip = new Pen(ink, 0.75f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawRectangle(ip, cx - q, cy - q, q * 2 - S(2), q * 2 - S(2));
            g.DrawLine(ip, cx - q + S(2), cy - q + S(2), cx, cy);
            g.DrawLine(ip, cx, cy - S(2), cx, cy);
            g.DrawLine(ip, cx - S(2), cy, cx, cy);
            g.DrawRectangle(ip, cx + S(1), cy + S(1), S(5), S(4));
        }

        // 볼륨
        double v = Pomodoro.S.Volume;
        using (var sm = new Font(MainForm.IconFontName(), 10f))
            TextRenderer.DrawText(g, v <= 0 ? "" : "", sm, muteRect, hot == 6 ? ink : dim, mid);
        int pad = S(7), ty = sliderRect.Y + sliderRect.Height / 2, x0 = sliderRect.X + pad, x1 = sliderRect.Right - pad;
        int xv = x0 + (int)Math.Round((x1 - x0) * v);
        using (var tp = new Pen(track, S(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(tp, x0, ty, x1, ty);
        if (xv > x0) using (var fp = new Pen(p.Accent, S(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(fp, x0, ty, xv, ty);
        using (var kb = new SolidBrush(ink)) g.FillEllipse(kb, xv - S(7), ty - S(7), S(14), S(14));
    }

    /// <summary>막대바 모양 최소화: 진행 막대만 있다. 한 번 누르면 정지/재생, 두 번 누르면 원래 크기로(멈추면 회색).</summary>
    private void PaintBar(Graphics g, LibraryPalette p, Color accent, Color face, Color track)
    {
        LayoutBar();
        using (var fp = LibraryPalette.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), (Height - 1) / 2f))
        {
            using var fb = new SolidBrush(face); g.FillPath(fb, fp);
            using var bp = new Pen(Themes.Blend(p.Main, p.Fore, 0.18f)); g.DrawPath(bp, fp);
        }
        if (Pomodoro.State != PomoState.Running) accent = Themes.Blend(p.Main, p.Fore, 0.4f); // 멈추면 회색
        // 진행 막대: 남은 시간만큼 색이 채워진다(줄어드는 방향은 설정을 따른다)
        double frac = Pomodoro.TotalMs > 0 ? Math.Clamp(Pomodoro.RemainingMs / Pomodoro.TotalMs, 0, 1) : 1;
        using (var tp = LibraryPalette.Rounded(barTrack, barTrack.Height / 2f)) using (var tb = new SolidBrush(track)) g.FillPath(tb, tp);
        int fw = (int)Math.Round(barTrack.Width * frac);
        if (fw > 0)
        {
            bool right = Pomodoro.S.OverlayDrainDirection != "left";
            var fr = right ? new Rectangle(barTrack.Right - fw, barTrack.Y, fw, barTrack.Height) : new Rectangle(barTrack.X, barTrack.Y, fw, barTrack.Height);
            using var fpth = LibraryPalette.Rounded(fr, barTrack.Height / 2f); using var fbb = new SolidBrush(accent); g.FillPath(fbb, fpth);
        }
    }
    private void PaintMini(Graphics g, LibraryPalette p, Color accent, Color face, Color track)
    {
        if (Pomodoro.State != PomoState.Running) { accent = Themes.Blend(p.Main, p.Fore, 0.4f); face = Themes.Blend(p.Main, p.Fore, 0.08f); } // 멈추면 전체가 회색
        float st = S(5);
        var r = new RectangleF(st / 2 + 1.5f, st / 2 + 1.5f, Width - st - 3f, Height - st - 3f);
        using (var bgm = new SolidBrush(face)) g.FillEllipse(bgm, 1f, 1f, Width - 2f, Height - 2f);
        DrawRing(g, r, st, accent, track);
    }
}

/// <summary>타이머 설정 창: 타이머 목록(이름 · 시간 · 색 · 소리 목록), 알림 · 자동 시작 · 고리 방향.</summary>
internal sealed class PomodoroSettingsDialog : Form
{
    private readonly LibraryPalette p;
    private readonly PomoSettings s = Pomodoro.S;
    private readonly ListBox timers = new();
    private readonly TextBox name = new();
    private readonly NumericUpDown min = new(), sec = new();
    private readonly Button color = new();
    private readonly CheckBox soundOn = new();
    private readonly ListBox sounds = new();
    private readonly ComboBox mode = new(), drain = new();
    private bool loading;

    public PomodoroSettingsDialog(LibraryPalette pal)
    {
        p = pal;
        Text = "타이머 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font(NovelStore.UiFontFamily, 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(Dpi(640), Dpi(470));
        BackColor = p.Main; ForeColor = p.Fore;

        // 왼쪽: 타이머 목록
        Label L(string t, int x, int y) => new() { Text = t, Left = Dpi(x), Top = Dpi(y), AutoSize = true, ForeColor = p.Dim };
        Button B(string t, int x, int y, int w, EventHandler h)
        {
            var b = new Button { Text = t, Left = Dpi(x), Top = Dpi(y), Width = Dpi(w), Height = Dpi(28), FlatStyle = FlatStyle.Flat, BackColor = p.Card, ForeColor = p.Fore };
            b.FlatAppearance.BorderColor = p.Line;
            b.Click += h;
            return b;
        }
        Style(timers); Style(sounds);
        Controls.Add(L("타이머 목록", 14, 12));
        timers.SetBounds(Dpi(14), Dpi(34), Dpi(170), Dpi(190));
        timers.SelectedIndexChanged += (_, _) => LoadSelected();
        Controls.Add(timers);
        Controls.Add(B("＋ 추가", 14, 230, 82, (_, _) => { s.Timers.Add(new PomoTimerDef { Name = $"Timer {s.Timers.Count + 1}", Duration = 10, Color = "#2563eb" }); Refill(s.Timers.Count - 1); }));
        Controls.Add(B("삭제", 102, 230, 82, (_, _) =>
        {
            int i = timers.SelectedIndex;
            if (i < 0 || s.Timers.Count <= 1) return;
            s.Timers.RemoveAt(i); Refill(Math.Min(i, s.Timers.Count - 1));
        }));
        Controls.Add(B("▲", 14, 262, 82, (_, _) => MoveSel(-1)));
        Controls.Add(B("▼", 102, 262, 82, (_, _) => MoveSel(1)));

        // 오른쪽: 선택한 타이머
        int x = 210;
        Controls.Add(L("이름", x, 12));
        name.SetBounds(Dpi(x), Dpi(32), Dpi(410), Dpi(26)); StyleInput(name);
        name.TextChanged += (_, _) => { if (!loading && Cur() is { } d) { d.Name = name.Text; timers.Items[timers.SelectedIndex] = d.Name; } };
        Controls.Add(name);
        Controls.Add(L("시간", x, 68));
        min.SetBounds(Dpi(x), Dpi(88), Dpi(64), Dpi(26)); min.Maximum = 999; StyleInput(min);
        sec.SetBounds(Dpi(x + 96), Dpi(88), Dpi(56), Dpi(26)); sec.Maximum = 59; StyleInput(sec);
        Controls.Add(min); Controls.Add(L("분", x + 68, 92)); Controls.Add(sec); Controls.Add(L("초", x + 156, 92));
        min.ValueChanged += (_, _) => { if (!loading && Cur() is { } d) d.Duration = (int)min.Value; };
        sec.ValueChanged += (_, _) => { if (!loading && Cur() is { } d) d.Seconds = (int)sec.Value; };
        color.SetBounds(Dpi(x + 220), Dpi(87), Dpi(90), Dpi(28)); color.FlatStyle = FlatStyle.Flat; color.Text = "색상";
        color.FlatAppearance.BorderColor = p.Line;
        color.Click += (_, _) =>
        {
            if (Cur() is not { } d) return;
            using var dlg = new ColorDialog { Color = d.ColorValue, FullOpen = true };
            if (dlg.ShowDialogOver(this) != DialogResult.OK) return;
            d.Color = ColorTranslator.ToHtml(dlg.Color);
            PaintColor(d);
        };
        Controls.Add(color);

        soundOn.Text = "이 타이머가 도는 동안 소리 재생";
        soundOn.SetBounds(Dpi(x), Dpi(128), Dpi(300), Dpi(24)); soundOn.ForeColor = p.Fore;
        soundOn.CheckedChanged += (_, _) => { if (!loading && Cur() is { } d) d.SoundEnabled = soundOn.Checked; };
        Controls.Add(soundOn);
        sounds.SetBounds(Dpi(x), Dpi(156), Dpi(410), Dpi(104));
        Controls.Add(sounds);
        Controls.Add(B("파일 추가…", x, 266, 96, (_, _) => AddSounds()));
        Controls.Add(B("빼기", x + 102, 266, 70, (_, _) =>
        {
            if (Cur() is { } d && sounds.SelectedIndex >= 0) { d.SoundPaths.RemoveAt(sounds.SelectedIndex); sounds.Items.RemoveAt(sounds.SelectedIndex); }
        }));
        mode.DropDownStyle = ComboBoxStyle.DropDownList;
        mode.Items.AddRange(new object[] { "섞어서 재생", "차례대로 반복" });
        mode.SetBounds(Dpi(x + 270), Dpi(267), Dpi(140), Dpi(26)); StyleInput(mode);
        mode.SelectedIndexChanged += (_, _) => { if (!loading && Cur() is { } d) d.PlayMode = mode.SelectedIndex == 1 ? "loop" : "shuffle"; };
        Controls.Add(mode);

        // 아래: 공통 설정
        int y = 318;
        var line = new Panel { Left = Dpi(14), Top = Dpi(y - 10), Width = Dpi(612), Height = 1, BackColor = p.Line };
        Controls.Add(line);
        CheckBox C(string t, int cx, int cy, bool v, Action<bool> set)
        {
            var c = new CheckBox { Text = t, Left = Dpi(cx), Top = Dpi(cy), Width = Dpi(290), Height = Dpi(24), Checked = v, ForeColor = p.Fore };
            c.CheckedChanged += (_, _) => set(c.Checked);
            return c;
        }
        Controls.Add(C("끝나면 다음 타이머 자동 시작", 14, y, s.AutoAdvance, v => s.AutoAdvance = v));
        Controls.Add(C("끝나면 바탕화면 알림", 14, y + 30, s.ShowDesktopNotification, v => s.ShowDesktopNotification = v));
        Controls.Add(C("끝나면 알림음", 14, y + 60, s.PlaySound, v => s.PlaySound = v));
        Controls.Add(L("고리가 줄어드는 방향", 330, y + 4));
        drain.DropDownStyle = ComboBoxStyle.DropDownList;
        drain.Items.AddRange(new object[] { "시계 반대 방향 (오른쪽부터)", "시계 방향 (왼쪽부터)" });
        drain.SelectedIndex = s.OverlayDrainDirection == "left" ? 1 : 0;
        drain.SetBounds(Dpi(330), Dpi(y + 26), Dpi(290), Dpi(26)); StyleInput(drain);
        drain.SelectedIndexChanged += (_, _) => s.OverlayDrainDirection = drain.SelectedIndex == 1 ? "left" : "right";
        Controls.Add(drain);
        Controls.Add(L("최소화 모양", 330, y + 56));
        var miniStyle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        miniStyle.Items.AddRange(new object[] { "작은 동그라미", "막대바" });
        miniStyle.SelectedIndex = s.MiniStyle == "bar" ? 1 : 0;
        miniStyle.SetBounds(Dpi(330), Dpi(y + 78), Dpi(290), Dpi(26)); StyleInput(miniStyle);
        miniStyle.SelectedIndexChanged += (_, _) => s.MiniStyle = miniStyle.SelectedIndex == 1 ? "bar" : "circle";
        Controls.Add(miniStyle);

        var ok = B("확인", 540, 430, 86, (_, _) => { Save(); DialogResult = DialogResult.OK; });
        ok.BackColor = p.Accent; ok.ForeColor = p.AccentFore;
        ok.FlatAppearance.BorderSize = 0;
        Controls.Add(ok);
        AcceptButton = ok;
        Controls.Add(B("취소", 446, 430, 86, (_, _) => { Revert(); DialogResult = DialogResult.Cancel; }));
        FormClosing += (_, e) => { if (DialogResult == DialogResult.None) Revert(); };

        backup = Snapshot();
        Refill(Math.Clamp(s.CurrentMode, 0, s.Timers.Count - 1));
    }

    // 취소하면 열기 전 상태로 되돌리려고 JSON으로 복사해 둔다
    private readonly string backup = "";
    private string Snapshot() => System.Text.Json.JsonSerializer.Serialize(s);

    private void Revert()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<PomoSettings>(backup);
        if (old == null) return;
        s.Timers = old.Timers; s.AutoAdvance = old.AutoAdvance; s.ShowDesktopNotification = old.ShowDesktopNotification;
        s.PlaySound = old.PlaySound; s.OverlayDrainDirection = old.OverlayDrainDirection; s.MiniStyle = old.MiniStyle;
    }

    private void Save() => Pomodoro.Save();

    private int Dpi(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    private void Style(ListBox l) { l.BackColor = p.Card; l.ForeColor = p.Fore; l.BorderStyle = BorderStyle.FixedSingle; }

    private void StyleInput(Control c) { c.BackColor = p.Card; c.ForeColor = p.Fore; if (c is TextBox t) t.BorderStyle = BorderStyle.FixedSingle; }

    private PomoTimerDef? Cur() => timers.SelectedIndex >= 0 && timers.SelectedIndex < s.Timers.Count ? s.Timers[timers.SelectedIndex] : null;

    private void Refill(int select)
    {
        timers.Items.Clear();
        foreach (var t in s.Timers) timers.Items.Add(t.Name);
        timers.SelectedIndex = Math.Clamp(select, 0, s.Timers.Count - 1);
    }

    private void MoveSel(int dir)
    {
        int i = timers.SelectedIndex, j = i + dir;
        if (i < 0 || j < 0 || j >= s.Timers.Count) return;
        (s.Timers[i], s.Timers[j]) = (s.Timers[j], s.Timers[i]);
        Refill(j);
    }

    private void PaintColor(PomoTimerDef d)
    {
        color.BackColor = d.ColorValue;
        double lum = (0.299 * color.BackColor.R + 0.587 * color.BackColor.G + 0.114 * color.BackColor.B) / 255;
        color.ForeColor = lum > 0.6 ? Color.Black : Color.White;
    }

    private void LoadSelected()
    {
        if (Cur() is not { } d) return;
        loading = true;
        name.Text = d.Name; min.Value = d.Duration; sec.Value = d.Seconds;
        soundOn.Checked = d.SoundEnabled;
        mode.SelectedIndex = d.PlayMode == "loop" ? 1 : 0;
        sounds.Items.Clear();
        foreach (var path in d.SoundPaths) sounds.Items.Add(Path.GetFileName(path));
        PaintColor(d);
        loading = false;
    }

    private void AddSounds()
    {
        if (Cur() is not { } d) return;
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "재생할 소리 파일",
            Filter = "소리 파일|*.mp3;*.wav;*.wma;*.m4a;*.mp4;*.aac;*.flac;*.ogg|모든 파일|*.*",
        };
        if (dlg.ShowDialogOver(this) != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) { d.SoundPaths.Add(f); sounds.Items.Add(Path.GetFileName(f)); }
        if (d.SoundPaths.Count > 0 && !soundOn.Checked) soundOn.Checked = true;
    }
}
