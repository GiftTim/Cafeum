namespace Cafeum;

/// <summary>
/// "문서 만들기" 창: 대분류(카테고리) · 분류(여러 개 가능) · 문서명 · 템플릿 선택하기 · 요약 · [만들기].
/// 속성 · 태그는 문서를 연 화면에서 고른다.
/// </summary>
internal sealed class WikiCreateDialog : Form
{
    private readonly string novelDir;
    private readonly TextBox title, summary;
    private readonly ComboBox category;
    private readonly ClassStrip strip;
    private readonly LibraryPalette palette;
    private readonly List<string> classes = new();

    public string TitleText => title.Text.Trim();
    public string SummaryText => summary.Text.Trim();
    public string CategoryKey => WikiCategory.All[Math.Max(0, category.SelectedIndex)].Key;
    /// <summary>고른 분류들(문서를 넣을 폴더).</summary>
    public List<string> Classes => classes.ToList();

    private readonly ComboBox templateBox;
    private readonly List<string> templateNames;
    /// <summary>고른 템플릿 이름(고르지 않았으면 null).</summary>
    public string? TemplateName => !templateBox.Enabled || templateBox.SelectedIndex <= 0 ? null : templateNames[templateBox.SelectedIndex - 1];

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public WikiCreateDialog(string novelDir, string? initialCategory = null, IReadOnlyList<string>? templates = null, string? initialClass = null, bool asTemplate = false)
    {
        this.novelDir = novelDir;
        palette = LibraryPalette.From(AppShared.LibraryTheme);
        templateNames = templates?.ToList() ?? new();
        if (!string.IsNullOrWhiteSpace(initialClass)) classes.Add(initialClass.Trim());
        Text = asTemplate ? "템플릿 만들기" : "문서 만들기";
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

        Shown += (_, _) => { Activate(); BringToFront(); }; // 앱 안에서만 앞으로 온다(다른 프로그램 위로 뜨지는 않는다)
        var root = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill }; // Dock해야 폼 Padding(여백)이 왼쪽 · 위에도 적용된다
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label L(string s) => new() { Text = s, AutoSize = true, Margin = new Padding(0, 0, 0, 4), Font = new Font(Font, FontStyle.Bold) };

        category = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 0, 16, 12) };
        foreach (var c in WikiCategory.All) category.Items.Add(c.Label);
        category.SelectedIndex = Array.FindIndex(WikiCategory.All, c => c.Key == (initialCategory ?? "none"));
        strip = new ClassStrip { Width = 224, Margin = new Padding(0, 0, 0, 12) };
        strip.SetPalette(palette);
        strip.AddRequested += PickClass;
        strip.RemoveRequested += name => { classes.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); RefreshStrip(); };
        title = new TextBox { Width = 440, PlaceholderText = asTemplate ? "템플릿명을 입력하세요 (예: 인물 기본 양식)" : "문서명을 입력하세요 (예: 등장인물 이름)", Margin = new Padding(0, 0, 0, 12) };
        // 템플릿 고르기: 만들어 둔 템플릿이 있을 때만 켜진다(없으면 흐리게). 고르면 카테고리(미분류일 때) · 분류 · 태그 · 속성 · 본문이 그 틀로 채워진다
        templateBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 224, Margin = new Padding(0, 0, 0, 12), Enabled = templateNames.Count > 0 };
        templateBox.Items.Add("템플릿 선택하기");
        foreach (var n in templateNames) templateBox.Items.Add(n);
        templateBox.SelectedIndex = 0;
        // 템플릿을 고르면 그 틀에 정해 둔 대분류 · 분류가 함께 적용된다
        templateBox.SelectedIndexChanged += (_, _) =>
        {
            if (templateBox.SelectedIndex <= 0) return;
            try
            {
                var t = WikiStore.LoadTemplate(this.novelDir, templateNames[templateBox.SelectedIndex - 1]);
                int ci = Array.FindIndex(WikiCategory.All, c => c.Key == t.Category);
                if (ci >= 0 && t.Category != "none") category.SelectedIndex = ci;
                foreach (var tag in t.Tags) AddClass(tag);
            }
            catch (Exception ex) { AppLog.Write("템플릿을 불러오지 못했어요", ex); }
        };
        summary = new TextBox { Width = 440, Height = 90, Multiline = true, AcceptsReturn = true, PlaceholderText = "문서 요약을 입력하세요", Margin = new Padding(0, 0, 0, 12) };

        root.Controls.Add(L("대분류 *"), 0, 0);
        root.Controls.Add(L("분류"), 1, 0);
        root.Controls.Add(category, 0, 1);
        root.Controls.Add(strip, 1, 1);
        root.Controls.Add(L(asTemplate ? "템플릿명 *" : "문서명 *"), 0, 2); root.SetColumnSpan(root.GetControlFromPosition(0, 2)!, 2);
        root.Controls.Add(title, 0, 3); root.SetColumnSpan(title, 2);
        if (!asTemplate) { root.Controls.Add(templateBox, 0, 4); root.SetColumnSpan(templateBox, 2); } // 템플릿을 만들 때는 템플릿 고르기가 필요 없다
        root.Controls.Add(L("요약"), 0, 5); root.SetColumnSpan(root.GetControlFromPosition(0, 5)!, 2);
        root.Controls.Add(summary, 0, 6); root.SetColumnSpan(summary, 2);

        var ok = new Button { Text = "✓  만들기", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(96, 32) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons, 0, 7); root.SetColumnSpan(buttons, 2);
        Controls.Add(root);
        AcceptButton = ok;
        KeyPreview = true; // 취소 버튼 없이도 Esc · 창의 X로 닫는다
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; } };
        ok.Click += (_, _) =>
        {
            if (!NovelStore.ValidName(title.Text))
            {
                MessageBox.Show(this, NovelStore.InvalidNameMessage("제목"), AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };
        Shown += (_, _) => title.Focus();
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
        strip.BackColor = BackColor;
        RefreshStrip();
    }

    private void RefreshStrip()
    {
        strip.SetItems(classes);
        strip.Height = Math.Max(S(30), strip.FitHeight(strip.Width));
        strip.Invalidate();
    }

    /// <summary>[분류 추가]: 이 작품의 분류들 중에서 고른다(없는 이름은 새로 만든다). 여러 개 고를 수 있다.</summary>
    private void PickClass(Rectangle anchor)
    {
        if (PickPopup.JustClosed) return;
        var choices = WikiStore.KnownClasses(novelDir).Where(c => !classes.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var pop = new PickPopup(palette, choices, "분류", "선택할 분류가 없어요", "맞는 분류가 없어요");
        pop.Picked += AddClass;
        pop.Created += name =>
        {
            try { WikiStore.AddClass(novelDir, name, null, CategoryKey); } catch (Exception ex) { AppLog.Write("분류 목록을 저장하지 못했어요", ex); }
            AddClass(name);
        };
        pop.ShowBelow(anchor, this, S(260));
    }

    private void AddClass(string name)
    {
        if (classes.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        classes.Add(name);
        RefreshStrip();
    }
}
