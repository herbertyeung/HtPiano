namespace HtPiano;

internal readonly record struct UiMessage(string Key, string? Argument = null)
{
    public string Display => Argument == null ? UiLanguage.Get(Key) : UiLanguage.Format(Key, Argument);
}
