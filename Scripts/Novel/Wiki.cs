using System.Drawing.Drawing2D;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Cafeum;

/// <summary>
/// 위키 문서 한 개 = 작품 폴더 안의 <c>wiki\제목.md</c>(새로 만드는 문서). 예전에 만든 <c>제목.txt</c>도 그대로 읽는다. 메모장이나 마크다운 편집기로 열어도 그대로 읽히는 평범한 글이다.
/// 파일 모양은 두 가지다. <c>.md</c>는 마크다운(맨 위 <c>---</c> 머리말에 속성, 본문 제목은 <c>## </c> · <c>### </c>)으로 저장하고,
/// <c>.txt</c>는 예전 모양(아래)을 그대로 쓴다. 읽을 때는 확장자와 상관없이 두 모양을 모두 알아본다(예전 모양의 .md도 다음 저장 때 마크다운으로 바뀐다).
/// 화면의 편집기(HeadingEditor)는 어느 모양이든 같은 "번호 붙은 항목" 글로 다루고, 파일로 읽고 쓸 때만 여기서 바꾼다.
/// 예전 모양:
/// 구분선(────) 위는 속성(카테고리 · 설명 · 분류 · 중요도 · 다른 이름, 그 밖의 "이름: 값"은 모두 속성 상자 항목),
/// 아래는 본문. 본문의 항목은 "1. 제목", 부항목은 "1-1. 제목", 그 아래는 "1-1-1. 제목"으로 적는다(저장할 때 번호를 다시 매긴다).
/// <code>
/// 카테고리: 등장인물
/// 설명: 카페 주인. 밤에만 문을 연다
/// 분류: 주연
/// 다른 이름: 사장님
/// 나이: 29
/// ────────────
/// 1. 성격
/// 말수가 적다.
///
/// 1-1. 버릇
/// 컵을 세 번 닦는다.
/// </code>
/// </summary>
internal sealed class WikiDoc
{
    public const string Separator = "────────────";
    private static readonly string[] FixedKeys = { "카테고리", "설명", "요약", "분류", "태그", "중요도", "다른 이름", "별칭", "속성 색" }; // 두 모양의 고정 속성 이름(같은 이름의 사용자 속성은 쓸 때 이름 뒤에 공백을 붙여 구분한다)

