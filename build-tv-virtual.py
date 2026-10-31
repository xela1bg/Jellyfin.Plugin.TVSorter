#!/usr/bin/env python3
"""
Build a clean Jellyfin TV tree with symlinks from a messy source library.
Does NOT move or rename original files.
"""

import argparse
import os
import re
import sys
from pathlib import Path

# ---------- CONFIG ----------
SOURCE = Path("/mnt/media/Others/Series")
TARGET = Path("/mnt/jellyfin-tv")          # must be on a local FS that supports symlinks
VIDEO_EXT = {".mkv", ".mp4", ".avi", ".m4v", ".ts", ".m2ts", ".wmv", ".mov"}
IGNORE_NAMES = {
    "screens", "sample", "samples", "subs", "subtitles", "proof",
    "new upcoming releases by xclusive.txt",
}
IGNORE_SUFFIXES = {".txt", ".nfo", ".jpg", ".jpeg", ".png", ".srt", ".idx", ".sub", ".part"}

# Release / quality junk to strip from show names
TAG_RE = re.compile(
    r"""(?ix)
    (?:
        \b(?:
            2160p|1080p|720p|480p|360p|
            web-?dl|webrip|web|hdtv|bluray|bdrip|brrip|dvdrip|hddvd|
            x264|x265|h\.?264|h\.?265|hevc|avc|xvid|
            aac|ac3|eac3|ddp?5\.1|ddp?2\.0|atmos|dts|
            proper|repack|internal|extended|unrated|directors\.cut|
            multi|dual|ita|eng|ger|fre|
            amzn|nf|dsnp|atvp|hulu|pcok|stan|syfy|fxnow|
            complete|season|series
        )\b
        |
        \[.*?\]
        |
        \{.*?\}
        |
        \.(?:mkv|mp4|avi|m4v)$
    )
    """,
)

# Primary SxxExx (and SxxExx-Eyy) pattern
EP_RE = re.compile(
    r"""(?ix)
    (?:^|[\s._-])
    (?:
        s(?P<season>\d{1,2})[\s._-]*e(?P<episode>\d{1,3})
        (?:[\s._-]*e?(?P<episode2>\d{1,3}))?     # optional multi-ep
        |
        (?P<season2>\d{1,2})x(?P<episode3>\d{1,3})
    )
    (?:[\s._-]|$)
    """
)

# Season-only (for season packs): S01, Season 01, Season 1
SEASON_ONLY_RE = re.compile(
    r"""(?ix)
    (?:^|[\s._-])
    (?:
        s(?P<season>\d{1,2})
        |
        season[\s._-]*(?P<season2>\d{1,2})
    )
    (?:[\s._-]|$)
    """
)

def is_video(path: Path) -> bool:
    if path.suffix.lower() not in VIDEO_EXT:
        return False
    name = path.name.lower()
    if name.endswith(".part"):
        return False
    if any(x in name for x in ("sample", "trailer")):
        return False
    return True

def should_skip_dir(name: str) -> bool:
    n = name.lower().strip()
    return n in IGNORE_NAMES or n.startswith(".")

def clean_show_name(raw: str) -> str:
    """Turn 'American.Gods.S01.COMPLETE...' into 'American Gods'."""
    s = raw

    # Cut at first season/episode marker if present
    m = EP_RE.search(s) or SEASON_ONLY_RE.search(s)
    if m:
        s = s[: m.start()]

    # Dots/underscores → spaces
    s = re.sub(r"[._]+", " ", s)

    # Remove leftover tags
    s = TAG_RE.sub(" ", s)

    # Collapse spaces, strip
    s = re.sub(r"\s+", " ", s).strip(" -._")

    # Light title case (keeps small words reasonable enough)
    if s and s.islower() or "." not in s:
        s = s.title()

    return s or "Unknown"

