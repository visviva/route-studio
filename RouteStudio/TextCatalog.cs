using System.Globalization;

namespace SCFarmingPathsConverter;

internal sealed class TextCatalog(Dictionary<string, string> messages)
{
    public string this[string key] => messages[key];

    public string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, this[key], arguments);
}
