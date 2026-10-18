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
        // Started from the plugin page: always a full conversion, whatever the mode and the time.
        var manual = _converter.ConsumeManualRun();
        var config = Plugin.Instance?.Configuration;

        if (!manual && string.Equals(config?.ConversionMode, "Upcoming", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("ASS to SRT: \"next episodes\" mode, no full conversion");
            await _converter.HideAllAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!manual && !AssToSrtConverter.IsInTimeWindow())
        {
            _logger.LogInformation("ASS to SRT: outside the time window, the conversion waits for the next run");
            return;
        }

        await _converter.ConvertAllAsync(progress, cancellationToken, respectTimeWindow: !manual).ConfigureAwait(false);
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
