namespace TrueAuthor.Desktop;

static class Branding
{
    public static readonly Icon AppIcon = Load("TrueAuthor.ico");
    public static readonly Icon PausedIcon = Load("TrueAuthor-paused.ico");

    static Icon Load(string name)
    {
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream($"TrueAuthor.Desktop.Assets.{name}")
            ?? throw new InvalidOperationException($"Missing app icon: {name}");
        using var icon = new Icon(stream, new Size(32, 32));
        return (Icon)icon.Clone();
    }
}
