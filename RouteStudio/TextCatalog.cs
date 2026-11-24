using System.Globalization;

namespace SCFarmingPathsConverter;

internal sealed record Localization(string Language, string Preference, Dictionary<string, string> Messages);

internal sealed class TextCatalog(Localization localization)
{
    public string Preference => localization.Preference;
    public CultureInfo Culture => CultureInfo.GetCultureInfo(localization.Language);
    public string this[string key] => localization.Messages[key];

    public void Apply(Localization updated) => localization = updated;

    public string Format(string key, params object[] arguments) =>
        string.Format(Culture, this[key], arguments);
}
