using System.IO.Pipes;
using System.Text;

namespace Cafeum;

static partial class Program
{
    /// <summary>
    /// 데이터 폴더(CAFEUM_DATA_DIR)를 따로 쓰는 복사본(휴대용 등)은 설치본과 별개로 하나씩 뜨도록, 하나만 뜨는 규칙의 이름에 그 폴더를 섞는다.
    /// 안 그러면 설치본이 켜져 있을 때 휴대용을 켜도 설치본으로 넘어가 버린다. 환경 변수가 없으면(설치본) 예전 이름 그대로다.
    /// </summary>
    private static readonly string InstanceSuffix = Environment.GetEnvironmentVariable("CAFEUM_DATA_DIR") is { Length: > 0 } dataDir
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\').ToLowerInvariant())))[..12]
        : "";
    private static readonly string PipeName = "Cafeum.SingleInstance.Pipe" + InstanceSuffix;

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
        using var mutex = new Mutex(true, @"Local\Cafeum.SingleInstance" + InstanceSuffix, out bool first);
        if (!first)
        {
            if (!SendToRunning(args)) MessageBox.Show("Cafeum이 이미 실행 중이지만 응답이 없어요. 잠시 뒤에 다시 시도해 주세요.", "Cafeum");
            return;
        }

        ApplicationConfiguration.Initialize();
        InstallCrashHandlers();

#if     DEBUG // 개발용(Scripts/Test/ShotTest.cs): 화면을 그려서 그림 파일로 저장하고 끝낸다
        if (Environment.GetEnvironmentVariable("CAFEUM_SHOT") is { Length: > 0 } shotDir) { ShotTest(shotDir); return; } 
#endif

        string? initialPath = args.Length > 0 ? args[0] : null;
        var context = AppShared.Start(initialPath, args.Skip(1).ToArray());
        StartListener();
        Updater.CleanupOldInstallers();
        TempCleanup.RunInBackground(); // 지난번 비정상 종료로 임시 폴더에 남은 epub · 소설 본문 복사본 정리
        Application.Run(context);
    }

    private static DateTime lastCrashNotice = DateTime.MinValue;

    /// <summary>
    /// 곳곳에서 잡지 못한 오류를 한곳에서 받아 cafeum.log에 자세히 남긴다(전에는 기본 오류창만 뜨고 기록이 없었다).
    /// 화면 쪽 오류는 앱을 계속 쓸 수 있게 두고 안내만 하되, 타이머처럼 같은 오류가 계속 나도 창이 쏟아지지 않게 30초에 한 번만 띄운다.
    /// 다른 스레드의 오류는 윈도우가 프로그램을 끝내므로 기록만 남긴다.
    /// </summary>
    private static void InstallCrashHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Write("처리하지 못한 오류(화면)\n" + e.Exception);
            if (DateTime.Now - lastCrashNotice < TimeSpan.FromSeconds(30)) return;
            lastCrashNotice = DateTime.Now;
            MessageBox.Show("예상하지 못한 오류가 생겼어요. 계속 쓸 수는 있지만 이상하면 Cafeum을 다시 켜 주세요.\n오류 내용은 기록해 두었어요: " + AppLog.FilePath, "Cafeum");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("처리하지 못한 오류(프로그램이 끝납니다)\n" + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { AppLog.Write("기다리지 않은 작업의 오류\n" + e.Exception); e.SetObserved(); };
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
        }) { IsBackground = true, Name = "CafeumPipe" }.Start();
    }
}
