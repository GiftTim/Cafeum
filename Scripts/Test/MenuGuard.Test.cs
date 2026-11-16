namespace Cafeum;

// MenuGuard를 시험할 때 마우스를 흉내 내는 부분(ShotTest가 쓴다). Debug 빌드에서만 값이 채워지고, 배포용(Release)에서는 항상 null이다.
#if DEBUG
internal static partial class MenuGuard
{
    /// <summary>마우스 단추 · 위치를 흉내 낼 때만 채운다(평소에는 null).</summary>
    internal static Func<bool>? ButtonDownOverride;
    internal static Func<Point>? PositionOverride;

    private static bool? TestButtonDown() => ButtonDownOverride?.Invoke();
    private static Point? TestPosition() => PositionOverride?.Invoke();
}
#else
internal static partial class MenuGuard
{
    private static bool? TestButtonDown() => null;
    private static Point? TestPosition() => null;
}
#endif
