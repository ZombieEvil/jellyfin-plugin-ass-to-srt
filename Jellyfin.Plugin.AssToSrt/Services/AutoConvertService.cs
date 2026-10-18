using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssToSrt.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssToSrt.Services;

/// <summary>
/// Converts the subtitles of new or updated videos without waiting for the nightly task,
/// and applies the "hide ASS tracks" option as soon as it changes.
/// </summary>
public sealed class AutoConvertService : IHostedService, IDisposable
{
    // Items are handled once they stopped changing for this long, so that ffprobe has finished.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly AssToSrtConverter _converter;
    private readonly ILogger<AutoConvertService> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _pending = new();
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;
    private int _running;
    private bool? _hideConvertedAss;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoConvertService"/> class.
    /// </summary>
    public AutoConvertService(ILibraryManager libraryManager, AssToSrtConverter converter, ILogger<AutoConvertService> logger)
    {
        _libraryManager = libraryManager;
        _converter = converter;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        if (Plugin.Instance is { } plugin)
        {
            _hideConvertedAss = plugin.Configuration.HideConvertedAss;
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _timer = new Timer(_ => _ = ProcessPendingAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
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
        var config = Plugin.Instance?.Configuration;
        if (config is null || !(config.AutoConvertNewItems || config.HideConvertedAss))
        {
            return;
        }

        if (e.Item is Video video && !video.IsVirtualItem && !video.IsFolder)
        {
            _pending[video.Id] = DateTime.UtcNow;
        }
    }

    private async Task ProcessPendingAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var due = _pending.Where(p => DateTime.UtcNow - p.Value >= SettleDelay).Select(p => p.Key).ToList();
            foreach (var id in due)
            {
                if (_stopping.IsCancellationRequested)
                {
                    return;
                }

                _pending.TryRemove(id, out _);
                if (_libraryManager.GetItemById(id) is not Video video || !_converter.IsHandled(video))
                {
                    continue;
                }

                try
                {
                    // Converting also hides the converted tracks when the option is on.
                    if (Plugin.Instance?.Configuration.AutoConvertNewItems == true)
                    {
                        await _converter.ProcessItemAsync(video, _stopping.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
