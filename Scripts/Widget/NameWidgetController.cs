namespace Cafeum;

/// <summary>
/// 에피소드 창의 이름 생성기 위젯(NameWidget)을 붙이고 다룬다: 위젯 모음 · 사이드바의 [이름 생성기]로 켜고 끈다.
/// 조건(최소 · 최대 글자, 시작 글자, 개수)은 설정에 적는다. 이름을 누르면 쓰던 자리에 들어간다.
/// </summary>
internal sealed class NameWidgetController
{
    private readonly IWriterHost host;
    private readonly NameWidget widget;
    private readonly FloatingWidgetPlacer placer;
    private bool shown;     // 켜져 있는가(창을 열 때마다 꺼진 채로 시작한다)

    /// <param name="topExtra">처음 자리의 위쪽에 더할 값(다른 위젯 아래로 겹치지 않게).</param>
    public NameWidgetController(IWriterHost host, Func<int> topExtra)
    {
        this.host = host;
        var s = host.Settings;
        widget = new NameWidget { Visible = false, MinLen = s.NameMin, MaxLen = s.NameMax, Start = s.NameStart, Count = s.NameCount, Korean = s.NameKorean, KSurname = s.NameKSurname, KStart = s.NameKStart };
        host.EditorHost.Controls.Add(widget);
        placer = new FloatingWidgetPlacer(host, widget, widget.DesiredSize, widget.SetColors,
            () => (host.Settings.NameRightGap, host.Settings.NameTop),
            (gap, top) => { host.Settings.NameRightGap = gap; host.Settings.NameTop = top; },
            topExtra);
        widget.NameChosen += Insert;
        widget.OptionsChanged += () =>
        {
            s.NameMin = widget.MinLen; s.NameMax = widget.MaxLen; s.NameStart = widget.Start; s.NameCount = widget.Count;
            s.NameKorean = widget.Korean; s.NameKSurname = widget.KSurname; s.NameKStart = widget.KStart;
            s.Save();
        };
        widget.Moved += placer.SavePos;
        widget.ExpandedChanged += placer.Resize;
        placer.Hook(() => shown, Sync);
    }

    public bool IsOn => widget.Visible;

    /// <summary>다른 위젯이 처음 자리를 잡을 때 이 위젯 아래로 비켜 가려고 쓴다: 보이면 이 위젯의 높이, 아니면 null.</summary>
    public int? VisibleHeight => widget.Visible ? widget.Height : null;

    public void Toggle() { shown = !shown; Sync(); }

    private void Insert(string text)
    {
        if (!host.TxtEditing) return;
        host.Editor.Focus();
        host.Editor.SelectedText = text; // 쓰던 자리(선택해 둔 글이 있으면 그 글을 바꾸며)에 넣는다
    }

    public void Sync() => placer.Sync(host.TxtEditing && host.EditorHost.Visible && shown);
}
