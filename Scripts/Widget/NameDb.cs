using System.IO.Compression;
using System.Xml.Linq;

namespace Cafeum;

/// <summary>
/// 한국어 이름 자료(엑셀 파일): [DB] 시트의 A열 = 성(많이 쓰는 순서), B열 = 이름(남녀 구분 없음), C · D열 = 남자 이름 · 인원, E · F열 = 여자 이름 · 인원. 첫 줄은 제목 줄이다.
/// C~F열은 없어도 된다(없으면 남자 · 여자를 고를 때 B열에서 뽑는다). 인원이 클수록 더 자주 나온다.
/// 앱에 기본 파일이 들어 있고, 데이터 폴더(DataDir)에 같은 이름의 파일(한글이름DB.xlsx)이 있으면 그것을 대신 쓴다 — 엑셀로 고쳐서 넣어 두면 위젯의 이름도 바뀐다.
/// </summary>
internal static class NameDb
{
    public const string FileName = "한글이름DB.xlsx";
    public static string UserPath => Path.Combine(AppSettings.DataDir, FileName);

    /// <summary>자료를 읽은 곳(안내 · 오류 표시에 쓴다).</summary>
    public static string Source { get; private set; } = "";
    public static string[] Surnames { get; private set; } = Array.Empty<string>();
    public static string[] Names { get; private set; } = Array.Empty<string>();
    /// <summary>남자 · 여자 이름과 인원(C~F열). 없으면 비어 있다.</summary>
    public static (string[] Names, double[] Cum) Male { get; private set; } = (Array.Empty<string>(), Array.Empty<double>());
    public static (string[] Names, double[] Cum) Female { get; private set; } = (Array.Empty<string>(), Array.Empty<double>());

    private static bool loaded;

    public static void EnsureLoaded() { if (!loaded) Reload(); }

    /// <summary>다시 읽는다: 내 파일이 있고 읽히면 그것, 아니면 앱에 든 기본 파일.</summary>
    public static void Reload()
    {
        loaded = true;
        if (File.Exists(UserPath))
        {
            try
            {
                using var fs = new FileStream(UserPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (Read(fs)) { Source = UserPath; return; }
            }
            catch (Exception ex) { AppLog.Write("이름 DB 파일을 읽지 못해 기본 자료를 써요", ex); }
        }
        using var res = typeof(NameDb).Assembly.GetManifestResourceStream("NameDb.xlsx");
        if (res != null && Read(res)) Source = "기본 자료";
    }

    /// <summary>내 파일 자리에 기본 파일을 복사해 둔다(엑셀로 고치도록). 이미 있으면 그대로 둔다.</summary>
    public static string EnsureUserCopy()
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        if (!File.Exists(UserPath))
        {
            using var res = typeof(NameDb).Assembly.GetManifestResourceStream("NameDb.xlsx");
            if (res != null) { using var o = File.Create(UserPath); res.CopyTo(o); }
        }
        return UserPath;
    }

    private static (string[], double[]) Weighted(List<(string Name, double W)> l)
    {
        double run = 0;
        return (l.Select(x => x.Name).ToArray(), l.Select(x => run += x.W).ToArray());
    }

    private static bool Read(Stream s)
    {
        using var zip = new ZipArchive(s, ZipArchiveMode.Read);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sse)
        {
            using var st = sse.Open();
            foreach (var si in XDocument.Load(st).Root!.Elements(ns + "si")) shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
        }
        // [DB] 시트(없으면 첫 시트)의 파일 찾기
        XDocument wbx; using (var st = zip.GetEntry("xl/workbook.xml")!.Open()) wbx = XDocument.Load(st);
        var sheets = wbx.Root!.Element(ns + "sheets")!.Elements(ns + "sheet").ToList();
        var pick = sheets.FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), "DB", StringComparison.OrdinalIgnoreCase)) ?? sheets.First();
        string rid = (string)pick.Attribute(rel + "id")!;
        XDocument rels; using (var st = zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open()) rels = XDocument.Load(st);
        string target = rels.Root!.Elements().First(e => (string?)e.Attribute("Id") == rid).Attribute("Target")!.Value;
        string path = target.StartsWith('/') ? target[1..] : "xl/" + target;
        var sur = new List<string>(); var names = new List<string>();
        var male = new List<(string, double)>(); var female = new List<(string, double)>();
        using (var st = zip.GetEntry(path)!.Open())
            foreach (var row in XDocument.Load(st).Root!.Descendants(ns + "row"))
            {
                if ((int?)row.Attribute("r") == 1) continue; // 제목 줄
                var cols = new Dictionary<char, string>();
                foreach (var c in row.Elements(ns + "c"))
                {
                    string refAttr = (string?)c.Attribute("r") ?? "";
                    string? text = (string?)c.Attribute("t") switch
                    {
                        "s" => int.TryParse(c.Element(ns + "v")?.Value, out int i) && i >= 0 && i < shared.Count ? shared[i] : null,
                        "inlineStr" => string.Concat(c.Descendants(ns + "t").Select(t => t.Value)),
                        _ => c.Element(ns + "v")?.Value,
                    };
                    text = text?.Trim();
                    if (string.IsNullOrEmpty(text)) continue;
                    if (refAttr.Length > 0 && refAttr[0] is >= 'A' and <= 'F') cols[refAttr[0]] = text;
                }
                if (cols.TryGetValue('A', out var a)) sur.Add(a);
                if (cols.TryGetValue('B', out var b)) names.Add(b);
                if (cols.TryGetValue('C', out var mn)) male.Add((mn, cols.TryGetValue('D', out var mw) && double.TryParse(mw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mv) && mv > 0 ? mv : 1));
                if (cols.TryGetValue('E', out var fn)) female.Add((fn, cols.TryGetValue('F', out var fw) && double.TryParse(fw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fv) && fv > 0 ? fv : 1));
            }
        if (sur.Count == 0 || names.Count == 0) return false;
        Surnames = sur.ToArray(); Names = names.ToArray();
        Male = Weighted(male); Female = Weighted(female);
        return true;
    }
}