    public string Path { get; set; } = "";
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Category { get; set; } = "none";
    public string Summary { get; set; } = "";
    /// <summary>분류: 이 작품의 위키에서 고른 분류 이름들(화면에서 목록 중에서 고른다).</summary>
    public List<string> Tags { get; set; } = new();
    /// <summary>태그: 소설 메인의 태그처럼 자유롭게 적는 이름표.</summary>
    public List<string> Labels { get; set; } = new();
    public int Importance { get; set; } = 1;
    /// <summary>속성 상자의 색(#RRGGBB). 비어 있으면 기본색.</summary>
    public string AttrColor { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public List<(string Key, string Value)> Attributes { get; set; } = new();
    /// <summary>본문(번호 붙은 항목 줄 포함, 파일에 적히는 그대로). 줄바꿈은 \r\n.</summary>
    public string Body { get; set; } = "";

    private static bool IsSeparator(string line) { string l = line.Trim(); return l.Length >= 3 && l.All(c => c == '─' || c == '━'); }

    public static WikiDoc Parse(string path, string text)
    {
        var d = new WikiDoc { Path = path };
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // 마크다운 모양: 첫 줄이 "---"이고 다음 "---"까지가 머리말(속성)
        if (lines.Length > 1 && lines[0].TrimStart('\uFEFF').Trim() == "---")
        {
            int end = Array.FindIndex(lines, 1, l => l.Trim() == "---");
            if (end > 0)
            {
                for (int i = 1; i < end; i++) ApplyProperty(d, lines[i], yaml: true);
                var rest = lines[(end + 1)..].ToList();
                if (rest.Count > 0 && rest[0].Trim().Length == 0) rest.RemoveAt(0); // 머리말 뒤 빈 줄 하나는 모양 맞추기용
                d.Body = FromMarkdownBody(string.Join("\r\n", rest));
                return d;
            }
        }
        // 예전 모양: 구분선(────) 위가 속성, 아래가 본문
        int sep = Array.FindIndex(lines, IsSeparator);
        if (sep >= 0)
        {
            for (int i = 0; i < sep; i++) ApplyProperty(d, lines[i], yaml: false);
            d.Body = string.Join("\r\n", lines[(sep + 1)..]);
        }
        else d.Body = string.Join("\r\n", lines);
        return d;
    }

    /// <summary>속성 한 줄("이름: 값")을 문서에 넣는다. 마크다운 머리말(yaml)은 "요약" · "별칭"과 따옴표 값도 받는다.</summary>
    private static void ApplyProperty(WikiDoc d, string raw, bool yaml)
    {
        int c = raw.IndexOfAny(new[] { ':', '：' });
        if (c <= 0) return;
        string key = raw[..c].Trim(), val = raw[(c + 1)..].Trim();
        // 고정 속성 이름과 겹치는 사용자 속성은 쓸 때 이름 뒤에 공백을 붙여 둔다 — 그런 줄은 속성 상자 항목으로 읽는다
        if (FixedKeys.Contains(key) && raw[..c].EndsWith(' ')) { d.Attributes.Add((key, yaml ? Unquote(val) : val)); return; }
        if (yaml && (key == "요약" || key == "설명")) { d.Summary = Unquote(val).Replace("\\n", "\n"); return; }
        if (yaml && (key == "별칭" || key == "다른 이름")) { d.Aliases = ParseList(val); return; }
        switch (key)
        {
            case "카테고리": d.Category = WikiCategory.Find(yaml ? Unquote(val) : val).Key; break;
            case "설명": d.Summary = val.Replace("\\n", "\n"); break;
            case "분류": d.Tags = yaml ? ParseList(val) : SplitList(val); break;
            case "태그": d.Labels = yaml ? ParseList(val) : SplitList(val); break;
            case "중요도": if (int.TryParse(yaml ? Unquote(val) : val, out int imp)) d.Importance = Math.Clamp(imp, 1, 5); break;
            case "속성 색": d.AttrColor = yaml ? Unquote(val) : val; break;
            case "다른 이름": d.Aliases = SplitList(val); break;
            default: d.Attributes.Add((key, yaml ? Unquote(val) : val)); break;
        }
    }

    // ── 마크다운 머리말의 값 쓰기 · 읽기(따옴표 · 목록) ──

    /// <summary>한 줄 값이 머리말(YAML)에서 그대로 읽히려면 따옴표가 필요한가.</summary>
    private static bool NeedsQuote(string v) =>
        v.Length == 0 || v != v.Trim() || v.IndexOfAny(new[] { ':', '#', '"', '\'', '\\', '\n', '\r', '[', ']', '{', '}', ',', '&', '*', '!', '|', '>', '%', '@', '`' }) >= 0 || v[0] == '-' || v[0] == '?';

    private static string YamlScalar(string v)
    {
        v = v.Replace("\r", "");
        if (!NeedsQuote(v)) return v;
        return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
    }

    private static string YamlList(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(YamlScalar)) + "]";

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
        {
            var sb = new StringBuilder();
            for (int i = 1; i < v.Length - 1; i++)
            {
                char ch = v[i];
                if (ch == '\\' && i + 1 < v.Length - 1)
                {
                    char n = v[++i];
                    sb.Append(n == 'n' ? '\n' : n);
                }
                else sb.Append(ch);
            }
            return sb.ToString();
        }
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1].Replace("''", "'");
        return v;
    }

    /// <summary>"[a, b]" 또는 "a, b" 목록(항목에 따옴표가 있으면 그 안의 쉼표는 구분으로 치지 않는다).</summary>
    private static List<string> ParseList(string v)
    {
        v = v.Trim();
        if (v.StartsWith('[') && v.EndsWith(']')) v = v[1..^1];
        var items = new List<string>();
        var cur = new StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < v.Length; i++)
        {
            char ch = v[i];
            if (ch == '"' && (i == 0 || v[i - 1] != '\\')) inQuote = !inQuote;
            if (ch == ',' && !inQuote) { items.Add(Unquote(cur.ToString())); cur.Clear(); continue; }
            cur.Append(ch);
        }
        items.Add(Unquote(cur.ToString()));
        return items.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    // ── 본문 제목: 편집기가 다루는 "1. 제목" · "1-1. 제목"과 마크다운 "## 제목" · "### 제목" 사이의 변환 ──

    /// <summary>번호 붙은 항목 줄을 마크다운 제목으로("1. 성격" → "## 성격", "1-1. 버릇" → "### 버릇"). 그 밖의 줄은 그대로.</summary>
    internal static string ToMarkdownBody(string body)
    {
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith(ListMark)) { lines[i] = lines[i][1..]; continue; } // 제목이 아닌 번호 줄(마크다운 번호 목록)은 표시만 떼고 그대로
            var (level, text) = HeadingOf(lines[i]);
            if (level == 2) lines[i] = "## " + text;
            else if (level == 3) lines[i] = "### " + text;
            else if (level == 4) lines[i] = "#### " + text;
        }
        return string.Join("\r\n", lines);
    }

    /// <summary>
    /// 제목이 아닌데 "1. 글"처럼 보이는 줄(마크다운 번호 목록)에 붙이는 보이지 않는 표시(너비 0 글자). 편집기가 그런 줄을 제목으로 읽지 않게 하고,
    /// 파일에 쓸 때는 떼어 낸다(마크다운에는 표시 없이 "1. 글" 그대로 남는다).
    /// </summary>
    internal const char ListMark = '\u200B';

    /// <summary>줄 맨 앞의 구분 표시를 뗀다(예전 모양 .txt에 쓸 때).</summary>
    internal static string StripListMarks(string body) =>
        string.Join("\r\n", body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.StartsWith(ListMark) ? l[1..] : l));

    /// <summary>마크다운 제목을 편집기가 쓰는 번호 붙은 항목 줄로("## 성격" → "1. 성격", 그 밑 "### 버릇" → "1-1. 버릇"). 번호는 차례대로 다시 센다.</summary>
    internal static string FromMarkdownBody(string body)
    {
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int n2 = 0, n3 = 0, n4 = 0;
        bool fence = false; // ``` 코드 블록 안의 줄은 제목으로 보지 않는다
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("```")) { fence = !fence; if (HeadingOf(line).Level > 0) lines[i] = ListMark + line; continue; }
            var m = fence ? System.Text.RegularExpressions.Match.Empty : MdHeadingRegex.Match(line);
            if (!m.Success)
            {
                if (HeadingOf(line).Level > 0) lines[i] = ListMark + line; // "1. 사과" 같은 번호 목록은 제목이 아니다
                continue;
            }
            int hashes = m.Groups[1].Length;
            string title = m.Groups[2].Value.Trim();
            if (hashes >= 4) { if (n2 == 0) n2 = 1; if (n3 == 0) n3 = 1; n4++; lines[i] = $"{n2}-{n3}-{n4}. {title}"; }
            else if (hashes == 3) { if (n2 == 0) n2 = 1; n3++; n4 = 0; lines[i] = $"{n2}-{n3}. {title}"; }
            else { n2++; n3 = 0; n4 = 0; lines[i] = $"{n2}. {title}"; }
        }
        return string.Join("\r\n", lines);
    }

    private static readonly System.Text.RegularExpressions.Regex MdHeadingRegex =
        new(@"^(#{1,6})\s+(\S.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static List<string> SplitList(string s) => s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    /// <summary>파일에 적을 글. .md는 마크다운 모양, 그 밖(.txt)은 예전 모양.</summary>
    public string Serialize() =>
        string.Equals(System.IO.Path.GetExtension(Path), ".md", StringComparison.OrdinalIgnoreCase) ? SerializeMarkdown() : SerializeLegacy();

    private string SerializeMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("---\r\n");
        sb.Append("카테고리: ").Append(YamlScalar(WikiCategory.Find(Category).Label)).Append("\r\n");
        if (Summary.Length > 0) sb.Append("요약: ").Append(YamlScalar(Summary)).Append("\r\n");
        if (Tags.Count > 0) sb.Append("분류: ").Append(YamlList(Tags)).Append("\r\n");
        if (Labels.Count > 0) sb.Append("태그: ").Append(YamlList(Labels)).Append("\r\n");
        if (Importance != 1) sb.Append("중요도: ").Append(Importance).Append("\r\n");
        if (AttrColor.Length > 0) sb.Append("속성 색: ").Append(YamlScalar(AttrColor)).Append("\r\n");
        if (Aliases.Count > 0) sb.Append("별칭: ").Append(YamlList(Aliases)).Append("\r\n");
        foreach (var (k, v) in Attributes)
        {
            string key = k.Replace(":", " ").Replace("：", " ").Trim();
            if (key.Length == 0) continue;
            if (FixedKeys.Contains(key)) key += " ";
            sb.Append(key).Append(": ").Append(YamlScalar(v.Replace("\r", "").Replace("\n", " "))).Append("\r\n");
        }
        sb.Append("---\r\n\r\n");
        sb.Append(ToMarkdownBody(Body));
        return sb.ToString();
    }

    private string SerializeLegacy()
    {
        var sb = new StringBuilder();
        sb.Append("카테고리: ").Append(WikiCategory.Find(Category).Label).Append("\r\n");
        if (Summary.Length > 0) sb.Append("설명: ").Append(Summary.Replace("\r", "").Replace("\n", "\\n")).Append("\r\n");
        if (Tags.Count > 0) sb.Append("분류: ").Append(string.Join(", ", Tags)).Append("\r\n");
        if (Labels.Count > 0) sb.Append("태그: ").Append(string.Join(", ", Labels)).Append("\r\n");
        if (Importance != 1) sb.Append("중요도: ").Append(Importance).Append("\r\n");
        if (AttrColor.Length > 0) sb.Append("속성 색: ").Append(AttrColor).Append("\r\n");
        if (Aliases.Count > 0) sb.Append("다른 이름: ").Append(string.Join(", ", Aliases)).Append("\r\n");
        foreach (var (k, v) in Attributes)
        {
            string key = k.Replace(":", " ").Replace("：", " ").Trim();
            if (key.Length == 0) continue;
            if (FixedKeys.Contains(key)) key += " "; // 고정 속성 이름과 겹치면 속성 상자 항목으로 남도록
            sb.Append(key).Append(": ").Append(v.Replace("\r", "").Replace("\n", " ")).Append("\r\n");
        }
        sb.Append(Separator).Append("\r\n");
        sb.Append(StripListMarks(Body));
        return sb.ToString();
    }

    /// <summary>본문 한 줄이 항목 · 부항목인지: "3. 제목" → (2, "제목"), "3-1. 제목" → (3, "제목"), "3-1-2. 제목" → (4, "제목"), 아니면 (0, 줄 그대로).</summary>
    public static (int Level, string Text) HeadingOf(string line)
    {
        var m = HeadingRegex.Match(line);
        if (!m.Success) return (0, line);
        return (m.Groups[3].Success ? 4 : m.Groups[2].Success ? 3 : 2, m.Groups[4].Value);
    }

    private static readonly System.Text.RegularExpressions.Regex HeadingRegex =
        new(@"^(\d{1,3})(-\d{1,3})?(-\d{1,3})?\.\s+(\S.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
}

