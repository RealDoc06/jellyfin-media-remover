using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.MediaRemover.WebIntegration;

/// <summary>Adds the voting client only to the server-hosted Jellyfin Web shell.</summary>
public sealed class VotingWebStartupFilter(IServerConfigurationManager configuration) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // This filter runs outside Jellyfin's BaseUrl map, before the static-file middleware.
        var baseUrl = configuration.GetNetworkConfiguration().BaseUrl;
        app.Use(handler => new VotingWebMiddleware(handler, baseUrl).InvokeAsync);
        next(app);
    };
}
