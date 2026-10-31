using Jellyfin.Plugin.TVSorter.Configuration;
using Jellyfin.Plugin.TVSorter.Models;
using Jellyfin.Plugin.TVSorter.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TVSorter.ScheduledTasks;

public sealed class RefreshVirtualTvLibraryTask : IScheduledTask
{
    private readonly BuildCoordinator _buildCoordinator;

    public RefreshVirtualTvLibraryTask(BuildCoordinator buildCoordinator)
    {
        _buildCoordinator = buildCoordinator;
    }

    public string Name => "Refresh TV Sorter Library";

    public string Key => "TVSorterRefresh";

    public string Description => "Synchronizes TV Sorter's virtual TV library with the configured source directory.";

    public string Category => "TV Sorter";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        var result = await _buildCoordinator.RunAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);

        if (result.Status is BuildStatus.Failed or BuildStatus.AlreadyRunning)
        {
            throw new InvalidOperationException(result.LastError.Length > 0
                ? result.LastError
                : "TV Sorter could not start because another synchronization is already running.");
        }

        if (result.Status == BuildStatus.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var interval = Plugin.Instance?.Configuration.RefreshInterval ?? RefreshInterval.Manual;
        var duration = interval switch
        {
            RefreshInterval.Every30Minutes => TimeSpan.FromMinutes(30),
            RefreshInterval.EveryHour => TimeSpan.FromHours(1),
            RefreshInterval.Every6Hours => TimeSpan.FromHours(6),
            RefreshInterval.Daily => TimeSpan.FromDays(1),
            _ => TimeSpan.Zero
        };

        return duration == TimeSpan.Zero
            ? []
            : [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = duration.Ticks }];
    }
}