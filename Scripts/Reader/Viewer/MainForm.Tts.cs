using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// 낭독(TTS): 읽어 주기 시작 · 멈춤 · 문장 강조와 웹 화면에 넣는 스크립트. (MainForm의 한 조각 — 클래스 설명은 MainForm.cs)
/// </summary>
public partial class MainForm
{
    // ───────────────────────── 낭독(TTS) ─────────────────────────

    /// <summary>F5·"자동 재생 > 시작/정지". 만화는 슬라이드쇼를, 그 밖(소설)은 낭독을 켜고 끈다.</summary>
    private void ToggleAutoPlay()
    {
        if (imageMode) ToggleSlideshow();
        else ToggleTts();
    }

    private void ToggleTts()
    {
        if (ttsActive) StopTts(announce: true);
        else StartTts();
    }

    private void UpdateAutoPlayMenu()
    {
        if (autoPlayToggleMenuItem != null) autoPlayToggleMenuItem.Text = ttsActive || slideshowTimer != null ? "정지" : "시작";
        UpdateToolbar();
    }

    /// <summary>
    /// 낭독을 시작한다. fromOffset이 있으면 그 글자부터(우클릭 "여기서부터 낭독"), 없으면 드래그로 고른 자리나
    /// 지금 화면 맨 위부터 읽는다. txt는 전체 글 기준 위치, epub은 챕터 본문 기준 위치다.
    /// </summary>
    private async void StartTts(int? fromOffset = null)
    {
        if (ttsActive) StopTts();

        if (epubMode)
        {
            if (epubEditable) { ShowToast("읽기 전용 상태에서만 낭독할 수 있습니다"); return; }
            ttsActive = true;
            UpdateAutoPlayMenu();
            if (!await PrepareEpubTts()) { StopTts(); return; }
            if (!ttsActive) return;

            int start;
            if (fromOffset is int f) start = f;
            else
            {
                start = 0;
                try
                {
                    string json = await epubView!.CoreWebView2.ExecuteScriptAsync("window.__clrTts.firstVisible()");
                    int.TryParse(json, out start);
                }
                catch { }
            }
            if (!ttsActive) return;
            ttsPos = Math.Clamp(start, 0, ttsEpubText.Length);
            ShowToast("낭독 시작");
            SpeakNextEpub();
        }
        else if (editorHost.Visible)
        {
            if (!mainEditor.ReadOnly) { ShowToast("읽기 전용 상태에서만 낭독할 수 있습니다"); return; }
            if (fullText.Length == 0) return;

            int start;
            if (fromOffset is int f) start = f;
            else
            {
                // 보이는 곳에 표시된 선택(직접 고른 글, 또는 방금 읽던 문장)이 있으면 거기부터, 아니면 화면 맨 위부터.
                int baseOffset = pageMode ? currentPageStart : 0;
                bool selectionVisible = false;
                if (mainEditor.SelectionLength > 0)
                {
                    int y = mainEditor.GetPositionFromCharIndex(mainEditor.SelectionStart).Y;
                    selectionVisible = y >= 0 && y < mainEditor.ClientSize.Height;
                }
                start = selectionVisible ? baseOffset + mainEditor.SelectionStart : (pageMode ? currentPageStart : TxtTopOffset());
            }
            ttsActive = true;
            ttsPos = Math.Clamp(start, 0, fullText.Length);
            UpdateAutoPlayMenu();
            ShowToast("낭독 시작");
            SpeakNextTxt();
        }
        else
        {
            ShowToast("이 형식은 낭독을 지원하지 않습니다");
        }
    }

    private void StopTts(bool announce = false)
    {
        if (!ttsActive && !ttsAwaitingChapter) return;
        ttsActive = false;
        ttsAwaitingChapter = false;
        tts.Stop();
        ClearEpubTtsMark();
        UpdateAutoPlayMenu();
        if (announce) ShowToast("낭독 정지");
    }

    private void FinishTts(string message)
    {
        ttsActive = false;
        ttsAwaitingChapter = false;
        ClearEpubTtsMark();
        UpdateAutoPlayMenu();
        ShowToast(message);
    }

    private void OnTtsSentenceFinished()
    {
        if (!ttsActive) return;
        if (epubMode) SpeakNextEpub();
        else SpeakNextTxt();
    }

    /// <summary>txt: 다음 문장을 찾아 표시하고 읽는다. 화면(페이지)의 끝에 닿으면 다음 페이지로 넘긴 뒤 이어서 읽는다.</summary>
    private void SpeakNextTxt()
    {
        while (ttsActive)
        {
            int limit = pageMode ? currentPageEnd : fullText.Length;
            var sentence = TtsText.NextSentence(fullText, ttsPos, limit);
            if (sentence == null)
            {
                if (pageMode && currentPageEnd < fullText.Length)
                {
                    ttsInternalNav = true;
                    try { ChangePage(1); }
                    finally { ttsInternalNav = false; }
                    ttsPos = currentPageStart;
                    continue;
                }
                FinishTts("끝까지 읽었습니다");
                return;
            }

            var (start, length) = sentence.Value;
            ttsPos = start + length;
            HighlightTxt(start, length);

            string spoken = TtsText.Clean(fullText.Substring(start, length));
            if (!TtsText.HasSpeakable(spoken)) continue; // "* * *" 같은 구분선은 건너뛴다
            if (!tts.Speak(spoken)) FinishTts("낭독을 시작할 수 없습니다");
            return;
        }
    }

