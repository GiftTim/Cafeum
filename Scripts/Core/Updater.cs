using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cafeum;

/// <summary>
/// 업데이트 확인: GitHub 저장소의 최신 릴리스(Releases)를 보고, 지금보다 새 버전이면 설치 파일(Setup.exe)을 내려받아
/// 조용히 설치한다(설치가 끝나면 Cafeum이 다시 열린다).
/// 새 버전을 낼 때: 저장소에 v1.0.2 같은 태그로 릴리스를 만들고 Install\Cafeum-Setup-1.0.2.exe를 첨부한다.
/// 안전장치: 주소는 코드에 고정(github.com, https만), 받은 파일은 GitHub이 알려 주는 SHA-256과 맞는지 확인한 뒤에만 실행한다.
/// </summary>
internal static class Updater
{
    /// <summary>"소유자/저장소" 형태(예: "계정/Cafeum"). 비어 있으면 업데이트 확인은 안내만 한다.</summary>
    public const string RepoSlug = "GiftTim/Cafeum";

    private static bool busy;

    /// <summary>업데이트로 내려받아 임시 폴더에 남은 옛 설치 파일(지금 버전 이하)을 지운다. 프로그램을 켤 때 한 번 부른다.</summary>
    public static void CleanupOldInstallers()
    {
        try
        {
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), "Cafeum*-Setup-*.exe")) // Cafeum-Setup-x.y.z.exe, 예전 이름 CafeumOffice-Setup-x.y.z.exe 둘 다
            {
                string name = Path.GetFileNameWithoutExtension(f);
                string v = name[(name.IndexOf("-Setup-", StringComparison.Ordinal) + "-Setup-".Length)..];
                if (Version.TryParse(v, out var ver) && ver <= Current) File.Delete(f);
            }
        }
        catch (Exception ex) { AppLog.Write("옛 설치 파일을 지우지 못했어요", ex); }
    }

    /// <summary>1.1.9와 1.1.9.0이 다르게 비교되지 않도록 항상 네 자리로 맞춘다.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public static Version Current => Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));

    private static readonly HttpClient http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Cafeum-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <param name="Sha256">GitHub이 알려 주는 설치 파일의 SHA-256(16진수). 응답에 없으면 null.</param>
    private sealed record Release(Version Version, string Tag, string AssetUrl, string AssetName, long Size, string? Sha256, string Notes);

    /// <summary>다운로드 주소가 github.com의 https 주소인지(응답이 이상한 주소를 가리켜도 거기서는 받지 않는다).</summary>
    private static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    public static async Task CheckAsync(IWin32Window? owner)
    {
        if (busy) return;
        busy = true;
        try
        {
            var go = MessageBox.Show(owner, $"현재 버전은 v{Format(Current)}입니다.\n\n업데이트를 확인하시겠습니까?", "업데이트 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (go != DialogResult.Yes) return;
            if (RepoSlug.Length == 0)
            {
                MessageBox.Show(owner, "업데이트를 받아 올 주소(GitHub 저장소)가 아직 정해지지 않았어요.", "업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Release? rel;
            try { rel = await FetchLatestAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "업데이트 정보를 가져오지 못했어요. 인터넷 연결을 확인해 주세요.\n\n" + ex.Message, "업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (rel == null || rel.Version <= Current)
            {
                MessageBox.Show(owner, $"이미 최신 버전이에요. (v{Format(Current)})", "업데이트 확인", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string notes = rel.Notes.Length > 0 ? "\n\n" + (rel.Notes.Length > 400 ? rel.Notes[..400] + "…" : rel.Notes) : "";
            var ask = MessageBox.Show(owner, $"새 버전 v{Format(rel.Version)}이 있어요. (지금 v{Format(Current)})\n지금 내려받아 설치할까요?\n설치하는 동안 Cafeum이 잠깐 닫혔다가 다시 열려요.{notes}",
                "업데이트 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;
            await DownloadAndInstallAsync(owner, rel);
        }
        finally { busy = false; }
    }

    private static string Format(Version v) => v.Build >= 0 && v.Revision <= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : v.ToString();

    private static async Task<Release?> FetchLatestAsync()
    {
        using var resp = await http.GetAsync($"https://api.github.com/repos/{RepoSlug}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // 아직 릴리스가 없다
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
        var ver = Normalize(parsed);
        string notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "").Trim() : "";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) continue;
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!IsGitHubUrl(url)) { AppLog.Write($"업데이트 파일 주소가 github.com의 https 주소가 아니라서 건너뛰었어요: {url}"); continue; }
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } s && s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? s["sha256:".Length..] : null;
            return new Release(ver, tag, url, name, a.GetProperty("size").GetInt64(), sha, notes);
        }
        return null;
    }

    private static async Task DownloadAndInstallAsync(IWin32Window? owner, Release rel)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetFileName(rel.AssetName)); // 이름만 쓴다(폴더 표시가 섞여 있어도 임시 폴더 밖에는 안 쓰도록)
        using var dlg = new Form
        {
            Text = "업데이트 내려받는 중", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false, ControlBox = false, AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96), ClientSize = new Size(440, 112), Font = new Font(NovelStore.UiFontFamily, 9.5f),
        };
        var label = new Label { Left = 20, Top = 16, Width = 400, Height = 30, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Text = $"v{Format(rel.Version)} 내려받는 중…" };
        var bar = new ProgressBar { Left = 20, Top = 58, Width = 400, Height = 22, Maximum = 1000 };
        dlg.Controls.Add(label);
        dlg.Controls.Add(bar);
        dlg.Show(owner as Form);
        try
        {
            using var resp = await http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? rel.Size;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(path))
            {
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) { bar.Value = (int)Math.Min(1000, done * 1000 / total); label.Text = $"v{Format(rel.Version)} 내려받는 중…  {done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB"; }
                }
            }
            if (new FileInfo(path).Length < 1_000_000) throw new IOException("내려받은 파일이 너무 작아요.");
            if (rel.Sha256 != null)
            {
                label.Text = "내려받은 파일 확인 중…";
                string actual;
                await using (var fs = File.OpenRead(path)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!actual.Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("내려받은 파일이 GitHub에 올라온 파일과 달라요(내려받다 손상됐을 수 있어요). 다시 시도해 주세요.");
            }
            else AppLog.Write("릴리스에 SHA-256 값이 없어 내려받은 파일 확인을 건너뛰었어요");
        }
        catch (Exception ex)
        {
            AppLog.Write("업데이트를 내려받지 못했어요", ex);
            dlg.Close();
            try { File.Delete(path); } catch { }
            MessageBox.Show(owner, "내려받지 못했어요.\n\n" + ex.Message, "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        dlg.Close();
        try
        {
            // 설치 프로그램이 Cafeum을 닫고 새 버전으로 덮은 뒤 다시 실행한다. 우리는 저장을 마치고 먼저 끝난다.
            Process.Start(new ProcessStartInfo(path, "/SILENT /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Write("설치 프로그램을 실행하지 못했어요", ex);
            MessageBox.Show(owner, "설치 프로그램을 실행하지 못했어요.\n\n" + ex.Message, "업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Application.Exit();
    }
}
