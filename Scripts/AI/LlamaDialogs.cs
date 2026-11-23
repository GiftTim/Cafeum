namespace Cafeum;

/// <summary>
/// 라마가 쓴 글을 한 토막씩 보여 주는 창(이어쓰기 · 다듬기 · 맞춤법 · 요약 공용).
/// 다 쓴 뒤에 글을 직접 고칠 수 있고, [적용]을 눌러야만 원고에 들어간다(Result).
/// </summary>
internal sealed class LlamaResultDialog : Form
{
    private readonly AppSettings settings;
    private readonly List<LlamaMessage> messages;
    private readonly TextBox output;
    private readonly Label status;
    private readonly Button apply, retry;
    private CancellationTokenSource? cts;

    /// <summary>[적용]을 눌렀을 때의 글(줄바꿈은 \r\n).</summary>
    public string Result => output.Text;

    public LlamaResultDialog(AppSettings settings, string title, string hint, List<LlamaMessage> messages, string applyText)
    {
        this.settings = settings;
        this.messages = messages;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
        ClientSize = new Size(640, 440);
        MinimumSize = new Size(480, 320);
        Padding = new Padding(14);
        FormBorderStyle = FormBorderStyle.Sizable;

        output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(NovelStore.UiFontFamily, 11f) };
        status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 0) };
        apply = new Button { Text = applyText, AutoSize = true, Enabled = false, DialogResult = DialogResult.OK, MinimumSize = new Size(88, 32) };
        retry = new Button { Text = "다시 쓰기", AutoSize = true, Enabled = false, MinimumSize = new Size(88, 32) };
        var close = new Button { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel, MinimumSize = new Size(88, 32) };
        retry.Click += (_, _) => Generate();
        AcceptButton = null;
        CancelButton = close;

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        bottom.Controls.AddRange(new Control[] { close, apply, retry, status });
        var top = new Label { Dock = DockStyle.Top, AutoSize = true, Text = hint, Padding = new Padding(0, 0, 0, 8), MaximumSize = new Size(600, 0) };
        Controls.Add(output);
        Controls.Add(bottom);
        Controls.Add(top);
        output.BringToFront();

        Shown += (_, _) => { Activate(); Generate(); };
        FormClosing += (_, _) => cts?.Cancel();
    }

    private async void Generate()
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        output.ReadOnly = true;
        output.Clear();
        apply.Enabled = retry.Enabled = false;
        status.Text = $"{settings.LlamaModel}이(가) 쓰는 중…";
        bool ok = false;
        try
        {
            await LlamaClient.ChatAsync(settings, messages, t => { if (!IsDisposed && !token.IsCancellationRequested) output.AppendText(t.Replace("\r\n", "\n").Replace("\n", "\r\n")); }, token);
            ok = true;
        }
        catch (OperationCanceledException) { return; } // 다시 쓰기 · 닫기: 새 요청이 이어받는다
        catch (LlamaException ex) { status.Text = ex.Message; }
        if (IsDisposed || token.IsCancellationRequested) return;
        output.ReadOnly = false;
        retry.Enabled = true;
        if (ok)
        {
            output.Text = output.Text.Trim();
            apply.Enabled = output.TextLength > 0;
            status.Text = output.TextLength > 0 ? "다 썼어요. 고칠 곳이 있으면 직접 고친 뒤 적용하세요." : "라마가 빈 답을 줬어요. 다시 써 보세요.";
        }
    }
}

/// <summary>라마와 주고받는 대화 창(자유 대화 · 위키 · 설정 기반 질문). 앱 창 옆에 띄워 둔 채로 계속 글을 쓸 수 있다.</summary>
internal sealed class LlamaChatForm : Form
{
    private const int EpisodeBudget = 1800;
    private const int WikiBudget = 1800;
    private const int HistoryTurns = 12; // 대화 기억은 최근 12줄까지만(글이 길어지면 읽을 수 있는 양을 넘는다)

    private readonly AppSettings settings;
    private readonly Func<int, string> episodeProvider;
    private readonly Func<string, int, string> wikiProvider;
    private readonly RichTextBox transcript;
    private readonly TextBox input;
    private readonly Button send, stop;
    private readonly CheckBox useWiki, useEpisode;
    private readonly List<LlamaMessage> history = new();
    private CancellationTokenSource? cts;

