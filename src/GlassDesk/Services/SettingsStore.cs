using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlassDesk.Models;

namespace GlassDesk.Services;

public sealed class SettingsStore
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlassDesk",
        "settings.json");

    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public GlassDeskSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new GlassDeskSettings();
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<GlassDeskSettings>(json, _options) ?? new GlassDeskSettings();
        }
        catch
        {
            return new GlassDeskSettings();
        }
    }

    public void Save(GlassDeskSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, _options);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsPath, true);
    }
}
