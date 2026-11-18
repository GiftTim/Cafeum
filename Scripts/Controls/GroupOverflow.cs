namespace Cafeum;

/// <summary>
/// 서식 줄(ToolStrip)이 좁아졌을 때의 넘침 처리. 기본 동작은 오른쪽 끝부터 항목을 하나씩 잘라 »(넘침) 버튼 안에 세로로 늘어놓는다.
/// 여기서는 구분선(|) 사이를 한 묶음으로 보고:
/// - 묶음이 통째로 들어갈 때만 줄에 둔다. 일부만 들어가면 그 묶음은 전부 가린다(뒤의 묶음도 같이).
/// - 넘침 버튼을 누르면 가려진 묶음을 묶음마다 한 줄로(구분선 기준) 펼쳐 보여 준다.
/// 항목은 복사하지 않고 그대로 옮겨 담았다가 닫을 때 원래 자리로 돌려놓으므로, 항목의 동작과 상태는 그대로다.
/// </summary>
internal sealed class GroupOverflow
{
    private readonly ToolStrip bar;
    private readonly Func<int> sideMin;  // 줄 왼쪽에 최소한 남길 여백(화면 배율 반영)
    private readonly Func<int> buttonW;  // 넘침 버튼이 차지할 폭(화면 배율 반영)
    private List<List<ToolStripItem>> hidden = new();
    private bool updating;
    private ToolStripDropDown? popup;
    private List<(ToolStripItem Item, int Index)> moved = new();
    private System.Windows.Forms.Timer? watch;
    /// <summary>펼침 창이 열려 있는 동안 넘침 버튼의 Tag에 넣는 표식. 렌더러(OverflowButtonPainter)가 눌린 모양으로 그린다.</summary>
    public const string OpenTag = "open";
    private long closedTick;
    private readonly ToolStripLabel keepButton = new("") { AutoSize = false, Width = 0, Overflow = ToolStripItemOverflow.Always }; // 펼친 동안에도 넘침 버튼이 사라지지 않게 줄에 남겨 두는 빈 항목

    public GroupOverflow(ToolStrip bar, Func<int> sideMin, Func<int> buttonW)
    {
        this.bar = bar;
        this.sideMin = sideMin;
        this.buttonW = buttonW;
        bar.CanOverflow = true;
        bar.OverflowButton.ToolTipText = "더 보기";
        bar.OverflowButton.AutoToolTip = false;
        // 기본 넘침 목록 대신 묶음별로 펼치는 창을 띄운다.
        bar.OverflowButton.DropDown.Opening += (_, e) => { e.Cancel = true; Toggle(); };
        bar.Resize += (_, _) => Update();
        bar.VisibleChanged += (_, _) => { if (!bar.Visible) Close(); else Update(); };
    }

    /// <summary>줄에서 가려진 항목이 하나라도 있는지.</summary>
    public bool IsOverflowing => hidden.Count > 0;

    /// <summary>이 항목이 지금 줄에 보이지 않고 넘침으로 빠져 있는지(가운데 맞춤 계산에서 뺀다).</summary>
    public static bool IsHidden(ToolStripItem i) => i.Overflow == ToolStripItemOverflow.Always;

    private static int Wid(ToolStripItem i) => i.Width + i.Margin.Horizontal;

    /// <summary>줄 안의 항목 묶음을 가운데에 놓을 왼쪽 여백. 가려진 묶음이 있으면 넘침 버튼이 오른쪽 끝을 차지하므로 가운데로 모으지 않고 왼쪽 최소 여백부터 놓는다.</summary>
    public int CenteredLeft()
    {
        Update();
        int min = sideMin();
        if (IsOverflowing) return min;
        int items = bar.Items.Cast<ToolStripItem>().Where(i => i.Available && !IsHidden(i)).Sum(Wid);
        return Math.Max(min, (bar.ClientSize.Width - items) / 2);
    }

    /// <summary>줄 폭에 맞춰 보일 묶음과 가릴 묶음을 다시 정한다.</summary>
    public void Update()
    {
        if (updating || popup != null || bar.IsDisposed) return;
        updating = true;
        try
        {
            // 구분선을 기준으로 묶음 나누기: 묶음 앞에 놓인 구분선들도 그 묶음에 딸려 다닌다.
            var groups = new List<(List<ToolStripSeparator> Seps, List<ToolStripItem> Items)>();
            var seps = new List<ToolStripSeparator>();
            List<ToolStripItem>? cur = null;
            foreach (ToolStripItem it in bar.Items)
            {
                if (!it.Available) continue;
                if (it is ToolStripSeparator s) { seps.Add(s); cur = null; continue; }
                if (cur == null) { cur = new(); groups.Add((seps, cur)); seps = new(); }
                cur.Add(it);
            }
            var tail = seps; // 마지막 묶음 뒤에 남은 구분선

            int avail = bar.ClientSize.Width - sideMin() - bar.Padding.Right;
            var need = groups.Select(g => g.Seps.Sum(Wid) + g.Items.Sum(Wid)).ToArray();
            int total = need.Sum() + tail.Sum(Wid);

            int keep = groups.Count;
            if (total > avail)
            {
                int room = avail - buttonW();
                int used = 0;
                keep = 0;
                while (keep < groups.Count && used + need[keep] <= room) { used += need[keep]; keep++; }
            }

            hidden = new();
            for (int g = 0; g < groups.Count; g++)
            {
                bool hide = g >= keep;
                var mode = hide ? ToolStripItemOverflow.Always : ToolStripItemOverflow.Never;
                foreach (var s in groups[g].Seps) SetMode(s, mode);
                foreach (var i in groups[g].Items) SetMode(i, mode);
                if (hide) hidden.Add(groups[g].Items);
            }
            foreach (var s in tail) SetMode(s, keep < groups.Count ? ToolStripItemOverflow.Always : ToolStripItemOverflow.Never);
        }
        finally { updating = false; }
    }

