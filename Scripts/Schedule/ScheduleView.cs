using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cafeum;

/// <summary>집필 일정 한 건. Cal은 어느 캘린더(색)에 속하는지.</summary>
internal sealed class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Date { get; set; } = "";   // yyyy-MM-dd
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public string Cal { get; set; } = "";
    public int StartMin { get; set; } = -1;   // 시작 시각(0시부터 분). -1이면 종일
    public int DurMin { get; set; } = 60;     // 길이(분)
    public string Place { get; set; } = "";
    public string Note { get; set; } = "";
    public string EndDate { get; set; } = "";  // 여러 날에 걸친 일정의 마지막 날(yyyy-MM-dd). 비면 하루짜리
}

/// <summary>캘린더(일정 묶음): 이름 · 색 · 보이기. 달력에서 같은 색으로 보인다.</summary>
internal sealed class ScheduleCal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4aa3df";
    public bool Visible { get; set; } = true;
    public string Note { get; set; } = "";
    /// <summary>맨 위의 지워지지 않는 [새 스케줄](일정을 찍을 때 새 이름 · 새 색으로 스케줄을 만드는 자리).</summary>
    public bool IsDefault { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Color ColorValue { get { try { return ColorTranslator.FromHtml(Color); } catch { return System.Drawing.Color.SteelBlue; } } }
}

/// <summary>일정 저장소(앱 데이터 폴더의 schedule.json). 볼트와 상관없이 하나를 같이 쓴다.</summary>
internal static class ScheduleStore
{
    private sealed class Data
    {
        public List<ScheduleCal> Calendars { get; set; } = new();
        public List<ScheduleEntry> Entries { get; set; } = new();
        public bool HolidaysVisible { get; set; } = true;
        public string HolidayColor { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string FilePath => Path.Combine(AppSettings.DataDir, "schedule.json");
    private static Data? data;

    public static event Action? Changed;

    private static Data D
    {
        get
        {
            if (data == null) return data = LoadData();
            if (!JsonFile.IsUnreadable(FilePath)) return data;
            // 처음에 다른 프로그램이 잡고 있어서 못 읽어 기본값으로 시작한 경우(그동안 저장은 거부된다): 잠금이 풀렸으면 같은 목록 객체를 채워서 이어 간다
            var fresh = LoadData();
            if (JsonFile.IsUnreadable(FilePath)) return data;
            data.Calendars.Clear(); data.Calendars.AddRange(fresh.Calendars);
            data.Entries.Clear(); data.Entries.AddRange(fresh.Entries);
            data.HolidaysVisible = fresh.HolidaysVisible; data.HolidayColor = fresh.HolidayColor;
            return data;
        }
    }

    private static Data LoadData()
    {
        var d = JsonFile.LoadWith<Data>(FilePath, json => json.TrimStart().StartsWith("[") // 예전 형식(일정 목록만)도 읽는다
            ? new Data { Entries = JsonSerializer.Deserialize<List<ScheduleEntry>>(json, Opts) ?? new() }
            : JsonSerializer.Deserialize<Data>(json, Opts)) ?? new();
        d.Calendars ??= new();
        d.Entries ??= new();
        if (d.Calendars.Count == 0)
        {
            d.Calendars.Add(new ScheduleCal { Name = "집필", Color = "#4aa3df" });
            d.Calendars.Add(new ScheduleCal { Name = "마감", Color = "#2b2570" });
            d.Calendars.Add(new ScheduleCal { Name = "휴식", Color = "#a0714f" });
        }
        // 맨 위에 지워지지 않는 [새 스케줄]이 늘 있게 한다
        var def = d.Calendars.FirstOrDefault(c => c.IsDefault);
        if (def == null) def = new ScheduleCal { Id = "default", Name = "새 스케줄", Color = "#9aa0a6", IsDefault = true };
        d.Calendars.Remove(def);
        d.Calendars.Insert(0, def);
        foreach (var c in d.Calendars) if (c.IsDefault) c.Visible = true; // 맨 위 [새 스케줄]은 늘 보인다(나머지는 저장된 보이기 / 안 보이기를 따른다)
        int dropped = d.Entries.RemoveAll(e => e == null || !TryParse(e.Date, out _));
        if (dropped > 0) AppLog.Write($"날짜를 읽지 못한 일정 {dropped}개를 목록에서 뺐어요(schedule.json)");
        foreach (var e in d.Entries) if (d.Calendars.All(c => c.Id != e.Cal)) e.Cal = d.Calendars[0].Id;
        return d;
    }

    public static List<ScheduleCal> Calendars => D.Calendars;
    public static List<ScheduleEntry> All => D.Entries;
    public static Color HolidayColor
    {
        get { try { return string.IsNullOrEmpty(D.HolidayColor) ? Color.FromArgb(0xE5, 0x48, 0x4D) : ColorTranslator.FromHtml(D.HolidayColor); } catch { return Color.FromArgb(0xE5, 0x48, 0x4D); } }
        set { D.HolidayColor = ColorTranslator.ToHtml(value); Save(); }
    }
    public static void ResetHolidayColor() { D.HolidayColor = ""; Save(); }
    public static bool HolidaysVisible { get => D.HolidaysVisible; set { D.HolidaysVisible = value; Save(); } }
    public static ScheduleCal CalOf(string id) => D.Calendars.FirstOrDefault(c => c.Id == id) ?? D.Calendars[0];

    public static string Key(DateTime d) => d.ToString("yyyy-MM-dd");
    /// <summary>내 스케줄 목록에서의 순서(달력에서 일정을 늘어놓는 순서).</summary>
    public static int CalIndex(ScheduleEntry e) { int i = D.Calendars.FindIndex(c => c.Id == e.Cal); return i < 0 ? int.MaxValue : i; }
    /// <summary>며칠에 걸친 일정인가.</summary>
    public static bool IsMulti(ScheduleEntry e) => !string.IsNullOrEmpty(e.EndDate) && string.CompareOrdinal(e.EndDate, e.Date) > 0;
    /// <summary>그날(k)이 이 일정의 기간 안인가(하루짜리는 그날만).</summary>
    public static bool Covers(ScheduleEntry e, string k) => IsMulti(e) ? string.CompareOrdinal(e.Date, k) <= 0 && string.CompareOrdinal(e.EndDate, k) >= 0 : e.Date == k;
    public static bool TryParse(string s, out DateTime d) => DateTime.TryParseExact(s, "yyyy-MM-dd", null, DateTimeStyles.None, out d);

    public static void Add(DateTime d, string text, string cal, int startMin = -1, int durMin = 60, string place = "", string note = "", DateTime? end = null)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        bool multi = end is DateTime ed && ed.Date > d.Date;
        All.Add(new ScheduleEntry { Date = Key(d), Text = text, Cal = cal, StartMin = multi ? -1 : startMin, DurMin = multi ? 0 : durMin, Place = place, Note = note, EndDate = multi ? Key(end!.Value) : "" });
        Save();
    }

    public static void Remove(ScheduleEntry e) { All.Remove(e); Save(); }

    /// <summary>며칠짜리 일정에서 그날(day)만 지운다: 가장자리면 줄어들고, 중간이면 앞뒤 둘로 갈라진다. 하루짜리는 그냥 지운다.</summary>
    public static void RemoveDay(ScheduleEntry e, DateTime day)
    {
        if (!IsMulti(e) || !TryParse(e.Date, out var s) || !TryParse(e.EndDate, out var f) || day.Date < s || day.Date > f) { Remove(e); return; }
        day = day.Date;
        if (day == s) { e.Date = Key(day.AddDays(1)); if (day.AddDays(1) == f) e.EndDate = ""; Save(); return; }
        if (day == f) { e.EndDate = day.AddDays(-1) == s ? "" : Key(day.AddDays(-1)); Save(); return; }
        // 중간: 뒤쪽 조각을 새 일정으로
        var tail = new ScheduleEntry { Date = Key(day.AddDays(1)), EndDate = day.AddDays(1) == f ? "" : Key(f), Text = e.Text, Done = e.Done, Cal = e.Cal, StartMin = e.StartMin, DurMin = e.DurMin, Place = e.Place, Note = e.Note };
        e.EndDate = day.AddDays(-1) == s ? "" : Key(day.AddDays(-1));
        All.Insert(All.IndexOf(e) + 1, tail);
        Save();
    }

    /// <summary>colors of the 7 that no schedule uses yet (ignoreId: the schedule being edited keeps its own color)</summary>
    public static List<Color> FreeColors(string? ignoreId = null) => SchedulePalette7.Colors.Where(c => !Calendars.Any(x => !x.IsDefault && x.Id != ignoreId && SchedulePalette7.Same(x.ColorValue, c))).ToList();

    public static ScheduleCal AddCal(string name, Color color, string note = "")
    {
        var dup = Calendars.FirstOrDefault(c => !c.IsDefault && SchedulePalette7.Same(c.ColorValue, color));
        if (dup != null) return dup; // one schedule per color
        var cal = new ScheduleCal { Name = name.Trim(), Color = ColorTranslator.ToHtml(color), Note = note };
        Calendars.Add(cal);
        Save();
        return cal;
    }

    public static void RemoveCal(ScheduleCal c)
    {
        if (c.IsDefault) return; // 맨 위 [새 스케줄]은 지울 수 없다
        Calendars.Remove(c);
        All.RemoveAll(e => e.Cal == c.Id); // 그 캘린더의 일정도 함께
        Save();
    }

    public static void Save()
    {
        JsonFile.Save(FilePath, D, Opts);
        Changed?.Invoke();
    }
}

/// <summary>
/// 스케줄러(노션 캘린더 모양): 가운데는 큰 월간 달력(일정은 캘린더 색의 칩), 오른쪽은 미니 달력 + 캘린더(색) 목록.
/// 날짜 칸의 [+](또는 더블클릭)로 일정 추가, 칩을 누르면 완료 · 수정 · 삭제.
/// </summary>
internal sealed class ScheduleView : Panel
{
    private readonly WeekGrid grid;
    private readonly MiniMonth mini;
    private readonly CalList calList;
    private readonly Panel side;
    private readonly Panel topBar;
    private readonly DayPanel dayNote = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly FullScreenButton fullButton = new();
    private LibraryPalette? palette;
    private DateTime selected = DateTime.Today;

    public event Action? FullScreenRequested;
    public void SetFullScreenIcon(bool on) => fullButton.On = on;

    public ScheduleView()
    {
        DoubleBuffered = true;
        grid = new WeekGrid { Dock = DockStyle.Fill };
        mini = new MiniMonth { Dock = DockStyle.Top };
        calList = new CalList { Dock = DockStyle.Fill };
        side = new Panel { Dock = DockStyle.Right, Width = 260 };
        // 맨 위 가로줄(전체 화면 버튼): 아래 달력과 선으로 나눈다
        topBar = new Panel { Dock = DockStyle.Top, Height = 38 }; // 전체 화면 아이콘 높이만큼만(아이콘 32 + 위아래 3)
        topBar.Controls.Add(fullButton);
        topBar.Paint += (_, e) => { using var pen = new Pen(palette?.Line ?? Color.Gainsboro); e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1); };
        topBar.Resize += (_, _) => PlaceFull();
        side.Controls.Add(dayNote);
        side.Controls.Add(calList);
        side.Controls.Add(mini);
        side.Controls.Add(topBar);

        Controls.Add(grid);
        Controls.Add(side);
        fullButton.Click += (_, _) => FullScreenRequested?.Invoke();

