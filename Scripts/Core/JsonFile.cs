using System.Text.Json;

namespace Cafeum;

/// <summary>
/// 설정 · 기록 JSON 파일(settings · library · schedule · pomodoro · canvas, 작품 폴더의 .cafeum.json · 메모)을 안전하게 읽고 쓰는 공용 도구.
///
/// 쓰기: ① 먼저 글자로 만들어 본다(NaN 같은 값에서 여기서 실패하면 기존 파일은 손대지 않는다)
///       ② 이번 실행에서 처음 저장하는 파일이면 지금 파일을 .bak으로 복사해 둔다
///       ③ 임시 파일(.tmp)에 쓴 뒤 본 파일과 바꾼다(쓰다 죽어도 반쪽짜리 파일이 남지 않는다).
///       숨김 파일은 덮어쓰기가 막힐 수 있어 잠깐 풀었다가 다시 숨긴다(hidden: true면 새로 만들 때도 숨긴다).
/// 읽기: 본 파일이 깨졌으면 깨진 파일은 .corrupt로 옮겨 두고(원인 확인용) .bak에서 되살려 본 파일 자리에 다시 놓는다.
///       둘 다 안 되면 null을 돌려준다(= 호출한 쪽이 기본값으로 시작). 파일이 아예 없으면(처음 실행) 조용히 null.
/// 실패는 모두 AppLog(cafeum.log)에 한 줄 남긴다.
/// </summary>
internal static class JsonFile
{
    private static readonly HashSet<string> backedUp = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object gate = new();

    public static T? Load<T>(string path, JsonSerializerOptions? options = null) where T : class =>
        LoadWith(path, text => JsonSerializer.Deserialize<T>(text, options));

    /// <summary>읽는 방법을 직접 정하고 싶을 때(예전 형식도 읽어야 하는 파일). parse가 null을 돌려주거나 예외를 던지면 깨진 파일로 본다.</summary>
    public static T? LoadWith<T>(string path, Func<string, T?> parse) where T : class
    {
        if (!File.Exists(path)) return null;
        string name = System.IO.Path.GetFileName(path);
        try { return Read(path, parse); }
        catch (Exception ex)
        {
            AppLog.Write($"{name}을(를) 읽지 못했어요", ex);
            try { Unhide(path + ".corrupt"); File.Move(path, path + ".corrupt", overwrite: true); }
            catch (Exception moveEx) { AppLog.Write($"{name}을(를) .corrupt로 옮기지 못했어요", moveEx); }
        }

        string bak = path + ".bak";
        if (!File.Exists(bak)) return null;
        try
        {
            var restored = Read(bak, parse);
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

    private static T Read<T>(string file, Func<string, T?> parse) where T : class =>
        parse(File.ReadAllText(file)) ?? throw new InvalidDataException("내용이 비어 있어요");

    /// <summary>저장에 성공하면 true. 실패해도 예외는 던지지 않고 기록만 남긴다.</summary>
    public static bool Save<T>(string path, T value, JsonSerializerOptions? options = null, bool hidden = false)
    {
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
            return false;
        }
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
