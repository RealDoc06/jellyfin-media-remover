#!/usr/bin/env python3
"""Real Jellyfin integration checks against disposable, contract-shaped provider fixtures.

Build the matching Release plugin, then run `python3 scripts/integration.py run`.
Jellyfin 10.11.11 is the default; use `--jellyfin 12.1.0` for that release.
Each version has its own data and credentials under `.dev/<version>/`.
The isolated environment stays running for manual review; `... stop` shuts it down.
No third-party Python packages are required. Docker Compose and ffmpeg are required.
"""

import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import Request, urlopen


ROOT = Path(__file__).resolve().parents[1]
VERSIONS = {"10.11.11": ("jellyfin/jellyfin:10.11.11", "net9.0"), "12.1.0": ("jellyfin/jellyfin:12.1", "net10.0")}
JELLYFIN_VERSION = "10.11.11"
DEV = ROOT / ".dev" / JELLYFIN_VERSION
JELLYFIN = "http://127.0.0.1:18966"
PROVIDERS = "http://127.0.0.1:18967"
KEY = "synthetic-integration-fixture-key"
TITLE = "Media Remover Test Series"
TVDB = 121361
TMDB = 1399
MOVIE_TITLE = "Voting Test Movie"
# Deliberately overlap the TV ID to exercise Seerr's distinct movie/TV namespaces.
MOVIE_TMDB = TMDB
COMPOSE = ["docker", "compose", "-p", "jmr-test", "-f", str(ROOT / "compose.test.yml")]


def request(base, path, method="GET", data=None, token=None, expected=200):
    headers = {"Accept": "application/json"}
    if token:
        headers["X-Emby-Token"] = token
    headers["Authorization"] = 'MediaBrowser Client="MediaRemover Integration", Device="Test", DeviceId="jmr-integration", Version="1.0"'
    if token:
        headers["Authorization"] += f', Token="{token}"'
    body = None if data is None else json.dumps(data).encode()
    if body is not None:
        headers["Content-Type"] = "application/json"
    try:
        with urlopen(Request(base + path, body, headers, method=method), timeout=60) as response:
            status, payload = response.status, response.read()
    except HTTPError as error:
        status, payload = error.code, error.read()
    if status != expected:
        # API response bodies can contain session tokens. Do not print them on failure.
        raise AssertionError(f"{method} {path}: expected HTTP {expected}, got {status}")
    if not payload:
        return None
    try:
        return json.loads(payload)
    except json.JSONDecodeError:
        return payload.decode()


def compose(*args):
    env = dict(os.environ, JMR_TEST_IMAGE=VERSIONS[JELLYFIN_VERSION][0], JMR_TEST_DATA_DIR=str(DEV))
    subprocess.run(COMPOSE + list(args), cwd=ROOT, env=env, check=True)


def wait_ready():
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        try:
            info = request(JELLYFIN, "/System/Info/Public")
            if isinstance(info, dict) and "StartupWizardCompleted" in info:
                return info
        except (AssertionError, URLError, TimeoutError, ConnectionResetError):
            pass
        time.sleep(1)
    raise RuntimeError("Jellyfin did not become ready in 180 seconds; inspect jmr-test-jellyfin logs.")


def prepare_media():
    for name in ["config", "cache", "plugin", "media", "media-extra"]:
        (DEV / name).mkdir(parents=True, exist_ok=True)
    series = DEV / "media" / TITLE
    series.mkdir(exist_ok=True)
    (series / "tvshow.nfo").write_text(
        f'<tvshow><title>{TITLE}</title><year>2026</year><uniqueid type="tvdb" default="true">{TVDB}</uniqueid>'
        f'<uniqueid type="tmdb">{TMDB}</uniqueid><lockdata>true</lockdata></tvshow>'
    )
    for season, episode in [(1, 1), (1, 2), (0, 1)]:
        folder = series / ("Specials" if season == 0 else "Season 01")
        folder.mkdir(exist_ok=True)
        stem = f"{TITLE} - S{season:02}E{episode:02}"
        media = folder / (stem + ".mkv")
        if not media.exists():
            subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=160x90:r=1",
                            "-t", "2", "-c:v", "mpeg4", "-y", str(media)], check=True)
        (folder / (stem + ".nfo")).write_text(
            f"<episodedetails><title>Test episode {season}.{episode}</title><season>{season}</season>"
            f"<episode>{episode}</episode><aired>2026-01-01</aired><lockdata>true</lockdata></episodedetails>"
        )


