using Jellyfin.Plugin.TVSorter.Configuration;
using Jellyfin.Plugin.TVSorter.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TVSorter.Services;

public sealed class BuildCoordinator
{
    private readonly IVirtualTvLibraryBuilder _builder;
    private readonly ILibraryManager _libraryManager;
    private readonly BuildStatusStore _statusStore;
    private readonly LibrarySelectionService _librarySelectionService;
    private readonly Func<PluginConfiguration?> _configurationProvider;
    private readonly ILogger<BuildCoordinator> _logger;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private int _isRunning;
    private BuildResult? _lastResult;

    public BuildCoordinator(
        IVirtualTvLibraryBuilder builder,
        ILibraryManager libraryManager,
        BuildStatusStore statusStore,
        LibrarySelectionService librarySelectionService,
        Func<PluginConfiguration?> configurationProvider,
        ILogger<BuildCoordinator> logger)
    {
        _builder = builder;
        _libraryManager = libraryManager;
        _statusStore = statusStore;
        _librarySelectionService = librarySelectionService;
        _configurationProvider = configurationProvider;
        _logger = logger;
        _lastResult = _statusStore.Load();
    }

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public BuildResult? LastResult => Volatile.Read(ref _lastResult);

    public async Task<BuildResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!await _executionGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new BuildResult
            {
                Status = BuildStatus.AlreadyRunning,
                Warnings = ["A TV Sorter synchronization is already running."]
            };
        }

        var startedAt = DateTimeOffset.UtcNow;
        Volatile.Write(ref _isRunning, 1);
        try
        {
            var configuration = _configurationProvider();
            if (configuration is null)
            {
                var completedAt = DateTimeOffset.UtcNow;
                return SetLastResult(new BuildResult
                {
                    Status = BuildStatus.Failed,
                    StartedAt = startedAt,
                    CompletedAt = completedAt,
                    Duration = completedAt - startedAt,
                    Errors = ["TV Sorter plugin configuration is unavailable."]
                });
            }

            var selection = _librarySelectionService.GetState(configuration);
            if (!selection.IsValid)
            {
                return SetLastResult(new BuildResult
                {
                    Status = BuildStatus.Failed,
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Errors = [selection.Error]
                });
            }

            _logger.LogInformation("TV Sorter synchronization requested for Jellyfin library {LibraryName} at {TargetPath}. Source: {SourcePath}", selection.SelectedLibraryName, selection.EffectiveTargetPath, configuration.SourcePath);
            var result = await _builder.SynchronizeAsync(configuration.SourcePath, selection.EffectiveTargetPath, cancellationToken).ConfigureAwait(false);
            if (result.Errors.Count > 0)
            {
                _logger.LogError("TV Sorter synchronization reported {ErrorCount} error(s). First error: {Error}", result.Errors.Count, result.Errors[0]);
            }

            if (result.Conflicts.Count > 0)
            {
                var firstConflict = result.Conflicts[0];
                _logger.LogWarning(
                    "TV Sorter synchronization reported {ConflictCount} destination conflict(s). First destination: {DestinationPath}; reason: {Reason}.",
                    result.Conflicts.Count,
                    firstConflict.DestinationPath,
                    firstConflict.Reason);
            }

            if (result.IsSuccessful && cancellationToken.IsCancellationRequested)
            {
                result.Status = BuildStatus.Cancelled;
                result.Warnings.Add("Synchronization completed, but cancellation was requested before the optional library scan.");
            }

            if (result.IsSuccessful && configuration.ScanLibraryAfterRefresh)
            {
                try
                {
                    _libraryManager.QueueLibraryScan();
                    result.LibraryScanQueued = true;
                    _logger.LogInformation("Queued Jellyfin's broader library scan after TV Sorter synchronization.");
                }
                catch (Exception exception)
                {
                    result.Warnings.Add($"TV Sorter completed, but Jellyfin's library scan could not be queued: {exception.Message}");
                    _logger.LogWarning(exception, "Could not queue Jellyfin's broader library scan.");
                }
            }

            return SetLastResult(result, configuration.SourcePath, selection.EffectiveTargetPath);
        }
        catch (OperationCanceledException)
        {
            var completedAt = DateTimeOffset.UtcNow;
            var cancelled = new BuildResult
            {
                Status = BuildStatus.Cancelled,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Duration = completedAt - startedAt,
                Warnings = ["Synchronization was cancelled before completion."]
            };
            return SetLastResult(cancelled);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TV Sorter synchronization failed unexpectedly.");
            var completedAt = DateTimeOffset.UtcNow;
            return SetLastResult(new BuildResult
            {
                Status = BuildStatus.Failed,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Duration = completedAt - startedAt,
                Errors = [exception.Message]
            });
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            _executionGate.Release();
        }
    }

    public BuildStatusSnapshot GetSnapshot()
    {
        return new BuildStatusSnapshot(IsRunning, LastResult);
    }

    private BuildResult SetLastResult(BuildResult result, string? effectiveSourcePath = null, string? effectiveTargetPath = null)
    {
        Volatile.Write(ref _lastResult, result);
        try
        {
            var configuration = _configurationProvider();
            if (configuration is null || !_statusStore.Save(
                result,
                effectiveSourcePath ?? configuration.SourcePath,
                effectiveTargetPath ?? configuration.TargetPath))
            {
                result.Warnings.Add("The latest result was not persisted because the configured paths overlap TV Sorter's private data location or are unavailable.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            result.Warnings.Add($"The latest result could not be saved: {exception.Message}");
            _logger.LogWarning(exception, "Could not persist the latest TV Sorter result.");
        }

        _logger.LogInformation(
            "TV Sorter synchronization finished with {Status}. Discovered: {FilesDiscovered}; episodes matched: {EpisodesMatched}; created: {LinksCreated}; unchanged: {LinksUnchanged}; updated: {LinksUpdated}; stale removed: {StaleLinksRemoved}; empty directories removed: {EmptyDirectoriesRemoved}; skipped: {FilesSkipped}; conflicts: {ConflictCount}; warnings: {WarningCount}; errors: {ErrorCount}.",
            result.Status,
            result.FilesDiscovered,
            result.EpisodesMatched,
            result.LinksCreated,
            result.LinksAlreadyCorrect,
            result.LinksUpdated,
            result.StaleLinksRemoved,
            result.EmptyDirectoriesRemoved,
            result.FilesSkipped,
            result.Conflicts.Count,
            result.Warnings.Count,
            result.Errors.Count);
        return result;
    }
}

public sealed record BuildStatusSnapshot(bool IsRunning, BuildResult? LastResult);