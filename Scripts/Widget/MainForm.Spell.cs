using System.Text;

namespace Cafeum;

/// <summary>
/// 글쓰기 화면의 맞춤법 검사기 위젯(SpellWidget). 글을 쓰는 동안 내장 사전(SpellRules)으로 틀린 곳을 바로 찾아 목록으로 보여 주고,
/// 내 PC에 라마(Ollama)가 켜져 있으면 [AI 검사]로 한 번 더(선택한 글, 없으면 커서 주변) 검사해 목록에 더한다.
/// 고치기는 글 상자에 바로 넣으므로 Ctrl+Z로 되돌릴 수 있다.
/// </summary>
public partial class MainForm
{
    private SpellWidget? spellWidget;
    private bool spellShown;
    private bool spellChecked;                              // 한 번이라도 검사했는가
    private List<SpellIssue> spellRules = new(), spellAi = new(), spellAll = new();
    private readonly System.Windows.Forms.Timer spellTimer = new() { Interval = 700 };
    private CancellationTokenSource? spellCts;
    private string spellStatus = "";

    /// <summary>위젯 모음의 맞춤법 검사기 카드 · 사이드바 아이콘: 위젯을 켜고 끈다.</summary>
    internal void ToggleSpellWidget() { spellShown = !spellShown; SyncSpellWidget(); }
    internal bool SpellWidgetOn => spellWidget is { Visible: true };

    private void InitSpellWidget()
    {
        spellWidget = new SpellWidget { Visible = false };
        editorHost.Controls.Add(spellWidget);
        spellWidget.Moved += SaveSpellPos;
        spellWidget.ExpandedChanged += () => ResizeSpellWidget();
        spellWidget.JumpRequested += i => SpellJump(i);
        spellWidget.FixRequested += i => SpellFix(i);
        spellWidget.FixAllRequested += SpellFixAll;
        spellWidget.AiRequested += RunSpellAi;
        spellTimer.Tick += (_, _) => { spellTimer.Stop(); RefreshSpell(); };
        mainEditor.TextChanged += (_, _) => { if (spellWidget.Visible) { spellTimer.Stop(); spellTimer.Start(); } }; // 쓰다가 잠깐 멈추면 다시 찾는다
        editorHost.SizeChanged += (_, _) => { if (spellWidget.Visible) PlaceSpellWidget(); };
        editorHost.VisibleChanged += (_, _) => { if (spellShown) SyncSpellWidget(); };
        FormClosed += (_, _) => { spellTimer.Dispose(); spellCts?.Cancel(); spellWidget.Parent?.Controls.Remove(spellWidget); };
    }

    private void SyncSpellWidget()
    {
        if (spellWidget == null) return;
        bool show = directEdit && shownKind == ContentKind.Txt && editorHost.Visible && spellShown;
        if (spellWidget.Visible != show)
        {
            if (show) { PlaceSpellWidget(); spellWidget.BringToFront(); }
            spellWidget.Visible = show;
            if (show) { RefreshSpell(); CheckAiAvailable(); } else spellCts?.Cancel();
        }
        if (show) spellWidget.SetColors(mainEditor.BackColor, mainEditor.ForeColor, theme.Link);
    }

    // ───────── 검사 ─────────

    /// <summary>내장 사전으로 글 전체를 다시 검사하고, 아직 맞는 AI 결과를 합쳐 목록을 새로 만든다.</summary>
    private void RefreshSpell()
    {
        if (spellWidget == null || !spellWidget.Visible) return;
        string text = mainEditor.Text;
        spellRules = SpellRules.Find(text);
        // AI가 찾았던 곳: 글이 바뀌었으면 가까운 곳에서 다시 찾고, 없으면 버린다
        var keep = new List<SpellIssue>();
        foreach (var a in spellAi)
        {
            int at = a.Start;
            if (!(at >= 0 && at + a.Length <= text.Length && string.CompareOrdinal(text, at, a.Wrong, 0, a.Wrong.Length) == 0))
            {
                int from = Math.Max(0, a.Start - 300);
                int to = Math.Min(text.Length, a.Start + 600);
                at = to > from ? text.IndexOf(a.Wrong, from, to - from, StringComparison.Ordinal) : -1;
            }
            if (at < 0) continue;
            if (spellRules.Any(r => at < r.Start + r.Length && r.Start < at + a.Length)) continue; // 내장 사전이 이미 찾은 곳
            keep.Add(a with { Start = at });
        }
        spellAi = keep;
        spellAll = spellRules.Concat(spellAi).OrderBy(i => i.Start).ToList();
        spellChecked = true;
        if (spellCts == null) spellStatus = spellAll.Count == 0 ? "" : $"틀린 곳 {spellAll.Count}개";
        spellWidget.SetIssues(spellAll, spellStatus, spellChecked);
        ResizeSpellWidget();
    }

