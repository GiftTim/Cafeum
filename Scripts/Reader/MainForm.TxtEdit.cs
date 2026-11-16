using System.Text;

namespace Cafeum;

/// <summary>
/// txt를 메모장처럼: 파일을 열면 곧바로 쓸 수 있고(수정하기를 누를 필요 없음), 고친 내용은 잠깐 쉬면 알아서 저장한다.
/// 파일의 글자 인코딩(UTF-8 · UTF-8 BOM · UTF-16 · 한글 CP949)을 알아내서 저장할 때 같은 인코딩으로 되돌린다.
/// </summary>
public partial class MainForm
{
    private Encoding txtSaveEncoding = new UTF8Encoding(false);
    private bool directEdit;                 // 이 txt는 웹 화면을 거치지 않고 바로 텍스트 상자로 열렸다
    private string? savedSnapshot;           // 마지막으로 디스크와 맞춘 글(바뀌었는지 가리는 기준)
    private readonly System.Windows.Forms.Timer autoSaveTimer = new() { Interval = 10_000 };   // 아무것도 안 치고 이 시간이 지나면 저장(설정의 AutoSaveIdleSeconds)
    private readonly System.Windows.Forms.Timer autoSaveMaxTimer = new() { Interval = 60_000 }; // 계속 치고 있어도 최소 이 간격으로는 저장(설정의 AutoSaveMaxSeconds)

    /// <summary>칸 머리 제목 옆 보라색 동그라미: 디스크와 같은 글이면(= 저장됐으면) 보인다.</summary>
    private bool? lastSavedState;
    /// <summary>에피소드가 저장됐는지(디스크와 같은 글인지)를 칸 머리 쪽에 알린다. 에피소드가 아니면 null.</summary>
    public event Action<bool?>? SavedStateChanged;
    private void UpdateSavedDot()
    {
        bool? v = directEdit && !IsReferenceFile(currentPath) && !mainEditor.ReadOnly && savedSnapshot != null ? (mainEditor.Text == savedSnapshot && !formatDirty) : null;
        if (v == lastSavedState) return;
        lastSavedState = v;
        SavedStateChanged?.Invoke(v);
    }

    /// <summary>글 상자 안의 줄바꿈(\n)을 파일에 쓰는 줄바꿈(\r\n)으로.</summary>
    private static string ToCrlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>
    /// 이 파일의 글자 모양(인코딩)으로 지금 글을 빠짐없이 저장할 수 있는지 본다. 예전 한글 txt(CP949)에 이모지 같은 글자를 쓰면
    /// 그냥 저장할 때 '?'로 바뀌어 글자가 사라지므로, 그럴 땐 UTF-8로 바꿔 저장하고 알려 준다.
    /// </summary>
    private Encoding ChooseSaveEncoding(string content)
    {
        var enc = SafeFile.PickEncoding(content, txtSaveEncoding);
        if (!ReferenceEquals(enc, txtSaveEncoding))
        {
            AppLog.Write($"저장 형식을 {txtSaveEncoding.WebName}에서 UTF-8로 바꿨어요(이 형식으로는 저장할 수 없는 글자가 있어서): {currentPath}");
            ShowToast("이 파일의 옛 글자 형식으로는 저장할 수 없는 글자가 있어서 UTF-8로 저장했어요");
        }
        return enc;
    }

