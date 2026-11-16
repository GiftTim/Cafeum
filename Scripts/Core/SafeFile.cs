using System.Text;

namespace Cafeum;

/// <summary>
/// 사용자의 글(소설 원고 · 위키 · epub)을 파일에 안전하게 쓰는 도구.
/// File.WriteAllText는 원본 파일을 바로 비우고 새로 쓰기 때문에, 쓰는 도중 앱이 죽거나 전원이 나가면 원고가 반쪽으로 잘린 채 남는다.
/// 여기서는 먼저 옆의 임시 파일(.cafeum-tmp)에 끝까지 쓰고, 다 쓴 뒤에 원본과 바꾼다 — 중간에 멈춰도 원본은 그대로다.
/// </summary>
internal static class SafeFile
{
    private const string TmpExt = ".cafeum-tmp";

    public static void WriteAllText(string path, string content, Encoding encoding) =>
        WriteAtomic(path, tmp => File.WriteAllText(tmp, content, encoding));

    /// <summary>write가 임시 경로에 내용을 다 쓰면, 그 파일로 path를 바꿔치기한다. 실패하면 임시 파일은 지우고 오류를 그대로 던진다(원본은 그대로).</summary>
    public static void WriteAtomic(string path, Action<string> write)
    {
        string tmp = path + TmpExt;
        try
        {
            try { File.Delete(tmp); } catch { /* 지난번에 남은 임시 파일 */ }
            write(tmp);
            if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// 저장하지 못한 글을 데이터 폴더의 recover 폴더에 따로 남긴다(같은 이름이면 덮어쓴다). 성공하면 그 경로, 못 하면 null.
    /// 닫는 중이나 닫은 뒤에는 저장 실패를 화면에서 볼 수 없으므로, 쓰던 글이 통째로 사라지지 않게 하는 마지막 안전장치다.
    /// </summary>
    public static string? WriteRescueCopy(string fileName, string content)
    {
        try
        {
            string dir = Path.Combine(AppSettings.DataDir, "recover");
            Directory.CreateDirectory(dir);
            var bad = Path.GetInvalidFileNameChars();
            string safe = new(fileName.Select(c => bad.Contains(c) ? '_' : c).ToArray());
            string path = Path.Combine(dir, safe);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            AppLog.Write($"저장하지 못한 글을 복구 폴더에 남겼어요: {path}");
            return path;
        }
        catch (Exception ex) { AppLog.Write("복구 사본도 만들지 못했어요", ex); return null; }
    }

    /// <summary>
    /// preferred 인코딩으로 content를 글자 하나 잃지 않고 저장할 수 있으면 그대로, 못 하면 UTF-8(BOM 없음).
    /// 예전 한글 txt(CP949)에 이모지처럼 그 인코딩에 없는 글자를 쓰면, 그냥 저장할 때 조용히 '?'로 바뀌어 글자가 사라지기 때문이다.
    /// </summary>
    public static Encoding PickEncoding(string content, Encoding preferred)
    {
        if (preferred.CodePage is 65001 or 1200 or 1201 or 12000 or 12001) return preferred; // UTF-8 · UTF-16 · UTF-32는 모든 글자를 담는다
        try
        {
            var strict = CodePagesEncodingProvider.Instance.GetEncoding(preferred.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
            strict?.GetByteCount(content);
            return preferred;
        }
        catch (EncoderFallbackException) { return new UTF8Encoding(false); }
    }
}
