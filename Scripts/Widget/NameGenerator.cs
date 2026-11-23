namespace Cafeum;

/// <summary>
/// 판타지 이름 만들기: 발음이 자연스러운 앞 조각 · 가운데 조각 · 끝 조각을 이어 영어식 이름을 만들고, 한글 발음을 같이 돌려준다.
/// 조각마다 한글을 정해 두고 이어 붙이므로 발음이 이름과 어긋나지 않는다. 이어지는 자리에서 한쪽만 모음이 되게 맞춰(자음 + 모음 · 모음 + 자음) 읽기 어려운 겹침을 막는다.
/// 글자 수 범위와 시작 글자를 지킨다.
/// </summary>
internal static class NameGenerator
{
    private sealed record Part(string L, string K)
    {
        public bool StartsVowel => IsVowel(L[0]);
        public bool EndsVowel => IsVowel(L[^1]);
    }

    private static bool IsVowel(char c) => "aeiouy".Contains(char.ToLowerInvariant(c));
    private static Part[] P(params (string L, string K)[] xs) => xs.Select(x => new Part(x.L, x.K)).ToArray();

    /// <summary>앞 조각(그 자체로 이름이 되기도 한다).</summary>
    private static readonly Part[] Heads = P(
        ("Ael", "에일"), ("Bar", "바르"), ("Cal", "칼"), ("Dar", "다르"), ("Eld", "엘드"), ("Fen", "펜"), ("Gor", "고르"), ("Hal", "할"), ("Kar", "카르"), ("Lor", "로르"),
        ("Mor", "모르"), ("Nor", "노르"), ("Thal", "탈"), ("Ral", "랄"), ("Sar", "사르"), ("Tor", "토르"), ("Val", "발"), ("Zar", "자르"), ("Bran", "브란"), ("Dan", "단"),
        ("Gar", "가르"), ("Hel", "헬"), ("Kel", "켈"), ("Mal", "말"), ("Ner", "네르"), ("Ol", "올"), ("Per", "페르"), ("Rin", "린"), ("Sel", "셀"), ("Tar", "타르"),
        ("Vor", "보르"), ("Wen", "웬"), ("Xan", "잔"), ("Yor", "요르"), ("Quen", "퀜"), ("Quil", "퀼"), ("Ash", "애시"), ("Ed", "에드"), ("Ir", "이르"), ("Os", "오스"),
        ("Un", "운"), ("Ar", "아르"), ("El", "엘"), ("Is", "이스"), ("Jor", "조르"), ("Pal", "팔"), ("Rok", "로크"), ("Wil", "윌"), ("Yan", "얀"), ("Zel", "젤"),
        ("Ara", "아라"), ("Bela", "벨라"), ("Cora", "코라"), ("Dara", "다라"), ("Ela", "엘라"), ("Fia", "피아"), ("Gala", "갈라"), ("Hana", "하나"), ("Ina", "이나"),
        ("Jora", "조라"), ("Kira", "키라"), ("Lira", "리라"), ("Mira", "미라"), ("Nara", "나라"), ("Ori", "오리"), ("Pia", "피아"), ("Rhea", "레아"), ("Sera", "세라"),
        ("Tia", "티아"), ("Una", "우나"), ("Vera", "베라"), ("Wia", "위아"), ("Yara", "야라"), ("Zora", "조라"), ("Qua", "콰"), ("Eva", "에바"), ("Kai", "카이"), ("Lia", "리아"));

    /// <summary>가운데 조각(모음으로 시작해 자음으로 끝난다). 자음으로 끝난 앞 조각 뒤에만 온다.</summary>
    private static readonly Part[] Mids = P(("an", "안"), ("el", "엘"), ("ar", "아르"), ("il", "일"), ("or", "오르"), ("en", "엔"), ("ir", "이르"), ("al", "알"), ("on", "온"), ("ul", "울"));

    /// <summary>끝 조각.</summary>
    private static readonly Part[] Tails = P(
        ("ion", "이온"), ("iel", "이엘"), ("on", "온"), ("an", "안"), ("en", "엔"), ("ar", "아르"), ("or", "오르"), ("as", "아스"), ("us", "우스"), ("ius", "이우스"),
        ("ael", "아엘"), ("eth", "에스"), ("ia", "이아"), ("ira", "이라"), ("ora", "오라"), ("ana", "아나"), ("ena", "에나"), ("ell", "엘"), ("in", "인"),
        ("dor", "도르"), ("mir", "미르"), ("ric", "릭"), ("wyn", "윈"), ("thas", "타스"), ("del", "델"), ("dra", "드라"), ("ris", "리스"), ("ron", "론"), ("rik", "릭"),
        ("lin", "린"), ("las", "라스"), ("vyn", "빈"), ("dil", "딜"), ("rond", "론드"), ("nor", "노르"), ("ven", "벤"), ("sar", "사르"), ("tir", "티르"), ("gan", "간"),
        ("lon", "론"), ("nis", "니스"), ("ther", "서"), ("dan", "단"), ("rel", "렐"), ("sil", "실"));

    private static bool CanJoin(Part left, Part right) => left.EndsVowel != right.StartsVowel; // 한쪽만 모음

    /// <summary>이름 개수만큼 (영어 이름, 한글 발음)을 만든다. 조건에 맞는 이름이 모자라면 만든 만큼만 돌려준다.</summary>
    public static List<(string Name, string Korean)> Make(int min, int max, string start, int count, Random? rng = null)
    {
        rng ??= Random.Shared;
        if (max < min) max = min;
        string st = (start ?? "").Trim();
        var heads = st.Length == 0 ? Heads : Heads.Where(h => h.L.StartsWith(st, StringComparison.OrdinalIgnoreCase)).ToArray();
        var result = new List<(string, string)>();
        var seen = new HashSet<string>();
        if (heads.Length == 0) return result;
        for (int attempt = 0; attempt < count * 400 && result.Count < count; attempt++)
        {
            var head = heads[rng.Next(heads.Length)];
            string lat = head.L, kor = head.K;
            var left = head;
            int kind = rng.Next(100); // 0~19 앞 조각만 · 20~64 앞 + 끝 · 65~ 앞 + 가운데 + 끝
            if (kind >= 20)
            {
                if (kind >= 65 && !left.EndsVowel)
                {
                    var mid = Mids[rng.Next(Mids.Length)];
                    lat += mid.L; kor += mid.K; left = mid;
                }
                var tails = Tails.Where(t => left == head ? CanJoin(left, t) : !t.StartsVowel).ToArray(); // 가운데 조각 뒤에는 자음으로 시작하는 끝 조각
                if (tails.Length == 0) continue;
                var tail = tails[rng.Next(tails.Length)];
                lat += tail.L; kor += tail.K;
            }
            if (lat.Length < min || lat.Length > max) continue;
            if (seen.Add(lat)) result.Add((lat, kor));
        }
        return result;
    }
}
