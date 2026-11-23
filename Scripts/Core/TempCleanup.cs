namespace Cafeum;

/// <summary>
/// 읽는 동안 임시 폴더에 풀어 두는 것들(epub 푼 폴더, txt를 웹 화면으로 만든 문서 = 소설 본문 복사본, zip에서 꺼낸 epub · pdf)을 정리한다.
/// 전에는 어디서도 지우지 않아서 %TEMP%에 계속 쌓였고, 소설 본문이 그대로 남아 있었다.
/// 창을 닫을 때 자기가 쓴 폴더를 지우고, 프로그램을 켤 때 하루 넘게 손대지 않은 옛 폴더를 지운다(비정상 종료로 남은 것).
/// </summary>
internal static class TempCleanup
{
    internal static readonly string[] Roots = { "CafeumEpub", "CafeumTxt", "CafeumZip" };

    private static readonly HashSet<string> held = new(StringComparer.OrdinalIgnoreCase);
    private static System.Threading.Timer? heartbeat;

    /// <summary>
    /// 지금 쓰고 있는 임시 폴더로 등록한다. 쓰는 동안 30분마다 폴더의 "마지막 손댄 때"를 갱신해서, 다른 창(두 번째 실행)이
    /// 켜질 때 하루 넘게 열어 둔 책의 폴더를 옛 폴더로 보고 지우지 않게 한다. 폴더를 지우면(DeleteQuietly) 등록도 풀린다.
    /// </summary>
    public static void Hold(string dir)
    {
        lock (held)
        {
            held.Add(dir);
            Touch(dir);
            heartbeat ??= new System.Threading.Timer(_ => { string[] all; lock (held) all = held.ToArray(); foreach (var d in all) Touch(d); }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        }
    }

    private static void Touch(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); }
        catch (Exception ex) { AppLog.Write($"임시 폴더 시각을 갱신하지 못했어요: {dir}", ex); }
    }

    /// <summary>폴더 하나를 지운다(없거나 못 지우면 조용히 넘어가고 기록만 남긴다).</summary>
    public static void DeleteQuietly(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        lock (held) held.Remove(dir);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) { AppLog.Write($"임시 폴더를 지우지 못했어요: {dir}", ex); }
    }

    /// <summary>프로그램을 켤 때 한 번, 화면을 막지 않게 배경에서.</summary>
    public static void RunInBackground() => Task.Run(() =>
    {
        try
        {
            string temp = Path.GetTempPath();
            foreach (string name in Roots)
            {
                string root = Path.Combine(temp, name);
                if (!Directory.Exists(root)) continue;
                foreach (string dir in Directory.EnumerateDirectories(root))
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) > TimeSpan.FromDays(1)) DeleteQuietly(dir);
            }
        }
        catch (Exception ex) { AppLog.Write("임시 폴더 정리에 실패했어요", ex); }
    });
}
