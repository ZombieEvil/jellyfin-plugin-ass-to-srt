using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssToSrt.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AssToSrt.ScheduledTasks;

/// <summary>
/// Deletes the SRT files created by the plugin and converts again, to apply new settings to existing files.
/// </summary>
public class RegenerateSrtTask : IScheduledTask
{
    private readonly AssToSrtConverter _converter;

    /// <summary>
    /// Initializes a new instance of the <see cref="RegenerateSrtTask"/> class.
    /// </summary>
    /// <param name="converter">The converter.</param>
    public RegenerateSrtTask(AssToSrtConverter converter)
    {
        _converter = converter;
    }

    /// <inheritdoc />
    public string Name => "Regenerate SRT subtitles (apply new settings)";

    /// <inheritdoc />
    public string Key => "AssToSrtRegenerate";

    /// <inheritdoc />
    public string Description => "Deletes the SRT files created by ASS to SRT and creates them again with the current settings. SRT files you added yourself are never touched.";

    /// <inheritdoc />
    public string Category => "ASS to SRT";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var deleted = await _converter.DeleteCreatedSubtitlesAsync().ConfigureAwait(false);
        progress.Report(1);
        await _converter.ConvertAllAsync(new Progress<double>(p => progress.Report(1 + (p * 0.99))), cancellationToken, deleted).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
