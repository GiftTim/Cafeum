using System.IO.Compression;
using System.Xml.Linq;

namespace Cafeum;

public sealed record EpubChapter(string Name, string FilePath);

/// <summary>
/// epub(zip 안에 OPF+XHTML로 이루어진 전자책) 파일을 열어 챕터 목록을 뽑아낸다.
/// epub은 zip이므로 통째로 임시 폴더에 풀어두고, 그 안의 XHTML 파일들을 WebView2로 그대로 보여준다
/// (이미지·CSS 같은 상대 경로 참조가 실제 디스크 파일 경로로 자연스럽게 풀리게 하려는 것).
/// </summary>
public sealed class EpubBook
{
    private const int MaxEntries = 50_000;
    private const long MaxTotalBytes = 4L << 30; // 압축을 푼 뒤 합계 4GB

    public string Title { get; init; } = "";
    public List<EpubChapter> Chapters { get; init; } = new();

    /// <summary>progress: 압축 해제 중 바이트 기준 누적 진행률(0.0~1.0). 이미지가 많은 큰 epub을 열 때
    /// 화면에 게이지를 보여주려고 둔 것이라, 안 넘겨도 동작엔 지장 없다.
    /// cancellationToken: 취소되면 풀다 만 임시 폴더를 지우고 OperationCanceledException을 던진다.</summary>
    public static EpubBook Load(string epubPath, string extractDir, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);
        try
        {
            return LoadExtracted(epubPath, extractDir, progress, cancellationToken);
        }
        catch
        {
            // 풀다 만 폴더든, 풀고 나서 epub 형식이 틀렸다고 알게 된 폴더든 남기지 않는다.
            try { Directory.Delete(extractDir, true); } catch { /* 정리 실패는 무시 — 프로그램을 켤 때 오래된 임시 폴더를 지운다(TempCleanup) */ }
            throw;
        }
    }

    private static EpubBook LoadExtracted(string epubPath, string extractDir, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ExtractWithProgress(epubPath, extractDir, progress, cancellationToken);

        // 1) META-INF/container.xml에서 실제 OPF(패키지 문서) 경로를 찾는다.
        string containerPath = Path.Combine(extractDir, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
            throw new InvalidDataException("epub 형식이 아닌 것 같습니다(META-INF/container.xml 없음).");

        XNamespace cns = "urn:oasis:names:tc:opendocument:xmlns:container";
        var containerDoc = XDocument.Load(containerPath);
        string? opfRelPath = containerDoc.Descendants(cns + "rootfile").FirstOrDefault()?.Attribute("full-path")?.Value;
        if (string.IsNullOrEmpty(opfRelPath))
            throw new InvalidDataException("container.xml에서 OPF 경로를 찾을 수 없습니다.");

        string? opfPath = ResolveInside(extractDir, extractDir, opfRelPath);
        if (opfPath == null || !File.Exists(opfPath))
            throw new InvalidDataException("epub 안의 OPF 경로가 올바르지 않습니다.");
        string opfDir = Path.GetDirectoryName(opfPath) ?? extractDir;

        // 2) OPF: manifest(파일 id -> 실제 경로)와 spine(읽는 순서)을 읽는다.
        XNamespace opf = "http://www.idpf.org/2007/opf";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        var opfDoc = XDocument.Load(opfPath);

        string title = opfDoc.Descendants(dc + "title").FirstOrDefault()?.Value?.Trim()
            ?? Path.GetFileNameWithoutExtension(epubPath);

        var manifest = new Dictionary<string, string>();
        foreach (var item in opfDoc.Descendants(opf + "item"))
        {
            string? id = item.Attribute("id")?.Value;
            string? href = item.Attribute("href")?.Value;
            if (id == null || href == null) continue;
            // 풀어 둔 폴더 밖(../ 나 C:\ 같은 경로)을 가리키는 항목은 쓰지 않는다. 그대로 두면 책이 내 컴퓨터의 아무 파일이나 챕터로 띄우고,
            // 수정하기를 켰을 때는 그 파일을 덮어쓸 수도 있다.
            if (ResolveInside(extractDir, opfDir, Uri.UnescapeDataString(href)) is { } inside) manifest[id] = inside;
        }

        var chapters = new List<EpubChapter>();
        int idx = 1;
        foreach (var itemref in opfDoc.Descendants(opf + "itemref"))
        {
            string? idref = itemref.Attribute("idref")?.Value;
            if (idref == null || !manifest.TryGetValue(idref, out var path) || !File.Exists(path)) continue;
            chapters.Add(new EpubChapter(TryGetHtmlTitle(path) ?? $"{idx}장", path));
            idx++;
        }

        if (chapters.Count == 0)
            throw new InvalidDataException("이 epub에서 챕터를 하나도 찾지 못했습니다.");

        return new EpubBook { Title = title, Chapters = chapters };
    }

    /// <summary>
    /// ZipFile.ExtractToDirectory와 동작은 같지만(경로 탈출 방지 포함), 항목을 하나씩 풀면서 바이트
    /// 기준 진행률을 보고할 수 있게 직접 구현했다. 이미지가 많이 든 큰 epub은 이 단계가 시간이 걸린다.
    /// </summary>
    private static void ExtractWithProgress(string zipPath, string destDir, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.ToList();
        long totalBytes = entries.Sum(e => e.Length);
        // 압축 폭탄 방지: 작은 epub이 풀면 수십 GB가 되도록 만든 파일이 임시 폴더 디스크를 채우지 못하게 한도를 둔다.
        if (entries.Count > MaxEntries || totalBytes > MaxTotalBytes)
            throw new InvalidDataException("epub이 너무 커서 열지 않았어요(압축을 풀면 4GB가 넘거나 파일이 너무 많아요).");
        long doneBytes = 0, written = 0;
        string destRoot = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!destPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("잘못된 경로가 포함된 epub입니다."); // zip slip 방지

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destPath); // 디렉터리 항목
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                // 목록에 적힌 크기를 믿지 않고 실제로 쓴 크기를 센다(거짓 크기를 적은 압축 폭탄 대비).
                using var src = entry.Open();
                using var dst = File.Create(destPath);
                var buffer = new byte[81920];
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > MaxTotalBytes) throw new InvalidDataException("epub이 너무 커서 열지 않았어요(압축을 풀면 4GB가 넘어요).");
                    dst.Write(buffer, 0, read);
                }
            }

            doneBytes += entry.Length;
            if (totalBytes > 0) progress?.Report((double)doneBytes / totalBytes);
        }
    }

    /// <summary>baseDir 기준 상대 경로(relative)를 절대 경로로 바꾸되, root 폴더 안이 아니면 null. 이상한 글자가 있어도 null.</summary>
    private static string? ResolveInside(string root, string baseDir, string relative)
    {
        try
        {
            string full = Path.GetFullPath(Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            string rootFull = Path.GetFullPath(root + Path.DirectorySeparatorChar);
            return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>챕터 XHTML의 &lt;title&gt;을 읽어 목록에 조금 더 알아보기 쉬운 이름을 쓴다(실패하면 그냥 null).</summary>
    private static string? TryGetHtmlTitle(string htmlPath)
    {
        try
        {
            var doc = XDocument.Load(htmlPath);
            var t = doc.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }
        catch
        {
            return null;
        }
    }
}
