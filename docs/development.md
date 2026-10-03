# Development

## Get the source

```sh
git clone https://github.com/RealDoc06/jellyfin-media-remover.git
cd jellyfin-media-remover
```

Install the .NET 10 SDK and Python 3. To execute the `net9.0` tests, also install the ASP.NET Core 9 runtime or the .NET 9 SDK. Node.js is used only for JavaScript syntax checks. There is no frontend dependency installation or bundler.

Docker with Compose and `ffmpeg` are needed for the optional [integration environment](testing.md#isolated-integration-environment).

## Repository layout

```text
src/Jellyfin.Plugin.MediaRemover/
├── Api/                 Authenticated admin and voting endpoints
├── Configuration/       Jellyfin plugin configuration model
├── Core/                Removal workflow, history, requests, watch progress
├── Providers/           Radarr, Sonarr, and Seerr HTTP integrations
├── Voting/              Votes, library visibility, persistence
├── Web/                 Embedded dashboard and voting UI
├── WebIntegration/      Runtime injection into server-hosted Jellyfin Web
├── JellyfinSeriesLibrary.cs
├── Plugin.cs
└── SettingsService.cs
tests/                   xUnit tests
scripts/                 Packaging and disposable integration checks
compose.test.yml         Dedicated Jellyfin and simulated-provider containers
Directory.Build.props    Default Jellyfin version, framework, compiler settings
```

The `Series` names in some core files and legacy API models predate movie support. Check their media-type handling before assuming that they only process series.

## Build a target

Jellyfin 10.11.11 is the default and targets `net9.0`:

```sh
dotnet build --configuration Release
```

Jellyfin 12.1.0 targets `net10.0`:

```sh
dotnet build --configuration Release -p:JellyfinVersion=12.1.0
```

The DLL appears under `src/Jellyfin.Plugin.MediaRemover/bin/Release/<framework>/`. Run builds and tests for different targets **sequentially**: both use the project's intermediate restore output. Allow restore when switching targets instead of using `--no-restore`.

Compiler warnings are errors. Nullable reference types are enabled, and Jellyfin dependencies are compile-time references excluded from the release ZIP.

## Edit the interface

- `Web/voting.js` and `Web/voting.css`: Home, voting browser, sidebar entry, native-menu actions.
- `Web/configuration.html` and `Web/configuration.js`: admin library, provider settings, vote summary, removal review and history.
- `WebIntegration/`: how the embedded voting client is loaded into the server-hosted web application.

All web files are embedded resources. Saving a JavaScript or CSS file does not change a running server: rebuild the assembly, install it while Jellyfin is stopped, then restart and reload the page.

Voting assets use the **assembly version** in their URLs and are served with immutable caching. When preparing a new build for distribution, update both `<Version>` and `<AssemblyVersion>` in `src/Jellyfin.Plugin.MediaRemover/Jellyfin.Plugin.MediaRemover.csproj`, keeping the three-part release version and four-part assembly version aligned. For example, `1.3.1` and `1.3.1.0`. The packaging script reads those values automatically.

When iterating on a disposable server without changing the version, clear the browser's cached assets or use its developer-tools **Disable cache** option and reload. A normal reload may continue using the old script or stylesheet.

## Run checks

```sh
dotnet test --configuration Release
dotnet test --configuration Release -p:JellyfinVersion=12.1.0
node --check src/Jellyfin.Plugin.MediaRemover/Web/configuration.js
node --check src/Jellyfin.Plugin.MediaRemover/Web/voting.js
```

Add focused tests around behavior you change: provider identity matching and failure handling, voting access and persistence, progress aggregation, or web-shell injection. Integration checks exercise the real Jellyfin authentication and library APIs. See [testing.md](testing.md) for commands and coverage.

## Try changes in Jellyfin

After building the matching target, start a disposable test server:

```sh
python3 scripts/integration.py setup --jellyfin 10.11.11
```

Open `http://127.0.0.1:18966` on the machine running Docker. The generated login is in `.dev/10.11.11/auth.json`; these local files are ignored by Git. The setup creates synthetic series media and administrator/viewer accounts. Follow the [integration guide](testing.md) to configure simulated providers and add movies, music, books, and photos.

After the next edit/build, rerun `setup`. It stops the **test** Jellyfin container before replacing its assembly, then starts it again. Do not overwrite a DLL while the server is running because .NET may memory-map it.

Use `--jellyfin 12.1.0` after building that target. Each version has a separate data directory; they share the dedicated container names and ports, so run one version at a time.

## Create installable archives

```sh
python3 scripts/package.py --jellyfin 10.11.11
python3 scripts/package.py --jellyfin 12.1.0
```

For a .NET executable outside `PATH`, set `DOTNET_BIN`, for example:

```sh
DOTNET_BIN=/path/to/dotnet python3 scripts/package.py --jellyfin 10.11.11
```

For version 1.3.0, output is:

```text
artifacts/
├── media-remover-1.3.0-jellyfin-10.11.11.zip
├── media-remover-1.3.0-jellyfin-10.11.11.zip.sha256
├── media-remover-1.3.0-jellyfin-12.1.0.zip
└── media-remover-1.3.0-jellyfin-12.1.0.zip.sha256
```

Each ZIP contains the plugin DLL, `meta.json`, and `LICENSE`. Install using the [README instructions](../README.md#install). When publishing a release, update its changelog in `scripts/package.py`, the documented version/download URLs, and any screenshots affected by visible changes. Publish both target ZIPs with their checksum files.

[Back to the README](../README.md)
