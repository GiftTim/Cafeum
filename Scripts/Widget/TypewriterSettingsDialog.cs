namespace Cafeum;

/// <summary>
/// 타입라이터 설정 창(위젯 모음의 [타입라이터] 카드 톱니바퀴 > 타입라이터 설정…).
/// 지금은 "쓰는 문단 밖 화면 흐리게 하기" 하나만 켜고 끈다. 끄면 쓰는 줄의 띠만 남는다.
/// </summary>
internal sealed class TypewriterSettingsDialog : Form
{
    private readonly CheckBox dim;

    /// <summary>[확인]을 눌렀을 때의 값: 쓰는 문단 밖 화면을 흐리게 할지.</summary>
    public bool Dim => dim.Checked;

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public TypewriterSettingsDialog(bool currentDim)
    {
        Text = "타입라이터 설정";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font(NovelStore.UiFontFamily, 10f);
        ClientSize = new Size(S(420), S(176));

        dim = new CheckBox { Text = "쓰는 문단 밖 화면 흐리게 하기", AutoSize = true, Checked = currentDim, Location = new Point(S(20), S(22)) };
        var hint = new Label
        {
            Text = "타입라이터를 켰을 때 쓰는 문단 밖의 글을 흐리게 가려 줍니다.\n끄면 쓰는 줄의 띠만 남고 나머지 글은 그대로 보입니다.",
            AutoSize = false, Location = new Point(S(20), S(58)), Size = new Size(S(384), S(52)),
        };
        var ok = new Button { Text = "확인", Location = new Point(S(294), S(124)), Size = new Size(S(110), S(36)), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "취소", Location = new Point(S(174), S(124)), Size = new Size(S(110), S(36)), DialogResult = DialogResult.Cancel };
        Controls.AddRange(new Control[] { dim, hint, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
        Themes.ApplyChrome(this, AppShared.LibraryTheme);
        Shown += (_, _) => { Activate(); dim.Focus(); };
    }
}
