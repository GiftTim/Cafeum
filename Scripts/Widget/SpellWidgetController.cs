using System.Text;

namespace Cafeum;

/// <summary>
/// 에피소드 창의 맞춤법 검사기 위젯(SpellWidget)을 붙이고 다룬다. 글을 쓰는 동안 내장 사전(SpellRules)으로 틀린 곳을 바로 찾아 목록으로 보여 주고,
/// 내 PC에 라마(Ollama)가 켜져 있으면 [AI 검사]로 한 번 더(선택한 글, 없으면 커서 주변) 검사해 목록에 더한다.
/// 고치기는 글 상자에 바로 넣으므로 Ctrl+Z로 되돌릴 수 있다.
/// </summary>
internal sealed class SpellWidgetController
{
    private readonly IWriterHost host;
    private readonly SpellWidget widget;
    private readonly FloatingWidgetPlacer placer;
    private bool shown;
    private bool checkedOnce;                              // 한 번이라도 검사했는가
    private List<SpellIssue> rules = new(), ai = new(), all = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 700 };
    private CancellationTokenSource? cts;
    private string status = "";

    public SpellWidgetController(IWriterHost host)
    {
        this.host = host;
        widget = new SpellWidget { Visible = false };
        host.EditorHost.Controls.Add(widget);
        placer = new FloatingWidgetPlacer(host, widget, widget.DesiredSize, widget.SetColors,
            () => (host.Settings.SpellRightGap, host.Settings.SpellTop),
            (gap, top) => { host.Settings.SpellRightGap = gap; host.Settings.SpellTop = top; },
            skipResizeWhenSame: true, placeAfterResize: false);
        widget.Moved += placer.SavePos;
        widget.ExpandedChanged += placer.Resize;
        widget.JumpRequested += Jump;
        widget.FixRequested += Fix;
        widget.FixAllRequested += FixAll;
        widget.AiRequested += RunAi;
        timer.Tick += (_, _) => { timer.Stop(); Refresh(); };
        host.Editor.TextChanged += (_, _) => { if (widget.Visible) { timer.Stop(); timer.Start(); } }; // 쓰다가 잠깐 멈추면 다시 찾는다
        placer.Hook(() => shown, Sync);
        host.FormClosed += (_, _) => { timer.Dispose(); cts?.Cancel(); };
    }

    public bool IsOn => widget.Visible;

    /// <summary>위젯 모음의 맞춤법 검사기 카드 · 사이드바 아이콘: 위젯을 켜고 끈다.</summary>
    public void Toggle() { shown = !shown; Sync(); }

    public void Sync() => placer.Sync(host.TxtEditing && host.EditorHost.Visible && shown, nowShown =>
    {
        if (nowShown) { Refresh(); CheckAiAvailable(); } else cts?.Cancel();
    });

    // ───────── 검사 ─────────

    /// <summary>내장 사전으로 글 전체를 다시 검사하고, 아직 맞는 AI 결과를 합쳐 목록을 새로 만든다.</summary>
    private void Refresh()
    {
        if (!widget.Visible) return;
        string text = host.Editor.Text;
        rules = SpellRules.Find(text);
        // AI가 찾았던 곳: 글이 바뀌었으면 가까운 곳에서 다시 찾고, 없으면 버린다
        var keep = new List<SpellIssue>();
        foreach (var a in ai)
        {
            int at = a.Start;
            if (!(at >= 0 && at + a.Length <= text.Length && string.CompareOrdinal(text, at, a.Wrong, 0, a.Wrong.Length) == 0))
            {
                int from = Math.Max(0, a.Start - 300);
                int to = Math.Min(text.Length, a.Start + 600);
                at = to > from ? text.IndexOf(a.Wrong, from, to - from, StringComparison.Ordinal) : -1;
            }
            if (at < 0) continue;
            if (rules.Any(r => at < r.Start + r.Length && r.Start < at + a.Length)) continue; // 내장 사전이 이미 찾은 곳
            keep.Add(a with { Start = at });
        }
        ai = keep;
        all = rules.Concat(ai).OrderBy(i => i.Start).ToList();
        checkedOnce = true;
        if (cts == null) status = all.Count == 0 ? "" : $"틀린 곳 {all.Count}개";
        widget.SetIssues(all, status, checkedOnce);
        placer.Resize();
    }

    // ───────── 고치기 · 이동 ─────────

    private void Jump(int index)
    {
        if (index < 0 || index >= all.Count) return;
        var it = all[index];
        var editor = host.Editor;
        if (it.Start + it.Length > editor.TextLength) return;
        editor.Focus();
        editor.Select(it.Start, it.Length);
        host.EnsureCaretVisible();
    }

    private void Fix(int index)
    {
        var editor = host.Editor;
        if (index < 0 || index >= all.Count || editor.ReadOnly) return;
        var it = all[index];
        string text = editor.Text;
        if (it.Start + it.Length > text.Length || string.CompareOrdinal(text, it.Start, it.Wrong, 0, it.Wrong.Length) != 0) { Refresh(); return; } // 그새 글이 바뀌었다
        editor.Select(it.Start, it.Length);
        editor.SelectedText = it.Suggest; // 서식은 그 자리 것을 이어 간다 · Ctrl+Z로 되돌린다
        Refresh();
    }

    private void FixAll()
    {
        var editor = host.Editor;
        if (editor.ReadOnly || all.Count == 0) return;
        string text = editor.Text;
        int fixedCount = 0;
        foreach (var it in all.OrderByDescending(i => i.Start)) // 뒤에서부터 바꿔야 앞의 자리가 어긋나지 않는다
        {
            if (it.Start + it.Length > text.Length || string.CompareOrdinal(text, it.Start, it.Wrong, 0, it.Wrong.Length) != 0) continue;
            editor.Select(it.Start, it.Length);
            editor.SelectedText = it.Suggest;
            fixedCount++;
        }
        Refresh();
        host.ShowToast($"{fixedCount}곳을 고쳤어요 (Ctrl+Z로 되돌릴 수 있어요)");
    }

    // ───────── AI(라마) 검사 ─────────

    /// <summary>라마(Ollama)가 켜져 있는지 확인해서 [AI 검사] 단추를 켜고 끈다(안 켜져 있으면 흐리게).</summary>
    private async void CheckAiAvailable()
    {
        try { var models = await LlamaClient.ListModelsAsync(host.Settings); widget.SetAi(models.Count > 0, cts != null); }
        catch (LlamaException) { widget.SetAi(false, false); }
        catch (Exception) { widget.SetAi(false, false); }
    }

    private async void RunAi()
    {
        if (cts != null) { cts.Cancel(); return; } // 돌고 있으면 [중지]
        var editor = host.Editor;
        if (editor.ReadOnly) return;
        string text = editor.Text;
        int selStart = editor.SelectionStart, selLen = editor.SelectionLength;
        // 검사할 범위: 고른 글, 없으면 커서 주변(앞뒤 3,000자까지 문단 단위로)
        int from, to;
        if (selLen > 0) { from = selStart; to = selStart + selLen; }
        else
        {
            from = Math.Max(0, selStart - 3000); to = Math.Min(text.Length, selStart + 3000);
            if (from > 0) { int nl = text.IndexOf('\n', from); if (nl >= 0 && nl < selStart) from = nl + 1; }
            if (to < text.Length) { int nl = text.LastIndexOf('\n', to - 1, to - Math.Max(from, selStart)); if (nl > selStart) to = nl; }
        }
        if (text[from..to].Trim().Length == 0) { host.ShowToast("검사할 글이 없어요. 글을 쓰거나 선택해 주세요"); return; }

        // 문단을 모아 1,500자 안팎의 조각으로 나눈다
        var chunks = new List<(int Start, string Text)>();
        int p = from;
        while (p < to)
        {
            int end = p;
            while (end < to)
            {
                int nl = text.IndexOf('\n', end);
                int lineEnd = nl < 0 || nl >= to ? to : nl + 1;
                if (end > p && lineEnd - p > 1500) break;
                end = lineEnd;
                if (end >= to) break;
            }
            if (end <= p) end = Math.Min(to, p + 1500);
            if (text[p..end].Trim().Length > 0) chunks.Add((p, text[p..end]));
            p = end;
        }
        if (chunks.Count == 0) return;

        cts = new CancellationTokenSource();
        var ct = cts.Token;
        widget.SetAi(true, true);
        var found = new List<SpellIssue>();
        bool tooMany = false;
        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                status = $"AI 검사 중 {i + 1}/{chunks.Count}…";
                widget.SetIssues(all, status, checkedOnce);
                var (start, chunk) = chunks[i];
                var sb = new StringBuilder();
                await LlamaClient.ChatAsync(host.Settings, LlamaPrompts.Spelling(chunk), t => sb.Append(t), ct);
                string fixedText = sb.ToString().Trim();
                if (fixedText.Length == 0) continue;
                var diff = SpellRules.Diff(chunk, fixedText.Replace("\r\n", "\n"), start);
                if (diff == null) { tooMany = true; continue; }
                found.AddRange(diff);
            }
            // 이번에 검사한 범위의 옛 AI 결과는 새 결과로 바꾼다
            ai = ai.Where(a => a.Start + a.Length <= from || a.Start >= to).Concat(found).ToList();
            status = tooMany && found.Count == 0 ? "AI가 문장을 많이 바꿔서 목록으로 못 만들었어요" : "";
            host.ShowToast(found.Count == 0 ? "AI 검사: 더 고칠 곳을 못 찾았어요" : $"AI 검사: {found.Count}곳을 더 찾았어요");
        }
        catch (OperationCanceledException) { status = "검사를 멈췄어요"; }
        catch (LlamaException ex) { status = ""; host.ShowToast(ex.Message); CheckAiAvailable(); }
        finally
        {
            cts?.Dispose(); cts = null;
            widget.SetAi(true, false);
            Refresh();
        }
    }
}
