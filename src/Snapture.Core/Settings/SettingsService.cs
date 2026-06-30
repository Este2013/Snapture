using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snapture.Core.Settings;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> and resolves runtime paths. Tolerant of
/// a missing or corrupt settings file: it falls back to defaults rather than
/// throwing, because this sits in the startup path.
/// </summary>
public sealed class SettingsService
{
    private const string AppFolderName = "Snapture";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _settingsPath;
    private readonly object _gate = new();

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, AppFolderName);
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");
    }

    /// <summary>Raised after settings are saved, with the new snapshot.</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    public AppSettings Current { get; private set; } = new();

    /// <summary>The directory recordings are saved to, creating it if needed.</summary>
    public string ResolveLibraryFolder()
    {
        var folder = Current.LibraryFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            folder = Path.Combine(pictures, AppFolderName);
        }

        try { Directory.CreateDirectory(folder); }
        catch { /* surfaced later when the encoder tries to write */ }

        return folder;
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded is not null)
                        Current = loaded;
                }
            }
            catch
            {
                // Corrupt/unreadable file -> keep defaults. Don't crash startup.
                Current = new AppSettings();
            }

            return Current;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Current = settings;
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                // Write to a temp file then move, so a crash mid-write can't
                // corrupt the existing settings.
                var tmp = _settingsPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _settingsPath, overwrite: true);
            }
            catch
            {
                // Best-effort persistence; in-memory Current is still updated.
            }
        }

        SettingsChanged?.Invoke(this, settings);
    }
}
