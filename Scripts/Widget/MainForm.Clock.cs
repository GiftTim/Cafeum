namespace Cafeum;

/// <summary>글쓰기 화면의 시계 위젯(ClockWidget, 플립 시계): [위젯 모음]에서 켜고 끈다. 시계 위치(도시 · 시간대)는 위젯 모음의 톱니바퀴에서 정한다.</summary>
public partial class MainForm
{
    private ClockWidget? clockWidget;
    private bool clockShown;     // 켜져 있는가(창을 열 때마다 꺼진 채로 시작한다)

    /// <summary>위젯 모음의 시계 카드 · 사이드바 시계 아이콘: 위젯을 켜고 끈다.</summary>
    internal void ToggleClockWidget() { clockShown = !clockShown; SyncClockWidget(); }
    internal bool ClockWidgetOn => clockWidget is { Visible: true };

    private void InitClockWidget()
    {
        clockWidget = new ClockWidget { Visible = false };
        editorHost.Controls.Add(clockWidget);
        clockWidget.Moved += SaveClockPos;
        clockWidget.ExpandedChanged += () => ResizeClockWidget();
        editorHost.SizeChanged += (_, _) => { if (clockWidget.Visible) PlaceClockWidget(); };
        editorHost.VisibleChanged += (_, _) => { if (clockShown) SyncClockWidget(); };
        FormClosed += (_, _) => { clockWidget.Parent?.Controls.Remove(clockWidget); };
        ApplyClockPlace();
    }

    /// <summary>설정의 시계 위치(도시 · 시간대)를 위젯에 반영한다.</summary>
    internal void ApplyClockPlace() => clockWidget?.SetZone(settings.ClockZoneId, settings.ClockCity);

    private void SyncClockWidget()
    {
        if (clockWidget == null) return;
        bool show = directEdit && shownKind == ContentKind.Txt && editorHost.Visible && clockShown;
        if (clockWidget.Visible != show) { if (show) { PlaceClockWidget(); clockWidget.BringToFront(); } clockWidget.Visible = show; }
        if (show) clockWidget.SetColors(mainEditor.BackColor, mainEditor.ForeColor, theme.Link);
    }

    /// <summary>접거나 펼치면 크기를 바꾼다(위쪽 · 오른쪽 끝은 그대로).</summary>
    private void ResizeClockWidget()
    {
        if (clockWidget == null) return;
        var size = clockWidget.DesiredSize();
        clockWidget.Bounds = new Rectangle(clockWidget.Left + clockWidget.Width - size.Width, clockWidget.Top, size.Width, size.Height);
        if (settings.ClockTop >= 0) SaveClockPos(); // 옮겨 둔 자리가 있으면 접고 펴도 오른쪽 끝 · 위쪽 기준 자리를 이어 간다
        if (clockWidget.Visible) PlaceClockWidget();
    }

    private Control ClockHost => TopLevelControl ?? (Control)editorHost;

    /// <summary>위젯을 놓은 자리를 창 오른쪽 끝 · 위쪽 기준으로 설정에 적어 둔다.</summary>
    private void SaveClockPos()
    {
        if (clockWidget == null) return;
        var host = ClockHost;
        settings.ClockRightGap = Math.Max(0, host.ClientSize.Width - clockWidget.Right);
        settings.ClockTop = Math.Max(0, clockWidget.Top);
        settings.Save();
    }

    private void PlaceClockWidget()
    {
        if (clockWidget == null) return;
        var host = ClockHost;
        if (clockWidget.Parent != host)
        {
            Point? screen = clockWidget.Parent != null ? clockWidget.Parent.PointToScreen(clockWidget.Location) : null;
            clockWidget.Parent?.Controls.Remove(clockWidget);
            host.Controls.Add(clockWidget);
            if (screen is Point sp) clockWidget.Location = host.PointToClient(sp);
        }
        var size = clockWidget.DesiredSize();
        if (host.ClientSize.Width < size.Width || host.ClientSize.Height < size.Height || !host.IsHandleCreated)
        {
            clockWidget.Bounds = new Rectangle(new Point(Math.Max(0, host.ClientSize.Width - size.Width - LogicalToDeviceUnits(24)), LogicalToDeviceUnits(70)), size);
            return;
        }
        if (settings.ClockTop >= 0)
        {
            // 마지막으로 놓은 자리(창 오른쪽 끝 · 위쪽 기준)에 그대로
            int x = Math.Clamp(host.ClientSize.Width - size.Width - settings.ClockRightGap, 0, Math.Max(0, host.ClientSize.Width - size.Width));
            int y = Math.Clamp(settings.ClockTop, 0, Math.Max(0, host.ClientSize.Height - size.Height));
            clockWidget.Bounds = new Rectangle(x, y, size.Width, size.Height);
            clockWidget.BringToFront();
            return;
        }
        // 처음 자리: 글 칸 오른쪽 위쪽(특수문자 위젯이 있으면 그 아래로 겹치지 않게)
        int right = editorHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - LogicalToDeviceUnits(24);
        int topY = LogicalToDeviceUnits(70) + (specialWidget is { Visible: true } ? specialWidget.Height + LogicalToDeviceUnits(12) : 0);
        var at = host.PointToClient(editorHost.PointToScreen(new Point(Math.Max(0, right - size.Width), topY)));
        clockWidget.Bounds = new Rectangle(at, size);
        clockWidget.BringToFront();
        SaveClockPos(); // 처음 자리도 기억해서, 트리를 열고 닫거나 껐다 켜도 같은 자리에 켜지게
    }
}
