using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cafeum;

/// <summary>사전 종류(사전 창의 탭): 국어사전 · 한영 사전(한국어 단어의 영어 뜻) · 영한 사전(영어 단어의 한국어 뜻).</summary>
internal static class DictKind
{
    public const int Korean = 0, KoEn = 1, EnKo = 2;

    /// <summary>(가져올 사이트, 그 안에서 읽을 언어 항목, 뜻 위에 적을 출처).</summary>
    public static (string Host, string Lang, string Source) Info(int kind) => kind switch
    {
        KoEn => ("en.wiktionary.org", "Korean", "Wiktionary · en.wiktionary.org (한영)"),
        EnKo => ("ko.wiktionary.org", "영어", "위키낱말사전 · ko.wiktionary.org (영한)"),
        _ => ("ko.wiktionary.org", "한국어", "위키낱말사전 · ko.wiktionary.org"),
    };

    public static string Name(int kind) => kind switch { KoEn => "한영 사전", EnKo => "영한 사전", _ => "국어사전" };
}

/// <summary>저장해 둔 사전 결과 한 건.</summary>
internal sealed class DictionaryEntry
{
    public string Word { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>어느 사전의 결과인가(DictKind). 예전에 저장한 것은 0(국어사전).</summary>
    public int Kind { get; set; }
    public long Used { get; set; }
}

/// <summary>
/// 한 번 찾은 사전 결과를 이 컴퓨터(dictionary-cache.json)에 저장해 둔다. 인터넷이 끊겨도 저장된 단어는 볼 수 있다.
/// 사전 종류마다 따로 저장한다(같은 글자라도 국어사전 · 한영 · 영한 결과가 다르다).
/// 최대 3000건까지 두고, 넘치면 가장 오래 안 본 것부터 지운다. 파일이 깨져 있으면 빈 상태로 시작한다.
/// </summary>
internal static class DictionaryCache
{
    private const int MaxEntries = 3000;
    private static readonly object gate = new();
    private static Dictionary<string, DictionaryEntry>? map;
    private static string FilePath => Path.Combine(AppSettings.DataDir, "dictionary-cache.json");

    /// <summary>앞뒤 공백을 자르고 유니코드를 한 모양으로 맞춘다(같은 글자가 다른 키로 저장되지 않게).</summary>
    public static string Normalize(string? w) => (w ?? "").Trim().Normalize(NormalizationForm.FormC);

    private static string Key(int kind, string word) => kind + "|" + Normalize(word);

    private static Dictionary<string, DictionaryEntry> Map()
    {
        if (map != null) return map;
        map = new Dictionary<string, DictionaryEntry>();
        try
        {
            if (File.Exists(FilePath))
                foreach (var e in JsonSerializer.Deserialize<List<DictionaryEntry>>(File.ReadAllText(FilePath)) ?? new())
                    if (!string.IsNullOrEmpty(e.Word) && !string.IsNullOrEmpty(e.Text)) map[Key(e.Kind, e.Word)] = e;
        }
        catch (Exception ex) { AppLog.Write("사전 저장 파일을 읽지 못했어요(빈 상태로 시작)", ex); map = new(); }
        return map;
    }

    public static bool TryGet(int kind, string word, out DictionaryEntry? item)
    {
        lock (gate)
        {
            if (Map().TryGetValue(Key(kind, word), out var e)) { e.Used = DateTime.UtcNow.Ticks; item = e; return true; }
            item = null; return false;
        }
    }

    public static void Put(DictionaryEntry e)
    {
        lock (gate)
        {
            var m = Map();
            e.Word = Normalize(e.Word);
            e.Used = DateTime.UtcNow.Ticks;
            m[Key(e.Kind, e.Word)] = e;
            if (m.Count > MaxEntries)
                foreach (var old in m.Values.OrderBy(x => x.Used).Take(m.Count - MaxEntries).ToList()) m.Remove(Key(old.Kind, old.Word));
            Save(m);
        }
    }

    private static void Save(Dictionary<string, DictionaryEntry> m)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m.Values.ToList()));
            File.Move(tmp, FilePath, true); // 쓰다 끊겨도 예전 파일이 남도록 임시 파일에 쓴 뒤 바꾼다
        }
        catch (Exception ex) { AppLog.Write("사전 결과를 저장하지 못했어요", ex); }
    }
}

