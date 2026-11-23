using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// 만화 · 그림 읽기: zip과 그림 파일 열기 · 쪽 만들기 · 미리 읽기 · 읽는 순서 · 쪽 모양 · 슬라이드쇼 · 책 끝 동작 고르기. (MainForm의 한 조각 — 클래스 설명은 MainForm.cs)
/// </summary>
public partial class MainForm
{
    // ───────────────────────── 만화/이미지(zip 또는 이미지 파일) ─────────────────────────

    /// <summary>zip 안의 이미지들을 옵시디언 플러그인처럼 만화 보듯이 한 장씩 넘겨본다.</summary>
    private async void LoadZipImages(string path)
    {
        var cts = new CancellationTokenSource();
        loadCts = cts;
        loadingOverlay.Begin("압축 파일을 여는 중…");
        try
        {
            imageSet?.Dispose();
            var progress = new Progress<double>(loadingOverlay.Report);
            // 화별로 zip이 하나씩 들어있는 "묶음 zip"(예: 노예 부부 이야기.zip 안에 1화.zip, 2화.zip, ...)일 수도 있다.
            // 이 판별 + 실제로 안쪽 zip들을 통째로 읽어들이는 부분이 큰 파일에서 시간이 걸리는 부분이라
            // UI 스레드를 막지 않게 백그라운드에서 돌리고, 진행률은 NestedZipImageSet 쪽에서 보고해준다.
            IImageSet set = await Task.Run(() => NestedZipImageSet.LooksNested(path)
                ? new NestedZipImageSet(path, progress, cts.Token)
                : (IImageSet)new ZipImageSet(path));
            if (set.Count == 0)
            {
                set.Dispose();
                MessageBox.Show(this, "이 zip 안에서 이미지를 찾지 못했습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            imageSet = set;
            currentPath = path;
            OnBookOpened(path);
            SetContentMode(ContentKind.Image);
            RebuildImagePages();
            imageTitleBase = Path.GetFileNameWithoutExtension(path);
            ShowImagePage(SavedImagePage(path));
        }
        catch (OperationCanceledException)
        {
            statusLabel.Text = "불러오기를 취소했습니다.";
        }
        catch (Exception ex)
        {
            ShowZipOpenError(path, ex);
        }
        finally
        {
            loadingOverlay.Finish();
            if (loadCts == cts) loadCts = null;
            cts.Dispose();
            if (slideshowTimer is { Enabled: false }) StopSlideshow(); // 열기에 실패·취소돼 타이머가 세워진 채면 슬라이드쇼도 끝난 것으로
        }
    }

    /// <summary>이미지 파일 하나를 직접 열면, 같은 폴더 안의 이미지들도 같이 넘겨볼 수 있게 묶는다.</summary>
    private void LoadSingleImageFile(string path)
    {
        try
        {
            imageSet?.Dispose();
            string? dir = Path.GetDirectoryName(path);
            var set = dir != null ? FolderImageSet.FromDirectory(dir) : null;
            if (set == null || set.Count == 0)
            {
                MessageBox.Show(this, "이미지를 열지 못했습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            imageSet = set;
            int rawIndex = Math.Max(0, set.IndexOfPath(path));
            currentPath = path;
            SetContentMode(ContentKind.Image);
            RebuildImagePages();
            imageTitleBase = Path.GetFileName(path);
            JumpToImageItem(rawIndex);
        }
        catch (Exception ex)
        {
            StopSlideshow(); // OpenPath가 세워둔 슬라이드쇼 타이머를 마저 정리
            MessageBox.Show(this, $"이미지를 여는 데 실패했습니다: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 옵시디언 플러그인의 "순서 / 나누기" 그대로: 실제로 넘겨가며 볼 "페이지" 목록을 미리 만들어둔다.
    /// 화살표/버튼은 항상 이 목록을 순서대로만 넘기고, 읽는 방향에 따른 뒤집기는 여기서 미리 반영해둔다.
    /// - 왼→오: 파일 순서 그대로
    /// - 오→왼(일본식): 2장씩 묶어서 그 안에서 순서를 뒤집는다(2,1,4,3…)
    /// - 왼→오(2개로 나누기): 이미지 1장을 좌/우로 쪼개 좌→우로(1좌,1우,2좌,2우…)
    /// - 오→왼(2개로 나누기): 위와 같되 반쪽 순서를 우→좌로(1우,1좌,2우,2좌…)
    /// </summary>
    private void RebuildImagePages()
    {
        // 페이지 번호 ↔ 실제 이미지 대응이 통째로 바뀌므로, 예전 번호로 채워둔 프리페치 캐시는 여기서 버린다.
        ClearImageCache();
        var pages = new List<(int ItemIndex, ImageHalf Half)>();
        if (imageSet == null) { imagePages = pages; return; }
        int n = imageSet.Count;
        bool spread = imageOrder is ReadingOrder.LtrSpread or ReadingOrder.RtlSpread;
        bool rtl = imageOrder is ReadingOrder.Rtl or ReadingOrder.RtlSpread;

        if (spread)
        {
            for (int i = 0; i < n; i++)
            {
                if (rtl) { pages.Add((i, ImageHalf.Right)); pages.Add((i, ImageHalf.Left)); }
                else { pages.Add((i, ImageHalf.Left)); pages.Add((i, ImageHalf.Right)); }
            }
        }
        else if (imageDouble)
        {
            // 두쪽 보기: 연속한 두 장을 한 화면으로(좌우 순서는 LoadPageImage에서 읽는 방향대로 이어 붙인다). 홀수 장이면 마지막은 혼자.
            for (int i = 0; i < n; i += 2)
                pages.Add((i, i + 1 < n ? ImageHalf.Pair : ImageHalf.None));
        }
        else if (rtl)
        {
            for (int i = 0; i < n; i += 2)
            {
                if (i + 1 < n) { pages.Add((i + 1, ImageHalf.None)); pages.Add((i, ImageHalf.None)); }
                else pages.Add((i, ImageHalf.None));
            }
        }
        else
        {
            for (int i = 0; i < n; i++) pages.Add((i, ImageHalf.None));
        }
        imagePages = pages; // 다 만든 뒤에 한 번에 바꿔 끼운다
    }

    /// <summary>챕터 목록에서 실제 이미지 파일(원래 순서 기준)을 골랐을 때: 지금 순서/나누기 설정에서
    /// 그 이미지가 있는 페이지를 찾아서 연다(옵시디언 플러그인의 pageIndexForItem과 동일).</summary>
    private void JumpToImageItem(int rawIndex)
    {
        // 두쪽 보기의 한 화면은 그림 두 장(ItemIndex, ItemIndex+1)이므로 뒤쪽 그림으로도 찾을 수 있어야 한다.
        int pageIdx = imagePages.FindIndex(p => p.ItemIndex == rawIndex || (p.Half == ImageHalf.Pair && p.ItemIndex + 1 == rawIndex));
        ShowImagePage(pageIdx >= 0 ? pageIdx : 0);
    }

    /// <summary>만화 모드 제목표시줄을 반디뷰처럼 "[현재/전체] 이름 — {AppShared.AppDisplayName}"로 맞춘다.</summary>
    private void UpdateImageTitle()
    {
        if (!imageMode || imagePages.Count == 0) return;
        Text = $"[{imageIndex + 1}/{imagePages.Count}] {imageTitleBase} — {AppShared.AppDisplayName}";
    }

    private void ShowImagePage(int pageIndex)
    {
        if (imageSet == null || imagePages.Count == 0) return;
        pageIndex = Math.Clamp(pageIndex, 0, imagePages.Count - 1);
        imageIndex = pageIndex;
        var (itemIndex, half) = imagePages[pageIndex];
        try
        {
            // 미리 불러오기 캐시에 이미 있으면(대부분의 경우) 그걸 바로 쓰고, 없으면(캐시가 못 따라왔으면)
            // 예전처럼 지금 이 자리에서 동기적으로 불러온다 — 안전망이라 항상 뜨는 건 보장된다.
            Image shown = TakeFromImageCache(pageIndex) ?? LoadPageImage(pageIndex);
            imageCanvas.SetImage(shown);
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"이미지를 불러오지 못했습니다: {ex.Message}";
            return;
        }

        string halfLabel = half == ImageHalf.Left ? " [좌측]" : half == ImageHalf.Right ? " [우측]" : "";
        string name = imageSet.Name(itemIndex) + halfLabel;
        if (half == ImageHalf.Pair && itemIndex + 1 < imageSet.Count) name += " · " + imageSet.Name(itemIndex + 1);
        pageIndicatorLabel.Text = $"{pageIndex + 1} / {imagePages.Count}";
        pageScrubberBar.SetProgress(pageIndex, imagePages.Count);
        statusLabel.Text = name;
        // 반디뷰처럼 "[현재/전체]"를 그림 위 모서리와 제목표시줄에도 — 위치 막대·상태줄이 숨는
        // 페이지 터치·몰입 모드에서도 지금 몇 쪽인지 보이게.
        imageCanvas.PageInfoText = $"[{pageIndex + 1}/{imagePages.Count}]";
        UpdateImageTitle();
        RefreshNavPosition();
        imageCanvas.Focus(); // 화살표 키가 바로 먹도록

        // 슬라이드쇼 도중 직접 넘겼다면(화살표·위치 막대 등) 다음 자동 넘김까지의 대기시간을 지금부터 다시 잰다.
        if (slideshowTimer != null) { slideshowTimer.Stop(); slideshowTimer.Start(); }

        SchedulePrefetch(pageIndex);
    }

    /// <summary>이미지를 좌/우 반쪽으로 잘라낸다. disposeFull이 true면(기본값) 원본을 다 쓴 걸로 보고 정리한다
    /// — 스프레드 모드에서 양쪽을 다 잘라낼 땐 마지막 한 번만 true로 줘서 원본을 한 번만 디코딩·정리한다.</summary>
    private static Image CropHalf(Image full, ImageHalf half, bool disposeFull = true)
    {
        int halfWidth = Math.Max(1, full.Width / 2);
        var cropped = new Bitmap(halfWidth, full.Height);
        using (var g = Graphics.FromImage(cropped))
        {
            var src = half == ImageHalf.Left
                ? new Rectangle(0, 0, halfWidth, full.Height)
                : new Rectangle(full.Width - halfWidth, 0, halfWidth, full.Height);
            g.DrawImage(full, new Rectangle(0, 0, halfWidth, full.Height), src, GraphicsUnit.Pixel);
        }
        if (disposeFull) full.Dispose();
        return cropped;
    }

    /// <summary>두 그림을 나란히 이어 붙인 비트맵을 만든다(두쪽 보기). 높이가 다르면 큰 쪽 높이에 맞춰 비율대로 키우고,
    /// 두 그림은 다 쓴 뒤 정리한다.</summary>
    private static Image ComposePair(Image left, Image right)
    {
        try
        {
            int h = Math.Max(left.Height, right.Height);
            int lw = Math.Max(1, (int)Math.Round((double)left.Width * h / left.Height));
            int rw = Math.Max(1, (int)Math.Round((double)right.Width * h / right.Height));
            var result = new Bitmap(lw + rw, h);
            using var g = Graphics.FromImage(result);
            g.Clear(Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(left, new Rectangle(0, 0, lw, h));
            g.DrawImage(right, new Rectangle(lw, 0, rw, h));
            return result;
        }
        finally
        {
            left.Dispose();
            right.Dispose();
        }
    }

    /// <summary>페이지 하나를 실제로 디코딩한다(필요하면 반쪽 자르기까지). 지금 당장 보여주기용 동기 디코딩과
    /// 백그라운드 미리 불러오기가 모두 이 메서드 하나를 거친다.
    /// zip 안 압축 해제(공유 스트림을 건드리는 부분)만 락으로 감싸 한 번에 하나씩 하고, 정작 시간이 오래
    /// 걸리는 디코딩은 락 밖에서 한다 — 그래야 여러 페이지를 동시에(다른 코어에서) 미리 디코딩할 수 있다.</summary>
    private Image LoadPageImage(int pageIndex)
    {
        // 미리 불러오기 스레드에서도 불리므로, 쪽 목록과 이미지 묶음은 한 번만 집어 와서 끝까지 같은 것을 쓴다(그사이 다른 책이 열려도 섞이지 않게).
        var pages = imagePages;
        var set = imageSet ?? throw new InvalidOperationException("열려 있는 그림이 없어요.");
        var (itemIndex, half) = pages[pageIndex];
        byte[] bytes;
        lock (imageLoadLock)
        {
            bytes = set.LoadRawBytes(itemIndex);
        }
        Image full = ImageLoader.Decode(bytes, set.Name(itemIndex));
        if (half == ImageHalf.None) return full;

        if (half == ImageHalf.Pair)
        {
            // 두쪽 보기: 다음 그림도 불러와 나란히 이어 붙인다. 오른쪽→왼쪽 읽기면 앞 장이 오른쪽에 온다.
            byte[] bytes2;
            lock (imageLoadLock) { bytes2 = set.LoadRawBytes(itemIndex + 1); }
            Image second = ImageLoader.Decode(bytes2, set.Name(itemIndex + 1));
            bool rtlOrder = imageOrder is ReadingOrder.Rtl or ReadingOrder.RtlSpread;
            return rtlOrder ? ComposePair(second, full) : ComposePair(full, second);
        }

        // 2개로 나누기(스프레드) 모드: 좌/우 페이지가 원래 같은 원본 이미지 한 장에서 나온다.
        // 요청받은 쪽만 잘라내고 원본을 버리면, 곧이어(대부분 바로 다음/이전 페이지로) 반대쪽을
        // 불러올 때 같은 원본을 또 디코딩하게 된다. 그래서 여기서 반대쪽도 같이 잘라 캐시에
        // 넣어두고, 원본은 두 쪽을 다 자른 뒤에야 정리한다.
        int siblingIndex = FindSiblingSpreadPage(pages, pageIndex, itemIndex);
        if (siblingIndex >= 0)
        {
            ImageHalf siblingHalf = half == ImageHalf.Left ? ImageHalf.Right : ImageHalf.Left;
            Image siblingImg = CropHalf(full, siblingHalf, disposeFull: false);
            lock (imageCacheLock)
            {
                if (imagePageCache.ContainsKey(siblingIndex)) siblingImg.Dispose(); // 이미 딴 경로로 채워졌으면 이걸 버린다
                else imagePageCache[siblingIndex] = siblingImg;
            }
        }
        return CropHalf(full, half, disposeFull: true);
    }

    /// <summary>스프레드 모드에서 지금 페이지(pageIndex)와 같은 원본 이미지(itemIndex)를 공유하는
    /// 반대쪽 반쪽 페이지 번호를 찾는다. RebuildImagePages가 스프레드 페이지 둘을 항상 나란히
    /// 넣어두므로 바로 앞/뒤만 보면 된다. 못 찾으면(예: 목록 맨 끝) -1.</summary>
    private static int FindSiblingSpreadPage(List<(int ItemIndex, ImageHalf Half)> pages, int pageIndex, int itemIndex)
    {
        if (pageIndex + 1 < pages.Count && pages[pageIndex + 1].ItemIndex == itemIndex) return pageIndex + 1;
        if (pageIndex - 1 >= 0 && pages[pageIndex - 1].ItemIndex == itemIndex) return pageIndex - 1;
        return -1;
    }

    /// <summary>프리페치 캐시에서 꺼내온다(있으면 캐시에서 제거하고 소유권을 넘겨준다).</summary>
    private Image? TakeFromImageCache(int pageIndex)
    {
        lock (imageCacheLock)
        {
            if (imagePageCache.Remove(pageIndex, out var img)) return img;
        }
        return null;
    }

    /// <summary>지금 페이지를 기준으로 앞뒤 몇 장을 백그라운드 스레드에서 미리 디코딩해 캐시에 채워둔다.
    /// 실제로 넘기는 순간엔 이미 다 돼 있어서(캐시 적중) 압축 풀기·디코딩을 기다리지 않고 바로 뜬다.</summary>
    private void SchedulePrefetch(int centerIndex)
    {
        if (imageSet == null) return;
        var imageSetSnapshot = imageSet; // 백그라운드 작업이 끝났을 때 다른 파일로 이미 넘어갔는지 확인하는 용도

        var targets = new List<int>();
        for (int d = 1; d <= PrefetchAhead; d++) if (centerIndex + d < imagePages.Count) targets.Add(centerIndex + d);
        for (int d = 1; d <= PrefetchBehind; d++) if (centerIndex - d >= 0) targets.Add(centerIndex - d);

        foreach (int target in targets)
        {
            lock (imageCacheLock)
            {
                // 이미 캐시에 있거나, 다른 백그라운드 작업이 이미 이 페이지를 디코딩하는 중이면 또 예약하지 않는다.
                // (안 그러면 화살표를 몇 번만 눌러도 같은 페이지를 여러 번 겹쳐서 디코딩하느라 대기줄만 길어졌다.)
                if (imagePageCache.ContainsKey(target) || pendingPrefetch.Contains(target)) continue;
                pendingPrefetch.Add(target);
            }

            Task.Run(() =>
            {
                try
                {
                    // 락을 잡기 전에 먼저 지금도 필요한 페이지인지 확인 — 이미 너무 멀어졌으면 시작도 안 한다.
                    if (imageSet != imageSetSnapshot || Math.Abs(target - imageIndex) > CacheKeepRadius) return;

                    Image img;
                    try { img = LoadPageImage(target); }
                    catch { return; } // 미리 불러오기 실패는 조용히 넘어간다 — 실제로 그 페이지로 갈 때 다시 시도된다

                    // 디코딩하는 사이 다른 파일이 열렸거나 너무 멀리 넘어갔으면 그냥 버린다(메모리만 차지하니까).
                    if (imageSet != imageSetSnapshot || Math.Abs(target - imageIndex) > CacheKeepRadius)
                    {
                        img.Dispose();
                        return;
                    }

                    lock (imageCacheLock)
                    {
                        if (imagePageCache.ContainsKey(target)) { img.Dispose(); return; }
                        imagePageCache[target] = img;
                    }
                }
                finally
                {
                    lock (imageCacheLock) { pendingPrefetch.Remove(target); }
                }
            });
        }

        TrimImageCache(centerIndex);
    }

    /// <summary>지금 페이지에서 너무 멀어진 캐시는 정리한다(만화 한 편이 수백 장이어도 메모리가 계속 안 쌓이게).</summary>
    private void TrimImageCache(int centerIndex)
    {
        lock (imageCacheLock)
        {
            var stale = imagePageCache.Keys.Where(k => Math.Abs(k - centerIndex) > CacheKeepRadius).ToList();
            foreach (var k in stale)
            {
                imagePageCache[k].Dispose();
                imagePageCache.Remove(k);
            }
        }
    }

    private void ClearImageCache()
    {
        lock (imageCacheLock)
        {
            foreach (var img in imagePageCache.Values) img.Dispose();
            imagePageCache.Clear();
        }
    }

    private void ChangeImage(int delta)
    {
        if (imagePages.Count == 0) return;
        HideEdgePrompt(); // 페이지를 넘기면 끝 · 처음 안내 상자는 걷는다(또 끝이면 곧바로 다시 뜬다)
        int next = imageIndex + delta;
        if (next < 0)
        {
            if (delta < 0) HandleBookEdge(forward: false); // 첫 페이지에서 더 되돌리려 했을 때
            return;
        }
        if (next >= imagePages.Count)
        {
            if (delta > 0) HandleBookEdge(forward: true); // 마지막 페이지에서 더 오른쪽으로 넘기려 했을 때
            return;
        }
        ShowImagePage(next);
    }

    /// <summary>보기 > 보기 방식에서 고른 값을 설정에 저장하고 열려 있는 모든 창에 알린다.</summary>
    private void ChooseImageLayout(bool dbl)
    {
        settings.ImageDoublePage = dbl;
        settings.Save();
        AppShared.NotifySettingsChanged(SettingKind.ImageLayout);
    }

    /// <summary>한쪽/두쪽 보기가 바뀌었을 때: 메뉴 체크를 맞추고, 보던 그림이 있는 화면으로 페이지 목록을 다시 만든다.</summary>
    private void ApplyImageLayout()
    {
        imageDouble = settings.ImageDoublePage;
        foreach (var item in imageLayoutMenuItems) item.Checked = item.Tag is bool b && b == imageDouble;
        if (imageSet == null) return;
        int rawIndex = imagePages.Count > 0 ? imagePages[Math.Clamp(imageIndex, 0, imagePages.Count - 1)].ItemIndex : 0;
        RebuildImagePages();
        JumpToImageItem(rawIndex);
    }

    private void AddImageOrderItem(ToolStripMenuItem parent, string label, ReadingOrder order)
    {
        var item = new ToolStripMenuItem(label, null, (_, _) => SetImageReadingOrder(order))
        {
            Tag = order,
            Checked = order == imageOrder,
        };
        imageOrderMenuItems.Add(item);
        parent.DropDownItems.Add(item);
    }

    /// <summary>보기 > 순서 / 나누기를 바꾸면 페이지 목록을 다시 짜고, 지금 보던 실제 이미지가 있는 페이지로 되돌아간다.</summary>
    private void SetImageReadingOrder(ReadingOrder order)
    {
        imageOrder = order;
        foreach (var item in imageOrderMenuItems) item.Checked = item.Tag is ReadingOrder o && o == order;
        if (imageSet == null) return;
        int rawIndex = imagePages.Count > 0 ? imagePages[Math.Clamp(imageIndex, 0, imagePages.Count - 1)].ItemIndex : 0;
        RebuildImagePages();
        JumpToImageItem(rawIndex);
    }

    private void AddImageViewModeItem(ToolStripMenuItem parent, string label, ImageViewMode mode)
    {
        var item = new ToolStripMenuItem(label, null, (_, _) => SetImageViewMode(mode))
        {
            Tag = mode,
            Checked = mode == ImageViewMode.FitWindowUpscale, // ImageCanvas의 기본값과 일치
        };
        imageViewModeMenuItems.Add(item);
        parent.DropDownItems.Add(item);
    }

    private void SetImageViewMode(ImageViewMode mode)
    {
        if (mode == ImageViewMode.Custom)
        {
            int? pct = AskCustomImagePercent(imageCanvas.CustomPercent);
            if (pct == null) return; // 취소하면 지금 보기 모드를 그대로 둔다
            imageCanvas.CustomPercent = pct.Value;
        }
        foreach (var item in imageViewModeMenuItems) item.Checked = item.Tag is ImageViewMode m && m == mode;
        imageCanvas.SetViewMode(mode);
    }

    // ───────────────────────── 슬라이드쇼(만화 자동 넘김) ─────────────────────────

    private void ToggleSlideshow()
    {
        if (slideshowTimer != null) { StopSlideshow(); return; }
        if (!imageMode || imagePages.Count == 0) return;

        slideshowIntervalSeconds = Math.Clamp(settings.SlideshowSeconds, 1, 120);
        slideshowTimer = new System.Windows.Forms.Timer { Interval = slideshowIntervalSeconds * 1000 };
        slideshowTimer.Tick += (_, _) => SlideshowTick();
        slideshowTimer.Start();
        UpdateAutoPlayMenu();
        statusLabel.Text = $"슬라이드쇼 시작 ({slideshowIntervalSeconds}초 간격)";
    }

    private void StopSlideshow()
    {
        if (slideshowTimer == null) return;
        slideshowTimer.Stop();
        slideshowTimer.Dispose();
        slideshowTimer = null;
        UpdateAutoPlayMenu();
    }

    private void SlideshowTick()
    {
        if (!imageMode || imagePages.Count == 0) { StopSlideshow(); return; }
        if (imageIndex >= imagePages.Count - 1) { HandleBookEdge(forward: true); return; }
        ChangeImage(1);
    }

    private void SetSlideshowInterval(int seconds)
    {
        slideshowIntervalSeconds = seconds;
        settings.SlideshowSeconds = seconds;
        settings.Save();
        foreach (var item in slideshowIntervalMenuItems) item.Checked = item.Text == $"{seconds}초마다";
        if (slideshowTimer != null) slideshowTimer.Interval = seconds * 1000;
    }

    // ───────────────────────── 마지막 페이지에서 ─────────────────────────

    private void AddEndOfBookItem(ToolStripMenuItem parent, string label, EndOfBookAction action)
    {
        var item = new ToolStripMenuItem(label, null, (_, _) => SetEndOfBookAction(action))
        {
            Tag = action,
            Checked = action == endOfBookAction,
        };
        endOfBookMenuItems.Add(item);
        parent.DropDownItems.Add(item);
    }

    private void SetEndOfBookAction(EndOfBookAction action)
    {
        endOfBookAction = action;
        foreach (var item in endOfBookMenuItems) item.Checked = item.Tag is EndOfBookAction a && a == action;
    }
}
