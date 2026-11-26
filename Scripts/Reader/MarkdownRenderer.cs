using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Cafeum;

/// <summary>
/// 마크다운 → HTML (외부 라이브러리 없이 자주 쓰는 모양만): 제목(#) · 문단 · 굵게 · 기울임 · 취소선 · 인라인 코드 · 링크 · 그림 ·
/// 목록(번호 · 점 · 중첩 · 할 일) · 인용(>) · 코드 블록(```) · 가로줄 · 표(|). 글에 섞인 HTML은 모두 글자로 보여 준다(스크립트가 실행되지 않게).
/// 맨 위 블록마다 원본 글자 위치(offset)를 같이 돌려줘서, 읽던 자리 · 찾기를 원본 위치로 주고받을 수 있다.
/// </summary>
internal static class MarkdownRenderer
{
    private readonly record struct Line(string Text, int Off);

    /// <summary>text[start, end)를 맨 위 블록 단위로 렌더링한다: (블록이 시작하는 원본 위치, HTML).</summary>
    public static List<(int Offset, string Html)> Render(string text, int start, int end)
    {
        var lines = new List<Line>();
        int pos = start;
        while (pos < end)
        {
            int nl = text.IndexOf('\n', pos, end - pos);
            int lineEnd = nl < 0 ? end : nl;
            int contentEnd = lineEnd > pos && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            lines.Add(new Line(text.Substring(pos, contentEnd - pos), pos));
            pos = nl < 0 ? end : nl + 1;
        }
        var result = new List<(int, string)>();
        Blocks(lines, (off, html) => result.Add((off, html)));
        return result;
    }

    // ───────── 블록 ─────────

    private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex HeadingEmpty = new(@"^ {0,3}(#{1,6})[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex Fence = new(@"^( {0,3})(`{3,}|~{3,})[ \t]*([^`]*)$", RegexOptions.Compiled);
    private static readonly Regex Item = new(@"^(\s*)([-*+]|\d{1,9}[.)])[ \t]+(.*)$", RegexOptions.Compiled);
    private static readonly Regex TableSep = new(@"^\s*\|?\s*:?-{1,}:?\s*(\|\s*:?-{1,}:?\s*)*\|?\s*$", RegexOptions.Compiled);

    private static int Indent(string s) { int n = 0; foreach (char c in s) { if (c == ' ') n++; else if (c == '\t') n += 4; else break; } return n; }

    private static bool StartsBlock(string s) =>
        Heading.IsMatch(s) || Rule.IsMatch(s) || Fence.IsMatch(s) || s.TrimStart().StartsWith('>') || Item.IsMatch(s) && Indent(s) < 4;

