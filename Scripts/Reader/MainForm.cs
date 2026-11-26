using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// 소설 · 만화 뷰어 겸 간단한 에디터.
/// txt는 읽을 때 epub과 같은 웹 화면(TxtWebBook)으로 보여주고, "수정하기"를 켰을 때만 파일 전체를 담는
/// 편집 가능한 텍스트박스(mainEditor)로 바꾼다. 맨 위는 메뉴바 대신 아이콘 툴바다(MainForm.Toolbar.cs).
/// 챕터 · 이미지 고르기와 위치 이동은 아래쪽 탐색 막대(보기 > 탐색, MainForm.Nav.cs)가 맡는다.
/// </summary>
public partial class MainForm : Form
{
    private readonly PageLockTextBox mainEditor;
    private readonly Panel editorHost; // mainEditor를 감싸는 자리(Dock=Fill). 화살표는 폭을 줄이지 않고 이 위에 그냥 겹쳐 그린다.
    private readonly Panel epubHost; // epubView를 감싸는 자리(Dock=Fill). 마찬가지로 화살표는 그 위에 겹쳐 그린다.
    private readonly Panel imageHost; // imageCanvas(만화/이미지 뷰어)를 담는다.
    private readonly Panel pdfHost; // pdfView를 감싸는 자리(Dock=Fill).
    private readonly ImageCanvas imageCanvas;
    private readonly Panel contentHost; // editorHost(txt)·epubHost(epub)·imageHost(만화)·pdfHost(pdf) 중 하나만 보여준다.
    private readonly Label statusLabel;

    private readonly ToolStripMenuItem showImagesMenuItem;
    // 화면 반쪽 탭 넘기기 여부는 "읽기 방식" 메뉴 안에 "페이지 터치"로 들어가 있다(스크롤/페이지/페이지
    // 터치 세 개 중 하나) — 만화(이미지)는 스크롤 개념만 없을 뿐, 페이지/페이지 터치는 그대로 고른다.
    private bool tapToTurnEnabled = true;
    private ContentKind? shownKind; // 지금 보여주는 종류(아직 아무것도 안 열었으면 null) — 종류가 바뀔 때만 기본 읽기 방식을 적용하려고(SetContentMode)
    private readonly List<ToolStripMenuItem> fontSizeMenuItems = new();
    private readonly List<ToolStripItem> editOnlyMenuItems = new();
    private readonly List<ToolStripItem> txtOnlyMenuItems = new(); // epub을 보는 중엔 잠가둘 mainEditor 전용 메뉴들
    private readonly ToolStripMenuItem editModeMenuItem;
    private readonly ToolStripMenuItem saveMenuItem;
    private readonly ToolStripMenuItem saveAsMenuItem;
    private readonly ToolStripMenuItem findMenuItem;
    private readonly ToolStripMenuItem replaceMenuItem;
    private readonly ToolStripMenuItem readingModeMenu;
    private readonly ToolStripMenuItem fontSizeMenu;
    private readonly ToolStripMenuItem orderMenu;
    private readonly ToolStripMenuItem layoutMenu;
    private readonly ToolStripMenuItem viewModeMenu;
    private readonly ToolStripMenuItem slideshowMenu;
    private readonly ToolStripMenuItem endOfBookMenu;
    private readonly ToolStripMenuItem editMenu;

    private List<TxtChapter> chapters = new();
    private string? currentPath;
    private float fontSize = 11f;

    private readonly FindBar findBar;
    private readonly LoadingOverlay loadingOverlay; // 큰 파일을 불러오는 동안 게이지 + 남은 시간 추정을 보여준다
    private CancellationTokenSource? loadCts; // 지금 진행 중인 불러오기 하나당 하나 — 게이지의 "취소"가 이걸 취소시킨다

    // ── epub ──
    private WebView2? epubView;
    private EpubBook? epubBook;
    private bool epubMode = false;
    private int epubChapterIndex = 0;
    private bool lastKindDirect; // 직전 화면이 바로 쓰는 txt였는지(다음 epub이 기본 읽기 방식으로 시작하게)
    private int epubPageIdx, epubPageTotal; // 페이지 방식에서 지금 챕터의 현재/전체 페이지(네비게이션 바 숫자용)
    private bool epubPageMode = false; // epub 전용 읽기 방식(스크롤/페이지) — txt의 pageMode와는 별개
    private bool epubPendingStartAtLastPage = false; // "이전 챕터"로 넘어갈 때, 새 챕터의 마지막 페이지부터 시작
    private bool epubEditable = false; // epub 수정하기 on/off (document.body.contentEditable)
    private string? epubExtractDir; // epub을 통째로 풀어둔 임시 폴더 — 저장할 때 이걸 다시 zip으로 묶는다
    private string? txtTempDir; // txt를 웹 화면으로 보려고 만든 문서들(소설 본문 복사본)이 든 임시 폴더 — 창을 닫을 때 지운다

    // ── pdf ──
    // WebView2(크로미움 엔진)에 로컬 파일을 그대로 navigate시키면 엣지와 똑같은 내장 PDF 뷰어(페이지 넘기기·
    // 확대/축소·검색·인쇄까지 자체 툴바로 다 제공)가 뜬다. epub처럼 우리가 직접 다단/페이지 로직을 만들
    // 필요가 없다 — PDF는 애초에 고정 레이아웃이라 그런 재배치가 안 맞기도 하고.
    private WebView2? pdfView;

    // ── 만화/이미지(zip 또는 이미지 파일) ──
    private bool imageMode = false;
    private string imageTitleBase = ""; // 만화 모드 제목표시줄에 쓰는 책/파일 이름("[3/120] 이름 — {AppShared.AppDisplayName}"의 "이름")
    private IImageSet? imageSet;
    private int imageIndex = 0; // imagePages 안에서의 위치(실제 파일 순서가 아니라 화면에 넘겨 보여줄 페이지 순서)

    /// <summary>옵시디언 플러그인의 "순서 / 나누기" 설정과 동일.</summary>
    private enum ReadingOrder { Ltr, Rtl, LtrSpread, RtlSpread }
    // Pair = 두쪽 보기: ItemIndex번 그림과 그 다음 그림을 나란히 이어 붙인 한 화면
    private enum ImageHalf { None, Left, Right, Pair }
    private bool imageDouble; // 두쪽 보기 여부(설정 ImageDoublePage와 같은 값)
    private readonly List<ToolStripMenuItem> imageLayoutMenuItems = new();
    private ReadingOrder imageOrder = ReadingOrder.Ltr;
    // 쪽 목록은 고치지 않고 통째로 새 목록으로 바꿔 끼운다(RebuildImagePages). 미리 불러오기 스레드가 읽는 도중 목록이 지워지고 다시 채워지는 일이 없게.
    private List<(int ItemIndex, ImageHalf Half)> imagePages = new();
    private readonly List<ToolStripMenuItem> imageOrderMenuItems = new();
    private readonly List<ToolStripMenuItem> imageViewModeMenuItems = new();
    private readonly PageScrubberBar pageScrubberBar; // 만화 하단의 위치 막대(꿀뷰처럼 드래그로 페이지 이동)

