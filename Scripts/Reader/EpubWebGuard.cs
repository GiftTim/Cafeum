using Microsoft.Web.WebView2.Core;

namespace Cafeum;

/// <summary>
/// epub 웹 화면을 "이 컴퓨터 안의 주소만" 쓰게 묶는다.
/// epub은 인터넷에서 받은 남의 웹 문서(XHTML + 스크립트)라서, 열기만 해도 외부 서버에 접속하거나(읽는 사람의 IP가 알려지고 추적 그림이 불려 온다),
/// 새 창을 띄우거나, 다른 사이트로 화면이 넘어갈 수 있다. 앱 설명서의 "내 컴퓨터 안에서만 동작한다"를 지키려고 그 길을 막는다.
/// 책 안의 글 · 그림 · 스타일은 모두 풀어 둔 폴더(file://)에 있으므로 정상 epub에는 영향이 없다.
/// </summary>
internal static class EpubWebGuard
{
    private static int blockedLogged;

    /// <param name="onLinkBlocked">책 안의 링크를 눌러 다른 주소로 가려던 것을 막았을 때(화면에 알리는 용도).</param>
    public static void Attach(CoreWebView2 core, Action? onLinkBlocked = null)
    {
        // 그림 · 스타일 · 스크립트 · 글꼴 · fetch 등 모든 요청이 지나가게 걸고, 로컬이 아니면 빈 403 응답을 대신 준다.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (IsLocal(e.Request.Uri)) return;
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
            LogBlocked(e.Request.Uri);
        };
        // 링크를 눌러 다른 주소로 가는 것, 스크립트가 주소를 바꾸는 것.
        core.NavigationStarting += (_, e) =>
        {
            if (IsLocal(e.Uri)) return;
            e.Cancel = true;
            LogBlocked(e.Uri);
            onLinkBlocked?.Invoke();
        };
        // window.open · target=_blank 같은 새 창은 열지 않는다.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            LogBlocked(e.Uri);
        };
    }

    /// <summary>이 컴퓨터 안의 주소인가(풀어 둔 파일, 빈 문서, 문서 안에서 만든 data/blob 주소).</summary>
    internal static bool IsLocal(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "file" or "about" or "data" or "blob";

    private static void LogBlocked(string uri)
    {
        if (Interlocked.Increment(ref blockedLogged) <= 20) AppLog.Write("epub이 외부 주소로 나가려는 것을 막았어요: " + (uri.Length > 200 ? uri[..200] : uri));
    }
}
