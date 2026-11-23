using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// epub 읽기: 열기 · 챕터 · 페이지 넘김 · 글자 크기 · 웹 화면과 주고받는 메시지 · 읽기 방식 스크립트. (MainForm의 한 조각 — 클래스 설명은 MainForm.cs)
/// </summary>
public partial class MainForm
{
    // ───────────────────────── epub ─────────────────────────

    /// <summary>
    /// epub은 zip 안에 XHTML+이미지+CSS가 들어있는 진짜 웹 문서라 TextBox로는 못 그린다.
    /// 그래서 WebView2(엣지와 같은 크로미움 엔진)를 따로 붙여서 그 위에 XHTML을 그대로 띄운다.
    /// txt 전용 기능(수정하기·찾기·글자 크기)은 epub에는 아직 없어서 메뉴에서 잠가둔다.
    /// 읽기 방식(스크롤/페이지)만은 옵시디언 플러그인처럼 CSS 다단 레이아웃을 그대로 써서 epub에도 넣었다.
    /// </summary>
    private async void LoadEpubFile(string path)
    {
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("EPUB을 여는 중…");
        try
        {
            await EnsureEpubViewAsync();
            // 열 때마다 새 폴더에 푼다(같은 책을 두 창에서 열어도 서로의 폴더를 지우지 않게). 다 읽은 폴더는 지운다(TempCleanup).
            string extractDir = Path.Combine(Path.GetTempPath(), "CafeumEpub", Guid.NewGuid().ToString("N"));
            string? previousExtractDir = epubExtractDir;
            var progress = new Progress<double>(loadingOverlay.Report);
            // 이미지가 많이 든 큰 epub은 통째로 풀어두는(EpubBook.Load 안의 압축 해제) 부분이 시간이
            // 걸려서, UI 스레드를 막지 않게 백그라운드에서 돌리고 진행률만 받아온다.
            epubBook = await Task.Run(() => EpubBook.Load(path, extractDir, progress, cts.Token));
            epubExtractDir = extractDir; // 열기에 성공한 뒤에야 바꾼다(실패하면 앞서 보던 책의 폴더가 그대로 남도록)
            imageSet?.Dispose();
            imageSet = null;
            ClearImageCache();
            currentPath = path;
            OnBookOpened(path);
            SetContentMode(ContentKind.Epub);
            SetEditMode(false); // 새 epub을 열면 항상 읽기 전용으로 시작한다
            ShowEpubChapter(TakeSavedEpubStart(path)); // 읽던 챕터가 있으면 거기부터, 없으면 0
            TempCleanup.DeleteQuietly(previousExtractDir); // 앞서 보던 epub을 풀어 둔 폴더

            Text = $"{epubBook.Title} — {AppShared.AppDisplayName}";
            statusLabel.Text = $"{epubBook.Chapters.Count}개 챕터 (epub)";
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"epub을 여는 데 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// PDF는 애초에 페이지 크기·배치가 고정된 문서라, epub처럼 CSS 다단으로 재배치할 이유가 없다.
    /// WebView2를 파일에 그대로 navigate시키면 엣지와 같은 크로미움 내장 PDF 뷰어(페이지 넘기기·확대,
    /// 검색·인쇄까지)가 그 안에서 자체 툴바로 다 떠서, 우리가 따로 페이지 넘김·읽기 방식을 구현할 필요가 없다.
    /// </summary>
    private async void LoadPdfFile(string path)
    {
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("PDF를 여는 중…");
        try
        {
            await EnsurePdfViewAsync();
            imageSet?.Dispose();
            imageSet = null;
            ClearImageCache();
            currentPath = path;
            OnBookOpened(path);
            SetContentMode(ContentKind.Pdf);
            NavigatePdf(path);

            Text = $"{Path.GetFileName(path)} — {AppShared.AppDisplayName}";
            statusLabel.Text = "PDF (페이지 넘기기·확대·검색은 뷰어 자체 툴바를 이용하세요)";
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"PDF를 여는 데 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
        }
    }

    private async Task EnsurePdfViewAsync()
    {
        if (pdfView != null) return;
        pdfView = new WebView2 { Dock = DockStyle.Fill, AllowExternalDrop = false }; // 파일을 끌어놓으면 웹뷰가 그 파일로 이동해버리지 않게(창 전체 드롭이 처리한다)
        pdfHost.Controls.Add(pdfView);
        await pdfView.EnsureCoreWebView2Async();
        HookWebViewKeys(pdfView);
    }

    private async Task EnsureEpubViewAsync()
    {
        if (epubView != null) return;
        epubView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = theme.Back, AllowExternalDrop = false };
        epubHost.Controls.Add(epubView);
        await epubView.EnsureCoreWebView2Async();
        // 배경색을 안 정해둔 책이 시스템 다크 모드 때문에 자동으로 반전(검은 배경)되지 않도록,
        // 항상 밝은 배색으로 강제한다(실제 색은 아래 BuildEpubThemeScript가 테마대로 덮어쓴다).
        epubView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;
        epubView.CoreWebView2.Settings.IsZoomControlEnabled = false; // Ctrl+휠 · Ctrl+-/+ 는 브라우저 확대가 아니라 글자 크기로(ZoomBy)
        EpubWebGuard.Attach(epubView.CoreWebView2, () => ShowToast("책 안의 인터넷 주소는 열지 않아요")); // 책이 외부 서버에 접속하지 못하게
        epubView.CoreWebView2.NavigationCompleted += async (_, _) => await OnEpubNavigationCompleted();
        epubView.CoreWebView2.WebMessageReceived += OnEpubWebMessage;
        HookWebViewKeys(epubView);
        epubView.CoreWebView2.ContextMenuRequested += OnEpubContextMenuRequested;
    }

    /// <summary>
    /// startAtLastPage: "이전 챕터" 방향으로 넘어왔을 때, 앞쪽이 아니라 그 챕터의 마지막 페이지부터 보여준다
    /// (스크롤 모드에서는 의미 없고, 페이지 모드에서만 쓰인다).
    /// 지금 챕터를 편집 중이었다면, 떠나기 전에 고친 내용을 먼저 파일에 반영해둔다(안 그러면 그냥 날아간다).
    /// </summary>
    private async void ShowEpubChapter(int index, bool startAtLastPage = false)
    {
        if (epubBook == null || epubView?.CoreWebView2 == null) return;
        if (index < 0 || index >= epubBook.Chapters.Count) return;

        if (ttsActive && !ttsInternalNav) StopTts(); // 낭독 중에 직접 챕터를 옮기면(화살표·목차·글자 크기 변경 등) 낭독은 멈춘다
        if (epubEditable) await FlushEpubChapterEdits();

        epubChapterIndex = index;
        epubDocReady = false;
        RefreshNavPosition();
        if (index != lastEpubTrackedChapter) { epubRatioPermille = 0; lastEpubTrackedChapter = index; } // 다른 챕터로 옮기면 챕터 안 위치는 처음부터
        epubPendingStartAtLastPage = startAtLastPage;
        epubView.CoreWebView2.Navigate(new Uri(epubBook.Chapters[index].FilePath).AbsoluteUri);
    }

    /// <summary>지금 화면에 떠 있는 챕터의 (편집됐을 수도 있는) HTML을 읽어와서 풀어둔 폴더의 원본 파일에 덮어쓴다.</summary>
    private async Task FlushEpubChapterEdits()
    {
        if (epubBook == null || epubView?.CoreWebView2 == null) return;
        if (epubChapterIndex < 0 || epubChapterIndex >= epubBook.Chapters.Count) return;
        try
        {
            string json = await epubView.CoreWebView2.ExecuteScriptAsync("document.documentElement.outerHTML");
            string? html = JsonSerializer.Deserialize<string>(json);
            if (string.IsNullOrEmpty(html)) return;
            File.WriteAllText(epubBook.Chapters[epubChapterIndex].FilePath, "<!DOCTYPE html>\n" + html, new UTF8Encoding(false));
        }
        catch { /* 편집 중이 아니었거나 문서가 준비 안 됐으면 그냥 무시 */ }
    }

    /// <summary>챕터를 새로 불러올 때마다: 이미지 표시 여부 반영 + (페이지 모드면 다단 배치, 스크롤 모드면 바닥 감지) 스크립트를 심는다.</summary>
    private async Task OnEpubNavigationCompleted()
    {
        if (epubView?.CoreWebView2 == null) return;
        ApplyShowImages();
        ApplyEpubFontSize();

        try
        {
            // 책 자체가 어두운 배경/밝은 글자로 스타일을 박아둔 경우(다크 테마로 만든 책 등)도 있어서,
            // 브라우저의 다크모드 방지만으론 부족하다. 배경·글자색을 지금 테마 색으로 강제로 덮어쓴다.
            await epubView.CoreWebView2.ExecuteScriptAsync(BuildEpubThemeScript());
            await epubView.CoreWebView2.ExecuteScriptAsync(EpubTtsScript); // 낭독·우클릭 메뉴가 쓰는 도우미(문서마다 새로 심는다)
            await epubView.CoreWebView2.ExecuteScriptAsync(EpubKeepPlaceScript); // 글자 크기를 바꿔도 읽던 자리 지키기
        }
        catch { }

        if (epubEditable)
        {
            // 챕터를 새로 불러오면(다음/이전 챕터 이동 포함) 문서가 통째로 바뀌므로 매번 다시 켜줘야 한다.
            try { await epubView.CoreWebView2.ExecuteScriptAsync("document.body.contentEditable = 'true';"); } catch { }
        }

        try
        {
            if (epubPageMode)
            {
                string startPage = epubPendingStartAtLastPage ? "999999" : "0";
                epubPendingStartAtLastPage = false;
                string script = EpubPaginateScript.Replace("%START_PAGE%", startPage)
                    .Replace("%TAP_ENABLED%", tapToTurnEnabled ? "true" : "false")
                    .Replace("%TAP_CENTER%", TapZone.CenterRatio.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Replace("%PAD_L%", WebMargins().L.ToString()).Replace("%PAD_R%", WebMargins().R.ToString())
                    .Replace("%PAD_T%", WebMargins().T.ToString()).Replace("%PAD_B%", WebMargins().B.ToString())
                    .Replace("%COLS%", (epubMode && settings.EpubDoublePage) ? "2" : "1");
                await epubView.CoreWebView2.ExecuteScriptAsync(script);
            }
            else
            {
                // 스크롤 모드에서도, 웹소설을 변환한 epub 등 책 자체 CSS가 화면 넓을 때 스스로 다단
                // (column)을 켜는 경우가 있어 그대로 두면 글이 여러 단으로 쪼개져 뒤죽박죽으로 보인다.
                // 우리는 항상 세로로 쭉 이어지는 한 단짜리 스크롤을 기대하므로 강제로 꺼둔다.
                await epubView.CoreWebView2.ExecuteScriptAsync(BuildEpubResetColumnsScript());

                // 스크롤할 내용이 있는 보통 챕터라면 화살표는 원래대로 숨긴다 — 스크롤할 게 아예 없는
                // 챕터(이미지 한 장이 화면에 꽉 차는 경우 등)일 때만 스크립트가 곧 다시 보여달라고
                // 알려온다(아래 'scroll-dead-end' 처리) — 그런 챕터는 스크롤 이벤트 자체가 안 일어나서
                // 다음 챕터로 넘어갈 방법이 화살표(또는 키보드) 말고는 없기 때문이다.
                UpdatePageNavVisibility(false);
                if (epubPendingStartAtLastPage)
                {
                    // 위로 올려 이전 챕터에 왔으면 그 챕터의 끝(바닥 바로 위)에서 이어 읽는다.
                    // 바닥에 딱 붙이면 곧바로 "바닥 도달"로 다음 챕터가 열려 버리니 한 화면 가까이 위에 둔다.
                    epubPendingStartAtLastPage = false;
                    await epubView.CoreWebView2.ExecuteScriptAsync(
                        "window.scrollTo(0, Math.max(0, document.documentElement.scrollHeight - window.innerHeight - 60));");
                }
                await epubView.CoreWebView2.ExecuteScriptAsync(EpubScrollWatchScript);
            }
        }
        catch { /* 문서가 아직 준비 안 됐으면 그냥 무시 */ }

        try
        {
            // 만화/이미지 뷰어(ImageCanvas)와 같은 조작감으로, 본문 속 이미지도 클릭하면 확대해서 보게 한다.
            await epubView.CoreWebView2.ExecuteScriptAsync(EpubImageZoomScript);
        }
        catch { /* 문서가 아직 준비 안 됐으면 그냥 무시 */ }

        epubDocReady = true;
        await AfterEpubChapterReady(); // 읽던 위치 복원, 낭독 이어가기 (MainForm.Features.cs)
    }

    /// <summary>보기 > 이미지 표시 체크를 껐다 켰다 하면 지금 열려 있는 epub 챕터에 CSS를 꽂아 이미지를 숨기거나 되돌린다.</summary>
    private async void ApplyShowImages()
    {
        if (!epubMode || epubView?.CoreWebView2 == null) return;
        string css = showImagesMenuItem.Checked ? "" : "img, svg, image { display: none !important; }";
        string script = "(function(){var s=document.getElementById('__clr_hide_img');" +
            "if(!s){s=document.createElement('style');s.id='__clr_hide_img';document.head.appendChild(s);}" +
            "s.textContent=" + JsonSerializer.Serialize(css) + ";})();";
        try { await epubView.CoreWebView2.ExecuteScriptAsync(script); } catch { /* 문서가 아직 준비 안 됐으면 그냥 무시 */ }
    }

    /// <summary>지금 열려 있는 epub 챕터에 켜진 스크립트에 터치 켜짐 여부만 살짝 갱신한다 — 페이지 ↔
    /// 페이지 터치 전환처럼 다단 배치 자체는 안 바뀔 때, 챕터를 통째로 다시 불러와 위치가 처음으로
    /// 되돌아가는 것을 피하려고 쓴다.</summary>
    private async void PushEpubTapEnabled()
    {
        if (epubView?.CoreWebView2 == null) return;
        try { await epubView.CoreWebView2.ExecuteScriptAsync($"window.__clrTapEnabled = {(tapToTurnEnabled ? "true" : "false")};"); }
        catch { /* 문서가 아직 준비 안 됐으면(또는 스크롤 모드라 스크립트 자체가 없으면) 그냥 무시 */ }
    }

    /// <summary>보기 > 글자 크기를 지금 열려 있는 epub 챕터에도 그대로 적용한다(책 자체 글꼴 크기와 무관하게 강제).</summary>
    /// <summary>keepPlace: 읽는 도중 글자 크기 · 글꼴을 바꿀 때. 문서를 다시 불러오지 않고 그 자리에서 바꾸되, 바꾸기 전 화면 맨 위의
    /// 글자가 바꾼 뒤에도 보이게(페이지 방식 = 그 글자가 든 페이지로, 스크롤 = 같은 높이로) 맞춘다(EpubKeepPlaceScript).
    /// 예전엔 페이지 방식에서 문서를 통째로 다시 불러와 읽던 자리를 문단 단위로 어림잡느라, Ctrl+휠을 돌릴 때마다 앞뒤 페이지로 튀었다.</summary>
    private async void ApplyEpubFontSize(bool keepPlace = false)
    {
        if (!epubMode || epubView?.CoreWebView2 == null) return;
        epubView.ZoomFactor = ViewZoom / 100.0;
        // 글자 크기와 글꼴(설정에서 고른 것)을 책 자체 스타일과 무관하게 강제한다.
        // 줄 간격 · (txt의) 문단 간격도 여기서 같이 강제한다 — 설정창의 "줄 간격 · 문단 간격".
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string css = "* { font-size: " + fontSize.ToString(inv) + "pt !important; " +
            "font-family: " + (EffFontLatin is { } latin ? "\"" + latin.Replace("\"", "") + "\", " : "") + "\"" + EffFont.Replace("\"", "") + "\", sans-serif !important; " +
            "line-height: " + (EffLine / 100.0).ToString(inv) + " !important; }" +
            (EffAlign is "center" or "right" or "justify" ? " p { text-align: " + EffAlign + " !important; }" : EffAlign is "distribute" or "divide" ? " p { text-align: justify !important; text-align-last: justify !important; }" : "") +
            PaperCss() +
            " body.clr-txt p { margin-bottom: " + Math.Clamp(settings.ParagraphSpacing, 0, 60) + "px !important; }" + ParaCss();
        string apply = "var s=document.getElementById('__clr_font_size');" +
            "if(!s){s=document.createElement('style');s.id='__clr_font_size';document.head.appendChild(s);}" +
            "s.textContent=" + JsonSerializer.Serialize(css) + ";";
        string script = keepPlace && epubDocReady
            ? "(function(){var apply=function(){" + apply + "}; if (window.__clrKeepPlace) window.__clrKeepPlace(apply); else apply();})();"
            : "(function(){" + apply + "})();";
        try { await epubView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { /* 문서가 아직 준비 안 됐으면 그냥 무시 */ }
    }

    /// <summary>
    /// 글자 크기 · 글꼴을 바꿀 때 읽던 자리를 지키는 도우미(문서마다 심는다). 바꾸기 전에 화면 왼쪽 위 근처의 글자 하나를 집어 두고
    /// (caretRangeFromPoint — 페이지 방식의 translateX도 반영된 화면 좌표로 집힌다), 바꾼 다음 다단을 다시 나눈 뒤 그 글자가 있는
    /// 페이지로 간다. 스크롤 방식이면 그 글자가 전과 같은 높이에 오도록 스크롤한다. 글자를 못 집으면(그림만 있는 화면 등) 그냥 바꾼다.
    /// </summary>
    private const string EpubKeepPlaceScript = @"
(function(){
  if (window.__clrKeepPlace) return;
  function probe() {
    var w = window.innerWidth, h = window.innerHeight;
    for (var y = 6; y < h * 0.5; y += 14) {
      for (var x = 6; x < w * 0.6; x += 28) {
        var r = document.caretRangeFromPoint ? document.caretRangeFromPoint(x, y) : null;
        if (r && r.startContainer && r.startContainer.nodeType === 3 && /\S/.test(r.startContainer.nodeValue)) {
          var n = r.startContainer, o = Math.min(r.startOffset, n.nodeValue.length - 1);
          var k = document.createRange();
          k.setStart(n, Math.max(0, o)); k.setEnd(n, Math.max(0, o) + 1);
          // 빈 자리(여백)를 찍으면 caretRangeFromPoint는 가장 가까운 글자를 돌려주는데, 페이지 방식에서는 그게 화면 밖
          // 앞 페이지의 글자일 수 있다(그래서 글자 크기를 바꾸면 첫 페이지로 튀었다). 실제로 화면 안에 보이는 글자만 쓴다.
          var b = rectOf(k);
          if (b.width > 0 && b.left >= 0 && b.right <= w + 1 && b.top >= 0 && b.bottom <= h + 1) return k;
        }
      }
    }
    return null;
  }
  function rectOf(r) { var rs = r.getClientRects(); return rs.length ? rs[0] : r.getBoundingClientRect(); }
  function paged() { return window.__clrGoTo && window.__clrTotalPages !== undefined; }
  // 글자 크기를 연달아 바꾸는 동안(그사이 페이지를 넘기거나 스크롤하지 않았으면) 처음 집은 글자를 계속 기준으로 쓴다 — 작게 했다가
  // 다시 키울 때 매번 새로 집으면, 작을 때 화면 맨 위에 올라온 앞쪽 글(장 제목 등)로 기준이 밀려 점점 앞으로 간다.
  var anchor = null, anchorPage = -1, anchorY = -1;
  window.__clrKeepPlace = function(apply) {
    var here = anchor && (paged() ? window.__clrPageIndex === anchorPage : Math.abs(window.scrollY - anchorY) < 2);
    var r = here ? anchor : probe(), before = r ? rectOf(r) : null;
    apply();
    if (window.__clrRelayout) window.__clrRelayout();
    if (!r) { anchor = null; return; }
    var after = rectOf(r);
    if (paged()) {
      var tx = 0;
      try { tx = new DOMMatrix(getComputedStyle(document.body).transform).m41; } catch (e) {}
      window.__clrGoTo(Math.floor((after.left - tx + 1) / window.__clrPageWidth));
    } else if (before) {
      window.scrollBy(0, after.top - before.top);
    }
    anchor = r; anchorPage = window.__clrPageIndex; anchorY = window.scrollY;
  };
})();
";

    /// <summary>epub 안에서 온 메시지 처리: 페이지 번호 갱신, 페이지 모드에서 챕터 경계를 넘어갔을 때 다음/이전 챕터로,
    /// 스크롤 모드에서 바닥까지 밀었을 때 다음 챕터로.</summary>
    private void OnEpubWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? json = e.TryGetWebMessageAsString();
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            string type = doc.RootElement.GetProperty("type").GetString() ?? "";
            switch (type)
            {
                case "next-chapter":
                    // 스크롤 모드에서 낭독이 화면 맨 아래 문장에 닿으면 스크롤 감시가 "바닥 도달"로 다음 챕터를
                    // 넘겨버려 남은 문장을 못 읽게 된다 — 낭독 중엔 챕터 넘김을 낭독 쪽이 직접 맡는다.
                    if (ttsActive && !epubPageMode) break;
                    // 페이지 방식에서 마지막 챕터의 마지막 페이지를 넘기려 한 것 — 옆 파일 처리로(스크롤 방식은 바닥 감시가 반복해서 알리므로 제외).
                    if (epubPageMode && epubBook != null && epubChapterIndex + 1 >= epubBook.Chapters.Count) { HandleBookEdge(forward: true); break; }
                    ShowEpubChapter(epubChapterIndex + 1);
                    break;
                case "prev-chapter":
                    if (epubPageMode && epubChapterIndex - 1 < 0) { HandleBookEdge(forward: false); break; }
                    ShowEpubChapter(epubChapterIndex - 1, startAtLastPage: true);
                    break;
                case "page":
                    int index = doc.RootElement.GetProperty("index").GetInt32();
                    int total = doc.RootElement.GetProperty("total").GetInt32();
                    pageIndicatorLabel.Text = $"{index + 1} / {total} 페이지";
                    epubPageIdx = index; epubPageTotal = total;
                    RefreshNavPosition(); // 아래 네비게이션 바의 숫자도 현재 페이지 / 전체 페이지로
                    if (txtWeb) QueryTxtTop(); // 넘길 때마다 읽던 자리 · 탐색 막대를 따라가게
                    break;
                case "key":
                    TryHandleFeatureKey(doc.RootElement.GetProperty("key").GetString() switch
                    {
                        "F11" => Keys.F11,
                        "]" => Keys.OemCloseBrackets,
                        "[" => Keys.OemOpenBrackets,
                        _ => Keys.F5,
                    });
                    break;
                case "zoom":
                    ViewZoomBy(doc.RootElement.GetProperty("delta").GetInt32());
                    break;
                case "scroll-dead-end":
                    // 스크롤 모드인데 스크롤할 내용이 없는 챕터 — 스크롤로는 절대 다음 챕터로 못 넘어가니,
                    // 이때만 예외적으로 화살표를 보여준다(눌러서 챕터를 넘기게).
                    UpdatePageNavVisibility(true);
                    break;
            }
        }
        catch { /* 형식이 이상한 메시지는 무시 */ }
    }

    /// <summary>epub에서 화살표 버튼을 눌렀을 때. 페이지 모드면 페이지 넘기기(경계를 넘어가면 스크립트가
    /// 알아서 다음/이전 챕터를 요청한다) — 스크롤 모드에서 화살표가 보이는 건 스크롤할 내용이 아예
    /// 없는 챕터(scroll-dead-end)일 때뿐이라, 그때는 페이지 개념이 없으니 챕터 자체를 바로 넘긴다.</summary>
    private async void EpubChangePage(int delta)
    {
        if (epubView?.CoreWebView2 == null) return;
        HideEdgePrompt(); // 페이지를 넘기면 끝 · 처음 안내 상자는 걷는다(또 끝이면 곧바로 다시 뜬다)
        if (ttsActive) StopTts(); // 화살표로 직접 넘기면 낭독은 멈춘다
        string fn = epubPageMode
            ? (delta > 0 ? "window.__clrNext && window.__clrNext();" : "window.__clrPrev && window.__clrPrev();")
            : (delta > 0
                ? "window.chrome.webview.postMessage(JSON.stringify({type:'next-chapter'}));"
                : "window.chrome.webview.postMessage(JSON.stringify({type:'prev-chapter'}));");
        try { await epubView.CoreWebView2.ExecuteScriptAsync(fn); } catch { }
    }

    /// <summary>
    /// 책 자체 CSS가 어두운 배경/밝은 글자(다크 테마)로 박혀 있어도, 배경과 글자는 항상 지금 고른 테마 색이
    /// 되도록 강제로 덮어쓴다. 개별 요소에 걸린 배경색은 투명 처리해서 body의 배경이 그대로 비치게 하고,
    /// 글자색은 전부 body 색을 물려받게 한다.
    /// </summary>
    private string BuildEpubThemeScript()
    {
        string back = Themes.ToCss(theme.Back), fore = Themes.ToCss(theme.Fore), link = Themes.ToCss(theme.Link);
        string border = Themes.ToCss(Themes.Blend(theme.Back, theme.Fore, 0.25f));
        return @"
(function(){
  var s = document.getElementById('__clr_force_light');
  if (!s) { s = document.createElement('style'); s.id = '__clr_force_light'; document.head.appendChild(s); }
  s.textContent =
    'html, body { background-color: " + back + @" !important; color: " + fore + @" !important; }' +
    ' * { background-color: transparent !important; color: inherit !important; border-color: " + border + @" !important; }' +
    ' a, a:visited { color: " + link + @" !important; }';
})();
";
    }

    /// <summary>
    /// 스크롤 모드용: 책 자체 CSS가 화면이 넓을 때 스스로 다단(column) 레이아웃을 켜는 경우(웹소설을
    /// 변환한 epub 등에 흔함)가 있어, 그대로 두면 세로 스크롤이어야 할 글이 여러 단으로 쪼개져
    /// 뒤죽박죽으로 보인다. !important로 늘 단일 컬럼(사실상 컬럼 없음)으로 강제한다. 글자가 화면
    /// 양 끝에 바짝 붙어 읽기 불편하지 않도록 좌우 여백도 여기서 강제한다(EpubPaginateScript의
    /// __CLR_PAGE_PADDING과 같은 값 — 스크롤/페이지 모드를 오가도 여백이 안 달라지게).
    /// </summary>
    private const string EpubResetColumnsScript = @"
(function(){
  var s = document.getElementById('__clr_reset_columns');
  if (!s) { s = document.createElement('style'); s.id = '__clr_reset_columns'; document.head.appendChild(s); }
  s.textContent = 'html, body { column-width: auto !important; column-count: auto !important; }' +
    ' body { padding: %PAD_T%px %PAD_R%px %PAD_B%px %PAD_L%px !important; box-sizing: border-box !important; }' +
    ' img, svg, image { max-width: 100% !important; }';
})();
";

    /// <summary>설정의 좌우/상하 여백을 스크롤 모드 스크립트에 채워 넣는다(페이지 모드 스크립트도 같은 값을 쓴다).</summary>
    private string BuildEpubResetColumnsScript() => EpubResetColumnsScript
        .Replace("%PAD_L%", WebMargins().L.ToString()).Replace("%PAD_R%", WebMargins().R.ToString())
        .Replace("%PAD_T%", WebMargins().T.ToString()).Replace("%PAD_B%", WebMargins().B.ToString());

    /// <summary>
    /// 옵시디언 플러그인과 같은 방식: 본문을 화면 폭만큼의 CSS 다단(column)으로 흘려보내고
    /// translateX로 한 번에 한 칸씩 밀어서 "페이지"처럼 보이게 한다. 화면 안에 다 담기지 않는
    /// 다단 컬럼도 있을 수 있는데, epub 챕터 하나는 원래 크기가 작아서(우리가 txt에서 겪은
    /// "소설 전체가 한 파일" 문제가 애초에 없음) 성능 걱정 없이 이 방식을 그대로 쓸 수 있다.
    /// </summary>
    private const string EpubPaginateScript = @"
(function(){
  document.documentElement.style.margin = '0';
  document.documentElement.style.overflow = 'hidden';
  document.body.style.margin = '0';
  document.body.style.boxSizing = 'border-box';
  document.body.style.height = '100vh';
  document.body.style.transition = 'transform 0.15s ease';

  // 책 자체 CSS가 (특히 웹소설을 변환한 epub들이) 화면이 넓으면 자기 스타일시트로 다단(column)을
  // 스스로 켜는 경우가 있는데, 그게 !important로 박혀 있으면 아래 document.body.style.* 같은 인라인
  // 지정으로는 못 이긴다 — 그러면 우리가 원하는 '창 폭 = 1컬럼'이 아니라 책이 정한 컬럼 수/폭 그대로
  // 여러 단이 한 화면에 다 보여서 글이 뒤죽박죽으로 읽힌다. <style> 태그 + !important로 확실히
  // 덮어써서 항상 우리가 계산한 컬럼 폭 하나로 고정한다.
  var __clrColStyle = document.getElementById('__clr_page_columns');
  if (!__clrColStyle) { __clrColStyle = document.createElement('style'); __clrColStyle.id = '__clr_page_columns'; document.head.appendChild(__clrColStyle); }
  // 이미지가 페이지 경계에서 반으로 잘리지 않게 한다. break-inside만으로는 이미지가 페이지 세로
  // 길이보다 큰 경우 피할 데가 없어서 그대로 잘리므로, 세로 길이를 페이지 높이 안으로 미리 줄여둔다.
  var __clrImgStyle = document.getElementById('__clr_page_img');
  if (!__clrImgStyle) { __clrImgStyle = document.createElement('style'); __clrImgStyle.id = '__clr_page_img'; document.head.appendChild(__clrImgStyle); }

  // 사진 바로 밑에 '(설명)' · '커버접기' 같은 짧은 글이 있으면 사진과 한 덩어리로 묶어 같은 페이지에 두고, 그 글의
  // 높이만큼 사진을 줄여 한 칸(페이지)에 둘이 다 들어가게 한다. 안 그러면 사진이 페이지 높이를 꽉 채워서 글만 다음 페이지로 밀린다.
  // 책마다 구조가 달라서 세 가지를 다 본다:
  //   ① 사진과 설명이 따로 놓인 줄(<font><img></font><br><font>(설명)</font>)  ② 같은 상자 안(<div><img><div>커버접기</div></div>)
  //   ③ 사진 문단 바로 다음 문단(<p><img></p><p>(설명)</p>)
  function __clrCaptionText(t) {
    t = (t || '').replace(/\s+/g, ' ').trim();
    return t.length > 0 && t.length <= 300 && (/^[(（]/.test(t) || /^커버\s*(접기|펴기|보기)/.test(t));
  }
  function __clrFitCaptions(vPad) {
    var vh = window.innerHeight - vPad;
    // 책 맨 앞의 빈 줄바꿈(<br>)은 숨긴다 — 안 그러면 첫 페이지가 줄바꿈만으로 차서 표지가 다음 페이지로 밀린다.
    for (var c = document.body.firstChild; c; c = c.nextSibling) {
      if (c.nodeType === 1 && /^br$/i.test(c.tagName)) c.style.display = 'none';
      else if (c.nodeType === 3 && (c.textContent || '').trim() === '') continue;
      else break;
    }
    // 표지(사진 + 커버접기)는 따로 한 페이지를 쓴다 — 뒤따르는 본문은 다음 페이지부터.
    var covers = document.querySelectorAll('.cover-wrapper');
    for (var cv = 0; cv < covers.length; cv++) {
      // 표지 상자가 인라인 <font> 안에 들어 있으면(블록이 인라인 속에 든 꼴) 다단에서 위치가 어긋나므로 font 밖으로 꺼낸다.
      var pf = covers[cv].parentElement;
      if (pf && pf !== document.body && /^font$/i.test(pf.tagName) && pf.parentElement && pf.children.length === 1 &&
          (pf.textContent || '').replace(covers[cv].textContent || '', '').trim() === '') {
        pf.parentElement.insertBefore(covers[cv], pf);
        pf.style.display = 'none';
      }
      covers[cv].style.breakAfter = 'column';
      covers[cv].style.webkitColumnBreakAfter = 'always';
    }
    var imgs = document.querySelectorAll('img, svg, image');
    for (var n = 0; n < imgs.length; n++) {
      var img = imgs[n];
      img.style.maxHeight = '';
      // 사진만 달랑 들어 있는 바깥 상자(<font><img></font>, <p><img></p> 등)까지 올라가 한 덩어리의 시작으로 삼는다.
      var block = img;
      while (block.parentElement && block.parentElement !== document.body && !block.parentElement.classList.contains('__clr_fig') &&
             block.parentElement.children.length === 1 && (block.parentElement.textContent || '').trim() === '') block = block.parentElement;
      // ①③ 바깥 상자 뒤로 줄바꿈(<br>)과 빈 칸만 건너뛰어 처음 만나는 요소가 설명이면 한 덩어리
      var between = [], cap = null;
      for (var nx = block.nextSibling; nx; nx = nx.nextSibling) {
        if (nx.nodeType === 3) { if ((nx.textContent || '').trim() === '') { between.push(nx); continue; } break; }
        if (nx.nodeType !== 1) { between.push(nx); continue; }
        if (/^br$/i.test(nx.tagName)) { between.push(nx); continue; } // XHTML로 열리면 태그 이름이 소문자(br)라서 대소문자를 가리지 않는다
        cap = nx; break;
      }
      if (cap && !cap.querySelector('img, svg, image') && __clrCaptionText(cap.textContent) && block.parentElement) {
        var wrap = block.parentElement;
        if (!wrap.classList.contains('__clr_fig')) {
          wrap = document.createElement('div');
          wrap.className = '__clr_fig';
          wrap.style.cssText = 'break-inside: avoid; -webkit-column-break-inside: avoid;';
          block.parentElement.insertBefore(wrap, block);
          wrap.appendChild(block);
          for (var b = 0; b < between.length; b++) wrap.appendChild(between[b]);
          wrap.appendChild(cap);
        }
        var cs = getComputedStyle(cap);
        var capH = cap.getBoundingClientRect().height + (parseFloat(cs.marginTop) || 0) + (parseFloat(cs.marginBottom) || 0) + between.length * 4;
        img.style.maxHeight = Math.max(60, vh - capH - 24) + 'px';
        // 사진 줄 아래 빈틈(글줄 높이 · 줄바꿈 등) 때문에 묶음이 아직 한 칸보다 크면 그만큼 사진을 더 줄인다.
        var over1 = wrap.getBoundingClientRect().height - vh;
        if (over1 > 0) img.style.maxHeight = Math.max(60, parseFloat(img.style.maxHeight) - over1 - 6) + 'px';
        continue;
      }
      // ② 설명이 같은 상자 안, 사진 뒤에 이어지는 경우
      var par = img.parentElement;
      if (par && par !== document.body && !par.classList.contains('__clr_fig')) {
        var after = '';
        for (var s = img.nextSibling; s; s = s.nextSibling) after += (s.textContent || '') + ' ';
        if (__clrCaptionText(after)) {
          par.style.breakInside = 'avoid';
          par.style.webkitColumnBreakInside = 'avoid';
          var extra = par.getBoundingClientRect().height - img.getBoundingClientRect().height;
          img.style.maxHeight = Math.max(60, vh - extra - 16) + 'px';
          var over2 = par.getBoundingClientRect().height - vh;
          if (over2 > 0) img.style.maxHeight = Math.max(60, parseFloat(img.style.maxHeight) - over2 - 6) + 'px';
        }
      }
    }
  }

  // 컬럼 폭·이미지 최대 높이·총 페이지 수·페이지당 이동폭을 지금 창 크기 기준으로 다시 잰다.
  // 처음 배치할 때뿐 아니라, 창 크기가 바뀔 때도(아래 resize 리스너) 다시 불러야 한다 — 안 그러면
  // 옛날 창 크기에 맞춰 나뉜 컬럼 폭이 그대로 남아, 지금 창보다 넓은/좁은 컬럼 일부만 잘려 보이면서
  // 서로 다른 컬럼의 내용이 뒤섞여 보인다(창을 키웠다 줄였을 때 겪는 증상).
  function __clrRelayout() {
    // 글자가 화면 양 끝에 바짝 붙어 보이지 않도록 페이지 좌우에 여백(패딩)을 준다. 컬럼 하나짜리
    // 페이지 폭 전체(window.innerWidth)에서 여백만큼을 빼 실제 컬럼 폭으로 쓰고, 그만큼을 컬럼
    // 사이 간격(column-gap)으로 돌려 다음 컬럼도 똑같이 좌우 여백을 갖게 한다(간격의 절반은 이번
    // 컬럼의 오른쪽 여백, 나머지 절반은 다음 컬럼의 왼쪽 여백이 되는 셈) — 그리고 맨 처음 컬럼도
    // 똑같이 왼쪽 여백을 갖도록 body 자체에도 그만큼 왼쪽 패딩을 준다. 이렇게 해야 컬럼 사이 실제
    // 간격(컬럼 폭 + gap)이 여전히 window.innerWidth 그대로라 페이지 이동폭 계산이 안 어긋난다.
    var __clrPadL = %PAD_L%, __clrPadR = %PAD_R%;
    var __clrPadT = %PAD_T%, __clrPadB = %PAD_B%;
    var __clrCols = %COLS%; // 1 = 한쪽, 2 = 두쪽(한 화면에 2단)
    var __clrColWidth = Math.max(100, window.innerWidth / __clrCols - __clrPadL - __clrPadR);
    __clrColStyle.textContent = 'body { column-gap: ' + (__clrPadL + __clrPadR) + 'px !important; column-fill: auto !important;' +
      ' column-width: ' + __clrColWidth + 'px !important; column-count: auto !important;' +
      ' padding-left: ' + __clrPadL + 'px !important; padding-right: ' + __clrPadR + 'px !important;' +
      ' padding-top: ' + __clrPadT + 'px !important; padding-bottom: ' + __clrPadB + 'px !important; }';
    __clrImgStyle.textContent = 'img, svg, image { break-inside: avoid; -webkit-column-break-inside: avoid;' +
      ' max-height: ' + (window.innerHeight - __clrPadT - __clrPadB) + 'px; max-width: 100%; width: auto; height: auto; object-fit: contain; }';
    __clrFitCaptions(__clrPadT + __clrPadB); // 사진 밑 (설명)이 있으면 같은 페이지에 들어가게
    // column-width는 스펙상 최소 힌트일 뿐이라, 브라우저가 실제로 렌더링하는 컬럼 폭은 컨테이너를
    // 정확히 나눠 채우느라 window.innerWidth와 1px 안팎으로 다를 수 있다. 페이지 이동폭을 요청폭
    // 그대로 쓰면 넘어갈 때마다 오차가 누적되어(1페이지만 넘어가도 이미 어긋남) 옆 페이지 끝부분이
    // 살짝 겹쳐 보이므로, 실제 렌더된 컬럼 폭(scrollWidth / 컬럼수)을 구해서 그걸로 이동한다.
    window.__clrTotalPages = __clrCols > 1 ? Math.max(1, Math.ceil(document.body.scrollWidth / window.innerWidth - 0.15)) : Math.max(1, Math.round(document.body.scrollWidth / window.innerWidth));
    window.__clrPageWidth = __clrCols > 1 ? window.innerWidth : document.body.scrollWidth / window.__clrTotalPages;
  }
  __clrRelayout();
  window.__clrRelayout = __clrRelayout; // 글자 크기를 그 자리에서 바꿀 때(EpubKeepPlaceScript) 다단을 다시 나누려고 밖으로 꺼내 둔다
  window.__clrPageIndex = 0;

  window.__clrGoTo = function(i, silent) {
    i = Math.max(0, Math.min(window.__clrTotalPages - 1, i));
    window.__clrPageIndex = i;
    document.body.style.transform = 'translateX(-' + (i * window.__clrPageWidth) + 'px)';
    if (!silent) window.chrome.webview.postMessage(JSON.stringify({ type: 'page', index: i, total: window.__clrTotalPages }));
  };
  window.__clrNext = function() {
    if (window.__clrPageIndex >= window.__clrTotalPages - 1) { window.chrome.webview.postMessage(JSON.stringify({ type: 'next-chapter' })); return; }
    window.__clrGoTo(window.__clrPageIndex + 1);
  };
  window.__clrPrev = function() {
    if (window.__clrPageIndex <= 0) { window.chrome.webview.postMessage(JSON.stringify({ type: 'prev-chapter' })); return; }
    window.__clrGoTo(window.__clrPageIndex - 1);
  };

  // 창 크기가 바뀌면(최대화↔복원, 가장자리로 드래그 등) 다단을 다시 배치하고, 지금 보던 위치에
  // 최대한 가까운 새 페이지로 맞춘다. 드래그 중 연달아 발생하는 resize 이벤트마다 다시 배치하면
  // 버벅이므로 잠깐(150ms) 멎었을 때 한 번만 한다.
  var __clrResizeTimer = null;
  window.addEventListener('resize', function() {
    if (__clrResizeTimer) clearTimeout(__clrResizeTimer);
    __clrResizeTimer = setTimeout(function() {
      __clrResizeTimer = null;
      var oldIndex = window.__clrPageIndex, oldTotal = window.__clrTotalPages;
      __clrRelayout();
      var ratio = oldTotal > 1 ? oldIndex / (oldTotal - 1) : 0;
      window.__clrGoTo(Math.round(ratio * (window.__clrTotalPages - 1)));
    }, 150);
  });

  document.addEventListener('keydown', function(e) {
    if (e.key === 'ArrowRight') { window.__clrNext(); e.preventDefault(); }
    else if (e.key === 'ArrowLeft') { window.__clrPrev(); e.preventDefault(); }
    else if (!e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'PageDown' || (e.key === ' ' && !e.shiftKey))) { window.__clrNext(); e.preventDefault(); }
    else if (!e.ctrlKey && !e.altKey && !e.metaKey && (e.key === 'PageUp' || (e.key === ' ' && e.shiftKey))) { window.__clrPrev(); e.preventDefault(); }
    else if (!e.ctrlKey && ['ArrowUp', 'ArrowDown', 'Home', 'End'].indexOf(e.key) >= 0) e.preventDefault(); // 페이지 방식에는 스크롤이 없다
  });

  // 휠은 스크롤 대신 한 칸에 한 페이지씩 넘긴다(Ctrl+휠 글자 크기 조절은 따로 처리한다). 터치 패닝 · 정밀
  // 휠처럼 작은 값이 연달아 오는 경우를 위해 한 번 넘기고 나면 잠깐 무시한다.
  var __clrWheelLast = 0;
  document.addEventListener('wheel', function(e) {
    if (e.ctrlKey) return;
    e.preventDefault();
    var now = Date.now();
    if (now - __clrWheelLast < 250 || e.deltaY === 0) return;
    __clrWheelLast = now;
    if (e.deltaY > 0) window.__clrNext(); else window.__clrPrev();
  }, { passive: false });

  // 선택 끌기 · 포커스 이동 · 찾기처럼 코드가 스크롤을 일으켜도 항상 제자리로 되돌린다.
  window.addEventListener('scroll', function() { if (window.scrollX || window.scrollY) window.scrollTo(0, 0); });

  // 화면을 셋으로 나눠서, 끌지 않고 콕 찍었을 때만 왼쪽 = 뒤로, 오른쪽 = 앞으로, 가운데 = 무시
  // (가운데 칸 비율은 C#의 TapZone.CenterRatio를 넣어준다 — txt·만화와 같은 기준) — 화살표 대신
  // 터치로 넘기기 편하게. 이미지 위(확대가 대신 열림)·링크 위(그 링크로 이동해야 함)·확대창이 열려
  // 있을 때는 건드리지 않고, 살짝이라도 끌렸으면(텍스트를 선택하려던 것) 무시한다. 더블클릭(단어
  // 선택)의 첫 번째 클릭까지 곧장 넘기면 안 되니, 잠깐 기다렸다가 그사이 두 번째 클릭(e.detail > 1)이
  // 안 왔을 때만(=진짜 한 번 탭한 것으로 판명 났을 때) 넘긴다.
  window.__clrTapEnabled = %TAP_ENABLED%; // 보기 > 읽기 방식(페이지 터치)과 연동(PushEpubTapEnabled가 즉시 갱신도 해준다)
  var __clrTapDownX = 0, __clrTapDownY = 0, __clrTapTimer = null;
  document.addEventListener('mousedown', function(e) { __clrTapDownX = e.clientX; __clrTapDownY = e.clientY; });
  document.addEventListener('mouseup', function(e) {
    if (!window.__clrTapEnabled) return;
    var t = e.target;
    if (t && (t.tagName === 'IMG' || t.closest('a') || t.closest('#__clr_zoom_overlay'))) return;
    if (e.detail > 1) { if (__clrTapTimer) { clearTimeout(__clrTapTimer); __clrTapTimer = null; } return; }
    var dx = e.clientX - __clrTapDownX, dy = e.clientY - __clrTapDownY;
    if (dx * dx + dy * dy > 36) return;
    var side = window.innerWidth * (1 - %TAP_CENTER%) / 2;
    if (e.clientX >= side && e.clientX < window.innerWidth - side) return; // 가운데 칸은 넘기지 않는다
    var goLeft = e.clientX < side;
    if (__clrTapTimer) clearTimeout(__clrTapTimer);
    __clrTapTimer = setTimeout(function() {
      __clrTapTimer = null;
      if (goLeft) window.__clrPrev(); else window.__clrNext();
    }, 350);
  });

  window.__clrGoTo(%START_PAGE%, true);
  window.chrome.webview.postMessage(JSON.stringify({ type: 'page', index: window.__clrPageIndex, total: window.__clrTotalPages }));
})();
";

    /// <summary>스크롤 모드에서: 챕터 바닥까지 다 내리면 자동으로 다음 챕터로 넘어간다. 이미지 한 장이
    /// 화면에 꽉 차는 챕터처럼 스크롤할 내용이 아예 없으면 스크롤 이벤트 자체가 안 일어나서 다음 챕터로
    /// 갈 방법이 없어지는데, 그럴 때는 MainForm에 알려서 화살표 버튼을 예외적으로 보여달라고 한다.</summary>
    private const string EpubScrollWatchScript = @"
(function(){
  var triggered = false;
  window.addEventListener('scroll', function() {
    if (triggered) return;
    if ((window.scrollY + window.innerHeight) >= (document.body.scrollHeight - 4)) {
      triggered = true;
      window.chrome.webview.postMessage(JSON.stringify({ type: 'next-chapter' }));
    }
  });
  if (document.body.scrollHeight <= window.innerHeight + 4) {
    window.chrome.webview.postMessage(JSON.stringify({ type: 'scroll-dead-end' }));
  }
  // 맨 위에서 위로 더 올리면(휠 · ↑ · PageUp) 이전 챕터로 — 바닥에서 다음 챕터로 넘어가는 것의 반대 방향.
  var prevTriggered = false;
  function goPrev() {
    if (prevTriggered || window.scrollY > 0) return;
    prevTriggered = true;
    window.chrome.webview.postMessage(JSON.stringify({ type: 'prev-chapter' }));
  }
  window.addEventListener('wheel', function(e) { if (e.deltaY < 0 && !e.ctrlKey) goPrev(); }, { passive: true });
  window.addEventListener('keydown', function(e) {
    if ((e.key === 'ArrowUp' || e.key === 'PageUp') && !e.ctrlKey && !e.altKey && !e.shiftKey) {
      var t = e.target; if (t && (t.isContentEditable || /^(INPUT|TEXTAREA)$/.test(t.tagName))) return;
      goPrev();
    }
  });
})();
";

    /// <summary>
    /// epub 본문 속 이미지를 클릭하면, 만화/이미지 뷰어(ImageCanvas)와 같은 조작감(휠로 확대·축소,
    /// 드래그로 이동, 더블클릭으로 리셋)의 전체 화면 오버레이를 띄운다. 배율·휠 배수(1.15배)·
    /// 확대 한계(딱 맞는 배율의 0.3~8배)까지 ImageCanvas와 맞췄다 — 두 곳에서 "같은 이미지 보기"로
    /// 느껴지게 하려는 것. 챕터(=페이지 새로고침)마다 새로 심어야 해서 매번 다시 주입한다.
    /// </summary>
    private const string EpubImageZoomScript = @"
(function(){
  if (window.__clrZoomInit) return;
  window.__clrZoomInit = true;

  // 브라우저 기본 동작으로는 img를 누른 채 끌면(마우스가 살짝만 움직여도) 이미지를 통째로 끄는
  // OS 드래그가 시작된다 — 확대창을 열려고 클릭하다 이게 먼저 반응하면 이미지가 제자리에서
  // 벗어나 딸려가는 것처럼 보인다. 본문 이미지든 확대창 이미지든 다 막아서, 이미지는 항상 제자리에
  // 그대로 있고 클릭/휠/드래그는 오직 우리가 만든 확대·이동에만 쓰이게 한다.
  document.addEventListener('dragstart', function(e) {
    if (e.target && e.target.tagName === 'IMG') e.preventDefault();
  }, true);

  var overlay = document.createElement('div');
  overlay.id = '__clr_zoom_overlay';
  overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,0.85);display:none;' +
    'align-items:center;justify-content:center;z-index:2147483647;cursor:grab;overflow:hidden;';
  var img = document.createElement('img');
  img.draggable = false;
  img.style.cssText = 'transform-origin:center center;user-select:none;-webkit-user-drag:none;max-width:none;max-height:none;';
  overlay.appendChild(img);
  document.body.appendChild(overlay);

  var scale = 1, fitScale = 1, tx = 0, ty = 0, dragging = false, startX = 0, startY = 0, startTx = 0, startTy = 0;

  function apply() { img.style.transform = 'translate(' + tx + 'px,' + ty + 'px) scale(' + scale + ')'; }

  function openFor(src, natW, natH) {
    if (!natW || !natH) return;
    img.src = src;
    img.style.width = natW + 'px';
    img.style.height = natH + 'px';
    fitScale = Math.min(window.innerWidth / natW, window.innerHeight / natH, 1); // ImageCanvas의 '꽉차게 보기'와 동일 — 창보다 작은 그림은 확대하지 않는다
    scale = fitScale; tx = 0; ty = 0;
    apply();
    overlay.style.display = 'flex';
  }
  function close() { overlay.style.display = 'none'; img.src = ''; }

  document.addEventListener('click', function(e) {
    var t = e.target;
    if (t && t.tagName === 'IMG' && t !== img) {
      e.preventDefault(); e.stopPropagation();
      openFor(t.currentSrc || t.src, t.naturalWidth, t.naturalHeight);
    }
  }, true);

  // 책 안의 커버접기 줄(.cover-wrapper 아래 .cover-text): 누르면 표지 그림을 접고, 다시 누르면 펼친다.
  // 이 줄은 책이 마크업만 넣어 둔 것이라 읽는 쪽이 동작을 붙여 줘야 한다.
  var coverStyle = document.createElement('style');
  coverStyle.textContent = '.cover-text{cursor:pointer;user-select:none;-webkit-user-select:none}.cover-text i.icon::after{content:\x27\\25B2\x27;font-style:normal;font-size:0.8em;display:inline-block}';
  document.head.appendChild(coverStyle);
  document.addEventListener('click', function(e) {
    var t = e.target && e.target.closest ? e.target.closest('.cover-text') : null;
    if (!t) return;
    var wrap = t.closest('.cover-wrapper') || t.parentNode;
    var pic = wrap.querySelector('.cover-img, img');
    var folded = wrap.getAttribute('data-clr-folded') === '1';
    if (pic) pic.style.display = folded ? '' : 'none';
    wrap.setAttribute('data-clr-folded', folded ? '0' : '1');
    var first = t.firstChild;
    if (first && first.nodeType === 3) first.nodeValue = folded ? '커버접기 ' : '커버펼치기 ';
    var arrow = t.querySelector('i');
    if (arrow) arrow.style.transform = folded ? '' : 'rotate(180deg)';
    e.preventDefault(); e.stopPropagation();
  }, true);

  overlay.addEventListener('click', function(e) { if (e.target === overlay) close(); });
  img.addEventListener('dblclick', function(e) { e.preventDefault(); scale = fitScale; tx = 0; ty = 0; apply(); });
  document.addEventListener('keydown', function(e) {
    if (overlay.style.display !== 'none' && e.key === 'Escape') close();
  });

  overlay.addEventListener('wheel', function(e) {
    if (overlay.style.display === 'none') return;
    e.preventDefault();
    var factor = e.deltaY < 0 ? 1.15 : (1 / 1.15); // ImageCanvas(OnMouseWheel)와 같은 배율
    scale = Math.max(fitScale * 0.3, Math.min(fitScale * 8, scale * factor));
    apply();
  }, { passive: false });

  overlay.addEventListener('mousedown', function(e) {
    if (overlay.style.display === 'none') return;
    dragging = true; startX = e.clientX; startY = e.clientY; startTx = tx; startTy = ty;
    overlay.style.cursor = 'grabbing';
  });
  window.addEventListener('mousemove', function(e) {
    if (!dragging) return;
    tx = startTx + (e.clientX - startX); ty = startTy + (e.clientY - startY);
    apply();
  });
  window.addEventListener('mouseup', function() { dragging = false; overlay.style.cursor = 'grab'; });
})();
";
}