def parse_season_episode(text: str):
    """Return (season:int, episode:int|None, episode2:int|None) or None."""
    m = EP_RE.search(text)
    if m:
        season = int(m.group("season") or m.group("season2"))
        ep = int(m.group("episode") or m.group("episode3"))
        ep2 = m.group("episode2")
        ep2 = int(ep2) if ep2 else None
        return season, ep, ep2

    m = SEASON_ONLY_RE.search(text)
    if m:
        season = int(m.group("season") or m.group("season2"))
        return season, None, None

    return None

def link_file(src: Path, dest: Path, dry_run: bool, stats: dict):
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.exists() or dest.is_symlink():
        if dest.is_symlink() and dest.resolve() == src.resolve():
            stats["skipped"] += 1
            return
        if not dry_run:
            dest.unlink()

    print(f"  LINK  {src}  →  {dest}")
    stats["linked"] += 1
    if not dry_run:
        dest.symlink_to(src)
def walk(source: Path, target: Path, dry_run: bool):
    if not source.is_dir():
        print(f"ERROR: source not found: {source}", file=sys.stderr)
        sys.exit(1)

    stats = {"linked": 0, "skipped": 0, "guessed": 0, "videos": 0}
    print(f"Source : {source}")
    print(f"Target : {target}")
    print(f"Mode   : {'DRY-RUN' if dry_run else 'LIVE'}")
    print("-" * 60)

    for root, dirs, files in os.walk(source):
        dirs[:] = [d for d in dirs if not should_skip_dir(d)]

        root_path = Path(root)
        try:
            rel = root_path.relative_to(source)
        except ValueError:
            continue

        series_hint = None
        if len(rel.parts) >= 1:
            top = rel.parts[0]
            if parse_season_episode(top) is None and not should_skip_dir(top):
                series_hint = clean_show_name(top)

        for name in files:
            path = root_path / name
            if path.suffix.lower() in IGNORE_SUFFIXES:
                continue
            if not is_video(path):
                continue

            stats["videos"] += 1
            process_video(path, series_hint, target, dry_run, stats)

    print("-" * 60)
    print(f"Videos found : {stats['videos']}")
    print(f"Links created: {stats['linked']}")
    print(f"Already OK   : {stats['skipped']}")
    print(f"Guessed S01  : {stats['guessed']}")
    if dry_run:
        print("\nDry-run only. Re-run with --apply to create symlinks.")


def process_video(video: Path, series_hint: str | None, target: Path, dry_run: bool, stats: dict):
    candidates = [video.name, video.parent.name]
    if video.parent.parent != video.parent:  # has a parent
        candidates.append(video.parent.parent.name)

    season = episode = episode2 = None
    show = series_hint

    for text in candidates:
        parsed = parse_season_episode(text)
        if parsed:
            season, episode, episode2 = parsed
            if not show:
                show = clean_show_name(text)
            break

    if season is None:
        show = show or clean_show_name(video.parent.name)
        season = 1
        episode = None
        stats["guessed"] += 1

    show = show or "Unknown"
    season_dir = target / show / f"Season {season:02d}"

    if episode is not None:
        if episode2:
            new_name = f"{show} S{season:02d}E{episode:02d}-E{episode2:02d}{video.suffix.lower()}"
        else:
            new_name = f"{show} S{season:02d}E{episode:02d}{video.suffix.lower()}"
    else:
        new_name = video.name

    dest = season_dir / new_name
    link_file(video, dest, dry_run, stats)


def main():
    parser = argparse.ArgumentParser(description="Build clean Jellyfin TV symlink tree")
    parser.add_argument("--apply", action="store_true", help="Actually create symlinks (default is dry-run)")
    parser.add_argument("--source", type=Path, default=Path("/mnt/media/Others/Series"))
    parser.add_argument("--target", type=Path, default=Path("/mnt/jellyfin-tv"))
    args = parser.parse_args()

    walk(source=args.source, target=args.target, dry_run=not args.apply)


if __name__ == "__main__":
    main()