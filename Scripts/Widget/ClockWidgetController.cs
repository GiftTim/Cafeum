namespace Cafeum;

/// <summary>
/// 에피소드 창의 시계 위젯(ClockWidget, 플립 시계)을 붙이고 다룬다: [위젯 모음]에서 켜고 끈다. 시계 위치(도시 · 시간대)는 위젯 모음의 톱니바퀴에서 정한다.
/// 글쓰기 화면과의 연결은 IWriterHost 하나로만 한다.
/// </summary>
internal sealed class ClockWidgetController
{
    private readonly IWriterHost host;
    private readonly ClockWidget widget;
    private readonly FloatingWidgetPlacer placer;
    private bool shown;     // 켜져 있는가(창을 열 때마다 꺼진 채로 시작한다)

    /// <param name="topExtra">처음 자리의 위쪽에 더할 값(특수문자 위젯이 있으면 그 아래로 겹치지 않게).</param>
    public ClockWidgetController(IWriterHost host, Func<int> topExtra)
    {
        this.host = host;
        widget = new ClockWidget { Visible = false };
        host.EditorHost.Controls.Add(widget);
        placer = new FloatingWidgetPlacer(host, widget, widget.DesiredSize, widget.SetColors,
            () => (host.Settings.ClockRightGap, host.Settings.ClockTop),
            (gap, top) => { host.Settings.ClockRightGap = gap; host.Settings.ClockTop = top; },
            topExtra);
        widget.Moved += placer.SavePos;
        widget.ExpandedChanged += placer.Resize;
        placer.Hook(() => shown, Sync);
        ApplyPlace();
    }

    public bool IsOn => widget.Visible;

    /// <summary>다른 위젯이 처음 자리를 잡을 때 이 위젯 아래로 비켜 가려고 쓴다: 보이면 이 위젯의 높이, 아니면 null.</summary>
    public int? VisibleHeight => widget.Visible ? widget.Height : null;

    /// <summary>위젯 모음의 시계 카드 · 사이드바 시계 아이콘: 위젯을 켜고 끈다.</summary>
    public void Toggle() { shown = !shown; Sync(); }

    /// <summary>설정의 시계 위치(도시 · 시간대)를 위젯에 반영한다.</summary>
    public void ApplyPlace() => widget.SetZone(host.Settings.ClockZoneId, host.Settings.ClockCity);

    public void Sync() => placer.Sync(host.TxtEditing && host.EditorHost.Visible && shown);
}
