# Testing

Tests run against synthetic data and HTTP provider fixtures. The integration environment starts real Jellyfin containers, but it does not use a live Radarr, Sonarr, or Seerr database or delete real media files.

## Release verification

Version **1.3.0** passed the following checks for **both Jellyfin 10.11.11 and 12.1.0**:

| Suite | Checks per target | Covers |
| --- | ---: | --- |
| Unit tests | 274 | Provider contracts, progress, request owners, removal recovery, voting/storage, web integration |
| Series integration | 24 | Real authorization, all-user progress, requests, preview, confirmation, provider flags, restart/retry |
| Legacy voting integration | 18 | Private progress, user restrictions, anonymous totals, Home, vote persistence |
| All-media voting integration | 14 | Mixed media, whole-series resolution, playlist privacy, no provider or watched-state changes |
| Movie integration | 18 | Radarr/Seerr matching, movie/TV namespace separation, settings compatibility, removal and retry |

Both release packages built without warnings. A Jellyfin 10.11.11 upgrade from plugin 1.2.1 preserved the series vote, its timestamp, and the Home-dismissal preference.

Browser verification covered native menu voting, searchable all-media voting, Home vote/undo and dismiss/restore, movie/series admin review, provider preview, exact-title gating, and mobile layout. [Screenshots](screenshots.md) show the real interface with synthetic data.

## Unit and syntax checks

Requires the [.NET prerequisites](development.md#get-the-source). Run sequentially from the repository root:

```sh
dotnet test --configuration Release
dotnet test --configuration Release -p:JellyfinVersion=12.1.0
node --check src/Jellyfin.Plugin.MediaRemover/Web/configuration.js
node --check src/Jellyfin.Plugin.MediaRemover/Web/voting.js
```

The tests are in `tests/Jellyfin.Plugin.MediaRemover.Tests/`:

- `ProviderGatewayTests.cs` and `SeerrRequestTests.cs`: URL base paths, credentials, exact identities, malformed data, redirects, timeouts, provider failures, and absent records.
- `CoreTests.cs` and `SeriesRequestServiceTests.cs`: progress, linked requesters, previews, repeated confirmation, changed identities, partial failure, restart recovery, and journal errors.
- `VotingTests.cs` and `VotingStoreTests.cs`: nomination rules, visibility, ranking, duplicate votes, persistence, and unreadable storage.
- `WebIntegrationTests.cs`: safe runtime injection, bounded buffering, and response handling.

## Isolated integration environment

Requires **Docker with Compose**, **Python 3**, and **ffmpeg** on the execution host. No third-party Python packages are needed. The harness requires the local default Docker Unix socket and refuses a remote Docker context or `DOCKER_HOST` override.

It creates only the `jmr-test-jellyfin` and `jmr-test-providers` containers, a dedicated Compose network, and `.dev/<version>/` files. Ports bind to loopback. Run one Jellyfin version at a time; each version keeps a separate database directory.

### Run all suites

Build and run the series suite first. It initializes the server, generated series, user accounts, simulated provider connections, and viewing progress:

```sh
dotnet build --configuration Release
python3 scripts/integration.py run --jellyfin 10.11.11
```

Then run voting and movie checks in this order:

```sh
python3 scripts/voting_integration.py --jellyfin 10.11.11
python3 scripts/all_media_voting_integration.py --jellyfin 10.11.11 --prepare
python3 scripts/all_media_voting_integration.py --jellyfin 10.11.11
python3 scripts/movie_integration.py --jellyfin 10.11.11
```

`--prepare` generates movie/video files, silent music and audiobook files, an EPUB, photos, a collection, and public/private playlists. It also saves a vote/preference checkpoint. Movie tests require these fixtures.

For the second target, build with `-p:JellyfinVersion=12.1.0` and use `--jellyfin 12.1.0` on every integration command. Switching the version replaces only the dedicated test containers; it does not reuse an incompatible database.

Keep admin browser pages closed while voting checks compare provider request logs. An open admin page can independently fetch requester information and change those logs.

### Reuse or update the environment

To initialize the environment without running the series checks:

```sh
python3 scripts/integration.py setup --jellyfin 10.11.11
```

To rerun the series checks against the running environment:

```sh
python3 scripts/integration.py check --jellyfin 10.11.11
```

To install a particular compiled assembly, use `--plugin`:

```sh
python3 scripts/integration.py setup --jellyfin 10.11.11 \
  --plugin artifacts/jellyfin-10.11.11/Jellyfin.Plugin.MediaRemover.dll
```

`setup` and `run` stop the test Jellyfin container before copying the DLL. Never overwrite an assembly while its server is running.

### Check persistence and upgrades

For a separate legacy-vote restart check, run the voting suite, restart the test server, then verify its saved checkpoint:

```sh
python3 scripts/voting_integration.py --jellyfin 10.11.11
docker restart jmr-test-jellyfin
```

Wait until Jellyfin is ready to serve requests, then run:

```sh
python3 scripts/voting_integration.py --jellyfin 10.11.11 --verify-persistence
```

For an upgrade check, run `all_media_voting_integration.py --prepare` while the old plugin is installed. Build/install the new assembly with `integration.py setup`, then run the all-media suite **without preparing again**. It checks the original vote timestamp and Home preference survived the change.

### Inspect in a browser

The harness leaves its environment running:

| Resource | Location on the execution host |
| --- | --- |
| Jellyfin | `http://127.0.0.1:18966` |
| Simulated providers | `http://127.0.0.1:18967` |
| Test accounts and session tokens | `.dev/<version>/auth.json` |
| Jellyfin data and generated media | `.dev/<version>/` |

The login file is readable only by its owner and ignored by Git. Do not reuse these credentials for a real server.

If Docker runs on a remote development machine, forward the port from your own computer:

```sh
ssh -L 18966:127.0.0.1:18966 user@development-host
```

Then open `http://127.0.0.1:18966` locally. The media and provider records are synthetic, so you can exercise the full confirmation flow in this test environment.

### Stop the environment

```sh
python3 scripts/integration.py stop
```

This removes the test containers and network. `.dev/` files remain for later runs and are ignored by Git.

## What integration tests do not prove

The provider service implements deterministic Radarr, Sonarr, and Seerr API responses; it does not run those applications. Tests validate the plugin's requests and recovery behavior, including file-deletion flags, but do not validate a provider's own filesystem permissions, recycle bin, download client, or library synchronization.

The voting browser is injected into server-hosted Jellyfin Web. Browser checks do not imply support for separately hosted clients or native mobile/TV apps.

[Back to the README](../README.md)
