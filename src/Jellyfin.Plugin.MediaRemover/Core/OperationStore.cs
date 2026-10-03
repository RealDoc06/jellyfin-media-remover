using System.Text.Json;

namespace Jellyfin.Plugin.MediaRemover.Core;

public sealed class OperationStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<RemovalOperation> Load()
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<RemovalOperation>>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Empty removal journal.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Cannot read the Media Remover journal. Restore it from backup before removing media.", ex);
        }
    }

    // Replace atomically and flush before allowing the next provider mutation.
    public void Save(IEnumerable<RemovalOperation> operations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, operations, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
