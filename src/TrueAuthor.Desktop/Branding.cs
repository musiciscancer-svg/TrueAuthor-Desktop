namespace TrueAuthor.Desktop;

static class Branding
{
    public static readonly string IconData = LoadData();
    static string LoadData()
    {
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream("TrueAuthor.Desktop.Assets.TrueAuthor.ico")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return "data:image/x-icon;base64," + Convert.ToBase64String(buffer.ToArray());
    }
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
