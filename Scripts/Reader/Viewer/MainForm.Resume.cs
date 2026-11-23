using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// 읽던 자리 기억: 파일마다 마지막으로 읽은 자리를 저장하고 다시 열 때 되돌려 놓는다. (MainForm의 한 조각 — 클래스 설명은 MainForm.cs)
/// </summary>
public partial class MainForm
{
    // ───────────────────────── 읽던 자리 기억 ─────────────────────────

    /// <summary>파일을 열 때마다 서재 기록(마지막으로 연 시각)을 남긴다. 최근 파일 메뉴도 여기서 나온다.</summary>
    private void OnBookOpened(string path)
    {
        lookRec = library.Touch(path);
        library.SaveIfDirty();
        ApplyLook(); // 이 파일에 기억해 둔 서식으로
        // txt는 SetContentMode가 currentPath를 바꾸기 전에 불리므로, 탐색 막대의 "파일" 목록은 여기서 다시 맞춘다.
        ConfigureNavBar(navKind);
    }

    /// <summary>지금 보고 있는 위치를 서재 기록에 적는다(메모리에만; 디스크에는 타이머·종료 때 저장).</summary>
    private void CapturePosition()
    {
        string? path = currentPath;
        if (string.IsNullOrEmpty(path)) return;

        if (imageMode)
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && imagePages.Count > 0)
                library.SavePosition(path, imageIndex, 0, (imageIndex + 1.0) / imagePages.Count);
        }
        else if (txtWeb)
        {
            // 예전(텍스트 상자) 기록과 같은 형식 — 전체 글 기준 글자 위치 — 으로 남겨서 어느 쪽으로 열어도 이어 읽힌다.
            if (txtBook != null && fullText.Length > 0)
            {
                library.SavePosition(path, txtWebTopOffset, 0, (double)txtWebTopOffset / fullText.Length);
                QueryTxtTop(); // 다음 기록 때 쓸 최신 위치를 웹뷰에서 받아둔다
            }
        }
        else if (epubMode)
        {
            if (epubBook != null && epubBook.Chapters.Count > 0)
            {
                double progress = (epubChapterIndex + epubRatioPermille / 1000.0) / epubBook.Chapters.Count;
                library.SavePosition(path, epubChapterIndex, epubRatioPermille, progress);
                QueryEpubRatio(); // 다음 기록 때 쓸 최신 위치를 웹뷰에서 받아둔다
            }
        }
        else if (editorHost.Visible)
        {
            int offset = TxtTopOffset();
            int length = pageMode ? fullText.Length : mainEditor.TextLength;
            library.SavePosition(path, offset, 0, length == 0 ? 0 : (double)offset / length);
        }
    }

    private async void QueryEpubRatio()
    {
        if (epubView?.CoreWebView2 == null || positionQueryBusy) return;
        positionQueryBusy = true;
        try
        {
            string json = await epubView.CoreWebView2.ExecuteScriptAsync(
                "(window.__clrTotalPages !== undefined)" +
                "? Math.round(1000 * window.__clrPageIndex / Math.max(1, window.__clrTotalPages - 1))" +
                ": Math.round(1000 * window.scrollY / Math.max(1, document.documentElement.scrollHeight - window.innerHeight))");
            if (int.TryParse(json, out int v)) epubRatioPermille = Math.Clamp(v, 0, 1000);
        }
        catch { /* 문서가 바뀌는 중이면 이번 값은 건너뛴다 */ }
        finally { positionQueryBusy = false; }
    }

    /// <summary>txt에서 지금 화면 맨 위에 보이는 글자의 전체 글 기준 위치.</summary>
    private int TxtTopOffset()
    {
        if (pageMode) return currentPageStart;
        if (!mainEditor.IsHandleCreated) return 0;
        int firstLine = (int)SendMessage(mainEditor.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        return Math.Max(0, (int)SendMessage(mainEditor.Handle, EM_LINEINDEX, (IntPtr)firstLine, IntPtr.Zero));
    }

    /// <summary>
    /// 클릭한 자리(mainEditor 좌표)의 줄 첫 글자 위치. 텍스트박스의 글자 위치 조회 메시지(EM_CHARFROMPOS)는 결과가
    /// 16비트로 잘려서 글이 6만 자를 넘으면 틀리므로, 줄 번호로 계산한다: 맨 위 줄 + (클릭 높이 ÷ 실제 줄 간격).
    /// </summary>
    private int CharIndexAtPoint(Point p)
    {
        if (!mainEditor.IsHandleCreated) return 0;
        var h = mainEditor.Handle;
        int first = (int)SendMessage(h, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        int firstIdx = (int)SendMessage(h, EM_LINEINDEX, (IntPtr)first, IntPtr.Zero);
        int nextIdx = (int)SendMessage(h, EM_LINEINDEX, (IntPtr)(first + 1), IntPtr.Zero);
        if (firstIdx < 0) return 0;

        int y0 = mainEditor.GetPositionFromCharIndex(firstIdx).Y;
        int lineHeight = nextIdx > firstIdx ? mainEditor.GetPositionFromCharIndex(nextIdx).Y - y0 : mainEditor.Font.Height;
        if (lineHeight <= 0) lineHeight = Math.Max(1, mainEditor.Font.Height);

        int line = first + Math.Max(0, (int)Math.Floor((p.Y - y0) / (double)lineHeight));
        int idx = (int)SendMessage(h, EM_LINEINDEX, (IntPtr)line, IntPtr.Zero);
        return idx < 0 ? Math.Max(0, mainEditor.TextLength - 1) : idx;
    }

    /// <summary>mainEditor에서 index가 있는 줄이 화면 맨 위에 오도록 스크롤한다(ScrollToCaret은 그 줄이 맨 아래에 걸리기도 해서).</summary>
    private void ScrollCharToTop(int index)
    {
        if (!mainEditor.IsHandleCreated) return;
        var h = mainEditor.Handle;
        int line = (int)SendMessage(h, EM_LINEFROMCHAR, (IntPtr)index, IntPtr.Zero);
        int first = (int)SendMessage(h, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        if (line != first) SendMessage(h, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(line - first));
    }

    /// <summary>epub을 열 때 읽던 챕터 번호를 돌려주고, 챕터 안 위치는 챕터가 뜬 뒤에 복원하도록 예약해둔다.</summary>
    private int TakeSavedEpubStart(string path)
    {
        var rec = library.Find(path);
        int chapter = 0, ratio = 0;
        if (rec != null && epubBook != null && epubBook.Chapters.Count > 0)
        {
            chapter = Math.Clamp(rec.PosA, 0, epubBook.Chapters.Count - 1);
            ratio = Math.Clamp(rec.PosB, 0, 1000);
        }
        lastEpubTrackedChapter = chapter;
        epubRatioPermille = ratio;
        epubRestorePermille = ratio > 0 ? ratio : null;
        if (chapter > 0 || ratio > 0) ShowToast("이어서 읽습니다");
        return chapter;
    }

    private int SavedImagePage(string path)
    {
        var rec = library.Find(path);
        if (rec == null || rec.PosA <= 0) return 0;
        ShowToast("이어서 읽습니다");
        return rec.PosA;
    }

    /// <summary>epub 챕터가 다 뜬 직후(모든 스크립트를 심은 뒤): 읽던 위치로 옮기고, 낭독 중이었다면 이어서 읽는다.</summary>
    private async Task AfterEpubChapterReady()
    {
        if (epubView?.CoreWebView2 == null) return;

        if (epubRestorePermille is int ratio)
        {
            epubRestorePermille = null;
            string js = "(function(r){" +
                " if (window.__clrGoTo && window.__clrTotalPages !== undefined) window.__clrGoTo(Math.round(r / 1000 * (window.__clrTotalPages - 1)));" +
                " else window.scrollTo(0, Math.min(r, 980) / 1000 * Math.max(0, document.documentElement.scrollHeight - window.innerHeight));" +
                "})(" + ratio + ");";
            try { await epubView.CoreWebView2.ExecuteScriptAsync(js); } catch { }
        }

        // txt 웹 화면: 문서가 뜨면 기다리던 원본 위치로 옮기거나, 찾은 글을 표시한다.
        if (txtWeb && txtPendingSelect is var (selStart, selLength))
        {
            txtPendingSelect = null;
            txtPendingOffset = null;
            try { await epubView.CoreWebView2.ExecuteScriptAsync($"window.__clrTxtSel && window.__clrTxtSel({selStart},{selLength});"); } catch { }
        }
        else if (txtWeb && txtPendingOffset is int pending)
        {
            txtPendingOffset = null;
            try { await epubView.CoreWebView2.ExecuteScriptAsync($"window.__clrTxtGo && window.__clrTxtGo({pending});"); } catch { }
            txtWebTopOffset = pending;
        }
        else if (txtWeb && txtBook != null && epubChapterIndex >= 0 && epubChapterIndex < txtBook.Ranges.Count)
        {
            txtPendingOffset = null;
            QueryTxtTop(); // 앞 · 뒤 문서로 넘어온 경우(페이지 끝 · 스크롤 바닥) — 실제로 보이는 자리를 읽어 둔다
        }
        RefreshNavPosition();

        if (ttsActive && ttsAwaitingChapter)
        {
            ttsAwaitingChapter = false;
            if (!await PrepareEpubTts()) { StopTts(); return; }
            ttsPos = 0;
            SpeakNextEpub();
        }
    }
}
