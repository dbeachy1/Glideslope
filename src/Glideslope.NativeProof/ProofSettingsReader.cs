using System.IO;
using System.Text.Json;
using Glideslope.Core;

namespace Glideslope.NativeProof;

/// <summary>Re-reads the proof folder's settings.json with the same serialization convention
/// ProofSettingsSeeder wrote it with (src/Glideslope.Core/SettingsStore.cs), so a scenario can inspect
/// the persisted layout graph (Layout.Cards / Layout.Edges) after driving a gesture.</summary>
internal static class ProofSettingsReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings? TryRead(string settingsFile, Action<string> log)
    {
        try
        {
            var text = File.ReadAllText(settingsFile);
            return JsonSerializer.Deserialize<AppSettings>(text, Json);
        }
        catch (IOException exception)
        {
            log($"proof_settings_read_failed path={settingsFile} reason={exception.GetType().Name}");
            return null;
        }
        catch (JsonException exception)
        {
            log($"proof_settings_read_failed path={settingsFile} reason={exception.GetType().Name}");
            return null;
        }
    }
}
