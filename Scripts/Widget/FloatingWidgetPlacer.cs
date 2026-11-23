namespace Cafeum;

/// <summary>
/// 글 상자 위에 떠 있는 위젯(시계 · 특수문자 · 맞춤법 검사기)이 같이 쓰는 자리 잡기.
/// 놓은 자리는 창 오른쪽 끝에서 얼마나 떨어졌는지(RightGap)와 위쪽(Top)으로 설정에 기억하고, 창 크기가 바뀌어도 같은 자리에 있게 한다.
/// 처음에는 글 칸 오른쪽 위쪽에 놓는다. 접거나 펼쳐 크기가 바뀌면 위쪽 · 오른쪽 끝은 그대로 둔다.
/// </summary>
internal sealed class FloatingWidgetPlacer
{
    private readonly IWriterHost host;
    private readonly Control widget;
    private readonly Func<Size> desiredSize;
    private readonly Action<Color, Color, Color> setColors;
    private readonly Func<(int RightGap, int Top)> read;
    private readonly Action<int, int> write;
    private readonly Func<int>? topExtra;
    private readonly bool skipResizeWhenSame, placeAfterResize;

    /// <param name="read">설정에 적어 둔 자리(Top이 0보다 작으면 아직 놓은 적 없음).</param>
    /// <param name="write">자리를 설정에 적는다(저장은 이 클래스가 한다).</param>
    /// <param name="topExtra">처음 자리의 위쪽에 더할 값(다른 위젯 아래로 겹치지 않게). 없으면 0.</param>
    /// <param name="skipResizeWhenSame">크기가 그대로면 아무것도 하지 않는다(맞춤법 검사기는 목록이 바뀔 때마다 부른다).</param>
    /// <param name="placeAfterResize">크기를 바꾼 뒤 보이는 중이면 자리를 다시 잡는다.</param>
    public FloatingWidgetPlacer(IWriterHost host, Control widget, Func<Size> desiredSize, Action<Color, Color, Color> setColors,
        Func<(int RightGap, int Top)> read, Action<int, int> write, Func<int>? topExtra = null,
        bool skipResizeWhenSame = false, bool placeAfterResize = true)
    {
        this.host = host;
        this.widget = widget;
        this.desiredSize = desiredSize;
        this.setColors = setColors;
        this.read = read;
        this.write = write;
        this.topExtra = topExtra;
        this.skipResizeWhenSame = skipResizeWhenSame;
        this.placeAfterResize = placeAfterResize;
    }

    /// <summary>글 칸 크기 · 보임이 바뀔 때 자리를 맞추고, 창이 닫히면 위젯을 부모에서 뗀다.</summary>
    /// <param name="wanted">지금 켜 두었는가.</param>
    /// <param name="sync">켜 둔 위젯을 보이게 맞추는 일(Sync를 부르는 쪽).</param>
    public void Hook(Func<bool> wanted, Action sync)
    {
        host.EditorHost.SizeChanged += (_, _) => { if (widget.Visible) Place(); };
        host.EditorHost.VisibleChanged += (_, _) => { if (wanted()) sync(); };
        host.FormClosed += (_, _) => { widget.Parent?.Controls.Remove(widget); };
    }

    /// <summary>보일지 말지를 맞춘다. 보이게 되면 자리를 잡고 맨 앞으로, 보이는 동안은 글 상자 색을 따른다.</summary>
    /// <param name="changed">보임이 바뀐 직후(보이게 됐으면 true).</param>
    public void Sync(bool show, Action<bool>? changed = null)
    {
        if (widget.Visible != show)
        {
            if (show) { Place(); widget.BringToFront(); }
            widget.Visible = show;
            changed?.Invoke(show);
        }
        if (show) setColors(host.Editor.BackColor, host.Editor.ForeColor, host.Theme.Link);
    }

    /// <summary>접거나 펼치거나 칸이 늘어 크기가 바뀌었을 때(위쪽 · 오른쪽 끝은 그대로).</summary>
    public void Resize()
    {
        var size = desiredSize();
        if (skipResizeWhenSame && widget.Size == size) return;
        widget.Bounds = new Rectangle(widget.Left + widget.Width - size.Width, widget.Top, size.Width, size.Height);
        if (read().Top >= 0) SavePos(); // 옮겨 둔 자리가 있으면 접고 펴도 오른쪽 끝 · 위쪽 기준 자리를 이어 간다
        if (placeAfterResize && widget.Visible) Place();
    }

    /// <summary>위젯을 놓은 자리를 창 오른쪽 끝 · 위쪽 기준으로 설정에 적어 둔다.</summary>
    public void SavePos()
    {
        var parent = host.FloatingParent;
        write(Math.Max(0, parent.ClientSize.Width - widget.Right), Math.Max(0, widget.Top));
        host.Settings.Save();
    }

    public void Place()
    {
        var parent = host.FloatingParent;
        if (widget.Parent != parent)
        {
            Point? screen = widget.Parent != null ? widget.Parent.PointToScreen(widget.Location) : null;
            widget.Parent?.Controls.Remove(widget);
            parent.Controls.Add(widget);
            if (screen is Point sp) widget.Location = parent.PointToClient(sp);
        }
        var size = desiredSize();
        if (parent.ClientSize.Width < size.Width || parent.ClientSize.Height < size.Height || !parent.IsHandleCreated)
        {
            widget.Bounds = new Rectangle(new Point(Math.Max(0, parent.ClientSize.Width - size.Width - host.Dpi(24)), host.Dpi(70)), size);
            return;
        }
        var saved = read();
        if (saved.Top >= 0)
        {
            // 마지막으로 놓은 자리(창 오른쪽 끝 · 위쪽 기준)에 그대로
            int x = Math.Clamp(parent.ClientSize.Width - size.Width - saved.RightGap, 0, Math.Max(0, parent.ClientSize.Width - size.Width));
            int y = Math.Clamp(saved.Top, 0, Math.Max(0, parent.ClientSize.Height - size.Height));
            widget.Bounds = new Rectangle(x, y, size.Width, size.Height);
            widget.BringToFront();
            return;
        }
        // 처음 자리: 글 칸 오른쪽 위쪽
        var editorHost = host.EditorHost;
        int right = editorHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - host.Dpi(24);
        int topY = host.Dpi(70) + (topExtra?.Invoke() ?? 0);
        var at = parent.PointToClient(editorHost.PointToScreen(new Point(Math.Max(0, right - size.Width), topY)));
        widget.Bounds = new Rectangle(at, size);
        widget.BringToFront();
        SavePos(); // 처음 자리도 기억해서, 트리를 열고 닫거나 껐다 켜도 같은 자리에 켜지게
    }
}
