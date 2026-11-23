using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// 나만의 소설뷰어에서 가져온 기능들: 서재(책장) · 낭독(TTS) · 테마/설정창 · 몰입 모드 · 읽던 자리 기억 ·
/// 끌어놓기로 열기 · 우클릭 메뉴/웹 검색 · 토스트 알림 · 도움말.
/// 원래 MainForm.cs는 이미 크기가 커서, 새 기능은 이 파일에 모으고 MainForm.cs에는 연결 지점만 남겼다.
/// </summary>
public partial class MainForm
{
    // 설정과 서재 기록은 창이 여러 개여도 하나를 같이 쓴다(AppShared).
    private readonly AppSettings settings = AppShared.Settings;
    private readonly LibraryStore library = AppShared.Library;
    private ReaderTheme theme = Themes.All[0];

    /// <summary>지금 이 읽기 창에 열려 있는 파일의 전체 경로(같은 책을 또 열 때 새 창 대신 이 창을 앞으로 가져오려고).</summary>
    public string? CurrentFilePath => string.IsNullOrEmpty(currentPath) ? null : Path.GetFullPath(currentPath);
    private readonly ToastLabel toast = new();
    private readonly TtsEngine tts = new();
    private readonly System.Windows.Forms.Timer positionTimer = new() { Interval = 4000 };
    private readonly System.Windows.Forms.Timer immersionHoverTimer = new() { Interval = 200 };

    private ToolStripMenuItem? autoPlayToggleMenuItem; // 자동 재생 시작/정지(F5) — 만화는 슬라이드쇼, 소설은 낭독
    private ToolStripMenuItem? ttsRateMenu;
    private ToolStripMenuItem? pdfDarkMenuItem;
    private ToolStripMenuItem? immersionMenuItem;
    private ToolStripMenuItem? recentMenu;
    private ToolStripMenuItem? ttsVoiceMenu;
    private readonly List<ToolStripMenuItem> themeMenuItems = new();
    private readonly List<ToolStripMenuItem> ttsRateMenuItems = new();

    // ── 읽던 자리 ──
    private int epubRatioPermille;          // epub: 챕터 안 위치(‰). 주기 타이머가 웹뷰에서 읽어온 마지막 값
    private int? epubRestorePermille;       // 챕터를 새로 불러온 직후 이 위치로 옮긴다
    private int lastEpubTrackedChapter = -1;
    private bool positionQueryBusy;

    // ── 낭독 ──
    private bool ttsActive;
    private int ttsPos;                     // txt: 전체 글 기준 / epub: 챕터 본문 기준, 다음에 읽을 글자 위치
    private string ttsEpubText = "";
    private bool ttsAwaitingChapter;        // 다음 챕터로 넘기는 중 — 로딩이 끝나면 처음부터 이어 읽는다
    private bool ttsInternalNav;            // 낭독 스스로 페이지/챕터를 넘기는 중(직접 넘김 = 낭독 중지 규칙에서 제외)
    private Point editorContextPoint;

    // ── 몰입 모드 ──
    private bool immersive;
    internal bool Immersive => immersive; // 머리 막대의 몰입 버튼 그림이 이 값을 따른다
    private Rectangle immersiveRestoreBounds;
    private FormWindowState immersiveRestoreState;
    private IntPtr immersiveRestoreStyle;

    // ───────────────────────── 메뉴 ─────────────────────────

    private void BuildFeatureMenus(MenuStrip menu, ToolStripMenuItem fileMenu, ToolStripMenuItem viewMenu)
    {
        // 파일: 서재 / 최근 파일 (열기 바로 위·아래)
        fileMenu.DropDownItems.Insert(0, new ToolStripMenuItem("볼트 열기…", null, (_, _) => ShowLibrary()) { ShortcutKeyDisplayString = "Ctrl+L" });
        recentMenu = new ToolStripMenuItem("최근 파일");
        recentMenu.DropDownItems.Add(new ToolStripMenuItem("(없음)") { Enabled = false }); // 드롭다운 화살표가 뜨게 자리만 잡아둔다
        recentMenu.DropDownOpening += (_, _) => FillRecentMenu();
        fileMenu.DropDownItems.Insert(2, recentMenu);
        // 설정은 보기 메뉴가 아니라 파일 메뉴 맨 아래에 둔다(메모장처럼).
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("설정…", null, (_, _) => ShowSettings()) { ShortcutKeyDisplayString = "Ctrl+," });