def setup(plugin):
    # Refuse to point these destructive test commands at an arbitrary host or Docker context.
    host = subprocess.check_output(["docker", "context", "inspect", "--format", "{{.Endpoints.docker.Host}}"], text=True).strip()
    if host != "unix:///var/run/docker.sock" or os.environ.get("DOCKER_HOST"):
        raise RuntimeError("Integration tests require the local default Docker Unix socket and no DOCKER_HOST override.")
    if not plugin.is_file():
        raise RuntimeError(f"Build the plugin first: missing {plugin}")
    prepare_media()
    # Stop before replacing an assembly that the .NET runtime may memory-map.
    compose("stop", "jellyfin")
    shutil.copy2(plugin, DEV / "plugin" / plugin.name)
    compose("up", "-d")
    info = wait_ready()
    if info["Version"] != JELLYFIN_VERSION:
        raise RuntimeError(f'Expected Jellyfin {JELLYFIN_VERSION}, got {info["Version"]}.')
    auth_path = DEV / "auth.json"
    if info["StartupWizardCompleted"]:
        if not auth_path.exists():
            raise RuntimeError(f"The sandbox is already initialized but {auth_path.relative_to(ROOT)} is missing.")
        return json.loads(auth_path.read_text())
    password = secrets.token_urlsafe(24)
    request(JELLYFIN, "/Startup/Configuration", "POST", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}, expected=204)
    request(JELLYFIN, "/Startup/User")
    request(JELLYFIN, "/Startup/User", "POST", {"Name": "integration-admin", "Password": password}, expected=204)
    request(JELLYFIN, "/Startup/RemoteAccess", "POST", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False}, expected=204)
    request(JELLYFIN, "/Startup/Complete", "POST", expected=204)
    admin = request(JELLYFIN, "/Users/AuthenticateByName", "POST", {"Username": "integration-admin", "Pw": password})
    viewer = request(JELLYFIN, "/Users/New", "POST", {"Name": "integration-viewer", "Password": password}, admin["AccessToken"])
    viewer_auth = request(JELLYFIN, "/Users/AuthenticateByName", "POST", {"Username": "integration-viewer", "Pw": password})
    auth = {"baseUrl": JELLYFIN, "username": "integration-admin", "password": password,
            "adminToken": admin["AccessToken"], "adminId": admin["User"]["Id"],
            "viewerToken": viewer_auth["AccessToken"], "viewerId": viewer["Id"]}
    auth_path.write_text(json.dumps(auth, indent=2))
    auth_path.chmod(0o600)
    return auth


def create_library(auth):
    token = auth["adminToken"]
    folders = request(JELLYFIN, "/Library/VirtualFolders", token=token)
    if not any(f["Name"] == "Integration TV" for f in folders):
        query = urlencode({"name": "Integration TV", "collectionType": "tvshows", "refreshLibrary": "true"})
        request(JELLYFIN, "/Library/VirtualFolders?" + query, "POST", {"LibraryOptions": {
            "PathInfos": [{"Path": "/media"}], "EnableInternetProviders": False, "EnableRealtimeMonitor": False,
            "TypeOptions": [{"Type": kind, "MetadataFetchers": [], "ImageFetchers": []} for kind in ["Series", "Season", "Episode"]],
            "MetadataSavers": [], "LocalMetadataReaderOrder": ["Nfo"], "EnableAutomaticSeriesGrouping": False,
        }}, token, expected=204)
    request(JELLYFIN, "/Library/Refresh", "POST", token=token, expected=204)
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        result = request(JELLYFIN, "/Items?Recursive=true&IncludeItemTypes=Series&Fields=ProviderIds", token=token)
        matches = [item for item in result["Items"] if item["Name"] == TITLE]
        if matches:
            series = matches[0]
            episodes = request(JELLYFIN, f'/Items?ParentId={series["Id"]}&Recursive=true&IncludeItemTypes=Episode', token=token)["Items"]
            if len(episodes) == 3 and series.get("ProviderIds", {}).get("Tmdb") == str(TMDB):
                return series, episodes
        time.sleep(1)
    raise RuntimeError("Synthetic series and its three episodes were not scanned correctly.")


def check(condition, message):
    if not condition:
        raise AssertionError(message)
    print("PASS " + message, flush=True)


