namespace Cafeum;

/// <summary>
/// 한국어 이름: 엑셀 자료(NameDb)에서 성 · 이름을 뽑아 붙인다. 이름은 자료의 줄을 아무거나 하나(같은 이름이 여러 줄이면 그만큼 더 자주),
/// 성은 자료의 순서(많이 쓰는 순)대로 앞쪽일수록 더 자주 나오게 뽑는다(순위 r의 확률은 1/r에 비례).
/// </summary>
internal static class KoreanNameGenerator
{
    /// <summary>시작 초성으로 고를 수 있는 자음.</summary>
    public static readonly string[] Starts = { "ㄱ", "ㄴ", "ㄷ", "ㄹ", "ㅁ", "ㅂ", "ㅅ", "ㅇ", "ㅈ", "ㅊ", "ㅋ", "ㅌ", "ㅍ", "ㅎ" };
    private static readonly char[] ChoChars = { 'ㄱ', 'ㄲ', 'ㄴ', 'ㄷ', 'ㄸ', 'ㄹ', 'ㅁ', 'ㅂ', 'ㅃ', 'ㅅ', 'ㅆ', 'ㅇ', 'ㅈ', 'ㅉ', 'ㅊ', 'ㅋ', 'ㅌ', 'ㅍ', 'ㅎ' };

    private static bool StartsWithCho(string s, char cho)
    {
        if (s.Length == 0) return false;
        int code = s[0] - 0xAC00;
        return code >= 0 && code < 11172 && ChoChars[code / (21 * 28)] == cho;
    }

    private static string PickSurname(Random rng, string[] pool, double[] cum)
    {
        double x = rng.NextDouble() * cum[^1];
        int lo = 0, hi = cum.Length - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (cum[mid] < x) lo = mid + 1; else hi = mid; }
        return pool[lo];
    }

    /// <summary>gender: "" 전체(B열) · "남" · "여"(C~F열의 인원 비례. 그 열이 비어 있으면 B열).</summary>
    public static List<string> Make(bool withSurname, string gender, string start, int count, Random? rng = null)
    {
        rng ??= Random.Shared;
        NameDb.EnsureLoaded();
        char cho = (start ?? "").Length == 1 ? start![0] : '\0';
        var result = new List<string>();
        if (NameDb.Names.Length == 0) return result;
        var (pool, cum) = gender switch { "남" => NameDb.Male, "여" => NameDb.Female, _ => (NameDb.Names, Array.Empty<double>()) };
        if (pool.Length == 0) { pool = NameDb.Names; cum = Array.Empty<double>(); }

        // 성: 순위가 앞일수록 가중치가 크다(1/순위). 시작 초성을 골랐으면 그 초성의 성만.
        string[] surPool = Array.Empty<string>(); double[] surCum = Array.Empty<double>();
        if (withSurname && NameDb.Surnames.Length > 0)
        {
            var idx = Enumerable.Range(0, NameDb.Surnames.Length).Where(i => cho == '\0' || StartsWithCho(NameDb.Surnames[i], cho)).ToArray();
            surPool = idx.Select(i => NameDb.Surnames[i]).ToArray();
            double run = 0; surCum = idx.Select(i => run += 1.0 / (i + 1)).ToArray();
            if (surPool.Length == 0) return result;
        }
        else if (cho != '\0' && !pool.Any(n => StartsWithCho(n, cho))) return result;

        var seen = new HashSet<string>();
        for (int attempt = 0; attempt < count * 200 && result.Count < count; attempt++)
        {
            string given = cum.Length == 0 ? pool[rng.Next(pool.Length)] : PickSurname(rng, pool, cum); // 인원 비례(누계로 고른다)
            string name;
            if (surPool.Length > 0) name = PickSurname(rng, surPool, surCum) + given;
            else { if (cho != '\0' && !StartsWithCho(given, cho)) continue; name = given; }
            if (seen.Add(name)) result.Add(name);
        }
        return result;
    }
}