        // 보기: 테마 / PDF 야간 모드 / 몰입 모드 / 설정
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        var themeMenu = new ToolStripMenuItem("테마");
        foreach (var t in Themes.All)
        {
            var item = new ToolStripMenuItem(t.Name, null, (_, _) => { settings.Theme = t.Name; settings.Save(); AppShared.NotifySettingsChanged(SettingKind.Theme); });
            themeMenuItems.Add(item);
            themeMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(themeMenu);
        pdfDarkMenuItem = new ToolStripMenuItem("PDF 야간 모드", null, (_, _) =>
        {
            settings.PdfDarkMode = !settings.PdfDarkMode;
            settings.Save();
            AppShared.NotifySettingsChanged(SettingKind.Pdf);
        });
        viewMenu.DropDownItems.Add(pdfDarkMenuItem);
        immersionMenuItem = new ToolStripMenuItem("몰입 모드", null, (_, _) => ToggleImmersion()) { ShortcutKeyDisplayString = "F11" };
        viewMenu.DropDownItems.Add(immersionMenuItem);

        // 자동 재생 — 만화의 슬라이드쇼와 소설의 낭독을 한 메뉴·한 단축키(F5)로 묶었다.
        var ttsMenu = new ToolStripMenuItem("자동 재생");
        autoPlayToggleMenuItem = new ToolStripMenuItem("시작", null, (_, _) => ToggleAutoPlay()) { ShortcutKeyDisplayString = "F5" };
        ttsMenu.DropDownItems.Add(autoPlayToggleMenuItem);
        ttsMenu.DropDownItems.Add(new ToolStripSeparator());
        var rateMenu = ttsRateMenu = new ToolStripMenuItem("낭독 속도");
        foreach (int rate in new[] { -5, -3, -1, 0, 1, 2, 3, 5 })
        {
            var item = new ToolStripMenuItem(rate == 0 ? "보통" : rate > 0 ? $"빠르게 +{rate}" : $"느리게 {rate}", null, (_, _) =>
            {
                settings.TtsRate = rate;
                settings.Save();
                AppShared.NotifySettingsChanged(SettingKind.Tts);
            }) { Tag = rate };
            ttsRateMenuItems.Add(item);
            rateMenu.DropDownItems.Add(item);
        }
        ttsMenu.DropDownItems.Add(rateMenu);
        ttsVoiceMenu = new ToolStripMenuItem("낭독 목소리");
        ttsVoiceMenu.DropDownItems.Add(new ToolStripMenuItem("(불러오는 중)") { Enabled = false });
        ttsVoiceMenu.DropDownOpening += (_, _) => FillVoiceMenu();
        ttsMenu.DropDownItems.Add(ttsVoiceMenu);
        ttsMenu.DropDownItems.Add(slideshowMenu); // "만화 넘김 간격"(MainForm.cs에서 만들어 둔 것)
        menu.Items.Add(ttsMenu);

        var helpMenu = new ToolStripMenuItem("도움말");
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("사용법", null, (_, _) => ShowHelp()));
        menu.Items.Add(helpMenu);
    }

    private void FillRecentMenu()
    {
        if (recentMenu == null) return;
        recentMenu.DropDownItems.Clear();
        var items = library.Recent(12).Where(b => File.Exists(b.Path)).Take(10).ToList();
        if (items.Count == 0)
        {
            recentMenu.DropDownItems.Add(new ToolStripMenuItem("(없음)") { Enabled = false });
            return;
        }
        for (int i = 0; i < items.Count; i++)
        {
            string path = items[i].Path;
            string label = $"{i + 1}. {Path.GetFileName(path)}".Replace("&", "&&");
            recentMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => OpenPath(path)) { ToolTipText = path });
        }
    }

    private void FillVoiceMenu()
    {
        if (ttsVoiceMenu == null) return;
        ttsVoiceMenu.DropDownItems.Clear();
        var voices = TtsEngine.InstalledVoices();
        if (voices.Count == 0)
        {
            ttsVoiceMenu.DropDownItems.Add(new ToolStripMenuItem("(설치된 음성이 없습니다)") { Enabled = false });
            return;
        }
        string current = settings.TtsVoice != null && voices.Contains(settings.TtsVoice) ? settings.TtsVoice : voices[0];
        foreach (var v in voices)
        {
            string name = v;
            ttsVoiceMenu.DropDownItems.Add(new ToolStripMenuItem(name.Replace("&", "&&"), null, (_, _) =>
            {
                settings.TtsVoice = name;
                settings.Save();
                AppShared.NotifySettingsChanged(SettingKind.Tts);
            }) { Checked = name == current });
        }
    }

    /// <summary>모드(txt/epub/pdf/만화)에 따라 자동 재생·낭독·PDF 야간 모드 메뉴를 켜고 끈다. SetContentMode 끝에서 부른다.</summary>
    private void UpdateFeatureMenus(ContentKind kind)
    {
        bool text = kind is ContentKind.Txt or ContentKind.Epub;
        if (autoPlayToggleMenuItem != null) autoPlayToggleMenuItem.Enabled = text || kind == ContentKind.Image;
        if (ttsRateMenu != null) ttsRateMenu.Enabled = text;
        if (ttsVoiceMenu != null) ttsVoiceMenu.Enabled = text;
        UpdateAutoPlayMenu();
        if (pdfDarkMenuItem != null) pdfDarkMenuItem.Enabled = kind == ContentKind.Pdf;
    }

    // ───────────────────────── 시작 시 초기화 ─────────────────────────

    private long lastWheelTurnTick;

    private void InitFeatures()
    {
        ApplyLook();
        mainEditor.HideSelection = false; // 낭독 중 표시한 문장이 본문에 포커스가 없어도 계속 보이게
        mainEditor.BorderStyle = BorderStyle.None; // 테마 색이 바뀌어도 흰 테두리가 남지 않게(창 핸들이 만들어지기 전인 지금 한 번만 바꾼다)
        mainEditor.HandleCreated += (_, _) => ApplySideMargin();
        mainEditor.SizeChanged += (_, _) => { if (EffPaper != null) ApplySideMargin(); };
        mainEditor.MouseWheel += (_, e) =>
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                if (!CanZoomText) return;
                ViewZoomBy(e.Delta > 0 ? 1 : -1);
                if (e is HandledMouseEventArgs h) h.Handled = true; // 본문이 같이 스크롤되지 않게
                return;
            }
        };
        // (옛) 페이지 방식에서는 휠이 본문을 스크롤하지 않고(PageLockTextBox가 막는다), 한 칸에 한 페이지씩 넘긴다.
        mainEditor.WheelTurned += delta =>
        {
            if (!pageMode || delta == 0) return;
            // 터치 패닝이나 정밀 휠은 작은 값이 연달아 오므로, 한 번 넘긴 뒤 잠깐은 무시한다.
            if (Environment.TickCount64 - lastWheelTurnTick < 250) return;
            lastWheelTurnTick = Environment.TickCount64;
            ChangePage(delta < 0 ? 1 : -1);
        };
        zoomTimer.Tick += (_, _) => { zoomTimer.Stop(); ApplyPendingZoom(); };
        FormClosed += (_, _) => zoomTimer.Dispose();
        ApplyEditorWrap(); // 저장해 둔 자동 줄바꿈 설정

        tts.VoiceName = settings.TtsVoice;
        tts.Rate = settings.TtsRate;
        tts.SentenceFinished += OnTtsSentenceFinished;
        SyncTtsMenu();

        Controls.Add(toast);
        AllowDrop = true;
        DragEnter += OnFileDragEnter;
        DragDrop += OnFileDrop;
        mainEditor.ContextMenuStrip = BuildEditorContextMenu();

        positionTimer.Tick += (_, _) => { CapturePosition(); library.SaveIfDirty(); };
        positionTimer.Start();
        immersionHoverTimer.Tick += (_, _) => ImmersionHoverTick(); // 몰입 모드에 들어갈 때만 돌린다(ToggleImmersion)
        FormClosing += (_, _) =>
        {
            StopTts();
            CapturePosition();
            library.SaveIfDirty();
            settings.Save();
        };
        // 설정창·메뉴에서 바뀐 설정은 열려 있는 모든 읽기 창이 각자 반영한다(AppShared가 알려준다).
        AppShared.SettingsChanged += OnSettingsChanged;
        FormClosed += (_, _) =>
        {
            AppShared.SettingsChanged -= OnSettingsChanged;
            tts.Dispose();
            positionTimer.Dispose();
            immersionHoverTimer.Dispose();
        };

        ApplyMargins();
        ApplyTheme();
    }

    // ───────────────────────── 테마 · 여백 · 설정 반영 ─────────────────────────

    private void ApplyTheme()
    {
        theme = Themes.Find(settings.Theme);
        ToolStripManager.Renderer = Themes.CreateRenderer(theme);
        Themes.SetDarkTitleBar(this, theme.Dark);

        BackColor = theme.Chrome;
        ForeColor = theme.ChromeFore;
        statusLabel.BackColor = theme.Chrome;
        statusLabel.ForeColor = theme.ChromeFore;
        statusSegments?.ApplyColors(theme.Chrome, theme.ChromeFore);
        pageNavPanel.BackColor = theme.Chrome;
        pageIndicatorLabel.BackColor = theme.Chrome;
        pageIndicatorLabel.ForeColor = theme.ChromeFore;
        if (MainMenuStrip != null)
        {
            MainMenuStrip.BackColor = theme.Chrome;
            MainMenuStrip.ForeColor = theme.ChromeFore;
        }
        readerTitle?.SetColors(theme.Chrome, theme.ChromeFore);

        ApplyLookBarTheme();
        foreach (var host in new Control[] { contentHost, editorHost, epubHost, pdfHost }) host.BackColor = theme.Back;
        mainEditor.BackColor = theme.Back;
        mainEditor.ForeColor = theme.Fore;
        writerTitle?.SetColors(theme.Back, theme.Fore);
        SyncGoalWidget(); SyncFloatingWidgets();
        if (epubView != null) epubView.DefaultBackgroundColor = theme.Back;
        if (epubMode) RunEpubScript(BuildEpubThemeScript());

        foreach (var item in themeMenuItems) item.Checked = item.Text == theme.Name;

        navBar.ApplyTheme(theme);
        findBar.ApplyTheme(theme);
        // 서재·설정 창의 색은 AppShared.NotifySettingsChanged가 바꾼다.
    }

    private void ApplyMargins()
    {
        // 상하 여백은 본문을 감싼 자리의 안쪽 여백으로, 좌우 여백은 텍스트박스 내부 여백으로 준다.
        // (좌우를 바깥 패딩으로 주면 세로 스크롤바가 창 끝이 아니라 본문 안쪽에 떠서 어색하다.)
        var paper = EffPaper;
        editorHost.Padding = paper == null
            ? new Padding(0, settings.VerticalMargin, 0, settings.VerticalMargin)
            : new Padding(0, MmToPx(paper.Top), 0, MmToPx(paper.Bottom));
        ApplySideMargin();
    }

    private void ApplySideMargin()
    {
        if (!mainEditor.IsHandleCreated) return;
        int m = Math.Clamp(settings.SideMargin, 0, 300);
        if (EffPaper is { } paper)
        {
            // 용지 칸 폭만큼만 글이 놓이게, 남는 폭을 좌우 여백으로 나눈다(창이 좁으면 0).
            int usable = MmToPx(paper.ContentWidth) * ViewZoom / 100;
            m = Math.Clamp((mainEditor.ClientSize.Width - usable) / 2, 0, 2000);
        }
        int mr = -1;
        if (EffPaper == null && WriterLayout) (m, mr) = WriterMargins(m); // 회차 글쓰기: 글을 창 가운데 일정한 폭의 칸에
        mainEditor.SetSideMargins(m, mr);
        SyncWriterTitle(m, mr);
    }

    /// <summary>설정창의 챕터 나누기 미리보기용: 지금 txt를 보고 있으면 그 전체 글과 이름을 돌려준다.</summary>
    internal bool TryGetTxtForPreview(out string text, out string name)
    {
        bool ok = shownKind == ContentKind.Txt && fullText.Length > 0;
        text = ok ? fullText : "";
        name = Path.GetFileNameWithoutExtension(currentPath ?? "");
        return ok;
    }

    /// <summary>설정창(또는 메뉴)에서 값이 바뀌었을 때 화면·엔진에 즉시 반영한다.</summary>
    private void OnSettingsChanged(SettingKind kind)
    {
        switch (kind)
        {
            case SettingKind.Theme:
                ApplyTheme();
                break;
            case SettingKind.Toolbar:
                ApplyToolbarVisibility();
                ApplyLookBarShown();
                break;
            case SettingKind.NovelReading:
                // 보고 있는 epub · 웹 화면 글에도 바로(바로 쓰는 txt · 수정 중에는 건드리지 않는다).
                if (epubMode && !(epubEditable && !txtWeb))
                    SetReadingMode(settings.NovelReadingMode switch { NovelReadingMode.Page => ReadingMode.Page, NovelReadingMode.PageTouch => ReadingMode.PageTouch, _ => ReadingMode.Scroll });
                break;
            case SettingKind.Font:
                if (mainEditor.Font.Name != EffFont || Math.Abs(mainEditor.Font.SizeInPoints - fontSize) > 0.01f) mainEditor.Font = new Font(EffFont, fontSize);
                if (epubMode) ApplyEpubFontSize(keepPlace: true); // 글자 크기와 같이, 다시 불러오지 않고 보던 자리에서
                else ReflowAfterLookChange();
                break;
            case SettingKind.FontSize:
                ApplyLook();
                break;
            case SettingKind.Margin:
                marginBoxesFilled = false;
                ApplyMargins();
                ReflowAfterLookChange();
                break;
            case SettingKind.Tts:
                tts.Rate = settings.TtsRate;
                tts.VoiceName = settings.TtsVoice;
                SyncTtsMenu();
                break;
            case SettingKind.Pdf:
                if (pdfHost.Visible && !string.IsNullOrEmpty(currentPath)) NavigatePdf(currentPath);
                break;
            case SettingKind.ImageLayout:
                ApplyImageLayout();
                break;
            case SettingKind.ImageMargin:
                marginBoxesFilled = false;
                ApplyImageMargins();
                break;
            case SettingKind.Anim:
                imageCanvas.RestartAnimation();
                break;
            case SettingKind.ImageReading:
                if (imageMode) SetReadingMode(settings.ImagePageTouch ? ReadingMode.PageTouch : ReadingMode.Page); // 보고 있는 만화에도 바로
                break;
            case SettingKind.TxtBreak:
                // 문장 단위 줄바꿈을 켜거나 껐다: 열려 있는 txt 웹 화면을 그 자리에서 다시 만든다.
                if (shownKind == ContentKind.Txt && txtWeb && !string.IsNullOrEmpty(currentPath)) ShowTxtWeb(txtWebTopOffset);
                break;
            case SettingKind.TxtSplit:
                // 열려 있는 txt는 챕터 목록만 다시 나눈다(본문과 읽던 위치는 그대로). 수정 중이거나 zip에서 꺼낸 글은 다시 열 때 적용된다.
                if (shownKind == ContentKind.Txt && !txtFromZip && mainEditor.ReadOnly && fullText.Length > 0)
                {
                    chapters = TxtChapterSplitter.Split(fullText, Path.GetFileNameWithoutExtension(currentPath ?? ""), TxtSplitOptions.From(settings));
                    statusLabel.Text = $"{chapters.Count}개 챕터 · 총 {fullText.Length:N0}자";
                    if (txtWeb) ShowTxtWeb(txtWebTopOffset); // 웹 화면의 문서 나눔도 새 챕터대로 다시 만든다
                }
                break;
        }
    }

    private void SyncTtsMenu()
    {
        foreach (var item in ttsRateMenuItems) item.Checked = item.Tag is int r && r == settings.TtsRate;
    }

    /// <summary>글꼴·여백이 바뀌어 한 화면에 들어가는 글이 달라졌으니, 페이지 모드면 페이지를 다시 잰다.</summary>
    private void ReflowAfterLookChange()
    {
        if (pageMode)
        {
            currentPageEnd = FindPageEnd(currentPageStart);
            ShowCurrentPage();
            mainEditor.SelectionStart = 0;
        }
        if (epubMode) ApplyEpubLook();
    }

    private void ApplyEpubLook()
    {
        if (epubPageMode)
        {
            ReloadEpubKeepingPlace(); // 다단 배치를 새 글꼴·여백 기준으로 다시 잰다
        }
        else
        {
            ApplyEpubFontSize();
            RunEpubScript(BuildEpubResetColumnsScript());
        }
    }

    private async void RunEpubScript(string script)
    {
        if (epubView?.CoreWebView2 == null) return;
        try { await epubView.CoreWebView2.ExecuteScriptAsync(script); } catch { /* 문서가 아직 준비 안 됐으면 무시 */ }
    }


    // ───────────────────────── 서재 · 끌어놓기 · 설정/도움말 창 ─────────────────────────

    // 서재는 이제 프로그램의 시작 화면이라 읽기 창이 소유하지 않는다 — 창을 열고 닫는 건 AppShared가 맡는다.
    // 볼트 창 안의 탭이면 홈 화면으로 돌아가 달라고 알린다(작은 PIP 창에는 받는 쪽이 없다).
    private void ShowLibrary()
    {
        HomeRequested?.Invoke();
    }

    private void ShowSettings() => AppShared.ShowSettings(this);

    private void ShowHelp()
    {
        using var help = new HelpForm();
        help.ApplyTheme(theme);
        help.TopMost = HostForm.TopMost;
        help.ShowDialogOver(this);
    }

    private void OnFileDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>파일을 끌어다 놓으면 그 책을 열고, 폴더를 놓으면 그 폴더를 볼트로 연다.</summary>
    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        var dirs = paths.Where(Directory.Exists).ToList();
        if (dirs.Count > 0)
        {
            AppShared.OpenDroppedFolder(dirs);
            return;
        }

        string? file = paths.FirstOrDefault(File.Exists);
        if (file != null) OpenPath(file);
    }

    // ───────────────────────── 우클릭 메뉴 · 웹 검색 ─────────────────────────

    private ContextMenuStrip BuildEditorContextMenu()
    {
        var readHere = new ToolStripMenuItem("여기서부터 낭독", null, (_, _) =>
        {
            StartTts((pageMode ? currentPageStart : 0) + CharIndexAtPoint(editorContextPoint));
        });
        var copy = new ToolStripMenuItem("복사", null, (_, _) => mainEditor.Copy());
        var selectAll = new ToolStripMenuItem("모두 선택", null, (_, _) => mainEditor.SelectAll());
        var naver = new ToolStripMenuItem("선택한 글 네이버로 검색", null, (_, _) => WebSearch(mainEditor.SelectedText, naver: true));
        var google = new ToolStripMenuItem("선택한 글 구글로 검색", null, (_, _) => WebSearch(mainEditor.SelectedText, naver: false));
        var undo = new ToolStripMenuItem("실행 취소", null, (_, _) => mainEditor.Undo());
        var cut = new ToolStripMenuItem("잘라내기", null, (_, _) => mainEditor.Cut());
        var paste = new ToolStripMenuItem("붙여넣기", null, (_, _) => mainEditor.Paste());
        var delete = new ToolStripMenuItem("삭제", null, (_, _) => { if (mainEditor.SelectionLength > 0) mainEditor.SelectedText = ""; });
        var editSeparator = new ToolStripSeparator();

        var menu = new ContextMenuStrip();
        MenuGuard.Attach(menu);
        menu.Items.AddRange(new ToolStripItem[]
        {
            readHere, new ToolStripSeparator(), copy, selectAll, new ToolStripSeparator(), naver, google,
            editSeparator, undo, cut, paste, delete,
        });
        menu.Opening += (_, _) =>
        {
            editorContextPoint = mainEditor.PointToClient(Cursor.Position);
            bool editing = !mainEditor.ReadOnly;
            bool hasSelection = mainEditor.SelectionLength > 0;
            readHere.Enabled = !editing;
            copy.Enabled = hasSelection;
            naver.Enabled = google.Enabled = hasSelection;
            // 잘라내기·붙여넣기 같은 건 수정 모드일 때만 보여준다.
            editSeparator.Visible = undo.Visible = cut.Visible = paste.Visible = delete.Visible = editing;
            cut.Enabled = delete.Enabled = hasSelection;
        };
        return menu;
    }

    private static void WebSearch(string text, bool naver)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 200) text = text[..200];
        string url = (naver ? "https://search.naver.com/search.naver?query=" : "https://www.google.com/search?q=") + Uri.EscapeDataString(text);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OnEpubContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (epubView?.CoreWebView2 == null) return;
        var env = epubView.CoreWebView2.Environment;
        string selected = e.ContextMenuTarget.HasSelection ? e.ContextMenuTarget.SelectionText : "";

        var read = env.CreateContextMenuItem("여기서부터 낭독", null, CoreWebView2ContextMenuItemKind.Command);
        read.IsEnabled = !epubEditable;
        read.CustomItemSelected += async (_, _) =>
        {
            int offset = -1;
            try
            {
                string json = await epubView.CoreWebView2.ExecuteScriptAsync("window.__clrTts ? window.__clrTts.lastCtx : -1");
                int.TryParse(json, out offset);
            }
            catch { }
            StartTts(offset >= 0 ? offset : null);
        };

        int at = 0;
        e.MenuItems.Insert(at++, read);
        if (selected.Length > 0)
        {
            var naver = env.CreateContextMenuItem("선택한 글 네이버로 검색", null, CoreWebView2ContextMenuItemKind.Command);
            naver.CustomItemSelected += (_, _) => WebSearch(selected, naver: true);
            var google = env.CreateContextMenuItem("선택한 글 구글로 검색", null, CoreWebView2ContextMenuItemKind.Command);
            google.CustomItemSelected += (_, _) => WebSearch(selected, naver: false);
            e.MenuItems.Insert(at++, naver);
            e.MenuItems.Insert(at++, google);
        }
        e.MenuItems.Insert(at, env.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
    }

    // ───────────────────────── 토스트 ─────────────────────────

    private void ShowToast(string text, int milliseconds = 2200)
    {
        toast.ShowMessage(text, milliseconds);
        toast.Left = Math.Max(0, (ClientSize.Width - toast.Width) / 2);
        toast.Top = Math.Max(0, contentHost.Bottom - toast.Height - 24);
    }


    // ───────────────────────── Win32 ─────────────────────────

    private const int EM_SETMARGINS = 0x00D3, EM_LINEFROMCHAR = 0x00C9, EM_GETFIRSTVISIBLELINE = 0x00CE,
        EM_LINESCROLL = 0x00B6, EM_LINEINDEX = 0x00BB;
    private const int EC_LEFTMARGIN = 1, EC_RIGHTMARGIN = 2;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    private const uint SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
