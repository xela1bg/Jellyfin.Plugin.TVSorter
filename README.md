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

The plugin metadata is in the repository-root `build.yaml`, following JPRM/Jellyfin plugin repository conventions. The GUID is `9f31de5a-5d0f-4a4a-a820-724b42d8a416`; the current version is `1.0.0.0`; the owner is `xela1bg`. Manual installation requires placing the plugin release package in a `plugins/TV Sorter` directory under Jellyfin's data directory and restarting the server.

## Plugin catalog and image

`TVSorter.png` is the supplied artwork. It is copied byte-for-byte to the JPRM-conventional root file `image.png`; JPRM embeds it in the package and uses it to create the catalog `imageUrl` when the repository manifest is generated. The release packaging step also writes Jellyfin's supported `imagePath: "image.png"` into the ZIP's local `meta.json`, so the installed plugin can serve the supplied image through Jellyfin's built-in plugin image endpoint. No custom endpoint or unsupported catalog property is used.

Jellyfin 12.3's installed-plugin API does not expose Developer or Repository fields. The Dashboard details page loads package info from configured repositories: Developer comes from the catalog `owner`, while the Repository link comes from the configured repository's name/URL attached to the matching package version. JPRM's supported catalog entry has no source-GitHub-URL property; the project source URL is therefore documented here and in the GitHub repository, rather than added as an unsupported manifest field.

The requested GitHub repository currently exists but is empty and has no Release asset. Consequently, there is not yet a truthful installable catalog entry or a hosted manifest URL. The tag workflow at `.github/workflows/release.yml` builds/tests and publishes `tv-sorter_<version>.zip` on a `v<version>` tag. After that asset exists, run `Publish-RepositoryManifest.ps1` with its actual browser download URL and the public base URL where the repository manifest will be hosted. JPRM then calculates the package checksum and creates/updates root `manifest.json` plus the `tv-sorter/image.png` catalog image. Commit those generated files and add the hosted `manifest.json` URL in Jellyfin's repository settings. Until the manifest is committed and publicly reachable, no repository URL should be entered or advertised as working.

For the first release, commit and push the source to the specified GitHub repository, push tag `v1.0.0.0`, and wait for the release workflow to upload `tv-sorter_1.0.0.0.zip`. Then run the manifest helper with that release's actual asset URL. The helper verifies the URL before generating repository metadata; it never creates a catalog `sourceUrl` for an unpublished asset.