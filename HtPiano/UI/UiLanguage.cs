using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;

namespace HtPiano;

internal sealed record LanguagePack(string Id, string Name, Dictionary<string, string> Strings);

internal static class UiLanguage
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HtPiano", "language.txt");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex FormatItems = new(@"\{[^{}]*\}|[{}]", RegexOptions.CultureInvariant);
    private static ResourceDictionary? _resources;
    private static volatile LanguagePack _current = null!;

    public static IReadOnlyList<LanguagePack> Available { get; private set; } = [];
    public static LanguagePack Current => _current;

    public static string Get(string key) => Current.Strings[key];
    public static string Format(string key, params object[] args)
    {
        LanguagePack pack = Current;
        return string.Format(CultureInfo.GetCultureInfo(pack.Id), pack.Strings[key], args);
    }

    public static void Initialize()
    {
        using Stream embedded = typeof(UiLanguage).Assembly.GetManifestResourceStream("HtPiano.Languages.en.json")!;
        LanguagePack baseline = ReadPack(embedded, "en");
        var packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase) { [baseline.Id] = baseline };
        string directory = Path.Combine(AppContext.BaseDirectory, "Languages");
        try
        {
            if (Directory.Exists(directory))
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
                {
                    try
                    {
                        using var input = File.OpenRead(path);
                        var pack = ReadPack(input, Path.GetFileNameWithoutExtension(path));
                        packs[pack.Id] = pack;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                        JsonException or InvalidDataException or ArgumentException)
                    {
                        Trace.TraceWarning("Ignoring language pack {0}: {1}", path, error.Message);
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Couldn't read the language directory: {0}", error.Message);
        }

        LanguagePack english = WithFallback(packs["en"], baseline);
        Available = packs.Values.Select(pack => pack.Id.Equals("en", StringComparison.OrdinalIgnoreCase)
                ? english : WithFallback(pack, english))
            .OrderBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        string? preference = null;
        try
        {
            if (File.Exists(PreferencePath)) preference = File.ReadAllText(PreferencePath).Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Couldn't read the UI language: {0}", error.Message);
        }

        LanguagePack? selected = Available.FirstOrDefault(p => SameId(p.Id, preference));
        CultureInfo system = CultureInfo.CurrentUICulture;
        for (CultureInfo culture = system; selected == null && culture.Name.Length != 0; culture = culture.Parent)
            selected = Available.FirstOrDefault(p => SameId(p.Id, culture.Name));
        selected ??= Available.FirstOrDefault(p =>
            SameId(CultureInfo.GetCultureInfo(p.Id).TwoLetterISOLanguageName, system.TwoLetterISOLanguageName));
        Apply(selected ?? english);
    }

    private static LanguagePack ReadPack(Stream stream, string expectedId)
    {
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Language pack exceeds 1 MiB.");
        var pack = JsonSerializer.Deserialize<LanguagePack>(stream, JsonOptions);
        if (pack == null || string.IsNullOrWhiteSpace(pack.Id) || !SameId(pack.Id, expectedId) ||
            string.IsNullOrWhiteSpace(pack.Name) || pack.Strings == null)
            throw new InvalidDataException("Expected a matching id, a display name and a strings object.");

        _ = CultureInfo.GetCultureInfo(pack.Id);
        _ = XmlLanguage.GetLanguage(pack.Id);
        return pack;
    }

    private static bool SameId(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static LanguagePack WithFallback(LanguagePack translated, LanguagePack fallback)
    {
        var strings = new Dictionary<string, string>(fallback.Strings, StringComparer.Ordinal);
        foreach ((string key, string value) in translated.Strings)
        {
            if (!strings.TryGetValue(key, out string? original) || string.IsNullOrWhiteSpace(value)) continue;
            // Translators can reorder placeholders but cannot introduce new format specifiers or braces.
            if (FormatItems.Matches(original).Select(m => m.Value).Order(StringComparer.Ordinal)
                .SequenceEqual(FormatItems.Matches(value).Select(m => m.Value).Order(StringComparer.Ordinal)))
                strings[key] = value;
            else
                Trace.TraceWarning("Ignoring mismatched placeholders in {0}: {1}", translated.Id, key);
        }
        return translated with { Strings = strings };
    }

    public static void Select(LanguagePack pack)
    {
        if (ReferenceEquals(pack, Current)) return;
        Apply(pack);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, pack.Id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Couldn't save the UI language: {0}", error.Message);
        }
    }

    private static void Apply(LanguagePack pack)
    {
        // The audio worker may read error messages. Never mutate a published pack.
        _current = pack;
        var next = new ResourceDictionary { ["Lang.XmlLanguage"] = XmlLanguage.GetLanguage(pack.Id) };
        foreach ((string key, string value) in pack.Strings) next[key] = value;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (_resources == null) dictionaries.Add(next);
        else dictionaries[dictionaries.IndexOf(_resources)] = next;
        _resources = next;
    }
}