    // 같은 값을 다시 쓰면 줄 배치가 또 계산되므로 바뀔 때만 쓴다.
    private static void SetMode(ToolStripItem i, ToolStripItemOverflow mode) { if (i.Overflow != mode) i.Overflow = mode; }

    private void Toggle()
    {
        if (popup != null) { Close(); return; }
        if (Environment.TickCount64 - closedTick < 400) return; // 이 클릭 때문에 방금 밖을 눌러 닫힌 것 — 다시 열지 않는다
        Show();
    }

    private void Show()
    {
        if (hidden.Count == 0) return;
        var back = bar.BackColor;
        var line = Themes.Blend(bar.BackColor, bar.ForeColor, 0.2f);

        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(4),
            BackColor = back,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        moved = new();
        var at = bar.Items.Cast<ToolStripItem>().Select((it, i) => (it, i)).ToDictionary(x => x.it, x => x.i); // 떼기 전의 원래 번호(하나씩 떼면 번호가 밀린다)
        var strips = new List<ToolStrip>();
        for (int g = 0; g < hidden.Count; g++)
        {
            if (g > 0)
                table.Controls.Add(new Panel { Height = 1, Dock = DockStyle.Fill, BackColor = line, Margin = new Padding(2, 3, 2, 3) });
            var strip = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                CanOverflow = false,
                Dock = DockStyle.None,
                AutoSize = true,
                BackColor = back,
                ForeColor = bar.ForeColor,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
                Anchor = AnchorStyles.Left,
                ShowItemToolTips = true,
            };
            strips.Add(strip);
            table.Controls.Add(strip);
            // 줄에서 떼어 묶음 줄로 옮긴다.
            foreach (var item in hidden[g])
            {
                moved.Add((item, at[item]));
                bar.Items.Remove(item);
                item.Overflow = ToolStripItemOverflow.Never;
                strip.Items.Add(item);
            }
        }

        var host = new ToolStripControlHost(table) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false };
        var dd = new ToolStripDropDown { AutoClose = false, Padding = new Padding(1), BackColor = back };
        dd.Items.Add(host);
        host.Size = table.GetPreferredSize(Size.Empty);
        popup = dd;
        bar.Items.Add(keepButton);
        bar.OverflowButton.Tag = OpenTag;
        bar.OverflowButton.Invalidate();
        dd.Closed += (_, _) => Restore(dd, strips);
        dd.Show(bar, new Point(bar.Width, bar.Height), ToolStripDropDownDirection.BelowLeft);

        // 밖을 누르면 닫는다. 글꼴 목록 같은 펼침 목록이 열려 있는 동안의 클릭은 그 목록을 고르는 것이라 닫지 않는다.
        bool wasDown = Control.MouseButtons != MouseButtons.None;
        watch = new System.Windows.Forms.Timer { Interval = 30 };
        watch.Tick += (_, _) =>
        {
            bool down = Control.MouseButtons != MouseButtons.None;
            bool dropped = strips.Any(s => s.Items.OfType<ToolStripComboBox>().Any(c => c.DroppedDown));
            if (down && !wasDown && !dropped && !dd.Bounds.Contains(Cursor.Position)) { closedTick = Environment.TickCount64; dd.Close(); }
            wasDown = down;
        };
        watch.Start();
    }

    /// <summary>펼침 창이 열려 있으면 닫는다(닫히면 Restore가 항목을 줄로 돌려놓는다).</summary>
    public void Close() => popup?.Close();

    private void Restore(ToolStripDropDown dd, List<ToolStrip> strips)
    {
        watch?.Stop();
        watch?.Dispose();
        watch = null;
        bar.Items.Remove(keepButton);
        bar.OverflowButton.Tag = null;
        foreach (var s in strips) s.Items.Clear(); // Clear는 항목을 없애지 않고 떼기만 한다
        // 원래 번호가 작은 것부터 끼워 넣으면 처음 배열과 같아진다.
        foreach (var (item, index) in moved.OrderBy(m => m.Index))
            bar.Items.Insert(Math.Min(Math.Max(index, 0), bar.Items.Count), item);
        moved = new();
        popup = null;
        Update();
        void Dispose() { dd.Dispose(); foreach (var s in strips) s.Dispose(); }
        if (!bar.IsDisposed && bar.IsHandleCreated) bar.BeginInvoke(Dispose); // 닫히는 중인 창 안에서 바로 없애지 않는다
        else Dispose();
        bar.PerformLayout();
    }
}
