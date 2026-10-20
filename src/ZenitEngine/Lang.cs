using System.Globalization;

// Windows' display language picks Spanish or English; anything else falls back to English.
static class Lang
{
    public static readonly bool Es = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";
    public static string T(string en, string es) => Es ? es : en;
}
