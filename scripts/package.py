#!/usr/bin/env python3
"""Build an installable ZIP containing the plugin assembly, manifest, and license."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PLUGIN_ID = "8b60f2b0-8e08-4b9c-a6e2-5e4f4d519980"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jellyfin", choices=["12.1.0", "10.11.11"], default="10.11.11")
    args = parser.parse_args()
    project = ROOT / "src/Jellyfin.Plugin.MediaRemover/Jellyfin.Plugin.MediaRemover.csproj"
    properties = ET.parse(project).getroot()
    version = properties.findtext("PropertyGroup/Version")
    assembly_version = properties.findtext("PropertyGroup/AssemblyVersion")
    if not version or not assembly_version:
        raise RuntimeError("The plugin project must declare its release and assembly versions.")
    output = ROOT / "artifacts" / ("jellyfin-" + args.jellyfin)
    subprocess.run([
        os.environ.get("DOTNET_BIN", "dotnet"), "build",
        str(project),
        "--configuration", "Release", "-p:JellyfinVersion=" + args.jellyfin,
        "--output", str(output),
    ], check=True, cwd=ROOT)
    assembly = output / "Jellyfin.Plugin.MediaRemover.dll"
    assembly_bytes = assembly.read_bytes()
    if any(str(ROOT).encode(encoding) in assembly_bytes for encoding in ("utf-8", "utf-16le")):
        raise RuntimeError("The release assembly contains the local checkout path; refusing to package it.")
    manifest = {
        "category": "General", "changelog": "Voting views and admin tables now show posters: a poster grid in Deletion votes, a scrolling poster row of up to ten items on Home, and thumbnails in the admin library, vote list and review dialog. Advisory votes now cover movies, series, music, books, photos, collections, and playlists. Episode and season actions keep voting for the whole series. Admin movie removal now integrates Radarr and Seerr with preview, title confirmation, and resumable cleanup. Existing votes and Home preferences are preserved.",
        "description": "Advisory votes for library media, with movie and series progress and Radarr/Sonarr/Seerr removal.",
        "guid": PLUGIN_ID, "name": "Media Remover", "overview": "Library voting and admin media removal",
        "owner": "Doc", "targetAbi": args.jellyfin + ".0", "version": assembly_version,
    }
    archive = ROOT / "artifacts" / ("media-remover-" + version + "-jellyfin-" + args.jellyfin + ".zip")
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as package:
        package.write(assembly, "Jellyfin.Plugin.MediaRemover.dll")
        package.write(ROOT / "LICENSE", "LICENSE")
        package.writestr("meta.json", json.dumps(manifest, indent=2) + "\n")
    checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".zip.sha256").write_text(checksum + "  " + archive.name + "\n")
    print(archive)


if __name__ == "__main__":
    main()
