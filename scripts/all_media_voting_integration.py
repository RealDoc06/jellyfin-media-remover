#!/usr/bin/env python3
"""All-media voting checks against the disposable fixture initialized by integration.py.

Use --prepare before installing the new plugin to also verify old series votes survive
an upgrade. Generated media lives only in .dev/<version>/media-extra.
"""

import argparse
import base64
import copy
import json
from pathlib import Path
import shutil
import subprocess
import time
from urllib.parse import urlencode
import zipfile

import integration
from integration import JELLYFIN, PROVIDERS, check, request
from voting_integration import fixture_guard, normalized


PREFIX = "/MediaRemover/Voting/"
LIBRARIES = {
    "movies": "movies", "music": "music", "videos": "homevideos",
    "musicvideos": "musicvideos", "books": "books", "photos": "homevideos",
}


def native_items(auth):
    return request(JELLYFIN, "/Items?Recursive=true&Fields=Path", token=auth["adminToken"])["Items"]


def prepare(auth, dev):
    fixture_guard(auth)
    extra = dev / "media-extra"
    for name in LIBRARIES:
        (extra / name).mkdir(parents=True, exist_ok=True)
    sample = next((dev / "media").rglob("*.mkv"))
    movie = extra / "movies" / "Voting Test Movie (2026)"
    movie.mkdir(exist_ok=True)
    shutil.copyfile(sample, movie / "Voting Test Movie.mkv")
    (movie / "movie.nfo").write_text(f'<movie><title>Voting Test Movie</title><year>2026</year><uniqueid type="tmdb" default="true">{integration.MOVIE_TMDB}</uniqueid><lockdata>true</lockdata></movie>')
    shutil.copyfile(sample, extra / "videos" / "Voting Test Video.mkv")
    shutil.copyfile(sample, extra / "musicvideos" / "Voting Test Music Video.mkv")
    album = extra / "music" / "Voting Test Artist" / "Voting Test Album"
    album.mkdir(parents=True, exist_ok=True)
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono",
                    "-t", "2", "-metadata", "title=Voting Test Track", "-metadata", "artist=Voting Test Artist",
                    "-metadata", "album_artist=Voting Test Artist", "-metadata", "album=Voting Test Album",
                    "-y", str(album / "01 Voting Test Track.flac")], check=True)
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono",
                    "-t", "2", "-c:a", "aac", "-metadata", "title=Voting Test Audiobook",
                    "-y", str(extra / "books" / "Voting Test Audiobook.m4b")], check=True)
    with zipfile.ZipFile(extra / "books" / "Voting Test Book.epub", "w") as book:
        book.writestr("mimetype", "application/epub+zip")
        book.writestr("META-INF/container.xml", '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
        book.writestr("content.opf", '<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">jmr-test</dc:identifier><dc:title>Voting Test Book</dc:title><dc:language>en</dc:language></metadata><manifest><item id="page" href="page.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="page"/></spine></package>')
        book.writestr("page.xhtml", '<html xmlns="http://www.w3.org/1999/xhtml"><body><p>Synthetic voting fixture.</p></body></html>')
    photos = extra / "photos" / "Voting Test Photo Album"
    photos.mkdir(exist_ok=True)
    (photos / "Voting Test Photo.png").write_bytes(base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="))

    libraries = request(JELLYFIN, "/Library/VirtualFolders", token=auth["adminToken"])
    kinds = ["Movie", "Video", "MusicVideo", "Audio", "MusicAlbum", "MusicArtist", "Book", "AudioBook", "Photo", "PhotoAlbum"]
    for directory, collection_type in LIBRARIES.items():
        name = "Voting " + directory
        if any(library["Name"] == name for library in libraries):
            continue
        request(JELLYFIN, "/Library/VirtualFolders?" + urlencode({"name": name, "collectionType": collection_type, "refreshLibrary": "false"}),
                "POST", {"LibraryOptions": {"PathInfos": [{"Path": "/media-extra/" + directory}], "EnableInternetProviders": False,
                "EnableRealtimeMonitor": False, "TypeOptions": [{"Type": kind, "MetadataFetchers": [], "ImageFetchers": []} for kind in kinds],
                "MetadataSavers": [], "LocalMetadataReaderOrder": ["Nfo"]}}, auth["adminToken"], expected=204)
    request(JELLYFIN, "/Library/Refresh", "POST", token=auth["adminToken"], expected=204)
    expected = {"Movie", "Video", "MusicVideo", "Audio", "MusicAlbum", "Book", "AudioBook", "Photo", "PhotoAlbum"}
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        items = native_items(auth)
        seen = {item["Type"] for item in items if item.get("Path", "").startswith("/media-extra/")}
        if expected <= seen:
            break
        time.sleep(1)
    else:
        raise AssertionError(f"Fixture scan missing media types: {expected - seen}; found {seen}")
    movie_id = next(item["Id"] for item in items if item["Name"] == "Voting Test Movie")
    for name, public in [("Voting Test Public Playlist", True), ("Voting Test Private Playlist", False)]:
        if not any(item["Name"] == name for item in items):
            request(JELLYFIN, "/Playlists", "POST", {"Name": name, "Ids": [movie_id], "UserId": auth["adminId"], "IsPublic": public}, auth["adminToken"])
    if not any(item["Name"] == "Voting Test Collection" for item in items):
        request(JELLYFIN, "/Collections?" + urlencode({"name": "Voting Test Collection", "ids": movie_id}), "POST", token=auth["adminToken"])

    series = request(JELLYFIN, PREFIX + "Series", token=auth["viewerToken"])["items"][0]
    vote = request(JELLYFIN, PREFIX + f'Series/{series["id"]}/Vote', "PUT", {"approved": True}, auth["viewerToken"])
    request(JELLYFIN, PREFIX + "Preferences", "PUT", {"homeDismissed": True}, auth["viewerToken"])
    (dev / "all-media-checkpoint.json").write_text(json.dumps({"seriesId": series["id"], "votedAt": vote["votedAt"]}))
    print("Prepared mixed-media fixture and legacy series vote checkpoint.", flush=True)


def run(auth, dev):
    fixture_guard(auth)
    viewer = lambda path, **kwargs: request(JELLYFIN, PREFIX + path, token=auth["viewerToken"], **kwargs)
    admin = lambda path, **kwargs: request(JELLYFIN, PREFIX + path, token=auth["adminToken"], **kwargs)
    checkpoint = json.loads((dev / "all-media-checkpoint.json").read_text())
    previous = viewer("Items/" + checkpoint["seriesId"])
    check(previous["myVote"] and previous["votedAt"] == checkpoint["votedAt"] and viewer("Preferences")["homeDismissed"],
          "legacy series vote identity/timestamp and Home preference survive the upgrade")
    viewer("Preferences", method="PUT", data={"homeDismissed": False})
    provider_before = request(PROVIDERS, "/__state")["requests"]
    history_before = request(JELLYFIN, "/MediaRemover/Removals", token=auth["adminToken"])
    items = native_items(auth)
    test_items = [item for item in items if item["Name"].startswith("Voting Test ") and item["Type"] not in ["MusicArtist", "Folder"]]
    movie = next(item for item in test_items if item["Type"] == "Movie")
    request(JELLYFIN, f'/Users/{auth["viewerId"]}/PlayedItems/{movie["Id"]}', "POST", token=auth["viewerToken"])
    watched_before = request(JELLYFIN, f'/Users/{auth["viewerId"]}/Items/{movie["Id"]}', token=auth["viewerToken"])["UserData"]
    for item in test_items:
        admin(f'Items/{item["Id"]}/Vote', method="PUT", data={"approved": False})
        if "Private Playlist" not in item["Name"]:
            viewer(f'Items/{item["Id"]}/Vote', method="PUT", data={"approved": False})
    for path in ["Items", "Items/Home", "Items/Summary", "Items/" + checkpoint["seriesId"]]:
        request(JELLYFIN, PREFIX + path, expected=401)
    viewer("Items/Summary", expected=403)
    page = viewer("Items?limit=100")
    types = {item["type"] for item in page["items"]}
    expected = {"Series", "Movie", "Video", "MusicVideo", "Audio", "MusicAlbum", "Book", "AudioBook", "Photo", "PhotoAlbum", "Playlist", "BoxSet"}
    check(expected <= types and not {"Episode", "Season", "MusicArtist", "CollectionFolder"}.intersection(types),
          "browse covers all library media, keeping episodes/seasons under their whole series")
    check(not any(item["name"] == "Voting Test Private Playlist" for item in page["items"]), "private playlists do not leak into another user's browse or counts")
    check(not any({"users", "voters", "requesters"}.intersection(item) for item in page["items"]), "mixed-media user responses expose no voter identities")
    movie_row = viewer("Items/" + movie["Id"])
    check(movie_row["itemCount"] == 1 and movie_row["playedCount"] == 1 and movie_row["status"] == "watched",
          "movies use their own played state, rather than empty episode progress")
    for item in test_items:
        path = "Items/" + item["Id"]
        if "Private Playlist" in item["Name"]:
            viewer(path, expected=404)
            viewer(path + "/Vote", method="PUT", data={"approved": True}, expected=404)
            continue
        result = viewer(path)
        assert result["type"] == item["Type"] and normalized(result["id"]) == normalized(item["Id"])
        vote = viewer(path + "/Vote", method="PUT", data={"approved": True})
        assert vote == viewer(path + "/Vote", method="PUT", data={"approved": True})
    check(True, "each media kind can be nominated directly; duplicate votes remain idempotent")
    generic = admin("Items/Summary")
    check(expected <= {item["type"] for item in generic["items"]}, "admin summary includes voted media kinds with their voter identities")
    check(all(item["seriesId"] == checkpoint["seriesId"] for item in admin("Summary")["series"]), "cached series-only admin clients never receive non-series removals")
    episode = next(item for item in items if item["Type"] == "Episode")
    season = next(item for item in items if item["Type"] == "Season")
    for child in [episode, season]:
        resolved = viewer("Items/" + child["Id"])
        assert normalized(resolved["id"]) == normalized(checkpoint["seriesId"]) and resolved["type"] == "Series"
        vote = viewer("Items/" + child["Id"] + "/Vote", method="PUT", data={"approved": True})
        assert normalized(vote["itemId"]) == normalized(checkpoint["seriesId"]) and vote["votedAt"] == checkpoint["votedAt"]
    check(True, "episode and season actions reuse one whole-series vote, preserving its timestamp")
    movie_path = "Items/" + movie["Id"]
    check(not viewer("Items/Home")["items"] and len(admin("Items/Home")["items"]) == 3,
          "Home excludes self-only nominations and caps other-user nominations at three")
    admin(movie_path + "/Vote", method="PUT", data={"approved": True})
    check(normalized(viewer("Items/Home")["items"][0]["id"]) == normalized(movie["Id"]), "a movie nominated by another user appears on Home")
    policy = request(JELLYFIN, f'/Users/{auth["viewerId"]}', token=auth["adminToken"])["Policy"]
    try:
        restricted = copy.deepcopy(policy)
        restricted.update(EnableAllFolders=False, EnabledFolders=[])
        request(JELLYFIN, f'/Users/{auth["viewerId"]}/Policy', "POST", restricted, auth["adminToken"], expected=204)
        viewer(movie_path, expected=404)
        viewer(movie_path + "/Vote", method="PUT", data={"approved": False}, expected=404)
        viewer("Items/" + episode["Id"], expected=404)
        check(not any(item["type"] == "Movie" for item in viewer("Items")["items"]) and not viewer("Items/Home")["items"],
              "restricted libraries hide movie nominations and deny canonical TV lookups")
    finally:
        request(JELLYFIN, f'/Users/{auth["viewerId"]}/Policy', "POST", policy, auth["adminToken"], expected=204)
    check(request(PROVIDERS, "/__state")["requests"] == provider_before and request(JELLYFIN, "/MediaRemover/Removals", token=auth["adminToken"]) == history_before,
          "all-media voting makes no provider calls or removal operations")
    check(request(JELLYFIN, f'/Users/{auth["viewerId"]}/Items/{movie["Id"]}', token=auth["viewerToken"])["UserData"] == watched_before,
          "all-media voting leaves the movie's watched state unchanged")
    print("All-media voting checks passed.", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jellyfin", choices=integration.VERSIONS, default="10.11.11")
    parser.add_argument("--prepare", action="store_true")
    args = parser.parse_args()
    dev = integration.ROOT / ".dev" / args.jellyfin
    auth = json.loads((dev / "auth.json").read_text())
    if args.prepare:
        prepare(auth, dev)
    else:
        run(auth, dev)
