namespace Cafeum;

/// <summary>
/// 자주 바뀌는 데이터를 바뀔 때마다 디스크에 쓰지 않고, 마지막 변경 뒤 잠깐(기본 0.4초) 조용하면 한 번만 쓰게 모아 주는 도구.
/// 화면(UI) 스레드에서만 쓴다. 프로그램이 정상으로 끝날 때는 남은 것을 바로 쓰고, Flush로 지금 바로 쓸 수도 있다(창을 닫을 때 등).
/// </summary>
internal sealed class DebouncedSave
{
    private readonly Action save;
    private readonly int delayMs;
    private System.Windows.Forms.Timer? timer;
    private bool pending;

    public DebouncedSave(Action save, int delayMs = 400)
    {
        this.save = save;
        this.delayMs = delayMs;
    }

    /// <summary>저장할 것이 생겼다. 기다리는 시간을 처음부터 다시 잰다.</summary>
    public void Request()
    {
        pending = true;
        if (timer == null)
        {
            timer = new System.Windows.Forms.Timer { Interval = delayMs };
            timer.Tick += (_, _) => Flush();
            Application.ApplicationExit += (_, _) => Flush();
        }
        timer.Stop();
        timer.Start();
    }

    /// <summary>기다리는 저장이 있으면 지금 바로 쓴다.</summary>
    public void Flush()
    {
        timer?.Stop();
        if (!pending) return;
        pending = false;
        save();
    }
}
