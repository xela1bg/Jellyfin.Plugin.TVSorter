using System.Reflection;
using Jellyfin.Plugin.TVSorter.Configuration;
using Jellyfin.Plugin.TVSorter.Models;
using Jellyfin.Plugin.TVSorter.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TVSorter.Tests.Services;

public sealed class BuildCoordinatorTests
{
    [Fact]
    public async Task BuilderReceivesSelectedLocationInsteadOfStaleTargetPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "tv-sorter-selected-target-" + Guid.NewGuid().ToString("N"));
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var staleTarget = Directory.CreateDirectory(Path.Combine(root, "old-target")).FullName;
        var selectedTarget = Directory.CreateDirectory(Path.Combine(root, "selected-target")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        string? builderTarget = null;

        try
        {
            var configuration = new PluginConfiguration
            {
                SourcePath = source,
                TargetPath = staleTarget,
                SelectedLibraryId = "selected-shows",
                SelectedLibraryLocation = selectedTarget
            };
            var builder = new CallbackBuilder(_ =>
            {
                return Task.FromResult(new BuildResult { Status = BuildStatus.Success });
            }, (_, targetPath, _) =>
            {
                builderTarget = targetPath;
                return Task.FromResult(new BuildResult { Status = BuildStatus.Success });
            });
            var coordinator = new BuildCoordinator(
                builder,
                DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>(),
                new BuildStatusStore(data),
                new LibrarySelectionService(new TestShowsLibraryProvider(
                    [new ShowsLibrary("selected-shows", "Family Television", [selectedTarget])])),
                () => configuration,
                NullLogger<BuildCoordinator>.Instance);

            var result = await coordinator.RunAsync(CancellationToken.None);

            Assert.Equal(BuildStatus.Success, result.Status);
            Assert.True(SymlinkManager.PathEquals(selectedTarget, builderTarget!));
            Assert.False(SymlinkManager.PathEquals(staleTarget, builderTarget!));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AlreadyRunningRequestDoesNotReplaceLastCompletedResult()
    {
        var root = Path.Combine(Path.GetTempPath(), "tv-sorter-coordinator-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(data);

        try
        {
            var callCount = 0;
            var secondBuild = new TaskCompletionSource<BuildResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var builder = new CallbackBuilder(_ =>
            {
                return Interlocked.Increment(ref callCount) == 1
                    ? Task.FromResult(new BuildResult { Status = BuildStatus.Success })
                    : secondBuild.Task;
            });
            var configuration = new PluginConfiguration
            {
                SourcePath = source,
                TargetPath = target,
                SelectedLibraryId = "shows-id",
                SelectedLibraryLocation = target,
                ScanLibraryAfterRefresh = false
            };
            var selectionService = new LibrarySelectionService(new TestShowsLibraryProvider(
                [new ShowsLibrary("shows-id", "English Series", [target])]));
            var coordinator = new BuildCoordinator(
                builder,
                DispatchProxy.Create<ILibraryManager, LibraryManagerProxy>(),
                new BuildStatusStore(data),
                selectionService,
                () => configuration,
                NullLogger<BuildCoordinator>.Instance);

            var completed = await coordinator.RunAsync(CancellationToken.None);
            Assert.Equal(BuildStatus.Success, completed.Status);
            var runningBuild = coordinator.RunAsync(CancellationToken.None);
            Assert.True(coordinator.IsRunning);
            var lastCompleted = coordinator.LastResult;

            var alreadyRunning = await coordinator.RunAsync(CancellationToken.None);

            Assert.Equal(BuildStatus.AlreadyRunning, alreadyRunning.Status);
            Assert.Same(lastCompleted, coordinator.LastResult);

            secondBuild.SetResult(new BuildResult { Status = BuildStatus.Success });
            Assert.Equal(BuildStatus.Success, (await runningBuild).Status);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class CallbackBuilder(
        Func<CancellationToken, Task<BuildResult>> callback,
        Func<string, string, CancellationToken, Task<BuildResult>>? synchronize = null) : IVirtualTvLibraryBuilder
    {
        public Task<BuildResult> SynchronizeAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
        {
            return synchronize is null ? callback(cancellationToken) : synchronize(sourcePath, targetPath, cancellationToken);
        }
    }

    private sealed class TestShowsLibraryProvider(IReadOnlyList<ShowsLibrary> libraries) : IShowsLibraryProvider
    {
        public IReadOnlyList<ShowsLibrary> GetShowsLibraries() => libraries;
    }
}

public class LibraryManagerProxy : DispatchProxy
{
    public List<MediaBrowser.Model.Entities.VirtualFolderInfo> VirtualFolders { get; set; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "GetVirtualFolders")
        {
            return VirtualFolders;
        }

        throw new NotSupportedException($"Unexpected ILibraryManager call: {targetMethod?.Name}");
    }
}