    private static void Blocks(List<Line> lines, Action<int, string> emit)
    {
        int i = 0;
        while (i < lines.Count)
        {
            string s = lines[i].Text;
            int off = lines[i].Off;
            if (string.IsNullOrWhiteSpace(s)) { i++; continue; }

            // 코드 블록
            var fm = Fence.Match(s);
            if (fm.Success)
            {
                string marker = fm.Groups[2].Value;
                var sb = new StringBuilder();
                int j = i + 1;
                for (; j < lines.Count; j++)
                {
                    string t = lines[j].Text.TrimStart();
                    if (t.StartsWith(marker, StringComparison.Ordinal) && t.TrimEnd().All(c => c == marker[0])) { j++; break; }
                    sb.Append(WebUtility.HtmlEncode(lines[j].Text)).Append('\n');
                }
                emit(off, "<pre><code>" + sb + "</code></pre>");
                i = j; continue;
            }

            // 제목
            var hm = Heading.Match(s);
            if (hm.Success) { int n = hm.Groups[1].Length; emit(off, $"<h{n}>{Inline(hm.Groups[2].Value)}</h{n}>"); i++; continue; }
            var he = HeadingEmpty.Match(s);
            if (he.Success) { int n = he.Groups[1].Length; emit(off, $"<h{n}><br></h{n}>"); i++; continue; }

            // 가로줄
            if (Rule.IsMatch(s)) { emit(off, "<hr>"); i++; continue; }

            // 표: 머리 줄 + 구분 줄(---|---)
            if (s.Contains('|') && i + 1 < lines.Count && lines[i + 1].Text.Contains('-') && TableSep.IsMatch(lines[i + 1].Text) && (lines[i + 1].Text.Contains('|') || SplitCells(s).Count > 1))
            {
                var head = SplitCells(s);
                var aligns = SplitCells(lines[i + 1].Text).Select(c => { string t = c.Trim(); bool l = t.StartsWith(':'), r = t.EndsWith(':'); return l && r ? "center" : r ? "right" : l ? "left" : ""; }).ToList();
                var sb = new StringBuilder("<table><thead><tr>");
                for (int c = 0; c < head.Count; c++) sb.Append(Cell("th", head[c], aligns, c));
                sb.Append("</tr></thead><tbody>");
                int j = i + 2;
                for (; j < lines.Count && !string.IsNullOrWhiteSpace(lines[j].Text) && lines[j].Text.Contains('|'); j++)
                {
                    var cells = SplitCells(lines[j].Text);
                    sb.Append("<tr>");
                    for (int c = 0; c < head.Count; c++) sb.Append(Cell("td", c < cells.Count ? cells[c] : "", aligns, c));
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
                emit(off, sb.ToString());
                i = j; continue;
            }

            // 인용
            if (s.TrimStart().StartsWith('>'))
            {
                var inner = new List<Line>();
                int j = i;
                for (; j < lines.Count; j++)
                {
                    string t = lines[j].Text.TrimStart();
                    if (t.StartsWith('>')) inner.Add(new Line(t[1..].StartsWith(' ') ? t[2..] : t[1..], -1));
                    else if (!string.IsNullOrWhiteSpace(lines[j].Text) && inner.Count > 0 && !StartsBlock(lines[j].Text)) inner.Add(new Line(lines[j].Text, -1)); // 이어지는 줄
                    else break;
                }
                var sb = new StringBuilder("<blockquote>");
                Blocks(inner, (_, h) => sb.Append(h));
                emit(off, sb.Append("</blockquote>").ToString());
                i = j; continue;
            }

            // 목록
            var im = Item.Match(s);
            if (im.Success && Indent(s) < 4)
            {
                i = List(lines, i, out string html);
                emit(off, html);
                continue;
            }

            // 문단: 다른 블록이 시작하기 전까지(줄바꿈은 그대로 살린다)
            {
                var para = new List<string> { s };
                int j = i + 1;
                for (; j < lines.Count && !string.IsNullOrWhiteSpace(lines[j].Text) && !StartsBlock(lines[j].Text); j++) para.Add(lines[j].Text);
                emit(off, "<p>" + string.Join("<br>\n", para.Select(p => Inline(p.Trim()))) + "</p>");
                i = j;
            }
        }
    }

    /// <summary>lines[i]부터 시작하는 목록 한 덩이를 HTML로 만든다. 다음에 읽을 줄 번호를 돌려준다.</summary>
    private static int List(List<Line> lines, int i, out string html)
    {
        var first = Item.Match(lines[i].Text);
        int baseIndent = Indent(first.Groups[1].Value);
        bool ordered = char.IsDigit(first.Groups[2].Value[0]);
        var items = new List<List<Line>>();
        int j = i;
        while (j < lines.Count)
        {
            string s = lines[j].Text;
            var m = Item.Match(s);
            if (m.Success && Indent(m.Groups[1].Value) == baseIndent)
            {
                if (char.IsDigit(m.Groups[2].Value[0]) != ordered) break; // 점 목록 → 번호 목록(또는 반대)이 되면 새 목록
                items.Add(new List<Line> { new(m.Groups[3].Value, -1) });
                j++;
            }
            else if (items.Count > 0 && (string.IsNullOrWhiteSpace(s) ? j + 1 < lines.Count && !string.IsNullOrWhiteSpace(lines[j + 1].Text) && Indent(lines[j + 1].Text) > baseIndent : Indent(s) > baseIndent))
            {
                // 이 항목에 딸린 줄(들여쓴 하위 목록 · 이어지는 문장 · 항목 사이의 빈 줄)
                int cut = Math.Min(Indent(s), baseIndent + 2);
                items[^1].Add(new Line(string.IsNullOrWhiteSpace(s) ? "" : DropIndent(s, cut), -1));
                j++;
            }
            else break;
        }
        var sb = new StringBuilder(ordered ? "<ol>" : "<ul>");
        foreach (var it in items)
        {
            string firstText = it[0].Text;
            string box = "";
            var tm = Regex.Match(firstText, @"^\[([ xX])\][ \t]+(.*)$");
            if (tm.Success) { box = tm.Groups[1].Value == " " ? "☐ " : "☑ "; it[0] = new Line(tm.Groups[2].Value, -1); }
            var inner = new StringBuilder();
            var parts = new List<string>();
            Blocks(it, (_, h) => parts.Add(h));
            // 문단이 하나뿐이면 <p>를 벗겨 촘촘하게(점 바로 옆에 글이 오게)
            if (parts.Count > 0 && parts[0].StartsWith("<p>") && parts[0].EndsWith("</p>")) parts[0] = parts[0][3..^4];
            foreach (var p in parts) inner.Append(p);
            sb.Append("<li>").Append(box).Append(inner).Append("</li>");
        }
        sb.Append(ordered ? "</ol>" : "</ul>");
        html = sb.ToString();
        return Math.Max(j, i + 1);
    }

    private static string DropIndent(string s, int n)
    {
        int k = 0, w = 0;
        while (k < s.Length && w < n && (s[k] == ' ' || s[k] == '\t')) { w += s[k] == '\t' ? 4 : 1; k++; }
        return s[k..];
    }

    private static List<string> SplitCells(string row)
    {
        string t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|")) t = t[..^1];
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool code = false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cur.Append('|'); i++; }
            else if (c == '`') { code = !code; cur.Append(c); }
            else if (c == '|' && !code) { cells.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString().Trim());
        return cells;
    }

