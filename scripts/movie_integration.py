#!/usr/bin/env python3
"""Movie progress and Radarr/Seerr removal checks in the disposable Jellyfin fixture."""

import argparse
import json
import shutil
import time
from urllib.parse import parse_qs, urlsplit

import integration
from integration import JELLYFIN, PROVIDERS, KEY, MOVIE_TITLE, MOVIE_TMDB, check, request
from voting_integration import fixture_guard


DELETION_TITLE = "Jellyfin Deletion Test Movie"


def find_movie(token, title, scan=False):
    """Return the scanned movie, giving it the fixture TMDB ID. With scan, refresh the library until it appears."""
    if scan:
        request(JELLYFIN, "/Library/Refresh", "POST", token=token, expected=204)
    deadline = time.monotonic() + 90
    while True:
        native = request(JELLYFIN, "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=ProviderIds", token=token)["Items"]
        movie = next((item for item in native if item["Name"] == title), None)
        if movie or time.monotonic() > deadline:
            break
        time.sleep(1)
    if movie is None:
        raise RuntimeError(f"{title} was not found in the Jellyfin library.")
    # Only generated local media is used, with movie/TV IDs intentionally overlapping.
    if movie.get("ProviderIds", {}).get("Tmdb") != str(MOVIE_TMDB):
        metadata = request(JELLYFIN, "/Items/" + movie["Id"], token=token)
        metadata["ProviderIds"] = dict(metadata.get("ProviderIds", {}), Tmdb=str(MOVIE_TMDB))
        request(JELLYFIN, "/Items/" + movie["Id"], "POST", metadata, token, expected=204)
    return movie


