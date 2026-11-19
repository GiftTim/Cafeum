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
/// 아래는 본문. 본문의 항목은 "1. 제목", 부항목은 "1-1. 제목"으로 적는다(저장할 때 번호를 다시 매긴다).
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
    private static readonly string[] FixedKeys = { "카테고리", "설명", "요약", "분류", "중요도", "다른 이름", "별칭" }; // 두 모양의 고정 속성 이름(같은 이름의 사용자 속성은 쓸 때 이름 뒤에 공백을 붙여 구분한다)

    public string Path { get; set; } = "";
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Category { get; set; } = "none";
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public int Importance { get; set; } = 1;
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
            case "중요도": if (int.TryParse(yaml ? Unquote(val) : val, out int imp)) d.Importance = Math.Clamp(imp, 1, 5); break;
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
        int n2 = 0, n3 = 0;
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
            if (hashes >= 3) { if (n2 == 0) n2 = 1; n3++; lines[i] = $"{n2}-{n3}. {title}"; }
            else { n2++; n3 = 0; lines[i] = $"{n2}. {title}"; }
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
        if (Importance != 1) sb.Append("중요도: ").Append(Importance).Append("\r\n");
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
        if (Importance != 1) sb.Append("중요도: ").Append(Importance).Append("\r\n");
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

    /// <summary>본문 한 줄이 항목 · 부항목인지: "3. 제목" → (2, "제목"), "3-1. 제목" → (3, "제목"), 아니면 (0, 줄 그대로).</summary>
    public static (int Level, string Text) HeadingOf(string line)
    {
        var m = HeadingRegex.Match(line);
        if (!m.Success) return (0, line);
        return (m.Groups[2].Success ? 3 : 2, m.Groups[3].Value);
    }

    private static readonly System.Text.RegularExpressions.Regex HeadingRegex =
        new(@"^(\d{1,3})(-\d{1,3})?\.\s+(\S.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
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

    public static WikiDoc Load(string path)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, new UTF8Encoding(false), true)) text = sr.ReadToEnd();
        return WikiDoc.Parse(path, text);
    }

    public static void Save(WikiDoc d) => SafeFile.WriteAllText(d.Path, d.Serialize(), new UTF8Encoding(false));

    public static WikiDoc Create(string novelDir, string title, string category, string summary, List<string> tags)
    {
        string dir = Dir(novelDir);
        Directory.CreateDirectory(dir);
        string name = title.Trim();
        if (!NovelStore.ValidName(name)) throw new IOException(NovelStore.InvalidNameMessage("제목")); // 점으로 시작하는 이름은 숨김 파일이 되어 목록에서 사라진다
        if (TitleTaken(dir, name)) throw new IOException($"같은 이름의 위키 문서가 이미 있어요: {name}");
        string path = Path.Combine(dir, name + NewExt);
        var d = new WikiDoc { Path = path, Category = category, Summary = summary.Trim(), Tags = tags };
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
        d.Path = target;
        return target;
    }

    public static void Delete(string path) => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    /// <summary>[+ 새 위키 문서] 창을 띄워 만든다. 만든 문서 경로(취소하면 null).</summary>
    public static string? AskCreate(IWin32Window? owner, string novelDir)
    {
        using var dlg = new WikiCreateDialog();
        if (dlg.ShowDialogOver(owner) != DialogResult.OK) return null;
        try { return Create(novelDir, dlg.TitleText, dlg.CategoryKey, dlg.SummaryText, dlg.Tags).Path; }
        catch (Exception ex) { MessageBox.Show(owner, $"위키 문서를 만들지 못했어요: {ex.Message}", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
    }
}

/// <summary>Muvel의 "새 위키 문서" 창: 제목 · 설명 · 카테고리 · 분류 · [만들기].</summary>
internal sealed class WikiCreateDialog : Form
{
    private readonly TextBox title, summary, tags;
    private readonly ComboBox category;

    public string TitleText => title.Text.Trim();
    public string SummaryText => summary.Text.Trim();
    public string CategoryKey => WikiCategory.All[Math.Max(0, category.SelectedIndex)].Key;
    public List<string> Tags => tags.Text.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    public WikiCreateDialog(string? initialCategory = null)
    {
        Text = "새 위키 문서";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);

        Shown += (_, _) => { Activate(); BringToFront(); }; // 앱 안에서만 앞으로 온다(다른 프로그램 위로 뜨지는 않는다)
        var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill }; // Dock해야 폼 Padding(여백)이 왼쪽 · 위에도 적용된다
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label L(string s) => new() { Text = s, AutoSize = true, Margin = new Padding(0, 0, 0, 4), Font = new Font(Font, FontStyle.Bold) };

        title = new TextBox { Width = 440, PlaceholderText = "문서 제목 (예: 등장인물 이름)", Margin = new Padding(0, 0, 0, 12) };
        summary = new TextBox { Width = 440, Height = 70, Multiline = true, AcceptsReturn = true, PlaceholderText = "어떤 문서인지 한두 줄로 적어 주세요", Margin = new Padding(0, 0, 0, 12) };
        category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 0, 16, 12) };
        foreach (var c in WikiCategory.All) category.Items.Add(c.Label);
        category.SelectedIndex = Array.FindIndex(WikiCategory.All, c => c.Key == (initialCategory ?? "none"));
        tags = new TextBox { Width = 224, PlaceholderText = "쉼표로 여러 개", Margin = new Padding(0, 0, 0, 12) };

        root.Controls.Add(L("제목"), 0, 0); root.SetColumnSpan(root.GetControlFromPosition(0, 0)!, 2);
        root.Controls.Add(title, 0, 1); root.SetColumnSpan(title, 2);
        root.Controls.Add(L("설명"), 0, 2); root.SetColumnSpan(root.GetControlFromPosition(0, 2)!, 2);
        root.Controls.Add(summary, 0, 3); root.SetColumnSpan(summary, 2);
        root.Controls.Add(L("카테고리"), 0, 4);
        root.Controls.Add(L("분류"), 1, 4);
        root.Controls.Add(category, 0, 5);
        root.Controls.Add(tags, 1, 5);

        var ok = new Button { Text = "✓  만들기", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(96, 32) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(84, 32) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 6); root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (!NovelStore.ValidName(title.Text))
            {
                MessageBox.Show(this, NovelStore.InvalidNameMessage("제목"), AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };
        Shown += (_, _) => title.Focus();
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
    }
}

