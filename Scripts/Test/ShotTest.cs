using System.Text;

namespace Cafeum;

// 개발용 화면 점검. Debug 빌드에서만 들어가고, 배포용(Release) exe에는 포함되지 않는다.
#if DEBUG
static partial class Program
{
    /// <summary>개발용 점검: 스케줄러 화면과 팝업을 실제로 그려서 PNG로 저장한다(CAFEUM_SHOT=저장 폴더).</summary>
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
            string sampleFile = Path.Combine(Path.GetTempPath(), "cafeum_pip_test", "pip1.txt");
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
            TempCleanup.DeleteQuietly(bk.Dir);
            log.AppendLine($"txtweb temp: dirCleaned={!Directory.Exists(bk.Dir)}");
        }
        // 원고 안전 저장(SafeFile) · 저장 인코딩 고르기 · 글 파일 판별(LooksLikeText) 시험 — 모두 실제 앱 코드를 그대로 부른다
        {
            string d = Path.Combine(Path.GetTempPath(), "cafeum_safefile_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            string f = Path.Combine(d, "a.txt"), tmp = f + ".cafeum-tmp";
            var utf8 = new UTF8Encoding(false);
            SafeFile.WriteAllText(f, "첫 글", utf8);
            log.AppendLine($"safefile new: content={File.ReadAllText(f) == "첫 글"} noTmp={!File.Exists(tmp)}");
            SafeFile.WriteAllText(f, "둘째 글", utf8);
            log.AppendLine($"safefile overwrite: content={File.ReadAllText(f) == "둘째 글"} noTmp={!File.Exists(tmp)}");
            try { SafeFile.WriteAtomic(f, t => { File.WriteAllText(t, "반쪽만"); throw new IOException("시험용 실패"); }); } catch (IOException) { }
            log.AppendLine($"safefile failedWrite: originalKept={File.ReadAllText(f) == "둘째 글"} noTmp={!File.Exists(tmp)}");
            var cp949 = CodePagesEncodingProvider.Instance.GetEncoding(949)!;
            log.AppendLine($"pickEncoding: cp949+korean={SafeFile.PickEncoding("가나다 …", cp949).CodePage} cp949+emoji={SafeFile.PickEncoding("가나다 😀", cp949).CodePage} utf8+emoji={SafeFile.PickEncoding("😀", utf8).CodePage}");
            string textFile = Path.Combine(d, "text.txt"), binFile = Path.Combine(d, "bin.dat"), u16File = Path.Combine(d, "u16.txt");
            File.WriteAllText(textFile, "일반 글", utf8);
            File.WriteAllBytes(binFile, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x08, 0x00 }); // zip(docx 등)처럼 NUL이 든 파일
            File.WriteAllText(u16File, "유니코드 글", new UnicodeEncoding(false, true));
            var looks = typeof(MainForm).GetMethod("LooksLikeText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            bool L(string p) => (bool)looks.Invoke(null, new object[] { p })!;
            log.AppendLine($"looksLikeText: text={L(textFile)} binary={L(binFile)} utf16={L(u16File)} missing={L(Path.Combine(d, "none.txt"))}");
            log.AppendLine($"epubGuard: file={EpubWebGuard.IsLocal("file:///C:/a.html")} http={EpubWebGuard.IsLocal("http://a.com")}");
            try { Directory.Delete(d, true); } catch { }
        }
        File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
    }
}
#endif
