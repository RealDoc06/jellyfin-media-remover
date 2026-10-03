# How Media Remover works

Media Remover has two separate workflows: users leave advisory votes, and administrators explicitly review and confirm provider removal. Voting never calls the providers, deletes media, changes watched state, or creates a removal operation.

## Voting and access

Supported voting targets are movies, series, videos, music videos, audio tracks, music albums, audiobooks, books, photos, photo albums, collections, and playlists. Episodes and seasons resolve to the **whole series** and share that series vote. The selected episode/season and its parent series must both be visible to the voter.

Users can vote once per item and withdraw their vote. Repeating an existing vote does not create another vote or reset its timestamp. All personal voting routes require a signed-in, enabled Jellyfin account. Server API keys cannot cast votes.

The searchable voting list respects the user's library access, parental restrictions, and playlist privacy. Users see their own progress and anonymous totals. The admin summary identifies voters and their vote dates for media the administrator can access. Votes from deleted/disabled users and media no longer present in Jellyfin are excluded from the summary.

Home shows at most three items nominated by **other enabled users**, ranked by vote count. Items with only the current user's vote are excluded. Dismissal is stored per account and applies across devices; **Show on Home** in the voting browser restores it.

Votes and preferences are written atomically to `<Jellyfin data directory>/media-remover/votes.json`. An unreadable file causes voting to fail instead of silently discarding saved data. Version 1.3.0 retains existing series votes, timestamps, and preferences. Legacy series endpoints remain available for cached older clients.

## What viewing progress means

For a movie, “watched” uses that movie's Jellyfin played state.

For a series, it means every **currently indexed, non-virtual, non-special episode** is marked played for the user. It does not establish that every aired episode is in the library or that the series has finished airing. Empty series are never considered watched. Duplicate versions share an episode count; files spanning multiple numbered episodes count each episode.

Partial playback and last-played dates come from Jellyfin user data. Manually marking an item played affects the result. Other media uses the same personal progress data with appropriate watched, played, read, or viewed wording.

The admin movie/series view includes every account, including disabled accounts, against the same library inventory. This is context for a decision, not an automatic deletion rule or a guarantee nobody wants to keep the media.

## Request owners

Request owners come from Jellyseerr / Seerr's current request records. Multiple requesters appear together; repeated requests by the same account share one name. Deleted or missing requester records appear as unknown. A provider error shows **Requests unavailable** without hiding Jellyfin viewing progress.

A requester's progress is linked through Seerr's exact `jellyfinUserId`, never a matching name or email. A local Seerr account without that link still appears as a requester, with **No linked Jellyfin user** in its detail view. Removed request records cannot be reconstructed, and viewing outside Jellyfin is not tracked.

## Provider matching

| Service | Matching identity | Supported media |
| --- | --- | --- |
| Radarr | TMDB ID | Movies |
| Sonarr | TVDB ID | Whole series |
| Jellyseerr / Seerr | TMDB ID **and media type** | Movies and TV |

Movie and TV IDs occupy separate namespaces. Names are never used to match records. Missing IDs, conflicting identities, duplicate provider matches, authentication failures, and redirects stop the operation. Correct the Jellyfin metadata or provider connection before trying again.

The plugin uses HTTP APIs rather than provider databases or direct filesystem access. Provider contracts target the Radarr/Sonarr v3 APIs and Jellyseerr 2.7.3 / Seerr `/api/v1`. Configured URLs must be reachable from the Jellyfin server, including any reverse-proxy base path.

## Preview and confirmation

A preview describes the exact provider records, path, and options to be used. It expires after ten minutes and belongs to the administrator who created it. Changing provider settings invalidates pending previews; changing removal options requires a new preview.

Execution requires the exact title. The plugin rechecks provider identity and the Radarr/Sonarr path immediately before deletion. The selected media manager runs first; Seerr runs only after that step succeeds. Seerr-only cleanup is also supported.

**Delete files is off by default.** With it disabled, provider records are removed but media remains playable in Jellyfin. Enabling it asks Radarr to delete the movie files or Sonarr to delete the **entire series**, including specials and episodes absent from the Jellyfin library.

The media manager controls filesystem permissions, recycle-bin behavior, and file-deletion processing. Jellyfin reflects changes through realtime monitoring or a later library scan; the plugin never directly deletes Jellyfin database records.

Seerr cleanup uses `DELETE /api/v1/media/{id}` to clear the media record and its current requests. It does not call Seerr's separate file-deletion endpoint. Blocklisted media must first be unblocked in Seerr. If files remain, Seerr may rediscover them on a later scan. A Radarr/Sonarr import-list exclusion does not prevent future manual Seerr requests.

## Failures and recovery

Provider APIs do not share a transaction. If one step fails after another succeeds, Removal history records the completed work. Retry requires the same administrator and title confirmation, rechecks identities, and skips completed steps. It can continue after a Jellyfin restart or after the media disappears from Jellyfin.

API keys may be corrected or rotated for retry; provider URLs must still identify the original servers. If a provider reported an error after already deleting its record, retry reconciles the now-absent record. The plugin cannot undo deletions.

History is stored at `<Jellyfin data directory>/media-remover/operations.json`, with atomic writes before and after provider mutations. A failed journal write stops subsequent steps. An unreadable journal blocks removal until repaired or restored. The dashboard shows every unfinished operation and the latest 200 completed operations.

Back up this file alongside votes and plugin configuration. It contains provider IDs, titles, paths, administrator IDs, and outcomes, but no API keys. Keys are stored in Jellyfin's standard plugin configuration XML, so protect that file and its backups.

## Web integration

The plugin embeds its HTML, JavaScript, and CSS in the assembly. Middleware adds a versioned voting script to the web shell at runtime without editing Jellyfin's web files. The admin page uses Jellyfin's native plugin configuration interface.

Voting controls are available in **server-hosted Jellyfin Web**. Separately hosted web clients and native TV/mobile apps do not load the injected UI. Refresh open web tabs after upgrading. The plugin follows Jellyfin styles, though custom themes can change its appearance.

## Implementation references

- [Radarr OpenAPI](https://github.com/Radarr/Radarr/blob/develop/src/Radarr.Api.V3/openapi.json)
- [Sonarr series API](https://github.com/Sonarr/Sonarr/blob/develop/src/Sonarr.Api.V3/Series/SeriesController.cs)
- [Jellyseerr 2.7.3 media routes](https://github.com/fallenbagel/jellyseerr/blob/v2.7.3/server/routes/media.ts)
- [Seerr media routes](https://github.com/seerr-team/seerr/blob/develop/server/routes/media.ts)
- [Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template)

[Back to the README](../README.md)
