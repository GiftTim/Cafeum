using System.IO.Pipes;
using System.Text;

namespace CoffeeViewer;

static class Program
{
    private const string PipeName = "Caffeum.SingleInstance.Pipe";

    /// <summary>
    /// 진입점. 프로그램은 하나만 떠 있는다: 이미 실행 중이면 새로 띄우지 않고 받은 인자(폴더 · 파일)를 기존 프로그램에 넘긴 뒤 끝낸다
    /// (기존 창이 앞으로 나오고, 파일을 넘겼다면 PIP 창이 하나 더 열린다. PIP 창은 여러 개 있어도 된다).
    /// 인자 없이 실행하면 마지막에 열었던 볼트(폴더)가 자동으로 열리고, 처음이면 폴더를 고르게 한다.
    /// 폴더 경로를 인자로 주면 그 폴더를 볼트로 열고, 파일 경로 하나를 주면(더블클릭 등) 마지막 볼트에서 그 문서를 탭으로 연다.
    /// 볼트 창이 모두 닫히면 프로그램이 끝난다(AppShared).
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\Caffeum.SingleInstance", out bool first);
        if (!first)
        {
            if (!SendToRunning(args)) MessageBox.Show("Caffeum이 이미 실행 중이지만 응답이 없어요. 잠시 뒤에 다시 시도해 주세요.", "Caffeum");
            return;
        }