    // ── 슬라이드쇼(만화 자동 넘김) ──
    private System.Windows.Forms.Timer? slideshowTimer;
    private int slideshowIntervalSeconds = 3;
    private readonly List<ToolStripMenuItem> slideshowIntervalMenuItems = new();

    // ── 마지막 페이지에서(화살표로 더 넘기려 하거나 슬라이드쇼가 끝에 닿았을 때) ──
    private enum EndOfBookAction { Stop, Ask, NextFile }
    private EndOfBookAction endOfBookAction = EndOfBookAction.Ask;
    private readonly List<ToolStripMenuItem> endOfBookMenuItems = new();

    // ── 이미지 미리 불러오기(프리페치) 캐시 ──
    // 화살표로 넘기기 전에 앞뒤 몇 장을 백그라운드 스레드에서 미리 디코딩해둬서, 실제로 넘기는 순간엔
    // 압축 풀기·디코딩을 기다리지 않고 바로 뜨게 한다. imageSet.Load()는 zip 안 공유 스트림을 건드리므로
    // 여러 스레드가 동시에 부르면 깨질 수 있어, 실제 로딩은 항상 imageLoadLock으로 한 번에 하나씩만 실행한다.
    private readonly object imageLoadLock = new();
    private readonly object imageCacheLock = new(); // imagePageCache/pendingPrefetch 자체를 보호(짧게만 잡는다)
    private readonly Dictionary<int, Image> imagePageCache = new();
    private readonly HashSet<int> pendingPrefetch = new(); // 지금 백그라운드에서 디코딩 중인 페이지 번호 — 같은 페이지를 중복으로 예약하지 않으려고 둔다
    private const int PrefetchAhead = 3; // 다음 방향으로 미리 불러올 장 수
    private const int PrefetchBehind = 1; // 이전 방향으로 미리 불러올 장 수
    private const int CacheKeepRadius = 5; // 지금 페이지에서 이 범위를 벗어난 캐시는 정리한다(메모리 절약)

    // ── 읽기 방식(스크롤/페이지) ──
    private readonly ToolStripMenuItem scrollModeMenuItem;
    private readonly ToolStripMenuItem pageModeMenuItem;
    private readonly ToolStripMenuItem pageTouchModeMenuItem;
    private readonly Panel pageNavPanel;
    private readonly Label pageIndicatorLabel;
    // txt/epub/만화 공용 — 본문 위에 그대로 얹은 동그란 넘김 버튼(사각 배경 없이 본문/그림 위에 뜬다).
    // 자리만 차지하고 폭을 줄이지 않도록, editorHost/epubHost에 여백을 주지 않고 그냥 맨 위에 겹쳐 그린다.
    // 평소엔 숨겨두고, 마우스가 버튼 근처에 오면 그쪽 버튼만 나타난다(ArrowHoverTick).
    private readonly ArrowOverlayButton prevNavBtn;
    private readonly ArrowOverlayButton nextNavBtn;
    private readonly System.Windows.Forms.Timer arrowHoverTimer = new() { Interval = 80 };
    private DateTime prevNavLastNear, nextNavLastNear;
    // 화면 반쪽 탭으로 페이지 넘기기(txt page 모드) 판정용 — MouseDown 때 좌표를 담아뒀다가 MouseUp에서
    // 얼마나 끌렸는지 비교한다. 6px는 ImageCanvas의 TapMoveThreshold와 맞춘 값.
    private Point mainEditorTapDownLocation;
    private const int TapMoveThresholdSq = 6 * 6;
    private readonly System.Windows.Forms.Timer mainEditorTapTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private int pendingMainEditorTapDelta;
    private string fullText = ""; // 페이지 모드에서는 mainEditor.Text가 현재 페이지만 담으므로, 전체는 따로 들고 있는다.
    private bool pageMode = false;
    private int currentPageStart = 0;
    private int currentPageEnd = 0;

    // ── txt 웹 화면 ──
    // 읽기 전용 txt는 메모장 같은 mainEditor 대신 epubView 위에 "가상 epub"(TxtWebBook)으로 띄운다 — 줄 간격·
    // 문단 간격·다단 페이지·테마·낭독을 epub과 같은 길로 쓴다. mainEditor는 "수정하기"를 켰을 때만 쓴다.
    // 이때 epubMode도 같이 켜져 있으므로, txt와 epub을 갈라야 하는 곳은 txtWeb을 먼저 본다.
    private bool txtWeb;
    private TxtWebBook? txtBook;
    private bool epubDocReady; // 지금 epubView에 뜬 문서에 스크립트까지 다 심겼는지(같은 문서 안 이동은 다시 불러오지 않으려고)
    private int? txtPendingOffset; // 문서가 다 뜬 직후 이 원본 위치로 옮긴다
    private (int Start, int Length)? txtPendingSelect; // 문서가 다 뜬 직후 이 구간을 찾은 글로 표시한다
    private int txtWebTopOffset; // 웹 화면 맨 위 줄의 원본 위치(QueryTxtTop이 주기적으로 읽어 둔다)
    private int txtFindStart, txtFindEnd; // 웹 화면에서 마지막으로 찾은 구간(찾기 바의 "다음 · 이전" 기준)

    /// <summary>
    /// Ctrl+L · 맨 앞 홈 버튼 — 작품 화면(볼트 창 안의 탭)에서 홈 화면으로 돌아가 달라는 요청. 작은 PIP 창에서는 받는 쪽이 없다.
    /// (이 읽기 화면은 늘 다른 창 안에 들어가 쓴다: 볼트 창의 탭, 또는 PIP 창. 예전 독립 창 모드는 없어졌다.)
    /// </summary>
    public event Action? HomeRequested;

