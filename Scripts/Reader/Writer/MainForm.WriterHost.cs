namespace Cafeum;

/// <summary>
/// 글쓰기 위젯(시계 · 특수문자 · 맞춤법)과 AI 도구가 에피소드 창(MainForm)을 쓰는 창구(IWriterHost)와, 그 위젯들을 들고 있는 자리.
/// 위젯 · AI 도구의 코드는 Widget · AI 폴더의 독립 클래스에 있고, MainForm은 만들어서 켜고 끄는 일만 한다.
/// </summary>
public partial class MainForm : IWriterHost
{
    private ClockWidgetController? clock;
    private SpecialWidgetController? special;
    private SpellWidgetController? spell;
    private NameWidgetController? names;
    private LlamaWriterTools? llama;

    /// <summary>글쓰기 위젯을 만든다(글 상자를 만든 뒤, 한 번). 시계는 특수문자 위젯이 보이면 그 아래에 처음 자리를 잡는다.</summary>
    private void InitFloatingWidgets()
    {
        special = new SpecialWidgetController(this);
        clock = new ClockWidgetController(this, () => special?.VisibleHeight is int h ? h + LogicalToDeviceUnits(12) : 0);
        spell = new SpellWidgetController(this);
        names = new NameWidgetController(this, () => { int h = 0; if (special?.VisibleHeight is int a) h += a + LogicalToDeviceUnits(12); if (clock?.VisibleHeight is int c) h += c + LogicalToDeviceUnits(12); return h; });
    }

    /// <summary>켜 둔 위젯을 지금 화면(txt 편집 중인지, 글 칸이 보이는지)에 맞게 보이거나 감춘다.</summary>
    private void SyncFloatingWidgets() { special?.Sync(); clock?.Sync(); spell?.Sync(); names?.Sync(); }

    // 위젯 모음 · 사이드바가 부르는 입구(NovelWorkspace → DocPane → 여기)
    internal void ToggleClockWidget() => clock?.Toggle();
    internal bool ClockWidgetOn => clock?.IsOn ?? false;
    internal void ApplyClockPlace() => clock?.ApplyPlace();
    internal void ToggleSpecialWidget() => special?.Toggle();
    internal bool SpecialWidgetOn => special?.IsOn ?? false;
    internal void ApplySpecialSize() => special?.ApplySize();
    internal void ToggleSpellWidget() => spell?.Toggle();
    internal bool SpellWidgetOn => spell?.IsOn ?? false;
    internal void ToggleNameWidget() => names?.Toggle();
    internal bool NameWidgetOn => names?.IsOn ?? false;

    private void AddLlamaMenu(ToolStripMenuItem tools) => (llama ??= new LlamaWriterTools(this)).AddMenu(tools);

    // ───────── IWriterHost ─────────
    PageLockTextBox IWriterHost.Editor => mainEditor;
    Control IWriterHost.EditorHost => editorHost;
    Control IWriterHost.FloatingParent => TopLevelControl ?? (Control)editorHost;
    Form IWriterHost.HostForm => HostForm;
    AppSettings IWriterHost.Settings => settings;
    ReaderTheme IWriterHost.Theme => theme;
    bool IWriterHost.TxtEditing => directEdit && shownKind == ContentKind.Txt;
    bool IWriterHost.TxtWritable => shownKind == ContentKind.Txt && !txtWeb;
    string? IWriterHost.CurrentPath => currentPath;
    int IWriterHost.Dpi(int logical) => LogicalToDeviceUnits(logical);
    void IWriterHost.ShowToast(string text) => ShowToast(text);
    void IWriterHost.EnsureCaretVisible() => tall?.EnsureCaretVisible(false);
    void IWriterHost.SummarySaved() { RefreshWriterProps(); EpisodeMetaChanged?.Invoke(); }
    event FormClosedEventHandler IWriterHost.FormClosed { add => FormClosed += value; remove => FormClosed -= value; }
}
