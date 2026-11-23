using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// 보기: 읽기 방식(스크롤 · 페이지 · 페이지 터치) 바꾸기와 페이지 나누기 계산. (MainForm의 한 조각 — 클래스 설명은 MainForm.cs)
/// </summary>
public partial class MainForm
{
    // ───────────────────────── 보기: 읽기 방식(스크롤/페이지/페이지 터치) ─────────────────────────

    private enum ReadingMode { Scroll, Page, PageTouch }

    /// <summary>보기 > 읽기 방식 메뉴는 txt/epub/만화 공용이라, 지금 어느 쪽을 보고 있는지에 따라
    /// 알맞은 쪽으로 보낸다. 터치 켜짐 여부(tapToTurnEnabled)와 화살표 표시는 여기서 한 번에 정한다 —
    /// 페이지 터치를 고르면 화면 전체가 곧 "버튼"이니 작은 화살표는 굳이 같이 안 보여준다.</summary>
    private void SetReadingMode(ReadingMode mode)
    {
        bool isPage = mode != ReadingMode.Scroll;
        bool wasTapEnabled = tapToTurnEnabled;
        tapToTurnEnabled = mode == ReadingMode.PageTouch;
        imageCanvas.TapToTurnEnabled = tapToTurnEnabled;

        if (epubMode)
        {
            if (isPage == epubPageMode && tapToTurnEnabled != wasTapEnabled)
            {
                // 다단 배치 자체는 안 바뀌고(페이지 ↔ 페이지 터치) 터치 켜짐 여부만 바뀐 거면, 챕터를
                // 통째로 다시 불러와 위치가 처음으로 돌아가지 않도록 지금 켜진 스크립트만 살짝 갱신한다.
                PushEpubTapEnabled();
                UpdatePageNavVisibility(isPage && !tapToTurnEnabled);
            }
            else
            {
                SetEpubPageMode(isPage); // 내부에서 챕터를 다시 불러오며, 지금 tapToTurnEnabled 기준으로 스크립트를 새로 심는다.
            }
        }
        else if (!imageMode)
        {
            SetTxtPageMode(isPage);
        }
        else
        {
            UpdatePageNavVisibility(!tapToTurnEnabled); // 만화는 항상 "페이지" — 터치 켜짐 여부만 화살표 표시에 영향을 준다.
        }

        UpdateReadingModeMenuChecks(mode);
    }

    private void UpdateReadingModeMenuChecks(ReadingMode mode)
    {
        scrollModeMenuItem.Checked = mode == ReadingMode.Scroll;
        pageModeMenuItem.Checked = mode == ReadingMode.Page;
        pageTouchModeMenuItem.Checked = mode == ReadingMode.PageTouch;
    }

    /// <summary>epub 읽기 방식 전환 — 지금 챕터를 다시 불러와서 다단 배치/바닥 감지 스크립트를 깨끗하게 새로 심는다.</summary>
    private void SetEpubPageMode(bool isPage)
    {
        epubPageMode = isPage;
        UpdatePageNavVisibility(isPage && !tapToTurnEnabled);
        ReloadEpubKeepingPlace();
    }

    /// <summary>
    /// 스크롤: 지금까지처럼 mainEditor에 전체 글을 담아 이어서 훑어본다.
    /// 페이지: 지금 화면에 실제로 몇 글자가 들어가는지 직접 재서(옵시디언 플러그인처럼 화면에 꽉 차게)
    /// 그만큼만 보여주고, 아래 이동줄로 앞뒤 페이지를 넘긴다.
    /// 어느 쪽이든 지금 보고 있던 위치는 최대한 그대로 유지한 채로 전환한다.
    /// </summary>
    private void SetTxtPageMode(bool isPage)
    {
        if (isPage != pageMode)
        {
            if (isPage)
            {
                int offset = mainEditor.SelectionStart; // 스크롤 상태라 지금 이게 전체 글 기준 위치
                pageMode = true;
                ApplyEditorWrap(); // 페이지 방식은 줄바꿈이 켜져 있어야 한 화면에 들어가는 글자 수를 잴 수 있다
                currentPageStart = offset;
                currentPageEnd = FindPageEnd(currentPageStart);
                ShowCurrentPage();
                mainEditor.SelectionStart = 0;
            }
            else
            {
                int offset = currentPageStart;
                pageMode = false;
                mainEditor.ScrollLocked = false; // 아래 ScrollToCaret이 막히지 않게 먼저 푼다
                mainEditor.Text = fullText;
                mainEditor.SelectionStart = Math.Clamp(offset, 0, mainEditor.Text.Length);
            }
            mainEditor.SelectionLength = 0;
            mainEditor.ScrollToCaret();
            if (!isPage) ApplyEditorWrap(); // 스크롤로 돌아오면 사용자가 정한 줄바꿈 설정으로
        }

        UpdatePageNavVisibility(isPage && !tapToTurnEnabled);
    }

