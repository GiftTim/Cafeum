using System.Net;
using System.Text;

namespace Cafeum;

/// <summary>
/// txt를 epub처럼 WebView2로 보여주려고, 글을 작은 HTML 문서 여러 개로 나눠 임시 폴더에 써 둔 "가상 epub".
/// 메모장 같은 TextBox로는 줄 간격 · 문단 간격 · 다단 페이지를 못 주므로, epub과 같은 웹 화면(페이지 넘김 ·
/// 테마 · 글꼴 · 낭독 · 이미지 확대 스크립트)을 그대로 같이 쓰게 하려는 것이다.
/// 한 문서가 너무 크면 다단 배치(CSS column)가 느려지므로, 챕터(TxtChapterSplitter)를 기본 단위로 하되
/// 큰 챕터는 MaxChunk 글자 안쪽에서 줄 경계로 다시 자른다.
/// 각 줄은 &lt;p data-o="전체 글 기준 시작 위치"&gt;로 쓰고 white-space: pre-wrap으로 공백까지 그대로 살린다 —
/// 그래서 문단 안의 글자 위치가 원본 글자 위치와 1:1로 맞아, 읽던 자리 · 찾기를 원본 위치로 주고받을 수 있다.
/// </summary>
public sealed class TxtWebBook
{
    private const int MaxChunk = 40000;

    public EpubBook Book { get; }
    /// <summary>각 문서가 맡은 전체 글 기준 [Start, End) 구간. Book.Chapters와 같은 순서·개수.</summary>
    public IReadOnlyList<(int Start, int End)> Ranges { get; }
    public int TextLength { get; }
    /// <summary>문서들을 써 둔 임시 폴더(소설 본문의 복사본이 들어 있다). 다 보고 나면 TempCleanup으로 지운다.</summary>
    public string Dir { get; }

    private TxtWebBook(EpubBook book, List<(int, int)> ranges, int textLength, string dir)
    {
        Book = book;
        Ranges = ranges;
        TextLength = textLength;
        Dir = dir;
    }

