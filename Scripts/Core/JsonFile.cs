using System.Text.Json;

namespace Cafeum;

/// <summary>
/// 설정 · 기록 JSON 파일(settings · library · schedule · pomodoro · canvas, 작품 폴더의 .cafeum.json · 메모)을 안전하게 읽고 쓰는 공용 도구.
///
/// 쓰기: ① 먼저 글자로 만들어 본다(NaN 같은 값에서 여기서 실패하면 기존 파일은 손대지 않는다)
///       ② 이번 실행에서 처음 저장하는 파일이면 지금 파일을 .bak으로 복사해 둔다
///       ③ 임시 파일(.tmp)에 쓴 뒤 본 파일과 바꾼다(쓰다 죽어도 반쪽짜리 파일이 남지 않는다).
///       숨김 파일은 덮어쓰기가 막힐 수 있어 잠깐 풀었다가 다시 숨긴다(hidden: true면 새로 만들 때도 숨긴다).
/// 읽기: 내용이 깨졌으면(해석 실패) 깨진 파일은 .corrupt로 옮겨 두고(원인 확인용) .bak에서 되살려 본 파일 자리에 다시 놓는다.
///       둘 다 안 되면 null을 돌려준다(= 호출한 쪽이 기본값으로 시작). 파일이 아예 없으면(처음 실행) 조용히 null.
///       다른 프로그램이 잡고 있어서 못 읽는 것(IOException 등)은 깨진 게 아니므로 몇 번 다시 해 보고, 그래도 안 되면 파일은 그대로 두고 null을 돌려준다.
///       그런 파일은 다음에 읽기에 성공할 때까지 덮어쓰지 않는다(기본값으로 시작한 쪽이 멀쩡한 파일을 덮어쓰는 일을 막는다).
/// 실패는 모두 AppLog(cafeum.log)에 한 줄 남기고, 저장 실패는 사용자에게도 한 번 알린다(같은 파일은 1분에 한 번).
/// </summary>
internal static class JsonFile
{
    private static readonly HashSet<string> backedUp = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DateTime> lastNotice = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object gate = new();

    public static T? Load<T>(string path, JsonSerializerOptions? options = null) where T : class =>
        LoadWith(path, text => JsonSerializer.Deserialize<T>(text, options));

    /// <summary>읽는 방법을 직접 정하고 싶을 때(예전 형식도 읽어야 하는 파일). parse가 null을 돌려주거나 예외를 던지면 깨진 파일로 본다.</summary>
    public static T? LoadWith<T>(string path, Func<string, T?> parse) where T : class
    {
        if (!File.Exists(path)) { lock (gate) unreadable.Remove(path); return null; }
        string name = System.IO.Path.GetFileName(path);
        string text;
        try { text = ReadText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"{name}을(를) 읽지 못했어요(다른 프로그램이 쓰는 중일 수 있어요. 파일은 그대로 둬요)", ex);
            lock (gate) unreadable.Add(path);
            return null;
        }
        try
        {
            var value = parse(text) ?? throw new InvalidDataException("내용이 비어 있어요");
            lock (gate) unreadable.Remove(path);
            return value;
        }
        catch (Exception ex)
        {
            AppLog.Write($"{name}의 내용이 깨져 있어요", ex);
            try { Unhide(path + ".corrupt"); File.Move(path, path + ".corrupt", overwrite: true); }
            catch (Exception moveEx) { AppLog.Write($"{name}을(를) .corrupt로 옮기지 못했어요", moveEx); }
        }

        lock (gate) unreadable.Remove(path);
        string bak = path + ".bak";
        if (!File.Exists(bak)) return null;
        try
        {
            var restored = parse(ReadText(bak)) ?? throw new InvalidDataException("내용이 비어 있어요");
            File.Copy(bak, path, overwrite: true); // 본 파일 자리에도 되돌려 둔다(다음에 읽을 때 또 "없음"으로 보지 않게)
            AppLog.Write($"{name}을(를) 백업(.bak)에서 되살렸어요");
            return restored;
        }
        catch (Exception ex)
        {
            AppLog.Write($"{name}.bak으로 되살리지 못했어요", ex);
            return null;
        }
    }

    /// <summary>잠깐 다른 프로그램이 잡고 있는 파일을 위해 세 번까지 읽어 본다.</summary>
    private static string ReadText(string file)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(file); }
            catch (Exception ex) when (attempt < 3 && (ex is IOException or UnauthorizedAccessException)) { Thread.Sleep(100); }
        }
    }

    /// <summary>저장에 성공하면 true. 실패해도 예외는 던지지 않고 기록만 남기고 사용자에게 알린다.</summary>
    public static bool Save<T>(string path, T value, JsonSerializerOptions? options = null, bool hidden = false)
    {
        bool skip;
        lock (gate) skip = unreadable.Contains(path);
        if (skip)
        {
            string fileName = System.IO.Path.GetFileName(path);
            AppLog.Write($"{fileName}을(를) 읽지 못한 채라 저장하지 않았어요(기존 내용을 지우지 않으려고)");
            NotifyFailed(path, "파일을 읽지 못해서 저장하지 않았어요. 기존 내용을 지우지 않으려는 거예요.\n다른 프로그램이 파일을 쓰고 있지 않은지 확인하고, 필요하면 Cafeum을 다시 켜 주세요.");
            return false;
        }
        try
        {
            string json = JsonSerializer.Serialize(value, options); // 먼저 만들어 본다: 여기서 실패하면 디스크는 그대로
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            BackupOnce(path);
            bool hide = hidden || IsHidden(path);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            Unhide(path);
            File.Move(tmp, path, overwrite: true);
            if (hide) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"{System.IO.Path.GetFileName(path)}을(를) 저장하지 못했어요", ex);
            NotifyFailed(path, "파일을 저장하지 못했어요. 디스크 공간이나 폴더 권한을 확인해 주세요.");
            return false;
        }
    }

    /// <summary>저장 실패를 화면에 한 번 알린다(같은 파일은 1분에 한 번만, 어느 스레드에서 불려도 된다).</summary>
    private static void NotifyFailed(string path, string reason)
    {
        lock (gate)
        {
            if (lastNotice.TryGetValue(path, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(1)) return;
            lastNotice[path] = DateTime.UtcNow;
        }
        try
        {
            var owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
            if (owner == null || owner.IsDisposed || !owner.IsHandleCreated) return;
            string msg = $"'{System.IO.Path.GetFileName(path)}' {reason}\n\n자세한 내용은 cafeum.log에 남겼어요.";
            owner.BeginInvoke(() => MessageBox.Show(owner, msg, AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        catch { /* 알리지 못해도 앱은 계속 */ }
    }

    /// <summary>이번 실행에서 이 파일을 처음 저장할 때, 지금 파일(= 지난 실행이 남긴 멀쩡한 파일)을 .bak으로 복사해 둔다.</summary>
    private static void BackupOnce(string path)
    {
        lock (gate) { if (!backedUp.Add(path)) return; }
        try
        {
            if (!File.Exists(path)) return;
            Unhide(path + ".bak");
            File.Copy(path, path + ".bak", overwrite: true);
        }
        catch (Exception ex) { AppLog.Write($"{System.IO.Path.GetFileName(path)} 백업을 만들지 못했어요", ex); }
    }

    private static bool IsHidden(string path)
    {
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Hidden) != 0; }
        catch { return false; }
    }

    /// <summary>숨김 · 읽기 전용 표시가 있으면 풀어서 덮어쓸 수 있게 한다(없으면 아무것도 안 한다).</summary>
    private static void Unhide(string path)
    {
        try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); } catch { }
    }
}