/// <summary>위키 카테고리(Muvel과 같은 여덟 가지). 글리프는 Segoe Fluent/MDL2 아이콘.</summary>
internal sealed record WikiCategory(string Key, string Label, string Glyph)
{
    public static readonly WikiCategory[] All =
    {
        new("character", "등장인물", "\uE77B"),
        new("event", "사건", "\uE823"),
        new("group", "단체", "\uE716"),
        new("place", "장소", "\uE707"),
        new("item", "아이템", "\uE7B8"),
        new("world", "세계관 설정", "\uE909"),
        new("story", "스토리", "\uE736"),
        new("none", "미분류", "\uE712"),
    };

    public static WikiCategory Find(string? keyOrLabel) =>
        All.FirstOrDefault(c => string.Equals(c.Key, keyOrLabel, StringComparison.OrdinalIgnoreCase) || c.Label == keyOrLabel) ?? All[^1];
}

internal static class WikiStore
{
    public static string Dir(string novelDir) => Path.Combine(novelDir, "wiki");

    /// <summary>새 위키 문서의 확장자(마크다운). 예전 문서의 .txt도 목록에 그대로 나온다.</summary>
    public const string NewExt = ".md";
    private static readonly string[] Exts = { ".md", ".txt" };

    /// <summary>그 폴더에 그 제목의 위키 문서가 이미 있는가(.md · .txt 어느 쪽이든).</summary>
    private static bool TitleTaken(string dir, string title, string? exceptPath = null) =>
        Exts.Any(e =>
        {
            string path = Path.Combine(dir, title + e);
            return File.Exists(path) && !string.Equals(path, exceptPath, StringComparison.OrdinalIgnoreCase);
        });

