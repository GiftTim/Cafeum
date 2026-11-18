namespace Cafeum;

/// <summary>
/// 대화상자를 띄우는 길. 주인 창이 "항상 위"(서재의 전체 화면은 항상 위도 함께 건다)이면 그 위에 뜨는 대화상자도 항상 위로 만든다.
/// 그러지 않으면 대화상자가 항상 위인 주인 창 뒤에 가려져서, 눌러도 아무것도 안 뜬 것처럼 보인다.
/// </summary>
internal static class DialogExtensions
{
    public static DialogResult ShowDialogOver(this Form dialog, IWin32Window? owner)
    {
        // 읽기 화면처럼 창 안의 탭(TopLevel=false)이 주인이면 그 탭이 들어 있는 실제 창을 본다
        var top = owner is Control c ? (c.TopLevelControl as Form ?? c.FindForm()) : owner as Form;
        if (top is { TopMost: true }) dialog.TopMost = true;
        return dialog.ShowDialog(owner);
    }

    /// <summary>파일 · 폴더 고르기 창은 시스템이 주인 창 위에 띄우므로 그대로 연다(부르는 쪽을 한 모양으로 맞추려고 둔다).</summary>
    public static DialogResult ShowDialogOver(this CommonDialog dialog, IWin32Window? owner) => dialog.ShowDialog(owner);
}
