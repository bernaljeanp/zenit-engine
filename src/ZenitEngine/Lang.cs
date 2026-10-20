using System.Globalization;

// Spanish or English: the language picked in Zenit's settings, otherwise Windows' display language (anything but
// Spanish falls back to English). Compiled into both projects.
static class Lang
{
    static readonly bool WindowsEs = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";
    public static bool Es => Settings.Current.Language switch { "es" => true, "en" => false, _ => WindowsEs };
    public static string T(string en, string es) => Es ? es : en;
}
