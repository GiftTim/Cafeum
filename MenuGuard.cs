using System.Runtime.InteropServices;

namespace CoffeeViewer;

/// <summary>
/// 팝업 메뉴가 열려 있는 동안 메뉴 밖을 한 번 누르면 닫는다. WinForms 기본 동작은 웹 화면(WebView) 위를 누를 때처럼
/// 다른 창이 클릭을 받으면 메뉴를 닫지 못하는 경우가 있어서, 마우스 단추가 새로 눌리는 순간을 직접 지켜본다.
/// </summary>
internal static class MenuGuard
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    /// <summary>시험용: 마우스 단추 · 위치를 흉내 낼 때만 채운다(평소에는 null).</summary>
    internal static Func<bool>? ButtonDownOverride;
    internal static Func<Point>? PositionOverride;
    private static bool AnyButtonDown() => ButtonDownOverride?.Invoke() ?? ((GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0);

    private static bool Inside(ToolStripDropDown dd, Point p)
    {
        if (!dd.Visible) return false;
        if (dd.Bounds.Contains(p)) return true;
        foreach (ToolStripItem i in dd.Items)
            if (i is ToolStripDropDownItem d && d.DropDown.Visible && Inside(d.DropDown, p)) return true;
        return false;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> hooked = new();

    /// <summary>이 팝업(메뉴 · 목록)이 열릴 때마다, 밖을 누르면 닫히게 지켜보기 시작한다. 한 팝업에 여러 번 불러도 한 번만 걸린다.</summary>
    public static void Attach(ToolStripDropDown menu)
    {
        if (hooked.TryGetValue(menu, out _)) return;
        hooked.Add(menu, menu);
        menu.Opened += (_, _) => CloseOnOutsideClick(menu);
    }

    /// <summary>툴바 · 메뉴 줄에 달린 모든 드롭다운(하위 메뉴까지)에 같은 보호를 건다.</summary>
    public static void AttachAll(ToolStrip strip)
    {
        foreach (ToolStripItem item in strip.Items) AttachItem(item);
        if (hooked.TryGetValue(strip, out _)) return;
        hooked.Add(strip, strip);
        strip.ItemAdded += (_, e) => AttachItem(e.Item); // 나중에 올라오는 버튼(참고 자료 서식창의 읽기 방식 등)도 걸어 둔다
    }

    private static void AttachItem(ToolStripItem item)
    {
        if (item is not ToolStripDropDownItem d) return;
        if (!hooked.TryGetValue(d, out _))
        {
            hooked.Add(d, d);
            d.DropDownOpened += (_, _) => CloseOnOutsideClick(d.DropDown);
        }
        foreach (ToolStripItem child in d.DropDownItems) AttachItem(child);
    }
    public static void CloseOnOutsideClick(ToolStripDropDown menu)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 30 };
        bool wasDown = AnyButtonDown(); // 메뉴를 연 그 클릭이 아직 눌려 있어도 닫지 않게
        void Stop() { if (timer.Enabled) timer.Stop(); }
        timer.Tick += (_, _) =>
        {
            if (!menu.Visible) { Stop(); timer.Dispose(); return; }
            bool down = AnyButtonDown();
            if (down && !wasDown && !Inside(menu, PositionOverride?.Invoke() ?? Cursor.Position)) menu.Close(ToolStripDropDownCloseReason.AppClicked);
            wasDown = down;
        };
        menu.Closed += (_, _) => Stop();
        timer.Start();
    }
}