    public MainForm(string? initialPath)
    {
        AutoScaleMode = AutoScaleMode.None; // DPI 배율에 따른 좌표 재계산으로 컨트롤이 겹치는 걸 방지
        Icon = AppShared.LoadAppIcon(); // exe에 심어둔 아이콘(csproj의 ApplicationIcon)을 그대로 창/작업표시줄에도 사용
        Text = AppShared.AppDisplayName;
        Width = 900;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;

        mainEditor = new PageLockTextBox
        {
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("맑은 고딕", fontSize),
            MaxLength = int.MaxValue,
            AcceptsTab = true,
            ReadOnly = true, // 기본값: 읽기 전용. "편집" 버튼을 눌러야 고칠 수 있다.
        };
        editorHost = new Panel { Dock = DockStyle.Fill };
        editorHost.Controls.Add(mainEditor);
        epubHost = new Panel { Dock = DockStyle.Fill, Visible = false }; // epubView는 나중에(비동기 초기화 후) 여기 넣는다
        imageCanvas = new ImageCanvas { Dock = DockStyle.Fill };
        ApplyImageMargins();
        imageDouble = settings.ImageDoublePage;
        imageCanvas.PageRequested += delta => ChangeImage(delta);
        imageCanvas.ZoomPercentChanged += pct => { if (imageMode) ShowZoomBubble($"{pct}%"); }; // 원본 대비 몇 %인지(100% = 원본 크기)
        imageHost = new Panel { Dock = DockStyle.Fill, Visible = false };
        imageHost.Controls.Add(imageCanvas);
        pdfHost = new Panel { Dock = DockStyle.Fill, Visible = false }; // pdfView는 나중에(비동기 초기화 후) 여기 넣는다
        contentHost = new Panel { Dock = DockStyle.Fill };
        contentHost.Controls.Add(editorHost);
        contentHost.Controls.Add(epubHost);
        contentHost.Controls.Add(imageHost);
        contentHost.Controls.Add(pdfHost);

        statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        // 페이지 모드일 때만 보이는 위치 표시줄(하단, 가운데 "약 N% 지점"). 만화(이미지) 모드에서는
        // 이 자리에 pageIndicatorLabel 대신 pageScrubberBar(꿀뷰 스타일 위치 막대)를 보여준다.
        pageIndicatorLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
        pageScrubberBar = new PageScrubberBar { Dock = DockStyle.Fill, Visible = false };
        pageScrubberBar.PageSeek += pageIndex => ShowImagePage(pageIndex);
        pageNavPanel = new Panel { Dock = DockStyle.Bottom, Height = 36, Visible = false };
        pageNavPanel.Controls.Add(pageIndicatorLabel);
        pageNavPanel.Controls.Add(pageScrubberBar);

        // 옵시디언 플러그인처럼, 페이지 넘김은 본문 좌우 가장자리에 뜨는 화살표로 — txt/epub/만화 모두 공용.
        prevNavBtn = new ArrowOverlayButton(pointsLeft: true) { Visible = false };
        nextNavBtn = new ArrowOverlayButton(pointsLeft: false) { Visible = false };
        prevNavBtn.Activated += () => { if (imageMode) ChangeImage(-1); else if (epubMode) EpubChangePage(-1); else ChangePage(-1); };
        nextNavBtn.Activated += () => { if (imageMode) ChangeImage(1); else if (epubMode) EpubChangePage(1); else ChangePage(1); };
        arrowHoverTimer.Tick += (_, _) => ArrowHoverTick();

        // txt 페이지 모드(읽기 전용일 때만): 화면을 셋으로 나눠서 왼쪽을 탭하면 뒤로, 오른쪽을
        // 탭하면 앞으로, 가운데는 무시(TapZone) — 화살표를 정확히 안 눌러도 되는, 터치에 훨씬 편한 방식. 편집 중이거나
        // (커서를 옮기려는 클릭일 수 있으니) 스크롤 모드일 때는(페이지 개념이 없으니) 끄고 원래
        // 클릭 동작 그대로 둔다. 살짝이라도 끌었으면(텍스트를 선택하려던 것) 탭이 아니므로 무시한다.
        // 더블클릭(단어 선택)의 첫 번째 클릭까지 곧장 넘겨버리면 안 되니, 시스템 더블클릭 시간만큼
        // 기다렸다가 그사이 두 번째 클릭이 안 왔을 때만(=진짜 한 번 탭한 것으로 판명 났을 때) 넘긴다.
        mainEditorTapTimer.Tick += (_, _) => { mainEditorTapTimer.Stop(); ChangePage(pendingMainEditorTapDelta); };
        mainEditor.MouseDown += (_, e) => mainEditorTapDownLocation = e.Location;
        mainEditor.MouseUp += (_, e) =>
        {
            if (!tapToTurnEnabled || !pageMode || !mainEditor.ReadOnly) return;
            if (e.Clicks > 1) { mainEditorTapTimer.Stop(); return; } // 더블클릭의 두 번째 클릭 — 단어 선택은 원래대로 둔다
            int dx = e.X - mainEditorTapDownLocation.X, dy = e.Y - mainEditorTapDownLocation.Y;
            if (dx * dx + dy * dy > TapMoveThresholdSq) return;
            pendingMainEditorTapDelta = TapZone.Direction(e.X, mainEditor.Width);
            if (pendingMainEditorTapDelta == 0) return; // 가운데 칸은 넘기지 않는다
            mainEditorTapTimer.Stop();
            mainEditorTapTimer.Start();
        };
        // 만화/이미지는 ImageCanvas가 자기 안에서 같은 방식(끌지 않고 반쪽 탭)으로 직접 처리해서
        // PageRequested로 알려준다(위 134번째 줄에서 ChangeImage로 연결). epub은 WebView2 안에서
        // JS로 처리한다(EpubPaginateScript).

        // 페이지 모드에서는 방향키로도 앞뒤 페이지를 넘긴다(옵시디언 플러그인과 동일).
        mainEditor.KeyDown += (_, e) =>
        {
            if (!pageMode) return;
            if (e.KeyCode == Keys.Left) { ChangePage(-1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Right) { ChangePage(1); e.Handled = true; e.SuppressKeyPress = true; }
            // 페이지 방식에는 스크롤이 없다 — PgUp/PgDn/Space는 페이지 넘김으로, 그 밖의 스크롤 키는 막는다.
            else if (e.KeyCode == Keys.PageDown || (e.KeyCode == Keys.Space && !e.Shift)) { ChangePage(1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.PageUp || (e.KeyCode == Keys.Space && e.Shift)) { ChangePage(-1); e.Handled = true; e.SuppressKeyPress = true; }
            else if (!e.Control && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)) { e.Handled = true; e.SuppressKeyPress = true; }
        };

        // 윈도우 11 메모장처럼, 본문 위에 떠 있는 찾기 바(알약 모양). 본문 폭은 안 건드린다.
        // 페이지 모드에서는 mainEditor.Text가 지금 페이지 조각만 담고 있으므로, 찾기는 항상
        // fullText(전체 글) 기준으로 검색하고 찾은 위치가 다른 페이지에 있으면 그 페이지로 이동한다.
        // txt 웹 화면에서는 찾기도 전체 글(fullText) 기준으로 하고, 처음 찾을 땐 지금 보이는 자리부터 찾는다.
        findBar = new FindBar(mainEditor,
            getText: () => pageMode || txtWeb ? fullText : mainEditor.Text,
            getSelectionStart: () => txtWeb ? (txtFindEnd > 0 ? txtFindStart : txtWebTopOffset)
                : pageMode ? currentPageStart + mainEditor.SelectionStart : mainEditor.SelectionStart,
            getSelectionEnd: () => txtWeb ? (txtFindEnd > 0 ? txtFindEnd : txtWebTopOffset)
                : pageMode ? currentPageStart + mainEditor.SelectionStart + mainEditor.SelectionLength
                : mainEditor.SelectionStart + mainEditor.SelectionLength,
            selectRange: SelectInDocument,
            focusBody: () => { if (txtWeb) epubView?.Focus(); else mainEditor.Focus(); }
        ) { Visible = false };

        loadingOverlay = new LoadingOverlay { Visible = false };
        // 취소 버튼이 눌렸을 때 실제로 멈추는 일 — 이미 끝났거나 막 끝나려는 참이면 cts가 먼저
        // Dispose됐을 수 있어서, 그 경우(ObjectDisposedException)는 그냥 무시한다.
        loadingOverlay.CancelRequested += () => { try { loadCts?.Cancel(); } catch (ObjectDisposedException) { } };

        // ── 메뉴: 파일 / 편집 / 보기 ──
        var menu = new MenuStrip();

        var fileMenu = new ToolStripMenuItem("파일");
        // 단축키 있는 메뉴는 ScopedMenuItem — 볼트 창의 탭마다 메뉴가 따로 있어서, 지금 보이는 탭의 메뉴만 단축키를 받게 한다.
        fileMenu.DropDownItems.Add(new ScopedMenuItem("열기...", (_, _) => OpenFileDialog(), () => Visible) { ShortcutKeys = Keys.Control | Keys.O });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        saveMenuItem = new ScopedMenuItem("저장", (_, _) => SaveFile(false), () => Visible) { ShortcutKeys = Keys.Control | Keys.S };
        saveAsMenuItem = new ToolStripMenuItem("다른 이름으로 저장...", null, (_, _) => SaveFile(true));
        fileMenu.DropDownItems.Add(saveMenuItem);
        fileMenu.DropDownItems.Add(saveAsMenuItem);

        editMenu = new ToolStripMenuItem("편집");
        // txt든 epub이든 "수정하기"는 같은 메뉴를 쓰고, 지금 어느 쪽을 보고 있는지에 따라 SetEditMode가 알아서 나눠 보낸다.
        editModeMenuItem = new ToolStripMenuItem("수정하기", null, (_, _) => SetEditMode(epubMode && !txtWeb ? !epubEditable : mainEditor.ReadOnly));
        // 실행 취소 · 잘라내기 · 복사 · 붙여넣기 · 모두 선택은 메뉴에 단축키를 걸지 않고(표시만) 포커스 있는 글 상자가 직접 처리하게 둔다.
        // 메뉴에 걸면 탭이 여럿일 때 다른(숨은) 탭의 본문으로 가버리고, 찾기 칸 · 검색 칸에서 누른 Ctrl+V도 본문에 붙었다.
        var undoItem = new ToolStripMenuItem("실행 취소", null, (_, _) => mainEditor.Undo()) { ShortcutKeyDisplayString = "Ctrl+Z" };
        var cutItem = new ToolStripMenuItem("잘라내기", null, (_, _) => mainEditor.Cut()) { ShortcutKeyDisplayString = "Ctrl+X" };
        var copyItem = new ToolStripMenuItem("복사", null, (_, _) => mainEditor.Copy()) { ShortcutKeyDisplayString = "Ctrl+C" };
        var pasteItem = new ToolStripMenuItem("붙여넣기", null, (_, _) => mainEditor.Paste()) { ShortcutKeyDisplayString = "Ctrl+V" };
        var deleteItem = new ToolStripMenuItem("삭제", null, (_, _) => { if (mainEditor.SelectionLength > 0) mainEditor.SelectedText = ""; });
        var selectAllItem = new ToolStripMenuItem("모두 선택", null, (_, _) => mainEditor.SelectAll()) { ShortcutKeyDisplayString = "Ctrl+A" };
        findMenuItem = new ScopedMenuItem("찾기", (_, _) => findBar.Show(), () => Visible) { ShortcutKeys = Keys.Control | Keys.F };
        replaceMenuItem = new ScopedMenuItem("바꾸기", (_, _) => findBar.ShowReplace(), () => Visible) { ShortcutKeys = Keys.Control | Keys.H };
        editModeMenuItem.Visible = false; // 읽는 글은 고치지 않는다: [수정하기]는 없애고, 그 자리 버튼이 [줄바꿈 · 줄간격] 설정을 연다
        editMenu.DropDownItems.Add(editModeMenuItem);
        editMenu.DropDownItems.Add(new ToolStripSeparator());
        editMenu.DropDownItems.Add(undoItem);
        editMenu.DropDownItems.Add(new ToolStripSeparator());
        editMenu.DropDownItems.Add(cutItem);
        editMenu.DropDownItems.Add(copyItem);
        editMenu.DropDownItems.Add(pasteItem);
        editMenu.DropDownItems.Add(deleteItem);
        editMenu.DropDownItems.Add(new ToolStripSeparator());
        editMenu.DropDownItems.Add(findMenuItem);
        editMenu.DropDownItems.Add(replaceMenuItem);
        editMenu.DropDownItems.Add(selectAllItem);
        // 실행취소/잘라내기/붙여넣기/삭제는 읽기 전용 상태에서는 의미가 없으니 편집 모드일 때만 활성화한다.
        editOnlyMenuItems.AddRange(new ToolStripItem[] { undoItem, cutItem, pasteItem, deleteItem });
        // 찾기/복사/모두 선택은 mainEditor를 대상으로 하는 기능이라, epub을 보는 중엔 의미가 없어서 잠가둔다
        // (epub 안에서 직접 클릭해서 선택하고 Ctrl+C로 복사하는 건 브라우저가 알아서 처리한다).
        txtOnlyMenuItems.AddRange(new ToolStripItem[] { findMenuItem, replaceMenuItem, copyItem, selectAllItem });

        var viewMenu = new ToolStripMenuItem("보기");
        showImagesMenuItem = new ToolStripMenuItem("이미지 표시", null, (_, _) => ApplyShowImages()) { CheckOnClick = true, Checked = true };
        viewMenu.DropDownItems.Add(showImagesMenuItem); // 맨 위 "탐색"은 InitNavBar가 끼워 넣는다
        viewMenu.DropDownItems.Add(new ToolStripSeparator());

        // 스크롤/페이지/페이지 터치 — 셋 중 하나. "페이지 터치"를 고르면 화면 반쪽을 탭해서 넘길 수 있고,
        // 화살표 버튼은 그 대신 숨긴다(누를 자리가 화면 전체이니 굳이 작은 화살표가 따로 있을 필요가
        // 없다). 만화(이미지)는 스크롤 개념이 없어서 그 항목만 잠근다(아래 SetContentMode).
        readingModeMenu = new ToolStripMenuItem("읽기 방식");
        scrollModeMenuItem = new ToolStripMenuItem("스크롤", null, (_, _) => SetReadingMode(ReadingMode.Scroll)) { Checked = true };
        pageModeMenuItem = new ToolStripMenuItem("페이지", null, (_, _) => SetReadingMode(ReadingMode.Page));
        pageTouchModeMenuItem = new ToolStripMenuItem("페이지 터치", null, (_, _) => SetReadingMode(ReadingMode.PageTouch));
        readingModeMenu.DropDownItems.Add(scrollModeMenuItem);
        readingModeMenu.DropDownItems.Add(pageModeMenuItem);
        readingModeMenu.DropDownItems.Add(pageTouchModeMenuItem);
        viewMenu.DropDownItems.Add(readingModeMenu);

        fontSizeMenu = new ToolStripMenuItem("글자 크기");
        // 실제 키 처리는 TryHandleFeatureKey(Ctrl+- / Ctrl++ / Ctrl+0)와 Ctrl+휠이 맡고, 여기는 눌러서도 쓰고 단축키를 알려주는 용도.
        fontSizeMenu.DropDownItems.Add(new ToolStripMenuItem("화면 확대", null, (_, _) => ViewZoomBy(1)) { ShortcutKeyDisplayString = "Ctrl++" });
        fontSizeMenu.DropDownItems.Add(new ToolStripMenuItem("화면 축소", null, (_, _) => ViewZoomBy(-1)) { ShortcutKeyDisplayString = "Ctrl+-" });
        fontSizeMenu.DropDownItems.Add(new ToolStripMenuItem("화면 배율 100%", null, (_, _) => ViewZoomReset()) { ShortcutKeyDisplayString = "Ctrl+0" });
        fontSizeMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (int size in new[] { 10, 11, 12, 14, 16, 18, 20, 24, 28, 32 })
        {
            var item = new ToolStripMenuItem($"{size}pt", null, (_, _) => SetFontSize(size)) { Checked = size == (int)fontSize };
            fontSizeMenuItems.Add(item);
            fontSizeMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(fontSizeMenu);

        // 옵시디언 Image & Zip Viewer 플러그인의 "순서 / 나누기"와 동일 — 만화(zip/이미지) 모드에서만 쓰인다.
        orderMenu = new ToolStripMenuItem("순서 / 나누기");
        AddImageOrderItem(orderMenu, "왼쪽 → 오른쪽", ReadingOrder.Ltr);
        AddImageOrderItem(orderMenu, "오른쪽 → 왼쪽 (일본식)", ReadingOrder.Rtl);
        AddImageOrderItem(orderMenu, "왼쪽 → 오른쪽 (2개로 나누기)", ReadingOrder.LtrSpread);
        AddImageOrderItem(orderMenu, "오른쪽 → 왼쪽 (2개로 나누기)", ReadingOrder.RtlSpread);
        viewMenu.DropDownItems.Add(orderMenu);

        // 만화(이미지) 모드 전용 — 한 번에 한 장(한쪽 보기) / 두 장을 나란히(두쪽 보기).
        layoutMenu = new ToolStripMenuItem("보기 방식");
        foreach (var (label, dbl) in new[] { ("한쪽 보기", false), ("두쪽 보기", true) })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => ChooseImageLayout(dbl)) { Tag = dbl, Checked = dbl == settings.ImageDoublePage };
            imageLayoutMenuItems.Add(item);
            layoutMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(layoutMenu);

        // 만화(이미지) 모드 전용 — 이미지를 창에 맞추는 네 가지 방식.
        viewModeMenu = new ToolStripMenuItem("보기 모드");
        AddImageViewModeItem(viewModeMenu, "원본 크기로 보기", ImageViewMode.ActualSize);
        AddImageViewModeItem(viewModeMenu, "꽉차게 보기", ImageViewMode.FitWindow);
        AddImageViewModeItem(viewModeMenu, "꽉차게 보기 (폭 맞춤)", ImageViewMode.FitWidth);
        AddImageViewModeItem(viewModeMenu, "작은 그림도 꽉차게 보기", ImageViewMode.FitWindowUpscale);
        viewMenu.DropDownItems.Add(viewModeMenu);

        // 만화(이미지) 모드 전용 — 슬라이드쇼(일정 간격으로 자동 넘김)의 간격. 시작/정지는 낭독과 같은 F5 하나로
        // 묶여 있어서(자동 재생 메뉴, MainForm.Features.cs) 여기서는 메뉴만 만들고, 붙이는 건 그쪽에서 한다.
        slideshowMenu = new ToolStripMenuItem("만화 넘김 간격");
        foreach (int sec in new[] { 1, 2, 3, 5, 10 })
        {
            var item = new ToolStripMenuItem($"{sec}초마다", null, (_, _) => SetSlideshowInterval(sec)) { Checked = sec == slideshowIntervalSeconds };
            slideshowIntervalMenuItems.Add(item);
            slideshowMenu.DropDownItems.Add(item);
        }

        // 만화·소설(페이지 방식) — 마지막 페이지에서 더 넘기려 하면(또는 첫 페이지에서 더 되돌리려 하면) 옆 파일로 어떻게 할지.
        // 옆 파일이 없는 폴더의 끝 파일일 때는 설정창의 "처음 · 마지막 파일에서"가 정한다.
        endOfBookMenu = new ToolStripMenuItem("마지막 · 첫 페이지에서");
        AddEndOfBookItem(endOfBookMenu, "여기서 멈추기", EndOfBookAction.Stop);
        AddEndOfBookItem(endOfBookMenu, "안내 띄우기", EndOfBookAction.Ask);
        AddEndOfBookItem(endOfBookMenu, "자동으로 다음 · 이전 파일 열기", EndOfBookAction.NextFile);
        viewMenu.DropDownItems.Add(endOfBookMenu);

        menu.Items.Add(fileMenu);
        menu.Items.Add(editMenu);
        menu.Items.Add(viewMenu);
        menu.Items.Add(BuildFormatMenu()); // 파일 / 편집 / 보기 / 서식 / (자동 재생) / 도움말 — 뒤의 둘은 BuildFeatureMenus가 붙인다
        BuildFeatureMenus(menu, fileMenu, viewMenu); // 서재·테마·설정·낭독·도움말 (MainForm.Features.cs)

        // 메뉴바 오른쪽 끝에 "항상 위" 토글 — 드롭다운 없이 바로 눌러서 켜고 끈다.
        var alwaysOnTopItem = new ToolStripMenuItem("항상 위") { CheckOnClick = true, Alignment = ToolStripItemAlignment.Right };
        alwaysOnTopItem.CheckedChanged += (_, _) => { HostForm.TopMost = alwaysOnTopItem.Checked; UpdatePinLook(alwaysOnTopItem); }; // 탭 안에 들어 있으면 창(볼트 창)에 건다
        menu.Items.Add(alwaysOnTopItem);

        MainMenuStrip = menu;
        Load += (_, _) => AttachMenuGuards();   // 툴바 · 메뉴의 드롭다운도 밖을 한 번 누르면 닫히게(웹 화면 위여도)
        Shown += (_, _) => AttachMenuGuards();
        menu.AutoSize = false; menu.Height = 0; // 이 화면은 메뉴 줄을 안 쓴다(메뉴는 칸 머리의 ⋯로) — 남겨 두면 탭 밑에 빈 띠가 생긴다

        SetEditMode(false); // 시작은 항상 읽기 전용

        // WinForms는 Dock 컨트롤을 "나중에 추가한 것부터" 배치하므로,
        // Fill(editorHost) → Bottom(페이지 이동줄 → 상태바) → Top(메뉴) 순으로 추가해야
        // 위에서부터 [본문][페이지 이동줄][상태바] 순서로 쌓인다.
        // findBar는 Dock 없이 contentHost 위에 떠 있는 오버레이라 맨 나중에 추가해서 위로 올린다.
        Controls.Add(contentHost);
        Controls.Add(pageNavPanel);
        Controls.Add(statusLabel);
        InitStatusSegments();
        InitLookBar();
        Controls.Add(menu);
        Controls.Add(findBar);
        Controls.Add(prevNavBtn);
        Controls.Add(nextNavBtn);
        Controls.Add(loadingOverlay);
        InitNavBar(viewMenu, 0); // 테마를 입히는 InitFeatures보다 먼저 — 막대도 같이 칠해지게
        BuildToolbar(menu, alwaysOnTopItem);
        InitEdgePrompt();
        InitFeatures();
        InitHwp();
        InitTxtEdit();
        InitWriter();

        // 시작할 파일 열기는 Load 이후로 미룬다 — 생성자 도중(창 핸들이 아직 없어 Progress<T>가 UI
        // 스레드로 못 돌아오는 시점)에 곧장 열면, 이 파일이 커서 게이지가 뜨는 경우 진행률 보고가
        // 엉뚱한 스레드에서 컨트롤을 건드리게 될 수 있다.
        Load += (_, _) =>
        {
            RepositionOverlays();
            if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
                OpenPath(initialPath);
        };
        Resize += (_, _) => RepositionOverlays();
        // 프리페치 캐시에 들고 있던 이미지들이 창을 닫을 때 같이 정리되도록.
        FormClosed += (_, _) =>
        {
            loadCts?.Cancel(); imageSet?.Dispose(); ClearImageCache(); StopSlideshow(); mainEditorTapTimer.Dispose(); arrowHoverTimer.Dispose();
            TempCleanup.DeleteQuietly(epubExtractDir); // 풀어 둔 epub · 소설 본문 복사본은 창을 닫을 때 지운다
            TempCleanup.DeleteQuietly(txtTempDir);
        };

        if (string.IsNullOrEmpty(initialPath) || !File.Exists(initialPath))
            statusLabel.Text = "파일 > 열기로 .txt / .md / .epub / .pdf 파일을 선택하세요.";
    }

    /// <summary>확장자를 보고 txt/epub/pdf/zip(만화)/이미지 중 어느 쪽으로 열지 정한다(더블클릭, 명령줄 인자, 파일 열기 대화상자 공용).</summary>
    public void OpenPath(string path)
    {
        // 다른 책으로 넘어가기 전에, 읽던 자리를 기록해두고 낭독도 멈춘다.
        HideEdgePrompt();
        CapturePosition();
        StopTts();
        FlushAutoSave(); // 다른 파일로 가기 전에 고친 글을 저장(경로가 바뀐 뒤엔 엉뚱한 파일에 쓰이지 않게)
        // 슬라이드쇼 중이면 새 책이 뜰 때까지 타이머만 잠깐 세운다(큰 zip은 여는 동안에도 옛 책이 마지막 페이지라 다시 넘김이 일어나
        // 같은 파일을 또 여는 일이 없게). 새 만화가 뜨면 ShowImagePage가 다시 돌리고, 만화가 아니면 SetContentMode가 끝낸다.
        slideshowTimer?.Stop();
        if (hwpWatchedPath != null && !string.Equals(hwpWatchedPath, path, StringComparison.OrdinalIgnoreCase)) StopHwpWatch(); // 다른 책으로 가면 한글 저장 지켜보기도 끝
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (HwpDocument.IsHwpExtension(path)) LoadHwpFile(path); // 한글 문서: 글을 뽑아 txt처럼 (MainForm.Hwp.cs)
        else if (ext == ".epub") LoadEpubFile(path);
        else if (ext == ".pdf") LoadPdfFile(path);
        else if (ext == ".zip") LoadZip(path); // zip 안이 소설이면 소설로, 그림이면 만화로 (MainForm.Zip.cs)
        else if (ZipImageSet.IsImageExtension(path)) LoadSingleImageFile(path);
        else LoadFile(path);
    }

    // ───────────────────────── 읽기 ↔ 편집 모드 ─────────────────────────

    /// <summary>편집 메뉴의 "수정하기"는 txt/epub 공용이라, 지금 어느 쪽을 보고 있는지에 따라 알맞은 쪽으로 보낸다.</summary>
    private void SetEditMode(bool editable)
    {
        if (editable && lockMode) { ShowToast("잠금 모드에서는 수정할 수 없어요 — 잠금을 풀어 주세요"); return; }
        // 한글 문서는 서식(표 · 그림 · 글자 모양)을 지키려고 한컴 한글에서 고친다 — 커피뷰어 안의 글 상자로는 들어가지 않는다.
        if (editable && hwpSource) { EditHwpInHancom(); return; }
        if (editable) StopTts(); // 고치는 중에는 낭독하지 않는다(글이 바뀌면 읽던 위치가 어긋난다)
        else FlushAutoSave(); // 읽기 전용으로 돌아가기 전에 고친 글을 저장
        if (epubMode && !txtWeb) { SetEpubEditMode(editable); return; }

        // txt는 웹 화면에서 읽다가, 고칠 때만 텍스트 상자로 넘어갔다가 다 고치면 돌아온다.
        if (editable && txtWeb) LeaveTxtWebForEditing();
        bool wasEditing = !mainEditor.ReadOnly;
        SetTxtEditMode(editable);
        if (!editable && wasEditing && shownKind == ContentKind.Txt) ReturnToTxtWeb();
    }

    /// <summary>기본은 읽기 전용이고, 편집 메뉴의 "수정하기"를 눌러야만 내용을 고칠 수 있게 한다.</summary>
    private void SetTxtEditMode(bool editable)
    {
        if (!editable && !mainEditor.ReadOnly)
        {
            // 편집 중이었다면, 잠그기 전에 지금까지 고친 내용을 fullText에 반영해둔다.
            fullText = mainEditor.Text;
        }
        mainEditor.ReadOnly = !editable;
        editModeMenuItem.Text = editable ? "읽기 전용으로" : hwpSource ? "한글에서 수정하기" : "수정하기";
        foreach (var item in editOnlyMenuItems) item.Enabled = editable;
        pageModeMenuItem.Enabled = !editable; // 편집 중엔 페이지(글 조각) 모드로 못 바꾼다
        pageTouchModeMenuItem.Enabled = !editable;

        if (editable && pageMode) SetReadingMode(ReadingMode.Scroll); // 편집을 시작하면 안전하게 스크롤(전체 글)로 되돌린다
    }

    /// <summary>epub 챕터를 웹 페이지 그대로 직접 고쳐 쓸 수 있게(contentEditable) 켜고 끈다.</summary>
    private async void SetEpubEditMode(bool editable)
    {
        epubEditable = editable;
        editModeMenuItem.Text = editable ? "읽기 전용으로" : "수정하기";

        if (editable && epubPageMode) SetReadingMode(ReadingMode.Scroll); // 편집 중엔 다단으로 잘려 보이면 위험하니 스크롤로

        if (epubView?.CoreWebView2 == null) return;
        string script = editable ? "document.body.contentEditable = 'true';" : "document.body.contentEditable = 'false';";
        try { await epubView.CoreWebView2.ExecuteScriptAsync(script); } catch { }
    }

    // ───────────────────────── 챕터 · 이미지 목록(탐색 막대의 "챕터" / "이미지 선택") ─────────────────────────

    private List<string> CurrentChapterNames()
    {
        if (imageMode && imageSet != null)
            return Enumerable.Range(0, imageSet.Count).Select(i => imageSet.Name(i)).ToList();
        if (epubMode && !txtWeb)
            return epubBook?.Chapters.Select(c => c.Name).ToList() ?? new();
        return chapters.Select(c => c.Name).ToList();
    }

    /// <summary>탐색 막대의 목록에서 챕터(이미지)를 골랐을 때. 이미지면 그 장으로, epub이면 그 챕터 파일로, txt면 본문에서 그 위치로 이동한다.</summary>
    private void OnChapterActivated(int index)
    {
        HideEdgePrompt();
        if (imageMode)
        {
            JumpToImageItem(index);
        }
        else if (epubMode && !txtWeb)
        {
            ShowEpubChapter(index);
        }
        else if (index >= 0 && index < chapters.Count)
        {
            GoToOffset(chapters[index].Start);
            if (txtWeb) epubView?.Focus(); else mainEditor.Focus();
        }
        HostForm.Activate(); // 본문 창을 앞으로 가져온다(서재 안에 들어가 있으면 서재 창을)
    }

    /// <summary>전체 글 기준 위치(offset)로 이동한다. 페이지 모드면 그 위치부터 새로 한 페이지를 잡아서 연다.</summary>
    private void GoToOffset(int offset)
    {
        offset = Math.Clamp(offset, 0, fullText.Length);
        if (txtWeb) { ShowTxtWebAt(offset); return; }
        if (pageMode)
        {
            currentPageStart = offset;
            currentPageEnd = FindPageEnd(currentPageStart);
            ShowCurrentPage();
            mainEditor.SelectionStart = 0;
        }
        else
        {
            mainEditor.SelectionStart = offset;
            mainEditor.SelectionLength = 0;
            ScrollCharToTop(offset); // 막대로 옮기면 그 자리가 화면 맨 위에 오게(ScrollToCaret은 맨 아래에 걸린다)
            return;
        }
        mainEditor.SelectionLength = 0;
        mainEditor.ScrollToCaret();
    }

    /// <summary>
    /// 찾기 결과(전체 글 fullText 기준 offset)를 화면에 보여준다. 스크롤 모드면 그냥 그 위치를 선택하면
    /// 되지만, 페이지 모드에서는 mainEditor가 지금 페이지 조각만 담고 있으므로 찾은 위치가 지금 페이지
    /// 범위 밖이면 그 위치부터 시작하는 새 페이지로 먼저 이동한 뒤 그 안에서 선택한다.
    /// </summary>
    private void SelectInDocument(int start, int length)
    {
        if (txtWeb) { TxtWebSelect(start, length); return; }
        if (pageMode)
        {
            if (start < currentPageStart || start + length > currentPageEnd)
            {
                currentPageStart = start;
                currentPageEnd = FindPageEnd(currentPageStart);
                ShowCurrentPage();
            }
            mainEditor.Focus();
            mainEditor.Select(start - currentPageStart, length);
        }
        else
        {
            mainEditor.Focus();
            mainEditor.Select(start, length);
        }
        mainEditor.ScrollToCaret();
    }



    // 책의 끝·처음에 닿았을 때의 처리(HandleBookEdge)와 옆 파일·폴더 찾기는 MainForm.Edge.cs에 있다.

    /// <summary>txt/epub/이미지/pdf 화면 중 하나만 보이게 하고, 그 모드에 안 맞는 메뉴들은 잠그거나 푼다.</summary>
    private enum ContentKind { Txt, Epub, Image, Pdf }

    private void SetContentMode(ContentKind kind)
    {
        // 만화와 소설은 각자 설정의 기본 읽기 방식으로 시작한다. 같은 종류끼리 넘어갈 땐(만화 → 만화,
        // txt ↔ epub) 읽는 중에 바꾼 방식을 그대로 이어가고, 다른 종류에서 들어올 때만 기본값을 다시 적용한다.
        static bool IsNovel(ContentKind? k) => k is ContentKind.Txt or ContentKind.Epub;
        if (kind == ContentKind.Image && shownKind != ContentKind.Image)
        {
            tapToTurnEnabled = settings.ImagePageTouch;
        }
        else if (kind == ContentKind.Txt && directEdit)
        {
            // 메모장처럼 바로 쓰는 txt는 쪽 넘김이 없다(스크롤만).
            epubPageMode = false;
            pageMode = false;
            tapToTurnEnabled = false;
            ApplyEditorWrap();
        }
        else if (IsNovel(kind) && (!IsNovel(shownKind) || lastKindDirect))
        {
            // 여기서는 값만 정해둔다 — 불러온 쪽(ShowLoadedText · LoadEpubFile)이 이 값대로 본문을 그린다.
            // epub · zip 속 소설 · 한글 문서에서 뽑은 글은 웹 화면(다단 배치)이라 설정의 기본 읽기 방식(스크롤/페이지/페이지 터치)을 따른다.
            epubPageMode = settings.NovelReadingMode != NovelReadingMode.Scroll;
            pageMode = false;
            tapToTurnEnabled = settings.NovelReadingMode == NovelReadingMode.PageTouch;
            ApplyEditorWrap();
        }
        lastKindDirect = kind == ContentKind.Txt && directEdit;
        shownKind = kind;

        if (kind != ContentKind.Txt) { txtWeb = false; directEdit = false; }
        if (navBar != null) UpdateNavBarLayout(); // 에피소드를 쓰는 화면에서는 네비게이션 바를 숨기고, 읽는 화면으로 바뀌면 다시 보인다
        bool web = kind == ContentKind.Epub || (kind == ContentKind.Txt && txtWeb);
        epubMode = web;
        imageMode = kind == ContentKind.Image;
        editorHost.Visible = kind == ContentKind.Txt && !txtWeb;
        epubHost.Visible = web;
        imageHost.Visible = kind == ContentKind.Image;
        pdfHost.Visible = kind == ContentKind.Pdf;
        ApplySideMargin(); // 회차 글쓰기 칸 · 제목 줄을 켜고 끈다

        // 찾기/복사/모두 선택/실행취소/잘라내기/붙여넣기/삭제는 mainEditor 전용 기능이라 txt가 아니면 잠근다.
        bool isTxt = kind == ContentKind.Txt;
        // txt 웹 화면에서는 찾기만 된다(복사 · 모두 선택은 웹 화면 안에서 Ctrl+C · Ctrl+A로).
        foreach (var item in txtOnlyMenuItems) item.Enabled = isTxt && (!txtWeb || item == findMenuItem);
        foreach (var item in editOnlyMenuItems) item.Enabled = isTxt && !txtWeb && !mainEditor.ReadOnly;
        // 이미지(만화)·pdf는 수정·글자 크기·이미지 표시·저장 개념이 아예 없어서 다 잠근다(pdf는 뷰어
        // 자체 툴바에 인쇄·저장이 이미 있다). 읽기 방식은 이제 만화도 고를 수 있지만(페이지/페이지 터치),
        // 스크롤만은 만화에 없는 개념이라 그 항목만 잠그고, pdf는 페이지 넘김 자체를 뷰어가 알아서
        // 하므로 읽기 방식 메뉴를 통째로 잠근다.
        bool notReflowable = kind == ContentKind.Image || kind == ContentKind.Pdf;
        editModeMenuItem.Enabled = !notReflowable;
        scrollModeMenuItem.Enabled = kind != ContentKind.Image;
        pageModeMenuItem.Enabled = pageTouchModeMenuItem.Enabled = !(kind == ContentKind.Txt && directEdit); // 바로 쓰는 txt는 쪽 넘김이 없다
        readingModeMenu.Enabled = kind != ContentKind.Pdf;
        fontSizeMenu.Enabled = !notReflowable;
        showImagesMenuItem.Enabled = kind == ContentKind.Epub;
        saveMenuItem.Enabled = !notReflowable;
        saveAsMenuItem.Enabled = !notReflowable;
        orderMenu.Enabled = kind == ContentKind.Image; // "순서 / 나누기"는 만화(이미지) 모드 전용
        layoutMenu.Enabled = kind == ContentKind.Image; // "보기 방식"도 만화(이미지) 모드 전용
        viewModeMenu.Enabled = kind == ContentKind.Image; // "보기 모드"도 만화(이미지) 모드 전용
        slideshowMenu.Enabled = kind == ContentKind.Image; // "만화 넘김 간격"도 마찬가지
        endOfBookMenu.Enabled = kind != ContentKind.Pdf; // "마지막 · 첫 페이지에서"는 만화와 소설 모두(pdf는 뷰어가 알아서 넘긴다)
        if (kind != ContentKind.Image) StopSlideshow(); // 다른 모드로 나가면 자동 넘김도 멈춘다

        // 탐색 막대의 버튼 이름(챕터 / 이미지 선택)과 쓸 수 있는 것을 맞춘다. pdf는 챕터 목록 · 위치 막대를 잠근다.
        navKind = kind;
        ApplyLookBarShown();
        ConfigureNavBar(kind);

        // 지금 모드에 맞는 스크롤/페이지/페이지 터치 체크로 다시 맞춘다. 이미지(만화)는 항상 "페이지
        // 넘기기" 방식이라 스크롤은 나올 수 없고, pdf는 뷰어가 자체적으로 페이지를 넘기므로 우리 쪽
        // 화살표·표시줄은 아예 띄우지 않는다. 터치 켜짐 여부(tapToTurnEnabled)는 모드를 넘나들어도 그대로 이어간다.
        bool currentIsPage = kind switch { ContentKind.Image => true, ContentKind.Epub => epubPageMode, ContentKind.Pdf => false, _ => txtWeb ? epubPageMode : pageMode };
        UpdateReadingModeMenuChecks(!currentIsPage ? ReadingMode.Scroll : tapToTurnEnabled ? ReadingMode.PageTouch : ReadingMode.Page);
        UpdatePageNavVisibility(kind != ContentKind.Pdf && currentIsPage && !tapToTurnEnabled);
        imageCanvas.TapToTurnEnabled = tapToTurnEnabled;

        // 만화(이미지) 모드에서는 하단 표시줄에 텍스트 대신 꿀뷰 스타일 위치 막대를 보여준다. pdf는 뷰어
        // 자체 툴바가 위치를 보여주니 둘 다 끈다.
        bool showScrubber = kind == ContentKind.Image;
        pageScrubberBar.Visible = showScrubber;
        pageIndicatorLabel.Visible = !showScrubber && kind != ContentKind.Pdf;

        if (kind != ContentKind.Txt) findBar.Hide();
        if (kind == ContentKind.Image) imageCanvas.Focus();
        UpdateFeatureMenus(kind);
    }

    /// <summary>이전/다음 화살표·하단 위치 표시줄을 켜고 끄고, 위치를 다시 맞춘다. txt/epub/만화 세 모드가
    /// 화살표를 공용으로 쓴다(동그란 버튼 오버레이 — 본문/그림 폭은 안 줄이고 그 위에 그냥 얹는다).
    /// 화살표는 여기서 바로 띄우지 않고, 쓸 수 있는 상태일 때만 ArrowHoverTick이 마우스 위치를 보며 띄운다.</summary>
    private void UpdatePageNavVisibility(bool visible)
    {
        pageNavVisible = visible;
        // 탐색 막대가 켜져 있으면 위치 막대 · "약 N% 지점" 표시줄은 탐색 막대가 대신하므로 겹쳐 띄우지 않는다.
        pageNavPanel.Visible = visible && !settings.NavBarEnabled && !WritingEpisode;
        prevNavBtn.Visible = false;
        nextNavBtn.Visible = false;
        if (visible)
        {
            RepositionOverlays();
            arrowHoverTimer.Start();
            ArrowHoverTick(); // 이미 가장자리에 마우스가 있으면 기다리지 않고 바로 띄운다
        }
        else arrowHoverTimer.Stop();
    }

    /// <summary>마우스가 좌우 넘김 버튼 근처(버튼 둘레 ArrowNearRadius 안)에 오면 그쪽 버튼만 나타나고,
    /// 벗어나면 잠깐 뒤에 다시 숨는다. epub(WebView2) 위에서는 마우스 이벤트가 이 폼까지 안 올라오니,
    /// 이벤트 대신 커서 위치를 주기적으로 살핀다(탐색 막대의 OnNavTimer와 같은 방식).</summary>
    private const int ArrowNearRadius = 110;
    private const int ArrowHideDelayMs = 500;

    private void ArrowHoverTick()
    {
        if (!pageNavVisible) return;
        var p = PointToClient(Cursor.Position);
        bool active = (ContainsFocus || IsHostActive) && contentHost.Bounds.Contains(p);
        UpdateArrowHover(prevNavBtn, active, p, ref prevNavLastNear);
        UpdateArrowHover(nextNavBtn, active, p, ref nextNavLastNear);
    }

    private static void UpdateArrowHover(ArrowOverlayButton btn, bool active, Point p, ref DateTime lastNear)
    {
        var zone = btn.Bounds;
        zone.Inflate(ArrowNearRadius, ArrowNearRadius);
        bool near = active && zone.Contains(p);
        if (near) lastNear = DateTime.Now;

        if (near && !btn.Visible)
        {
            btn.Visible = true;
            btn.BringToFront();
        }
        else if (!near && btn.Visible && (DateTime.Now - lastNear).TotalMilliseconds > ArrowHideDelayMs)
        {
            btn.Visible = false;
        }
    }

    // ───────────────────────── 보기: 글자 크기 ─────────────────────────

    private void SetFontSize(int size)
    {
        size = Math.Clamp(size, 8, 48);
        // 열린 파일이 있으면 그 파일에만 기억하고, 없으면 설정창 기본값으로.
        if (lookRec != null) { lookRec.LookSize = size; library.MarkDirty(); library.SaveIfDirty(); }
        else if (settings.FontSize != size) { settings.FontSize = size; settings.Save(); }
        ApplyLook();
    }
}