    public static List<WikiDoc> List(string novelDir)
    {
        var dir = Dir(novelDir);
        if (!Directory.Exists(dir)) return new();
        var docs = new List<WikiDoc>();
        try
        {
            foreach (var ext in Exts)
                foreach (var f in Directory.EnumerateFiles(dir, "*" + ext))
                {
                    // 패턴 "*.md"는 .md로 끝나는 이름만이 아니라 같은 모양의 긴 확장자도 잡을 수 있어 한 번 더 확인한다
                    if (!string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(f).StartsWith('.')) continue;
                    try { docs.Add(Load(f)); } catch { }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return docs.OrderBy(d => d.Importance).ThenBy(d => d.Title, NaturalStringComparer.Instance).ToList();
    }

    // ── 분류 목록: 작품마다 wiki\.분류.txt(한 줄에 하나, 상위 분류가 있으면 "이름<탭>상위"). 분류는 폴더처럼 계층으로 묶고, 한 문서를 여러 분류에 넣을 수 있다 ──

    private static string ClassFile(string novelDir) => Path.Combine(Dir(novelDir), ".분류.txt");

    /// <summary>만들어 둔 분류 하나: 이름, 상위 분류들(여러 개 가능), 대분류(카테고리 키), 요약.</summary>
    public sealed record ClassInfo(string Name, List<string> Parents, string Category, string Summary)
    {
        /// <summary>중요도(별 1~5). 문서의 중요도와 같은 눈금.</summary>
        public int Importance { get; init; } = 1;
        /// <summary>분류의 속성 표(이름, 값).</summary>
        public List<(string Key, string Value)> Attributes { get; init; } = new();
        /// <summary>분류의 태그들.</summary>
        public List<string> Tags { get; init; } = new();
    }

    private static string Clean(string s) => s.Replace('\u001E', ' ').Replace('\u001F', ' ');

    private static string EscapeField(string s) => s.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\n", "\\n").Replace("\t", " ");

    private static string UnescapeField(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i] == 'n' ? '\n' : s[i]); }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static List<ClassInfo> ReadClassCatalog(string novelDir)
    {
        try
        {
            string f = ClassFile(novelDir);
            if (!File.Exists(f)) return new();
            var list = new List<ClassInfo>();
            foreach (var raw in File.ReadAllLines(f, Encoding.UTF8))
            {
                var parts = raw.Split('\t');
                string name = parts[0].Trim();
                if (name.Length == 0 || list.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                var parents = parts.Length > 1 ? parts[1].Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList() : new List<string>();
                string cat = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : "none";
                string summary = parts.Length > 3 ? UnescapeField(parts[3]) : "";
                int imp = parts.Length > 4 && int.TryParse(parts[4].Trim(), out var iv) ? Math.Clamp(iv, 1, 5) : 1;
                var attrList = new List<(string, string)>();
                if (parts.Length > 5)
                    foreach (var pair in UnescapeField(parts[5]).Split('\u001E', StringSplitOptions.RemoveEmptyEntries))
                    {
                        int cut = pair.IndexOf('\u001F');
                        if (cut > 0) attrList.Add((pair[..cut], pair[(cut + 1)..]));
                    }
                var tagList = parts.Length > 6 ? UnescapeField(parts[6]).Split('\u001E', StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>();
                list.Add(new ClassInfo(name, parents, cat, summary) { Importance = imp, Attributes = attrList, Tags = tagList });
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    private static void WriteClassCatalog(string novelDir, List<ClassInfo> items)
    {
        Directory.CreateDirectory(Dir(novelDir));
        var lines = items.Select(c =>
        {
            string line = c.Name;
            if (c.Parents.Count > 0 || c.Category != "none" || c.Summary.Length > 0 || c.Importance != 1 || c.Attributes.Count > 0 || c.Tags.Count > 0)
                line += "\t" + string.Join("|", c.Parents) + "\t" + c.Category + "\t" + EscapeField(c.Summary)
                    + (c.Importance != 1 || c.Attributes.Count > 0 || c.Tags.Count > 0 ? "\t" + c.Importance : "")
                    + (c.Attributes.Count > 0 || c.Tags.Count > 0 ? "\t" + EscapeField(string.Join('\u001E', c.Attributes.Select(a => Clean(a.Key) + '\u001F' + Clean(a.Value)))) : "")
                    + (c.Tags.Count > 0 ? "\t" + EscapeField(string.Join('\u001E', c.Tags.Select(Clean))) : "");
            return line;
        }).ToList();
        SafeFile.WriteAllText(ClassFile(novelDir), string.Join("\r\n", lines) + (lines.Count > 0 ? "\r\n" : ""), new UTF8Encoding(false));
    }

    /// <summary>이 작품에서 고를 수 있는 분류들: 만들어 둔 분류 + 문서에 쓰인 분류(많이 쓴 순).</summary>
    public static List<string> KnownClasses(string novelDir) =>
        ReadClassCatalog(novelDir).Select(c => c.Name).Concat(Frequent(List(novelDir).SelectMany(d => d.Tags))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>분류 → 상위 분류들(없으면 빈 목록). 만들어 둔 분류 + 문서에만 쓰인 분류(맨 위 분류로).</summary>
    public static Dictionary<string, List<string>> ClassParents(string novelDir)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadClassCatalog(novelDir)) map[c.Name] = c.Parents.ToList();
        foreach (var n in KnownClasses(novelDir)) map.TryAdd(n, new List<string>());
        return map;
    }

    /// <summary>만들어 둔 분류들(요약 · 대분류 · 상위 포함).</summary>
    public static List<ClassInfo> ClassInfos(string novelDir) => ReadClassCatalog(novelDir);

    /// <summary>만들어 둔 분류의 요약 · 대분류 · 상위를 고친다(null인 항목은 그대로). 목록에 없으면(문서에만 쓰인 분류) 새로 넣는다.</summary>
    public static void UpdateClass(string novelDir, string name, string? summary = null, string? category = null, List<string>? parents = null, int? importance = null, List<(string Key, string Value)>? attributes = null, List<string>? tags = null)
    {
        var list = ReadClassCatalog(novelDir);
        int i = list.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var cur = i >= 0 ? list[i] : new ClassInfo(name, new List<string>(), "none", "");
        var ps = parents == null ? cur.Parents : parents.Where(x => x.Length > 0 && !x.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var next = cur with { Parents = ps, Category = category ?? cur.Category, Summary = summary ?? cur.Summary, Importance = importance is int iv ? Math.Clamp(iv, 1, 5) : cur.Importance, Attributes = attributes ?? cur.Attributes, Tags = tags ?? cur.Tags };
        if (i >= 0) list[i] = next; else list.Add(next);
        WriteClassCatalog(novelDir, list);
    }

    /// <summary>분류 → 대분류(카테고리 키). 만들어 둔 분류만(없는 분류는 미분류로 본다).</summary>
    public static Dictionary<string, string> ClassCategories(string novelDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ReadClassCatalog(novelDir)) map[c.Name] = c.Category;
        return map;
    }

    /// <summary>상위 분류들 중 첫 번째를 따라 맨 위 분류까지 올라가 그 대분류를 돌려준다(상위가 없으면 "none").</summary>
    public static string RootCategory(string novelDir, string? cls)
    {
        var all = ReadClassCatalog(novelDir);
        string? cur = cls;
        for (int guard = 0; guard < 16 && cur != null; guard++)
        {
            var info = all.FirstOrDefault(c => c.Name.Equals(cur, StringComparison.OrdinalIgnoreCase));
            if (info == null) return "none";
            if (info.Parents.Count == 0) return info.Category;
            cur = info.Parents[0];
        }
        return "none";
    }

    /// <summary>새 분류를 목록에 넣는다(이미 있으면 그대로). parents = 상위 분류들.</summary>
    public static void AddClass(string novelDir, string name, IEnumerable<string>? parents = null, string category = "none", string summary = "")
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var list = ReadClassCatalog(novelDir);
        if (list.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        var ps = (parents ?? Array.Empty<string>()).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        list.Add(new ClassInfo(name, ps, string.IsNullOrWhiteSpace(category) ? "none" : category, summary.Trim()));
        WriteClassCatalog(novelDir, list);
    }

    /// <summary>분류를 목록에서 지우고, 그 분류를 쓰던 모든 문서에서도 뺀다. 그 밑의 분류는 지운 분류의 상위로 올라간다.</summary>
    public static void DeleteClass(string novelDir, string name)
    {
        var all = ReadClassCatalog(novelDir);
        var up = all.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Parents ?? new List<string>();
        var list = new List<ClassInfo>();
        foreach (var c in all)
        {
            if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var ps = c.Parents.Where(x => !x.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ps.Count != c.Parents.Count) foreach (var u in up) if (!ps.Contains(u, StringComparer.OrdinalIgnoreCase) && !u.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) ps.Add(u);
            list.Add(c with { Parents = ps });
        }
        WriteClassCatalog(novelDir, list);
        foreach (var d in List(novelDir))
            if (d.Tags.RemoveAll(t => t.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0) Save(d);
    }

    // ── 템플릿: 작품마다 wiki\.템플릿\이름.md. 카테고리 · 분류 · 태그 · 속성 · 본문을 틀로 저장해 두고 새 문서를 만들 때 고른다 ──

    private static string TemplateDir(string novelDir) => Path.Combine(Dir(novelDir), ".템플릿");

    public static List<string> TemplateNames(string novelDir)
    {
        try
        {
            if (!Directory.Exists(TemplateDir(novelDir))) return new();
            return Directory.EnumerateFiles(TemplateDir(novelDir), "*.md").Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!)
                .OrderBy(n => n, NaturalStringComparer.Instance).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>템플릿 보관함(wiki\.템플릿)의 문서들(제목 순). 일반 위키 목록에는 나오지 않는다.</summary>
    public static List<WikiDoc> TemplateDocs(string novelDir)
    {
        var list = new List<WikiDoc>();
        foreach (var n in TemplateNames(novelDir)) { try { list.Add(LoadTemplate(novelDir, n)); } catch { } }
        return list;
    }

    /// <summary>새 위키 문서와 같은 입력(대분류 · 분류 · 이름 · 요약)으로 템플릿을 만들어 템플릿 보관함에만 둔다. 만든 틀 문서를 돌려준다(열어서 속성 · 본문을 채운다).</summary>
    public static WikiDoc CreateTemplate(string novelDir, string title, string category, string summary, List<string> tags)
    {
        string name = title.Trim();
        if (!NovelStore.ValidName(name)) throw new IOException(NovelStore.InvalidNameMessage("템플릿 이름"));
        Directory.CreateDirectory(TemplateDir(novelDir));
        if (File.Exists(Path.Combine(TemplateDir(novelDir), name + ".md"))) throw new IOException($"같은 이름의 템플릿이 이미 있어요: {name}");
        var d = new WikiDoc { Path = Path.Combine(TemplateDir(novelDir), name + ".md"), Category = category, Summary = summary.Trim(), Tags = tags.ToList() };
        Save(d);
        return d;
    }

    /// <summary>템플릿을 복제한다(이름 뒤에 " 복사", 겹치면 숫자). 만든 파일 경로를 돌려준다.</summary>
    public static string DuplicateTemplate(string novelDir, string path)
    {
        var d = Load(path);
        string baseName = d.Title + " 복사", name = baseName;
        for (int i = 2; File.Exists(Path.Combine(TemplateDir(novelDir), name + ".md")); i++) name = $"{baseName} {i}";
        d.Path = Path.Combine(TemplateDir(novelDir), name + ".md");
        Save(d);
        return d.Path;
    }

    /// <summary>이 문서가 템플릿 보관함에 있는 템플릿인가.</summary>
    public static bool IsTemplatePath(string? path) =>
        !string.IsNullOrEmpty(path) && string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), ".템플릿", StringComparison.OrdinalIgnoreCase);

    public static WikiDoc LoadTemplate(string novelDir, string name) => Load(Path.Combine(TemplateDir(novelDir), name + ".md"));

    /// <summary>이 문서의 카테고리 · 분류 · 태그 · 속성 이름 · 본문을 템플릿으로 저장한다(속성 값은 비워 둔다). 같은 이름이 있으면 덮어쓴다.</summary>
    public static void SaveTemplate(string novelDir, string name, WikiDoc source)
    {
        name = name.Trim();
        if (!NovelStore.ValidName(name)) throw new IOException(NovelStore.InvalidNameMessage("템플릿 이름"));
        Directory.CreateDirectory(TemplateDir(novelDir));
        var t = new WikiDoc
        {
            Path = Path.Combine(TemplateDir(novelDir), name + ".md"),
            Category = source.Category, Importance = source.Importance, AttrColor = source.AttrColor,
            Tags = source.Tags.ToList(), Labels = source.Labels.ToList(),
            Attributes = source.Attributes.Select(a => (a.Key, "")).ToList(),
            Body = source.Body,
        };
        Save(t);
    }

    public static void DeleteTemplate(string novelDir, string name)
    {
        string f = Path.Combine(TemplateDir(novelDir), name + ".md");
        if (File.Exists(f)) FileSystem.DeleteFile(f, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    /// <summary>이 작품의 위키 문서들에 쓰인 속성 이름들(많이 쓴 순, 같으면 이름순). 속성 항목을 고를 때의 목록.</summary>
    public static List<string> KnownAttributeNames(string novelDir) =>
        Frequent(List(novelDir).SelectMany(d => d.Attributes.Select(a => a.Key)));

    // ── 속성 항목 카탈로그: 기본 항목(아이콘 포함) + 문서에 쓰인 항목. 즐겨찾기는 작품마다 wiki\.속성즐겨찾기.txt(없으면 기본 즐겨찾기) ──

    private const string DefaultGlyph = "";
    /// <summary>기본 항목 (이름, 아이콘 글자, 처음부터 즐겨찾기인가).</summary>
    private static readonly (string Name, string Glyph, bool Fav)[] DefaultAttributes =
    {
        ("나이", "", true), ("능력", "", true), ("랭크", "", true), ("몸무게", DefaultGlyph, true), ("별명", DefaultGlyph, true),
        ("상태", "", true), ("생일", "", true), ("설명", "", true), ("성격", "", true), ("성별", "", true),
        ("소속", "", true), ("영문명", "", true), ("위치", "", true), ("이름", DefaultGlyph, true), ("일본어명", "", true),
        ("제작자", "", true), ("종족", "", true), ("칭호", DefaultGlyph, true), ("키", DefaultGlyph, true), ("한자명", "", true),
        ("MBTI", DefaultGlyph, true),
        ("가격", "", false), ("구성원", "", false), ("기간", "", false), ("리더", "", false), ("수도", "", false), ("인원", "", false),
    };

    public static string AttributeGlyph(string name) =>
        DefaultAttributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Glyph ?? DefaultGlyph;

    private static string AttrFavFile(string novelDir) => Path.Combine(Dir(novelDir), ".속성즐겨찾기.txt");

    /// <summary>고를 수 있는 속성 항목 전부: 기본 항목 + 문서에 쓰인 항목.</summary>
    public static List<string> AllAttributeNames(string novelDir) =>
        DefaultAttributes.Select(a => a.Name).Concat(KnownAttributeNames(novelDir)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>즐겨찾기한 속성 항목들.</summary>
    public static HashSet<string> AttributeFavorites(string novelDir)
    {
        try
        {
            string f = AttrFavFile(novelDir);
            if (File.Exists(f)) return File.ReadAllLines(f, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return DefaultAttributes.Where(a => a.Fav).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static void SetAttributeFavorite(string novelDir, string name, bool fav)
    {
        var set = AttributeFavorites(novelDir);
        if (fav) set.Add(name); else set.Remove(name);
        Directory.CreateDirectory(Dir(novelDir));
        SafeFile.WriteAllText(AttrFavFile(novelDir), string.Join("\r\n", set.OrderBy(n => n, NaturalStringComparer.Instance)) + (set.Count > 0 ? "\r\n" : ""), new UTF8Encoding(false));
    }

    private static List<string> Frequent(IEnumerable<string> names) =>
        names.Select(n => n.Trim()).Where(n => n.Length > 0)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, NaturalStringComparer.Instance)
            .Select(g => g.First()).ToList();

    public static WikiDoc Load(string path)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, new UTF8Encoding(false), true)) text = sr.ReadToEnd();
        return WikiDoc.Parse(path, text);
    }

    public static void Save(WikiDoc d) => SafeFile.WriteAllText(d.Path, d.Serialize(), new UTF8Encoding(false));

    public static WikiDoc Create(string novelDir, string title, string category, string summary, List<string> tags, WikiDoc? template = null)
    {
        string dir = Dir(novelDir);
        Directory.CreateDirectory(dir);
        string name = title.Trim();
        if (!NovelStore.ValidName(name)) throw new IOException(NovelStore.InvalidNameMessage("제목")); // 점으로 시작하는 이름은 숨김 파일이 되어 목록에서 사라진다
        if (TitleTaken(dir, name)) throw new IOException($"같은 이름의 위키 문서가 이미 있어요: {name}");
        string path = Path.Combine(dir, name + NewExt);
        var d = new WikiDoc { Path = path, Category = category, Summary = summary.Trim(), Tags = tags };
        if (template != null)
        {
            d.Tags = template.Tags.Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            d.Labels = template.Labels.ToList();
            d.Importance = template.Importance;
            d.AttrColor = template.AttrColor;
            d.Attributes = template.Attributes.ToList();
            d.Body = template.Body;
        }
        Save(d);
        return d;
    }

    public static string Rename(WikiDoc d, string newTitle)
    {
        string title = newTitle.Trim();
        if (!NovelStore.ValidName(title)) throw new IOException(NovelStore.InvalidNameMessage("제목"));
        string folder = Path.GetDirectoryName(d.Path)!;
        string target = Path.Combine(folder, title + Path.GetExtension(d.Path)); // 이름을 바꿔도 확장자(.md · .txt)는 그대로 둔다
        if (string.Equals(target, d.Path, StringComparison.Ordinal)) return d.Path;
        if (TitleTaken(folder, title, d.Path)) throw new IOException($"같은 이름의 위키 문서가 이미 있어요: {newTitle}"); // 다른 확장자의 같은 제목도 겹치는 것으로 본다(대 · 소문자만 바꾸는 건 허용)
        File.Move(d.Path, target);
        try { string oldPhoto = PhotoPath(d.Path), newPhoto = PhotoPath(target); if (File.Exists(oldPhoto) && !File.Exists(newPhoto)) File.Move(oldPhoto, newPhoto); } catch (IOException) { } // 문서 사진도 새 이름으로 따라간다
        d.Path = target;
        return target;
    }

    public static void Delete(string path)
    {
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        string photo = PhotoPath(path);
        try { if (File.Exists(photo)) FileSystem.DeleteFile(photo, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); } catch (IOException) { }
    }

    // ── 문서 사진: 이름 옆 동그라미에 넣는 사진. wiki\.images\제목.png(정사각 256px로 줄여 둔다) ──

    public static string PhotoPath(string docPath) => Path.Combine(Path.GetDirectoryName(docPath)!, ".images", Path.GetFileNameWithoutExtension(docPath) + ".png");

    /// <summary>문서 사진(없거나 못 읽으면 null). 파일을 잡고 있지 않도록 메모리에서 읽는다.</summary>
    public static Bitmap? LoadPhoto(string docPath)
    {
        try
        {
            string f = PhotoPath(docPath);
            if (!File.Exists(f)) return null;
            using var ms = new MemoryStream(File.ReadAllBytes(f));
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException) { return null; }
    }

    /// <summary>고른 그림 파일을 가운데 정사각으로 잘라 256px로 줄여 문서 사진으로 저장한다.</summary>
    public static void SetPhoto(string docPath, string sourceFile, Rectangle? cropRect = null)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(sourceFile));
        using var src = Image.FromStream(ms);
        int side = Math.Min(src.Width, src.Height);
        var crop = cropRect ?? new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side); // 자르기 창에서 고른 영역(없으면 가운데 정사각)
        using var bmp = new Bitmap(256, 256);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, 256, 256), crop, GraphicsUnit.Pixel);
        }
        string f = PhotoPath(docPath);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        string tmp = f + ".tmp";
        bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
        File.Move(tmp, f, overwrite: true);
    }