def run(auth):
    fixture_guard(auth)
    token = auth["adminToken"]
    api = lambda path, **kwargs: request(JELLYFIN, "/MediaRemover/" + path, token=token, **kwargs)
    movie = find_movie(token, MOVIE_TITLE)
    for path in ["Items", "Items/" + movie["Id"], "Items/" + movie["Id"] + "/Requests"]:
        request(JELLYFIN, "/MediaRemover/" + path, expected=401)
        request(JELLYFIN, "/MediaRemover/" + path, token=auth["viewerToken"], expected=403)
    check(True, "movie admin routes reject anonymous and ordinary-user access")

    base = "http://providers:18967"
    settings = {"sonarrUrl": base + "/sonarr", "seerrUrl": base + "/seerr", "radarrUrl": base + "/radarr",
                "sonarrApiKey": KEY, "seerrApiKey": KEY, "radarrApiKey": KEY}
    saved = api("Settings", method="PUT", data=settings)
    check(saved["hasRadarrApiKey"] and KEY not in json.dumps(saved), "Radarr credentials are stored without exposing the key")
    legacy = {name: value for name, value in settings.items() if not name.startswith("radarr")}
    api("Settings", method="PUT", data=legacy)
    check(api("Settings") == saved, "settings saves from older clients preserve Radarr configuration")
    request(PROVIDERS, "/__control", "POST", {"reset": True})
    check(all(result["ok"] for result in api("Connections/Test", method="POST").values()), "all three provider connection checks pass")

    request(JELLYFIN, f'/Users/{auth["viewerId"]}/PlayedItems/{movie["Id"]}', "POST", token=token)
    request(JELLYFIN, f'/Users/{auth["adminId"]}/PlayedItems/{movie["Id"]}', "DELETE", token=token)
    detail = api("Items/" + movie["Id"])
    progress = {user["name"]: user for user in detail["users"]}
    check(detail["type"] == "Movie" and detail["episodeCount"] == 1
          and progress["integration-viewer"]["status"] == "watched" and progress["integration-viewer"]["watchedCount"] == 1
          and progress["integration-admin"]["status"] == "unwatched", "movie detail reports each user's actual movie watched state")
    page = api("Items?search=Voting%20Test%20Movie&limit=1&userId=" + auth["viewerId"])
    check(page["totalCount"] == 1 and page["items"][0]["type"] == "Movie" and page["items"][0]["watchedCount"] == 1,
          "admin media search and pagination include movies")
    request_records = [{"id": 93, "requestedBy": {"id": 2, "displayName": "integration-viewer", "jellyfinUserId": auth["viewerId"]}}]
    request(PROVIDERS, "/__control", "POST", {"reset": True, "movieRequestRecords": request_records})
    owners = api("Items/" + movie["Id"] + "/Requests")
    check(owners["state"] == "available" and owners["requestCount"] == 1
          and owners["requesters"][0]["watchStatus"] == "watched", "Seerr movie requesters link to the movie's own Jellyfin progress")

    options = {"seriesId": movie["Id"], "mediaType": "Movie", "removeSonarr": False, "removeRadarr": True,
               "removeSeerr": True, "deleteFiles": True, "addImportListExclusion": True}
    preview = api("Removals/Preview", method="POST", data=options)
    check(preview["radarr"]["id"] == 42 and preview["sonarr"] is None and preview["seerr"]["id"] == 74
          and preview["seerr"]["mediaType"] == "movie", "movie preview matches Radarr and Seerr movie identities, never same-ID TV records")
    api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": "wrong title"}, expected=400)
    check(not any(entry["method"] == "DELETE" for entry in request(PROVIDERS, "/__state")["requests"]),
          "movie preview and incorrect title confirmation cannot delete anything")
    request(PROVIDERS, "/__control", "POST", {"failSeerrMovieDelete": True})
    operation = api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": MOVIE_TITLE})
    check(operation["status"] == "partialFailure" and operation["radarrDone"] and operation["jellyfinDone"] and not operation["seerrDone"],
          "successful Radarr and Jellyfin removals are checkpointed before a failed Seerr step")
    api("Items/" + movie["Id"], expected=404)
    check(True, "movie files deleted through Radarr also leave the Jellyfin library")
    state = request(PROVIDERS, "/__state")
    deletes = [entry for entry in state["requests"] if entry["method"] == "DELETE" and entry["path"].startswith("/radarr/")]
    check(len(deletes) == 1 and parse_qs(urlsplit(deletes[0]["path"]).query) == {"deleteFiles": ["true"], "addImportExclusion": ["true"]},
          "Radarr receives its own explicit file-deletion and import-exclusion parameters")
    repeated = api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": MOVIE_TITLE})
    check(repeated["status"] == "partialFailure" and request(PROVIDERS, "/__state")["requests"] == state["requests"],
          "duplicate confirmation does not repeat provider calls")
    integration.compose("restart", "jellyfin")
    integration.wait_ready()
    check(any(entry["id"] == preview["id"] and entry["status"] == "partialFailure" and entry["radarrDone"] for entry in api("Removals")),
          "unfinished movie removal survives a real Jellyfin restart")
    request(PROVIDERS, "/__control", "POST", {"failSeerrMovieDelete": False})
    operation = api("Removals/" + preview["id"] + "/Retry", method="POST", data={"confirmationTitle": MOVIE_TITLE})
    state = request(PROVIDERS, "/__state")
    check(operation["status"] == "completed" and sum(entry["method"] == "DELETE" and entry["path"].startswith("/radarr/") for entry in state["requests"]) == 1,
          "retry completes Seerr without deleting Radarr twice")
    check(state["sonarrPresent"] and state["seerrPresent"] and not state["radarrPresent"] and not state["seerrMoviePresent"],
          "movie removal leaves the corresponding TV provider records untouched")
    # The simulated Radarr never touched the files, so a scan restores the fixture movie with the same ID.
    check(find_movie(token, MOVIE_TITLE, scan=True)["Id"] == movie["Id"], "library scan restores the fixture movie with its original ID")
    request(JELLYFIN, f'/Users/{auth["viewerId"]}/PlayedItems/{movie["Id"]}', "POST", token=token)

    request(PROVIDERS, "/__control", "POST", {"reset": True, "wrongRadarrIdentity": True})
    api("Removals/Preview", method="POST", data=options, expected=502)
    check(not any(entry["method"] == "DELETE" for entry in request(PROVIDERS, "/__state")["requests"]),
          "a conflicting Radarr TMDB identity fails closed")
    request(PROVIDERS, "/__control", "POST", {"reset": True})
    keep_files = dict(options, deleteFiles=False, addImportListExclusion=False)
    preview = api("Removals/Preview", method="POST", data=keep_files)
    operation = api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": MOVIE_TITLE})
    deletes = [entry for entry in request(PROVIDERS, "/__state")["requests"] if entry["method"] == "DELETE" and entry["path"].startswith("/radarr/")]
    check(operation["status"] == "completed" and operation["jellyfinDone"] is None
          and parse_qs(urlsplit(deletes[0]["path"]).query) == {"deleteFiles": ["false"], "addImportExclusion": ["false"]},
          "record-only movie removal explicitly keeps files")
    check(request(JELLYFIN, f'/Users/{auth["viewerId"]}/Items/{movie["Id"]}', token=token)["UserData"]["Played"],
          "record-only removal leaves the movie and its watched state in Jellyfin")

    # Radarr has no record (e.g. removed earlier with files kept): Jellyfin deletes the files itself.
    # A dedicated copy in the writable media-extra mount is really deleted from disk.
    folder = integration.DEV / "media-extra" / "movies" / (DELETION_TITLE + " (2026)")
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(next((integration.DEV / "media").rglob("*.mkv")), folder / (DELETION_TITLE + ".mkv"))
    (folder / "movie.nfo").write_text(f'<movie><title>{DELETION_TITLE}</title><year>2026</year><uniqueid type="tmdb" default="true">{MOVIE_TMDB}</uniqueid><lockdata>true</lockdata></movie>')
    doomed = find_movie(token, DELETION_TITLE, scan=True)
    request(PROVIDERS, "/__control", "POST", {"reset": True, "radarrPresent": False, "seerrMoviePresent": False})
    preview = api("Removals/Preview", method="POST", data=dict(options, seriesId=doomed["Id"]))
    check(preview["radarr"] is None and preview["seerr"] is None
          and any("Jellyfin will delete the movie files from disk" in w for w in preview["warnings"]),
          "file deletion without a Radarr record previews Jellyfin's own deletion with a warning")
    api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": "wrong title"}, expected=400)
    check((folder / (DELETION_TITLE + ".mkv")).exists(), "an incorrect title leaves the files on disk")
    operation = api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": DELETION_TITLE})
    check(operation["status"] == "completed" and operation["jellyfinDone"]
          and not any(entry["method"] == "DELETE" for entry in request(PROVIDERS, "/__state")["requests"]),
          "Jellyfin deletion completes without any provider deletion")
    check(not (folder / (DELETION_TITLE + ".mkv")).exists() and not folder.exists(),
          "Jellyfin deleted the movie folder from disk")
    api("Items/" + doomed["Id"], expected=404)
    check(DELETION_TITLE not in [i["Name"] for i in request(JELLYFIN, "/Items?Recursive=true&IncludeItemTypes=Movie", token=token)["Items"]],
          "the deleted movie left the Jellyfin library")
    request(PROVIDERS, "/__control", "POST", {"reset": True, "movieRequestRecords": request_records})
    print("Movie integration checks passed.", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jellyfin", choices=integration.VERSIONS, default="10.11.11")
    args = parser.parse_args()
    integration.JELLYFIN_VERSION = args.jellyfin
    integration.DEV = integration.ROOT / ".dev" / args.jellyfin
    run(json.loads((integration.DEV / "auth.json").read_text()))