/// <summary>
/// 위키 목록(Muvel의 "카테고리별 보기"): 카테고리 머리 줄(아이콘 · 이름 · 개수) 밑에 문서 줄(제목 · 설명).
/// 문서 줄을 누르면 DocChosen, 우클릭하면 MenuRequested.
/// </summary>
internal sealed class WikiList : Control
{
    private readonly List<(WikiCategory? Header, WikiDoc? Doc, int Count)> rows = new();
    private readonly VScrollBar scroll = new() { Dock = DockStyle.Right, Visible = false };
    private LibraryPalette? palette;
    private int hover = -1;
    private string? current;

    public event Action<string>? DocChosen;
    public event Action<string, Point>? MenuRequested;

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
    private int RowH(int i) => rows[i].Header != null ? Font.Height + S(i == 0 ? 14 : 24) : Font.Height + S(OnMain ? 24 : 16);
    private int Offset => scroll.Visible ? scroll.Value : 0;

    public void SetPalette(LibraryPalette p) { palette = p; BackColor = Surface; Invalidate(); }

    public void SetDocs(List<WikiDoc> docs)
    {
        rows.Clear();
        foreach (var cat in WikiCategory.All)
        {
            var inCat = docs.Where(d => d.Category == cat.Key).ToList();
            if (inCat.Count == 0) continue;
            rows.Add((cat, null, inCat.Count));
            foreach (var d in inCat) rows.Add((null, d, 0));
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
        if (i >= 0 && rows[i].Doc == null) i = -1;
        if (i != hover) { hover = i; Invalidate(); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = IndexAt(e.Y);
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
                var (header, doc, count) = rows[i];
                if (header != null)
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
                    var r = new Rectangle(pad, y + S(1), w - pad * 2, h - S(2));
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
