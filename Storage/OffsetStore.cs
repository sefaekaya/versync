using System.Text.Json;

namespace LyricsSync.Probe.Storage;

/// <summary>
/// Şarkı bazlı senkron ofsetleri (ms). Pozitif değer = sözler daha erken gösterilir.
/// %LOCALAPPDATA%\LyricsSync\offsets.json içinde saklanır.
/// </summary>
public sealed class OffsetStore
{
    private readonly Dictionary<string, int> _offsets;
    private readonly object _gate = new();

    public OffsetStore()
    {
        _offsets = Load();
    }

    public int Get(string trackKey)
    {
        lock (_gate) return _offsets.TryGetValue(trackKey, out var v) ? v : 0;
    }

    public int Adjust(string trackKey, int deltaMs)
    {
        lock (_gate)
        {
            var value = Math.Clamp(Get(trackKey) + deltaMs, -10_000, 10_000);
            if (value == 0) _offsets.Remove(trackKey);
            else _offsets[trackKey] = value;
            Save();
            return value;
        }
    }

    public void Reset(string trackKey)
    {
        lock (_gate)
        {
            if (_offsets.Remove(trackKey)) Save();
        }
    }

    private static Dictionary<string, int> Load()
    {
        try
        {
            if (File.Exists(AppPaths.OffsetsFile))
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(AppPaths.OffsetsFile))
                       ?? new Dictionary<string, int>();
        }
        catch
        {
            // bozuk dosya: sıfırdan başla
        }
        return new Dictionary<string, int>();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.OffsetsFile, JsonSerializer.Serialize(_offsets));
        }
        catch
        {
            // yazılamazsa yalnızca bu oturumda geçerli olur
        }
    }
}