/// <summary>위키낱말사전에서 뜻을 가져온다(열쇠가 필요 없는 공개 API). 한국어 사이트는 한국어 · 영어 단어를, 영어 사이트는 한국어 단어를 담고 있다.</summary>
internal static class DictionaryClient
{
    private static readonly HttpClient http = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Cafeum/{Updater.Current} (https://github.com/{Updater.RepoSlug})"); // 위키미디어는 프로그램 이름이 있는 User-Agent를 요구한다
        return c;
    }

    /// <summary>그 사전의 글. 사전에 없거나 해당 언어 항목이 없으면 null. 인터넷이 안 되면 예외.</summary>
    public static async Task<DictionaryEntry?> FetchAsync(int kind, string word, CancellationToken ct)
    {
        var found = await FetchOne(kind, word, ct);
        // 문장 첫머리처럼 첫 글자를 대문자로 쳤다면, 작은 글자로도 한 번 찾는다(영어 사전 제목은 대 · 소문자를 가린다)
        if (found == null && kind != DictKind.Korean && word != word.ToLowerInvariant()) found = await FetchOne(kind, word.ToLowerInvariant(), ct);
        return found;
    }

    private static async Task<DictionaryEntry?> FetchOne(int kind, string word, CancellationToken ct)
    {
        var (host, lang, _) = DictKind.Info(kind);
        string url = $"https://{host}/w/api.php?action=query&prop=extracts&explaintext=1&exsectionformat=wiki&redirects=1&format=json&titles=" + Uri.EscapeDataString(word);
        using var resp = await http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages)) return null;
        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.TryGetProperty("missing", out _)) return null;
            string text = page.Value.TryGetProperty("extract", out var ex) ? ex.GetString() ?? "" : "";
            string? section = Section(text, lang);
            if (section == null) return null;
            return new DictionaryEntry { Word = word, Text = section, Kind = kind };
        }
        return null;
    }

    /// <summary>"== 한국어 ==" 같은 언어 제목부터 다음 언어 제목 앞까지(= 두 개짜리 제목만 언어 구분이다).</summary>
    internal static string? Section(string text, string lang)
    {
        text = text.Replace("\r", "");
        var start = Regex.Match(text, @"^==\s*" + Regex.Escape(lang) + @"\s*==\s*$", RegexOptions.Multiline);
        if (!start.Success) return null;
        string rest = text[(start.Index + start.Length)..];
        var next = Regex.Match(rest, @"^==\s*[^=].*?\s*==\s*$", RegexOptions.Multiline);
        string section = (next.Success ? rest[..next.Index] : rest).Trim();
        section = Regex.Replace(section, @"\n{3,}", "\n\n");
        return section.Length == 0 ? null : section;
    }

    private static readonly Regex PartOfSpeech = new(@"^={3,6}\s*(Noun|Verb|Adjective|Adverb|Pronoun|Proper noun|Interjection|Particle|Determiner|Numeral|Suffix|Prefix|Counter|명사|동사|형용사|부사|대명사|감탄사|조사|수사|관형사|접사|어미|접두사|접미사)\b.*$", RegexOptions.IgnoreCase);

    /// <summary>단어장에 같이 적어 둘 한 줄 뜻: 품사 제목(명사 · Noun …) 바로 뒤의 첫 뜻 줄. 못 찾으면 제목 · 발음 · 어원을 건너뛴 첫 줄.</summary>
    internal static string Gist(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        bool afterPos = false;
        string fallback = "";
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (Regex.IsMatch(line, @"^={3,6}")) { afterPos = PartOfSpeech.IsMatch(line); continue; }
            if (line.Contains("IPA") || line.StartsWith("어원") || line.StartsWith("발음") || line.StartsWith("활용") || line.StartsWith("(표준어")
                || line.StartsWith("Etymology") || line.StartsWith("Pronunciation") || line.StartsWith("Inflection") || line.StartsWith("Revised Romanization")
                || line.StartsWith("Hangul") || line.StartsWith("Phonetic")) continue;
            string cleaned = Regex.Replace(line, @"^\d+(-\d+)?\.\s*", "");
            if (afterPos) return Trim(cleaned);
            if (fallback.Length == 0) fallback = Trim(cleaned);
        }
        return fallback;
    }

    private static string Trim(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
