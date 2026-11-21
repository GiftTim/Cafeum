using System.Text.RegularExpressions;

namespace Cafeum;

/// <summary>맞춤법 검사에서 찾은 한 곳: 글 안의 자리, 틀린 말, 추천하는 말, 이유. Source: 0 내장 사전 · 1 AI(라마).</summary>
internal sealed record SpellIssue(int Start, int Length, string Wrong, string Suggest, string Note, int Source = 0);

/// <summary>
/// 내장 맞춤법 사전(인터넷 · AI 없이 바로 동작): 자주 틀리는 낱말 · 활용 · 띄어쓰기를 규칙으로 찾는다.
/// 오탐을 줄이려고 "거의 틀림없는" 것만 담았다 — 문맥이 필요한 것(되/돼 일부, 띄어쓰기 전반 등)은 AI 검사가 맡는다.
/// </summary>
internal static class SpellRules
{
    private sealed record Word(string Wrong, string Right, string Note);

    /// <summary>자주 틀리는 낱말(틀린 말 → 맞는 말). 낱말 안에서도 찾으므로 활용형은 어간 꼴로 적었다.</summary>
    private static readonly Word[] Words =
    {
        new("몇일", "며칠", "'몇 일'이 아니라 '며칠'"), new("몇 일", "며칠", "'며칠'이 맞아요"),
        new("웬지", "왠지", "'왠지'(왜인지)가 맞아요"), new("왠일", "웬일", "'웬일'이 맞아요"), new("왠만", "웬만", "'웬만하다'가 맞아요"),
        new("어떻해", "어떡해", "'어떡해'(어떻게 해)가 맞아요"), new("어떡게", "어떻게", "'어떻게'가 맞아요"),
        new("금새", "금세", "'금세'(금시에)가 맞아요"), new("일일히", "일일이", "'일일이'가 맞아요"), new("오랫만", "오랜만", "'오랜만'이 맞아요"),
        new("설레임", "설렘", "'설렘'이 맞아요"), new("바램", "바람", "'바람'(바라는 일)이 맞아요"), new("희안", "희한", "'희한하다'가 맞아요"),
        new("어의없", "어이없", "'어이없다'가 맞아요"), new("역활", "역할", "'역할'이 맞아요"), new("궂이", "굳이", "'굳이'가 맞아요"),
        new("뵈요", "봬요", "'봬요'(뵈어요)가 맞아요"), new("뵈서", "봬서", "'봬서'(뵈어서)가 맞아요"),
        new("되요", "돼요", "'돼요'(되어요)가 맞아요"), new("되서", "돼서", "'돼서'(되어서)가 맞아요"), new("됬", "됐", "'됐'(되었)이 맞아요"),
        new("않되", "안 돼", "'안 돼'가 맞아요"), new("않돼", "안 돼", "'안 돼'가 맞아요"),
        new("가르켜", "가르쳐", "'가르쳐'(가르치다)가 맞아요"), new("가르켰", "가르쳤", "'가르쳤'(가르치다)이 맞아요"),
        new("삼가하", "삼가", "'삼가다'가 맞아요(삼가하다 ×)"), new("알아맞추", "알아맞히", "'알아맞히다'가 맞아요"),
        new("윗어른", "웃어른", "'웃어른'이 맞아요"), new("윗쪽", "위쪽", "'위쪽'이 맞아요"), new("아랫쪽", "아래쪽", "'아래쪽'이 맞아요"), new("뒷쪽", "뒤쪽", "'뒤쪽'이 맞아요"),
        new("넓다랗", "널따랗", "'널따랗다'가 맞아요"), new("깨끗히", "깨끗이", "'깨끗이'가 맞아요"), new("꼼꼼이", "꼼꼼히", "'꼼꼼히'가 맞아요"),
        new("열심이", "열심히", "'열심히'가 맞아요"), new("간단이", "간단히", "'간단히'가 맞아요"), new("분명이", "분명히", "'분명히'가 맞아요"),
        new("곰곰히", "곰곰이", "'곰곰이'가 맞아요"), new("번번히", "번번이", "'번번이'가 맞아요"),
        new("뒷풀이", "뒤풀이", "'뒤풀이'가 맞아요"), new("웃옷", "윗옷", "'윗옷'이 맞아요"),
        new("치루", "치르", "'치르다'가 맞아요"), new("치뤘", "치렀", "'치렀'(치르다)이 맞아요"), new("치뤄", "치러", "'치러'(치르다)가 맞아요"),
        new("잠궈", "잠가", "'잠가'(잠그다)가 맞아요"), new("잠궜", "잠갔", "'잠갔'(잠그다)이 맞아요"), new("담궈", "담가", "'담가'(담그다)가 맞아요"),
        new("햇갈리", "헷갈리", "'헷갈리다'가 맞아요"), new("어짜피", "어차피", "'어차피'가 맞아요"), new("어줍잖", "어쭙잖", "'어쭙잖다'가 맞아요"),
        new("쌩뚱맞", "생뚱맞", "'생뚱맞다'가 맞아요"), new("제작년", "재작년", "'재작년'이 맞아요"), new("내노라", "내로라", "'내로라하다'가 맞아요"),
        new("비로서", "비로소", "'비로소'가 맞아요"), new("무릎쓰", "무릅쓰", "'무릅쓰다'가 맞아요"), new("무릎쓴", "무릅쓴", "'무릅쓰다'가 맞아요"),
        new("눈꼽", "눈곱", "'눈곱'이 맞아요"), new("설겆이", "설거지", "'설거지'가 맞아요"), new("곱배기", "곱빼기", "'곱빼기'가 맞아요"),
        new("이래뵈", "이래 봬", "'이래 봬도'가 맞아요"),
        new("아니예요", "아니에요", "'아니에요'가 맞아요"),
        new("것같", "것 같", "'것 같다'는 띄어 써요"), new("수 밖에", "수밖에", "'수밖에'는 붙여 써요"),
    };

