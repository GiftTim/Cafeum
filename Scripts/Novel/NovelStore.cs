using System.Text;
using System.Text.Json;

namespace Cafeum;

/// <summary>
/// Muvel식 작품 · 회차 구조. 볼트 바로 아래 폴더 하나 = 작품, 그 폴더 바로 안의 .txt 하나 = 회차.
/// 회차 순서와 작품 소개는 작품 폴더의 숨김 파일 <c>.cafeum.json</c>에 둔다(파일 트리에는 점으로 시작하는 이름이 안 보인다).
/// 순서 목록에 없는 txt는 이름의 자연 순서로 뒤에 붙는다 — 탐색기에서 넣은 파일도 그대로 회차가 된다.
/// </summary>
internal static class NovelStore
{
    internal sealed class Meta
    {
        public string? Description { get; set; }
        public List<string> Order { get; set; } = new();
        /// <summary>작품 화면에 붙이는 태그.</summary>
        public List<string>? Tags { get; set; }
        /// <summary>작품 화면 [메모] 탭의 글.</summary>
        public string? Memo { get; set; }
        /// <summary>회차 상태(파일 이름 → 예정 · 진행 · 완료). 없으면 글자 수로 자동(비었으면 예정, 아니면 진행).</summary>
        public Dictionary<string, string>? Status { get; set; }
        /// <summary>회차 유형(파일 이름 → 프롤로그 · 에필로그 · 특별편 · 단편). 없으면 일반 에피소드.</summary>
        public Dictionary<string, string>? Types { get; set; }
        /// <summary>회차 상세 정보 속성(파일 이름 → 요약 · 작가의 말 · 줄바꿈 · 태그 · 수위 · 시점 · 등장).</summary>
        public Dictionary<string, EpisodeProps>? Props { get; set; }
        /// <summary>에피소드 그룹 이름들(보이는 순서).</summary>
        public List<string>? Groups { get; set; }
        /// <summary>회차가 속한 그룹(파일 이름 → 그룹 이름). 없으면 그룹 밖.</summary>
        public Dictionary<string, string>? GroupOf { get; set; }
        /// <summary>참고 자료 독서상태(파일 이름 → 안 읽음 · 읽는 중 · 완독). 없으면 안 읽음.</summary>
        public Dictionary<string, string>? RefStatus { get; set; }
        /// <summary>즐겨찾기한 참고 자료(파일 이름).</summary>
        public List<string>? RefFavorites { get; set; }
    }

    /// <summary>소개를 비워 새 작품을 만들 때 대신 적어 두는 안내 문구(눌러서 고친다).</summary>
    public const string DefaultDescription = "이곳에 소개를 적어주세요";

    // ───────── 표지(작품 폴더의 숨김 파일 .cover.png, 400 × 600 px · 20MB 이하) ─────────

    public const int CoverWidth = 400, CoverHeight = 600;
    /// <summary>표지 파일 크기 상한(20MB).</summary>
    public const long MaxCoverBytes = 20L * 1024 * 1024;

    public static string CoverFile(string novelDir) => Path.Combine(novelDir, ".cover.png");

