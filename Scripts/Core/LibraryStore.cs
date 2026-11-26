using System.Text.Json.Serialization;

namespace Cafeum;

/// <summary>읽은 책 한 권의 기록. 읽던 위치와 마지막으로 연 시각, 파일별 서식도 여기에 같이 든다.</summary>
public sealed class BookRecord
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime? LastOpened { get; set; }
    /// <summary>0~1.</summary>
    public double Progress { get; set; }
    /// <summary>txt: 글자 위치 / epub: 챕터 번호 / 만화 zip: 페이지 번호.</summary>
    public int PosA { get; set; }
    /// <summary>epub: 챕터 안에서의 위치(‰).</summary>
    public int PosB { get; set; }
    /// <summary>예전 서재 화면의 즐겨찾기(★). 지금 화면에서는 쓰지 않지만 예전 기록이 사라지지 않게 그대로 둔다.</summary>
    public bool Favorite { get; set; }
    // 파일별 서식(txt · epub). null이면 설정창의 기본값을 따른다.
    public string? LookFont { get; set; }
    /// <summary>영어(라틴) 글자에만 쓸 글꼴. null이면 LookFont를 따른다.</summary>
    public string? LookFontLatin { get; set; }
    public int? LookSize { get; set; }
    public int? LookLine { get; set; }
    public string? LookAlign { get; set; }
    /// <summary>글자 서식(이 파일 전체): 진하게 · 기울임 · 밑줄 · 취소선 · 글자색(#RRGGBB).</summary>
    public bool? LookBold { get; set; }
    public bool? LookItalic { get; set; }
    public bool? LookUnderline { get; set; }
    public bool? LookStrike { get; set; }
    public string? LookColor { get; set; }
    /// <summary>문단 서식(여백 · 첫 줄 · 문단 간격 · 줄 나눔 기준 …). null이면 기본.</summary>
    public ParaFormat? Para { get; set; }
    public bool? LookWrap { get; set; }
    public PaperSetup? Paper { get; set; }
    /// <summary>화면 배율(%). null이면 100.</summary>
    public int? LookZoom { get; set; }
}

/// <summary>
/// 읽은 책들의 기록(읽던 위치 · 마지막으로 연 시각 · 파일별 서식). %LOCALAPPDATA%\Cafeum\library.json 한 파일에 담는다.
/// 소설 몇 천 권 규모면 JSON 하나로 충분해서 DB 엔진(SQLite)은 쓰지 않았다 — 네이티브 DLL이 하나 더 늘면
/// 단일 exe 배포(csproj 주석 참고)에서 또 "DLL was not found"를 겪을 수 있어서다.
/// UI 스레드에서만 만지는 걸 전제로 한다. 읽기 · 쓰기와 백업은 JsonFile이 맡는다.
/// </summary>
public sealed class LibraryStore
{
    /// <summary>열 수 있는 책 파일 확장자(볼트 왼쪽 패널의 파일 목록도 이걸 따른다).</summary>
    public static readonly string[] BookExtensions = { ".txt", ".md", ".markdown", ".epub", ".pdf", ".zip", ".hwp", ".hwpx" };

    /// <summary>마크다운 글(.md · .markdown)인가: txt처럼 열리고, 읽기 화면에서는 제목 · 인용 · 코드 줄의 모양을 입힌다.</summary>
    public static bool IsMarkdown(string? path) =>
        Path.GetExtension(path ?? "").ToLowerInvariant() is ".md" or ".markdown";

    private sealed class Data
    {
        public List<BookRecord> Books { get; set; } = new();
    }

    private readonly Dictionary<string, BookRecord> books = new(StringComparer.OrdinalIgnoreCase);
    private bool dirty;

    private static string FilePath => System.IO.Path.Combine(AppSettings.DataDir, "library.json");

    public static LibraryStore Load()
    {
        var store = new LibraryStore();
        if (JsonFile.Load<Data>(FilePath, AppSettings.JsonOptions)?.Books is { } list)
            foreach (var b in list)
                if (b != null && !string.IsNullOrEmpty(b.Path)) store.books[b.Path] = b;
        return store;
    }

    public void SaveIfDirty()
    {
        if (!dirty) return;
        if (JsonFile.Save(FilePath, new Data { Books = books.Values.ToList() }, AppSettings.JsonOptions)) dirty = false; // 실패하면 다음 기회에 다시
    }

    /// <summary>경로를 절대경로 한 가지 모양으로 맞춘다. 경로로 쓸 수 없는 글자가 있으면 null.</summary>
    private static string? Normalize(string path)
    {
        try { return System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    public void MarkDirty() => dirty = true;

    public BookRecord? Find(string path) => Normalize(path) is { } key && books.TryGetValue(key, out var b) ? b : null;

    /// <summary>파일을 열 때: 기록이 없으면 새로 만들고, 마지막으로 연 시각을 갱신한다(읽던 위치는 그대로). 경로가 이상하면 저장되지 않는 임시 기록을 준다.</summary>
    public BookRecord Touch(string path)
    {
        if (Normalize(path) is not { } key) return new BookRecord { Path = path };
        if (!books.TryGetValue(key, out var rec))
        {
            rec = new BookRecord { Path = key };
            books[key] = rec;
        }
        try { rec.Size = new FileInfo(key).Length; } catch { }
        rec.LastOpened = DateTime.Now;
        dirty = true;
        return rec;
    }

    public void SavePosition(string path, int posA, int posB, double progress)
    {
        var rec = Find(path);
        if (rec == null) return;
        progress = double.IsNaN(progress) ? 0 : Math.Clamp(progress, 0, 1); // NaN이 들어오면 JSON 저장이 계속 실패하므로 여기서 막는다
        if (rec.PosA == posA && rec.PosB == posB && Math.Abs(rec.Progress - progress) < 0.0005) return; // 안 바뀌었으면 디스크도 안 건드린다
        rec.PosA = posA;
        rec.PosB = posB;
        rec.Progress = progress;
        dirty = true;
    }

    public IEnumerable<BookRecord> Recent(int count) =>
        books.Values.Where(b => b.LastOpened != null).OrderByDescending(b => b.LastOpened).Take(count);
}