    /// <summary>ㄹ 받침으로 끝나는 글자인가(할 · 갈 · 먹을 …).</summary>
    private static bool EndsWithRieul(char c) => c is >= '가' and <= '힣' && (c - 0xAC00) % 28 == 8;

    private static bool IsHangul(char c) => c is >= '가' and <= '힣';

    private sealed record Pattern(Regex Rx, Func<Match, (string Suggest, string Note)?> Make);

    /// <summary>활용을 보는 규칙(앞 글자가 ㄹ 받침일 때만 틀린 것).</summary>
    private static readonly Pattern[] Patterns =
    {
        // 할께 · 갈께요 → 할게 · 갈게요
        new(new Regex(@"([가-힣])께(?=요|[\s.,!?…""”’』」~]|$)"), m => EndsWithRieul(m.Groups[1].Value[0]) ? (m.Groups[1].Value + "게", "'-ㄹ게'가 맞아요(할께 ×)") : null),
        // 할껄 → 할걸
        new(new Regex(@"([가-힣])껄(?!껄)"), m => EndsWithRieul(m.Groups[1].Value[0]) ? (m.Groups[1].Value + "걸", "'-ㄹ걸'이 맞아요(할껄 ×)") : null),
        // 할수있다 · 갈수없다 → 할 수 있다 · 갈 수 없다 ('수없이'는 멀쩡한 낱말이라 뺀다)
        new(new Regex(@"([가-힣])수(있|없)(?!이)"), m => EndsWithRieul(m.Groups[1].Value[0]) ? (m.Groups[1].Value + " 수 " + m.Groups[2].Value, "'수 있다 · 수 없다'는 띄어 써요") : null),
        // 학생이예요 → 학생이에요 (받침 있는 말 뒤의 '이에요'. '아이예요'처럼 받침이 없으면 '예요'가 맞다)
        new(new Regex(@"([가-힣])이예요"), m => (m.Groups[1].Value[0] - 0xAC00) % 28 != 0 ? (m.Groups[1].Value + "이에요", "받침 있는 말 뒤에는 '이에요'") : null),
        // 할꺼야 → 할 거야
        new(new Regex(@"([가-힣]) ?꺼(야|예요|에요|라|다|니|지|죠)"), m => EndsWithRieul(m.Groups[1].Value[0]) ? (m.Groups[1].Value + " 거" + m.Groups[2].Value, "'-ㄹ 거'로 적어요(꺼 ×)") : null),
    };