        grid.DateSelected += d => Select(d);
        grid.ViewChanged += _ => ApplyView();
        dayNote.EntryClicked += (en, at) => EditEntry(en, at, selected);
        dayNote.TodayClicked += () => { Select(DateTime.Today); grid.GoTo(DateTime.Today); mini.Month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); };
        grid.WeekChanged += _ => mini.Month = grid.Selected;
        grid.CreateRequested += (d, a, b, at) => CreateEntry(d, a, b, at);
        grid.RangeRequested += (a, b, at) =>
        {
            var br = grid.Brush;
            if (br != null) ScheduleStore.Add(a, br.Name, br.Id, -1, 0, "", "", b); else CreateEntry(a, -1, -1, at, b);
        };
        grid.PaintRequested += (d, a, b) => { var br = grid.Brush; if (br == null) return; ScheduleStore.Add(d, br.Name, br.Id, a, a < 0 ? 0 : Math.Max(15, b - a)); if (grid.View != 0) Select(d); };
        calList.ActiveChanged += c => grid.Brush = c is { IsDefault: true } ? null : c;
        calList.PickFirst();
        grid.EntryClicked += (en, at) => EditEntry(en, at, grid.LastClickDay); // 일정을 누르면 바로 보기 · 수정 창
        mini.DateSelected += d => { Select(d); grid.GoTo(d); };
        calList.Changed += () => { calList.Fit(); grid.Invalidate(); mini.Invalidate(); calList.Invalidate(); };
        ScheduleStore.Changed += OnStoreChanged;
        Disposed += (_, _) => ScheduleStore.Changed -= OnStoreChanged;
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private void PlaceFull() => fullButton.SetBounds(topBar.ClientSize.Width - S(12) - S(32), (topBar.ClientSize.Height - S(32)) / 2, S(32), S(32));
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); topBar.Height = S(38); side.Width = S(260); PlaceFull(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); topBar.Height = S(38); side.Width = S(260); PlaceFull(); }
    private void OnStoreChanged() { if (IsHandleCreated) { calList.Fit(); grid.Invalidate(); mini.Invalidate(); calList.Invalidate(); dayNote.Invalidate(); } }

    /// <summary>월 보기에서는 작은 달력 대신 [오늘 날짜 + 오늘 찍어 둔 스케줄 목록]을, 주 보기에서는 작은 달력을 보인다. (오른쪽 아래 "내 스케줄"은 그대로)</summary>
    private void ApplyView()
    {
        bool month = grid.View == 0;
        mini.Visible = !month;
        dayNote.Visible = month;
        calList.Dock = month ? DockStyle.Bottom : DockStyle.Fill;
        calList.Fit();
        if (month) { dayNote.SetDay(selected); calList.SelectDefault(); }
    }

    private void Select(DateTime d) { selected = d; grid.Selected = d; mini.Selected = d; dayNote.SetDay(d); }

    /// <summary>스케줄러를 열 때: 오늘 날짜로 맞춘다.</summary>
    public void ShowToday() { Select(DateTime.Today); grid.GoTo(DateTime.Today); mini.Month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); grid.ScrollToNow(); grid.ShowMonthView(); ApplyView(); }

    public void SetPalette(LibraryPalette p)
    {
        palette = p;
        BackColor = p.Main;
        side.BackColor = p.Side;
        topBar.BackColor = p.Dark ? p.Main : Color.White; // 전체 화면 아이콘 줄은 하얗게(어두운 테마에서는 본문색)
        fullButton.BackColor = topBar.BackColor;
        topBar.Invalidate();
        grid.SetPalette(p); mini.SetPalette(p); dayNote.SetPalette(p); calList.SetPalette(p);
        fullButton.SetPalette(p);
    }

    private void CreateEntry(DateTime d, int startMin, int endMin, Point screen, DateTime? end = null)
    {
        if (palette == null) return;
        using var dlg = new ScheduleEntryDialog(palette, "", ScheduleStore.Calendars.FirstOrDefault(c => c.Visible)?.Id ?? ScheduleStore.Calendars[0].Id, d, startMin, endMin);
        PlaceDialog(dlg, screen);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        string calId = dlg.CalId;
        if (dlg.NewColor is Color nc) calId = ScheduleStore.AddCal(dlg.EntryText, nc).Id;
        DateTime? endShift = end is DateTime e0 ? dlg.EntryDate + (e0.Date - d.Date) : null; // 날짜를 바꾸면 기간도 같이 옮긴다
        ScheduleStore.Add(dlg.EntryDate, dlg.EntryText, calId, dlg.StartMin, dlg.DurMin, dlg.Place, dlg.Note, endShift);
        if (grid.View != 0) { Select(dlg.EntryDate); grid.GoTo(dlg.EntryDate); } // 월 보기에서는 오른쪽 날짜를 바꾸지 않는다(숫자를 눌러야 바뀐다)
    }

    private void EntryMenu(ScheduleEntry en, Point screen)
    {
        var m = new ContextMenuStrip();
        MenuGuard.Attach(m);
        m.Items.Add(en.Done ? "완료 취소" : "완료로 표시", null, (_, _) => { en.Done = !en.Done; ScheduleStore.Save(); });
        m.Items.Add("수정…", null, (_, _) => BeginInvoke(() => EditEntry(en, screen)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("삭제", null, (_, _) => ScheduleStore.RemoveDay(en, selected));
        m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
        m.Show(screen);
    }

    private void EditEntry(ScheduleEntry en, Point screen, DateTime? day = null)
    {
        if (palette == null || !ScheduleStore.TryParse(en.Date, out var d)) return;
        using var dlg = new ScheduleEntryDialog(palette, en.Text, en.Cal, d, en.StartMin, en.StartMin < 0 ? -1 : en.StartMin + en.DurMin, en.Place, en.Note, en.Done, editing: true);
        PlaceDialog(dlg, screen);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        if (dlg.Deleted) { ScheduleStore.RemoveDay(en, day ?? d); return; }
        en.Text = dlg.EntryText.Trim();
        en.Cal = dlg.NewColor is Color nc2 ? ScheduleStore.AddCal(dlg.EntryText, nc2).Id : dlg.CalId;
        int span = ScheduleStore.IsMulti(en) && ScheduleStore.TryParse(en.EndDate, out var oldEnd) ? (int)(oldEnd - d).TotalDays : 0;
        en.Date = ScheduleStore.Key(dlg.EntryDate);
        en.EndDate = span > 0 && dlg.StartMin < 0 ? ScheduleStore.Key(dlg.EntryDate.AddDays(span)) : "";
        en.StartMin = dlg.StartMin;
        en.DurMin = dlg.DurMin;
        en.Place = dlg.Place;
        en.Note = dlg.Note;
        en.Done = dlg.Done;
        ScheduleStore.Save();
    }
    /// <summary>스케줄 창은 늘 가운데(스케줄러가 들어 있는 창의 한가운데)에 뜬다.</summary>
    private static void PlaceDialog(Form f, Point screen) => f.StartPosition = FormStartPosition.CenterParent;
    // ───────── 가운데: 주간 시간표 ─────────

    /// <summary>한 주(일~토) 시간표: 위에 종일 줄, 아래에 시간 줄(0~24시, 휠로 스크롤). 빈 곳을 끌면 그 시간대로 새 일정.</summary>
    private sealed class WeekGrid : Control
    {
        private LibraryPalette? p;
        private DateTime weekStart = StartOfWeek(DateTime.Today);
        private DateTime selectedDay = DateTime.Today;
        private int scrollY;
        private Rectangle prevRect, nextRect, todayRect;
        private readonly Rectangle[] viewRects = new Rectangle[3]; // 월 · 주 · 일 보기 전환 아이콘
        private int titleRight;
        private int curView = 0;                                    // 0 월 · 1 주 · 2 일 (일은 아직)
        private readonly ToolTip tip = new();
        private int hot = -1; // 0 이전 · 1 오늘 · 2 다음
        private readonly List<(Rectangle R, ScheduleEntry E)> blocks = new();
        private readonly System.Windows.Forms.Timer clock = new() { Interval = 30_000 };
        // 끌어서 새 일정
        private bool dragging;
        private int dragCol, dragA, dragB;
        private bool dragAll;
        private ScheduleEntry? lastBlock;
        private long lastBlockTick;

        public event Action<DateTime>? DateSelected;
        public event Action<DateTime>? WeekChanged;
        public event Action<DateTime, int, int, Point>? CreateRequested; // 날짜, 시작(분, 종일 -1), 끝(분), 화면 위치
        /// <summary>오른쪽에서 고른 스케줄(색)로 칠하기: 날짜, 시작(분, 시간 없이 -1), 끝(분).</summary>
        public event Action<DateTime, int, int>? PaintRequested;
        /// <summary>마지막으로 누른 일정이 놓인 날(월: 누른 칸, 주: 누른 열). 며칠짜리 일정에서 그날만 지울 때 쓴다.</summary>
        public DateTime LastClickDay { get; private set; } = DateTime.Today;
        private void NoteClickDay(Point pt) { LastClickDay = curView == 0 ? (MonthDayAt(pt, true) ?? selectedDay) : weekStart.AddDays(Math.Max(0, ColAt(pt.X))); }
        /// <summary>월 보기에서 가로로 그어 며칠에 걸친 일정을 만든다: 첫 날, 끝 날, 화면 위치.</summary>
        public event Action<DateTime, DateTime, Point>? RangeRequested;
        private DateTime? mFrom, mTo; // 월 보기에서 긋는 중인 날짜 범위

        private DateTime? MonthDayAt(Point pt, bool clamp)
        {
            var (top, rowH, rows, first) = MonthGeo();
            int col = (pt.X - Gutter) / Math.Max(1, ColW), row = (pt.Y - top) / Math.Max(1, rowH);
            if (clamp) { col = Math.Clamp(col, 0, 6); row = Math.Clamp(row, 0, rows - 1); }
            else if (pt.X < Gutter || col < 0 || col > 6 || pt.Y < top || row < 0 || row >= rows) return null;
            return first.AddDays(row * 7 + col);
        }
        /// <summary>지금 고른 스케줄(붓). 있으면 시간표를 누르거나 끌 때 그 색으로 바로 칠해진다.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ScheduleCal? Brush { get; set; }
        public event Action<ScheduleEntry, Point>? EntryClicked;

        public static DateTime StartOfWeek(DateTime d) => d.Date.AddDays(-(int)d.DayOfWeek);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DateTime Selected { get => selectedDay; set { selectedDay = value; var ws = StartOfWeek(value); if (ws != weekStart) { weekStart = ws; WeekChanged?.Invoke(ws); } Invalidate(); } }

        public WeekGrid()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            clock.Tick += (_, _) => Invalidate();
            clock.Start();
            Disposed += (_, _) => { clock.Dispose(); tip.Dispose(); };
        }

        public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Main; Invalidate(); }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

        private int TitleH => S(108);
        private int DayHeadH => S(58);
        private int Gutter => S(58);
        private int HourH => S(52);
        private int ColW => Math.Max(S(40), (Width - Gutter - S(44)) / 7); // 오른쪽에 주 이동 화살표 자리
        private int AllDayH
        {
            get
            {
                int most = 0;
                var vis = VisibleIds();
                for (int c = 0; c < 7; c++)
                {
                    string k = ScheduleStore.Key(weekStart.AddDays(c));
                    most = Math.Max(most, ScheduleStore.All.Count(e => ScheduleStore.Covers(e, k) && vis.Contains(e.Cal)));
                }
                return S(10) + Math.Min(3, Math.Max(1, most)) * S(22);
            }
        }
        private int GridTop => TitleH + DayHeadH + AllDayH;
        private Rectangle GridRect => new(0, GridTop, Width, Math.Max(0, Height - GridTop));
        private int MaxScroll => Math.Max(0, 24 * HourH - GridRect.Height + S(8));
        private HashSet<string> VisibleIds() => ScheduleStore.Calendars.Where(c => c.Visible).Select(c => c.Id).ToHashSet();

        /// <summary>그 날짜의 주로 옮긴다(시간 줄은 그대로).</summary>
        public void GoTo(DateTime d) { selectedDay = d; var ws = StartOfWeek(d); if (ws != weekStart) { weekStart = ws; WeekChanged?.Invoke(ws); } Invalidate(); }

        /// <summary>열 때: 지금 시각 조금 위로 스크롤.</summary>
        public void ScrollToNow()
        {
            int hour = Math.Clamp(DateTime.Now.Hour - 2, 0, 23);
            scrollY = Math.Clamp(hour * HourH, 0, MaxScroll);
            Invalidate();
        }

        private void LayoutButtons()
        {
            int bs = S(34);

            todayRect = Rectangle.Empty; // [오늘] 버튼은 뺐다
            int cy = TitleH + (curView == 0 ? S(26) : S(26)) - S(18); // 월 · 주 모두 요일 글자 줄 높이 // 날짜 숫자 줄과 같은 높이
            prevRect = new Rectangle(S(12), cy, S(36), S(36));      // ‹ 지난주
            nextRect = new Rectangle(Width - S(46), cy, S(36), S(36)); // › 다음주
            titleRight = Width - S(60);
            for (int k = 0; k < 3; k++) viewRects[k] = new Rectangle(S(28) + k * S(44), S(30), S(44), bs);
        }

        private int ColAt(int x) { int c = (x - Gutter) / ColW; return x >= Gutter && c is >= 0 and < 7 ? c : -1; }
        private int MinuteAt(int y) => Math.Clamp((int)Math.Round((y - GridTop + scrollY) / (double)HourH * 60 / 15.0) * 15, 0, 24 * 60);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            LayoutButtons();
            if (mFrom != null) { var nt = MonthDayAt(e.Location, true); if (nt != mTo) { mTo = nt; Invalidate(); } return; }
            if (dragging) { dragB = dragAll ? -1 : MinuteAt(e.Y); Invalidate(); return; }
            int h = prevRect.Contains(e.Location) ? 0 : todayRect.Contains(e.Location) ? 1 : nextRect.Contains(e.Location) ? 2 : viewRects[0].Contains(e.Location) ? 3 : viewRects[1].Contains(e.Location) ? 4 : viewRects[2].Contains(e.Location) ? 5 : -1;
            bool onBlock = blocks.Any(b => b.R.Contains(e.Location));
            bool onCell = curView == 0 && ColAt(e.X) >= 0 && e.Y >= MonthGeo().Top;
            Cursor = h >= 0 || onBlock || onCell ? Cursors.Hand : Cursors.Default;
            if (h != hot) { hot = h; tip.SetToolTip(this, h == 3 ? "월" : h == 4 ? "주" : h == 5 ? "일 (준비 중)" : null); Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            LayoutButtons();
            if (prevRect.Contains(e.Location) || todayRect.Contains(e.Location) || nextRect.Contains(e.Location) || viewRects.Any(v => v.Contains(e.Location))) return;
            if (blocks.Any(b => b.R.Contains(e.Location))) return;
            if (curView == 0)
            {   // 월 보기: 날짜 칸(숫자 줄 아래)에서 누르면 긋기 시작, 놓을 때 한 칸이면 예전처럼 누르기로 처리
                var (mtop, mrowH, _, _) = MonthGeo();
                if (MonthDayAt(e.Location, false) is DateTime md && (e.Y - mtop) % mrowH >= S(32)) { mFrom = mTo = md; Capture = true; }
                return;
            }
            int col = ColAt(e.X);
            if (col < 0) return;
            if (e.Y >= TitleH + DayHeadH && e.Y < GridTop) { dragging = true; dragAll = true; dragCol = col; dragA = dragB = -1; Capture = true; return; } // 종일 줄
            if (!GridRect.Contains(e.Location)) { // 요일 머리: 그 날을 고른다
                if (e.Y >= TitleH && e.Y < TitleH + DayHeadH) { selectedDay = weekStart.AddDays(col); DateSelected?.Invoke(selectedDay); Invalidate(); }
                return;
            }
            dragging = true; dragAll = false; dragCol = col; dragA = dragB = MinuteAt(e.Y); Capture = true;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (mFrom is DateTime mf0)
            {
                var mt0 = mTo ?? mf0; mFrom = mTo = null; Capture = false; Invalidate();
                if (mt0 != mf0 && e.Button == MouseButtons.Left)
                {
                    var a0 = mf0 < mt0 ? mf0 : mt0; var b0 = mf0 < mt0 ? mt0 : mf0;
                    RangeRequested?.Invoke(a0, b0, PointToScreen(e.Location));
                    return;
                }
            }
            if (dragging)
            {
                dragging = false; Capture = false;
                var day = weekStart.AddDays(dragCol);
                var at = PointToScreen(new Point(Gutter + dragCol * ColW + ColW, Math.Clamp(e.Y, GridTop, Height - S(40))));
                if (dragAll) { if (Brush != null) PaintRequested?.Invoke(day, -1, -1); else CreateRequested?.Invoke(day, -1, -1, at); }
                else
                {
                    int a = Math.Min(dragA, dragB), b = Math.Max(dragA, dragB);
                    if (b - a < 15) b = a + 60;          // 그냥 누르면 한 시간짜리
                    b = Math.Min(b, 24 * 60);
                    if (b <= a) a = b - 15;
                    if (Brush != null) PaintRequested?.Invoke(day, a, b); else CreateRequested?.Invoke(day, a, b, at);
                }
                Invalidate();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            // 일정(블록 · 칩): 두 번 연속 누르면 보기 · 수정 창(프레임워크의 더블클릭 이벤트에 기대지 않고 직접 센다)
            foreach (var (r, en) in blocks)
                if (r.Contains(e.Location))
                {
                    NoteClickDay(e.Location);
                    EntryClicked?.Invoke(en, PointToScreen(new Point(r.Left, r.Bottom))); // 한 번 누르면 바로 보기 · 수정 창
                    return;
                }
            LayoutButtons();
            if (viewRects[0].Contains(e.Location)) SetView(0);
            else if (viewRects[1].Contains(e.Location)) SetView(1);
            else if (prevRect.Contains(e.Location)) { if (curView == 0) StepMonth(-1); else { GoTo(weekStart.AddDays(-7 + (int)selectedDay.DayOfWeek)); DateSelected?.Invoke(selectedDay); } }
            else if (nextRect.Contains(e.Location)) { if (curView == 0) StepMonth(1); else { GoTo(weekStart.AddDays(7 + (int)selectedDay.DayOfWeek)); DateSelected?.Invoke(selectedDay); } }
            else if (todayRect.Contains(e.Location)) { GoTo(DateTime.Today); DateSelected?.Invoke(DateTime.Today); ScrollToNow(); }
            else if (curView == 0) MonthClick(e.Location);
        }

        /// <summary>일정(블록 · 칩)을 두 번 누르면 보기 · 수정 창이 뜬다.</summary>
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            foreach (var (r, en) in blocks)
                if (r.Contains(e.Location)) { NoteClickDay(e.Location); EntryClicked?.Invoke(en, PointToScreen(new Point(r.Left, r.Bottom))); return; }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (curView == 0) { StepMonth(e.Delta > 0 ? -1 : 1); return; } // 월 보기: 휠로 달 넘기기
            scrollY = Math.Clamp(scrollY - e.Delta / 120 * HourH, 0, MaxScroll);
            Invalidate();
        }

        private static string HourLabel(int h) => h == 0 ? "" : h == 12 ? "정오" : h < 12 ? $"오전 {h}시" : $"오후 {h - 12}시";
        public static string TimeText(int min) { int h = min / 60 % 24, m = min % 60; return $"{(h < 12 ? "오전" : "오후")} {(h % 12 == 0 ? 12 : h % 12)}:{m:00}"; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (p == null) { g.Clear(SystemColors.Control); return; }
            var pal = p;
            g.Clear(pal.Main);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            LayoutButtons();
            blocks.Clear();
            scrollY = Math.Clamp(scrollY, 0, MaxScroll);
            string ff = NovelStore.UiFontFamily;
            if (curView == 0) { PaintMonth(g, pal, ff); return; }
            const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            Color red = Color.FromArgb(0xE5, 0x48, 0x4D);
            Color lineCol = Themes.Blend(pal.Main, pal.Fore, 0.10f);
            var vis = VisibleIds();
            int colW = ColW, gutter = Gutter;

            // 시간 줄(스크롤): 0~24시
            var grid = GridRect;
            var state = g.Save();
            g.SetClip(grid);
            using (var lp = new Pen(lineCol))
            using (var gf = new Font(ff, 8f))
            {
                for (int h = 0; h <= 24; h++)
                {
                    int y = grid.Top + h * HourH - scrollY;
                    if (y < grid.Top - HourH || y > grid.Bottom + HourH) continue;
                    g.DrawLine(lp, gutter - S(6), y, gutter + 7 * colW, y);
                    if (h is > 0 and < 24 && y - S(9) >= grid.Top && y + S(9) <= grid.Bottom) // 영역 경계에 걸쳐 반쯤 잘려 보이는 시각 글자는 그리지 않는다
                        TextRenderer.DrawText(g, HourLabel(h), gf, new Rectangle(0, y - S(9), gutter - S(10), S(18)), Themes.Blend(pal.Main, pal.Fore, 0.5f), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                for (int c = 0; c <= 7; c++) g.DrawLine(lp, gutter + c * colW, grid.Top, gutter + c * colW, grid.Bottom);
            }
            // 오늘 열을 아주 옅게
            for (int c = 0; c < 7; c++)
                if (weekStart.AddDays(c).Date == DateTime.Today)
                {
                    using var tb = new SolidBrush(Color.FromArgb(pal.Dark ? 18 : 14, pal.Accent));
                    g.FillRectangle(tb, gutter + c * colW + 1, grid.Top, colW - 1, grid.Height);
                }

            using (var tf2 = new Font(ff, 9f, FontStyle.Bold))
            using (var sf = new Font(ff, 8f))
                for (int c = 0; c < 7; c++)
                {
                    string k = ScheduleStore.Key(weekStart.AddDays(c));
                    var list = ScheduleStore.All.Where(x => x.Date == k && x.StartMin >= 0 && vis.Contains(x.Cal)).OrderBy(x => x.StartMin).ThenBy(x => x.DurMin).ToList();
                    // 겹치는 일정은 옆으로 나란히(차선 배정)
                    var lanes = new List<int>(); var laneEnd = new List<int>(); var cluster = new List<int>();
                    int clusterEnd = -1, clusterStart = 0;
                    var laneOf = new int[list.Count]; var lanesIn = new int[list.Count];
                    for (int i = 0; i < list.Count; i++)
                    {
                        int s0 = list[i].StartMin, e0 = s0 + Math.Max(15, list[i].DurMin);
                        if (s0 >= clusterEnd) { for (int j = clusterStart; j < i; j++) lanesIn[j] = laneEnd.Count; laneEnd.Clear(); clusterStart = i; clusterEnd = -1; }
                        int lane = laneEnd.FindIndex(le => le <= s0);
                        if (lane < 0) { laneEnd.Add(e0); lane = laneEnd.Count - 1; } else laneEnd[lane] = e0;
                        laneOf[i] = lane; clusterEnd = Math.Max(clusterEnd, e0);
                    }
                    for (int j = clusterStart; j < list.Count; j++) lanesIn[j] = Math.Max(1, laneEnd.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var en = list[i];
                        int w = (colW - S(6)) / Math.Max(1, lanesIn[i]);
                        int y0 = grid.Top + (int)(en.StartMin * HourH / 60.0) - scrollY;
                        int hgt = Math.Max(S(20), (int)(Math.Max(15, en.DurMin) * HourH / 60.0) - S(2));
                        var r = new Rectangle(gutter + c * colW + S(3) + laneOf[i] * w, y0 + 1, w - S(2), hgt);
                        if (r.Bottom < grid.Top || r.Top > grid.Bottom) { blocks.Add((r, en)); continue; }
                        DrawBlock(g, en, r, tf2, sf, pal);
                        blocks.Add((Rectangle.Intersect(r, grid), en));
                    }
                }

            // 끌어서 만드는 중인 일정(유령)
            if (dragging && !dragAll)
            {
                int a = Math.Min(dragA, dragB), b = Math.Max(dragA, dragB);
                if (b - a < 15) b = a + 60;
                var r = new Rectangle(gutter + dragCol * colW + S(3), grid.Top + (int)(a * HourH / 60.0) - scrollY, colW - S(6), (int)((b - a) * HourH / 60.0));
                var brushCol = Brush?.ColorValue ?? pal.Accent;
                using var gb = new SolidBrush(Color.FromArgb(110, brushCol));
                using var gp = LibraryPalette.Rounded(r, S(6));
                g.FillPath(gb, gp);
                using var gpen = new Pen(brushCol, S(1)); g.DrawPath(gpen, gp);
                using var f = new Font(ff, 8f);
                TextRenderer.DrawText(g, $"{TimeText(a)} – {TimeText(b)}", f, Rectangle.Inflate(r, -S(6), 0), pal.Fore, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
            }

            // 지금 시각 선: 이번 주에는 한 주 전체(일~토)를 가로질러 이어진다. 오늘 열은 진하게, 나머지 날은 옅게, 왼쪽 끝에 점.
            for (int c = 0; c < 7; c++)
                if (weekStart.AddDays(c).Date == DateTime.Today)
                {
                    int y = grid.Top + (int)(DateTime.Now.TimeOfDay.TotalMinutes * HourH / 60.0) - scrollY;
                    using (var thin = new Pen(Color.FromArgb(110, red), Math.Max(1f, S(1)))) g.DrawLine(thin, gutter, y, gutter + 7 * colW, y);
                    using var rp = new Pen(red, S(2)); using var rb = new SolidBrush(red);
                    g.DrawLine(rp, gutter + c * colW, y, gutter + (c + 1) * colW, y);
                    g.FillEllipse(rb, gutter - S(4), y - S(4), S(8), S(8)); // 점은 한 주 선의 왼쪽 끝(일요일 열 시작)에
                }            g.Restore(state);

            // 시간 줄에서 위로 삐져나온 글자·블록은 머리 영역(제목 · 요일 · 종일 줄)을 바탕으로 덮어서 가린다
            using (var hb = new SolidBrush(pal.Main)) g.FillRectangle(hb, 0, 0, Width, GridTop);


            DrawHeader(g, pal, ff);
            using (var bf = new Font(ff, 10f))
            using (var icon = new Font(MainForm.IconFontName(), 10f))
            {
                void Btn(Rectangle r, int idx, string text, bool isIcon)
                {
                    using var path = LibraryPalette.Rounded(r, S(8));
                    if (hot == idx) { using var fill = new SolidBrush(pal.CardHover); g.FillPath(fill, path); }
                    using var pen = new Pen(pal.Line); g.DrawPath(pen, path);
                    TextRenderer.DrawText(g, text, isIcon ? icon : bf, r, pal.Fore, mid);
                }
            }

            // 요일 머리: 요일 + 날짜 숫자(오늘은 빨간 원)
            string[] days = { "일", "월", "화", "수", "목", "금", "토" };
            using (var wf = new Font(ff, 9f))
            using (var nf = new Font(ff, 13f, FontStyle.Bold))
                for (int c = 0; c < 7; c++)
                {
                    var d = weekStart.AddDays(c);
                    int x = gutter + c * colW;
                    Color wc = c == 0 ? red : c == 6 ? Color.FromArgb(0x4A, 0x7F, 0xD0) : Themes.Blend(pal.Main, pal.Fore, 0.6f);
                    TextRenderer.DrawText(g, days[c], wf, new Rectangle(x, TitleH, colW, S(20)), wc, mid);
                    var circle = new Rectangle(x + (colW - S(32)) / 2, TitleH + S(22), S(32), S(32));
                    bool isToday = d.Date == DateTime.Today;
                    if (isToday) { using var b = new SolidBrush(red); g.FillEllipse(b, circle); }
                    else if (d.Date == selectedDay.Date) { using var pen = new Pen(pal.Accent, S(2)); g.DrawEllipse(pen, circle); }
                    string? whol = ScheduleStore.HolidaysVisible ? KoreanHolidays.NameOn(d) : null;
                    TextRenderer.DrawText(g, d.Day.ToString(), nf, circle, isToday ? Color.White : whol != null ? ScheduleStore.HolidayColor : pal.Fore, mid);
                    if (whol != null) using (var hfw = new Font(ff, 8f)) TextRenderer.DrawText(g, whol, hfw, new Rectangle(circle.Right + S(3), circle.Y, Math.Max(10, x + colW - circle.Right - S(5)), circle.Height), ScheduleStore.HolidayColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                }

            // 종일 줄
            int adTop = TitleH + DayHeadH, adH = AllDayH;
            using (var lp = new Pen(lineCol)) { g.DrawLine(lp, 0, adTop, Width, adTop); g.DrawLine(lp, 0, adTop + adH, Width, adTop + adH); }
            using (var af = new Font(ff, 8f))
                TextRenderer.DrawText(g, "스케줄", af, new Rectangle(0, adTop, gutter - S(8), adH), Themes.Blend(pal.Main, pal.Fore, 0.5f), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var itemFont = new Font(ff, 8.5f))
                for (int c = 0; c < 7; c++)
                {
                    string k = ScheduleStore.Key(weekStart.AddDays(c));
                    var list = ScheduleStore.All.Where(x => ScheduleStore.Covers(x, k) && vis.Contains(x.Cal)).OrderBy(x => ScheduleStore.CalIndex(x)).ThenBy(x => x.StartMin).ThenBy(x => x.Text).ToList(); // 그날 적어 둔 일정을 짧게 모아서 위에
                    int show = list.Count <= 3 ? list.Count : 2;
                    for (int i = 0; i < show; i++)
                    {
                        var r = new Rectangle(gutter + c * colW + S(3), adTop + S(5) + i * S(22), colW - S(6), S(19));
                        DrawChip(g, list[i], r, itemFont, pal, left, true);
                        blocks.Add((r, list[i]));
                    }
                    if (list.Count > 3)
                        TextRenderer.DrawText(g, $"+{list.Count - 2}개 더", itemFont, new Rectangle(gutter + c * colW + S(8), adTop + S(5) + 2 * S(22), colW - S(12), S(19)), Themes.Blend(pal.Main, pal.Fore, 0.55f),
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                }

        }

        /// <summary>맨 위 머리: 제목("2026 11월", 고른 날의 달) · 보기 전환 아이콘 · 이전/다음 꺾쇠. 주 · 월 보기가 같이 쓴다.</summary>
        private void DrawHeader(Graphics g, LibraryPalette pal, string ff)
        {
            using (var mf = new Font(ff, 26f, FontStyle.Bold))
            using (var yf = new Font(ff, 26f, FontStyle.Regular))
            {
                string mt = $" {selectedDay.Month}월", yt = $"{selectedDay.Year}";
                var msz = TextRenderer.MeasureText(g, mt, mf, Size.Empty, TextFormatFlags.NoPadding);
                var ysz = TextRenderer.MeasureText(g, yt, yf, Size.Empty, TextFormatFlags.NoPadding);
                int tx = titleRight - (msz.Width + ysz.Width), ty = S(47) - msz.Height / 2;
                TextRenderer.DrawText(g, yt, yf, new Point(tx, ty), Themes.Blend(pal.Main, pal.Fore, 0.55f), TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, mt, mf, new Point(tx + ysz.Width, ty), pal.Fore, TextFormatFlags.NoPadding);
            }
            DrawViewSwitch(g, pal);
            DrawChevron(g, prevRect, true, hot == 0 ? pal.Fore : Themes.Blend(pal.Main, pal.Fore, 0.85f));
            DrawChevron(g, nextRect, false, hot == 2 ? pal.Fore : Themes.Blend(pal.Main, pal.Fore, 0.85f));
        }

        // ───────── 월 보기 ─────────

        private int MonthHeadH => S(46); // 제목 밑 요일 줄까지

        /// <summary>월 보기 격자: 위 · 한 줄 높이 · 줄 수 · 첫 칸(그 달 1일이 든 주의 일요일).</summary>
        private (int Top, int RowH, int Rows, DateTime First) MonthGeo()
        {
            var m1 = new DateTime(selectedDay.Year, selectedDay.Month, 1);
            int rows = ((int)m1.DayOfWeek + DateTime.DaysInMonth(m1.Year, m1.Month) + 6) / 7;
            int top = TitleH + MonthHeadH;
            int rowH = Math.Max(S(64), (Height - top - S(8)) / rows);
            return (top, rowH, rows, m1.AddDays(-(int)m1.DayOfWeek));
        }

        /// <summary>월 단위로 이동(고른 날은 같은 일, 없으면 그 달 마지막 날).</summary>
        private void StepMonth(int n) { GoTo(selectedDay.AddMonths(n)); DateSelected?.Invoke(selectedDay); }

        private void SetView(int k) { if (k == curView) return; curView = k; hot = -1; Invalidate(); ViewChanged?.Invoke(k); }
        /// <summary>0 월 · 1 주.</summary>
        public int View => curView;
        public void ShowMonthView() => SetView(0);
        public event Action<int>? ViewChanged;

        /// <summary>날짜 칸을 눌렀다: 숫자 줄이면 그 날을 고르고, 빈 곳이면 그 날 종일 일정을 만든다(붓이 있으면 바로 칠한다).</summary>
        private void MonthClick(Point pt)
        {
            var (top, rowH, rows, first) = MonthGeo();
            int col = ColAt(pt.X);
            if (col < 0 || pt.Y < top) return;
            int row = (pt.Y - top) / rowH;
            if (row >= rows) return;
            var day = first.AddDays(row * 7 + col);
            if (pt.Y < top + row * rowH + S(32)) { selectedDay = day; DateSelected?.Invoke(day); Invalidate(); return; }
            selectedDay = day; DateSelected?.Invoke(day); Invalidate(); // ?좎쭨瑜??꾨Ⅴ硫??ㅻⅨ履쎌뿉 洹몃궇???섑??쒕떎
            if (Brush != null) PaintRequested?.Invoke(day, -1, -1);
            else CreateRequested?.Invoke(day, -1, -1, PointToScreen(pt));
        }

        private void PaintMonth(Graphics g, LibraryPalette pal, string ff)
        {
            const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            Color red = Color.FromArgb(0xE5, 0x48, 0x4D);
            Color lineCol = Themes.Blend(pal.Main, pal.Fore, 0.10f);
            Color dim = Themes.Blend(pal.Main, pal.Fore, 0.5f);
            var vis = VisibleIds();
            int colW = ColW, gutter = Gutter;
            var (top, rowH, rows, first) = MonthGeo();
            int right = gutter + 7 * colW, bottom = top + rows * rowH;

            // 요일 줄
            string[] days = { "일", "월", "화", "수", "목", "금", "토" };
            using (var wf = new Font(ff, 9f))
                for (int c = 0; c < 7; c++)
                {
                    Color wc = c == 0 ? red : c == 6 ? Color.FromArgb(0x4A, 0x7F, 0xD0) : Themes.Blend(pal.Main, pal.Fore, 0.6f);
                    TextRenderer.DrawText(g, days[c], wf, new Rectangle(gutter + c * colW, TitleH + S(14), colW, S(24)), wc, mid);
                }

            // 격자 선 + 오늘 칸 옅게
            using (var lp = new Pen(lineCol))
            {
                for (int r = 0; r <= rows; r++) g.DrawLine(lp, gutter, top + r * rowH, right, top + r * rowH);
                for (int c = 0; c <= 7; c++) g.DrawLine(lp, gutter + c * colW, top, gutter + c * colW, bottom);
            }
            for (int i = 0; i < rows * 7; i++)
                if (first.AddDays(i).Date == DateTime.Today)
                {
                    using var tb = new SolidBrush(Color.FromArgb(pal.Dark ? 18 : 14, pal.Accent));
                    g.FillRectangle(tb, gutter + i % 7 * colW + 1, top + i / 7 * rowH + 1, colW - 1, rowH - 1);
                }

            // 며칠에 걸친 일정: 줄(주)마다 칸을 가로로 이어 붙이는 막대. 막대가 차지한 줄 수(laneUsed)만큼 그날 칩은 아래로 밀린다
            int step0 = S(21), fit = Math.Max(0, (rowH - S(30)) / step0);
            var cellLanes = new int[rows * 7]; // 칸마다 막대가 차지한 줄 수(막대가 없는 칸은 위부터 칩을 채운다)
            var segs = new List<(ScheduleEntry E, int Row, int C0, int C1, int Lane)>();
            var multis = ScheduleStore.All.Where(x => ScheduleStore.IsMulti(x) && vis.Contains(x.Cal)).OrderBy(x => ScheduleStore.CalIndex(x)).ThenBy(x => x.Date).ThenByDescending(x => x.EndDate).ThenBy(x => x.Text).ToList();
            for (int r = 0; r < rows; r++)
            {
                var ws = first.AddDays(r * 7); var we = ws.AddDays(6);
                var laneEnd = new List<int>();
                foreach (var en in multis)
                {
                    if (!ScheduleStore.TryParse(en.Date, out var s0) || !ScheduleStore.TryParse(en.EndDate, out var f0)) continue;
                    if (f0 < ws || s0 > we) continue;
                    int c0 = (int)((s0 > ws ? s0 : ws) - ws).TotalDays, c1 = (int)((f0 < we ? f0 : we) - ws).TotalDays;
                    int lane = laneEnd.FindIndex(le => le < c0);
                    if (lane < 0) { laneEnd.Add(c1); lane = laneEnd.Count - 1; } else laneEnd[lane] = c1;
                    if (lane < fit) { segs.Add((en, r, c0, c1, lane)); for (int cc = c0; cc <= c1; cc++) cellLanes[r * 7 + cc] = Math.Max(cellLanes[r * 7 + cc], lane + 1); }
                }
            }

            using (var nf = new Font(ff, 10f, FontStyle.Bold))
            using (var itemFont = new Font(ff, 8.5f))
                for (int i = 0; i < rows * 7; i++)
                {
                    var d = first.AddDays(i);
                    int x = gutter + i % 7 * colW, y = top + i / 7 * rowH;
                    bool inMonth = d.Month == selectedDay.Month, isToday = d.Date == DateTime.Today;
                    var circle = new Rectangle(x + S(5), y + S(4), S(24), S(24));
                    if (isToday) { using var b = new SolidBrush(red); g.FillEllipse(b, circle); }
                    else if (d.Date == selectedDay.Date) { using var pen = new Pen(pal.Accent, S(2)); g.DrawEllipse(pen, circle); }
                    Color nc = isToday ? Color.White : !inMonth ? Themes.Blend(pal.Main, pal.Fore, 0.3f) : i % 7 == 0 ? red : i % 7 == 6 ? Color.FromArgb(0x4A, 0x7F, 0xD0) : pal.Fore;
                    string? hol = ScheduleStore.HolidaysVisible ? KoreanHolidays.NameOn(d) : null;
                    if (hol != null && !isToday && inMonth) nc = ScheduleStore.HolidayColor;
                    TextRenderer.DrawText(g, d.Day.ToString(), nf, circle, nc, mid);
                    if (hol != null) TextRenderer.DrawText(g, hol, itemFont, new Rectangle(circle.Right + S(4), y + S(4), Math.Max(10, colW - circle.Width - S(14)), S(24)), inMonth ? ScheduleStore.HolidayColor : Color.FromArgb(120, ScheduleStore.HolidayColor), left);

                    // 그날 일정: 칸에 들어갈 만큼만 칩으로, 넘치면 "+N개 더"
                    string k = ScheduleStore.Key(d);
                    var list = ScheduleStore.All.Where(e => e.Date == k && !ScheduleStore.IsMulti(e) && vis.Contains(e.Cal)).OrderBy(e => ScheduleStore.CalIndex(e)).ThenBy(e => e.StartMin < 0 ? -1 : e.StartMin).ThenBy(e => e.Text).ToList();
                    int lanes = cellLanes[i];
                    int step = S(21), cap = Math.Max(0, (rowH - S(30)) / step - lanes);
                    int show = list.Count <= cap ? list.Count : Math.Max(0, cap - 1);
                    for (int j = 0; j < show; j++)
                    {
                        var r = new Rectangle(x + S(3), y + S(32) + (lanes + j) * step, colW - S(6), S(19));
                        DrawChip(g, list[j], r, itemFont, pal, left, false);
                        blocks.Add((r, list[j]));
                    }
                    if (list.Count > show && cap > 0)
                        TextRenderer.DrawText(g, $"+{list.Count - show}개 더", itemFont, new Rectangle(x + S(8), y + rowH - S(22), colW - S(12), S(19)), dim,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                }

            using (var bf = new Font(ff, 8.5f))
                foreach (var sg in segs)
                {
                    var br = new Rectangle(gutter + sg.C0 * colW + S(3), top + sg.Row * rowH + S(32) + sg.Lane * step0, (sg.C1 - sg.C0 + 1) * colW - S(6), S(19));
                    DrawChip(g, sg.E, br, bf, pal, left, false);
                    blocks.Add((br, sg.E));
                }
            if (mFrom is DateTime pf && mTo is DateTime pt2 && pf != pt2)
            {   // 긋는 중: 지나는 칸을 옅게 물들인다
                var pa = pf < pt2 ? pf : pt2; var pb = pf < pt2 ? pt2 : pf;
                using var pv = new SolidBrush(Color.FromArgb(55, pal.Accent));
                for (int r = 0; r < rows; r++)
                {
                    var ws = first.AddDays(r * 7); var we = ws.AddDays(6);
                    if (pb < ws || pa > we) continue;
                    int c0 = (int)((pa > ws ? pa : ws) - ws).TotalDays, c1 = (int)((pb < we ? pb : we) - ws).TotalDays;
                    g.FillRectangle(pv, gutter + c0 * colW + 1, top + r * rowH + 1, (c1 - c0 + 1) * colW - 1, rowH - 1);
                }
            }

            DrawHeader(g, pal, ff);
        }

        /// <summary>왼쪽의 보기 전환: 월 · 주 · 일 아이콘(지금 보기는 눌린 모양). 일 보기는 나중에 채운다.</summary>
        private void DrawViewSwitch(Graphics g, LibraryPalette pal)
        {
            var all = Rectangle.Union(Rectangle.Union(viewRects[0], viewRects[1]), viewRects[2]);
            using (var path = LibraryPalette.Rounded(all, S(8))) using (var pen = new Pen(pal.Line)) g.DrawPath(pen, path);
            for (int k = 0; k < 3; k++)
            {
                var r = viewRects[k];
                bool on = k == curView;
                if (on)
                {
                    using var path = LibraryPalette.Rounded(Rectangle.Inflate(r, -S(2), -S(2)), S(6));
                    using var fill = new SolidBrush(Themes.Blend(pal.Main, pal.Fore, pal.Dark ? 0.20f : 0.12f));
                    g.FillPath(fill, path);
                }
                else if (hot == 3 + k)
                {
                    using var path = LibraryPalette.Rounded(Rectangle.Inflate(r, -S(2), -S(2)), S(6));
                    using var fill = new SolidBrush(pal.CardHover);
                    g.FillPath(fill, path);
                }
                Color ic = on ? pal.Fore : Themes.Blend(pal.Main, pal.Fore, 0.55f);
                DrawCalIcon(g, new Rectangle(r.X + (r.Width - S(28)) / 2, r.Y + (r.Height - S(28)) / 2, S(28), S(28)), k, ic, pal);            }
        }
        /// <summary>달력 모양 아이콘: 위의 고리 두 개 + 칸. kind 0 월(칸이 가득) · 1 주(WEEK) · 2 일(DAY).</summary>
        private void DrawCalIcon(Graphics g, Rectangle r, int kind, Color col, LibraryPalette pal)
        {
            float lw = Math.Max(1.6f, r.Width / 13f);
            var body = new RectangleF(r.X + lw, r.Y + r.Height * 0.16f, r.Width - lw * 2, r.Height * 0.80f);
            using var pen = new Pen(col, lw) { LineJoin = LineJoin.Round };
            using var br = new SolidBrush(col);
            using (var bp = LibraryPalette.Rounded(Rectangle.Round(body), (int)(r.Width * 0.14f))) g.DrawPath(pen, bp);
            // 고리 두 개
            float rx1 = body.X + body.Width * 0.30f, rx2 = body.X + body.Width * 0.70f;
            using (var rp = new Pen(col, lw * 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(rp, rx1, r.Y + r.Height * 0.03f, rx1, r.Y + r.Height * 0.26f);
                g.DrawLine(rp, rx2, r.Y + r.Height * 0.03f, rx2, r.Y + r.Height * 0.26f);
            }
            float top = body.Y + body.Height * 0.26f;
            if (kind == 0) // 월: 머리 줄 + 칸 격자(채운 네모)
            {
                g.DrawLine(pen, body.X, top, body.Right, top);
                float cw = body.Width / 5f, chh = (body.Bottom - top) / 3.4f;
                for (int row = 0; row < 3; row++)
                    for (int c = 0; c < 4; c++)
                    {
                        if (row == 0 && c == 0) continue;
                        float bx = body.X + cw * 0.45f + c * cw, by = top + chh * 0.35f + row * chh * 1.0f;
                        g.FillRectangle(br, bx, by, cw * 0.72f, chh * 0.72f);
                    }
            }
            else
            {
                using var tf = new Font(NovelStore.UiFontFamily, kind == 1 ? Math.Max(5f, r.Width * 0.235f) : Math.Max(5f, r.Width * 0.31f), FontStyle.Bold, GraphicsUnit.Pixel);
                var tr = new Rectangle((int)body.X, (int)(top - lw), (int)body.Width, (int)(body.Bottom - top + lw));
                TextRenderer.DrawText(g, kind == 1 ? "WEEK" : "DAY", tf, tr, col, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }
        /// <summary>주 이동 화살표(얇고 큰 꺾쇠).</summary>
        private void DrawChevron(Graphics g, Rectangle r, bool left, Color col)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, h = S(11), w = S(6);
            using var pen = new Pen(col, S(2) + 0.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float dx = left ? w : -w;
            g.DrawLines(pen, new[] { new PointF(cx + dx, cy - h), new PointF(cx - dx, cy), new PointF(cx + dx, cy + h) });
        }
        private static void DrawChip(Graphics g, ScheduleEntry en, Rectangle r, Font f, LibraryPalette pal, TextFormatFlags left, bool allDay)
        {
            var col = ScheduleStore.CalOf(en.Cal).ColorValue;
            using var path = LibraryPalette.Rounded(r, 5);
            using var fill = new SolidBrush(Themes.Blend(pal.Main, col, en.Done ? 0.10f : (pal.Dark ? 0.35f : 0.22f)));
            g.FillPath(fill, path);
            using var bar = new SolidBrush(en.Done ? Themes.Blend(pal.Main, col, 0.4f) : col);
            g.FillRectangle(bar, r.X, r.Y + 3, 3, r.Height - 6);
            using var ff2 = new Font(f, en.Done ? FontStyle.Strikeout : FontStyle.Regular);
            TextRenderer.DrawText(g, en.Text, ff2, new Rectangle(r.X + 9, r.Y, r.Width - 12, r.Height), en.Done ? pal.Dim : pal.Fore, left);
        }

        private void DrawBlock(Graphics g, ScheduleEntry en, Rectangle r, Font title, Font sub, LibraryPalette pal)
        {
            var col = ScheduleStore.CalOf(en.Cal).ColorValue;
            using var path = LibraryPalette.Rounded(r, S(6));
            using var fill = new SolidBrush(Themes.Blend(pal.Main, col, en.Done ? 0.12f : (pal.Dark ? 0.40f : 0.26f)));
            g.FillPath(fill, path);
            var oldClip = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            using var bar = new SolidBrush(en.Done ? Themes.Blend(pal.Main, col, 0.4f) : col);
            g.FillRectangle(bar, r.X, r.Y, S(4), r.Height);
            g.Clip = oldClip;
            using var tf = new Font(title, en.Done ? FontStyle.Bold | FontStyle.Strikeout : FontStyle.Bold);
            var tr = new Rectangle(r.X + S(9), r.Y + S(2), r.Width - S(12), Math.Min(r.Height - S(2), title.Height + S(2)));
            TextRenderer.DrawText(g, en.Text, tf, tr, en.Done ? pal.Dim : pal.Fore, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (r.Height >= S(44))
                TextRenderer.DrawText(g, $"{TimeText(en.StartMin)} – {TimeText(en.StartMin + Math.Max(15, en.DurMin))}", sub,
                    new Rectangle(r.X + S(9), r.Y + S(4) + title.Height + S(2), r.Width - S(12), sub.Height), en.Done ? pal.Dim : Themes.Blend(pal.Main, pal.Fore, 0.7f),
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
    }

    // ───────── 월 보기의 오른쪽: 고른 날짜 + 그날 찍어 둔 스케줄 ─────────

    /// <summary>
    /// 월 보기에서 작은 달력 대신 들어가는 칸: 맨 위에 고른 날짜(오늘과 며칠 차이인지 · 날짜 · 요일), 그 밑에 그날 찍어 둔 스케줄이 시간 순서로 나타난다.
    /// 달력의 날짜를 누를 때마다 그 날짜로 바뀐다. 일정이 많으면 휠로 넘기고, 머리 부분 위로는 넘치지 않는다.
    /// </summary>
    private sealed class DayPanel : Control
    {
        private LibraryPalette? p;
        private DateTime day = DateTime.Today;
        private int scroll, hotRow = -1;
        private readonly List<(Rectangle R, ScheduleEntry E)> rows = new();

        /// <summary>일정을 눌렀다(보기 · 수정 창을 여는 쪽이 받는다).</summary>
        public event Action<ScheduleEntry, Point>? EntryClicked;

        /// <summary>맨 위 "오늘 · N일 후" 글자를 눌렀다(오늘로 돌아간다).</summary>
        public event Action? TodayClicked;
        private Rectangle RelRect() { var sz = TextRenderer.MeasureText(Relative(), Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding); return new Rectangle(S(14), S(6), sz.Width + S(28), S(28)); }

        public DayPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Height = 312;
        }

        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(312); }
        public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Side; Invalidate(); }

        /// <summary>보여 줄 날짜를 바꾼다(달력에서 날짜를 눌렀을 때).</summary>
        public void SetDay(DateTime d) { d = d.Date; if (d != day) scroll = 0; day = d; Invalidate(); }

        private List<ScheduleEntry> DayList()
        {
            string k = ScheduleStore.Key(day);
            return ScheduleStore.All.Where(e => ScheduleStore.Covers(e, k) && ScheduleStore.CalOf(e.Cal).Visible).OrderBy(e => e.StartMin < 0 ? -1 : e.StartMin).ThenBy(e => e.Text).ToList();
        }

        private static string TimeText(ScheduleEntry e)
        {
            if (e.StartMin < 0) return "시간 없이";
            int s = e.StartMin, f = Math.Min(24 * 60, s + Math.Max(15, e.DurMin));
            return $"{s / 60:00}:{s % 60:00} – {f / 60 % 24:00}:{f % 60:00}";
        }

        /// <summary>오늘과의 차이: 오늘 · 3일 후 · 2일 전.</summary>
        private string Relative()
        {
            int diff = (int)(day - DateTime.Today).TotalDays;
            return diff == 0 ? "오늘" : diff > 0 ? $"{diff}일 후" : $"{-diff}일 전";
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll = Math.Max(0, scroll - e.Delta / 120 * S(40));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = rows.FindIndex(r => r.R.Contains(e.Location));
            Cursor = h >= 0 || RelRect().Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            if (h != hotRow) { hotRow = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotRow = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && RelRect().Contains(e.Location)) { TodayClicked?.Invoke(); return; }
            if (e.Button != MouseButtons.Left || e.Y < S(86)) return;
            foreach (var (r, en) in rows)
                if (r.Contains(e.Location)) { EntryClicked?.Invoke(en, PointToScreen(new Point(r.Left, r.Bottom))); return; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (p == null) { g.Clear(SystemColors.Control); return; }
            var pal = p;
            g.Clear(pal.Side);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            rows.Clear();
            string ff = NovelStore.UiFontFamily;
            const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            Color dim = Themes.Blend(pal.Side, pal.Fore, 0.55f);
            int headH = S(86);

            // 1) 일정 줄을 먼저 그린다(글자는 영역 밖으로도 그려질 수 있어서, 아래에서 머리 영역을 다시 덮는다)
            var list = DayList();
            var area = new Rectangle(0, headH, Width, Height - headH);
            scroll = Math.Clamp(scroll, 0, Math.Max(0, list.Count * S(46) - area.Height + S(8)));
            if (list.Count == 0)
            {
                using var nf = new Font(ff, 9.5f);
                TextRenderer.DrawText(g, day == DateTime.Today ? "오늘 날짜에 찍어 둔 스케줄이 없어요" : "이 날짜에 찍어 둔 스케줄이 없어요", nf, new Rectangle(S(20), headH + S(16), Width - S(40), S(24)), dim, left);
            }
            else
            {
                using var tf = new Font(ff, 10.5f, FontStyle.Bold);
                using var tmf = new Font(ff, 8.5f);
                for (int i = 0; i < list.Count; i++)
                {
                    var en = list[i];
                    var r = new Rectangle(S(10), area.Top + i * S(46) - scroll, Width - S(20), S(42));
                    if (r.Bottom < area.Top || r.Top > area.Bottom) continue;
                    rows.Add((r, en));
                    if (hotRow >= 0 && rows.Count - 1 == hotRow) { using var hb = new SolidBrush(pal.CardHover); using var hp = LibraryPalette.Rounded(r, S(8)); g.FillPath(hb, hp); }
                    var col = ScheduleStore.CalOf(en.Cal).ColorValue;
                    using (var bar = new SolidBrush(en.Done ? Themes.Blend(pal.Side, col, 0.4f) : col))
                    using (var bp = LibraryPalette.Rounded(new Rectangle(r.X + S(8), r.Y + S(6), S(5), r.Height - S(12)), S(2))) g.FillPath(bar, bp);
                    using var tfont = new Font(tf, en.Done ? FontStyle.Bold | FontStyle.Strikeout : FontStyle.Bold);
                    TextRenderer.DrawText(g, en.Text, tfont, new Rectangle(r.X + S(22), r.Y + S(4), r.Width - S(30), S(20)), en.Done ? dim : pal.Fore, left);
                    TextRenderer.DrawText(g, TimeText(en), tmf, new Rectangle(r.X + S(22), r.Y + S(23), r.Width - S(30), S(16)), dim, left);
                }
            }

            // 2) 머리 영역을 바탕으로 덮고 다시 그린다: 위로 넘친 일정이 날짜 위에 겹쳐 보이지 않게
            using (var cover = new SolidBrush(pal.Side)) g.FillRectangle(cover, 0, 0, Width, headH);
            var ko = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
            using (var sf = new Font(ff, 9.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, Relative(), sf, new Rectangle(S(20), S(10), Width - S(40), S(22)), dim, left);
            string dateText = $"{day.Month}월 {day.Day}일";
            using (var bf = new Font(ff, 22f, FontStyle.Bold))
            using (var wf = new Font(ff, 11f))
            {
                var dsz = TextRenderer.MeasureText(g, dateText, bf, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, dateText, bf, new Rectangle(S(20), S(30), dsz.Width + S(4), S(40)), pal.Fore, left);
                // 요일은 날짜 오른쪽, 아래쪽(글자 바닥)에 맞춰 작게
                int wx = S(20) + dsz.Width + S(10);
                TextRenderer.DrawText(g, day.ToString("dddd", ko), wf, new Rectangle(wx, S(30) + S(40) - S(26), Math.Max(10, Width - wx - S(12)), S(22)), dim, left);
            }
            using (var lp = new Pen(pal.Line)) g.DrawLine(lp, S(16), S(78), Width - S(16), S(78));
        }
    }

    // ───────── 오른쪽 미니 달력 ─────────

    private sealed class MiniMonth : Control
    {
        private LibraryPalette? p;
        private DateTime month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        private DateTime selectedDay = DateTime.Today;
        private Rectangle upRect, downRect, titleRect;
        private readonly Rectangle[] cells = new Rectangle[42];
        private int rows = 6;
        private int hot = -1;

        public event Action<DateTime>? DateSelected;
        public event Action<DateTime>? MonthChanged;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DateTime Month { get => month; set { var m = new DateTime(value.Year, value.Month, 1); if (m == month) return; month = m; Invalidate(); } }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public DateTime Selected { get => selectedDay; set { selectedDay = value; Invalidate(); } }

        public MiniMonth()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            Height = 312;
        }

        public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Side; Invalidate(); }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = S(312); }
        private DateTime FirstCell => month.AddDays(-(int)month.DayOfWeek);

        private void LayoutCells()
        {
            rows = ((int)month.DayOfWeek + DateTime.DaysInMonth(month.Year, month.Month) + 6) / 7;
            int pad = S(14), top = S(96), cw = (Width - pad * 2) / 7, ch = S(36);
            for (int i = 0; i < 42; i++) cells[i] = i < rows * 7 ? new Rectangle(pad + (i % 7) * cw, top + (i / 7) * ch, cw, ch) : Rectangle.Empty;
            int bs = S(28);
            downRect = new Rectangle(Width - pad - bs, S(16), bs, bs);
            upRect = new Rectangle(downRect.X - bs, S(16), bs, bs);
            titleRect = new Rectangle(pad + S(4), S(16), Width - pad * 2 - bs * 2, bs);
        }

        private int HitAt(Point pt)
        {
            LayoutCells();
            if (upRect.Contains(pt)) return 0;
            if (downRect.Contains(pt)) return 1;
            for (int i = 0; i < rows * 7; i++) if (cells[i].Contains(pt)) return 100 + i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitAt(e.Location);
            if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitAt(e.Location);
            if (h == 0) { month = month.AddMonths(-1); MonthChanged?.Invoke(month); Invalidate(); }
            else if (h == 1) { month = month.AddMonths(1); MonthChanged?.Invoke(month); Invalidate(); }
            else if (h >= 100) { var d = FirstCell.AddDays(h - 100); selectedDay = d; DateSelected?.Invoke(d); Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); month = month.AddMonths(e.Delta > 0 ? -1 : 1); MonthChanged?.Invoke(month); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (p == null) { g.Clear(SystemColors.Control); return; }
            var pal = p;
            g.Clear(pal.Side);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            LayoutCells();
            string ff = NovelStore.UiFontFamily;
            const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            Color red = Color.FromArgb(0xE5, 0x48, 0x4D);

            using (var tf = new Font(ff, 11f, FontStyle.Bold))
                TextRenderer.DrawText(g, $"{month.Year}년 {month.Month}월", tf, titleRect, pal.Fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var af = new Font(MainForm.IconFontName(), 9f))
            {
                TextRenderer.DrawText(g, "", af, upRect, hot == 0 ? pal.Fore : pal.Dim, mid);
                TextRenderer.DrawText(g, "", af, downRect, hot == 1 ? pal.Fore : pal.Dim, mid);
            }
            string[] days = { "일", "월", "화", "수", "목", "금", "토" };
            using (var wf = new Font(ff, 8.5f))
                for (int c = 0; c < 7; c++)
                    TextRenderer.DrawText(g, days[c], wf, new Rectangle(cells[c].X, S(64), cells[c].Width, S(20)), Themes.Blend(pal.Side, pal.Fore, 0.5f), mid);

            // 고른 날이 있는 주(한 줄)를 둥근 띠로
            var first = FirstCell;
            for (int r = 0; r < rows; r++)
            {
                bool hit = false;
                for (int c = 0; c < 7; c++) if (first.AddDays(r * 7 + c).Date == selectedDay.Date) hit = true;
                if (!hit) continue;
                var band = Rectangle.FromLTRB(cells[r * 7].X, cells[r * 7].Y + S(3), cells[r * 7 + 6].Right, cells[r * 7].Bottom - S(3));
                using var path = LibraryPalette.Rounded(band, S(10));
                using var bb = new SolidBrush(Themes.Blend(pal.Side, pal.Fore, pal.Dark ? 0.14f : 0.09f));
                g.FillPath(bb, path);
            }

            using var df = new Font(ff, 9.5f);
            for (int i = 0; i < rows * 7; i++)
            {
                var d = first.AddDays(i);
                bool inMonth = d.Month == month.Month, isToday = d.Date == DateTime.Today;
                var r = cells[i];
                var circle = new Rectangle(r.X + (r.Width - S(28)) / 2, r.Y + (r.Height - S(28)) / 2, S(28), S(28));
                Color nc = inMonth ? pal.Fore : Themes.Blend(pal.Side, pal.Fore, 0.3f);
                if (isToday) { using var b = new SolidBrush(red); g.FillRoundedRect(circle, S(8)); nc = Color.White; }
                else if (hot == 100 + i) { using var b = new SolidBrush(pal.CardHover); using var hp = LibraryPalette.Rounded(circle, S(8)); g.FillPath(b, hp); }
                TextRenderer.DrawText(g, d.Day.ToString(), df, circle, nc, mid);
            }
        }
    }

    // ───────── 오른쪽 캘린더(색) 목록 ─────────

    private sealed class CalList : Control
    {
        private LibraryPalette? p;
        private int hot = -1;
        private string? activeId;
        public event Action? Changed;
        /// <summary>고른 스케줄(붓)이 바뀌었다.</summary>
        public event Action<ScheduleCal?>? ActiveChanged;

        /// <summary>지금 고른 스케줄: 시간표를 누르거나 끌면 이 색으로 칠해진다. 숨겨 둔 것은 못 고른다.</summary>
        public ScheduleCal? Active => ScheduleStore.Calendars.FirstOrDefault(c => c.Id == activeId && c.Visible);

        private void SetActive(ScheduleCal? c)
        {
            if (c?.Id == activeId) return;
            activeId = c?.Id;
            ActiveChanged?.Invoke(Active);
            Invalidate();
        }

        public void PickFirst() { if (Active == null) SetActive(ScheduleStore.Calendars.FirstOrDefault(c => c.IsDefault) ?? ScheduleStore.Calendars.FirstOrDefault()); }
        /// <summary>맨 위 [새 스케줄]을 고른다(월 보기에 들어가면 기본으로 이게 골라진다).</summary>
        /// <summary>留???[???ㅼ?以???怨좊Ⅸ????蹂닿린???ㅼ뼱媛硫?湲곕낯?쇰줈 ?닿쾶 怨⑤씪吏꾨떎).</summary>
        public void SelectDefault() => SetActive(ScheduleStore.Calendars.FirstOrDefault(c => c.IsDefault));

        public CalList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            InitReorder();
        }

        public void SetPalette(LibraryPalette pal) { p = pal; BackColor = pal.Side; Invalidate(); }
        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
        private int RowH => S(34);
        private int scroll;
        private const int VisibleRows = 4; // 내 스케줄은 4줄까지 보이고, 그 이상은 휠로 넘긴다
        private int ListH() => Math.Min(Count, VisibleRows) * RowH;
        private int ListTop() => S(46);
        private int ListBottom() => ListTop() + ListH();
        private Rectangle RowRect(int i) => new(S(8), ListTop() + i * RowH - scroll, Width - S(16), RowH - S(2));
        private int OtherTop() => ListBottom() + S(14);
        private Rectangle HolidayRect() => new(S(8), OtherTop() + S(28), Width - S(16), RowH - S(2));
        private int MaxScroll() => Math.Max(0, Count * RowH - ListH());

        /// <summary>아래에 붙어 있을 때(월 보기): 줄 수에 맞춰 높이를 정한다.</summary>
        public void Fit()
        {
            scroll = Math.Clamp(scroll, 0, MaxScroll());
            if (Dock != DockStyle.Bottom) return;
            int h = OtherTop() + S(28) + 2 * RowH + S(8); // 다른 캘린더는 두 줄 자리
            if (Height != h) Height = h;
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Y >= ListBottom()) return;
            int ns = Math.Clamp(scroll - e.Delta / 120 * RowH, 0, MaxScroll());
            if (ns != scroll) { scroll = ns; Invalidate(); }
        }
        private Rectangle SwatchRect(int i) { var r = RowRect(i); return new Rectangle(r.X + S(10), r.Y + (r.Height - S(22)) / 2, S(22), S(22)); }
        private int Count => ScheduleStore.Calendars.Count;
        private Rectangle PlusRect() => new(Width - S(8) - S(30), S(8), S(30), S(30)); // "내 스케줄" 줄 오른쪽 끝의 +

        private int HitAt(Point pt)
        {
            if (PlusRect().Contains(pt)) return 1000;
            if (pt.Y < S(44)) return -1;
            if (HolidayRect().Contains(pt)) return 2000;
            if (pt.Y >= ListBottom()) return -1;
            for (int i = 0; i < Count; i++) if (RowRect(i).Contains(pt)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragIdx >= 0) { dragY = e.Y; Invalidate(); return; }
            if (pressIdx >= 0 && (Math.Abs(e.X - pressPt.X) > S(6) || Math.Abs(e.Y - pressPt.Y) > S(6))) { holdTimer.Stop(); pressIdx = -1; }
            int h = HitAt(e.Location);
            if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (suppressClick) { suppressClick = false; return; }
            int h = HitAt(e.Location);
            if (h < 0) return;
            if (h == 1000) { AddCalendar(); return; } // 머리줄의 +
            if (h == 2000)
            {
                var hr = HolidayRect();
                if (e.Button == MouseButtons.Right || HolidayMoreRect().Contains(e.Location)) { HolidayMenu(PointToScreen(new Point(HolidayMoreRect().Left, HolidayMoreRect().Bottom))); return; }
                if (Rectangle.Inflate(new Rectangle(hr.X + S(10), hr.Y + (hr.Height - S(22)) / 2, S(22), S(22)), S(5), S(5)).Contains(e.Location)) { ScheduleStore.HolidaysVisible = !ScheduleStore.HolidaysVisible; Changed?.Invoke(); }
                return;
            }
            var cal = ScheduleStore.Calendars[h];
            if (e.Button == MouseButtons.Right) { if (!cal.IsDefault) CalMenu(cal, PointToScreen(e.Location)); return; }
            if (!cal.IsDefault && MoreRect(h).Contains(e.Location)) { CalMenu(cal, PointToScreen(new Point(MoreRect(h).Left, MoreRect(h).Bottom))); return; } // ⋯
            if (!cal.IsDefault && Rectangle.Inflate(SwatchRect(h), S(5), S(5)).Contains(e.Location))
            {
                cal.Visible = !cal.Visible; // 색 칸을 누르면 보이기(체크) / 안 보이기(빈 칸)
                if (!cal.Visible && cal.Id == activeId) SelectDefault();
                ScheduleStore.Save();
                Changed?.Invoke();
                return;
            }
            SetActive(cal); // 줄의 나머지를 누르면 그 스케줄(색)을 붓으로 고른다
        }

        private Rectangle HolidayMoreRect() { var r = HolidayRect(); return new Rectangle(r.Right - S(34), r.Y + (r.Height - S(26)) / 2, S(28), S(26)); }

        /// <summary>휴일 ⋯ 메뉴: 색을 직접 골라 바꾸거나 처음 색으로 되돌린다.</summary>
        private void HolidayMenu(Point screen)
        {
            var m = new ContextMenuStrip();
            MenuGuard.Attach(m);
            m.Items.Add("색 바꾸기...", null, (_, _) => BeginInvoke(() =>
            {
                using var dlg = new ColorDialog { Color = ScheduleStore.HolidayColor, FullOpen = true, AnyColor = true };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                ScheduleStore.HolidayColor = dlg.Color; Changed?.Invoke();
            }));
            m.Items.Add("처음 색으로", null, (_, _) => BeginInvoke(() => { ScheduleStore.ResetHolidayColor(); Changed?.Invoke(); }));
            m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
            m.Show(screen);
        }

        private void DrawCalRow(Graphics g, LibraryPalette pal, Font nf, ScheduleCal cal, Rectangle r, bool isHot, bool lifted)
        {
            if (lifted) { using var lb = new SolidBrush(pal.CardHover); using var lp = LibraryPalette.Rounded(r, S(8)); g.FillPath(lb, lp); using var lpn = new Pen(pal.Accent, S(1)); g.DrawPath(lpn, lp); }
            else if (cal.Id == activeId && cal.Visible) { using var ab = new SolidBrush(pal.AccentSoft); using var ap = LibraryPalette.Rounded(r, S(8)); g.FillPath(ab, ap); using var apn = new Pen(pal.Accent, S(1)); g.DrawPath(apn, ap); }
            else if (isHot) { using var hb = new SolidBrush(pal.CardHover); using var hp = LibraryPalette.Rounded(r, S(8)); g.FillPath(hb, hp); }
            var sw0 = new Rectangle(r.X + S(10), r.Y + (r.Height - S(22)) / 2, S(22), S(22));
            var col = cal.ColorValue;
            using var path = LibraryPalette.Rounded(sw0, S(8));
            if (cal.Visible) { using var b = new SolidBrush(col); g.FillPath(b, path); }
            else { using var pen = new Pen(col, S(2)); g.DrawPath(pen, path); } // 안 보이게 해 둔 스케줄: 빈 칸(테두리만)
            if (cal.IsDefault) { using var pl = new Pen(Color.White, S(2)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round }; float cx0 = sw0.X + sw0.Width / 2f, cy0 = sw0.Y + sw0.Height / 2f, ar = S(5); g.DrawLine(pl, cx0 - ar, cy0, cx0 + ar, cy0); g.DrawLine(pl, cx0, cy0 - ar, cx0, cy0 + ar); }
            else if (cal.Visible) // 보이는 스케줄: 색 칸 안에 체크 표시
            {
                using var ck = new Pen(Color.White, S(2) + 0.5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                g.DrawLines(ck, new[] { new PointF(sw0.X + sw0.Width * 0.25f, sw0.Y + sw0.Height * 0.52f), new PointF(sw0.X + sw0.Width * 0.44f, sw0.Y + sw0.Height * 0.70f), new PointF(sw0.X + sw0.Width * 0.76f, sw0.Y + sw0.Height * 0.32f) });
            }
            TextRenderer.DrawText(g, cal.Name, nf, new Rectangle(sw0.Right + S(12), r.Y, r.Width - sw0.Width - S(64), r.Height),
                cal.Visible ? pal.Fore : pal.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (cal.IsDefault) return;
            var more = new Rectangle(r.Right - S(34), r.Y + (r.Height - S(26)) / 2, S(28), S(26));
            DrawMore(g, more, Themes.Blend(pal.Side, pal.Fore, 0.6f), isHot && !lifted && PointToClient(Cursor.Position) is var cp && more.Contains(cp));
        }

        // ── 길게 눌러서(0.35초) 끌면 순서를 바꾼다. 맨 위 [새 스케줄]은 고정 ──
        private readonly System.Windows.Forms.Timer holdTimer = new() { Interval = 350 };
        private readonly System.Windows.Forms.Timer edgeTimer = new() { Interval = 50 };
        private int pressIdx = -1, dragIdx = -1, dragY;
        private Point pressPt;
        private bool suppressClick;

        private int DropIndex(int y) => Math.Clamp((y - ListTop() + scroll) / RowH, 1, Math.Max(1, Count - 1));

        private void InitReorder()
        {
            holdTimer.Tick += (_, _) =>
            {
                holdTimer.Stop();
                if (pressIdx < 1 || pressIdx >= Count) return;
                dragIdx = pressIdx; Capture = true; Cursor = Cursors.SizeAll; edgeTimer.Start(); Invalidate();
            };
            edgeTimer.Tick += (_, _) =>
            {   // 목록 위·아래 끝으로 끌면 목록이 넘어간다
                if (dragIdx < 0) { edgeTimer.Stop(); return; }
                int d = dragY < ListTop() + S(14) ? -S(8) : dragY > ListBottom() - S(14) ? S(8) : 0;
                if (d == 0) return;
                int ns = Math.Clamp(scroll + d, 0, MaxScroll());
                if (ns != scroll) { scroll = ns; Invalidate(); }
            };
            Disposed += (_, _) => { holdTimer.Dispose(); edgeTimer.Dispose(); };
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            suppressClick = false;
            if (e.Button != MouseButtons.Left) return;
            int h = HitAt(e.Location);
            if (h < 1 || h >= Count) { pressIdx = -1; return; }
            if (MoreRect(h).Contains(e.Location) || Rectangle.Inflate(SwatchRect(h), S(5), S(5)).Contains(e.Location)) { pressIdx = -1; return; }
            pressIdx = h; pressPt = e.Location; holdTimer.Start();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            holdTimer.Stop(); edgeTimer.Stop();
            if (dragIdx >= 0)
            {
                var l = ScheduleStore.Calendars;
                if (dragIdx < l.Count) { var c = l[dragIdx]; l.RemoveAt(dragIdx); l.Insert(Math.Clamp(DropIndex(e.Y), 1, l.Count), c); ScheduleStore.Save(); }
                dragIdx = -1; Capture = false; suppressClick = true; Cursor = Cursors.Default; Changed?.Invoke(); Invalidate();
            }
            pressIdx = -1;
        }

        private Rectangle MoreRect(int i) { var r = RowRect(i); return new Rectangle(r.Right - S(34), r.Y + (r.Height - S(26)) / 2, S(28), S(26)); }

        /// <summary>점 세 개(⋯): 글꼴 모양에 기대지 않고 직접 그려서 칸의 정중앙에 놓는다.</summary>
        private void DrawMore(Graphics g, Rectangle r, Color col, bool hover)
        {
            if (hover) { using var hb = new SolidBrush(Color.FromArgb(40, col)); using var hp = LibraryPalette.Rounded(r, S(6)); g.FillPath(hb, hp); }
            float rad = S(2) + 0.3f, gap = S(6), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            using var b = new SolidBrush(col);
            for (int k = -1; k <= 1; k++) g.FillEllipse(b, cx + k * gap - rad, cy - rad, rad * 2, rad * 2);
        }

        private void AddCalendar()
        {
            var free = ScheduleStore.FreeColors();
            if (free.Count == 0) { MessageBox.Show(FindForm(), $"스케줄 색 {SchedulePalette7.Colors.Length}가지를 모두 쓰고 있어요(색마다 스케줄 하나).\n다른 스케줄을 지우거나 색을 바꾼 뒤에 만들어 주세요.", "스케줄", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using var dlg = new ScheduleDefDialog(p!, "", free[0], "");
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            ScheduleStore.AddCal(dlg.DefName, dlg.DefColor, dlg.DefNote);
            SetActive(ScheduleStore.Calendars[^1]); // 만든 스케줄을 바로 붓으로 고른다
            Changed?.Invoke();
        }

        private void EditCalendar(ScheduleCal cal)
        {
            using var dlg = new ScheduleDefDialog(p!, cal.Name, cal.ColorValue, cal.Note, cal.Id);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            cal.Name = dlg.DefName;
            cal.Color = ColorTranslator.ToHtml(dlg.DefColor);
            cal.Note = dlg.DefNote;
            ScheduleStore.Save();
            Changed?.Invoke();
        }

        /// <summary>⋯ 메뉴: 수정하기 / 삭제.</summary>
        private void CalMenu(ScheduleCal cal, Point screen)
        {
            var m = new ContextMenuStrip();
            MenuGuard.Attach(m);
            m.Items.Add("수정하기", null, (_, _) => BeginInvoke(() => EditCalendar(cal)));
            var del = new ToolStripMenuItem("삭제") { Enabled = !cal.IsDefault };
            del.Click += (_, _) => BeginInvoke(() =>
            {
                if (MessageBox.Show(FindForm(), $"'{cal.Name}' 스케줄과 그 일정을 모두 지울까요?\n되돌릴 수 없어요.", "스케줄 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                if (cal.Id == activeId) SetActive(ScheduleStore.Calendars.FirstOrDefault(c => c.Id != cal.Id));
                ScheduleStore.RemoveCal(cal); Changed?.Invoke();
            });
            m.Items.Add(del);
            m.Closed += (_, _) => BeginInvoke(() => m.Dispose());
            m.Show(screen);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (p == null) { g.Clear(SystemColors.Control); return; }
            var pal = p;
            g.Clear(pal.Side);
            using (var sep = new Pen(pal.Line)) g.DrawLine(sep, 0, 0, Width, 0); // 위 달력과 나누는 선
            using (var hf = new Font(NovelStore.UiFontFamily, 9f, FontStyle.Bold))
                TextRenderer.DrawText(g, "내 스케줄", hf, new Rectangle(S(18), S(12), Width - S(36), S(24)), Themes.Blend(pal.Side, pal.Fore, 0.5f), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            string ff = NovelStore.UiFontFamily;
            using var nf = new Font(ff, 10.5f);
            var cals = ScheduleStore.Calendars.ToList();
            ScheduleCal? lifted = null;
            if (dragIdx >= 0 && dragIdx < cals.Count) { lifted = cals[dragIdx]; cals.RemoveAt(dragIdx); cals.Insert(DropIndex(dragY), lifted); }
            for (int i = 0; i < cals.Count; i++)
                if (cals[i] != lifted) DrawCalRow(g, pal, nf, cals[i], RowRect(i), hot == i, false);
            if (lifted != null)
            {   // 끌고 있는 줄: 손가락(마우스)을 따라 떠다닌다
                var lr = RowRect(0); lr.Y = Math.Clamp(dragY - RowH / 2, ListTop(), Math.Max(ListTop(), ListBottom() - RowH));
                DrawCalRow(g, pal, nf, lifted, lr, false, true);
            }
            // 목록 칸(4줄) 밖으로 밀려난 줄을 덮는다
            using (var cov = new SolidBrush(pal.Side)) g.FillRectangle(cov, 0, ListBottom(), Width, Height - ListBottom());
            // ── 다른 캘린더: 선 + 제목 + 대한민국 휴일(빨강) ──
            var holiday = HolidayRect();
            using (var sp = new Pen(pal.Line)) g.DrawLine(sp, S(10), OtherTop() - S(6), Width - S(10), OtherTop() - S(6));
            using (var hf2 = new Font(ff, 9f, FontStyle.Bold))
                TextRenderer.DrawText(g, "다른 캘린더", hf2, new Rectangle(S(18), OtherTop(), Width - S(36), S(24)), Themes.Blend(pal.Side, pal.Fore, 0.5f), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            {
                if (hot == 2000) { using var hb = new SolidBrush(pal.CardHover); using var hp = LibraryPalette.Rounded(holiday, S(8)); g.FillPath(hb, hp); }
                var hsw = new Rectangle(holiday.X + S(10), holiday.Y + (holiday.Height - S(22)) / 2, S(22), S(22));
                var red = ScheduleStore.HolidayColor;
                using var hpath = LibraryPalette.Rounded(hsw, S(8));
                bool on = ScheduleStore.HolidaysVisible;
                if (on) { using var hbr = new SolidBrush(red); g.FillPath(hbr, hpath); }
                else { using var hpen = new Pen(red, S(2)); g.DrawPath(hpen, hpath); }
                if (on)
                {
                    using var ck = new Pen(Color.White, S(2) + 0.5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                    g.DrawLines(ck, new[] { new PointF(hsw.X + hsw.Width * 0.25f, hsw.Y + hsw.Height * 0.52f), new PointF(hsw.X + hsw.Width * 0.44f, hsw.Y + hsw.Height * 0.70f), new PointF(hsw.X + hsw.Width * 0.76f, hsw.Y + hsw.Height * 0.32f) });
                }
                TextRenderer.DrawText(g, "대한민국 휴일", nf, new Rectangle(hsw.Right + S(12), holiday.Y, holiday.Width - hsw.Width - S(30), holiday.Height), on ? pal.Fore : pal.Dim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                var hm = HolidayMoreRect();
                DrawMore(g, hm, Themes.Blend(pal.Side, pal.Fore, 0.6f), hot == 2000 && PointToClient(Cursor.Position) is var hcp && hm.Contains(hcp));
            }
            // 스크롤로 위로 밀려 올라간 줄이 머리(내 스케줄 · +) 위에 겹치지 않게 머리 영역을 덮고 다시 그린다
            using (var cover = new SolidBrush(pal.Side)) g.FillRectangle(cover, 0, 0, Width, S(44));
            using (var sep2 = new Pen(pal.Line)) g.DrawLine(sep2, 0, 0, Width, 0);
            using (var hf = new Font(ff, 9f, FontStyle.Bold))
                TextRenderer.DrawText(g, "내 스케줄", hf, new Rectangle(S(18), S(12), Width - S(36), S(24)), Themes.Blend(pal.Side, pal.Fore, 0.5f), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var pr = PlusRect();
            if (hot == 1000) { using var hb = new SolidBrush(pal.CardHover); using var hp = LibraryPalette.Rounded(pr, S(8)); g.FillPath(hb, hp); }
            using (var pp = new Pen(hot == 1000 ? pal.Fore : Themes.Blend(pal.Side, pal.Fore, 0.6f), S(1) + 0.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float pcx = pr.X + pr.Width / 2f, pcy = pr.Y + pr.Height / 2f, arm = S(6);
                g.DrawLine(pp, pcx - arm, pcy, pcx + arm, pcy);
                g.DrawLine(pp, pcx, pcy - arm, pcx, pcy + arm);
            }
        }
    }
}

/// <summary>
/// 스케줄 추가 · 보기 창(작은 팝업): [스케줄 ········ ×] / [색 네모][제목] / ─ / [시계] 시작 → 끝 (길이) / 날짜 / ─ / [핀] 위치 / ─ / 설명.
/// 새로 만들 때와 기존 일정을 눌렀을 때 같은 모양으로 뜬다(기존 일정이면 아래에 완료 체크와 삭제가 붙는다).
/// 빈 곳을 끌면 창을 옮길 수 있다. 모든 위치는 화면 배율(DeviceDpi)에 맞춰 직접 계산한다(창이 뜬 뒤 배율이 정해져도 어긋나지 않게 핸들이 만들어질 때 배치).
/// </summary>
internal sealed class ScheduleEntryDialog : Form
{
    private readonly TextBox input, place, note;
    private readonly DateTimePicker datePick, startPick, endPick;
    private readonly CheckBox allDay, doneBox;
    private readonly Label durLabel;
    private readonly Button close, ok, del;
    private readonly LibraryPalette p;
    private readonly bool editing;
    private string calId;
    private Rectangle swatchRect;

    public string EntryText => input.Text;
    public string CalId => calId;
    public Color? NewColor { get; private set; }
    public DateTime EntryDate => datePick.Value.Date;
    /// <summary>시작 시각(0시부터 분). 시간 없이면 -1.</summary>
    public int StartMin => allDay.Checked ? -1 : startPick.Value.Hour * 60 + startPick.Value.Minute;
    public int DurMin => allDay.Checked ? 0 : Math.Max(15, endPick.Value.Hour * 60 + endPick.Value.Minute - StartMin);
    public string Place => place.Text.Trim();
    public string Note => note.Text.Trim();
    public bool Done => doneBox.Checked;
    public bool Deleted { get; private set; }

    private const int W = 344;                 // 96dpi 기준 폭
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public ScheduleEntryDialog(LibraryPalette pal, string text, string cal, DateTime date, int startMin, int endMin, string placeText = "", string noteText = "", bool done = false, bool editing = false)
    {
        p = pal;
        calId = cal;
        this.editing = editing;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font(NovelStore.UiFontFamily, 9.5f);
        Color bg = p.Dark ? p.Main : Color.White;
        BackColor = bg; ForeColor = p.Fore;
        Color field = p.Dark ? Themes.Blend(p.Main, Color.White, 0.07f) : Color.FromArgb(0xF1, 0xF0, 0xEE);
        Paint += (_, e) => PaintAll(e.Graphics);
        MouseClick += (_, e) => { if (swatchRect.Contains(e.Location)) PickCalendar(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.Handled = true; } };

        close = new Button { Text = "", FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = p.Fore, TabStop = false, Font = new Font(MainForm.IconFontName(), 11f) };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;

        input = new TextBox { Text = text, BackColor = field, ForeColor = p.Fore, BorderStyle = BorderStyle.FixedSingle, Font = new Font(NovelStore.UiFontFamily, 11f), PlaceholderText = "제목" };

        int s0 = startMin < 0 ? 9 * 60 : startMin, e0 = startMin < 0 || endMin <= s0 ? s0 + 60 : Math.Min(endMin, 24 * 60 - 1);
        DateTime Base(int min) => new(2000, 1, 1, Math.Clamp(min / 60, 0, 23), Math.Clamp(min % 60, 0, 59), 0);
        startPick = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = Base(s0) };
        endPick = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Value = Base(e0) };
        durLabel = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, ForeColor = p.Dim };
        int len = e0 - s0;
        void ShowLen() { int d = Math.Max(15, endPick.Value.Hour * 60 + endPick.Value.Minute - (startPick.Value.Hour * 60 + startPick.Value.Minute)); durLabel.Text = d % 60 == 0 ? $"{d / 60}시간" : d < 60 ? $"{d}분" : $"{d / 60}시간 {d % 60}분"; }
        startPick.ValueChanged += (_, _) => { int ns = startPick.Value.Hour * 60 + startPick.Value.Minute; endPick.Value = Base(Math.Min(ns + Math.Max(15, len), 24 * 60 - 1)); ShowLen(); };
        endPick.ValueChanged += (_, _) => { len = Math.Max(15, endPick.Value.Hour * 60 + endPick.Value.Minute - (startPick.Value.Hour * 60 + startPick.Value.Minute)); ShowLen(); };
        ShowLen();

        datePick = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "M월 d일 (ddd)", Value = date.Date };
        allDay = new CheckBox { Text = "시간 없이", ForeColor = p.Fore, Checked = startMin < 0, AutoSize = false };
        allDay.CheckedChanged += (_, _) => { startPick.Enabled = endPick.Enabled = !allDay.Checked; };
        startPick.Enabled = endPick.Enabled = !allDay.Checked;

        place = new TextBox { Text = placeText, BorderStyle = BorderStyle.None, BackColor = bg, ForeColor = p.Fore, PlaceholderText = "위치" };
        note = new TextBox { Multiline = true, Text = noteText, BorderStyle = BorderStyle.None, BackColor = field, ForeColor = p.Fore, PlaceholderText = "스케줄에 대한 메모", ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };

        doneBox = new CheckBox { Text = "", ForeColor = p.Fore, Checked = done, Visible = editing, AutoSize = false };
        ok = new Button { Text = "저장", FlatStyle = FlatStyle.Flat, BackColor = p.Accent, ForeColor = p.AccentFore };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) => { if (input.Text.Trim().Length == 0) { input.Focus(); return; } DialogResult = DialogResult.OK; };
        del = new Button { Text = "삭제", FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = Color.FromArgb(0xE0, 0x2B, 0x2B), Visible = editing };
        del.FlatAppearance.BorderColor = p.Line;
        del.Click += (_, _) => { Deleted = true; DialogResult = DialogResult.OK; };

        Controls.AddRange(new Control[] { close, input, startPick, endPick, durLabel, datePick, allDay, place, note, doneBox, ok, del });
        AcceptButton = ok;
        Load += (_, _) => Relayout(); Shown += (_, _) => { Relayout(); input.Focus(); input.SelectAll(); };
        Relayout();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Relayout(); }

    /// <summary>모든 칸의 자리와 크기를 지금 배율로 다시 정한다.</summary>
    private void Relayout()
    {
        Size = new Size(S(W), S(editing ? 416 : 380));
        close.SetBounds(S(W) - S(44), S(8), S(34), S(34));
        int ix = editing ? 90 : 58; // 완료 체크가 색 네모 왼쪽에 들어가는 만큼(기존 일정일 때)
        input.SetBounds(S(ix), S(56), S(W) - S(ix) - S(16), S(32));
        int row = S(112), h = S(30);
        startPick.SetBounds(S(58), row, S(90), h);
        endPick.SetBounds(S(176), row, S(90), h);
        durLabel.SetBounds(S(274), row, S(W) - S(274) - S(8), h);
        datePick.SetBounds(S(58), S(150), S(170), h);
        allDay.SetBounds(S(236), S(150), S(W) - S(236) - S(8), h);
        place.SetBounds(S(58), S(204), S(W) - S(74), S(24));
        note.SetBounds(S(16), S(270), S(W) - S(32), S(96));
        int by = S(382);
        doneBox.SetBounds(S(14), S(58), S(28), S(28)); // 완료 체크: 색 네모 왼쪽
        ok.SetBounds(S(W) - S(16) - S(90), by, S(90), S(32));
        del.SetBounds(S(16), by, S(90), S(32));
        Size = new Size(S(W), ok.Bottom + S(14)); // 삭제 · 저장 아래 여백은 14만
        Invalidate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    /// <summary>창의 빈 곳(제목 줄 · 여백)을 마우스로 끌면 창이 따라 움직인다.</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || swatchRect.Contains(e.Location)) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
    }

    private void PickCalendar()
    {
        var pk = new ScheduleColorPicker(p, true, NewColor == null ? calId : null, NewColor);
        pk.ExistingChosen += c => { calId = c.Id; NewColor = null; Invalidate(); };
        pk.ColorChosen += col =>
        {
            var same = ScheduleStore.Calendars.FirstOrDefault(c => !c.IsDefault && SchedulePalette7.Same(c.ColorValue, col));
            if (same != null) { calId = same.Id; NewColor = null; }
            else { calId = ScheduleStore.Calendars.First(c => c.IsDefault).Id; NewColor = col; }
            Invalidate();
        };
        ScheduleColorPicker.Show(this, PointToScreen(new Point(swatchRect.Left, swatchRect.Bottom + S(4))), pk);
    }
    private void PaintAll(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(p.Line)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using (var tf = new Font(NovelStore.UiFontFamily, 15f, FontStyle.Bold))
            TextRenderer.DrawText(g, "스케줄", tf, new Rectangle(S(16), S(8), S(200), S(34)), p.Fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        swatchRect = new Rectangle(S(editing ? 46 : 16), S(56), S(34), S(32));
        using (var path = LibraryPalette.Rounded(swatchRect, S(8)))
        using (var b = new SolidBrush(NewColor ?? ScheduleStore.CalOf(calId).ColorValue))
            g.FillPath(b, path);
        using var line = new Pen(p.Line);
        g.DrawLine(line, 0, S(100), Width, S(100));
        g.DrawLine(line, 0, S(192), Width, S(192));
        g.DrawLine(line, 0, S(244), Width, S(244));
        using var icon = new Font(MainForm.IconFontName(), 11f);
        const TextFormatFlags mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, "", icon, new Rectangle(S(16), S(112), S(34), S(30)), p.Dim, mid);
        TextRenderer.DrawText(g, "", icon, new Rectangle(S(148), S(112), S(28), S(30)), p.Dim, mid);
        TextRenderer.DrawText(g, "", icon, new Rectangle(S(16), S(202), S(34), S(28)), p.Dim, mid);
        TextRenderer.DrawText(g, "설명", Font, new Rectangle(S(16), S(250), S(100), S(18)), p.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// 새 스케줄(색 묶음) 만들기 · 수정 창: [스케줄 ········ ×] / [색 네모][이름] / ─ / 설명. 시간 · 위치 · 완료는 없다.
/// 색 네모를 누르면 색을 고르고, 빈 곳을 끌면 창을 옮길 수 있다.
/// </summary>
internal sealed class ScheduleDefDialog : Form
{
    private readonly TextBox input, note;
    private readonly Button close, ok;
    private readonly LibraryPalette p;
    private Color color;
    private Rectangle swatchRect;

    public string DefName => input.Text.Trim();
    public Color DefColor => color;
    public string DefNote => note.Text.Trim();

    private const int W = 336;
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    private readonly string? ownId;
    public ScheduleDefDialog(LibraryPalette pal, string name, Color col, string noteText, string? ownId = null)
    {
        p = pal;
        color = col;
        this.ownId = ownId;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font(NovelStore.UiFontFamily, 9.5f);
        Color bg = p.Dark ? p.Main : Color.White;
        BackColor = bg; ForeColor = p.Fore;
        Color field = p.Dark ? Themes.Blend(p.Main, Color.White, 0.07f) : Color.FromArgb(0xF1, 0xF0, 0xEE);
        Paint += (_, e) => PaintAll(e.Graphics);
        MouseClick += (_, e) => { if (swatchRect.Contains(e.Location)) PickColor(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; e.Handled = true; } };

        close = new Button { Text = "", FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = p.Fore, TabStop = false, Font = new Font(MainForm.IconFontName(), 11f) };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;
        input = new TextBox { Text = name, BackColor = field, ForeColor = p.Fore, BorderStyle = BorderStyle.FixedSingle, Font = new Font(NovelStore.UiFontFamily, 11f), PlaceholderText = "스케줄 이름" };
        note = new TextBox { Multiline = true, Text = noteText, BorderStyle = BorderStyle.None, BackColor = bg, ForeColor = p.Fore, PlaceholderText = "스케줄에 대한 메모", ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
        ok = new Button { Text = "저장", FlatStyle = FlatStyle.Flat, BackColor = p.Accent, ForeColor = p.AccentFore };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) => { if (input.Text.Trim().Length == 0) { input.Focus(); return; } DialogResult = DialogResult.OK; };
        Controls.AddRange(new Control[] { close, input, note, ok });
        AcceptButton = ok;
        Load += (_, _) => Relayout(); Shown += (_, _) => { Relayout(); input.Focus(); input.SelectAll(); };
        Relayout();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Relayout(); }

    private void Relayout()
    {
        Size = new Size(S(W), S(280));
        close.SetBounds(S(W) - S(44), S(8), S(34), S(34));
        input.SetBounds(S(58), S(56), S(W) - S(74), S(32));
        note.SetBounds(S(16), S(132), S(W) - S(32), S(90));
        ok.SetBounds(S(W) - S(16) - S(90), S(280) - S(14) - S(32), S(90), S(32));
        Invalidate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || swatchRect.Contains(e.Location)) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
    }

    private void PickColor()
    {
        var pk = new ScheduleColorPicker(p, false, null, color, ownId);
        pk.ColorChosen += c => { color = c; Invalidate(); };
        ScheduleColorPicker.Show(this, PointToScreen(new Point(swatchRect.Left, swatchRect.Bottom + S(4))), pk);
    }
    private void PaintAll(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(p.Line)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using (var tf = new Font(NovelStore.UiFontFamily, 15f, FontStyle.Bold))
            TextRenderer.DrawText(g, "스케줄", tf, new Rectangle(S(16), S(8), S(200), S(34)), p.Fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        swatchRect = new Rectangle(S(16), S(56), S(34), S(32));
        using (var path = LibraryPalette.Rounded(swatchRect, S(8)))
        using (var b = new SolidBrush(color))
            g.FillPath(b, path);
        using var line = new Pen(p.Line);
        g.DrawLine(line, 0, S(100), Width, S(100));
        TextRenderer.DrawText(g, "설명", Font, new Rectangle(S(16), S(108), S(100), S(18)), p.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>새 스케줄에 쓸 수 있는 색 8가지(스케줄 만들기 · 수정 · 일정 창 모두 같다).</summary>
internal static class SchedulePalette7
{
    public static readonly Color[] Colors =
    {
        Color.FromArgb(0xE5, 0x48, 0x4D), // 빨강
        Color.FromArgb(0xF2, 0x99, 0x4A), // 주황
        Color.FromArgb(0xE5, 0xB8, 0x00), // 노랑
        Color.FromArgb(0x3F, 0xAE, 0x5A), // 초록
        Color.FromArgb(0x4A, 0xA3, 0xDF), // 파랑
        Color.FromArgb(0x8E, 0x5B, 0xD6), // 보라
        Color.FromArgb(0xE8, 0x6A, 0xA6), // 분홍
        Color.FromArgb(0x2B, 0x25, 0x70), // 남색
    };

    /// <summary>같은 색인가(RGB만 본다).</summary>
    public static bool Same(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;
}

/// <summary>
/// 색 네모를 누르면 뜨는 작은 선택 창: 위에는 지금 있는 스케줄(누르면 그 스케줄로 정해진다), 아래에는 새 색 8가지(고르면 새 스케줄 색).
/// listExisting이 false면 색 8가지만 보인다(스케줄 만들기 · 수정 창).
/// </summary>
internal sealed class ScheduleColorPicker : Control
{
    private readonly LibraryPalette p;
    private readonly bool listExisting;
    private readonly List<ScheduleCal> cals;
    private readonly Color? current;
    private readonly string? currentId;
    private readonly string? ignoreId;
    private readonly List<(Rectangle R, ScheduleCal C)> rowRects = new();
    private readonly List<(Rectangle R, Color C)> dotRects = new();
    private int hot = -1;

    public event Action<ScheduleCal>? ExistingChosen;
    public event Action<Color>? ColorChosen;

    public ScheduleColorPicker(LibraryPalette pal, bool listExisting, string? currentId, Color? current, string? ignoreId = null)
    {
        p = pal;
        this.listExisting = listExisting;
        this.currentId = currentId;
        this.current = current;
        this.ignoreId = ignoreId;
        cals = listExisting ? ScheduleStore.Calendars.Where(c => !c.IsDefault).ToList() : new();
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
        int h = S(8) + S(30) + S(24) + S(16);
        if (listExisting && cals.Count > 0) h += S(26) + cals.Count * S(32) + S(10);
        Size = new Size(S(236), h);
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    private int ExistingBlockH => listExisting && cals.Count > 0 ? S(34 + cals.Count * 32 + 10) : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = rowRects.FindIndex(r => r.R.Contains(e.Location));
        if (h < 0) { int d = dotRects.FindIndex(r => r.R.Contains(e.Location)); h = d >= 0 && ScheduleStore.FreeColors(ignoreId).Any(f => SchedulePalette7.Same(f, dotRects[d].C)) ? 100 + d : -1; }
        if (h != hot) { hot = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach (var (r, c) in rowRects) if (r.Contains(e.Location)) { ExistingChosen?.Invoke(c); return; }
        foreach (var (r, c) in dotRects) if (r.Contains(e.Location)) { if (ScheduleStore.FreeColors(ignoreId).Any(f => SchedulePalette7.Same(f, c))) ColorChosen?.Invoke(c); return; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(p.Dark ? p.Main : Color.White);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        rowRects.Clear(); dotRects.Clear();
        string ff = NovelStore.UiFontFamily;
        const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        Color dim = Themes.Blend(p.Main, p.Fore, 0.5f);
        using var hf = new Font(ff, 9f, FontStyle.Bold);
        using var nf = new Font(ff, 10f);
        int y = S(8);
        if (listExisting && cals.Count > 0)
        {
            TextRenderer.DrawText(g, "지금 있는 스케줄", hf, new Rectangle(S(14), y, Width - S(28), S(22)), dim, left);
            y += S(26);
            for (int i = 0; i < cals.Count; i++)
            {
                var r = new Rectangle(S(6), y, Width - S(12), S(30));
                rowRects.Add((r, cals[i]));
                if (hot == i) { using var hb = new SolidBrush(p.CardHover); using var hp = LibraryPalette.Rounded(r, S(7)); g.FillPath(hb, hp); }
                using (var b = new SolidBrush(cals[i].ColorValue)) using (var bp = LibraryPalette.Rounded(new Rectangle(r.X + S(8), r.Y + S(5), S(20), S(20)), S(6))) g.FillPath(b, bp);
                TextRenderer.DrawText(g, cals[i].Name, nf, new Rectangle(r.X + S(38), r.Y, r.Width - S(46), r.Height), p.Fore, left);
                if (cals[i].Id == currentId) TextRenderer.DrawText(g, "✓", nf, new Rectangle(r.Right - S(28), r.Y, S(22), r.Height), p.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                y += S(32);
            }
            using (var lp = new Pen(p.Line)) g.DrawLine(lp, S(10), y + S(2), Width - S(10), y + S(2));
            y += S(10);
        }
        TextRenderer.DrawText(g, listExisting ? "새 색 (8가지)" : "색 (8가지)", hf, new Rectangle(S(14), y, Width - S(28), S(22)), dim, left);
        y += S(30);
        int sz = S(22), n = SchedulePalette7.Colors.Length, gap = (Width - S(28) - sz * n) / (n - 1);
        for (int i = 0; i < n; i++)
        {
            var r = new Rectangle(S(14) + i * (sz + gap), y, sz, sz);
            dotRects.Add((r, SchedulePalette7.Colors[i]));
            bool free = ScheduleStore.FreeColors(ignoreId).Any(c => SchedulePalette7.Same(c, SchedulePalette7.Colors[i]));
            // 내 스케줄의 색 칸처럼 둥근 네모. 이미 쓰는 색은 빈 칸(테두리만)이고 눌리지 않는다.
            using (var sp = LibraryPalette.Rounded(r, S(6)))
            {
                if (free) { using var b = new SolidBrush(SchedulePalette7.Colors[i]); g.FillPath(b, sp); }
                else { using var op = new Pen(SchedulePalette7.Colors[i], S(2)); g.DrawPath(op, sp); }
            }
            if (free && (hot == 100 + i || (current is Color cc && SchedulePalette7.Same(cc, SchedulePalette7.Colors[i]))))
            {
                using var ring = new Pen(p.Fore, S(2));
                using var rp = LibraryPalette.Rounded(Rectangle.Inflate(r, S(3), S(3)), S(8));
                g.DrawPath(ring, rp);
            }
        }
        using (var border = new Pen(p.Line)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>색 네모 바로 밑에 이 선택 창을 띄운다(밖을 누르면 닫힌다).</summary>
    public static void Show(Control owner, Point screen, ScheduleColorPicker picker)
    {
        var dd = new ToolStripDropDown { Padding = Padding.Empty, AutoSize = true, BackColor = picker.BackColor };
        MenuGuard.Attach(dd);
        dd.Items.Add(new ToolStripControlHost(picker) { Margin = Padding.Empty, Padding = Padding.Empty });
        picker.ExistingChosen += _ => dd.Close();
        picker.ColorChosen += _ => dd.Close();
        dd.Closed += (_, _) => owner.BeginInvoke(() => dd.Dispose());
        dd.Show(screen);
    }
}

/// <summary>
/// 대한민국 공휴일(법정 공휴일 + 대체공휴일)을 앱 안에서 계산한다(인터넷 없이).
/// 양력 고정일 + 음력(설날 · 부처님오신날 · 추석, .NET의 KoreanLunisolarCalendar 1901~2050년) + 대체공휴일 규칙.
/// 임시공휴일 · 선거일처럼 그해에 따로 정해지는 날은 알 수 없어 들어 있지 않다.
/// </summary>
internal static class KoreanHolidays
{
    private static readonly Dictionary<int, Dictionary<DateTime, string>> cache = new();

    /// <summary>그날이 공휴일이면 이름(없으면 null).</summary>
    public static string? NameOn(DateTime d)
    {
        var year = Year(d.Year);
        return year.TryGetValue(d.Date, out var n) ? n : null;
    }

    private static Dictionary<DateTime, string> Year(int y)
    {
        lock (cache)
        {
            if (cache.TryGetValue(y, out var c)) return c;
            var map = Build(y);
            cache[y] = map;
            return map;
        }
    }

    private static DateTime? Lunar(int year, int month, int day)
    {
        try
        {
            var kl = new System.Globalization.KoreanLunisolarCalendar();
            if (year < kl.MinSupportedDateTime.Year || year > kl.MaxSupportedDateTime.Year - 1) return null;
            int leap = kl.GetLeapMonth(year);
            int idx = leap > 0 && month >= leap ? month + 1 : month; // 윤달이 앞에 끼면 달 번호가 하나 밀린다
            return kl.ToDateTime(year, idx, day, 0, 0, 0, 0).Date;
        }
        catch { return null; }
    }

    private static Dictionary<DateTime, string> Build(int y)
    {
        var map = new Dictionary<DateTime, string>();
        void Add(DateTime d, string name) { if (!map.ContainsKey(d.Date)) map[d.Date] = name; }

        var fixedDays = new (DateTime D, string Name)[]
        {
            (new DateTime(y, 1, 1), "신정"), (new DateTime(y, 3, 1), "삼일절"), (new DateTime(y, 5, 5), "어린이날"), (new DateTime(y, 6, 6), "현충일"),
            (new DateTime(y, 8, 15), "광복절"), (new DateTime(y, 10, 3), "개천절"), (new DateTime(y, 10, 9), "한글날"), (new DateTime(y, 12, 25), "성탄절"),
        };
        foreach (var (d, n) in fixedDays) Add(d, n);

        var groups = new List<List<DateTime>>(); // 연휴(설날 · 추석)
        if (Lunar(y, 1, 1) is DateTime seol)
        {
            Add(seol.AddDays(-1), "설날 연휴"); Add(seol, "설날"); Add(seol.AddDays(1), "설날 연휴");
            groups.Add(new() { seol.AddDays(-1), seol, seol.AddDays(1) });
        }
        if (Lunar(y, 8, 15) is DateTime chu)
        {
            Add(chu.AddDays(-1), "추석 연휴"); Add(chu, "추석"); Add(chu.AddDays(1), "추석 연휴");
            groups.Add(new() { chu.AddDays(-1), chu, chu.AddDays(1) });
        }
        DateTime? buddha = Lunar(y, 4, 8);
        if (buddha is DateTime bd) Add(bd, "부처님오신날");

        // 대체공휴일: 겹치는 날 다음의 첫 평일(공휴일 · 주말이 아닌 날)
        bool Taken(DateTime d) => map.ContainsKey(d.Date) || d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        void Substitute(DateTime after)
        {
            var d = after.AddDays(1);
            while (Taken(d)) d = d.AddDays(1);
            Add(d, "대체휴일");
        }
        // 설날 · 추석 연휴: 일요일이 끼면(2014년부터)
        if (y >= 2014)
            foreach (var g in groups)
                if (g.Any(x => x.DayOfWeek == DayOfWeek.Sunday)) Substitute(g.Max());
        // 어린이날: 2014년부터 일요일(2021년부터 토요일)과 겹치면
        var kids = new DateTime(y, 5, 5);
        if (y >= 2014 && (kids.DayOfWeek == DayOfWeek.Sunday || (y >= 2021 && kids.DayOfWeek == DayOfWeek.Saturday))) Substitute(kids);
        // 삼일절 · 광복절 · 개천절 · 한글날: 2021년부터 주말과 겹치면(2023년부터 부처님오신날 · 성탄절도)
        if (y >= 2021)
        {
            var nat = new List<DateTime> { new DateTime(y, 3, 1), new DateTime(y, 8, 15), new DateTime(y, 10, 3), new DateTime(y, 10, 9) };
            if (y >= 2023) { nat.Add(new DateTime(y, 12, 25)); if (buddha is DateTime b2) nat.Add(b2); }
            foreach (var d in nat) if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) Substitute(d);
        }
        return map;
    }
}

internal static class GraphicsRoundedExt
{
    /// <summary>둥근 네모 채우기(지금 브러시 없이 오늘 표시용으로 쓰는 간단한 도우미).</summary>
    public static void FillRoundedRect(this Graphics g, Rectangle r, int radius)
    {
        using var path = LibraryPalette.Rounded(r, radius);
        using var b = new SolidBrush(Color.FromArgb(0xE5, 0x48, 0x4D));
        g.FillPath(b, path);
    }
}
