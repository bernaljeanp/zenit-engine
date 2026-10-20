using System.Text.Json;

// The user's settings, %LOCALAPPDATA%\Zenit\settings.json: the app writes them, the Engine (same user, elevated) reads
// them. Compiled into both projects. Language: null follows Windows' display language.
record Settings(bool AutoPowerProfile = true, string? Language = null)
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zenit");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static Settings cached = new();
    static DateTime stamp;

    // Re-read only when the file changed: the game watcher asks every few seconds.
    public static Settings Current
    {
        get
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(FilePath); // a fixed 1601 date while the file doesn't exist
                if (written != stamp)
                {
                    cached = File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : new();
                    stamp = written;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { } // keep the last good settings
            return cached;
        }
    }

    public static Settings Parse(string json) => JsonSerializer.Deserialize<Settings>(json) ?? new();

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}
