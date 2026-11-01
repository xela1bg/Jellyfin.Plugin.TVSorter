# TV Sorter

![TV Sorter](image.png)

TV Sorter synchronizes a selected Jellyfin Shows library location from a messy unsorted media directory using file symbolic links. Source media is never renamed, moved, edited, or deleted. The ownership manifest and most recent structured result are stored under Jellyfin's application data directory, outside the virtual library.

Developer: [xela1bg](https://github.com/xela1bg)  
Repository: [Jellyfin.Plugin.TVSorter](https://github.com/xela1bg/Jellyfin.Plugin.TVSorter)

## Target

This project targets Jellyfin Server 12.3 with `net10.0`, `Jellyfin.Controller` 12.3.0, and `Jellyfin.Model` 12.3.0. The official `jellyfin-plugin-template` currently still contains 10.11.5 package pins and a `net9.0` project. TV Sorter follows its plugin configuration-page, plugin identity, packaging, and dependency-exclusion conventions while using one consistent Jellyfin 12.3 API/package/framework target.

## Parser behavior

`TvEpisodeParser` ports the patterns and fallback order in `build-tv-virtual.py`. The Python script remains the behavioral reference. In particular, season-only packs without episode markers keep their original file names, unmarked files fall back to Season 01, and the parser does not add common-but-unsupported filename syntaxes.

Python `str.title()` and .NET invariant `TextInfo.ToTitleCase()` intentionally differ for all-uppercase words: Python turns `SHOW` into `Show`, while .NET preserves `SHOW` as an acronym. A parser test records that behavior. An accented Latin title-case case is also tested. Invariant casing keeps output stable across host locales.

## Automatic show names

After parsing, TV Sorter removes only trailing four-digit years (`Foundation (2021)` or `Foundation 2021`) and normalizes casing for local identity. Numeric titles such as `1923`, `1883`, `3 Body Problem`, `Station 19`, and `Hawaii Five-0` are preserved. Unmarked files and season-only packs still keep their original file names.

**Use TVmaze to improve TV show names** is enabled by default. TVmaze's public [show search API](https://www.tvmaze.com/api) requires no account or API key. Episodes are grouped by normalized show candidate, with at most one request per group per run. Clear matches use TVmaze's canonical name for both the show directory and episode filenames; successful mappings (local name, TVmaze ID, canonical name, and any local year evidence) are cached in `TVSorter/show-names.json` under Jellyfin's application data directory and reused across scheduled runs and restarts. Disabling the option uses local names without requesting TVmaze or applying its cached mappings.

Matches require at least 88% normalized-name similarity, strict combined confidence, and a clear margin over competing results. TVmaze search scores are ranking signals, not probabilities or an absolute confidence cutoff. Available premiere years help distinguish remakes; conflicting year evidence falls back locally. Requests have a two-second timeout and an eight-second total online budget per synchronization. Lookup attempts rotate across scheduled runs so the budget or a failed request does not repeatedly exclude the same shows. A transport error, timeout, or unsuccessful HTTP response stops additional online requests for that run; cached matches remain usable. Unavailable, empty, malformed, ambiguous, unsafe, or low-confidence responses never block normal link processing. There is no confirmation screen, unmatched queue, or manual-alias requirement.

If a typo search returns an empty result, TV Sorter can reuse an unambiguous TVmaze-verified canonical name already cached or verified elsewhere in the same run. This requires at least 90% similarity, at most one character edit in a name of at least eight normalized characters, compatible year evidence, and a clear margin over other verified show identities. For example, `Hawaii Flve 0` can reuse a verified `Hawaii Five-0` mapping. Ambiguous search responses are never overridden this way, and untracked target directories are never used as naming evidence.

When a name changes, canonical links are created first. Old links are removed only when ownership is verified and any desired replacements are successfully reconciled. Only recorded, empty managed directories are removed. Untracked files, symlinks, and directories are left untouched, even if they prevent an old show directory from disappearing.

Tests use mocked HTTP responses and never contact TVmaze.

## Safety and limits

The builder requires canonical source and target trees not to overlap, and neither tree may contain the plugin's external data directory. It refuses unmanaged destination entries and duplicate desired destinations, only replaces/removes symbolic links whose stored target matches the ownership manifest, and reads dangling link targets without resolving them. Conflicts and safe skips are structured informational outcomes and do not fail an otherwise completed synchronization. With a missing manifest, existing destinations are treated as unowned conflicts while absent desired destinations may be created; an invalid manifest stops synchronization before media-tree changes. Filesystem operations are synchronous and cannot always be interrupted mid-call; cancellation is checked between operations.

Jellyfin 12.3's public `ILibraryManager.QueueLibraryScan()` queues a broader library scan. There is no public scoped scan API in the selected version. If enabled, TV Sorter queues that broader scan only after a successful synchronization.

## Library selection

The Dashboard reads public `ILibraryManager.GetVirtualFolders()` data and includes only libraries whose `CollectionType` is `CollectionTypeOptions.tvshows`. It persists Jellyfin's `VirtualFolderInfo.ItemId` plus the selected canonical location; the legacy `TargetPath` remains stored as the effective runtime path for compatibility. A single location is resolved automatically. Multi-location libraries require an explicit choice. Existing targets are migrated only when they canonically match exactly one Shows-library location; ambiguous or unmatched targets are preserved and require user action. Ownership manifests are keyed and validated by canonical target root, so changing locations does not clean or reuse the old target's manifest.

Symbolic links require host filesystem and process support. TV Sorter tests this capability using a temporary link in the target directory; support is not guaranteed on every OS, filesystem, mount, container, or permission configuration. A broken symbolic link in the source is skipped as an inaccessible source candidate rather than reproduced as another dangling link.

## Build and tests

With the .NET 10 SDK installed:

```powershell
dotnet build Jellyfin.Plugin.TVSorter.sln
dotnet test Jellyfin.Plugin.TVSorter.sln
```

The filesystem tests require actual symbolic-link creation permissions and fail explicitly when the host does not permit it. On Windows, enable Developer Mode where permitted or run the tests yourself from a suitably privileged terminal. TV Sorter does not elevate itself. A successful build does not establish symbolic-link capability for the Jellyfin service account or its container.

The plugin metadata is in the repository-root `build.yaml`. The GUID is `9f31de5a-5d0f-4a4a-a820-724b42d8a416`; the current version is `1.0.0.4`; the owner is `xela1bg`.

## Installation and configuration

Published release assets are listed on the [GitHub Releases page](https://github.com/xela1bg/Jellyfin.Plugin.TVSorter/releases). For manual installation, place the release ZIP in a `plugins/TV Sorter` directory under Jellyfin's data directory and restart the server.

In the Jellyfin Dashboard, open TV Sorter settings, enter the unsorted media directory, select a Jellyfin Shows library and (when needed) its location, then save. Use Test Paths before running. Choose a refresh interval or leave it at Manual only; the optional scan setting requests Jellyfin's broader library scan after a successful synchronization.

## Disclaimer

TV Sorter was developed primarily with AI-assisted coding tools, with manual testing, review, and validation. It is an independent community project and is not an official Jellyfin plugin.