    // ───────── 고치기 · 이동 ─────────

    private void SpellJump(int index)
    {
        if (index < 0 || index >= spellAll.Count) return;
        var it = spellAll[index];
        if (it.Start + it.Length > mainEditor.TextLength) return;
        mainEditor.Focus();
        mainEditor.Select(it.Start, it.Length);
        tall?.EnsureCaretVisible(false);
    }

    private void SpellFix(int index)
    {
        if (index < 0 || index >= spellAll.Count || mainEditor.ReadOnly) return;
        var it = spellAll[index];
        string text = mainEditor.Text;
        if (it.Start + it.Length > text.Length || string.CompareOrdinal(text, it.Start, it.Wrong, 0, it.Wrong.Length) != 0) { RefreshSpell(); return; } // 그새 글이 바뀌었다
        mainEditor.Select(it.Start, it.Length);
        mainEditor.SelectedText = it.Suggest; // 서식은 그 자리 것을 이어 간다 · Ctrl+Z로 되돌린다
        RefreshSpell();
    }

    private void SpellFixAll()
    {
        if (mainEditor.ReadOnly || spellAll.Count == 0) return;
        string text = mainEditor.Text;
        int fixedCount = 0;
        foreach (var it in spellAll.OrderByDescending(i => i.Start)) // 뒤에서부터 바꿔야 앞의 자리가 어긋나지 않는다
        {
            if (it.Start + it.Length > text.Length || string.CompareOrdinal(text, it.Start, it.Wrong, 0, it.Wrong.Length) != 0) continue;
            mainEditor.Select(it.Start, it.Length);
            mainEditor.SelectedText = it.Suggest;
            fixedCount++;
        }
        RefreshSpell();
        ShowToast($"{fixedCount}곳을 고쳤어요 (Ctrl+Z로 되돌릴 수 있어요)");
    }

    // ───────── AI(라마) 검사 ─────────

    /// <summary>라마(Ollama)가 켜져 있는지 확인해서 [AI 검사] 단추를 켜고 끈다(안 켜져 있으면 흐리게).</summary>
    private async void CheckAiAvailable()
    {
        if (spellWidget == null) return;
        try { var models = await LlamaClient.ListModelsAsync(settings); spellWidget.SetAi(models.Count > 0, spellCts != null); }
        catch (LlamaException) { spellWidget.SetAi(false, false); }
        catch (Exception) { spellWidget.SetAi(false, false); }
    }

