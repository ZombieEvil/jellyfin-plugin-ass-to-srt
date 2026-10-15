using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssToSrt.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssToSrt.ScheduledTasks;

/// <summary>
/// Scans the libraries and creates the missing SRT copies of ASS subtitles.
/// </summary>
public class ConvertAssSubtitlesTask : IScheduledTask
{
    private readonly AssToSrtConverter _converter;
    private readonly ILogger<ConvertAssSubtitlesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConvertAssSubtitlesTask"/> class.
    /// </summary>
    /// <param name="converter">The converter.</param>
    /// <param name="logger">The logger.</param>
    public ConvertAssSubtitlesTask(AssToSrtConverter converter, ILogger<ConvertAssSubtitlesTask> logger)
    {
        _converter = converter;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Convert ASS subtitles to SRT";

    /// <inheritdoc />
    public string Key => "AssToSrtConvert";

    /// <inheritdoc />
    public string Description => "Creates an SRT copy of every ASS/SSA subtitle that has no SRT equivalent yet.";

    /// <inheritdoc />
    public string Category => "ASS to SRT";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = _converter.GetCandidateItems();
        var created = 0;
        var failed = 0;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                created += await _converter.ProcessItemAsync(items[i], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogError(ex, "ASS to SRT: error while processing {Path}", items[i].Path);
            }

            progress.Report(100.0 * (i + 1) / items.Count);
        }

        _logger.LogInformation("ASS to SRT: {Count} videos checked, {Created} SRT created, {Failed} errors", items.Count, created, failed);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}