    /// <summary>글 전체에서 틀린 곳을 찾는다(앞에서부터, 겹치면 앞의 것만).</summary>
    public static List<SpellIssue> Find(string text)
    {
        var found = new List<SpellIssue>();
        foreach (var w in Words)
        {
            int i = 0;
            while ((i = text.IndexOf(w.Wrong, i, StringComparison.Ordinal)) >= 0)
            {
                // '빛바램'처럼 낱말의 일부로 멀쩡히 쓰이는 경우는 건드리지 않는다
                if (w.Wrong == "바램" && i > 0 && text[i - 1] == '빛') { i += w.Wrong.Length; continue; }
                found.Add(new SpellIssue(i, w.Wrong.Length, w.Wrong, w.Right, w.Note));
                i += w.Wrong.Length;
            }
        }
        foreach (var p in Patterns)
            foreach (Match m in p.Rx.Matches(text))
                if (p.Make(m) is { } r) found.Add(new SpellIssue(m.Index, m.Length, m.Value, r.Suggest, r.Note));
        found.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.Length.CompareTo(a.Length));
        var result = new List<SpellIssue>();
        int end = -1;
        foreach (var f in found)
        {
            if (f.Start < end) continue; // 앞의 것과 겹친다
            result.Add(f);
            end = f.Start + f.Length;
        }
        return result;
    }

    /// <summary>원문과 AI가 고친 글을 낱말 단위로 견줘 바뀐 곳들을 찾는다(chunkStart는 이 조각이 글 전체에서 시작하는 자리). 너무 많이 바뀌었으면 null.</summary>
    public static List<SpellIssue>? Diff(string original, string corrected, int chunkStart)
    {
        var a = Regex.Matches(original, @"\S+").Cast<Match>().ToList();
        var b = Regex.Matches(corrected, @"\S+").Cast<Match>().Select(m => m.Value).ToList();
        if (a.Count == 0 || a.Count > 600 || b.Count > 700) return null;
        int n = a.Count, k = b.Count;
        var lcs = new int[n + 1, k + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = k - 1; j >= 0; j--)
                lcs[i, j] = a[i].Value == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var list = new List<SpellIssue>();
        int ai = 0, bj = 0, changed = 0;
        while (ai < n || bj < k)
        {
            if (ai < n && bj < k && a[ai].Value == b[bj]) { ai++; bj++; continue; }
            int sa = ai, sb = bj;
            while ((ai < n || bj < k) && !(ai < n && bj < k && a[ai].Value == b[bj]))
            {
                if (ai < n && (bj >= k || lcs[ai + 1, bj] >= lcs[ai, bj + 1])) ai++; else bj++;
            }
            if (ai > sa) // 원문에서 바뀐 낱말이 있어야 자리가 정해진다(끼워 넣기만 한 것은 건너뛴다)
            {
                int s = a[sa].Index, e = a[ai - 1].Index + a[ai - 1].Length;
                string wrong = original.Substring(s, e - s), suggest = string.Join(" ", b.GetRange(sb, bj - sb));
                changed += ai - sa;
                if (suggest.Length > 0 && suggest != wrong) list.Add(new SpellIssue(chunkStart + s, e - s, wrong, suggest, "AI가 고쳤어요", 1));
            }
        }
        if (changed > Math.Max(8, a.Count * 0.4)) return null; // 문장을 다시 쓴 수준이면 맞춤법 목록으로 쓰지 않는다
        return list;
    }
}
