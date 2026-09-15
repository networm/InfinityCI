using System.Globalization;

namespace InfinityCI.Core;

/// <summary>
/// Minimal UI-message localizer: picks between an English and a Chinese string
/// based on the current UI culture. Request localization sets it per request
/// from the Accept-Language header; background threads fall back to the server
/// default (zh). Any other language falls back to English.
/// </summary>
public static class Msg
{
    public static string T(string en, string zh) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? zh : en;
}