    private void HighlightTxt(int start, int length)
    {
        int local = start - (pageMode ? currentPageStart : 0);
        mainEditor.Select(local, length);
        if (pageMode) return; // 페이지 모드는 페이지 안에 다 보인다
        // 읽는 문장이 화면 아래쪽에 닿으면 그 줄을 맨 위로 올려서, 문장마다 한 줄씩 밀리지 않고 화면 단위로 넘어가게 한다.
        int y = mainEditor.GetPositionFromCharIndex(local).Y;
        if (y < 0 || y > mainEditor.ClientSize.Height * 0.7) ScrollCharToTop(local);
    }

    /// <summary>epub: 챕터에 낭독 스크립트가 있는지 확인하고 본문 텍스트를 받아온다.</summary>
    private async Task<bool> PrepareEpubTts()
    {
        if (epubView?.CoreWebView2 == null) return false;
        try
        {
            await epubView.CoreWebView2.ExecuteScriptAsync(EpubTtsScript); // 이미 심겨 있으면 그대로 둔다
            string json = await epubView.CoreWebView2.ExecuteScriptAsync("window.__clrTts.text()");
            ttsEpubText = JsonSerializer.Deserialize<string>(json) ?? "";
            return true;
        }
        catch { return false; }
    }

    private async void SpeakNextEpub()
    {
        while (ttsActive)
        {
            var sentence = TtsText.NextSentence(ttsEpubText, ttsPos, ttsEpubText.Length);
            if (sentence == null)
            {
                // 이 챕터를 다 읽었다 — 다음 챕터가 있으면 넘어가서 처음부터 이어 읽는다(글이 없는 이미지 챕터는 자동으로 건너뛰게 된다).
                if (epubBook != null && epubChapterIndex + 1 < epubBook.Chapters.Count)
                {
                    ttsAwaitingChapter = true;
                    ttsInternalNav = true;
                    try { ShowEpubChapter(epubChapterIndex + 1); }
                    finally { ttsInternalNav = false; }
                    return;
                }
                FinishTts("끝까지 읽었습니다");
                return;
            }

            var (start, length) = sentence.Value;
            ttsPos = start + length;
            try { await epubView!.CoreWebView2.ExecuteScriptAsync($"window.__clrTts.mark({start},{start + length})"); }
            catch { }
            if (!ttsActive) return;

            string spoken = TtsText.Clean(ttsEpubText.Substring(start, length));
            if (!TtsText.HasSpeakable(spoken)) continue;
            if (!tts.Speak(spoken)) FinishTts("낭독을 시작할 수 없습니다");
            return;
        }
    }

    private void ClearEpubTtsMark()
    {
        if (epubMode) RunEpubScript("window.__clrTts && window.__clrTts.clear();");
    }

    /// <summary>
    /// epub 문서 안에서 낭독을 돕는 스크립트. 본문의 글자 노드를 이어 붙인 텍스트(text)와 그 안의 위치 ↔ 화면 위치 대응을
    /// 만든다. 읽는 문장은 DOM을 건드리지 않는 CSS Custom Highlight로 표시해서(수정 모드에서 저장하는 HTML에 섞이지 않게),
    /// 필요하면 그 문장이 있는 페이지/스크롤 위치로 화면을 옮긴다. 문서(챕터)마다 새로 심어야 한다.
    /// </summary>
    private const string EpubTtsScript = @"
(function(){
  if (window.__clrTts) return;
  var nodes = [], full = '';
  var BLOCK = 'p,div,li,h1,h2,h3,h4,h5,h6,blockquote,section,article,tr,td,th,pre,dd,dt';

  function collect() {
    nodes = []; full = '';
    var lastBlock = null;
    var w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null);
    var n;
    while ((n = w.nextNode())) {
      var p = n.parentElement;
      if (!p) continue;
      var tag = p.tagName;
      if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'RT' || tag === 'RP') continue;
      var v = n.nodeValue;
      if (!v) continue;
      var blk = p.closest(BLOCK) || document.body;
      var brBefore = n.previousSibling && n.previousSibling.nodeName === 'BR';
      // 문단이 바뀌거나 줄바꿈(BR)이 있으면 문장도 거기서 끊기도록 줄바꿈 문자를 끼워 넣는다.
      if (full.length > 0 && (blk !== lastBlock || brBefore)) full += '\n';
      lastBlock = blk;
      nodes.push({ n: n, s: full.length, l: v.length });
      full += v;
    }
    return full;
  }

