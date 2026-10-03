#!/usr/bin/env python3
"""Movie progress and Radarr/Seerr removal checks in the disposable Jellyfin fixture."""

import argparse
import json
from urllib.parse import parse_qs, urlsplit

import integration
from integration import JELLYFIN, PROVIDERS, KEY, MOVIE_TITLE, MOVIE_TMDB, check, request
from voting_integration import fixture_guard


def run(auth):
    fixture_guard(auth)
    token = auth["adminToken"]
    api = lambda path, **kwargs: request(JELLYFIN, "/MediaRemover/" + path, token=token, **kwargs)
    native = request(JELLYFIN, "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=ProviderIds", token=token)["Items"]
    movie = next(item for item in native if item["Name"] == MOVIE_TITLE)
    # Only generated local media is used, with movie/TV IDs intentionally overlapping.
    if movie.get("ProviderIds", {}).get("Tmdb") != str(MOVIE_TMDB):
        metadata = request(JELLYFIN, "/Items/" + movie["Id"], token=token)
        metadata["ProviderIds"] = dict(metadata.get("ProviderIds", {}), Tmdb=str(MOVIE_TMDB))
        request(JELLYFIN, "/Items/" + movie["Id"], "POST", metadata, token, expected=204)
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
    check(operation["status"] == "partialFailure" and operation["radarrDone"] and not operation["seerrDone"],
          "successful Radarr removal is checkpointed before a failed Seerr step")
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

    request(PROVIDERS, "/__control", "POST", {"reset": True, "wrongRadarrIdentity": True})
    api("Removals/Preview", method="POST", data=options, expected=502)
    check(not any(entry["method"] == "DELETE" for entry in request(PROVIDERS, "/__state")["requests"]),
          "a conflicting Radarr TMDB identity fails closed")
    request(PROVIDERS, "/__control", "POST", {"reset": True})
    keep_files = dict(options, deleteFiles=False, addImportListExclusion=False)
    preview = api("Removals/Preview", method="POST", data=keep_files)
    operation = api("Removals/" + preview["id"] + "/Execute", method="POST", data={"confirmationTitle": MOVIE_TITLE})
    deletes = [entry for entry in request(PROVIDERS, "/__state")["requests"] if entry["method"] == "DELETE" and entry["path"].startswith("/radarr/")]
    check(operation["status"] == "completed" and parse_qs(urlsplit(deletes[0]["path"]).query) == {"deleteFiles": ["false"], "addImportExclusion": ["false"]},
          "record-only movie removal explicitly keeps files")
    request(PROVIDERS, "/__control", "POST", {"reset": True, "movieRequestRecords": request_records})
    check(request(JELLYFIN, f'/Users/{auth["viewerId"]}/Items/{movie["Id"]}', token=token)["UserData"]["Played"],
          "provider fixture removals leave the synthetic movie and watched state intact")
    print("Movie integration checks passed.", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jellyfin", choices=integration.VERSIONS, default="10.11.11")
    args = parser.parse_args()
    integration.JELLYFIN_VERSION = args.jellyfin
    integration.DEV = integration.ROOT / ".dev" / args.jellyfin
    run(json.loads((integration.DEV / "auth.json").read_text()))
