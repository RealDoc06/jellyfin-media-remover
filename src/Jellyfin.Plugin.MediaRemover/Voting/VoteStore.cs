using System.Text.Json;

namespace Jellyfin.Plugin.MediaRemover.Voting;

// This file stores advisory votes and UI preferences only. It has no provider or media dependencies.
public sealed class VoteStore(string path) : IVoteStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public VotingState Load()
    {
        if (!File.Exists(path)) return VotingState.Empty;
        try
        {
            var state = JsonSerializer.Deserialize<VotingState>(File.ReadAllText(path), JsonOptions);
            if (state is null || state.Version != 1 || state.Votes is null || state.Preferences is null
                || state.Votes.Any(v => v is null || v.ItemId == Guid.Empty || v.UserId == Guid.Empty || v.VotedAt == default)
                || state.Preferences.Any(p => p is null || p.UserId == Guid.Empty)
                || state.Votes.Select(v => (v.ItemId, v.UserId)).Distinct().Count() != state.Votes.Count
                || state.Preferences.Select(p => p.UserId).Distinct().Count() != state.Preferences.Count)
                throw new InvalidDataException("Invalid Media Remover votes file. Restore it from backup before voting.");
            return state;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Cannot read the Media Remover votes file. Restore it from backup before voting.", ex);
        }
    }

    // Publish the complete next state only after the file has been durably flushed.
    public void Save(VotingState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
