using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssToSrt.Configuration;
using Jellyfin.Plugin.AssToSrt.ScheduledTasks;
using Jellyfin.Plugin.AssToSrt.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AssToSrt.Api;

/// <summary>
/// Actions of the settings page: preview, convert a series, statistics.
/// </summary>
[ApiController]
[Route("AssToSrt")]
[Authorize(Policy = "RequiresElevation")]
public class AssToSrtController : ControllerBase
{
    private readonly AssToSrtConverter _converter;
    private readonly AutoConvertService _queue;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssToSrtController"/> class.
    /// </summary>
    public AssToSrtController(AssToSrtConverter converter, AutoConvertService queue, ITaskManager taskManager)
    {
        _converter = converter;
        _queue = queue;
        _taskManager = taskManager;
    }

    /// <summary>
    /// Converts the beginning of a video's ASS tracks with the given settings, without writing anything.
    /// </summary>
    /// <param name="request">Video and settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The original and converted subtitles.</returns>
    [HttpPost("Preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<PreviewTrack>>> Preview([FromBody] PreviewRequest request, CancellationToken cancellationToken)
    {
        var result = await _converter.PreviewAsync(request.ItemId, request.Settings ?? new PluginConfiguration(), cancellationToken).ConfigureAwait(false);
        return result is null ? NotFound() : result;
    }

    /// <summary>
    /// Converts a series, a season, a folder or a single video right away.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The number of videos added to the queue.</returns>
    [HttpPost("Convert/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<int> Convert([FromRoute] Guid itemId)
        => _queue.Enqueue(_converter.ResolveVideos(itemId));

    /// <summary>
    /// Starts a full conversion of the selected libraries, ignoring the mode and the time window.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("ConvertAll")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ConvertAll()
    {
        _converter.RequestManualRun();
        _taskManager.QueueScheduledTask<ConvertAssSubtitlesTask>();
        return NoContent();
    }

    /// <summary>
    /// Deletes and creates again the SRT files of the plugin with the current settings.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpPost("Regenerate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Regenerate()
    {
        _taskManager.QueueScheduledTask<RegenerateSrtTask>();
        return NoContent();
    }

    /// <summary>
    /// Gets the statistics.
    /// </summary>
    /// <returns>The statistics.</returns>
    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ConversionStats> Stats()
    {
        var stats = _converter.GetStats();
        stats.Queued = _queue.QueueLength;
        return stats;
    }
}

/// <summary>
/// Body of the preview request.
/// </summary>
public class PreviewRequest
{
    /// <summary>Gets or sets the video.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the settings to try.</summary>
    public PluginConfiguration? Settings { get; set; }
}
