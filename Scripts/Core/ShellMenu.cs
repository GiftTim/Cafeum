using Microsoft.Win32;

namespace Cafeum;

/// <summary>
/// 탐색기에서 소설 · 만화 · 문서 파일을 오른쪽 클릭하면 나오는 "Cafeum으로 보기"를 현재 사용자 레지스트리(HKCU, 관리자 권한 없이)에 등록한다.
/// 누르면 이 프로그램이 그 파일을 PIP 창으로 연다(AppShared.Start). 실행할 때마다 지금 exe 경로로 맞춰 두되, 이미 맞게 돼 있으면 건드리지 않는다.
/// </summary>
internal static class ShellMenu
{
    private const string Verb = "Cafeum";
    private const string Label = "Cafeum으로 보기";

    public static void Register()
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0 || !File.Exists(exe)) return;
        string icon = $"\"{exe}\",0";
        string command = $"\"{exe}\" \"%1\"";
        foreach (var ext in NovelStore.ReferenceExtensions)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\{Verb}");
                if (key == null) continue;
                using var cmd = key.CreateSubKey("command");
                if (cmd == null) continue;
                if (key.GetValue("") as string == Label && key.GetValue("Icon") as string == icon && cmd.GetValue("") as string == command) continue; // 이미 맞다
                key.SetValue("", Label);
                key.SetValue("Icon", icon);
                cmd.SetValue("", command);
            }
            catch (Exception ex) { AppLog.Write($"우클릭 메뉴({ext})를 등록하지 못했어요", ex); } // 등록하지 못해도 프로그램은 그대로 쓴다
        }
    }
}