    /// <summary>text는 줄바꿈이 \r\n으로 정리된 전체 글(ShowLoadedText 참고), chapters는 그 글을 나눈 챕터 목록.</summary>
    public static TxtWebBook Build(string text, IReadOnlyList<TxtChapter> chapters, string title, string sourcePath, bool autoBreak = false)
    {
        // 만들 때마다 새 폴더를 쓴다(같은 글을 두 창에서 열어도, 글꼴을 바꿔 다시 만들어도 서로 지우지 않게). 옛 폴더는 부른 쪽이 지운다.
        string dir = Path.Combine(Path.GetTempPath(), "CafeumTxt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        TempCleanup.Hold(dir);

        var parts = new List<(string Name, int Start, int End)>();
        foreach (var ch in chapters)
        {
            int start = ch.Start, part = 1;
            while (start < ch.End)
            {
                int end = ch.End;
                if (end - start > MaxChunk)
                {
                    // 글자 수 한도 안쪽의 마지막 줄바꿈에서 자른다(줄 중간에서 끊기지 않게). 한 줄이 한도보다 길면 그냥 한도에서.
                    int nl = text.LastIndexOf('\n', start + MaxChunk - 1, MaxChunk);
                    end = nl > start ? nl + 1 : start + MaxChunk;
                    if (end < ch.End && char.IsHighSurrogate(text[end - 1])) end++; // 이모지 같은 두 칸짜리 글자의 가운데서 끊지 않는다
                }
                parts.Add((part == 1 ? ch.Name : $"{ch.Name} ({part})", start, end));
                start = end;
                part++;
            }
        }
        if (parts.Count == 0) parts.Add((title, 0, text.Length)); // 빈 파일도 문서 하나는 있어야 화면이 뜬다

        bool md = LibraryStore.IsMarkdown(sourcePath);
        var epubChapters = new List<EpubChapter>();
        var ranges = new List<(int, int)>();
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                var (name, start, end) = parts[i];
                string file = Path.Combine(dir, $"{i + 1:D4}.html");
                File.WriteAllText(file, BuildHtml(text, start, end, name, autoBreak, md), new UTF8Encoding(false));
                epubChapters.Add(new EpubChapter(name, file));
                ranges.Add((start, end));
            }
        }
        catch
        {
            TempCleanup.DeleteQuietly(dir); // 디스크가 가득 찬 경우 등 — 쓰다 만 본문 복사본을 임시 폴더에 남기지 않는다
            throw;
        }
        return new TxtWebBook(new EpubBook { Title = title, Chapters = epubChapters }, ranges, text.Length, dir);
    }

    /// <summary>offset(전체 글 기준)이 들어 있는 문서 번호.</summary>
    public int ChunkAt(int offset)
    {
        int lo = 0, hi = Ranges.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Ranges[mid].Start <= offset) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>
    /// 한 줄(문단) [start, end)을 문장 단위로 나눈다. 문장 끝 = 마침표 · 물음표 · 느낌표 · … 뒤에 닫는 따옴표 · 괄호가 붙을 수 있고, 그 다음이 공백이거나 줄 끝일 때
    /// (3.14 · a.b 처럼 바로 이어지는 마침표는 끊지 않는다). 공백은 앞 문장 끝에 붙여 둬서 글자 위치가 원본과 1:1로 맞는다.
    /// </summary>
    private static List<(int Start, int End)> SplitSentences(string text, int start, int end)
    {
        var list = new List<(int, int)>();
        int segStart = start, i = start;
        while (i < end)
        {
            char c = text[i];
            if (c is '.' or '!' or '?' or '…' or '。' or '！' or '？')
            {
                int j = i + 1;
                while (j < end && text[j] is '.' or '!' or '?' or '…' or '。' or '！' or '？') j++;                    // 문장 끝 기호 연속(…… · ?!)
                while (j < end && text[j] is '"' or '\'' or '”' or '’' or '」' or '』' or ')' or '）' or ']' or '〕') j++; // 닫는 따옴표 · 괄호
                if (j >= end || char.IsWhiteSpace(text[j]))
                {
                    while (j < end && char.IsWhiteSpace(text[j])) j++;
                    if (j > segStart) list.Add((segStart, j));
                    segStart = j;
                }
                i = Math.Max(j, i + 1);
                continue;
            }
            i++;
        }
        if (segStart < end) list.Add((segStart, end));
        if (list.Count == 0) list.Add((start, end));
        return list;
    }
    private static string BuildHtml(string text, int start, int end, string name, bool autoBreak, bool md)
    {
        var sb = new StringBuilder((end - start) * 6 / 5 + 4096);
        sb.Append("<!DOCTYPE html><html lang=\"ko\"><head><meta charset=\"utf-8\"><title>")
          .Append(WebUtility.HtmlEncode(name))
          .Append("</title><style>")
          .Append("html,body{margin:0}")
          .Append("body.clr-txt p{margin:0;white-space:pre-wrap;word-break:keep-all;overflow-wrap:anywhere}");
        if (md) sb.Append(MarkdownCss);
        sb
          .Append("</style><script>").Append(HelperScript).Append("</script></head><body class=\"clr-txt\">\n");

        if (md)
        {
            // 마크다운: 블록(제목 · 문단 · 표 · 목록 …)마다 원본 위치를 달아 꾸민 HTML로. 읽던 자리 · 찾기는 블록 단위로 맞춘다.
            foreach (var (off, html) in MarkdownRenderer.Render(text, start, end))
                sb.Append("<div class=\"md\" data-o=\"").Append(off).Append("\">").Append(html).Append("</div>\n");
            if (start >= end) sb.Append("<div class=\"md\" data-o=\"").Append(start).Append("\"><p><br></p></div>\n");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        int pos = start;
        while (pos < end)
        {
            int nl = text.IndexOf('\n', pos, end - pos);
            int lineEnd = nl < 0 ? end : nl;
            int contentEnd = lineEnd > pos && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            if (autoBreak && contentEnd > pos)
            {
                // 자동 줄바꿈: 한 줄(문단)을 문장 단위로 쪼개 각각 새 줄로. 위치(data-o)는 원본 글자 위치 그대로다.
                foreach (var (s, e) in SplitSentences(text, pos, contentEnd))
                    sb.Append("<p data-o=\"").Append(s).Append("\">").Append(WebUtility.HtmlEncode(text.Substring(s, e - s))).Append("</p>\n");
            }
            else
            {
                sb.Append("<p data-o=\"").Append(pos).Append("\">");
                if (contentEnd > pos) sb.Append(WebUtility.HtmlEncode(text.Substring(pos, contentEnd - pos)));
                else sb.Append("<br>"); // 빈 줄도 한 줄 높이를 차지하게
                sb.Append("</p>\n");
            }
            pos = nl < 0 ? end : nl + 1;
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// 문서마다 심어 두는 도우미: 원본 글자 위치(data-o 기준)로 화면 위치를 찾고 옮긴다.
    /// - __clrTxtTop(): 지금 화면에 보이는 첫 줄의 원본 위치
    /// - __clrTxtGo(off): 그 위치가 있는 줄이 보이게(페이지 방식이면 그 페이지로, 스크롤이면 화면 맨 위로)
    /// - __clrTxtSel(start, len): 찾은 글자를 표시하고 보이게
    /// 페이지 방식의 __clrGoTo · __clrPageWidth는 epub용 다단 스크립트(EpubPaginateScript)가 나중에 심는다.
    /// </summary>
    /// <summary>마크다운 보기 모양(글 색은 읽기 화면의 테마를 따르고, 선 · 바탕은 반투명으로 어느 테마에서나 어울리게).</summary>
    private const string MarkdownCss =
        "body.clr-txt .md{margin:0 0 .5em}body.clr-txt .md p{white-space:normal;margin:0}" +
        "body.clr-txt .md h1,body.clr-txt .md h2,body.clr-txt .md h3,body.clr-txt .md h4,body.clr-txt .md h5,body.clr-txt .md h6{margin:.7em 0 .3em;font-weight:700;line-height:1.35}" +
        "body.clr-txt .md h1{font-size:1.75em}body.clr-txt .md h2{font-size:1.45em}body.clr-txt .md h3{font-size:1.25em}body.clr-txt .md h4,body.clr-txt .md h5,body.clr-txt .md h6{font-size:1.08em}" +
        "body.clr-txt .md blockquote{margin:.3em 0;padding:.2em .9em;border-left:3px solid rgba(127,127,127,.6);opacity:.85}body.clr-txt .md blockquote p{margin:.2em 0}" +
        "body.clr-txt .md pre{margin:.4em 0;padding:.7em .9em;border-radius:6px;background:rgba(127,127,127,.14);white-space:pre-wrap;overflow-wrap:anywhere}" +
        "body.clr-txt .md code{font-family:Consolas,D2Coding,monospace;font-size:.92em;background:rgba(127,127,127,.16);padding:0 .25em;border-radius:4px}body.clr-txt .md pre code{background:none;padding:0}" +
        "body.clr-txt .md table{border-collapse:collapse;margin:.5em 0;max-width:100%}body.clr-txt .md th,body.clr-txt .md td{border:1px solid rgba(127,127,127,.5);padding:.3em .7em;text-align:left}body.clr-txt .md th{background:rgba(127,127,127,.15)}" +
        "body.clr-txt .md hr{border:0;border-top:1px solid rgba(127,127,127,.5);margin:1em 0}" +
        "body.clr-txt .md ul,body.clr-txt .md ol{margin:.2em 0;padding-left:1.6em}body.clr-txt .md li{margin:.1em 0}body.clr-txt .md li p{display:inline}" +
        "body.clr-txt .md a{color:inherit;text-decoration:underline}body.clr-txt .md img{max-width:100%}body.clr-txt .md del{opacity:.7}";

    private const string HelperScript = @"
(function(){
  function paras() { return document.querySelectorAll('[data-o]'); }
  function paraAt(off) {
    var ps = paras(), lo = 0, hi = ps.length - 1, ans = ps.length ? 0 : -1;
    while (lo <= hi) { var mid = (lo + hi) >> 1; if (+ps[mid].dataset.o <= off) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
    return ans < 0 ? null : ps[ans];
  }
  function show(rect) {
    if (window.__clrGoTo && window.__clrTotalPages !== undefined) {
      var tx = 0;
      try { tx = new DOMMatrix(getComputedStyle(document.body).transform).m41; } catch (e) {}
      var page = Math.floor((rect.left - tx + 1) / window.__clrPageWidth);
      if (page !== window.__clrPageIndex) window.__clrGoTo(page);
    } else {
      window.scrollTo(0, window.scrollY + rect.top - Math.min(40, window.innerHeight * 0.1));
    }
  }
  window.__clrTxtTop = function() {
    var ps = paras(), vw = window.innerWidth, vh = window.innerHeight;
    for (var i = 0; i < ps.length; i++) {
      var b = ps[i].getBoundingClientRect();
      if (b.bottom > 0 && b.top < vh && b.right > 0 && b.left < vw) return +ps[i].dataset.o;
    }
    return ps.length ? +ps[0].dataset.o : 0;
  };
  window.__clrTxtGo = function(off) {
    var p = paraAt(off);
    if (p) show(p.getBoundingClientRect());
  };
  window.__clrTxtSel = function(start, len) {
    var p = paraAt(start);
    if (!p) return;
    var node = p.firstChild, r = document.createRange();
    if (node && node.nodeType === 3) {
      var a = Math.max(0, Math.min(node.nodeValue.length, start - (+p.dataset.o)));
      r.setStart(node, a);
      r.setEnd(node, Math.min(node.nodeValue.length, a + len));
    } else r.selectNodeContents(p);
    var st = document.getElementById('__clr_find_style');
    if (!st) { st = document.createElement('style'); st.id = '__clr_find_style'; st.textContent = '::highlight(clr-find) { background-color: #ff9632; color: #000000; }'; document.head.appendChild(st); }
    try { CSS.highlights.set('clr-find', new Highlight(r)); } catch (e) {}
    show(r.getBoundingClientRect());
  };
})();
";
}
