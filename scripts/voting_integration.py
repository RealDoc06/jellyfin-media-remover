#!/usr/bin/env python3
"""Voting checks for the existing disposable Jellyfin fixture, never a live server.

Import run(auth, series_id) after integration.validate() has initialized the fixture.
The caller owns container startup/restart/shutdown. run() returns a vote timestamp;
after restarting the fixture, verify_persistence(auth, series_id, timestamp) checks
durability and restores the Home section for browser review.
"""

import argparse
import copy
import json
from urllib.parse import urlencode
import uuid

import integration
from integration import JELLYFIN, PROVIDERS, check, request


PREFIX = "/MediaRemover/Voting/"


def fixture_guard(auth):
    if auth.get("baseUrl") != JELLYFIN or JELLYFIN != "http://127.0.0.1:18966":
        raise RuntimeError("Voting checks are limited to the local disposable Jellyfin fixture.")
    admin = request(JELLYFIN, "/Users/Me", token=auth["adminToken"])
    viewer = request(JELLYFIN, "/Users/Me", token=auth["viewerToken"])
    if admin["Name"] != "integration-admin" or viewer["Name"] != "integration-viewer":
        raise RuntimeError("Expected disposable integration users; refusing to change this server.")


def run(auth, series_id):
    fixture_guard(auth)
    admin_token, viewer_token = auth["adminToken"], auth["viewerToken"]
    api = lambda path, **kwargs: request(JELLYFIN, PREFIX + path, token=viewer_token, **kwargs)
    admin = lambda path, **kwargs: request(JELLYFIN, PREFIX + path, token=admin_token, **kwargs)
    vote_path = f"Series/{series_id}/Vote"
    provider_before = request(PROVIDERS, "/__state")["requests"]
    progress_before = request(JELLYFIN, f"/MediaRemover/Series/{series_id}", token=admin_token)["users"]
    history_before = request(JELLYFIN, "/MediaRemover/Removals", token=admin_token)

    for path in ("Series", "Home", f"Series/{series_id}", "Preferences", "Summary"):
        request(JELLYFIN, PREFIX + path, expected=401)
    request(JELLYFIN, PREFIX + vote_path, method="PUT", data={"approved": True}, expected=401)
    api("Summary", expected=403)
    check(True, "voting requires authentication and voter identities require administrator access")

    api(vote_path, method="PUT", data={"approved": False})
    admin(vote_path, method="PUT", data={"approved": False})
    api("Preferences", method="PUT", data={"homeDismissed": False})
    admin("Preferences", method="PUT", data={"homeDismissed": False})
    page = api("Series?" + urlencode({"search": integration.TITLE, "limit": 25, "userId": auth["adminId"]}))
    row = next(item for item in page["items"] if normalized(item["id"]) == normalized(series_id))
    check(row["episodeCount"] == 2 and row["watchedCount"] == 1 and row["status"] == "inProgress"
          and not row["myVote"] and row["voteCount"] == 0 and row["votedAt"] is None,
          "ordinary-user voting list uses only that account's progress, ignoring a spoofed userId")
    check(not {"users", "voters", "requesters"}.intersection(row), "ordinary-user voting list exposes no other users or voter identities")
    check(api(f"Series/{series_id}") == row and api("Home")["items"] == [],
          "single-series voting lookup matches the user list, and Home excludes unvoted series")
    api("Home?limit=0", expected=400)
    api("Home?limit=11", expected=400)

    vote = api(vote_path, method="PUT", data={"approved": True, "userId": auth["adminId"]})
    repeated = api(vote_path, method="PUT", data={"approved": True})
    summary = admin("Summary")
    voted_series = next(item for item in summary["series"] if normalized(item["seriesId"]) == normalized(series_id))
    check(vote == repeated and vote["myVote"] and vote["voteCount"] == 1 and vote["votedAt"],
          "repeating an affirmative vote is idempotent and preserves its timestamp")
    home = admin("Home")
    check(api("Home")["items"] == [] and len(home["items"]) == 1
          and normalized(home["items"][0]["id"]) == normalized(series_id)
          and home["items"][0]["voteCount"] == 1 and not home["items"][0]["myVote"]
          and home["items"][0]["watchedCount"] == 2,
          "Home shows only nominations from another enabled account with the viewer's own progress")
    check(voted_series["name"] == integration.TITLE and voted_series["productionYear"] == 2026
          and voted_series["voteCount"] == 1 and normalized(voted_series["voters"][0]["id"]) == normalized(auth["viewerId"])
          and voted_series["voters"][0]["name"] == "integration-viewer",
          "administrator sees the real authenticated voter and actionable series title")
    api(vote_path, method="PUT", data={}, expected=400)
    api("Preferences", method="PUT", data={}, expected=400)
    check(api("Series")["items"][0]["myVote"], "missing explicit booleans cannot accidentally withdraw votes or change preferences")
    removed = api(vote_path, method="PUT", data={"approved": False})
    check(not removed["myVote"] and removed["voteCount"] == 0 and removed["votedAt"] is None
          and api(vote_path, method="PUT", data={"approved": False}) == removed,
          "users can withdraw their own votes idempotently")

    api("Preferences", method="PUT", data={"homeDismissed": True})
    check(api("Preferences")["homeDismissed"] and not admin("Preferences")["homeDismissed"],
          "Home dismissal is private to the signed-in account")
    api("Preferences", method="PUT", data={"homeDismissed": False})
    check(not api("Preferences")["homeDismissed"], "users can restore the voting section on Home")

    original_policy = request(JELLYFIN, f'/Users/{auth["viewerId"]}', token=admin_token)["Policy"]
    policy_path = f'/Users/{auth["viewerId"]}/Policy'
    admin(vote_path, method="PUT", data={"approved": True})
    check(len(api("Home")["items"]) == 1, "another account's nomination appears on the viewer's Home")
    try:
        restricted = copy.deepcopy(original_policy)
        restricted.update(EnableAllFolders=False, EnabledFolders=[])
        request(JELLYFIN, policy_path, "POST", restricted, admin_token, expected=204)
        hidden_page = api("Series")
        api(vote_path, method="PUT", data={"approved": True}, expected=404)
        api(vote_path, method="PUT", data={"approved": False}, expected=404)
        api(f"Series/{uuid.uuid4()}/Vote", method="PUT", data={"approved": True}, expected=404)
        api(f"Series/{series_id}", expected=404)
        api(f"Series/{uuid.uuid4()}", expected=404)
        hidden_home = api("Home")
        check(hidden_page["items"] == [] and hidden_page["totalCount"] == 0
              and hidden_home["items"] == [] and hidden_home["totalCount"] == 0,
              "library restrictions hide nominated Home series and deny direct lookup, votes, or withdrawals")
        restricted = copy.deepcopy(original_policy)
        restricted["AllowedTags"] = ["jmr-no-test-series-has-this-tag"]
        request(JELLYFIN, policy_path, "POST", restricted, admin_token, expected=204)
        hidden_page = api("Series")
        api(vote_path, method="PUT", data={"approved": True}, expected=404)
        api(f"Series/{series_id}", expected=404)
        hidden_home = api("Home")
        check(hidden_page["items"] == [] and hidden_page["totalCount"] == 0
              and hidden_home["items"] == [] and hidden_home["totalCount"] == 0,
              "parental allowed-tag restrictions also hide nominated Home series and deny direct lookup or votes")
    finally:
        request(JELLYFIN, policy_path, "POST", original_policy, admin_token, expected=204)
        admin(vote_path, method="PUT", data={"approved": False})
    check(api("Series")["totalCount"] == 1 and admin("Summary")["totalVotes"] == 0,
          "restoring library access reveals the series without any vote from rejected attempts")

    app = "jmr-voting-check-" + uuid.uuid4().hex
    key = None
    try:
        request(JELLYFIN, "/Auth/Keys?" + urlencode({"app": app}), "POST", token=admin_token, expected=204)
        keys = request(JELLYFIN, "/Auth/Keys", token=admin_token)["Items"]
        key = next(item["AccessToken"] for item in keys if item["AppName"] == app)
        for path in ("Series", "Home", f"Series/{series_id}", "Preferences", "Summary"):
            request(JELLYFIN, PREFIX + path, token=key, expected=403)
        request(JELLYFIN, PREFIX + vote_path, "PUT", {"approved": True}, key, expected=403)
        request(JELLYFIN, PREFIX + "Preferences", "PUT", {"homeDismissed": True}, key, expected=403)
        check(True, "server API keys cannot vote, change personal preferences, or impersonate a user")
    finally:
        if key:
            try:
                request(JELLYFIN, "/Auth/Keys/" + key, "DELETE", token=admin_token, expected=204)
            except Exception:
                raise RuntimeError("Failed to revoke the disposable voting-test API key.") from None

    # Leave a meaningful vote and preference for the caller's restart durability check.
    vote = api(vote_path, method="PUT", data={"approved": True})
    api("Preferences", method="PUT", data={"homeDismissed": True})
    check(request(PROVIDERS, "/__state")["requests"] == provider_before,
          "voting makes no provider calls of any kind")
    check(request(JELLYFIN, f"/MediaRemover/Series/{series_id}", token=admin_token)["users"] == progress_before
          and request(JELLYFIN, "/MediaRemover/Removals", token=admin_token) == history_before,
          "voting changes no watched state and creates no removal operations")
    return vote["votedAt"]


