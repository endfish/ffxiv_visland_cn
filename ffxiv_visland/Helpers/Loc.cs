using System;
using System.Globalization;

namespace visland.Helpers;

internal static class Loc
{
    internal enum UiLanguage
    {
        English,
        SimplifiedChinese,
    }

    private static readonly UiLanguage _currentLanguage = DetectLanguage();

    public static UiLanguage CurrentLanguage => _currentLanguage;

    public static string Tr(string english, string simplifiedChinese)
        => CurrentLanguage == UiLanguage.SimplifiedChinese ? simplifiedChinese : english;

    public static string Format(string english, string simplifiedChinese, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, Tr(english, simplifiedChinese), args);

    private static UiLanguage DetectLanguage()
    {
        var overrideLanguage = Environment.GetEnvironmentVariable("VISLAND_UI_LANG");
        if (string.Equals(overrideLanguage, "en", StringComparison.OrdinalIgnoreCase))
            return UiLanguage.English;
        if (string.Equals(overrideLanguage, "zh", StringComparison.OrdinalIgnoreCase) || string.Equals(overrideLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase))
            return UiLanguage.SimplifiedChinese;

        return CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? UiLanguage.English : UiLanguage.SimplifiedChinese;
    }
}
