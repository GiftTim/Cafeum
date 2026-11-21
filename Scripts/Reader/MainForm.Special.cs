namespace Cafeum;

/// <summary>글쓰기 화면의 특수문자 위젯(SpecialCharWidget): 사이드바 [특수문자] 아이콘으로 켜고 끄고, 칸을 누르면 쓰던 자리에 문자가 들어간다.</summary>
public partial class MainForm
{
    private SpecialCharWidget? specialWidget;
    private bool specialShown;     // 켜져 있는가(창을 열 때마다 꺼진 채로 시작한다)

    /// <summary>사이드바의 특수문자 아이콘: 위젯을 켜고 끈다.</summary>
    internal void ToggleSpecialWidget() { specialShown = !specialShown; SyncSpecialWidget(); }
    internal bool SpecialWidgetOn => specialWidget is { Visible: true };

    private void InitSpecialWidget()
    {
        specialWidget = new SpecialCharWidget { Visible = false };
        editorHost.Controls.Add(specialWidget);
        specialWidget.CharChosen += InsertSpecial;
        specialWidget.AddRequested += AskSpecial;
        specialWidget.RemoveRequested += ch =>
        {
            settings.SpecialChars.RemoveAll(s => s.Split('\t')[0] == ch);
            settings.Save();
            RefreshSpecialItems();
        };
        specialWidget.Moved += SaveSpecialPos;
        specialWidget.ExpandedChanged += () => { ResizeSpecialWidget(); };
        editorHost.SizeChanged += (_, _) => { if (specialWidget.Visible) PlaceSpecialWidget(); };
        editorHost.VisibleChanged += (_, _) => { if (specialShown) SyncSpecialWidget(); };
        FormClosed += (_, _) => { specialWidget.Parent?.Controls.Remove(specialWidget); };
        RefreshSpecialItems();
    }

    /// <summary>처음 들어 있는 문자들 + 직접 더한 문자들(설정에 "문자⇥설명"으로 저장).</summary>
    /// <summary>환경설정의 특수문자 크기(작게 · 중간 · 크게)를 위젯에 반영한다.</summary>
    internal void ApplySpecialSize()
    {
        if (specialWidget == null) return;
        specialWidget.SizeLevel = settings.SpecialCharSize;
        ResizeSpecialWidget();
    }

    private void RefreshSpecialItems()
    {
        if (specialWidget == null) return;
        specialWidget.SizeLevel = settings.SpecialCharSize;
        var list = SpecialCharWidget.Defaults.Select(d => (d.Ch, d.Desc, false)).ToList();
        foreach (var s in settings.SpecialChars)
        {
            var parts = s.Split('\t');
            if (parts[0].Length > 0) list.Add((parts[0], parts.Length > 1 ? parts[1] : "", true));
        }
        specialWidget.SetItems(list);
        ResizeSpecialWidget();
    }

    private void InsertSpecial(string s)
    {
        if (!(directEdit && shownKind == ContentKind.Txt)) return;
        mainEditor.Focus();
        mainEditor.SelectedText = s; // 쓰던 자리(선택해 둔 글이 있으면 그 글을 바꾸며)에 넣는다
    }

    private void AskSpecial()
    {
        using var dlg = new SpecialCharDialog();
        if (dlg.ShowDialogOver(HostForm) != DialogResult.OK) return; // × · Esc는 아무것도 더하지 않는다
        string ch = dlg.Chars;
        if (ch.Length == 0 || SpecialCharWidget.Defaults.Any(d => d.Ch == ch) || settings.SpecialChars.Any(s => s.Split('\t')[0] == ch)) return;
        settings.SpecialChars.Add(ch + "\t" + dlg.Desc.Replace('\t', ' '));
        settings.Save();
        RefreshSpecialItems();
    }

    private void SyncSpecialWidget()
    {
        if (specialWidget == null) return;
        bool show = directEdit && shownKind == ContentKind.Txt && editorHost.Visible && specialShown;
        if (specialWidget.Visible != show) { if (show) { PlaceSpecialWidget(); specialWidget.BringToFront(); } specialWidget.Visible = show; }
        if (show) specialWidget.SetColors(mainEditor.BackColor, mainEditor.ForeColor, theme.Link);
    }

    /// <summary>접거나 펼치거나 칸이 늘면 크기를 바꾼다(위쪽 · 오른쪽 끝은 그대로).</summary>
    private void ResizeSpecialWidget()
    {
        if (specialWidget == null) return;
        var size = specialWidget.DesiredSize();
        specialWidget.Bounds = new Rectangle(specialWidget.Left + specialWidget.Width - size.Width, specialWidget.Top, size.Width, size.Height);
        if (settings.SpecialTop >= 0) SaveSpecialPos(); // 옮겨 둔 자리가 있으면 접고 펴도 오른쪽 끝 · 위쪽 기준 자리를 이어 간다
        if (specialWidget.Visible) PlaceSpecialWidget();
    }

    private Control SpecialHost => TopLevelControl ?? (Control)editorHost;

    /// <summary>위젯을 놓은 자리를 창 오른쪽 끝 · 위쪽 기준으로 설정에 적어 둔다.</summary>
    private void SaveSpecialPos()
    {
        if (specialWidget == null) return;
        var host = SpecialHost;
        settings.SpecialRightGap = Math.Max(0, host.ClientSize.Width - specialWidget.Right);
        settings.SpecialTop = Math.Max(0, specialWidget.Top);
        settings.Save();
    }

    private void PlaceSpecialWidget()
    {
        if (specialWidget == null) return;
        var host = SpecialHost;
        if (specialWidget.Parent != host)
        {
            Point? screen = specialWidget.Parent != null ? specialWidget.Parent.PointToScreen(specialWidget.Location) : null;
            specialWidget.Parent?.Controls.Remove(specialWidget);
            host.Controls.Add(specialWidget);
            if (screen is Point sp) specialWidget.Location = host.PointToClient(sp);
        }
        var size = specialWidget.DesiredSize();
        if (host.ClientSize.Width < size.Width || host.ClientSize.Height < size.Height || !host.IsHandleCreated)
        {
            specialWidget.Bounds = new Rectangle(new Point(Math.Max(0, host.ClientSize.Width - size.Width - LogicalToDeviceUnits(24)), LogicalToDeviceUnits(70)), size);
            return;
        }
        if (settings.SpecialTop >= 0)
        {
            // 마지막으로 놓은 자리(창 오른쪽 끝 · 위쪽 기준)에 그대로
            int x = Math.Clamp(host.ClientSize.Width - size.Width - settings.SpecialRightGap, 0, Math.Max(0, host.ClientSize.Width - size.Width));
            int y = Math.Clamp(settings.SpecialTop, 0, Math.Max(0, host.ClientSize.Height - size.Height));
            specialWidget.Bounds = new Rectangle(x, y, size.Width, size.Height);
            specialWidget.BringToFront();
            return;
        }
        // 처음 자리: 글 칸 오른쪽 위쪽
        int right = editorHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - LogicalToDeviceUnits(24);
        var at = host.PointToClient(editorHost.PointToScreen(new Point(Math.Max(0, right - size.Width), LogicalToDeviceUnits(70))));
        specialWidget.Bounds = new Rectangle(at, size);
        specialWidget.BringToFront();
        SaveSpecialPos(); // 처음 자리도 기억해서, 트리를 열고 닫거나 껐다 켜도 같은 자리에 켜지게
    }
}