    private static string Cell(string tag, string text, List<string> aligns, int c) =>
        $"<{tag}{(c < aligns.Count && aligns[c].Length > 0 ? $" style=\"text-align:{aligns[c]}\"" : "")}>{Inline(text)}</{tag}>";

    // ───────── 인라인 ─────────

    private static readonly Regex CodeSpan = new(@"(`+)(.+?)\1(?!`)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Img = new(@"!\[([^\]]*)\]\(([^)\s]+)(?:\s+&quot;[^&]*&quot;)?\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)\s]+)(?:\s+&quot;[^&]*&quot;)?\)", RegexOptions.Compiled);
    private static readonly Regex AutoLink = new(@"&lt;(https?://[^\s&]+)&gt;", RegexOptions.Compiled);
    private static readonly Regex BoldItalic = new(@"\*\*\*(?=\S)(.+?)(?<=\S)\*\*\*", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"\*\*(?=\S)(.+?)(?<=\S)\*\*|(?<!\w)__(?=\S)(.+?)(?<=\S)__(?!\w)", RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"\*(?=\S)(.+?)(?<=\S)\*|(?<!\w)_(?=\S)(.+?)(?<=\S)_(?!\w)", RegexOptions.Compiled);
    private static readonly Regex Strike = new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled);

    private static bool SafeUrl(string url)
    {
        string u = WebUtility.HtmlDecode(url).Trim();
        if (u.StartsWith("#") || u.StartsWith("/") || u.StartsWith("./") || u.StartsWith("../")) return true;
        return u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || !u.Contains(':'); // 상대 경로
    }

    /// <summary>한 줄 안의 모양(굵게 · 기울임 · 코드 · 링크…). 글에 든 HTML은 글자로 바꿔 보여 준다.</summary>
    internal static string Inline(string s)
    {
        var stash = new List<string>();
        string Hold(string html) { stash.Add(html); return "\u0001" + (stash.Count - 1) + "\u0002"; }
        // 1) 인라인 코드를 먼저 빼 둔다(안의 * _ 는 모양이 아니다)
        s = CodeSpan.Replace(s, m => Hold("<code>" + WebUtility.HtmlEncode(m.Groups[2].Value.Trim()) + "</code>"));
        // 2) \* \_ 같은 이스케이프는 글자 그대로
        s = Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!|~>])", m => Hold(WebUtility.HtmlEncode(m.Groups[1].Value)));
        // 3) 나머지 글자의 HTML 기호는 모두 글자로
        s = WebUtility.HtmlEncode(s);
        s = Img.Replace(s, m => SafeUrl(m.Groups[2].Value) ? Hold($"<img src=\"{m.Groups[2].Value}\" alt=\"{m.Groups[1].Value}\" style=\"max-width:100%\">") : m.Value);
        s = Link.Replace(s, m => SafeUrl(m.Groups[2].Value) ? Hold($"<a href=\"{m.Groups[2].Value}\">") + m.Groups[1].Value + Hold("</a>") : m.Value);
        s = AutoLink.Replace(s, m => Hold($"<a href=\"{m.Groups[1].Value}\">{m.Groups[1].Value}</a>"));
        s = BoldItalic.Replace(s, "<strong><em>$1</em></strong>");
        s = Bold.Replace(s, m => "<strong>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</strong>");
        s = Italic.Replace(s, m => "<em>" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "</em>");
        s = Strike.Replace(s, "<del>$1</del>");
        return Regex.Replace(s, "\u0001(\\d+)\u0002", m => stash[int.Parse(m.Groups[1].Value)]);
    }
}
