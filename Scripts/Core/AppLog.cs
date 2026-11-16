namespace Cafeum;

/// <summary>
/// 조용히 넘기던 오류를 한 줄씩 남기는 작은 기록장(%LOCALAPPDATA%\Cafeum\cafeum.log).
/// 기록하다가 실패해도 앱은 멈추지 않는다. 파일이 커지면(256KB) 옛 기록은 cafeum.log.old로 넘기고 새로 시작한다.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object gate = new();

    public static string FilePath => Path.Combine(AppSettings.DataDir, "cafeum.log");

    public static void Write(string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* 기록 실패가 앱을 막으면 안 된다 */ }
    }

    public static void Write(string message, Exception ex) => Write($"{message} ({ex.GetType().Name}: {ex.Message})");
}
