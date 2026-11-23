namespace Cafeum;

/// <summary>
/// 에피소드 창의 특수문자 위젯(SpecialCharWidget)을 붙이고 다룬다: 사이드바 [특수문자] 아이콘으로 켜고 끄고, 칸을 누르면 쓰던 자리에 문자가 들어간다.
/// 직접 더한 문자는 설정(SpecialChars)에 "문자⇥설명"으로 저장한다.
/// </summary>
internal sealed class SpecialWidgetController
{
    private readonly IWriterHost host;
    private readonly SpecialCharWidget widget;
    private readonly FloatingWidgetPlacer placer;
    private bool shown;     // 켜져 있는가(창을 열 때마다 꺼진 채로 시작한다)

    public SpecialWidgetController(IWriterHost host)
    {
        this.host = host;
        widget = new SpecialCharWidget { Visible = false };
        host.EditorHost.Controls.Add(widget);
        placer = new FloatingWidgetPlacer(host, widget, widget.DesiredSize, widget.SetColors,
            () => (host.Settings.SpecialRightGap, host.Settings.SpecialTop),
            (gap, top) => { host.Settings.SpecialRightGap = gap; host.Settings.SpecialTop = top; });
        widget.CharChosen += Insert;
        widget.AddRequested += Ask;
        widget.RemoveRequested += ch =>
        {
            host.Settings.SpecialChars.RemoveAll(s => s.Split('\t')[0] == ch);
            host.Settings.Save();
            RefreshItems();
        };
        widget.Moved += placer.SavePos;
        widget.ExpandedChanged += placer.Resize;
        placer.Hook(() => shown, Sync);
        RefreshItems();
    }

    public bool IsOn => widget.Visible;

    /// <summary>다른 위젯이 처음 자리를 잡을 때 이 위젯 아래로 비켜 가려고 쓴다: 보이면 이 위젯의 높이, 아니면 null.</summary>
    public int? VisibleHeight => widget.Visible ? widget.Height : null;

    /// <summary>사이드바의 특수문자 아이콘: 위젯을 켜고 끈다.</summary>
    public void Toggle() { shown = !shown; Sync(); }

    /// <summary>환경설정의 특수문자 크기(작게 · 중간 · 크게)를 위젯에 반영한다.</summary>
    public void ApplySize()
    {
        widget.SizeLevel = host.Settings.SpecialCharSize;
        placer.Resize();
    }

    /// <summary>처음 들어 있는 문자들 + 직접 더한 문자들로 칸을 다시 채운다.</summary>
    private void RefreshItems()
    {
        widget.SizeLevel = host.Settings.SpecialCharSize;
        var list = SpecialCharWidget.Defaults.Select(d => (d.Ch, d.Desc, false)).ToList();
        foreach (var s in host.Settings.SpecialChars)
        {
            var parts = s.Split('\t');
            if (parts[0].Length > 0) list.Add((parts[0], parts.Length > 1 ? parts[1] : "", true));
        }
        widget.SetItems(list);
        placer.Resize();
    }

    private void Insert(string s)
    {
        if (!host.TxtEditing) return;
        host.Editor.Focus();
        host.Editor.SelectedText = s; // 쓰던 자리(선택해 둔 글이 있으면 그 글을 바꾸며)에 넣는다
    }

    private void Ask()
    {
        using var dlg = new SpecialCharDialog();
        if (dlg.ShowDialogOver(host.HostForm) != DialogResult.OK) return; // × · Esc는 아무것도 더하지 않는다
        string ch = dlg.Chars;
        var settings = host.Settings;
        if (ch.Length == 0 || SpecialCharWidget.Defaults.Any(d => d.Ch == ch) || settings.SpecialChars.Any(s => s.Split('\t')[0] == ch)) return;
        settings.SpecialChars.Add(ch + "\t" + dlg.Desc.Replace('\t', ' '));
        settings.Save();
        RefreshItems();
    }

    public void Sync() => placer.Sync(host.TxtEditing && host.EditorHost.Visible && shown);
}