def validate(auth):
    token = auth["adminToken"]
    api = lambda path, **kwargs: request(JELLYFIN, "/MediaRemover/" + path, token=token, **kwargs)
    series, episodes = create_library(auth)
    regular = sorted([e for e in episodes if e.get("ParentIndexNumber") == 1], key=lambda e: e["IndexNumber"])
    for item in episodes:
        request(JELLYFIN, f'/Users/{auth["viewerId"]}/PlayedItems/{item["Id"]}', "DELETE", token=token)
        request(JELLYFIN, f'/Users/{auth["adminId"]}/PlayedItems/{item["Id"]}', "POST", token=token)
    request(JELLYFIN, f'/Users/{auth["viewerId"]}/PlayedItems/{regular[0]["Id"]}', "POST", token=token)
    request(JELLYFIN, "/MediaRemover/Settings", expected=401)
    request(JELLYFIN, "/MediaRemover/Settings", token=auth["viewerToken"], expected=403)
    request(JELLYFIN, f'/MediaRemover/Series/{series["Id"]}/Requests', expected=401)
    request(JELLYFIN, f'/MediaRemover/Series/{series["Id"]}/Requests', token=auth["viewerToken"], expected=403)
    check(True, "anonymous and non-admin callers rejected by real Jellyfin authentication")
    detail = api("Series/" + series["Id"])
    progress = {u["name"]: u for u in detail["users"]}
    check(detail["episodeCount"] == 2, "specials excluded from episode totals")
    check(progress["integration-admin"]["status"] == "watched" and progress["integration-admin"]["watchedCount"] == 2,
          "administrator progress counts two watched episodes")
    check(progress["integration-viewer"]["status"] == "inProgress" and progress["integration-viewer"]["watchedCount"] == 1,
          "viewer progress is independent and partially watched")
    page = api("Series?" + urlencode({"userId": auth["viewerId"], "search": "Media Remover", "limit": 1}))
    check(page["totalCount"] == 1 and len(page["items"]) == 1 and page["items"][0]["watchedCount"] == 1,
          "selected-user list, search and pagination use live library data")
    everyone = api("Series?limit=25")["items"][0]
    check(everyone["watchedCount"] is None and {u["name"]: u["status"] for u in everyone["users"]} ==
          {"integration-admin": "watched", "integration-viewer": "inProgress"},
          "series list includes every user's independent viewing progress without selecting a user")
    api("Series?" + urlencode({"userId": auth["viewerId"], "limit": 0}), expected=400)
    provider_url = "http://providers:18967"
    settings = {"sonarrUrl": provider_url + "/sonarr", "seerrUrl": provider_url + "/seerr", "sonarrApiKey": KEY, "seerrApiKey": KEY}
    saved = api("Settings", method="PUT", data=settings)
    check(saved["hasSonarrApiKey"] and saved["hasSeerrApiKey"] and KEY not in json.dumps(saved) and KEY not in json.dumps(api("Settings")),
          "settings preserve keys server-side without returning them")
    result = api("Connections/Test", method="POST")
    check(result["sonarr"]["ok"] and result["seerr"]["ok"], "authenticated Sonarr and Seerr connection checks succeed")
    request_records = [{"id": 91, "requestedBy": {"id": 2, "displayName": "integration-viewer", "jellyfinUserId": auth["viewerId"]}},
                       {"id": 92, "requestedBy": {"id": 2, "displayName": "integration-viewer", "jellyfinUserId": auth["viewerId"]}}]
    request(PROVIDERS, "/__control", "POST", {"reset": True, "requestRecords": request_records})
    owners = api(f'Series/{series["Id"]}/Requests')
    check(owners["state"] == "available" and owners["requestCount"] == 2 and owners["unknownRequesterCount"] == 0
          and len(owners["requesters"]) == 1 and owners["requesters"][0]["name"] == "integration-viewer"
          and owners["requesters"][0]["watchStatus"] == "inProgress" and owners["requesters"][0]["watchedCount"] == 1,
          "Seerr requester records are grouped and linked to actual Jellyfin viewing progress")
    check(not any(e["method"] == "DELETE" for e in request(PROVIDERS, "/__state")["requests"]),
          "requester lookup is read-only")
    request(PROVIDERS, "/__control", "POST", {"reset": True, "failSeerrDelete": True})
    options = {"seriesId": series["Id"], "removeSonarr": True, "removeSeerr": True, "deleteFiles": True, "addImportListExclusion": True}
    preview = api("Removals/Preview", method="POST", data=options)
    check(preview["sonarr"]["id"] == 41 and preview["seerr"]["id"] == 73, "preview resolves exact provider identities")
    check(not any(e["method"] == "DELETE" for e in request(PROVIDERS, "/__state")["requests"]), "preview performs no deletion")
    api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": "wrong title"}, expected=400)
    check(not any(e["method"] == "DELETE" for e in request(PROVIDERS, "/__state")["requests"]), "wrong confirmation title performs no deletion")
    operation = api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": TITLE})
    check(operation["status"] == "partialFailure" and operation["sonarrDone"] and not operation["seerrDone"],
          "partial provider failure is persisted with completed Sonarr checkpoint")
    state = request(PROVIDERS, "/__state")
    sonarr_deletes = [e for e in state["requests"] if e["method"] == "DELETE" and e["path"].startswith("/sonarr/")]
    check(len(sonarr_deletes) == 1 and parse_qs(urlsplit(sonarr_deletes[0]["path"]).query) == {"deleteFiles": ["true"], "addImportListExclusion": ["true"]},
          "Sonarr receives both explicit file-deletion and import-exclusion flags")
    repeat = api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": TITLE})
    check(repeat["status"] == "partialFailure" and len(request(PROVIDERS, "/__state")["requests"]) == len(state["requests"]),
          "repeated execute does not repeat any provider requests")
    compose("restart", "jellyfin")
    wait_ready()
    history = api("Removals")
    check(any(o["id"] == preview["id"] and o["status"] == "partialFailure" for o in history), "removal history survives a real Jellyfin restart")
    request(PROVIDERS, "/__control", "POST", {"failSeerrDelete": False})
    retry = api("Removals/" + preview["id"] + "/Retry", method="POST", data={"confirmationTitle": TITLE})
    check(retry["status"] == "completed" and retry["sonarrDone"] and retry["seerrDone"], "retry after restart completes the remaining provider step")
    state = request(PROVIDERS, "/__state")
    check(sum(e["method"] == "DELETE" and e["path"].startswith("/sonarr/") for e in state["requests"]) == 1,
          "retry does not delete Sonarr twice")
    check(not any("/file" in e["path"] for e in state["requests"]), "Seerr removal never calls the independent file-deletion endpoint")
    api("Removals/Preview", method="POST", data=options, expected=409)
    check(True, "missing provider records cannot produce a destructive preview")
    request(PROVIDERS, "/__control", "POST", {"reset": True, "wrongSonarrIdentity": True})
    api("Removals/Preview", method="POST", data=options, expected=502)
    check(not any(e["method"] == "DELETE" for e in request(PROVIDERS, "/__state")["requests"]), "mismatched external identity fails closed")
    request(PROVIDERS, "/__control", "POST", {"reset": True, "unavailable": True})
    check(api(f'Series/{series["Id"]}/Requests')["state"] == "unavailable"
          and len(api("Series?limit=25")["items"][0]["users"]) == 2,
          "provider outage reports unavailable requesters while retaining all viewing progress")
    api("Removals/Preview", method="POST", data=options, expected=502)
    check(not any(e["method"] == "DELETE" for e in request(PROVIDERS, "/__state")["requests"]), "provider outage fails closed")
    request(PROVIDERS, "/__control", "POST", {"reset": True, "requestRecords": request_records})
    print(f"Jellyfin {JELLYFIN_VERSION} integration validation complete. Sandbox: {JELLYFIN}; local credentials: {(DEV / 'auth.json').relative_to(ROOT)}", flush=True)