  function locate(off) {
    var lo = 0, hi = nodes.length - 1, ans = -1;
    while (lo <= hi) {
      var mid = (lo + hi) >> 1;
      if (nodes[mid].s <= off) { ans = mid; lo = mid + 1; } else { hi = mid - 1; }
    }
    if (ans < 0) return null;
    var e = nodes[ans];
    if (off >= e.s + e.l) {
      if (ans + 1 < nodes.length) return { n: nodes[ans + 1].n, o: 0 };
      return { n: e.n, o: e.l };
    }
    return { n: e.n, o: off - e.s };
  }

  function rangeOf(start, end) {
    var a = locate(start), b = locate(end > start ? end - 1 : end);
    if (!a || !b) return null;
    var r = document.createRange();
    r.setStart(a.n, a.o);
    r.setEnd(b.n, Math.min(b.n.nodeValue.length, b.o + (end > start ? 1 : 0)));
    return r;
  }

  var st = document.createElement('style');
  st.id = '__clr_tts_style';
  st.textContent = '::highlight(clr-tts) { background-color: #ffe066; color: #000000; }';
  document.head.appendChild(st);

  function ensureVisible(r) {
    var rc = r.getBoundingClientRect();
    if (window.__clrGoTo && window.__clrTotalPages !== undefined) {
      // 페이지 모드: body를 translateX로 밀고 있으니, 밀린 만큼(전환 애니메이션 중간값 포함)을 빼서 원래 x 위치를 구한다.
      var tx = 0;
      try { tx = new DOMMatrix(getComputedStyle(document.body).transform).m41; } catch (e) {}
      var page = Math.floor((rc.left - tx) / window.__clrPageWidth);
      if (page !== window.__clrPageIndex) window.__clrGoTo(page);
    } else if (rc.top < 0 || rc.bottom > window.innerHeight) {
      window.scrollTo(0, window.scrollY + rc.top - window.innerHeight * 0.25);
    }
  }

  function mark(start, end) {
    var r = rangeOf(start, end);
    if (!r) return;
    try { CSS.highlights.set('clr-tts', new Highlight(r)); } catch (e) {}
    ensureVisible(r);
  }

  function clear() { try { CSS.highlights.delete('clr-tts'); } catch (e) {} }

  // 지금 화면에 보이는 첫 글자의 위치 — 스크롤 모드든 페이지 모드든 똑같이, 화면 안에 들어온 첫 글자를 찾는다.
  function firstVisible() {
    collect();
    var vw = window.innerWidth, vh = window.innerHeight;
    var r = document.createRange();
    for (var i = 0; i < nodes.length; i++) {
      var e = nodes[i];
      if (!e.l || !/\S/.test(e.n.nodeValue)) continue;
      r.selectNodeContents(e.n);
      var b = r.getBoundingClientRect();
      if (b.bottom < 0 || b.top > vh || b.right < 0 || b.left > vw) continue;
      for (var o = 0; o < e.l; o += 8) {
        r.setStart(e.n, o); r.setEnd(e.n, Math.min(e.l, o + 1));
        var c = r.getBoundingClientRect();
        if (c.width === 0 && c.height === 0) continue;
        if (c.left >= -1 && c.top >= -1 && c.top < vh && c.left < vw) return e.s + o;
      }
    }
    return 0;
  }

  function offsetAt(x, y) {
    collect();
    var rg = document.caretRangeFromPoint ? document.caretRangeFromPoint(x, y) : null;
    if (!rg) return -1;
    for (var i = 0; i < nodes.length; i++) if (nodes[i].n === rg.startContainer) return nodes[i].s + rg.startOffset;
    return -1;
  }

  var api = { text: collect, firstVisible: firstVisible, mark: mark, clear: clear, lastCtx: -1 };
  document.addEventListener('contextmenu', function(e) { api.lastCtx = offsetAt(e.clientX, e.clientY); }, true);
  // 웹뷰 키 가로채기(HookWebViewKeys)가 안 먹는 경우를 위한 예비 — F5는 안 막으면 웹뷰가 챕터를 새로고침해버린다.
  document.addEventListener('keydown', function(e) {
    if (e.key === 'F5' || e.key === 'F11') {
      e.preventDefault();
      window.chrome.webview.postMessage(JSON.stringify({ type: 'key', key: e.key }));
    } else if ((e.key === ']' || e.key === '[') && !e.ctrlKey && !e.altKey && !e.metaKey && !(document.body && document.body.isContentEditable)) {
      // 다음(]) · 이전([) 파일 — 글을 고치는 중이 아닐 때만
      window.chrome.webview.postMessage(JSON.stringify({ type: 'key', key: e.key }));
    }
  });
  // Ctrl+휠: 브라우저 확대(웹뷰 기본 동작) 대신 글자 크기를 바꾼다.
  document.addEventListener('wheel', function(e) {
    if (!e.ctrlKey) return;
    e.preventDefault();
    window.chrome.webview.postMessage(JSON.stringify({ type: 'zoom', delta: e.deltaY < 0 ? 1 : -1 }));
  }, { passive: false });
  window.__clrTts = api;
})();
";
}