    private async void RunSpellAi()
    {
        if (spellWidget == null) return;
        if (spellCts != null) { spellCts.Cancel(); return; } // 돌고 있으면 [중지]
        if (mainEditor.ReadOnly) return;
        string text = mainEditor.Text;
        int selStart = mainEditor.SelectionStart, selLen = mainEditor.SelectionLength;
        // 검사할 범위: 고른 글, 없으면 커서 주변(앞뒤 3,000자까지 문단 단위로)
        int from, to;
        if (selLen > 0) { from = selStart; to = selStart + selLen; }
        else
        {
            from = Math.Max(0, selStart - 3000); to = Math.Min(text.Length, selStart + 3000);
            if (from > 0) { int nl = text.IndexOf('\n', from); if (nl >= 0 && nl < selStart) from = nl + 1; }
            if (to < text.Length) { int nl = text.LastIndexOf('\n', to - 1, to - Math.Max(from, selStart)); if (nl > selStart) to = nl; }
        }
        if (text[from..to].Trim().Length == 0) { ShowToast("검사할 글이 없어요. 글을 쓰거나 선택해 주세요"); return; }

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

        spellCts = new CancellationTokenSource();
        var ct = spellCts.Token;
        spellWidget.SetAi(true, true);
        var found = new List<SpellIssue>();
        bool tooMany = false;
        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                spellStatus = $"AI 검사 중 {i + 1}/{chunks.Count}…";
                spellWidget.SetIssues(spellAll, spellStatus, spellChecked);
                var (start, chunk) = chunks[i];
                var sb = new StringBuilder();
                await LlamaClient.ChatAsync(settings, LlamaPrompts.Spelling(chunk), t => sb.Append(t), ct);
                string fixedText = sb.ToString().Trim();
                if (fixedText.Length == 0) continue;
                var diff = SpellRules.Diff(chunk, fixedText.Replace("\r\n", "\n"), start);
                if (diff == null) { tooMany = true; continue; }
                found.AddRange(diff);
            }
            // 이번에 검사한 범위의 옛 AI 결과는 새 결과로 바꾼다
            spellAi = spellAi.Where(a => a.Start + a.Length <= from || a.Start >= to).Concat(found).ToList();
            spellStatus = tooMany && found.Count == 0 ? "AI가 문장을 많이 바꿔서 목록으로 못 만들었어요" : "";
            ShowToast(found.Count == 0 ? "AI 검사: 더 고칠 곳을 못 찾았어요" : $"AI 검사: {found.Count}곳을 더 찾았어요");
        }
        catch (OperationCanceledException) { spellStatus = "검사를 멈췄어요"; }
        catch (LlamaException ex) { spellStatus = ""; ShowToast(ex.Message); CheckAiAvailable(); }
        finally
        {
            spellCts?.Dispose(); spellCts = null;
            spellWidget?.SetAi(true, false);
            RefreshSpell();
        }
    }

    // ───────── 자리 · 크기 ─────────

    private Control SpellHost => TopLevelControl ?? (Control)editorHost;

    private void SaveSpellPos()
    {
        if (spellWidget == null) return;
        var host = SpellHost;
        settings.SpellRightGap = Math.Max(0, host.ClientSize.Width - spellWidget.Right);
        settings.SpellTop = Math.Max(0, spellWidget.Top);
        settings.Save();
    }

    /// <summary>목록 길이 · 접기에 따라 크기를 바꾼다(위쪽 · 오른쪽 끝은 그대로).</summary>
    private void ResizeSpellWidget()
    {
        if (spellWidget == null) return;
        var size = spellWidget.DesiredSize();
        if (spellWidget.Size == size) return;
        spellWidget.Bounds = new Rectangle(spellWidget.Left + spellWidget.Width - size.Width, spellWidget.Top, size.Width, size.Height);
        if (settings.SpellTop >= 0) SaveSpellPos();
    }

    private void PlaceSpellWidget()
    {
        if (spellWidget == null) return;
        var host = SpellHost;
        if (spellWidget.Parent != host)
        {
            Point? screen = spellWidget.Parent != null ? spellWidget.Parent.PointToScreen(spellWidget.Location) : null;
            spellWidget.Parent?.Controls.Remove(spellWidget);
            host.Controls.Add(spellWidget);
            if (screen is Point sp) spellWidget.Location = host.PointToClient(sp);
        }
        var size = spellWidget.DesiredSize();
        if (host.ClientSize.Width < size.Width || host.ClientSize.Height < size.Height || !host.IsHandleCreated)
        {
            spellWidget.Bounds = new Rectangle(new Point(Math.Max(0, host.ClientSize.Width - size.Width - LogicalToDeviceUnits(24)), LogicalToDeviceUnits(70)), size);
            return;
        }
        if (settings.SpellTop >= 0)
        {
            int x = Math.Clamp(host.ClientSize.Width - size.Width - settings.SpellRightGap, 0, Math.Max(0, host.ClientSize.Width - size.Width));
            int y = Math.Clamp(settings.SpellTop, 0, Math.Max(0, host.ClientSize.Height - size.Height));
            spellWidget.Bounds = new Rectangle(x, y, size.Width, size.Height);
            spellWidget.BringToFront();
            return;
        }
        // 처음 자리: 글 칸 오른쪽 위쪽
        int right = editorHost.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - LogicalToDeviceUnits(24);
        var at = host.PointToClient(editorHost.PointToScreen(new Point(Math.Max(0, right - size.Width), LogicalToDeviceUnits(70))));
        spellWidget.Bounds = new Rectangle(at, size);
        spellWidget.BringToFront();
        SaveSpellPos();
    }
}
