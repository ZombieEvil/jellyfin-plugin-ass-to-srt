using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssToSrt.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssToSrt.Services;

/// <summary>
/// Background conversions: new videos, the episodes that follow the one being watched,
/// series asked from the settings page, cleanup of removed videos and the "hide ASS tracks" option.
/// </summary>
public sealed class AutoConvertService : IHostedService, IDisposable
{
    // New items are handled once they stopped changing for this long, so that ffprobe has finished.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly AssToSrtConverter _converter;
    private readonly ILogger<AutoConvertService> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _pending = new();
    private readonly ConcurrentQueue<Guid> _urgent = new();
    private readonly ConcurrentDictionary<Guid, byte> _urgentSet = new();
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;
    private int _running;
    private bool? _hideConvertedAss;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoConvertService"/> class.
    /// </summary>
    public AutoConvertService(ILibraryManager libraryManager, ISessionManager sessionManager, AssToSrtConverter converter, ILogger<AutoConvertService> logger)
    {
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _converter = converter;
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of videos waiting to be converted.
    /// </summary>
    public int QueueLength => _urgentSet.Count + _pending.Count;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Converts the given videos as soon as possible, ignoring the time window.
    /// </summary>
    /// <param name="videos">The videos.</param>
    /// <returns>The number of videos added to the queue.</returns>
    public int Enqueue(IEnumerable<Video> videos)
    {
        var added = 0;
        foreach (var video in videos)
        {
            if (_urgentSet.TryAdd(video.Id, 0))
            {
                _urgent.Enqueue(video.Id);
                added++;
            }
        }

        if (added > 0)
        {
            _timer?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15));
        }

        return added;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _sessionManager.PlaybackStart += OnPlaybackStart;
        if (Plugin.Instance is { } plugin)
        {
            _hideConvertedAss = plugin.Configuration.HideConvertedAss;
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _timer = new Timer(_ => _ = ProcessQueueAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _stopping.Dispose();
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        if (e is not PluginConfiguration config || config.HideConvertedAss == _hideConvertedAss)
        {
            return;
        }

        _hideConvertedAss = config.HideConvertedAss;
        _ = Task.Run(async () =>
        {
            try
            {
                if (config.HideConvertedAss)
                {
                    await _converter.HideAllAsync(_stopping.Token).ConfigureAwait(false);
                }
                else
                {
                    _converter.RestoreHiddenTracks();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ASS to SRT: cannot apply the \"hide ASS tracks\" option");
            }
        });
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var config = Config;
        var convertNew = config.AutoConvertNewItems && !IsUpcomingMode(config);
        if (!(convertNew || config.HideConvertedAss))
        {
            return;
        }

        if (e.Item is Video video && !video.IsVirtualItem && !video.IsFolder)
        {
            _pending[video.Id] = DateTime.UtcNow;
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (!Config.CleanupOnRemove || e.Item is not Video video)
        {
            return;
        }

        var id = video.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                await _converter.CleanupRemovedAsync(id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ASS to SRT: cleanup failed for a removed video");
            }
        });
    }

    // The episode being watched and the next ones are converted first.
    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (e.Item is not Video video || !_converter.IsHandled(video))
            {
                return;
            }

            var videos = new List<Video> { video };
            if (video is Episode episode)
            {
                videos.AddRange(_converter.GetUpcomingEpisodes(episode, Math.Clamp(Config.UpcomingCount, 0, 20)));
            }

            Enqueue(videos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ASS to SRT: cannot prepare the next episodes");
        }
    }

    private static bool IsUpcomingMode(PluginConfiguration config)
        => string.Equals(config.ConversionMode, "Upcoming", StringComparison.OrdinalIgnoreCase);

    private async Task ProcessQueueAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        var converted = new List<string>();
        try
        {
            // Requested by the user or for the next episodes: right away.
            while (!_stopping.IsCancellationRequested && _urgent.TryDequeue(out var id))
            {
                _urgentSet.TryRemove(id, out _);
                _pending.TryRemove(id, out _);
                await ProcessAsync(id, convert: true, urgent: true, converted).ConfigureAwait(false);
            }

            // New or updated videos: after the settle delay, and only in the time window.
            if (!AssToSrtConverter.IsInTimeWindow())
            {
                return;
            }

            var config = Config;
            var convertNew = config.AutoConvertNewItems && !IsUpcomingMode(config);
            var due = _pending.Where(p => DateTime.UtcNow - p.Value >= SettleDelay).Select(p => p.Key).ToList();
            foreach (var id in due)
            {
                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                _pending.TryRemove(id, out _);
                await ProcessAsync(id, convertNew, urgent: false, converted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            if (converted.Count > 0)
            {
                var names = string.Join(", ", converted.Distinct().Take(3)) + (converted.Distinct().Count() > 3 ? "…" : string.Empty);
                await _converter.LogActivityAsync(Messages.Batch(_converter.Culture, converted.Count, names)).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessAsync(Guid id, bool convert, bool urgent, List<string> converted)
    {
        if (_libraryManager.GetItemById(id) is not Video video || !_converter.IsHandled(video))
        {
            return;
        }

        try
        {
            if (convert)
            {
                if (await _converter.ProcessItemAsync(video, _stopping.Token, urgent).ConfigureAwait(false) > 0)
                {
                    converted.Add(video is Episode episode && !string.IsNullOrEmpty(episode.SeriesName) ? episode.SeriesName : video.Name);
                }
            }
            else
            {
                await _converter.HideItemAsync(video, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ASS to SRT: automatic conversion failed for {Path}", video.Path);
        }
    }
}
