using System.Globalization;
using System.Threading;
using AxialSqlTools.Properties;

namespace AxialSqlTools
{
    public static class UiCultureService
    {
        public static void ApplyFromSettings()
        {
            Apply(UiSettingsStore.GetUiLanguage());
        }

        public static void Apply(string uiLanguage)
        {
            var culture = CreateCulture(uiLanguage);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            // English uses neutral Strings.resx (Culture=null). Chinese uses zh-Hans satellite.
            Strings.Culture = IsEnglish(uiLanguage) ? null : culture;
        }

        public static CultureInfo CreateCulture(string uiLanguage)
        {
            if (IsEnglish(uiLanguage))
            {
                return CultureInfo.GetCultureInfo("en");
            }
            return CultureInfo.GetCultureInfo("zh-Hans");
        }

        public static bool IsEnglish(string uiLanguage)
        {
            return string.Equals(uiLanguage, UiSettingsStore.LanguageEn, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(uiLanguage, "en-US", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