    public static void DeletePhoto(string docPath)
    {
        string f = PhotoPath(docPath);
        try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
    }

    /// <summary>[+ 새 위키 문서] 창을 띄워 만든다. 만든 문서 경로(취소하면 null).</summary>
    public static string? AskCreate(IWin32Window? owner, string novelDir, string? initialCategory = null, string? initialClass = null)
    {
        using var dlg = new WikiCreateDialog(novelDir, initialCategory, TemplateNames(novelDir), initialClass);
        if (dlg.ShowDialogOver(owner) != DialogResult.OK) return null;
        try
        {
            WikiDoc? template = dlg.TemplateName is { } tn ? LoadTemplate(novelDir, tn) : null;
            string category = template != null && dlg.CategoryKey == "none" ? template.Category : dlg.CategoryKey; // 카테고리를 따로 고르지 않았으면 템플릿의 카테고리를 쓴다
            return Create(novelDir, dlg.TitleText, category, dlg.SummaryText, dlg.Classes, template).Path;
        }
        catch (Exception ex) { MessageBox.Show(owner, $"위키 문서를 만들지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
    }
}

/// <summary>
/// 위키 목록(Muvel의 "카테고리별 보기"): 카테고리 머리 줄(아이콘 · 이름 · 개수) 밑에 문서 줄(제목 · 설명).
/// 문서 줄을 누르면 DocChosen, 우클릭하면 MenuRequested.
/// </summary>
internal sealed class WikiList : Control
{
    /// <summary>목록 한 줄: 카테고리 머리(Header) · 분류 폴더(Folder) · 문서(Doc). Depth = 폴더 안 깊이.</summary>
    private readonly record struct Row(WikiCategory? Header, WikiDoc? Doc, int Count, string? Folder = null, int Depth = 0);
    private readonly List<Row> rows = new();
    private readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase); // 접어 둔 분류 폴더
    private List<WikiDoc> lastDocs = new();
    private IReadOnlyDictionary<string, List<string>>? lastParents;
    private IReadOnlyDictionary<string, string>? lastCats;
    private readonly VScrollBar scroll = new() { Dock = DockStyle.Right, Visible = false };
    private LibraryPalette? palette;
    private int hover = -1;
    private string? current;

    public event Action<string>? DocChosen;
    public event Action<string, Point>? MenuRequested;
    /// <summary>분류 폴더를 우클릭했다(이름, 화면 좌표).</summary>
    public event Action<string, Point>? FolderMenuRequested;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool OnMain { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Current { get => current; set { current = value; Invalidate(); } }

    public WikiList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        Controls.Add(scroll);
        scroll.ValueChanged += (_, _) => Invalidate();
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private Color Surface => palette == null ? BackColor : OnMain ? palette.Main : palette.Side;
    private int RowH(int i) => rows[i].Header != null ? Font.Height + S(i == 0 ? 14 : 24) : rows[i].Folder != null && rows[i].Doc == null ? Font.Height + S(OnMain ? 22 : 14) : Font.Height + S(OnMain ? 24 : 16);
    private int Offset => scroll.Visible ? scroll.Value : 0;

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = Surface; Invalidate(); }

    /// <summary>문서들을 보여 준다. 분류는 계층 폴더(parents: 분류 → 상위 분류, 만들어 둔 빈 분류도 폴더로 보인다). 분류에 든 문서는 그 폴더들 안에(여러 분류면 각각), 분류 없는 문서는 카테고리별로.</summary>
    public void SetDocs(List<WikiDoc> docs, IReadOnlyDictionary<string, List<string>>? parents = null, IReadOnlyDictionary<string, string>? categories = null)
    {
        lastDocs = docs; lastParents = parents; lastCats = categories;
        rows.Clear();
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (parents != null) foreach (var kv in parents) map[kv.Key] = kv.Value;
        foreach (var t in docs.SelectMany(d => d.Tags)) map.TryAdd(t, new List<string>());
        List<string> ParentsOf(string n) => map[n].Where(par => !par.Equals(n, StringComparison.OrdinalIgnoreCase) && map.ContainsKey(par)).ToList();
        List<string> Children(string? par) => map.Keys.Where(k => par == null ? ParentsOf(k).Count == 0 : ParentsOf(k).Contains(par, StringComparer.OrdinalIgnoreCase)).OrderBy(k => k, NaturalStringComparer.Instance).ToList();
        bool HasContent(string n, HashSet<string> seen) => seen.Add(n) && (docs.Any(d => d.Tags.Contains(n, StringComparer.OrdinalIgnoreCase)) || Children(n).Any(c => HasContent(c, seen)));
        void AddFolder(string name, int depth, HashSet<string> path)
        {
            if (!path.Add(name)) return; // 순환 방지
            if (parents == null && !HasContent(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase))) return; // 검색 중에는 빈 폴더를 숨긴다
            var inFolder = docs.Where(d => d.Tags.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
            rows.Add(new Row(null, null, inFolder.Count, name, depth));
            if (collapsed.Contains(name)) return;
            foreach (var c in Children(name)) AddFolder(c, depth + 1, new HashSet<string>(path, StringComparer.OrdinalIgnoreCase));
            foreach (var d in inFolder) rows.Add(new Row(null, d, 0, name, depth + 1));
        }
        string CatOf(string n) => categories != null && categories.TryGetValue(n, out var c) ? c : "none";
        HashSet<string> Subtree(string root, HashSet<string>? seen = null)
        {
            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (seen.Add(root)) foreach (var c in Children(root)) Subtree(c, seen);
            return seen;
        }
        // 맨 위 단계 = 대분류(카테고리). 그 밑에 그 대분류의 분류 폴더들(계층), 그 뒤에 분류 없는 문서.
        foreach (var cat in WikiCategory.All)
        {
            var roots = Children(null).Where(r => CatOf(r) == cat.Key).ToList();
            if (parents == null) roots = roots.Where(r => HasContent(r, new HashSet<string>(StringComparer.OrdinalIgnoreCase))).ToList(); // 검색 중에는 빈 폴더를 숨긴다
            var loose = docs.Where(d => d.Category == cat.Key && d.Tags.Count == 0).ToList();
            if (roots.Count == 0 && loose.Count == 0) continue;
            var subtrees = roots.Select(r => Subtree(r)).ToList();
            int total = loose.Count + docs.Count(d => d.Tags.Count > 0 && subtrees.Any(st => d.Tags.Any(st.Contains)));
            rows.Add(new Row(cat, null, total));
            foreach (var root in roots) AddFolder(root, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            foreach (var d in loose) rows.Add(new Row(null, d, 0));
        }
        SyncScroll();
        Invalidate();
    }

    private int ContentHeight() { int h = S(4); for (int i = 0; i < rows.Count; i++) h += RowH(i); return h + S(8); }
    /// <summary>목록을 스크롤 없이 다 펼치는 데 필요한 높이(쪽 전체가 스크롤될 때).</summary>
    public int PreferredHeight => ContentHeight() + S(8);

    private void SyncScroll()
    {
        int content = ContentHeight();
        bool need = content > ClientSize.Height && ClientSize.Height > 0;
        scroll.Visible = need;
        if (need) { scroll.Maximum = content; scroll.LargeChange = Math.Max(1, ClientSize.Height); scroll.SmallChange = S(30); }
        else if (scroll.Value != 0) scroll.Value = 0;
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); SyncScroll(); }

    private int IndexAt(int y)
    {
        int top = S(4) - Offset;
        for (int i = 0; i < rows.Count; i++) { int h = RowH(i); if (y >= top && y < top + h) return i; top += h; }
        return -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!scroll.Visible) return;
        scroll.Value = Math.Clamp(scroll.Value - Math.Sign(e.Delta) * S(90), 0, Math.Max(0, scroll.Maximum - scroll.LargeChange + 1));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Y);
        if (i >= 0 && rows[i].Doc == null && rows[i].Folder == null) i = -1;
        if (i != hover) { hover = i; Invalidate(); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = IndexAt(e.Y);
        if (i >= 0 && rows[i].Doc == null && rows[i].Folder is string folder)
        {
            if (e.Button == MouseButtons.Left) { if (!collapsed.Remove(folder)) collapsed.Add(folder); SetDocs(lastDocs, lastParents, lastCats); }
            else if (e.Button == MouseButtons.Right) FolderMenuRequested?.Invoke(folder, PointToScreen(e.Location));
            return;
        }
        if (i < 0 || rows[i].Doc is not WikiDoc d) return;
        if (e.Button == MouseButtons.Left) DocChosen?.Invoke(d.Path);
        else if (e.Button == MouseButtons.Right) MenuRequested?.Invoke(d.Path, PointToScreen(e.Location));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = palette;
        var g = e.Graphics;
        g.Clear(Surface);
        if (p == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = ClientSize.Width - (scroll.Visible ? scroll.Width : 0), pad = S(10);
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        using var iconFont = new Font(MainForm.IconFontName(), Font.SizeInPoints * 0.95f);
        using var small = new Font(Font.FontFamily, Font.SizeInPoints * 0.88f);
        using var bold = new Font(Font, FontStyle.Bold);
        int y = S(4) - Offset;
        for (int i = 0; i < rows.Count; i++)
        {
            int h = RowH(i);
            if (y + h >= 0 && y <= ClientSize.Height)
            {
                var (header, doc, count, folder, depth) = rows[i];
                if (header == null && doc == null && folder != null)
                {
                    int fi = depth * S(22);
                    var fr = new Rectangle(pad + fi, y + S(1), w - pad * 2 - fi, h - S(2));
                    if (i == hover) { using var hp = LibraryPalette.Rounded(fr, S(8)); using var hb = new SolidBrush(OnMain ? Themes.Blend(p.Main, p.Fore, 0.05f) : p.CardHover); g.FillPath(hb, hp); }
                    bool shut = collapsed.Contains(folder);
                    TextRenderer.DrawText(g, shut ? "" : "", iconFont, new Rectangle(fr.X + S(6), fr.Y, S(18), fr.Height), p.Dim, flags);
                    TextRenderer.DrawText(g, "", iconFont, new Rectangle(fr.X + S(28), fr.Y, S(22), fr.Height), p.Accent, flags);
                    int nw = Math.Min(TextRenderer.MeasureText(folder, bold, Size.Empty, TextFormatFlags.NoPadding).Width, fr.Right - fr.X - S(120));
                    TextRenderer.DrawText(g, folder, bold, new Rectangle(fr.X + S(54), fr.Y, nw + S(4), fr.Height), p.Fore, flags);
                    TextRenderer.DrawText(g, count.ToString(), small, new Rectangle(fr.X + S(54) + nw + S(10), fr.Y, S(40), fr.Height), Themes.Blend(Surface, p.Dim, 0.6f), flags);
                }
                else if (header != null)
                {
                    var r = new Rectangle(pad + S(6), y + h - Font.Height - S(6), w - pad * 2, Font.Height + S(4));
                    TextRenderer.DrawText(g, header.Glyph, iconFont, new Rectangle(r.X, r.Y, S(20), r.Height), p.Accent, flags);
                    string label = $"{header.Label}";
                    int lw = TextRenderer.MeasureText(label, small, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, label, small, new Rectangle(r.X + S(24), r.Y, lw + 2, r.Height), p.Dim, flags);
                    TextRenderer.DrawText(g, count.ToString(), small, new Rectangle(r.X + S(24) + lw + S(6), r.Y, S(40), r.Height), Themes.Blend(Surface, p.Dim, 0.6f), flags);
                }
                else if (doc != null)
                {
                    int ind = depth * S(22);
                    var r = new Rectangle(pad + ind, y + S(1), w - pad * 2 - ind, h - S(2));
                    bool cur = string.Equals(doc.Path, current, StringComparison.OrdinalIgnoreCase);
                    if (cur || i == hover)
                    {
                        using var path = LibraryPalette.Rounded(r, S(8));
                        using var b = new SolidBrush(cur ? p.AccentSoft : OnMain ? Themes.Blend(p.Main, p.Fore, 0.05f) : p.CardHover);
                        g.FillPath(b, path);
                    }
                    int tx = r.X + S(16);
                    int tw = Math.Min(TextRenderer.MeasureText(doc.Title, bold, Size.Empty, TextFormatFlags.NoPadding).Width, r.Right - tx - S(8));
                    TextRenderer.DrawText(g, doc.Title, cur ? bold : Font, new Rectangle(tx, r.Y, tw + S(4), r.Height), p.Fore, flags);
                    if (doc.Summary.Length > 0 && r.Right - (tx + tw + S(14)) > S(30))
                        TextRenderer.DrawText(g, doc.Summary.Replace("\n", " "), small, new Rectangle(tx + tw + S(14), r.Y, r.Right - (tx + tw + S(14)) - S(8), r.Height), p.Dim, flags);
                }
            }
            y += h;
        }
        if (rows.Count == 0)
            TextRenderer.DrawText(g, "아직 위키 문서가 없어요.\n[+ 새 위키 문서]로 인물 · 장소 · 설정을 적어 두세요.", Font, new Rectangle(0, S(16), w, S(80)), p.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}
