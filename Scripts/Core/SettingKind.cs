namespace Cafeum;

/// <summary>
/// 설정이 바뀌었을 때 "무엇이 바뀌었는지"를 열려 있는 창들에 알리는 종류. 설정창 · 메뉴가 AppShared.NotifySettingsChanged로 알리고,
/// 읽기 화면(MainForm.OnSettingsChanged)과 볼트 창이 각자 알맞게 다시 그린다.
/// 전에는 "theme" 같은 글자였는데, 오타가 나도 컴파일러가 못 잡고 아무 일도 안 일어났다. 이제는 목록에 없는 이름이면 빌드가 막힌다.
/// </summary>
public enum SettingKind
{
    Theme,         // 화면 색 테마
    Toolbar,       // 도구 막대 · 서식창 · 아이콘 막대 항목
    Font,          // 글꼴 · 문단 서식 기본값
    FontSize,      // 글자 크기
    Margin,        // 줄 간격 · 문단 간격 · 여백
    Tts,           // 낭독 목소리 · 속도
    Pdf,           // PDF 야간 모드
    ImageLayout,   // 만화 한쪽/두쪽 보기
    ImageMargin,   // 만화 여백
    ImageReading,  // 만화 읽기 방식(페이지/페이지 터치)
    NovelReading,  // 소설 읽기 방식(스크롤/페이지/페이지 터치)
    TxtSplit,      // txt 챕터 나누기
    TxtBreak,      // txt 문장 단위 줄바꿈
    Anim,          // GIF · WebP 반복 재생
    Edge,          // 폴더의 처음 · 마지막 파일에서의 동작(받아서 다시 그릴 곳은 없고, 필요할 때 설정값을 직접 읽는다)
    LibTheme,      // 볼트 화면 색
    LibFont,       // 볼트 폴더 트리 글자 크기
}