def serve_fixture():
    lock = threading.Lock()
    state = {}

    def reset():
        state.clear()
        state.update(sonarrPresent=True, seerrPresent=True, failSeerrDelete=False, wrongSonarrIdentity=False, unavailable=False,
                     radarrPresent=True, seerrMoviePresent=True, failSeerrMovieDelete=False, wrongRadarrIdentity=False,
                     requests=[], requestRecords=[{"id": 91}], movieRequestRecords=[{"id": 93}])

    reset()

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def respond(self, status, value=None):
            body = b"" if value is None else json.dumps(value).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            self.route()

        def do_POST(self):
            self.route()

        def do_DELETE(self):
            self.route()

        def route(self):
            with lock:
                path = urlsplit(self.path).path
                if path == "/__state":
                    return self.respond(200, state)
                if path == "/__control" and self.command == "POST":
                    data = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))))
                    if data.pop("reset", False):
                        reset()
                    state.update(data)
                    return self.respond(200, {"ok": True})
                state["requests"].append({"method": self.command, "path": self.path})
                if self.headers.get("X-Api-Key") != KEY:
                    return self.respond(401, {"message": "Invalid fixture API key"})
                if state["unavailable"]:
                    return self.respond(503, {"message": "Synthetic outage"})
                sonarr = {"id": 41, "tvdbId": TVDB + int(state["wrongSonarrIdentity"]), "title": TITLE, "path": "/synthetic/" + TITLE}
                media = {"id": 73, "tmdbId": TMDB, "tvdbId": TVDB, "mediaType": "tv", "status": 5, "requests": state["requestRecords"]}
                radarr = {"id": 42, "tmdbId": MOVIE_TMDB + int(state["wrongRadarrIdentity"]), "title": MOVIE_TITLE, "path": "/synthetic/" + MOVIE_TITLE}
                movie_media = {"id": 74, "tmdbId": MOVIE_TMDB, "mediaType": "movie", "status": 5, "requests": state["movieRequestRecords"]}
                if self.command == "GET":
                    if path == "/radarr/api/v3/system/status":
                        return self.respond(200, {"appName": "Radarr", "version": "6.0.0"})
                    if path == "/radarr/api/v3/movie":
                        if parse_qs(urlsplit(self.path).query) != {"tmdbId": [str(MOVIE_TMDB)]}:
                            return self.respond(400)
                        return self.respond(200, [radarr] if state["radarrPresent"] else [])
                    if path == "/radarr/api/v3/movie/42":
                        return self.respond(200, radarr) if state["radarrPresent"] else self.respond(404)
                    if path == f"/seerr/api/v1/movie/{MOVIE_TMDB}":
                        return self.respond(200, {"id": MOVIE_TMDB, "title": MOVIE_TITLE, "mediaInfo": movie_media if state["seerrMoviePresent"] else None})
                    if path == "/sonarr/api/v3/system/status":
                        return self.respond(200, {"appName": "Sonarr", "version": "4.0.0"})
                    if path == "/seerr/api/v1/auth/me":
                        return self.respond(200, {"id": 1, "permissions": 2})
                    if path == "/sonarr/api/v3/series":
                        if parse_qs(urlsplit(self.path).query) != {"tvdbId": [str(TVDB)]}:
                            return self.respond(400)
                        return self.respond(200, [sonarr] if state["sonarrPresent"] else [])
                    if path == "/sonarr/api/v3/series/41":
                        return self.respond(200, sonarr) if state["sonarrPresent"] else self.respond(404)
                    if path == f"/seerr/api/v1/tv/{TMDB}":
                        return self.respond(200, {"id": TMDB, "name": TITLE, "mediaInfo": media if state["seerrPresent"] else None})
                if self.command == "DELETE":
                    if path == "/radarr/api/v3/movie/42":
                        state["radarrPresent"] = False
                        return self.respond(200)
                    if path == "/seerr/api/v1/media/74":
                        if state["failSeerrMovieDelete"]:
                            return self.respond(503)
                        state["seerrMoviePresent"] = False
                        return self.respond(204)
                    if path == "/sonarr/api/v3/series/41":
                        state["sonarrPresent"] = False
                        return self.respond(200)
                    if path == "/seerr/api/v1/media/73":
                        if state["failSeerrDelete"]:
                            return self.respond(503)
                        state["seerrPresent"] = False
                        return self.respond(204)
                return self.respond(404)

    ThreadingHTTPServer(("0.0.0.0", 18967), Handler).serve_forever()


def main():
    global DEV, JELLYFIN_VERSION
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["run", "setup", "check", "stop", "fixture"])
    parser.add_argument("--jellyfin", choices=VERSIONS, default="10.11.11")
    parser.add_argument("--plugin", type=Path, help="Override the matching Release assembly path")
    args = parser.parse_args()
    JELLYFIN_VERSION = args.jellyfin
    DEV = ROOT / ".dev" / JELLYFIN_VERSION
    plugin = args.plugin or ROOT / "src/Jellyfin.Plugin.MediaRemover/bin/Release" / VERSIONS[JELLYFIN_VERSION][1] / "Jellyfin.Plugin.MediaRemover.dll"
    if args.command == "fixture":
        serve_fixture()
    elif args.command == "stop":
        compose("down")
    elif args.command == "check":
        validate(json.loads((DEV / "auth.json").read_text()))
    else:
        auth = setup(plugin)
        if args.command == "run":
            validate(auth)
        else:
            create_library(auth)
            print(f"Jellyfin {JELLYFIN_VERSION} sandbox ready at {JELLYFIN}; local credentials: {(DEV / 'auth.json').relative_to(ROOT)}")


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, RuntimeError, URLError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
