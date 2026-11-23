using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cafeum;

/// <summary>
/// PDF 보기 화면. (MainForm의 한 조각 — 클래스 설명은 MainForm.cs)
/// </summary>
public partial class MainForm
{
    // ───────────────────────── PDF ─────────────────────────

    /// <summary>
    /// PDF를 연다. 야간 모드가 꺼져 있으면 예전처럼 웹뷰가 PDF 파일 자체를 연다. 켜져 있으면 PDF를 담은 얇은 웹 페이지를
    /// 만들어 화면 전체에 색 반전(invert + hue-rotate)을 걸어서, 흰 종이가 검게·검은 글씨가 희게 보이게 한다.
    /// </summary>
    private void NavigatePdf(string path)
    {
        var core = pdfView!.CoreWebView2;
        if (!settings.PdfDarkMode)
        {
            core.Navigate(new Uri(path).AbsoluteUri);
            return;
        }

        // 웹 페이지에서 로컬 파일을 file://로 직접 못 가리키니, PDF가 있는 폴더를 가상 호스트 이름으로 연결해서 쓴다.
        core.SetVirtualHostNameToFolderMapping("coffee-pdf.local", Path.GetDirectoryName(path)!, CoreWebView2HostResourceAccessKind.Allow);
        string url = "https://coffee-pdf.local/" + Uri.EscapeDataString(Path.GetFileName(path));
        core.NavigateToString(
            "<!DOCTYPE html><html><body style=\"margin:0;background:#000;overflow:hidden\">" +
            $"<embed src=\"{url}\" type=\"application/pdf\" style=\"width:100vw;height:100vh;border:0;filter:invert(1) hue-rotate(180deg);\">" +
            "</body></html>");
    }
}
