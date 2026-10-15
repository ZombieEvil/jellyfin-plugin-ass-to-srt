using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssToSrt.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AssToSrt.ScheduledTasks;

/// <summary>
/// Scans the libraries and creates the missing SRT copies of ASS subtitles.
/// </summary>
public class ConvertAssSubtitlesTask : IScheduledTask
{
    private readonly AssToSrtConverter _converter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConvertAssSubtitlesTask"/> class.
    /// </summary>
    /// <param name="converter">The converter.</param>
    public ConvertAssSubtitlesTask(AssToSrtConverter converter)
    {
        _converter = converter;
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
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _converter.ConvertAllAsync(progress, cancellationToken);

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
