using System.Text;
using System.Text.Json;

namespace Cafeum;

/// <summary>라마에 보내는 대화 한 줄. Role은 system · user · assistant.</summary>
internal sealed record LlamaMessage(string Role, string Content);

/// <summary>라마(Ollama)를 부르다 생긴 문제. Message를 그대로 사용자에게 보여 줘도 되게 쓴다.</summary>
internal sealed class LlamaException : Exception
{
    public LlamaException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// 내 PC에서 돌아가는 Ollama(기본 http://localhost:11434)를 통해 라마를 쓴다. 글이 밖으로 나가지 않는다.
/// 추가 패키지 없이 HttpClient만 쓰고, 답은 한 토막씩 받아 바로 화면에 보여 줄 수 있게 스트리밍한다.
/// </summary>
internal static class LlamaClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan }; // 긴 글은 오래 걸리니 취소는 CancellationToken으로만

    /// <summary>
    /// Ollama 주소. Ollama는 IPv4(127.0.0.1)에서만 기다리는데 "localhost"는 IPv6(::1)부터 시도해서
    /// 연결이 몇 초씩 늦어지다 시간 초과가 나므로, localhost는 127.0.0.1로 바꿔서 쓴다.
    /// </summary>
    private static string Base(AppSettings s)
    {
        string url = s.LlamaEndpoint.Trim().TrimEnd('/');
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            url = new UriBuilder(u) { Host = "127.0.0.1" }.Uri.ToString().TrimEnd('/');
        return url;
    }

    /// <summary>설치된 모델 이름들(ollama list와 같다). 연결이 안 되면 LlamaException.</summary>
    public static async Task<List<string>> ListModelsAsync(AppSettings s, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var res = await Http.GetAsync(Base(s) + "/api/tags", cts.Token).ConfigureAwait(true);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(true));
            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var models))
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) names.Add(name);
            return names;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Unreachable(s); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or InvalidOperationException) { throw Unreachable(s, ex); }
    }

    private static LlamaException Unreachable(AppSettings s, Exception? inner = null) =>
        new($"Ollama에 연결할 수 없어요 ({Base(s)}). Ollama가 켜져 있는지, 주소가 맞는지 확인해 주세요.", inner);

    /// <summary>대화를 보내고, 답이 오는 대로 onToken에 한 토막씩 넘긴다. 취소하면 OperationCanceledException.</summary>
    public static async Task ChatAsync(AppSettings s, IEnumerable<LlamaMessage> messages, Action<string> onToken, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new
        {
            model = s.LlamaModel,
            stream = true,
            options = new { temperature = s.LlamaTemperature, num_ctx = s.LlamaNumCtx },
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
        });
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Base(s) + "/api/chat") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(true);
            if (!res.IsSuccessStatusCode) throw new LlamaException(await ReadError(res, s).ConfigureAwait(true));
            using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(true)) != null)
            {
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err)) throw new LlamaException("라마가 오류를 돌려줬어요: " + err.GetString());
                if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } text) onToken(text);
                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True) break;
            }
        }
        catch (HttpRequestException ex) { throw Unreachable(s, ex); }
        catch (JsonException ex) { throw new LlamaException("라마의 답을 읽지 못했어요.", ex); }
    }

    private static async Task<string> ReadError(HttpResponseMessage res, AppSettings s)
    {
        string detail = "";
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(true));
            if (doc.RootElement.TryGetProperty("error", out var e)) detail = e.GetString() ?? "";
        }
        catch { }
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            return $"모델 '{s.LlamaModel}'이(가) 없어요. 터미널에서 'ollama pull {s.LlamaModel}'로 받거나, 설정에서 설치된 모델을 골라 주세요.";
        return $"라마 요청이 실패했어요 ({(int)res.StatusCode}) {detail}".Trim();
    }
}

/// <summary>집필 기능별로 라마에게 시킬 말(프롬프트). 글 자체는 항상 [원고] 표시로 감싸서 보낸다.</summary>
internal static class LlamaPrompts
{
    private const string Base = "당신은 한국어 소설 집필을 돕는 보조입니다. 항상 한국어로 답하고, 설명 · 머리말 · 맺음말 · 따옴표 없이 요청한 본문만 출력하세요.";

    private static string Wiki(string wiki) => wiki.Length == 0 ? "" : "\n\n[세계관 · 설정 — 참고만 하고, 여기 없는 사실은 지어내지 마세요]\n" + wiki;

    public static List<LlamaMessage> Continue(string tail, string wiki) => new()
    {
        new("system", Base + " 주어진 원고의 문체 · 시점 · 시제 · 인물 말투를 그대로 지키고, 원고 바로 뒤에 자연스럽게 이어지는 내용을 300~500자 정도로 쓰세요. 원고를 되풀이하지 마세요." + Wiki(wiki)),
        new("user", "[원고 끝부분]\n" + tail + "\n\n위 글에 바로 이어서 쓰세요."),
    };

    public static List<LlamaMessage> Polish(string text, string wiki) => new()
    {
        new("system", Base + " 주어진 글을 더 자연스럽고 매끄럽게 다듬으세요. 뜻 · 인물 말투 · 시점 · 문단 나눔은 그대로 두고, 새 내용을 덧붙이지 마세요. 다듬은 글 전체를 출력하세요." + Wiki(wiki)),
        new("user", "[원고]\n" + text),
    };

    public static List<LlamaMessage> Spelling(string text) => new()
    {
        new("system", Base + " 주어진 글의 맞춤법 · 띄어쓰기 · 오탈자만 고치세요. 문체나 표현, 문단 나눔은 바꾸지 말고 고친 글 전체를 출력하세요."),
        new("user", "[원고]\n" + text),
    };

    public static List<LlamaMessage> Summarize(string text) => new()
    {
        new("system", Base + " 주어진 회차를 3~4문장으로 요약하세요. 주요 사건과 등장인물을 담고, 원고에 없는 내용은 쓰지 마세요."),
        new("user", "[원고]\n" + text),
    };

    public static string ChatSystem(string episode, string wiki)
    {
        var sb = new StringBuilder("당신은 한국어 소설 집필을 돕는 어시스턴트입니다. 항상 한국어로 답하세요. 아래 자료가 있으면 그 안에서 근거를 찾아 답하고, 자료에 없는 사실은 지어내지 말고 모른다고 하세요.");
        if (wiki.Length > 0) sb.Append("\n\n[세계관 · 설정]\n").Append(wiki);
        if (episode.Length > 0) sb.Append("\n\n[지금 쓰는 회차의 끝부분]\n").Append(episode);
        return sb.ToString();
    }
}