    /// <summary>찾기 바·좌우 넘김 화살표처럼 Dock 없이 떠 있는 오버레이들의 위치를 창 크기에 맞춰 다시 잡는다.
    /// 화살표는 본문/그림 폭을 줄이지 않고 그 위에 그냥 겹쳐 그린다(자리를 마련해주지 않는다).</summary>
    private void RepositionOverlays()
    {
        // contentHost 기준으로 놓는다(Form에 직접 자식으로 달린 건 contentHost 쪽이라, 좌표계가 같다).
        // editorHost는 그 안에 한 겹 더 있는 Dock=Fill 자식이라 여기 좌표계로 쓰면 어긋난다.
        findBar.Left = ClientSize.Width - findBar.Width - 24;
        findBar.Top = contentHost.Top + 10;

        int centerY = contentHost.Top + contentHost.Height / 2 - prevNavBtn.Height / 2;
        prevNavBtn.Left = contentHost.Left + 16;
        prevNavBtn.Top = centerY;
        nextNavBtn.Left = contentHost.Right - nextNavBtn.Width - 16;
        nextNavBtn.Top = centerY;

        loadingOverlay.Bounds = contentHost.Bounds;

        // z-order도 겹쳐 그릴 때 확실히 보장이 안 될 때가 있어서, 위치를 다시 잡을 때마다
        // 맨 앞으로 명시적으로 올려준다.
        prevNavBtn.BringToFront();
        nextNavBtn.BringToFront();
        findBar.BringToFront();
        if (loadingOverlay.Visible) loadingOverlay.BringToFront();
        if (edgePrompt.Visible) { PlaceEdgePrompt(); edgePrompt.BringToFront(); }
    }

    private void ShowCurrentPage()
    {
        mainEditor.Text = fullText.Substring(currentPageStart, currentPageEnd - currentPageStart);
        int percent = fullText.Length == 0 ? 100 : (int)(100.0 * currentPageStart / fullText.Length);
        pageIndicatorLabel.Text = $"약 {percent}% 지점";
        RefreshNavPosition();
    }

    private void ChangePage(int delta)
    {
        HideEdgePrompt(); // 페이지를 넘기면 끝 · 처음 안내 상자는 걷는다(또 끝이면 곧바로 다시 뜬다)
        if (ttsActive && !ttsInternalNav) StopTts(); // 낭독 중에 직접 페이지를 넘기면 낭독은 멈춘다
        // 페이지 방식으로 읽는 중 사용자가 직접 끝(처음)을 넘기려 할 때만 옆 파일 처리로 넘긴다(낭독이 스스로 넘기는 건 끝에 닿기 전에 멈춘다).
        bool atEdgeHandled = editorHost.Visible && pageMode && !ttsInternalNav;
        if (delta > 0)
        {
            if (currentPageEnd >= fullText.Length) // 이미 마지막 페이지
            {
                if (atEdgeHandled) HandleBookEdge(forward: true);
                return;
            }
            currentPageStart = currentPageEnd;
            currentPageEnd = FindPageEnd(currentPageStart);
        }
        else
        {
            if (currentPageStart <= 0) // 이미 첫 페이지
            {
                if (atEdgeHandled) HandleBookEdge(forward: false);
                return;
            }
            int end = currentPageStart;
            currentPageStart = FindPageStart(end);
            currentPageEnd = end;
        }
        ShowCurrentPage();
        mainEditor.SelectionStart = 0;
        mainEditor.ScrollToCaret();
    }