    public LlamaChatForm(AppSettings settings, Func<int, string> episodeProvider, Func<string, int, string> wikiProvider)
    {
        this.settings = settings;
        this.episodeProvider = episodeProvider;
        this.wikiProvider = wikiProvider;
        Text = "라마와 대화";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
        ClientSize = new Size(520, 640);
        MinimumSize = new Size(380, 420);
        Padding = new Padding(12);

        transcript = new RichTextBox { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, DetectUrls = false };
        input = new TextBox { Multiline = true, Height = 64, Dock = DockStyle.Top, ScrollBars = ScrollBars.Vertical, PlaceholderText = "질문이나 부탁을 적고 Enter (줄바꿈은 Shift+Enter)" };
        send = new Button { Text = "보내기", AutoSize = true, MinimumSize = new Size(80, 30) };
        stop = new Button { Text = "멈춤", AutoSize = true, Enabled = false, MinimumSize = new Size(70, 30) };
        var reset = new Button { Text = "새 대화", AutoSize = true, MinimumSize = new Size(80, 30) };
        useWiki = new CheckBox { Text = "위키 · 설정 참고", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 12, 0) };
        useEpisode = new CheckBox { Text = "지금 쓰는 글 참고", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 12, 0) };

        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        options.Controls.AddRange(new Control[] { useWiki, useEpisode });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange(new Control[] { send, stop, reset });
        var bottom = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(input);
        bottom.Controls.Add(options);

        Controls.Add(transcript);
        Controls.Add(bottom);
        transcript.BringToFront();

        send.Click += (_, _) => Send();
        stop.Click += (_, _) => cts?.Cancel();
        reset.Click += (_, _) => { cts?.Cancel(); history.Clear(); transcript.Clear(); Say("라마", $"{settings.LlamaModel}와(과) 새로 이야기해요. 위키나 지금 쓰는 글에 대해 물어보세요.", Color.Gray); };
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); }
        };
        Shown += (_, _) => { Say("라마", $"{settings.LlamaModel}와(과) 이야기해요. 위키나 지금 쓰는 글에 대해 물어보세요.", Color.Gray); input.Focus(); };
        FormClosing += (_, e) => { cts?.Cancel(); if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } }; // 닫아도 대화는 남겨 두고 숨긴다
    }

    private void Say(string who, string text, Color color)
    {
        transcript.SelectionStart = transcript.TextLength;
        transcript.SelectionFont = new Font(Font, FontStyle.Bold);
        transcript.SelectionColor = color;
        transcript.AppendText(who + "\n");
        transcript.SelectionFont = Font;
        transcript.SelectionColor = transcript.ForeColor;
        transcript.AppendText(text + (text.Length > 0 ? "\n\n" : ""));
    }

    private async void Send()
    {
        string q = input.Text.Trim();
        if (q.Length == 0 || !send.Enabled) return;
        input.Clear();
        Say("나", q, Color.SteelBlue);
        Say("라마", "", Color.SeaGreen);

        string episode = useEpisode.Checked ? episodeProvider(EpisodeBudget) : "";
        string wiki = useWiki.Checked ? wikiProvider(q, WikiBudget) : "";
        var messages = new List<LlamaMessage> { new("system", LlamaPrompts.ChatSystem(episode, wiki)) };
        messages.AddRange(history.TakeLast(HistoryTurns));
        messages.Add(new("user", q));

        cts?.Cancel();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        send.Enabled = false;
        stop.Enabled = true;
        var answer = new System.Text.StringBuilder();
        try
        {
            await LlamaClient.ChatAsync(settings, messages, t => { answer.Append(t); transcript.AppendText(t); }, token);
        }
        catch (OperationCanceledException) { transcript.AppendText("\n(멈췄어요)"); }
        catch (LlamaException ex) { transcript.SelectionColor = Color.Firebrick; transcript.AppendText(ex.Message); transcript.SelectionColor = transcript.ForeColor; }
        transcript.AppendText("\n\n");
        if (answer.Length > 0) { history.Add(new("user", q)); history.Add(new("assistant", answer.ToString())); }
        if (IsDisposed) return;
        send.Enabled = true;
        stop.Enabled = false;
        input.Focus();
    }
}

