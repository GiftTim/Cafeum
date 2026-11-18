namespace Cafeum;

/// <summary>
/// 그릴 때마다 Font를 새로 만들지 않고 (글꼴 이름, 크기, 모양)마다 하나씩 만들어 두고 다시 쓰는 작은 보관함.
/// 직접 그리는 컨트롤이 필드로 하나 들고 있다가 Dispose에서 함께 없앤다. 꺼낸 Font는 이 보관함이 주인이므로 호출한 쪽이 없애지 않는다.
/// </summary>
internal sealed class FontCache : IDisposable
{
    private readonly Dictionary<(string Family, float Size, FontStyle Style), Font> fonts = new();

    public Font Get(string family, float size, FontStyle style = FontStyle.Regular)
    {
        var key = (family, size, style);
        if (!fonts.TryGetValue(key, out var font)) fonts[key] = font = new Font(family, size, style);
        return font;
    }

    public void Dispose()
    {
        foreach (var f in fonts.Values) f.Dispose();
        fonts.Clear();
    }
}
