using System.Globalization;
using System.Windows;
using WprHelper.Contracts;

namespace WprHelper.App;

public static class LocalizationService
{
    public static void Apply(LanguagePreference preference)
    {
        var language = preference == LanguagePreference.Automatic
            ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? LanguagePreference.Russian : LanguagePreference.English)
            : preference;
        var culture = language == LanguagePreference.Russian ? new CultureInfo("ru-RU") : new CultureInfo("en-US");
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        if (System.Windows.Application.Current is null) return;
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var old = dictionaries.FirstOrDefault(x => x.Source?.OriginalString.Contains("Strings.", StringComparison.OrdinalIgnoreCase) == true);
        var dictionary = new ResourceDictionary { Source = new Uri($"Resources/Strings.{culture.Name}.xaml", UriKind.Relative) };
        if (old is null) dictionaries.Insert(0, dictionary); else dictionaries[dictionaries.IndexOf(old)] = dictionary;
    }

    public static string Get(string key) => System.Windows.Application.Current?.TryFindResource(key)?.ToString() ?? key;
}
