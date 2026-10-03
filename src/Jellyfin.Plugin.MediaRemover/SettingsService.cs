using Jellyfin.Plugin.MediaRemover.Configuration;
using Jellyfin.Plugin.MediaRemover.Core;
using Jellyfin.Plugin.MediaRemover.Providers;

namespace Jellyfin.Plugin.MediaRemover;

public sealed record SettingsView(string SonarrUrl, string SeerrUrl, bool HasSonarrApiKey, bool HasSeerrApiKey,
    string RadarrUrl, bool HasRadarrApiKey);
public sealed record SettingsUpdate(string SonarrUrl, string SeerrUrl, string? SonarrApiKey, string? SeerrApiKey,
    bool ClearSonarrApiKey = false, bool ClearSeerrApiKey = false,
    string? RadarrUrl = null, string? RadarrApiKey = null, bool ClearRadarrApiKey = false);

public sealed class SettingsService
{
    private readonly object _gate = new();

    public ProviderOptions GetOptions()
    {
        lock (_gate)
        {
            var config = Plugin.Instance.Configuration;
            return new(config.SonarrUrl, config.SonarrApiKey, config.SeerrUrl, config.SeerrApiKey, config.RadarrUrl, config.RadarrApiKey);
        }
    }

    public SettingsView GetView()
    {
        var options = GetOptions();
        return new(options.SonarrUrl, options.SeerrUrl, options.SonarrApiKey.Length > 0, options.SeerrApiKey.Length > 0,
            options.RadarrUrl, options.RadarrApiKey.Length > 0);
    }

    public SettingsView Save(SettingsUpdate update)
    {
        lock (_gate)
        {
            var previous = GetOptions();
            Plugin.Instance.UpdateConfiguration(new PluginConfiguration
            {
                SonarrUrl = ValidateUrl(update.SonarrUrl), SeerrUrl = ValidateUrl(update.SeerrUrl),
                SonarrApiKey = Key(update.SonarrApiKey, previous.SonarrApiKey, update.ClearSonarrApiKey),
                SeerrApiKey = Key(update.SeerrApiKey, previous.SeerrApiKey, update.ClearSeerrApiKey),
                RadarrUrl = update.RadarrUrl is null ? previous.RadarrUrl : ValidateUrl(update.RadarrUrl),
                RadarrApiKey = Key(update.RadarrApiKey, previous.RadarrApiKey, update.ClearRadarrApiKey)
            });
            return GetView();
        }
    }

    private static string Key(string? supplied, string previous, bool clear)
    {
        if (clear) return "";
        if (string.IsNullOrWhiteSpace(supplied)) return previous;
        if (supplied.Any(char.IsControl)) throw new RemovalException("An API key cannot contain control characters.");
        return supplied.Trim();
    }

    private static string ValidateUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new RemovalException("Provider URLs must use HTTP or HTTPS and cannot contain credentials, a query, or a fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }
}