    /// <summary>저장된 표지를 지운다(없으면 아무 일 없음).</summary>
    public static void DeleteCover(string novelDir)
    {
        try { string p = CoverFile(novelDir); if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); } } catch { }
    }

    /// <summary>이 크기가 표지 크기(400 × 600 px)와 정확히 같은지.</summary>
    public static bool IsCoverSize(int width, int height) => width == CoverWidth && height == CoverHeight;

    /// <summary>저장된 표지(없거나 못 읽으면 null). 파일을 잡고 있지 않게 복사본을 돌려준다.</summary>
    public static Bitmap? LoadCover(string novelDir)
    {
        try
        {
            string p = CoverFile(novelDir);
            if (!File.Exists(p)) return null;
            using var ms = new MemoryStream(File.ReadAllBytes(p));
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    /// <summary>
    /// 고른 이미지를 400 × 600 px PNG로 작품 폴더에 저장한다(크기 · 용량은 부르는 쪽이 먼저 확인).
    /// 고른 파일을 통째로 읽어 둔 뒤 쓰므로 지금 표지 파일을 그대로 고르는 경우에도 된다. 저장은 임시 파일을 거쳐서, 도중에 멈춰도 있던 표지가 그대로 남는다.
    /// </summary>
    public static void SaveCover(string novelDir, string sourcePath)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(sourcePath));
        using var src = Image.FromStream(ms);
        using var bmp = new Bitmap(CoverWidth, CoverHeight);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, CoverWidth, CoverHeight));
        }
        string p = CoverFile(novelDir);
        if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);
        SafeFile.WriteAtomic(p, tmp => bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Png));
    }

    /// <summary>참고 자료의 독서상태를 정한다.</summary>
    public static void SaveRefState(string novelDir, string fileName, string state)
    {
        var meta = LoadMeta(novelDir);
        meta.RefStatus ??= new();
        meta.RefStatus[fileName] = state;
        SaveMeta(novelDir, meta);
    }

    /// <summary>참고 자료의 즐겨찾기를 켜고 끈다.</summary>
    public static void ToggleRefFavorite(string novelDir, string fileName)
    {
        var meta = LoadMeta(novelDir);
        meta.RefFavorites ??= new();
        if (!meta.RefFavorites.Remove(fileName)) meta.RefFavorites.Add(fileName);
        if (meta.RefFavorites.Count == 0) meta.RefFavorites = null;
        SaveMeta(novelDir, meta);
    }

    public static void SaveTags(string novelDir, IEnumerable<string> tags)
    {
        var meta = LoadMeta(novelDir);
        var list = tags.ToList();
        meta.Tags = list.Count == 0 ? null : list;
        SaveMeta(novelDir, meta);
    }

    public static void SaveMemo(string novelDir, string? memo)
    {
        var meta = LoadMeta(novelDir);
        meta.Memo = string.IsNullOrEmpty(memo) ? null : memo;
        SaveMeta(novelDir, meta);
    }

    /// <summary>회차 상태를 정한다(null이면 자동으로 되돌린다).</summary>
    public static void SaveStatus(string episodePath, string? status)
    {
        string dir = Path.GetDirectoryName(episodePath)!;
        var meta = LoadMeta(dir);
        meta.Status ??= new();
        string name = Path.GetFileName(episodePath);
        if (status == null) meta.Status.Remove(name); else meta.Status[name] = status;
        if (meta.Status.Count == 0) meta.Status = null;
        SaveMeta(dir, meta);
    }

    // ───────── 에피소드 그룹(소설 메인 · 파일 트리에서 회차를 묶어 보여 준다) ─────────

    public const string DefaultGroupName = "새 에피소드 그룹";

    public static List<string> Groups(string novelDir) => new(ReadOnlyMeta(novelDir).Groups ?? new());

    /// <summary>회차가 속한 그룹 이름(없으면 null). 없는 그룹을 가리키면 그룹 밖으로 본다.</summary>
    public static string? GroupOfEpisode(string episodePath)
    {
        string? dir = Path.GetDirectoryName(episodePath);
        if (dir == null) return null;
        var meta = ReadOnlyMeta(dir);
        return meta.GroupOf != null && meta.GroupOf.TryGetValue(Path.GetFileName(episodePath), out var g) && (meta.Groups?.Contains(g) ?? false) ? g : null;
    }

    /// <summary>새 그룹을 만든다(이름이 겹치면 뒤에 숫자). 만든 그룹 이름을 돌려준다.</summary>
    public static string CreateGroup(string novelDir, string? name = null)
    {
        var meta = LoadMeta(novelDir);
        meta.Groups ??= new();
        string baseName = string.IsNullOrWhiteSpace(name) ? DefaultGroupName : name.Trim();
        string final = baseName;
        for (int i = 2; meta.Groups.Contains(final); i++) final = $"{baseName} {i}";
        meta.Groups.Add(final);
        SaveMeta(novelDir, meta);
        return final;
    }

    /// <summary>그룹 이름을 바꾼다(겹치면 뒤에 숫자). 바뀐 이름을 돌려준다(못 바꿨으면 원래 이름).</summary>
    public static string RenameGroup(string novelDir, string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == oldName) return oldName;
        var meta = LoadMeta(novelDir);
        if (meta.Groups == null || !meta.Groups.Contains(oldName)) return oldName;
        string final = newName;
        for (int i = 2; meta.Groups.Contains(final) && final != oldName; i++) final = $"{newName} {i}";
        meta.Groups[meta.Groups.IndexOf(oldName)] = final;
        if (meta.GroupOf != null) foreach (var k in meta.GroupOf.Where(kv => kv.Value == oldName).Select(kv => kv.Key).ToList()) meta.GroupOf[k] = final;
        SaveMeta(novelDir, meta);
        return final;
    }

    /// <summary>그룹을 지운다(속해 있던 회차는 지워지지 않고 그룹 밖으로 나온다).</summary>
    public static void DeleteGroup(string novelDir, string name)
    {
        var meta = LoadMeta(novelDir);
        meta.Groups?.Remove(name);
        if (meta.GroupOf != null) foreach (var k in meta.GroupOf.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList()) meta.GroupOf.Remove(k);
        if (meta.Groups is { Count: 0 }) meta.Groups = null;
        if (meta.GroupOf is { Count: 0 }) meta.GroupOf = null;
        SaveMeta(novelDir, meta);
    }

    /// <summary>회차를 그룹에 넣는다(group이 null이면 그룹에서 뺀다).</summary>
    public static void SetEpisodeGroup(string episodePath, string? group)
    {
        string dir = Path.GetDirectoryName(episodePath)!;
        var meta = LoadMeta(dir);
        meta.GroupOf ??= new();
        string name = Path.GetFileName(episodePath);
        if (group == null) meta.GroupOf.Remove(name); else meta.GroupOf[name] = group;
        if (meta.GroupOf.Count == 0) meta.GroupOf = null;
        SaveMeta(dir, meta);
    }

    /// <summary>유형이 프롤로그인데 어느 그룹에도 안 들어간 회차(예전에 유형만 바꿔 둔 것)를 "프롤로그" 그룹에 넣는다. 이미 다른 그룹에 넣어 둔 것은 건드리지 않는다.</summary>
    public static void SyncPrologueGroup(string novelDir)
    {
        var meta = LoadMeta(novelDir);
        if (meta.Types == null) return;
        foreach (var (file, t) in meta.Types.ToList())
        {
            if (t != "프롤로그") continue;
            string path = Path.Combine(novelDir, file);
            if (!File.Exists(path) || GroupOfEpisode(path) != null) continue;
            SaveEpisodeType(path, "프롤로그"); // 그룹 만들기 + 넣기
        }
    }

    // ───────── 회차 상세 정보(유형 · 집필 상태) ─────────

    /// <summary>회차 유형 목록(첫째가 기본).</summary>
    public static readonly string[] EpisodeTypes = { "일반", "프롤로그", "에필로그", "특별편", "단편" };
    /// <summary>집필 상태 이름(저장 값은 StatusKeys: 준비 · 집필 · 퇴고 · 완료 — 소설 메인 목록의 [집필상태]와 같은 값).</summary>
    public static readonly string[] WriteStatusNames = { "집필 예정", "집필 중", "퇴고 중", "집필 완료" };
    private static readonly string[] StatusKeys = { "준비", "집필", "퇴고", "완료" };

    public static string GetEpisodeType(string episodePath)
    {
        string? dir = Path.GetDirectoryName(episodePath);
        if (dir == null) return EpisodeTypes[0];
        var t = ReadOnlyMeta(dir).Types;
        return t != null && t.TryGetValue(Path.GetFileName(episodePath), out var v) && EpisodeTypes.Contains(v) ? v : EpisodeTypes[0];
    }

    public static void SaveEpisodeType(string episodePath, string type)
    {
        string dir = Path.GetDirectoryName(episodePath)!;
        var meta = LoadMeta(dir);
        meta.Types ??= new();
        string name = Path.GetFileName(episodePath);
        if (type == EpisodeTypes[0]) meta.Types.Remove(name); else meta.Types[name] = type;
        if (meta.Types.Count == 0) meta.Types = null;
        SaveMeta(dir, meta);
        // 유형이 프롤로그면 "프롤로그" 그룹(맨 앞)에 자동으로 넣고, 다른 유형으로 바꾸면 그 그룹에서 뺀다.
        const string PrologueGroup = "프롤로그";
        if (type == "프롤로그")
        {
            var m = LoadMeta(dir);
            m.Groups ??= new();
            if (!m.Groups.Contains(PrologueGroup)) { m.Groups.Insert(0, PrologueGroup); SaveMeta(dir, m); }
            SetEpisodeGroup(episodePath, PrologueGroup);
        }
        else if (GroupOfEpisode(episodePath) == PrologueGroup) SetEpisodeGroup(episodePath, null);
    }

    public static EpisodeProps GetProps(string episodePath)
    {
        string? dir = Path.GetDirectoryName(episodePath);
        if (dir == null) return new();
        var p = ReadOnlyMeta(dir).Props;
        return p != null && p.TryGetValue(Path.GetFileName(episodePath), out var v) ? v.Clone() : new();
    }

    public static void SaveProps(string episodePath, EpisodeProps props)
    {
        string dir = Path.GetDirectoryName(episodePath)!;
        var meta = LoadMeta(dir);
        meta.Props ??= new();
        string name = Path.GetFileName(episodePath);
        if (props.IsEmpty) meta.Props.Remove(name); else meta.Props[name] = props;
        if (meta.Props.Count == 0) meta.Props = null;
        SaveMeta(dir, meta);
    }

    /// <summary>집필 상태 이름(집필 예정 · 집필 중 · 퇴고 중 · 집필 완료). 정한 적이 없으면 글이 비었으면 예정, 아니면 집필 중.</summary>
    public static string GetWriteStatus(string episodePath)
    {
        string? dir = Path.GetDirectoryName(episodePath);
        if (dir != null && ReadOnlyMeta(dir).Status is { } st && st.TryGetValue(Path.GetFileName(episodePath), out var v))
        {
            int i = Array.IndexOf(StatusKeys, v switch { "예정" => "준비", "진행" => "집필", _ => v });
            if (i >= 0) return WriteStatusNames[i];
        }
        return CountChars(episodePath) > 0 ? WriteStatusNames[1] : WriteStatusNames[0];
    }

    public static void SaveWriteStatus(string episodePath, string statusName)
    {
        int i = Array.IndexOf(WriteStatusNames, statusName);
        if (i >= 0) SaveStatus(episodePath, StatusKeys[i]);
    }

    /// <summary>회차 글에 이 말이 들어 있는지(작품 안 검색용).</summary>
    public static bool ContainsText(string path, string q)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd().Contains(q, StringComparison.CurrentCultureIgnoreCase);
        }
        catch { return false; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static bool IsShown(FileSystemInfo i) =>
        !i.Name.StartsWith('.') && (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0;

    /// <summary>볼트 안의 작품 폴더들(최근에 고친 작품이 앞).</summary>
    public static List<string> ListNovels(string vault)
    {
        try
        {
            return new DirectoryInfo(vault).EnumerateDirectories().Where(IsShown)
                .OrderByDescending(LastEdited).Select(d => d.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    private static DateTime LastEdited(DirectoryInfo d)
    {
        var t = d.LastWriteTime;
        try { foreach (var f in d.EnumerateFiles("*.txt")) if (f.LastWriteTime > t) t = f.LastWriteTime; } catch { }
        return t;
    }

    public static DateTime LastEdited(string novelDir) => LastEdited(new DirectoryInfo(novelDir));

    /// <summary>작품 폴더의 .cafeum.json을 읽는다. 없거나 깨졌으면(백업에서도 못 살리면) 빈 값. 읽기 · 백업은 JsonFile 참고.</summary>
    public static Meta LoadMeta(string novelDir) =>
        JsonFile.Load<Meta>(NovelFiles.PathOf(novelDir, NovelFiles.MetaFileName), JsonOpts) ?? new Meta();

    public static void SaveMeta(string novelDir, Meta meta)
    {
        string path = NovelFiles.PathOf(novelDir, NovelFiles.MetaFileName);
        JsonFile.Save(path, meta, JsonOpts, hidden: true);
        lock (metaGate) metaCache.Remove(path);
    }

    private static readonly Dictionary<string, (long Size, DateTime Time, Meta Meta)> metaCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object metaGate = new();

    /// <summary>
    /// 읽기만 할 때 쓰는 메타(받은 쪽에서 고치면 안 된다 — 고칠 때는 LoadMeta).
    /// 회차 목록을 그릴 때 회차마다 상태 · 유형 · 그룹을 물어보므로, 파일 크기 · 고친 시각이 같으면 다시 읽지 않는다.
    /// 읽지 못해 빈 값이 된 경우는 담아 두지 않는다(잠금이 풀리면 바로 제대로 읽도록).
    /// </summary>
    private static Meta ReadOnlyMeta(string novelDir)
    {
        string path = NovelFiles.PathOf(novelDir, NovelFiles.MetaFileName);
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new Meta();
            lock (metaGate)
                if (metaCache.TryGetValue(path, out var c) && c.Size == fi.Length && c.Time == fi.LastWriteTimeUtc) return c.Meta;
            var meta = JsonFile.Load<Meta>(path, JsonOpts);
            if (meta == null) return new Meta();
            lock (metaGate) metaCache[path] = (fi.Length, fi.LastWriteTimeUtc, meta);
            return meta;
        }
        catch { return LoadMeta(novelDir); }
    }

    /// <summary>회차(작품 폴더 바로 안의 txt) — 저장해 둔 순서대로, 나머지는 이름 순으로 뒤에.</summary>
    public static List<string> Episodes(string novelDir)
    {
        List<FileInfo> files;
        try { files = new DirectoryInfo(novelDir).EnumerateFiles("*.txt").Where(IsShown).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
        var order = ReadOnlyMeta(novelDir).Order;
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        return files
            .OrderBy(f => rank.TryGetValue(f.Name, out int r) ? r : int.MaxValue)
            .ThenBy(f => f.Name, NaturalStringComparer.Instance)
            .Select(f => f.FullName).ToList();
    }

    public static void SaveOrder(string novelDir, IEnumerable<string> episodePaths)
    {
        var meta = LoadMeta(novelDir);
        meta.Order = episodePaths.Select(Path.GetFileName).OfType<string>().ToList();
        SaveMeta(novelDir, meta);
    }

    public static void SaveDescription(string novelDir, string? description)
    {
        var meta = LoadMeta(novelDir);
        meta.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        SaveMeta(novelDir, meta);
    }

    /// <summary>새 작품 폴더를 만든다(같은 이름이 있으면 뒤에 숫자를 붙인다).</summary>
    public static string CreateNovel(string vault, string title, string? description)
    {
        string name = SafeName(title);
        if (name.Length == 0) name = "새 작품";
        string dir = Unique(Path.Combine(vault, name), "");
        Directory.CreateDirectory(dir);
        SaveDescription(dir, string.IsNullOrWhiteSpace(description) ? DefaultDescription : description); // 비워 두면 안내 문구를 적어 둔다
        return dir;
    }

    /// <summary>맨 뒤에 새 회차("새 에피소드.txt", 겹치면 뒤에 숫자)를 만들고 그 경로를 돌려준다.</summary>
    public static string AddEpisode(string novelDir)
    {
        var eps = Episodes(novelDir);
        string path = Unique(Path.Combine(novelDir, "새 에피소드"), ".txt");
        ForgetEpisode(novelDir, Path.GetFileName(path)); // 지워진 회차가 같은 이름으로 남긴 상태 · 유형 · 그룹 · 서식이 새 회차에 붙지 않게
        using (File.Open(path, FileMode.CreateNew)) { }
        eps.Add(path);
        SaveOrder(novelDir, eps);
        return path;
    }

    /// <summary>
    /// 이 이름의 회차에 남아 있는 기록(상태 · 유형 · 그룹 · 상세 속성 · 글자 서식 파일)을 비운다.
    /// 회차를 휴지통으로 보내도 기록은 남겨 두므로(복원하면 그대로 돌아오게), 같은 이름의 새 회차가 생길 때 이어받지 않도록 여기서 정리한다.
    /// </summary>
    private static void ForgetEpisode(string novelDir, string fileName)
    {
        var meta = LoadMeta(novelDir);
        bool had = false;
        had |= meta.Status?.Remove(fileName) ?? false;
        had |= meta.Types?.Remove(fileName) ?? false;
        had |= meta.Props?.Remove(fileName) ?? false;
        had |= meta.GroupOf?.Remove(fileName) ?? false;
        if (had)
        {
            if (meta.Status is { Count: 0 }) meta.Status = null;
            if (meta.Types is { Count: 0 }) meta.Types = null;
            if (meta.Props is { Count: 0 }) meta.Props = null;
            if (meta.GroupOf is { Count: 0 }) meta.GroupOf = null;
            SaveMeta(novelDir, meta);
        }
        DeleteFormatFile(novelDir, fileName);
    }

    private static string FormatFile(string novelDir, string fileName) =>
        Path.Combine(NovelFiles.PathOf(novelDir, NovelFiles.FormatDirName), fileName + ".rtf");

    private static void DeleteFormatFile(string novelDir, string fileName)
    {
        try { string f = FormatFile(novelDir, fileName); if (File.Exists(f)) File.Delete(f); }
        catch (Exception ex) { AppLog.Write($"{fileName}의 글자 서식 파일을 지우지 못했어요", ex); }
    }

    /// <summary>from 이름의 기록을 to 이름으로 옮긴다(to에 남아 있던 옛 기록은 버린다). 비면 null.</summary>
    private static Dictionary<string, T>? MoveKey<T>(Dictionary<string, T>? d, string from, string to)
    {
        if (d == null) return null;
        bool had = d.Remove(from, out var v);
        d.Remove(to);
        if (had) d[to] = v!;
        return d.Count == 0 ? null : d;
    }

    /// <summary>회차 이름을 바꾼다(순서 · 상태 · 그룹 · 유형 · 상세 속성 · 글자 서식도 같이). 새 경로를 돌려준다.</summary>
    public static string RenameEpisode(string path, string newTitle)
    {
        string dir = Path.GetDirectoryName(path)!;
        string safe = SafeName(newTitle);
        if (safe.Length == 0) throw new IOException("이름이 비어 있어요."); // ".txt"라는 숨김 파일이 되어 목록에서 사라지는 것을 막는다
        string target = Path.Combine(dir, safe + ".txt");
        if (string.Equals(target, path, StringComparison.Ordinal)) return path;
        bool sameFile = string.Equals(target, path, StringComparison.OrdinalIgnoreCase); // 대소문자만 바꾸는 경우
        if (!sameFile && File.Exists(target)) throw new IOException($"같은 이름이 이미 있어요: {Path.GetFileName(target)}");
        var eps = Episodes(dir);
        File.Move(path, target);

        // 기록은 한 번에 옮겨 적는다(여러 번 나눠 쓰다 중간에 멈추면 서로 어긋나므로)
        string oldName = Path.GetFileName(path), newName = Path.GetFileName(target);
        var meta = LoadMeta(dir);
        meta.Status = MoveKey(meta.Status, oldName, newName);
        meta.Types = MoveKey(meta.Types, oldName, newName);
        meta.Props = MoveKey(meta.Props, oldName, newName);
        meta.GroupOf = MoveKey(meta.GroupOf, oldName, newName);
        meta.Order = eps.Select(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ? target : p)
            .Select(Path.GetFileName).OfType<string>().ToList();
        SaveMeta(dir, meta);

        // 글자 서식 보관 파일(.cafeum-fmt\이름.rtf)도 새 이름으로 따라가게
        try
        {
            string fmtOld = FormatFile(dir, oldName), fmtNew = FormatFile(dir, newName);
            if (File.Exists(fmtOld))
            {
                if (!sameFile && File.Exists(fmtNew)) File.Delete(fmtNew); // 대소문자만 다른 이름이면 같은 파일이므로 지우면 안 된다
                File.Move(fmtOld, fmtNew);
            }
            else if (!sameFile) DeleteFormatFile(dir, newName); // 옛 서식이 없으면 그 이름에 남아 있던 다른 회차의 서식을 이어받지 않게
        }
        catch (Exception ex) { AppLog.Write($"{oldName}의 글자 서식 파일을 옮기지 못했어요", ex); }
        return target;
    }

    public static string RenameNovel(string novelDir, string newTitle)
    {
        string safe = SafeName(newTitle);
        if (safe.Length == 0) throw new IOException("이름이 비어 있어요.");
        string target = Path.Combine(Path.GetDirectoryName(novelDir)!, safe);
        if (string.Equals(target, novelDir, StringComparison.Ordinal)) return novelDir;
        if (!string.Equals(target, novelDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target)) throw new IOException($"같은 이름의 작품이 이미 있어요: {Path.GetFileName(target)}");
        Directory.Move(novelDir, target);
        return target;
    }

    /// <summary>이름을 쓸 수 없을 때 보여 줄 안내.</summary>
    public static string InvalidNameMessage(string what = "이름") =>
        $"{what}이 비어 있거나 쓸 수 없는 글자(\\ / : * ? \" < > |)가 들어 있어요.\n맨 앞에 점(.)을 쓸 수 없고, CON · NUL · COM1 같은 윈도우 예약어도 쓸 수 없어요.";

    /// <summary>폴더 · 파일 이름으로 쓸 수 있는지. 점으로 시작하는 이름은 숨김으로 보아 목록에서 사라지므로 안 된다.</summary>
    public static bool ValidName(string s)
    {
        string t = s.Trim();
        return t.Length > 0 && t.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !t.StartsWith('.') && !IsReservedName(t);
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>윈도우가 장치 이름으로 쓰는 이름인가(확장자가 붙어도 마찬가지: CON.txt).</summary>
    private static bool IsReservedName(string name)
    {
        int dot = name.IndexOf('.');
        return ReservedNames.Contains((dot >= 0 ? name[..dot] : name).TrimEnd());
    }

    private static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Trim()) sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? ' ' : c);
        string name = sb.ToString().Trim().Trim('.').Trim(); // 앞의 점은 숨김이 되고, 끝의 점은 윈도우가 떼어 버린다
        return IsReservedName(name) ? name + "_" : name;
    }

    private static string Unique(string basePath, string ext)
    {
        string p = basePath + ext;
        for (int i = 2; File.Exists(p) || Directory.Exists(p); i++) p = $"{basePath} ({i}){ext}";
        return p;
    }

    // ───────── 참고 자료(작품 폴더 안 "참고 자료" 폴더: 참고할 소설 · 만화 · 이미지) ─────────

    public const string ReferenceFolder = "참고 자료";
    /// <summary>참고 자료로 넣을 수 있는 파일(소설 · 만화 zip · pdf · 한글 문서 · 이미지).</summary>
    public static readonly string[] ReferenceExtensions = { ".txt", ".epub", ".pdf", ".zip", ".cbz", ".hwp", ".hwpx", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    /// <summary>이 파일이 작품의 "참고 자료" 폴더 안에 있는가.</summary>
    public static bool IsReferencePath(string? path) =>
        !string.IsNullOrEmpty(path) && string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), ReferenceFolder, StringComparison.OrdinalIgnoreCase);

    public static string ReferenceDir(string novelDir) => Path.Combine(novelDir, ReferenceFolder);

    public static List<string> References(string novelDir)
    {
        string dir = ReferenceDir(novelDir);
        if (!Directory.Exists(dir)) return new();
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles().Where(f => IsShown(f) && ReferenceExtensions.Contains(f.Extension.ToLowerInvariant()))
                .OrderBy(f => f.Name, NaturalStringComparer.Instance).Select(f => f.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>파일들을 참고 자료 폴더로 복사한다(같은 이름이 있으면 뒤에 숫자). 넣은 파일 경로들을 돌려준다.</summary>
    public static List<string> AddReferences(string novelDir, IEnumerable<string> files)
    {
        string dir = ReferenceDir(novelDir);
        Directory.CreateDirectory(dir);
        var added = new List<string>();
        foreach (var f in files)
        {
            if (!File.Exists(f) || !ReferenceExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(f)), dir, StringComparison.OrdinalIgnoreCase)) continue; // 이미 들어 있다
            string target = Unique(Path.Combine(dir, Path.GetFileNameWithoutExtension(f)), Path.GetExtension(f));
            ForgetReference(novelDir, Path.GetFileName(target)); // 지운 자료가 같은 이름으로 남긴 독서상태 · 즐겨찾기가 새 자료에 붙지 않게
            File.Copy(f, target);
            added.Add(target);
        }
        return added;
    }

    /// <summary>이 이름의 참고 자료에 남아 있는 독서상태 · 즐겨찾기 기록을 비운다(자료를 휴지통으로 보내도 기록은 남겨 두므로, 같은 이름의 새 자료가 들어올 때 정리한다).</summary>
    private static void ForgetReference(string novelDir, string fileName)
    {
        var meta = LoadMeta(novelDir);
        bool had = meta.RefStatus?.Remove(fileName) ?? false;
        had |= meta.RefFavorites?.Remove(fileName) ?? false;
        if (!had) return;
        if (meta.RefStatus is { Count: 0 }) meta.RefStatus = null;
        if (meta.RefFavorites is { Count: 0 }) meta.RefFavorites = null;
        SaveMeta(novelDir, meta);
    }

    /// <summary>참고 자료 종류별 아이콘(소설 · 만화 · PDF · 이미지).</summary>
    public static string ReferenceGlyph(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".zip" or ".cbz" => "",
        ".pdf" => "",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "",
        _ => "",
    };

    // ───────── 글자 수(파일 크기 · 고친 시각이 같으면 다시 세지 않는다) ─────────

    private static readonly Dictionary<string, (long Size, DateTime Time, int Chars)> charCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool listWithSpaces;
    /// <summary>목록의 글자 수에 공백을 넣을지(바꾸면 센 값을 모두 버린다).</summary>
    public static bool ListCountWithSpaces
    {
        get => listWithSpaces;
        set { if (listWithSpaces == value) return; listWithSpaces = value; lock (charCache) charCache.Clear(); }
    }

    /// <summary>회차의 글자 수(\r은 세지 않아 줄바꿈 \r\n은 한 글자, 공백은 ListCountWithSpaces가 켜져 있을 때만 센다). 못 읽으면 0.</summary>
    public static int CountChars(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return 0;
            lock (charCache)
                if (charCache.TryGetValue(path, out var c) && c.Size == fi.Length && c.Time == fi.LastWriteTimeUtc) return c.Chars;
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true)) text = sr.ReadToEnd();
            int n = 0;
            foreach (char ch in text)
            {
                if (ch == '\r') continue;
                if (!listWithSpaces && char.IsWhiteSpace(ch)) continue; // 기본은 공백 제외(설정: 글자 수 세기 > 목록 글자 수 기준)
                n++;
            }
            lock (charCache) charCache[path] = (fi.Length, fi.LastWriteTimeUtc, n);
            return n;
        }
        catch { return 0; }
    }

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "방금";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}분 전";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}시간 전";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}일 전";
        return t.ToString("yyyy. M. d.");
    }

    /// <summary>화면 글꼴: 깔끔한 Noto Sans KR이 있으면 그것, 없으면 맑은 고딕.</summary>
    public static string UiFontFamily
    {
        get
        {
            if (uiFont != null) return uiFont;
            using var probe = new Font("Noto Sans KR", 10f);
            return uiFont = probe.Name == "Noto Sans KR" ? "Noto Sans KR" : "맑은 고딕";
        }
    }
    private static string? uiFont;
}

/// <summary>이름 · 소개를 묻는 작은 창(작품 만들기 · 이름 바꾸기 · 소개 고치기).</summary>
internal sealed class NovelInputDialog : Form
{
    private readonly TextBox first;
    private readonly TextBox? second;

    public string FirstValue => first.Text.Trim();
    public string SecondValue => second?.Text.Trim() ?? "";

    public NovelInputDialog(string caption, string label1, string value1, string? label2 = null, string? value2 = null, bool firstIsName = true)
    {
        Text = caption;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(new Label { Text = label1, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        first = new TextBox { Text = value1, Width = 380, Margin = new Padding(0, 0, 0, 12) };
        root.Controls.Add(first);
        if (label2 != null)
        {
            root.Controls.Add(new Label { Text = label2, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
            second = new TextBox { Text = value2 ?? "", Width = 380, Height = 84, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, Margin = new Padding(0, 0, 0, 12) };
            root.Controls.Add(second);
        }
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(84, 30) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(84, 30) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons);
        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (firstIsName && !NovelStore.ValidName(first.Text))
            {
                MessageBox.Show(this, NovelStore.InvalidNameMessage(), AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };
        Shown += (_, _) => { first.Focus(); first.SelectAll(); };
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
    }
}