def verify_persistence(auth, series_id, voted_at):
    fixture_guard(auth)
    token = auth["viewerToken"]
    page = request(JELLYFIN, PREFIX + "Series", token=token)
    row = next(item for item in page["items"] if normalized(item["id"]) == normalized(series_id))
    preferences = request(JELLYFIN, PREFIX + "Preferences", token=token)
    check(row["myVote"] and row["votedAt"] == voted_at and preferences["homeDismissed"],
          "vote identity, original timestamp, and Home dismissal survive a Jellyfin restart")
    request(JELLYFIN, PREFIX + "Preferences", "PUT", {"homeDismissed": False}, token)


def normalized(value):
    return str(value).replace("-", "").lower()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jellyfin", choices=integration.VERSIONS, default="10.11.11")
    parser.add_argument("--verify-persistence", action="store_true", help="Check the saved checkpoint after restarting the test container")
    args = parser.parse_args()
    auth_path = integration.ROOT / ".dev" / args.jellyfin / "auth.json"
    auth = json.loads(auth_path.read_text())
    fixture_guard(auth)
    if args.verify_persistence:
        checkpoint = json.loads((auth_path.parent / "voting-checkpoint.json").read_text())
        verify_persistence(auth, checkpoint["seriesId"], checkpoint["votedAt"])
        raise SystemExit(0)
    page = request(JELLYFIN, "/MediaRemover/Series?" + urlencode({"search": integration.TITLE}), token=auth["adminToken"])
    series = next(item for item in page["items"] if item["name"] == integration.TITLE)
    voted_at = run(auth, series["id"])
    checkpoint = auth_path.parent / "voting-checkpoint.json"
    checkpoint.write_text(json.dumps({"seriesId": series["id"], "votedAt": voted_at}))
    print("Voting checks passed. Restart the disposable container, then call verify_persistence with voting-checkpoint.json.")
