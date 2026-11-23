using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// 몰입 모드(전체 화면): 메뉴 · 툴바를 숨기고 글만 보이게 하며, 마우스를 가장자리에 대면 다시 나타난다. (MainForm의 한 조각 — 클래스 설명은 MainForm.cs)
/// </summary>
public partial class MainForm
{
    // ───────────────────────── 몰입 모드(전체 화면) ─────────────────────────

    /// <summary>
    /// 메뉴·상태줄·제목표시줄을 숨기고 화면 전체로 본다. FormBorderStyle을 바꾸면 창 핸들이 다시 만들어져서
    /// 안에 든 WebView2(epub·PDF)가 깨질 수 있어, 창 스타일 비트만 직접 바꾸는 방식으로 한다.
    /// </summary>
    /// <summary>실제 창. 볼트 창의 탭 · PIP 창 안에 들어가 있으면 그 창, 아니면 이 창 자신 — 몰입 모드 · 활성 여부는 이 창 기준이어야 한다.</summary>
    private Form HostForm => TopLevelControl is Form f ? f : this;

    /// <summary>이 읽기 화면이 들어 있는 창이 지금 앞에 있는지(서재 안에 들어가 있으면 ActiveForm은 서재 창이다).</summary>
    private bool IsHostActive => ActiveForm == HostForm;

    /// <summary>서재 창(머리 막대)의 메뉴 · 몰입 버튼이 부르는 길.</summary>
    /// <summary>머리 막대 ☰ 버튼 바로 밑에 문서 메뉴를 띄운다. 화면에서 숨겨 둔 항목의 드롭다운은 위치를 못 잡고 화면 가운데로 가서,
    /// 항목들을 잠깐 일반 팝업 메뉴로 옮겨 띄우고 닫히면 제자리에 돌려놓는다(단축키는 원래 항목에 그대로 걸려 있다).</summary>
    internal ContextMenuStrip? ShowMainMenu(Control anchor, Point at)
    {
        if (toolbarMore == null) return null;
        var items = toolbarMore.DropDownItems.Cast<ToolStripItem>().ToList();
        if (items.Count == 0) return null;
        var cm = new ContextMenuStrip { Font = toolbarMore.DropDown.Font };
        toolbarMore.DropDownItems.Clear();
        cm.Items.AddRange(items.ToArray());
        cm.Closed += (_, _) => BeginInvoke(() =>
        {
            cm.Items.Clear();
            toolbarMore.DropDownItems.AddRange(items.ToArray());
            cm.Dispose();
        });
        cm.Show(anchor, at);
        MenuGuard.CloseOnOutsideClick(cm);
        return cm;
    }
    internal void ToggleImmersionFromHost() => ToggleImmersion();

    /// <summary>몰입 모드를 쓸 수 있는가: 참고 자료는 PIP 창에서만(서재 칸 안의 참고 자료는 막는다). 그 밖의 문서는 그대로.</summary>
    private bool ImmersionAllowed => InPip || !refBarActive;

    /// <summary>몰입 단추 · 메뉴 항목을 지금 문서에 맞게 눌리게/회색으로 맞춘다.</summary>
    private void UpdateImmersionAvailability()
    {
        bool ok = ImmersionAllowed;
        if (immersionMenuItem != null) immersionMenuItem.Enabled = ok || immersive;
        if (toolbarItems.TryGetValue("immersion", out var immItem))
        {
            immItem.Enabled = ok || immersive;
            immItem.ToolTipText = ok ? "몰입 모드 (F11)" : "몰입 모드 — 참고 자료는 PIP 창에서만 쓸 수 있어요";
        }
    }

    private void ToggleImmersion()
    {
        if (!immersive && !ImmersionAllowed) { ShowToast("몰입 모드는 PIP 창에서만 쓸 수 있어요"); return; }
        immersive = !immersive;
        var host = HostForm;
        if (immersive)
        {
            immersiveRestoreState = host.WindowState;
            immersiveRestoreBounds = host.WindowState == FormWindowState.Normal ? host.Bounds : host.RestoreBounds;
            if (host.WindowState == FormWindowState.Maximized) host.WindowState = FormWindowState.Normal;

            immersiveRestoreStyle = GetWindowLongPtr(host.Handle, GWL_STYLE);
            SetWindowLongPtr(host.Handle, GWL_STYLE, (IntPtr)((long)immersiveRestoreStyle & ~(long)(WS_CAPTION | WS_THICKFRAME)));
            var screen = Screen.FromControl(host).Bounds;
            SetWindowPos(host.Handle, IntPtr.Zero, screen.X, screen.Y, screen.Width, screen.Height, SWP_FRAMECHANGED | SWP_NOOWNERZORDER);

            SetToolbarVisible(false);
            statusLabel.Visible = false;
            immersionHoverTimer.Start();
            ShowToast("몰입 모드 — F11 또는 Esc로 나갑니다");
        }
        else
        {
            SetWindowLongPtr(host.Handle, GWL_STYLE, immersiveRestoreStyle);
            var r = immersiveRestoreBounds;
            SetWindowPos(host.Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_FRAMECHANGED | SWP_NOOWNERZORDER);
            if (immersiveRestoreState == FormWindowState.Maximized) host.WindowState = FormWindowState.Maximized;

            immersionHoverTimer.Stop();
            SetToolbarVisible(true);
            statusLabel.Visible = true;
        }
        if (immersionMenuItem != null) immersionMenuItem.Checked = immersive;
        // 몰입 중에는 "나가기" 모양(안쪽으로 모이는 화살표), 아닐 때는 "들어가기" 모양(바깥으로 퍼지는 화살표)
        if (toolbarItems.TryGetValue("immersion", out var immItem)) immItem.Text = immersive ? "" : GlyphFullScreen;
        RepositionOverlays();
    }
}
