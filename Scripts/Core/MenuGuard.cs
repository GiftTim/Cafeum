using System.Runtime.InteropServices;

namespace Cafeum;

/// <summary>
/// 팝업 메뉴가 열려 있는 동안 메뉴 밖을 한 번 누르면 닫는다. WinForms 기본 동작은 웹 화면(WebView) 위를 누를 때처럼
/// 다른 창이 클릭을 받으면 메뉴를 닫지 못하는 경우가 있어서, 마우스 단추가 새로 눌리는 순간을 직접 지켜본다.
/// </summary>
internal static partial class MenuGuard
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    // 시험할 때 마우스를 흉내 내는 부분(TestButtonDown · TestPosition)은 Scripts/Test/MenuGuard.Test.cs에 있다. 배포판에서는 항상 null이다.
    private static bool AnyButtonDown() => TestButtonDown() ?? ((GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0);
    private static Point MousePosition() => TestPosition() ?? Cursor.Position;

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
        bool stopped = false;
        ToolStripDropDownClosedEventHandler? onClosed = null;
        void Stop() // 감시를 끝낸다. 메뉴가 열릴 때마다 처리기가 하나씩 쌓이지 않도록 걸어 둔 닫힘 처리기도 뺀다
        {
            if (stopped) return;
            stopped = true;
            if (onClosed != null) menu.Closed -= onClosed;
            timer.Stop();
            timer.Dispose();
        }
        onClosed = (_, _) => Stop();
        timer.Tick += (_, _) =>
        {
            if (stopped) return;
            if (!menu.Visible) { Stop(); return; }
            bool down = AnyButtonDown();
            if (down && !wasDown && !Inside(menu, MousePosition())) menu.Close(ToolStripDropDownCloseReason.AppClicked);
            wasDown = down;
        };
        menu.Closed += onClosed;
        timer.Start();
    }
}
