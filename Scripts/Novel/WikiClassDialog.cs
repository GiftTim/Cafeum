namespace Cafeum;

/// <summary>
/// "분류 만들기" 창: 대분류(카테고리) · 분류(상위 분류들, 여러 개 가능) · 분류명 · 요약 · [만들기].
/// 분류는 작품에 맞게 직접 만들고 계층으로 연결하는 묶음이다(예: 왕국 → 수도, 마법사 길드 → 구성원). 한 문서를 여러 분류에 넣을 수 있다.
/// </summary>
internal sealed class WikiClassDialog : Form
{
    private readonly string novelDir;
    private readonly TextBox nameBox, summary;
    private readonly ComboBox category;
    private readonly ClassStrip strip;
    private readonly LibraryPalette palette;
    private readonly List<string> parents = new();

    public string NameText => nameBox.Text.Trim();
    public string SummaryText => summary.Text.Trim();
    public string CategoryKey => WikiCategory.All[Math.Max(0, category.SelectedIndex)].Key;
    /// <summary>고른 상위 분류들.</summary>
    public List<string> Parents => parents.ToList();

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public WikiClassDialog(string novelDir, string? initialParent = null, string? initialCategory = null)
    {
        this.novelDir = novelDir;
        palette = LibraryPalette.From(AppShared.LibraryTheme);
        if (!string.IsNullOrWhiteSpace(initialParent)) parents.Add(initialParent.Trim());
        Text = "분류 만들기";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);

        Shown += (_, _) => { Activate(); BringToFront(); };
        var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label L(string s) => new() { Text = s, AutoSize = true, Margin = new Padding(0, 0, 0, 4), Font = new Font(Font, FontStyle.Bold) };

        category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 0, 16, 12) };
        foreach (var c in WikiCategory.All) category.Items.Add(c.Label);
        category.SelectedIndex = Array.FindIndex(WikiCategory.All, c => c.Key == (initialCategory ?? "none"));
        strip = new ClassStrip { Width = 224, Margin = new Padding(0, 0, 0, 12) };
        strip.SetPalette(palette);
        strip.AddRequested += PickParent;
        strip.RemoveRequested += name => { parents.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); RefreshStrip(); };
        nameBox = new TextBox { Width = 440, PlaceholderText = "분류명을 입력하세요 (예: 왕국)", Margin = new Padding(0, 0, 0, 12) };
        summary = new TextBox { Width = 440, Height = 70, Multiline = true, AcceptsReturn = true, PlaceholderText = "어떤 분류인지 한두 줄로 적어 주세요", Margin = new Padding(0, 0, 0, 12) };

        root.Controls.Add(L("대분류 *"), 0, 0);
        root.Controls.Add(L("분류"), 1, 0);
        root.Controls.Add(category, 0, 1);
        root.Controls.Add(strip, 1, 1);
        root.Controls.Add(L("분류명 *"), 0, 2); root.SetColumnSpan(root.GetControlFromPosition(0, 2)!, 2);
        root.Controls.Add(nameBox, 0, 3); root.SetColumnSpan(nameBox, 2);
        root.Controls.Add(L("요약"), 0, 4); root.SetColumnSpan(root.GetControlFromPosition(0, 4)!, 2);
        root.Controls.Add(summary, 0, 5); root.SetColumnSpan(summary, 2);

        var ok = new Button { Text = "✓  만들기", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(96, 32) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons, 0, 6); root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
        AcceptButton = ok;
        KeyPreview = true; // 취소 버튼 없이도 Esc · 창의 X로 닫는다
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; } };
        ok.Click += (_, _) =>
        {
            string name = NameText;
            string? problem = name.Length == 0 ? "분류명을 입력해 주세요."
                : name.IndexOfAny(new[] { '\t', '|', '\r', '\n' }) >= 0 ? "분류명에는 탭 · | · 줄바꿈을 쓸 수 없어요."
                : WikiStore.KnownClasses(novelDir).Contains(name, StringComparer.OrdinalIgnoreCase) ? $"'{name}' 분류가 이미 있어요."
                : parents.Contains(name, StringComparer.OrdinalIgnoreCase) ? "자기 자신을 상위 분류로 둘 수 없어요." : null;
            if (problem != null)
            {
                MessageBox.Show(this, problem, AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };
        Shown += (_, _) => nameBox.Focus();
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
        strip.BackColor = BackColor;
        RefreshStrip();
    }

    private void RefreshStrip()
    {
        strip.SetItems(parents);
        // 상위 분류가 있으면 대분류는 그 상위를 따라간다(맨 위 분류만 대분류를 고른다)
        category.Enabled = parents.Count == 0;
        if (parents.Count > 0) category.SelectedIndex = Math.Max(0, Array.FindIndex(WikiCategory.All, c => c.Key == WikiStore.RootCategory(novelDir, parents[0])));
        strip.Height = Math.Max(S(30), strip.FitHeight(strip.Width));
        strip.Invalidate();
    }

    /// <summary>[분류 추가]: 이 작품의 분류들 중에서 상위 분류를 고른다(없는 이름은 새로 만든다).</summary>
    private void PickParent(Rectangle anchor)
    {
        if (PickPopup.JustClosed) return;
        var choices = WikiStore.KnownClasses(novelDir).Where(c => !parents.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var pop = new PickPopup(palette, choices, "분류", "선택할 분류가 없어요", "맞는 분류가 없어요");
        pop.Picked += AddParent;
        pop.Created += name =>
        {
            try { WikiStore.AddClass(novelDir, name); } catch (Exception ex) { AppLog.Write("분류 목록을 저장하지 못했어요", ex); }
            AddParent(name);
        };
        pop.ShowBelow(anchor, this, S(260));
    }

    private void AddParent(string name)
    {
        if (parents.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        parents.Add(name);
        RefreshStrip();
    }
}