    /// <summary>글자 크기·창 폭 기준으로 "이 정도면 한 페이지 되겠다" 하는 대략적인 시작 추정치.
    /// 이진 탐색의 첫 탐색 범위를 잡는 용도일 뿐이라, 정확하지 않아도 결과의 정확도엔 영향 없다.</summary>
    private int EstimateCharsPerPage()
    {
        using var g = mainEditor.CreateGraphics();
        const string sample = "가나다라마바사아자차카타파하ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var measured = g.MeasureString(sample, mainEditor.Font);
        float avgCharWidth = Math.Max(4f, measured.Width / sample.Length);
        int usableWidth = Math.Max(50, mainEditor.ClientSize.Width - 12);
        int charsPerLine = Math.Max(10, (int)(usableWidth / avgCharWidth));
        int lineHeight = Math.Max(1, mainEditor.Font.Height);
        int linesPerPage = Math.Max(3, mainEditor.ClientSize.Height / lineHeight);
        return Math.Max(200, charsPerLine * linesPerPage);
    }

    /// <summary>후보 텍스트를 실제로 mainEditor에 넣어보고, 스크롤 없이 화면에 다 들어가는지 진짜로 잰다.
    /// (옵시디언 플러그인이 CSS 레이아웃으로 실측했던 것과 같은 방식 — 여기선 WinForms의
    /// GetPositionFromCharIndex로 마지막 글자가 화면 안에 있는지를 직접 확인한다.)</summary>
    private bool FitsOnScreen(string candidate)
    {
        mainEditor.Text = candidate;
        if (candidate.Length == 0) return true;
        mainEditor.SelectionStart = 0; // 스크롤이 맨 위에 있어야 아래쪽 글자 위치가 정확히 측정된다
        mainEditor.SelectionLength = 0;
        mainEditor.ScrollToCaret();
        var pos = mainEditor.GetPositionFromCharIndex(candidate.Length - 1);
        return pos.Y + mainEditor.Font.Height <= mainEditor.ClientSize.Height;
    }

    /// <summary>start부터 시작해서, 화면에 꽉 차게(넘치지 않게) 들어가는 최대 끝 위치를 실측으로 찾는다.</summary>
    private int FindPageEnd(int start)
    {
        if (start >= fullText.Length) return fullText.Length;
        int remaining = fullText.Length - start;
        int cap = Math.Min(remaining, EstimateCharsPerPage() * 2);

        // 추정치가 화면을 다 못 채울 만큼 작았으면(글자가 작거나 창이 클 때), 다 채울 때까지 범위를 넓혀간다.
        while (cap < remaining && FitsOnScreen(fullText.Substring(start, cap)))
            cap = Math.Min(remaining, cap * 2);

        if (FitsOnScreen(fullText.Substring(start, cap)))
            return start + cap; // 남은 글 전체가 화면에 다 들어감 — 이 페이지가 마지막 페이지

        int loFit = 0, hiNotFit = cap;
        while (hiNotFit - loFit > 1)
        {
            int mid = (loFit + hiNotFit) / 2;
            if (FitsOnScreen(fullText.Substring(start, mid))) loFit = mid; else hiNotFit = mid;
        }
        return start + Math.Max(1, loFit); // 최소 한 글자는 진행해서 무한 루프를 막는다
    }

    /// <summary>end에서 끝나도록, 화면에 꽉 차게 들어가는 최소 시작 위치를 실측으로 찾는다(이전 페이지용).</summary>
    private int FindPageStart(int end)
    {
        if (end <= 0) return 0;
        int cap = Math.Min(end, EstimateCharsPerPage() * 2);

        while (cap < end && FitsOnScreen(fullText.Substring(end - cap, cap)))
            cap = Math.Min(end, cap * 2);

        if (FitsOnScreen(fullText.Substring(end - cap, cap)))
            return end - cap;

        int loFit = 0, hiNotFit = cap;
        while (hiNotFit - loFit > 1)
        {
            int mid = (loFit + hiNotFit) / 2;
            if (FitsOnScreen(fullText.Substring(end - mid, mid))) loFit = mid; else hiNotFit = mid;
        }
        return end - Math.Max(1, loFit);
    }
}
