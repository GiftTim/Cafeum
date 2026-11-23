namespace Cafeum;

/// <summary>
/// 에피소드 창(MainForm)이 글쓰기 위젯과 AI 도구에게 내어 주는 것들.
/// 위젯 · AI 도구는 MainForm 안을 직접 만지지 않고 이 창구만 쓴다 — 그래서 MainForm에서 떼어 따로 둘 수 있다.
/// </summary>
internal interface IWriterHost
{
    /// <summary>글 상자(문서영역).</summary>
    PageLockTextBox Editor { get; }
    /// <summary>글 상자를 감싸는 자리. 위젯이 처음 얹히는 곳이다.</summary>
    Control EditorHost { get; }
    /// <summary>떠 있는 위젯이 놓일 부모(창 맨 위 컨트롤, 없으면 EditorHost). 위젯 자리는 이 컨트롤의 오른쪽 끝 · 위쪽 기준이다.</summary>
    Control FloatingParent { get; }
    Form HostForm { get; }
    AppSettings Settings { get; }
    ReaderTheme Theme { get; }
    /// <summary>웹 화면을 거치지 않고 바로 쓰는 txt를 보고 있는가(위젯을 보여도 되는 때).</summary>
    bool TxtEditing { get; }
    /// <summary>글을 고칠 수 있는 txt(웹 화면이 아님)를 보고 있는가(AI 도구를 쓸 수 있는 때).</summary>
    bool TxtWritable { get; }
    string? CurrentPath { get; }
    /// <summary>96dpi 기준 값을 화면 배율에 맞춘다.</summary>
    int Dpi(int logical);
    void ShowToast(string text);
    /// <summary>글 상자의 커서가 화면 안에 보이게 한다.</summary>
    void EnsureCaretVisible();
    /// <summary>회차 요약을 속성에 넣은 뒤 머리와 목록을 새로 고친다.</summary>
    void SummarySaved();
    event FormClosedEventHandler FormClosed;
}
