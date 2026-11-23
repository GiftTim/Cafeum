using System.Text;

namespace Cafeum;

/// <summary>
/// 도구 > AI (라마): 내 PC의 Ollama로 라마를 불러 이어쓰기 · 문장 다듬기 · 맞춤법 검사 · 회차 요약 · 대화(위키 · 설정 기반 질문)를 한다.
/// 라마가 쓴 글은 먼저 결과 창에 보여 주고, [적용]을 눌러야만 원고에 들어간다(적용한 뒤에도 Ctrl+Z로 되돌릴 수 있다).
/// </summary>
public partial class MainForm
{
    private LlamaChatForm? llamaChat;

    /// <summary>[도구] 메뉴 맨 끝에 [AI (라마)] 묶음을 넣는다.</summary>
    private void AddLlamaMenu(ToolStripMenuItem tools)
    {
        var ai = new ToolStripMenuItem("AI (라마)");
        var cont = new ToolStripMenuItem("이어쓰기…", null, (_, _) => RunLlamaContinue());
        var polish = new ToolStripMenuItem("문장 다듬기… (선택한 글 · 없으면 지금 문단)", null, (_, _) => RunLlamaRewrite(spelling: false));
        var spell = new ToolStripMenuItem("맞춤법 검사… (선택한 글 · 없으면 지금 문단)", null, (_, _) => RunLlamaRewrite(spelling: true));
        var summary = new ToolStripMenuItem("이 회차 요약…", null, (_, _) => RunLlamaSummary());
        var chat = new ToolStripMenuItem("대화 · 위키 질문…", null, (_, _) => ShowLlamaChat());
        var cfg = new ToolStripMenuItem("라마 설정…", null, (_, _) => { using var dlg = new LlamaSettingsDialog(settings); dlg.ShowDialogOver(HostForm); });
        ai.DropDownItems.AddRange(new ToolStripItem[] { cont, polish, spell, summary, new ToolStripSeparator(), chat, cfg });
        ai.DropDownOpening += (_, _) =>
        {
            bool txt = shownKind == ContentKind.Txt && !txtWeb;
            cont.Enabled = polish.Enabled = spell.Enabled = txt && !mainEditor.ReadOnly; // 글을 고칠 수 있을 때만
            summary.Enabled = txt;
        };
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add(ai);
    }

    private bool LlamaReady()
    {
        if (shownKind == ContentKind.Txt && !txtWeb) return true;
        ShowToast("고칠 수 있는 글에서만 쓸 수 있어요");
        return false;
    }

    /// <summary>커서 앞 글의 끝부분(최대 max글자). 문장이 중간에 잘리지 않게 줄 앞에서 시작하려고 애쓴다.</summary>
    private string LlamaTail(int max)
    {
        string text = mainEditor.Text;
        int caret = Math.Clamp(mainEditor.SelectionStart, 0, text.Length);
        int start = Math.Max(0, caret - max);
        if (start > 0 && text.IndexOf('\n', start, caret - start) is int nl and >= 0 && nl - start < max / 3) start = nl + 1;
        return text[start..caret].Trim();
    }

    /// <summary>이 소설의 위키에서 질문 · 지금 글에 나온 문서를 먼저, 나머지는 위키 순서대로 budget글자까지 모은다.</summary>
    private string LlamaWiki(string query, int budget)
    {
        if (currentPath == null || Path.GetDirectoryName(currentPath) is not string dir) return "";
        List<WikiDoc> docs;
        try { docs = WikiStore.List(dir); } catch { return ""; }
        string hay = query + "\n" + LlamaTail(1500);
        var ranked = docs.OrderByDescending(d => hay.Contains(d.Title) || d.Aliases.Any(a => a.Length > 0 && hay.Contains(a)));
        var sb = new StringBuilder();
        foreach (var d in ranked)
        {
            string attrs = string.Join(", ", d.Attributes.Take(8).Select(a => $"{a.Key}: {a.Value}"));
            string body = d.Body.Replace("\r\n", "\n");
            if (body.Length > 300) body = body[..300] + "…";
            string entry = $"■ {d.Title} [{WikiCategory.Find(d.Category).Label}] {d.Summary}\n{(attrs.Length > 0 ? attrs + "\n" : "")}{body}\n";
            if (sb.Length + entry.Length > budget) continue; // 큰 문서는 건너뛰고 들어갈 만한 다음 문서를 살핀다
            sb.Append(entry);
        }
        return sb.ToString().Trim();
    }

