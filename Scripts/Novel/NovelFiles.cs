namespace Cafeum;

/// <summary>
/// 작품 폴더 안에 두는 숨김 파일들의 이름과, 예전 이름(.caffeum…)에서 새 이름(.cafeum…)으로 옮기는 일.
/// 예전 버전이 만든 작품 폴더를 처음 열면 한 번 이름을 바꿔 준다. 새 이름이 이미 있으면 그쪽을 쓰고 옛 파일은 건드리지 않는다.
/// 이름을 바꾸지 못하면(다른 프로그램이 쓰는 중 등) 옛 이름 파일을 그대로 읽고 쓴다 — 작품 데이터가 안 보이게 되는 일이 없게.
/// </summary>
internal static class NovelFiles
{
    /// <summary>회차 순서 · 작품 소개 · 상태 · 그룹 · 속성 …</summary>
    public const string MetaFileName = ".cafeum.json";
    public const string MemoFileName = ".cafeum-memo.json";
    public const string MemoGroupFileName = ".cafeum-memo-groups.json";
    /// <summary>글자 서식을 담은 .rtf 보관 폴더.</summary>
    public const string FormatDirName = ".cafeum-fmt";

    private const string NewPrefix = ".cafeum", OldPrefix = ".caffeum";
    private static readonly string[] AllNames = { MetaFileName, MemoFileName, MemoGroupFileName, FormatDirName };
    private static readonly HashSet<string> checkedDirs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object gate = new();

    /// <summary>작품 폴더 안의 숨김 파일/폴더 경로. 예전 이름이 남아 있으면 먼저 새 이름으로 옮기고, 못 옮겼으면 옛 경로를 돌려준다.</summary>
    public static string PathOf(string dir, string name)
    {
        Migrate(dir);
        string path = System.IO.Path.Combine(dir, name);
        string old = System.IO.Path.Combine(dir, OldPrefix + name[NewPrefix.Length..]);
        return !Exists(path) && Exists(old) ? old : path;
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>폴더마다 이번 실행에서 한 번만 확인한다.</summary>
    private static void Migrate(string dir)
    {
        lock (gate) { if (!checkedDirs.Add(dir)) return; }
        foreach (string name in AllNames)
        {
            string old = System.IO.Path.Combine(dir, OldPrefix + name[NewPrefix.Length..]);
            string now = System.IO.Path.Combine(dir, name);
            try
            {
                if (Exists(now))
                {
                    if (Exists(old)) AppLog.Write($"{dir}: {System.IO.Path.GetFileName(now)}와 예전 이름 {System.IO.Path.GetFileName(old)}가 둘 다 있어요. 새 이름을 쓰고 옛 파일은 그대로 둬요");
                    continue;
                }
                if (File.Exists(old)) { File.Move(old, now); AppLog.Write($"{dir}: {System.IO.Path.GetFileName(old)} → {System.IO.Path.GetFileName(now)}"); }
                else if (Directory.Exists(old)) { Directory.Move(old, now); AppLog.Write($"{dir}: {System.IO.Path.GetFileName(old)} → {System.IO.Path.GetFileName(now)}"); }
            }
            catch (Exception ex) { AppLog.Write($"{dir}: {System.IO.Path.GetFileName(old)} 이름을 바꾸지 못했어요(옛 이름 그대로 써요)", ex); }
        }
    }
}
