using Jellyfin.Plugin.TVSorter.Configuration;
using Jellyfin.Plugin.TVSorter.Services;
using Jellyfin.Plugin.TVSorter.Models;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TVSorter.Controllers;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("TVSorter")]
public sealed class TvSorterController : ControllerBase
{
    private readonly BuildCoordinator _buildCoordinator;
    private readonly PathValidator _pathValidator;
    private readonly LibrarySelectionService _librarySelectionService;
    private readonly ILogger<TvSorterController> _logger;

    public TvSorterController(
        BuildCoordinator buildCoordinator,
        PathValidator pathValidator,
        LibrarySelectionService librarySelectionService,
        ILogger<TvSorterController> logger)
    {
        _buildCoordinator = buildCoordinator;
        _pathValidator = pathValidator;
        _librarySelectionService = librarySelectionService;
        _logger = logger;
    }

    [HttpGet("Status")]
    public ActionResult<BuildStatusSnapshot> GetStatus()
    {
        return Ok(_buildCoordinator.GetSnapshot());
    }

    [HttpGet("Libraries")]
    public ActionResult<LibrarySelectionResult> GetLibraries()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Ok(_librarySelectionService.GetState(plugin.Configuration));
    }

    [HttpPost("Configuration")]
    public ActionResult<LibrarySelectionResult> SaveConfiguration([FromBody] SaveConfigurationRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var current = plugin.Configuration;
        var updated = new PluginConfiguration
        {
            SourcePath = request.SourcePath,
            TargetPath = current.TargetPath,
            SelectedLibraryId = request.SelectedLibraryId,
            SelectedLibraryLocation = request.SelectedLibraryLocation,
            RefreshInterval = request.RefreshInterval,
            ScanLibraryAfterRefresh = request.ScanLibraryAfterRefresh,
            UseTvmaze = request.UseTvmaze
        };

        if (!_librarySelectionService.TryApplySelection(updated, current, out var selection))
        {
            _logger.LogWarning("TV Sorter settings were not saved because the Shows library selection is invalid: {Error}", selection.Error);
            return BadRequest(selection);
        }

        var targetChangeWarning = selection.Warning;
        plugin.UpdateConfiguration(updated);
        selection = _librarySelectionService.GetState(updated);
        if (!string.IsNullOrWhiteSpace(targetChangeWarning))
        {
            selection.Warning = targetChangeWarning;
        }

        return Ok(selection);
    }

    [HttpPost("Run")]
    public async Task<ActionResult> Run(CancellationToken cancellationToken)
    {
        var result = await _buildCoordinator.RunAsync(cancellationToken).ConfigureAwait(false);
        return result.Status == BuildStatus.AlreadyRunning
            ? Conflict(result)
            : Ok(result);
    }

    [HttpPost("TestPaths")]
    public async Task<ActionResult<TestPathsResult>> TestPaths(
        [FromBody] PathTestRequest request,
        CancellationToken cancellationToken)
    {
        var current = Plugin.Instance?.Configuration;
        if (current is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var candidate = new PluginConfiguration
        {
            SourcePath = request.SourcePath,
            TargetPath = current.TargetPath,
            SelectedLibraryId = request.SelectedLibraryId,
            SelectedLibraryLocation = request.SelectedLibraryLocation,
            RefreshInterval = current.RefreshInterval,
            ScanLibraryAfterRefresh = current.ScanLibraryAfterRefresh,
            UseTvmaze = current.UseTvmaze
        };
        var selection = _librarySelectionService.GetState(candidate);
        if (!selection.IsValid)
        {
            var selectionFailure = new TestPathsResult
            {
                SourcePath = request.SourcePath,
                SourceStatus = "Not tested",
                LibraryName = selection.SelectedLibraryName ?? string.Empty,
                LibraryPath = selection.EffectiveTargetPath,
                TargetStatus = "Not tested",
                SymlinkStatus = "Not tested",
                Error = selection.Error
            };
            return BadRequest(selectionFailure);
        }

        var paths = await _pathValidator.ValidateAsync(request.SourcePath, selection.EffectiveTargetPath, cancellationToken).ConfigureAwait(false);
        var result = new TestPathsResult
        {
            Success = paths.Success,
            SourcePath = paths.SourcePath.Length > 0 ? paths.SourcePath : request.SourcePath,
            SourceStatus = paths.SourceAccessible ? "Readable" : "Not readable",
            LibraryName = selection.SelectedLibraryName ?? string.Empty,
            LibraryPath = paths.TargetPath.Length > 0 ? paths.TargetPath : selection.EffectiveTargetPath,
            TargetStatus = paths.TargetWritable ? "Writable" : paths.SourceAccessible ? "Not writable" : "Not tested",
            SymlinkStatus = paths.SymlinkSupported ? "Supported" : paths.TargetWritable ? "Unavailable" : "Not tested",
            Error = paths.Error
        };
        if (!paths.Success)
        {
            _logger.LogWarning("TV Sorter path test failed: {Error}", paths.Error);
        }

        return paths.Success ? Ok(result) : BadRequest(result);
    }
}

public sealed class PathTestRequest
{
    public string SourcePath { get; set; } = string.Empty;

    public string? SelectedLibraryId { get; set; }

    public string? SelectedLibraryLocation { get; set; }
}