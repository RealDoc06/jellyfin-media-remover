using Jellyfin.Plugin.MediaRemover.Core;
using Jellyfin.Plugin.MediaRemover.Providers;
using Jellyfin.Plugin.MediaRemover.Voting;
using Jellyfin.Plugin.MediaRemover.WebIntegration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.MediaRemover;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpClient("MediaRemover", client => client.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<SettingsService>();
        services.AddSingleton<Func<ProviderOptions>>(sp => sp.GetRequiredService<SettingsService>().GetOptions);
        services.AddSingleton<IProviderGateway, ProviderGateway>();
        services.AddSingleton<ISeriesLibrary, JellyfinSeriesLibrary>();
        services.AddSingleton(_ => new OperationStore(Plugin.Instance.JournalPath));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RemovalService>();
        services.AddSingleton<SeriesRequestService>();
        services.AddSingleton<IVotingLibrary, JellyfinVotingLibrary>();
        services.AddSingleton<IVoteStore>(_ => new VoteStore(Plugin.Instance.VotesPath));
        services.AddSingleton(sp =>
        {
            var voting = ActivatorUtilities.CreateInstance<VotingService>(sp);
            // Settle removals that completed before votes were cleared automatically. An unreadable
            // journal or votes file must not take voting down; the next startup tries again.
            try { voting.SettleRemovals(sp.GetRequiredService<OperationStore>().Load()); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
            return voting;
        });
        services.AddTransient<IStartupFilter, VotingWebStartupFilter>();
    }
}