    /// <summary>모델이 \n으로 준 줄바꿈을 글 상자 모양(\r\n)으로 바꾼다.</summary>
    private static string LlamaFit(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private void RunLlamaContinue()
    {
        if (!LlamaReady()) return;
        string tail = LlamaTail(1800);
        if (tail.Length == 0) { ShowToast("이어 쓸 글이 아직 없어요. 몇 줄 쓰고 다시 해 보세요"); return; }
        using var dlg = new LlamaResultDialog(settings, "AI 이어쓰기", "커서 앞의 글에 이어서 라마가 써요. 마음에 들면 [커서 뒤에 넣기]를 누르세요.",
            LlamaPrompts.Continue(tail, LlamaWiki("", 1800)), "커서 뒤에 넣기");
        if (dlg.ShowDialogOver(HostForm) != DialogResult.OK || dlg.Result.Length == 0) return;
        int at = mainEditor.SelectionStart + mainEditor.SelectionLength;
        mainEditor.SelectionStart = at;
        mainEditor.SelectionLength = 0;
        string add = LlamaFit(dlg.Result);
        bool needSpace = at > 0 && !char.IsWhiteSpace(mainEditor.Text[at - 1]);
        mainEditor.SelectedText = (needSpace ? " " : "") + add; // 한 번에 넣어서 Ctrl+Z로 되돌릴 수 있다
        ShowToast("이어쓴 글을 넣었어요 (Ctrl+Z로 되돌릴 수 있어요)");
    }

    private void RunLlamaRewrite(bool spelling)
    {
        if (!LlamaReady() || mainEditor.ReadOnly) return;
        string text = mainEditor.Text;
        int start = mainEditor.SelectionStart, len = mainEditor.SelectionLength;
        if (len == 0) // 선택한 글이 없으면 커서가 있는 문단
        {
            start = start > 0 ? text.LastIndexOf('\n', Math.Min(start, text.Length) - 1) + 1 : 0;
            int end = text.IndexOfAny(new[] { '\r', '\n' }, Math.Min(mainEditor.SelectionStart, text.Length));
            len = (end < 0 ? text.Length : end) - start;
        }
        string target = text.Substring(start, len);
        if (target.Trim().Length == 0) { ShowToast("다듬을 글이 없어요. 글을 선택하거나 문단 안에 커서를 두세요"); return; }
        if (target.Length > 3000) { ShowToast("한 번에 3,000자까지만 보낼 수 있어요. 더 짧게 선택해 주세요"); return; }

        string name = spelling ? "AI 맞춤법 검사" : "AI 문장 다듬기";
        using var dlg = new LlamaResultDialog(settings, name, $"{(mainEditor.SelectionLength > 0 ? "선택한 글" : "지금 문단")} {target.Length:N0}자를 {(spelling ? "맞춤법만 고쳐요" : "다듬어요")}. 마음에 들면 [바꾸기]를 누르세요.",
            spelling ? LlamaPrompts.Spelling(target) : LlamaPrompts.Polish(target, LlamaWiki("", 1200)), "바꾸기");
        if (dlg.ShowDialogOver(HostForm) != DialogResult.OK || dlg.Result.Length == 0 || dlg.Result == target) return;
        mainEditor.Select(start, len);
        mainEditor.SelectedText = LlamaFit(dlg.Result);
        ShowToast("바꿨어요 (Ctrl+Z로 되돌릴 수 있어요)");
    }

    /// <summary>회차를 요약해서, [저장]하면 상세 정보의 [요약] 속성에 넣는다.</summary>
    private void RunLlamaSummary()
    {
        if (!LlamaReady()) return;
        string text = mainEditor.Text.Trim();
        if (text.Length == 0) { ShowToast("요약할 글이 아직 없어요"); return; }
        if (text.Length > 4000) text = text[..2500] + "\n…\n" + text[^1500..]; // 긴 글은 앞 · 뒤만 보낸다
        using var dlg = new LlamaResultDialog(settings, "AI 회차 요약", "이 회차를 요약해요. [요약에 저장]을 누르면 글 위 상세 정보의 [요약]에 들어가요.", LlamaPrompts.Summarize(text), "요약에 저장");
        if (dlg.ShowDialogOver(HostForm) != DialogResult.OK || dlg.Result.Length == 0) return;
        if (currentPath == null) { Clipboard.SetText(dlg.Result); ShowToast("요약을 클립보드에 복사했어요"); return; }
        var props = NovelStore.GetProps(currentPath);
        props.Summary = dlg.Result.Replace("\r\n", " ").Trim();
        props.Added ??= new();
        if (!props.Added.Contains("summary")) props.Added.Add("summary");
        NovelStore.SaveProps(currentPath, props);
        RefreshWriterProps();
        EpisodeMetaChanged?.Invoke();
        ShowToast("요약을 저장했어요");
    }

    private void ShowLlamaChat()
    {
        if (llamaChat == null || llamaChat.IsDisposed)
        {
            llamaChat = new LlamaChatForm(settings, LlamaTail, LlamaWiki) { Owner = HostForm };
            llamaChat.StartPosition = FormStartPosition.Manual;
            var host = HostForm.Bounds;
            llamaChat.Location = new Point(Math.Max(0, host.Right - llamaChat.Width - 24), host.Top + 48);
        }
        if (HostForm.TopMost) llamaChat.TopMost = true;
        llamaChat.Show();
        llamaChat.Activate();
    }
}