/// <summary>라마 설정: Ollama 주소 · 모델 · 답의 자유도 · 읽을 수 있는 글의 양.</summary>
internal sealed class LlamaSettingsDialog : Form
{
    private readonly AppSettings settings;
    private readonly TextBox endpoint;
    private readonly ComboBox model;
    private readonly NumericUpDown temperature, numCtx;
    private readonly Label status;

    public LlamaSettingsDialog(AppSettings settings)
    {
        this.settings = settings;
        Text = "라마 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10.5f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        endpoint = new TextBox { Text = settings.LlamaEndpoint, Width = 300 };
        // 예전 기본 모델(llama3.1:8b)을 안 받았을 수 있으니 [연결 확인]을 누르면 설치된 모델로 골라 준다
        model = new ComboBox { Text = settings.LlamaModel, Width = 300, DropDownStyle = ComboBoxStyle.DropDown };
        temperature = new NumericUpDown { Minimum = 0, Maximum = 2, DecimalPlaces = 1, Increment = 0.1m, Value = (decimal)Math.Clamp(settings.LlamaTemperature, 0, 2), Width = 80 };
        numCtx = new NumericUpDown { Minimum = 2048, Maximum = 131072, Increment = 1024, Value = Math.Clamp(settings.LlamaNumCtx, 2048, 131072), Width = 100 };
        status = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 8, 0, 0) };
        var test = new Button { Text = "연결 확인 · 모델 목록", AutoSize = true, MinimumSize = new Size(0, 30) };
        var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 30) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 30) };
        AcceptButton = ok;
        CancelButton = cancel;

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        void Row(string label, Control c, string? hint = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 14, 8) });
            if (hint == null) { grid.Controls.Add(c); return; }
            var cell = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            cell.Controls.Add(c);
            cell.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(8, 6, 0, 0) });
            grid.Controls.Add(cell);
        }
        Row("Ollama 주소", endpoint);
        Row("모델", model);
        Row("자유도", temperature, "0 = 늘 비슷한 답 · 높을수록 다양");
        Row("읽는 양", numCtx, "토큰 · 클수록 메모리를 더 써요");
        grid.Controls.Add(new Label());
        grid.Controls.Add(test);
        grid.Controls.Add(new Label());
        grid.Controls.Add(status);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        Controls.Add(grid);
        Controls.Add(buttons);

        test.Click += async (_, _) => await Probe();
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            settings.LlamaEndpoint = endpoint.Text.Trim().Length > 0 ? endpoint.Text.Trim() : "http://127.0.0.1:11434";
            settings.LlamaModel = model.Text.Trim().Length > 0 ? model.Text.Trim() : "llama3.2:3b";
            settings.LlamaTemperature = (double)temperature.Value;
            settings.LlamaNumCtx = (int)numCtx.Value;
            settings.Save();
        };
    }

    private async Task Probe()
    {
        status.ForeColor = ForeColor;
        status.Text = "확인하는 중…";
        var probe = new AppSettings { LlamaEndpoint = endpoint.Text };
        try
        {
            var names = await LlamaClient.ListModelsAsync(probe);
            string current = model.Text;
            model.Items.Clear();
            model.Items.AddRange(names.Cast<object>().ToArray());
            model.Text = current;
            if (names.Count == 0)
            {
                status.ForeColor = Color.DarkOrange;
                status.Text = "연결됐지만 받아 둔 모델이 없어요. 터미널에서 'ollama pull llama3.1:8b'를 실행해 주세요.";
            }
            else
            {
                if (!names.Contains(current)) model.Text = names[0];
                status.ForeColor = Color.SeaGreen;
                status.Text = $"연결됐어요. 설치된 모델 {names.Count}개 — 목록에서 고를 수 있어요.";
            }
        }
        catch (LlamaException ex) { status.ForeColor = Color.Firebrick; status.Text = ex.Message; }
    }
}