        ApplicationConfiguration.Initialize();
        if (Environment.GetEnvironmentVariable("CAFFEUM_SHOT") is { Length: > 0 } shotDir) { ShotTest(shotDir); return; } // 개발용: 화면을 그려서 그림 파일로 저장하고 끝낸다
        string? initialPath = args.Length > 0 ? args[0] : null;
        var context = AppShared.Start(initialPath, args.Skip(1).ToArray());
        StartListener();
        Updater.CleanupOldInstallers();
        Application.Run(context);
    }

    /// <summary>개발용 점검: 스케줄러 화면과 팝업을 실제로 그려서 PNG로 저장한다(CAFFEUM_SHOT=저장 폴더).</summary>
    private static void ShotTest(string dir)
    {
        Directory.CreateDirectory(dir);
        var pal = LibraryPalette.From(Themes.All[0]);
        var log = new StringBuilder();
        void Save(Form f, string name)
        {
            f.Show(); Application.DoEvents(); Thread.Sleep(300); Application.DoEvents();
            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
            bmp.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            log.AppendLine($"{name}: size={f.Size} client={f.ClientSize} dpi={f.DeviceDpi}");
            f.Close();
        }
        Save(new ScheduleEntryDialog(pal, "마감", ScheduleStore.Calendars[0].Id, DateTime.Today, 13 * 60, 14 * 60, "", "", false, editing: true), "event_edit");
        Save(new ScheduleEntryDialog(pal, "", ScheduleStore.Calendars[0].Id, DateTime.Today, 13 * 60, 14 * 60), "event_new");
        Save(new ScheduleDefDialog(pal, "마감", Color.FromArgb(0x2B, 0x25, 0x70), ""), "def");
        var host = new Form { Size = new Size(1500, 820), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
        var view = new ScheduleView { Dock = DockStyle.Fill };
        host.Controls.Add(view);
        view.SetPalette(pal);
        view.ShowToday();
        Save(host, "view");
        {   // 월 보기: 오른쪽이 [오늘 날짜 + 스케줄 적는 칸]으로 바뀌는지
            ScheduleStore.Add(DateTime.Today, "집필", ScheduleStore.Calendars[0].Id, 13 * 60, 90); ScheduleStore.Add(DateTime.Today, "마감", ScheduleStore.Calendars[1].Id, -1, 0); // (시험은 임시 데이터 폴더에서만 돈다)
            var h2 = new Form { Size = new Size(1500, 820), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            var v2 = new ScheduleView { Dock = DockStyle.Fill };
            h2.Controls.Add(v2); v2.SetPalette(pal); v2.ShowToday();
            var bfl = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var g2 = typeof(ScheduleView).GetField("grid", bfl)!.GetValue(v2)!;
            g2.GetType().GetMethod("SetView", bfl)!.Invoke(g2, new object[] { 0 });
            var brushProp = g2.GetType().GetProperty("Brush");
            log.AppendLine($"month default brush null={(brushProp!.GetValue(g2) == null)}");
            for (int k = 0; k < 4; k++) ScheduleStore.Add(DateTime.Today, "일정", ScheduleStore.Calendars[1].Id, -1, 0);   // 일정이 많을 때 머리 위로 넘치지 않는지
            ScheduleStore.Add(DateTime.Today.AddDays(3), "생일", ScheduleStore.Calendars[2].Id, 10 * 60, 60);
            ScheduleStore.Calendars[2].Visible = false; // 안 보이게 한 스케줄 모양 확인(메모리에서만)
            v2.Invalidate();
            Save(h2, "view_month");
            try {   // 다른 날짜(3일 뒤)를 누른 것처럼
                var h3 = new Form { Size = new Size(1500, 820), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
                var v3 = new ScheduleView { Dock = DockStyle.Fill };
                h3.Controls.Add(v3); v3.SetPalette(pal); v3.ShowToday();
                typeof(ScheduleView).GetMethods(bfl).First(m => m.Name == "Select" && m.DeclaringType == typeof(ScheduleView) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(DateTime)).Invoke(v3, new object[] { DateTime.Today.AddDays(3) });
                Save(h3, "view_month_sel");
            } catch (Exception ex) { log.AppendLine("sel failed: " + ex); }
            {   // 색 선택 창(지금 있는 스케줄 + 새 색 7가지)
                var pf = new Form { Size = new Size(320, 440), StartPosition = FormStartPosition.Manual, Location = new Point(60, 60), BackColor = Color.White };
                var pk = new ScheduleColorPicker(pal, true, ScheduleStore.Calendars[1].Id, null) { Location = new Point(20, 20) };
                pf.Controls.Add(pk);
                Save(pf, "color_picker");
                var pf2 = new Form { Size = new Size(320, 200), StartPosition = FormStartPosition.Manual, Location = new Point(60, 60), BackColor = Color.White };
                pf2.Controls.Add(new ScheduleColorPicker(pal, false, null, SchedulePalette7.Colors[3]) { Location = new Point(20, 20) });
                Save(pf2, "color_picker7");
            }
        }
        // 뽀모도로: 막대 모양 최소화 + 설정 창
        Pomodoro.Init(); Pomodoro.S.Mini = true; Pomodoro.S.MiniStyle = "bar"; Pomodoro.S.BarCompact = false;
        var ph = new Form { Size = new Size(760, 160), StartPosition = FormStartPosition.Manual, Location = new Point(60, 60), BackColor = Color.White };
        var pw = new PomodoroWidget(pal); ph.Controls.Add(pw);
        ph.Show(); pw.Size = PomodoroWidget.SizeFor(ph.DeviceDpi); pw.Location = new Point(40, 40);
        Save(ph, "pomo_bar");
        Pomodoro.S.BarCompact = true;
        var ph2 = new Form { Size = new Size(560, 160), StartPosition = FormStartPosition.Manual, Location = new Point(60, 260), BackColor = Color.White };
        var pw2 = new PomodoroWidget(pal); ph2.Controls.Add(pw2);
        ph2.Show(); pw2.Size = PomodoroWidget.SizeFor(ph2.DeviceDpi); pw2.Location = new Point(40, 40);
        Save(ph2, "pomo_compact");
        Pomodoro.S.BarCompact = false;
        Save(new PomodoroSettingsDialog(pal), "pomo_settings");
        Save(new ViewSettingsDialog(Themes.All[0], 170, 0, true, true), "view_settings");
        {   // 압정 아이콘 모양 점검(꺼짐 · 켜짐)
            using var sheet = new Bitmap(360, 130);
            using (var sg = Graphics.FromImage(sheet))
            {
                sg.Clear(Color.White);
                using var off = PinIcon.Make(Color.FromArgb(0x2B, 0x26, 0x22), 88, false);
                using var on = PinIcon.Make(Color.FromArgb(0x2B, 0x26, 0x22), 88, true);
                using var offS = PinIcon.Make(Color.FromArgb(0x2B, 0x26, 0x22), 22, false);
                using var onS = PinIcon.Make(Color.FromArgb(0x2B, 0x26, 0x22), 22, true);
                sg.DrawImage(off, 10, 20); sg.DrawImage(on, 120, 20); sg.DrawImage(offS, 240, 20); sg.DrawImage(onS, 290, 20);
            }
            sheet.Save(Path.Combine(dir, "pin_icons.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        {   // 팝업 밖 클릭 닫기 시험(마우스는 흉내만 낸다): 툴바에 나중에 올라온 드롭다운 항목도 닫히는지
            var th = new Form { Size = new Size(500, 300), StartPosition = FormStartPosition.Manual, Location = new Point(60, 60) };
            var strip = new ToolStrip(); th.Controls.Add(strip);
            MenuGuard.AttachAll(strip);                                   // 항목이 올라오기 전에 걸어 둔다(앱과 같은 순서)
            var item = new ToolStripMenuItem("읽기 방식 ▾");
            item.DropDownItems.Add(new ToolStripMenuItem("…"));
            item.DropDownOpening += (_, _) => { item.DropDownItems.Clear(); item.DropDownItems.Add("스크롤"); item.DropDownItems.Add("페이지"); };
            strip.Items.Add(item);                                        // 나중에 추가
            th.Show(); Application.DoEvents();
            bool down = false; var pos = new Point(1500, 900);
            MenuGuard.ButtonDownOverride = () => down; MenuGuard.PositionOverride = () => pos;
            item.ShowDropDown(); Application.DoEvents(); Thread.Sleep(150); Application.DoEvents();
            bool openBefore = item.DropDown.Visible;
            // 메뉴 밖(본문 쪽)을 새로 누른 것처럼
            down = true; for (int i = 0; i < 6; i++) { Thread.Sleep(50); Application.DoEvents(); }
            bool openAfter = item.DropDown.Visible;
            MenuGuard.ButtonDownOverride = null; MenuGuard.PositionOverride = null;
            log.AppendLine($"menuguard: openBefore={openBefore} openAfterOutsideClick={openAfter}");
            th.Close();
        }
        {   // 실제 PIP 읽기 창의 [읽기 방식] 팝업이 밖을 눌러 닫히는지(마우스는 흉내만 낸다)
            string sampleFile = Path.Combine(Path.GetTempPath(), "caffeum_pip_test", "pip1.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sampleFile)!);
            File.WriteAllText(sampleFile, "읽기 방식 시험", new UTF8Encoding(false));
            var pip = new PipForm(sampleFile) { StartPosition = FormStartPosition.Manual, Location = new Point(80, 80) };
            pip.Show(); for (int i = 0; i < 40; i++) { Thread.Sleep(100); Application.DoEvents(); }
            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var doc = (MainForm?)typeof(PipForm).GetField("doc", bf)?.GetValue(pip);
            var rm = (ToolStripMenuItem?)typeof(MainForm).GetField("refReadMode", bf)?.GetValue(doc);
            var rb = typeof(MainForm).GetField("readBar", bf)?.GetValue(doc) as ToolStrip;
            log.AppendLine($"pip: doc={(doc != null)} refReadMode={(rm != null)} inReadBar={(rm != null && rb != null && rb.Items.Contains(rm))} readBarParent={(rb?.Parent != null)}");
            log.AppendLine($"pip item: visible={rm?.Visible} available={rm?.Available} enabled={rm?.Enabled} barVisible={rb?.Visible} items={rb?.Items.Count}");
            if (rm != null)
            {
                bool down2 = false; var pos2 = new Point(1700, 1000);
                MenuGuard.ButtonDownOverride = () => down2; MenuGuard.PositionOverride = () => pos2;
                pip.Activate(); Application.DoEvents(); Thread.Sleep(300); Application.DoEvents(); rm.ShowDropDown(); Application.DoEvents(); Thread.Sleep(150); Application.DoEvents();
                bool ob = rm.DropDown.Visible;
                down2 = true; for (int i = 0; i < 6; i++) { Thread.Sleep(50); Application.DoEvents(); }
                log.AppendLine($"pip readmode: openBefore={ob} openAfterOutsideClick={rm.DropDown.Visible}");
                MenuGuard.ButtonDownOverride = null; MenuGuard.PositionOverride = null;
            }
            pip.Close();
        }
        // 문장 단위 자동 줄바꿈 시험
        {
            string sample = "그는 말했다. “정말이야?” 그녀가 되물었다! 3.14는 원주율이다… 끝이었다.\r\n\r\n다음 문단이다. 두 번째 문장.\r\n";
            var chs = TxtChapterSplitter.Split(sample, "시험", new TxtSplitOptions());
            var bk = TxtWebBook.Build(sample, chs, "시험", Path.Combine(dir, "sample.txt"), autoBreak: true);
            File.WriteAllText(Path.Combine(dir, "autobreak.html"), File.ReadAllText(bk.Book.Chapters[0].FilePath));
        }
        File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
    }

    private static bool SendToRunning(string[] args)
    {
        try
        {
            for (int attempt = 0; attempt < 5; attempt++) // 서버가 다음 연결을 준비하는 짧은 틈에 걸리면 다시 시도
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(3000);
                    using var w = new StreamWriter(client, new UTF8Encoding(false));
                    foreach (var a in args) w.WriteLine(Path.IsPathRooted(a) || Directory.Exists(a) || File.Exists(a) ? Path.GetFullPath(a) : a);
                    w.Flush();
                    return true;
                }
                catch (IOException) { Thread.Sleep(150); }
                catch (TimeoutException) { }
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>다른 실행이 보낸 인자를 받아 기존 프로그램에서 처리한다.</summary>
    private static void StartListener()
    {
        var sync = new Control();
        sync.CreateControl();
        _ = sync.Handle; // 창 핸들을 지금 만들어 둬야 다른 스레드에서 BeginInvoke를 쓸 수 있다
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    // 여러 파일을 한꺼번에 열면 실행이 동시에 여러 개 들어온다 → 연결마다 따로 읽어서 줄 세우지 않는다
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            using (server)
                            using (var r = new StreamReader(server, new UTF8Encoding(false)))
                            {
                                var list = new List<string>();
                                for (string? line; (line = r.ReadLine()) != null;) if (line.Length > 0) list.Add(line);
                                sync.BeginInvoke(() => { try { AppShared.HandleExternal(list.ToArray()); } catch { } });
                            }
                        }
                        catch { }
                    });
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "CaffeumPipe" }.Start();
    }
}