    /// <summary>파일 앞부분에 NUL(0) 바이트가 있으면 글 파일이 아니다(압축 · 문서 · 실행 파일 등). UTF-16 글은 BOM으로 알아본다.</summary>
    private static bool LooksLikeText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[8192];
            int n = fs.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true;
            for (int i = 0; i < n; i++) if (buf[i] == 0) return false;
            return true;
        }
        catch { return true; } // 못 읽는 이유는 불러올 때 알려 준다
    }

    private void InitTxtEdit()
    {
        autoSaveTimer.Interval = Math.Max(1, settings.AutoSaveIdleSeconds) * 1000;
        autoSaveMaxTimer.Interval = Math.Max(1, settings.AutoSaveMaxSeconds) * 1000;
        autoSaveTimer.Tick += (_, _) => FlushAutoSave();
        autoSaveMaxTimer.Tick += (_, _) => FlushAutoSave();
        mainEditor.TextChanged += (_, _) =>
        {
            if (!directEdit || mainEditor.ReadOnly) return;
            autoSaveTimer.Stop(); autoSaveTimer.Start();            // 칠 때마다 "쉰 시간"을 다시 잰다
            if (!autoSaveMaxTimer.Enabled) autoSaveMaxTimer.Start(); // 첫 변경부터 최대 간격을 잰다
            UpdateSavedDot();
        };
        mainEditor.SelectionChanged += (_, _) => SyncCharButtons(); // 서식 버튼이 커서 자리의 서식대로 눌려 보이게
        mainEditor.KeyUp += (_, e) => { if (e.Control && e.KeyCode is Keys.B or Keys.I or Keys.U) MarkFormatDirty(); }; // 단축키(Ctrl+B/I/U)로 바꾼 서식도 저장 대상
        FormClosing += (_, _) => FlushAutoSave();
        FormClosed += (_, _) => { autoSaveTimer.Dispose(); autoSaveMaxTimer.Dispose(); };
    }

    /// <summary>고친 글이 있으면 지금 바로 파일에 쓴다(실패하면 상태 줄에 알리고 다음 기회에 다시 시도).</summary>
    private void FlushAutoSave()
    {
        autoSaveTimer.Stop();
        autoSaveMaxTimer.Stop();
        if (!directEdit || mainEditor.ReadOnly || string.IsNullOrEmpty(currentPath) || txtFromZip || hwpSource) return;
        string content = mainEditor.Text;
        bool textChanged = content != savedSnapshot;
        if (!textChanged && !formatDirty) { UpdateSavedDot(); return; }
        try
        {
            if (textChanged)
            {
                txtSaveEncoding = ChooseSaveEncoding(content);
                SafeFile.WriteAllText(currentPath, ToCrlf(content), txtSaveEncoding); // 임시 파일에 다 쓴 뒤 바꿔치기 — 쓰다 멈춰도 원고가 잘리지 않는다
                savedSnapshot = content;
                fullText = content;
            }
            SaveFormatSidecar();
            formatDirty = false;
            statusLabel.Text = $"저장했습니다 · {DateTime.Now:HH:mm:ss}";
            UpdateSavedDot();
        }
        catch (Exception ex)
        {
            AppLog.Write($"자동 저장에 실패했어요: {currentPath}", ex);
            statusLabel.Text = $"저장하지 못했어요: {ex.Message}";
        }
    }

    /// <summary>파일 맨 앞의 BOM과 글자 모양으로 인코딩을 고른다. BOM이 없고 UTF-8로 읽으면 깨지면 한글 윈도우 기본(CP949)으로 본다.</summary>
    private static (string Text, Encoding Encoding) ReadTextDetectEncoding(string path, IProgress<double>? progress, CancellationToken ct)
    {
        byte[] head = new byte[4];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = fs.Read(head, 0, 4);
        Encoding enc;
        if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) enc = new UTF8Encoding(true);
        else if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) enc = new UnicodeEncoding(false, true);
        else if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) enc = new UnicodeEncoding(true, true);
        else
        {
            enc = new UTF8Encoding(false);
            string probe = ReadAllTextWithProgress(path, progress, ct);
            if (!probe.Contains('�')) return (probe, enc);
            enc = CodePagesEncodingProvider.Instance.GetEncoding(949) ?? enc;
            return (enc.GetString(File.ReadAllBytes(path)), enc);
        }
        return (ReadAllTextWithProgress(path, progress, ct), enc);
    }
}
