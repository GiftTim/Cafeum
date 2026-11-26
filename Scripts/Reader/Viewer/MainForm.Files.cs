using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// 파일 열기와 저장: 열기 창 · txt 읽기 화면 · 저장 · epub 다시 묶기. (MainForm의 한 조각 — 클래스 설명은 MainForm.cs)
/// </summary>
public partial class MainForm
{
    // ───────────────────────── 파일 열기/저장 ─────────────────────────

    private void OpenFileDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "지원 파일 (*.txt;*.md;*.epub;*.pdf;*.hwp;*.hwpx;*.zip;*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp)|*.txt;*.md;*.markdown;*.epub;*.pdf;*.hwp;*.hwpx;*.zip;*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp" +
                "|텍스트 파일 (*.txt)|*.txt|마크다운 (*.md)|*.md;*.markdown|EPUB (*.epub)|*.epub|PDF (*.pdf)|*.pdf|한글 문서 (*.hwp;*.hwpx)|*.hwp;*.hwpx|zip (소설·만화) (*.zip)|*.zip|이미지 파일|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp|모든 파일|*.*",
        };
        if (dlg.ShowDialogOver(this) == DialogResult.OK)
            OpenPath(dlg.FileName);
    }

    private async void LoadFile(string path)
    {
        // 글 파일이 아닌 것(압축 · 문서 · 실행 파일 등)을 글로 열면 깨진 글자가 보이고, 거기서 한 글자라도 고치면 자동 저장이 원본을 덮어써서 망가진다.
        if (!LooksLikeText(path))
        {
            MessageBox.Show(this, "글 파일이 아닌 것 같아서 열지 않았어요.\n(txt · md · epub · pdf · hwp · zip · 그림 파일을 열 수 있어요.)", AppShared.AppDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string text;
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("파일을 여는 중…");
        try
        {
            var progress = new Progress<double>(loadingOverlay.Report);
            var (t, enc) = await Task.Run(() => ReadTextDetectEncoding(path, progress, cts.Token));
            text = t;
            txtSaveEncoding = enc;
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"파일을 여는 데 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
        }

        ShowLoadedText(path, text, prepared: null, fromZip: false);
    }

    /// <summary>
    /// 읽어들인 글을 화면에 올린다(txt 파일과 zip 안의 소설이 함께 쓴다). prepared는 zip 소설처럼 이미
    /// 줄바꿈 정리와 챕터 나누기를 마친 경우에 넘기고, 없으면 여기서 정리하고 나눈다.
    /// </summary>
    private void ShowLoadedText(string path, string text, List<TxtChapter>? prepared, bool fromZip)
    {
        // 줄바꿈 통일은 아래 두 갈래(편집기 · 웹 화면)가 각자 \n 한 글자로 맞춘다.
        FlushAutoSave(); // 앞서 쓰던 파일에 고친 게 남았으면 먼저 저장
        txtFromZip = fromZip;
        hwpSource = !fromZip && HwpDocument.IsHwpExtension(path);
        if (fromZip || hwpSource) txtSaveEncoding = new UTF8Encoding(false);
        // Cafeum: 보통 txt는 메모장처럼 바로 쓸 수 있게 텍스트 상자로 연다(zip · 한글 문서에서 뽑은 글은 예전처럼 읽기 화면).
        // 참고 자료 폴더의 txt는 남의 글이라 바로 고치게 열지 않는다: 읽기 화면으로 열고 [수정하기]를 눌러야 고칠 수 있다.
        directEdit = !fromZip && !hwpSource && !IsReferenceFile(path);
        if (directEdit)
        {
            txtWeb = false;
            txtBook = null;
            txtFindStart = txtFindEnd = 0;
            SetContentMode(ContentKind.Txt);
            imageSet?.Dispose();
            imageSet = null;
            ClearImageCache();
            currentPath = path;
            OnBookOpened(path);
            epubEditable = false;
            text = text.Replace("\r\n", "\n").Replace('\r', '\n'); // 글 상자 안에서는 줄바꿈이 \n 한 글자(저장할 때만 \r\n)
            fullText = text;
            chapters = TxtChapterSplitter.Split(text, Path.GetFileNameWithoutExtension(path), TxtSplitOptions.From(settings));
            Text = $"{Path.GetFileName(path)} — {AppShared.AppDisplayName}";
            statusLabel.Text = $"총 {text.Length:N0}자";
            mainEditor.Text = text;
            TryLoadFormatSidecar(text); // 이 글에 입혀 둔 서식이 있으면 불러온다
            mainEditor.ApplyParaFormat(EditorPara(), force: true); // 글을 새로 넣으면 문단 서식이 기본으로 돌아가니 다시 건다
            formatDirty = false;
            savedSnapshot = mainEditor.Text;
            ApplySideMargin(); // 회차 제목을 새 파일 이름으로
            SetTxtEditMode(true);
            ApplyManuscriptProtection(); // 집필 완료한 회차는 원고 보호로 잠근다
            var saveRec = library.Find(path);
            int pos = saveRec != null && saveRec.PosA > 0 && saveRec.PosA < text.Length ? saveRec.PosA : 0;
            GoToOffset(pos);
            mainEditor.Focus();
            return;
        }

        txtWeb = true; // 읽기는 웹 화면으로(SetContentMode가 epubHost를 보여준다)
        txtBook = null;
        txtFindStart = txtFindEnd = 0;
        SetContentMode(ContentKind.Txt); // 이전에 epub/이미지를 보고 있었다면 txt 화면으로 되돌린다
        imageSet?.Dispose();
        imageSet = null;
        ClearImageCache();
        currentPath = path;
        OnBookOpened(path);
        ApplyLookBarShown(); // 열린 파일이 참고 자료인지에 따라 서식창을 고른다(경로가 정해진 뒤에)
        // 새 파일을 열면 항상 읽기 전용으로 시작한다. SetEditMode를 거치면 "수정을 마쳤다"로 보고 옛 글로 웹 화면을
        // 다시 만들므로 상자만 잠그고, 앞서 보던 epub의 수정하기도 끈다(같은 웹뷰에 txt 문서가 뜨니까).
        SetTxtEditMode(false);
        epubEditable = false;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        fullText = text;
        chapters = prepared ?? TxtChapterSplitter.Split(text, Path.GetFileNameWithoutExtension(path), TxtSplitOptions.From(settings));
        Text = $"{Path.GetFileName(path)} — {AppShared.AppDisplayName}";
        statusLabel.Text = $"{chapters.Count}개 챕터 · 총 {text.Length:N0}자";

        var rec = library.Find(path);
        int saved = rec != null && rec.PosA > 0 && rec.PosA < fullText.Length ? rec.PosA : 0;
        if (saved > 0) ShowToast($"이어서 읽습니다 ({(int)Math.Round(100.0 * saved / fullText.Length)}%)");
        mainEditor.Text = ""; // 큰 글을 보이지도 않는 상자에 들고 있지 않게(수정하기를 켤 때 다시 넣는다)
        ShowTxtWeb(saved);
    }

    /// <summary>이 창의 모든 툴바 · 메뉴 줄의 드롭다운에 "밖을 누르면 닫기"를 건다(이미 건 것은 건너뛴다).</summary>
    private void AttachMenuGuards()
    {
        void Walk(Control c)
        {
            if (c is ToolStrip ts) MenuGuard.AttachAll(ts);
            foreach (Control child in c.Controls) Walk(child);
        }
        Walk(this);
        if (readBar != null) MenuGuard.AttachAll(readBar); // 참고 자료 서식창(읽기 방식 · 보기 모드 …)은 창에 붙기 전에도 걸어 둔다
        if (lookBar != null) MenuGuard.AttachAll(lookBar);
    }

    /// <summary>
    /// txt를 웹 화면으로 띄운다: 글을 문서 여러 개로 나눠 임시 폴더에 쓰고(TxtWebBook) epub과 같은 길로 보여준 뒤 offset 위치로 옮긴다.
    /// WebView2 런타임이 없는 PC처럼 웹 화면을 못 쓰면 예전처럼 텍스트 상자로 보여준다.
    /// </summary>
    private async void ShowTxtWeb(int offset)
    {
        string path = currentPath!;
        try
        {
            await EnsureEpubViewAsync();
            string text = fullText, title = Path.GetFileNameWithoutExtension(path);
            var chs = chapters;
            bool brk = settings.TxtAutoBreak;
            var book = await Task.Run(() => TxtWebBook.Build(text, chs, title, path, brk));
            if (!txtWeb || currentPath != path) { TempCleanup.DeleteQuietly(book.Dir); return; } // 만드는 사이 다른 파일을 열었거나 수정하기를 켰다
            txtBook = book;
            if (txtTempDir != null && txtTempDir != book.Dir) TempCleanup.DeleteQuietly(txtTempDir); // 앞서 만든 소설 본문 복사본
            txtTempDir = book.Dir;
        }
        catch (Exception ex)
        {
            if (currentPath != path) return;
            txtWeb = false;
            txtBook = null;
            SetContentMode(ContentKind.Txt);
            mainEditor.Text = fullText;
            GoToOffset(offset);
            statusLabel.Text = $"웹 화면을 쓸 수 없어 기본 화면으로 엽니다: {ex.Message}";
            return;
        }

        epubBook = txtBook.Book;
        TempCleanup.DeleteQuietly(epubExtractDir); // 앞서 보던 epub을 풀어 둔 폴더(이제 txt를 보니 필요 없다)
        epubExtractDir = null;
        epubChapterIndex = -1; // 예전 epub의 같은 번호 챕터를 "이미 떠 있는 문서"로 착각하지 않게
        lastEpubTrackedChapter = -1;
        ShowTxtWebAt(offset);
        epubView?.Focus(); // 방향키 · 휠이 바로 먹도록
    }

    /// <summary>txt 웹 화면에서 원본 글자 위치(offset)로 간다. 이미 그 문서가 떠 있으면 다시 불러오지 않고 그 안에서 옮긴다.</summary>
    private void ShowTxtWebAt(int offset, bool reload = false)
    {
        if (txtBook == null) return;
        offset = Math.Clamp(offset, 0, fullText.Length);
        txtWebTopOffset = offset;
        int chunk = txtBook.ChunkAt(offset);
        if (!reload && chunk == epubChapterIndex && epubDocReady)
        {
            RunEpubScript($"window.__clrTxtGo && window.__clrTxtGo({offset});");
            RefreshNavPosition();
            return;
        }
        txtPendingOffset = offset;
        epubRestorePermille = null;
        ShowEpubChapter(chunk);
    }

    /// <summary>txt 웹 화면에서 찾은 글(전체 글 기준 구간)을 표시하고 그 자리로 간다.</summary>
    private void TxtWebSelect(int start, int length)
    {
        if (txtBook == null) return;
        txtFindStart = start;
        txtFindEnd = start + length;
        txtWebTopOffset = start;
        int chunk = txtBook.ChunkAt(start);
        if (chunk == epubChapterIndex && epubDocReady)
        {
            RunEpubScript($"window.__clrTxtSel && window.__clrTxtSel({start},{length});");
            RefreshNavPosition();
            return;
        }
        txtPendingSelect = (start, length);
        epubRestorePermille = null;
        ShowEpubChapter(chunk);
    }

    /// <summary>txt 웹 화면 맨 위 줄의 원본 위치를 읽어 둔다(읽던 자리 저장 · 탐색 막대 · 글꼴 바꾼 뒤 제자리 찾기에 쓴다).</summary>
    private async void QueryTxtTop()
    {
        if (!txtWeb || epubView?.CoreWebView2 == null || positionQueryBusy || !epubDocReady || txtPendingOffset != null) return;
        positionQueryBusy = true;
        try
        {
            string json = await epubView.CoreWebView2.ExecuteScriptAsync("window.__clrTxtTop ? window.__clrTxtTop() : -1");
            if (txtWeb && epubDocReady && int.TryParse(json, out int v) && v >= 0) txtWebTopOffset = Math.Min(v, fullText.Length);
        }
        catch { /* 문서가 바뀌는 중이면 이번 값은 건너뛴다 */ }
        finally { positionQueryBusy = false; }
        RefreshNavPosition();
    }

    /// <summary>글꼴 · 여백 · 읽기 방식이 바뀌어 다단 배치를 새로 해야 할 때, 지금 문서를 다시 불러오되 읽던 자리 근처에 머문다.</summary>
    private void ReloadEpubKeepingPlace()
    {
        if (txtWeb) { ShowTxtWebAt(txtWebTopOffset, reload: true); return; }
        epubRestorePermille = epubRatioPermille > 0 ? epubRatioPermille : null;
        ShowEpubChapter(epubChapterIndex);
    }

    /// <summary>작품 폴더의 "참고 자료" 폴더 안 파일인가.</summary>
    private bool IsReferenceFile(string? path) =>
        InPip /* 탐색기 "Cafeum으로 보기"로 연 PIP 창의 문서는 어느 폴더의 파일이든 참고 자료처럼 다룬다(참고 자료 서식창 · 읽기 전용) */ || !string.IsNullOrEmpty(path) && string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), NovelStore.ReferenceFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>수정하기를 켜면: 웹 화면에서 보던 자리 그대로 텍스트 상자(mainEditor)로 넘어간다.</summary>
    private void LeaveTxtWebForEditing()
    {
        int offset = txtWebTopOffset;
        StopTts();
        txtWeb = false;
        if (IsReferenceFile(currentPath)) { directEdit = true; savedSnapshot = fullText; } // 참고 자료: 고치는 동안만 자동 저장
        SetContentMode(ContentKind.Txt);
        mainEditor.Text = fullText;
        GoToOffset(offset);
    }

    /// <summary>수정을 마치면: 고친 글로 챕터를 다시 나누고 웹 화면으로 돌아온다(보던 자리 유지).</summary>
    private void ReturnToTxtWeb()
    {
        int offset = TxtTopOffset();
        if (IsReferenceFile(currentPath)) { FlushAutoSave(); directEdit = false; } // 참고 자료: 마치면 저장하고 다시 읽기 전용
        chapters = TxtChapterSplitter.Split(fullText, Path.GetFileNameWithoutExtension(currentPath ?? ""), TxtSplitOptions.From(settings));
        txtWeb = true;
        txtBook = null;
        SetContentMode(ContentKind.Txt);
        mainEditor.Text = "";
        ShowTxtWeb(offset);
    }

    /// <summary>File.ReadAllText와 결과는 같지만, 청크 단위로 읽으면서 파일 크기 대비 읽은 바이트로
    /// 진행률을 보고한다. 큰 txt(수십~수백 MB 소설 등)를 열 때 게이지를 보여주려고 둔 것이다.</summary>
    private static string ReadAllTextWithProgress(string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, FileOptions.SequentialScan);
        long total = fs.Length;
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sb = new StringBuilder(total > 0 ? (int)Math.Min(total, int.MaxValue) : 0);
        char[] buffer = new char[1 << 16];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sb.Append(buffer, 0, read);
            // fs.Position은 StreamReader 내부 버퍼링 때문에 실제 디코딩 진행과 정확히 비례하진 않지만,
            // 게이지용 대략치로는 충분하다.
            if (total > 0) progress?.Report((double)fs.Position / total);
        }
        return sb.ToString();
    }

    private async void SaveFile(bool forceDialog)
    {
        if (epubMode && !txtWeb) { await SaveEpubFile(forceDialog); return; }

        string? path = currentPath;
        // zip 안에서 꺼내 보는 소설은 currentPath가 zip 파일이라, 그대로 저장하면 zip이 글자 파일로 덮어써진다 — 항상 새 이름으로.
        // 한글 문서에서 뽑은 글도 마찬가지로 원본(.hwp)을 글자 파일로 덮어쓰면 안 되니 txt로 "다른 이름으로"만.
        if (txtFromZip || hwpSource) forceDialog = true;
        if (forceDialog || string.IsNullOrEmpty(path))
        {
            using var dlg = new SaveFileDialog { Filter = "텍스트 파일 (*.txt)|*.txt|모든 파일|*.*" };
            if (dlg.ShowDialogOver(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }

        // 페이지 모드에서는 mainEditor.Text가 현재 페이지 조각뿐이니, 그럴 땐 fullText(전체)를 저장한다.
        string content = pageMode || txtWeb ? fullText : mainEditor.Text;
        try
        {
            txtSaveEncoding = ChooseSaveEncoding(content);
            SafeFile.WriteAllText(path!, ToCrlf(content), txtSaveEncoding);
            if (directEdit) savedSnapshot = content;
            currentPath = path;
            txtFromZip = false; // 이제 진짜 txt 파일로 저장됐다
            hwpSource = false;
            Text = $"{Path.GetFileName(path)} — {AppShared.AppDisplayName}";
            statusLabel.Text = $"저장했습니다: {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"저장에 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 지금 보고 있는 챕터의 편집 내용을 반영한 뒤, 풀어둔 폴더 전체를 다시 zip(epub)으로 묶는다.
    /// 다른 챕터에서 이미 편집했던 내용은 챕터를 넘어갈 때마다 이미 파일에 반영돼 있어서 그대로 같이 묶인다.
    /// </summary>
    private async Task SaveEpubFile(bool forceDialog)
    {
        if (epubBook == null || epubExtractDir == null)
        {
            MessageBox.Show(this, "저장할 epub이 열려 있지 않습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string? path = currentPath;
        if (forceDialog || string.IsNullOrEmpty(path))
        {
            using var dlg = new SaveFileDialog { Filter = "EPUB (*.epub)|*.epub" };
            if (dlg.ShowDialogOver(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }

        try
        {
            if (epubEditable) await FlushEpubChapterEdits();
            RepackEpub(epubExtractDir, path!);
            currentPath = path;
            Text = $"{epubBook.Title} — {AppShared.AppDisplayName}";
            statusLabel.Text = $"저장했습니다: {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"저장에 실패했습니다: {ex.Message}", "오류",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>풀어둔 epub 폴더를 다시 zip으로 묶는다. epub 규격상 mimetype은 압축 없이 맨 처음 항목이어야 한다.</summary>
    private static void RepackEpub(string extractDir, string targetPath)
    {
        // 원본 epub을 먼저 지우고 새로 만들면 만드는 도중 실패했을 때 책이 사라진다 — 임시 파일에 다 만든 뒤 바꿔치기한다.
        SafeFile.WriteAtomic(targetPath, tmp =>
        {
            using var zip = System.IO.Compression.ZipFile.Open(tmp, System.IO.Compression.ZipArchiveMode.Create);

            string mimetypePath = Path.Combine(extractDir, "mimetype");
            if (File.Exists(mimetypePath))
            {
                var entry = zip.CreateEntry("mimetype", System.IO.Compression.CompressionLevel.NoCompression);
                using var entryStream = entry.Open();
                using var src = File.OpenRead(mimetypePath);
                src.CopyTo(entryStream);
            }

            foreach (var file in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(extractDir, file).Replace('\\', '/');
                if (rel == "mimetype") continue;
                zip.CreateEntryFromFile(file, rel, System.IO.Compression.CompressionLevel.Optimal);
            }
        });
    }
}
