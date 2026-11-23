using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// 단축키: 키 입력을 받아 메뉴 동작으로 이어 준다. (MainForm의 한 조각 — 클래스 설명은 MainForm.cs)
/// </summary>
public partial class MainForm
{
    // ───────────────────────── 단축키 ─────────────────────────

    /// <summary>F5 자동 재생(만화 슬라이드쇼 · 소설 낭독) · F11 몰입 · F1 도움말 · Ctrl+L 서재 · Ctrl+, 설정. 메뉴가 숨겨진 몰입 모드나 웹뷰에 포커스가 있을 때도 되도록 메뉴 단축키가 아니라 여기서 직접 처리한다.</summary>
    private bool TryHandleFeatureKey(Keys key)
    {
        if (BlockedByLock(key)) return true; // 잠금 모드: 쪽을 넘기거나 움직이는 키는 막는다
        switch (key)
        {
            case Keys.F5: ToggleAutoPlay(); return true;
            case Keys.F11: // PIP 창: 몰입 모드. 서재 안: 서재의 전체 화면 버튼과 같이(전체 화면 + 항상 위, 다시 누르면 함께 풀린다)
                if (!InPip && HostForm is VaultForm fullVault) fullVault.ToggleFullScreenLikeLibrary(); else ToggleImmersion();
                return true;
            case Keys.Escape when edgePrompt.Visible: HideEdgePrompt(); return true; // 끝 · 처음 안내 상자부터 닫는다
            case Keys.Escape when immersive: ToggleImmersion(); return true;
            case Keys.Escape when !InPip && HostForm is VaultForm { IsFullScreen: true } escVault: escVault.ToggleFullScreenLikeLibrary(); return true; // 서재 전체 화면은 Esc로도 나간다
            case Keys.F1: if (HostForm is VaultForm vf) vf.ToggleFullScreenByKey(); else ShowHelp(); return true; // F1 = 전체 화면(PIP 같은 작은 창에서는 예전처럼 사용법)
            case Keys.OemCloseBrackets when CanUseBracketKeys(): GoAdjacentFile(forward: true); return true; // ]
            case Keys.OemOpenBrackets when CanUseBracketKeys(): GoAdjacentFile(forward: false); return true; // [
            case Keys.Control | Keys.L: ShowLibrary(); return true;
            case Keys.Control | Keys.S when directEdit && !mainEditor.ReadOnly: FlushAutoSave(); return true; // 쓰는 중인 글은 Ctrl+S로 바로 저장
            case Keys.Control | Keys.Oemcomma: ShowSettings(); return true;
            // 글자 크기: Ctrl+- 축소, Ctrl++(= 키·숫자패드 포함) 확대, Ctrl+0 기본. txt · epub만 — PDF · 만화는 그대로 통과시킨다.
            case Keys.Control | Keys.OemMinus or Keys.Control | Keys.Subtract when CanZoomText: ViewZoomBy(-1); return true;
            case Keys.Control | Keys.Oemplus or Keys.Control | Keys.Shift | Keys.Oemplus or Keys.Control | Keys.Add when CanZoomText: ViewZoomBy(1); return true;
            case Keys.Control | Keys.D0 or Keys.Control | Keys.NumPad0 when CanZoomText: ViewZoomReset(); return true;
            case Keys.Control | Keys.O when immersive: OpenFileDialog(); return true;
            // txt 웹 화면: 웹뷰에 포커스가 있어도 웹뷰 자체 찾기 대신 우리 찾기 바(글 전체 검색)로.
            case Keys.Control | Keys.F when txtWeb: findBar.Show(); return true;
            // ← → : 만화 · 소설 페이지 방식에서 이전 · 다음 페이지. 서재 안에 들어가 있으면 방향키를 서재 창이 "다음 칸으로
            // 포커스 옮기기"로 먼저 가져가서 만화가 안 넘어갔다 — 그래서 포커스가 어디 있든 여기서 먼저 받는다.
            case Keys.Left when CanUseArrowPaging(): TurnPage(-1); return true;
            case Keys.Right when CanUseArrowPaging(): TurnPage(1); return true;
        }
        return false;
    }

    /// <summary>← → 를 페이지 넘기기로 쓸지: 만화, 또는 소설 페이지 방식(고치는 중이 아닐 때). 찾기 칸처럼 글을 입력하는 칸에 커서가 있으면 그 칸이 쓴다.</summary>
    private bool CanUseArrowPaging()
    {
        if (!(imageMode || (epubMode && epubPageMode && !epubEditable))) return false;
        Control focused = this;
        while (focused is ContainerControl { ActiveControl: { } inner }) focused = inner;
        return focused is not TextBoxBase { ReadOnly: false };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        TryHandleFeatureKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

    /// <summary>웹뷰(epub·PDF)에 포커스가 있으면 키 입력이 윈폼까지 안 올라오므로(F5는 웹뷰가 새로고침까지 해버린다) 웹뷰 쪽에서 가로챈다.</summary>
    private void HookWebViewKeys(WebView2 view)
    {
        // WinForms 래퍼(1.0.4191)는 CoreWebView2Controller를 공개하지 않아서, 안에 든 필드를 리플렉션으로 꺼내 쓴다.
        // 패키지 버전을 고정해 두었으니 안전하고, 혹시 실패하면(버전이 바뀌어 필드가 없어지면) epub은 문서 안 JS가
        // 같은 키를 대신 처리하고 PDF는 몰입 모드의 "마우스를 맨 위로" 탈출구가 남는다.
        try
        {
            var field = typeof(WebView2).GetField("_coreWebView2Controller", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(view) is not CoreWebView2Controller controller) return;
            controller.AcceleratorKeyPressed += (_, e) =>
            {
                if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
                var key = (Keys)(int)e.VirtualKey;
                if ((ModifierKeys & Keys.Control) != 0) key |= Keys.Control;
                if ((ModifierKeys & Keys.Shift) != 0 && key is Keys.OemOpenBrackets or Keys.OemCloseBrackets) return; // { } 는 넘기기 키가 아니다
                if (TryHandleFeatureKey(key)) e.Handled = true;
            };
        }
        catch { }
    }

    /// <summary>몰입 모드에서 마우스를 화면 맨 위 가장자리에 대면 메뉴가 잠깐 나타난다(키보드 없이도 빠져나올 수 있게).</summary>
    private void ImmersionHoverTick()
    {
        if (!immersive || MainMenuStrip == null) return;
        var menu = MainMenuStrip;
        var p = PointToClient(Cursor.Position);
        bool insideWindow = p.X >= 0 && p.X < ClientSize.Width;
        if (!toolbarShown)
        {
            if (insideWindow && p.Y >= 0 && p.Y <= 2 && IsHostActive) { SetToolbarVisible(true); RepositionOverlays(); }
        }
        else if (p.Y > (ToolbarVisibilityHost != null ? 0 : menu.Height) + 12 && !menu.Items.OfType<ToolStripMenuItem>().Any(i => i.DropDown.Visible))
        {
            SetToolbarVisible(false);
            RepositionOverlays();
        }
    }
}
