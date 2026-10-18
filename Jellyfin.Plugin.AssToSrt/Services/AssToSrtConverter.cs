using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AssToSrt.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssToSrt.Services;

/// <summary>
/// Finds ASS/SSA subtitle tracks and writes SRT copies next to the video.
/// </summary>
public sealed partial class AssToSrtConverter : IDisposable
{
    private static readonly string[] AssCodecs = { "ass", "ssa" };
    private static readonly string[] SrtCodecs = { "subrip", "srt" };
    private static readonly string[] VideoCollectionTypes = { "movies", "tvshows", "homevideos", "musicvideos", "mixed" };
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromMinutes(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaStreamRepository _mediaStreamRepository;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalizationManager _localizationManager;
    private readonly ISessionManager _sessionManager;
    private readonly IActivityManager _activityManager;
    private readonly IServerConfigurationManager _serverConfiguration;
    private readonly ILogger<AssToSrtConverter> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _manualRunRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssToSrtConverter"/> class.
    /// </summary>
    public AssToSrtConverter(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaStreamRepository mediaStreamRepository,
        IMediaEncoder mediaEncoder,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILocalizationManager localizationManager,
        ISessionManager sessionManager,
        IActivityManager activityManager,
        IServerConfigurationManager serverConfiguration,
        ILogger<AssToSrtConverter> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaStreamRepository = mediaStreamRepository;
        _mediaEncoder = mediaEncoder;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _localizationManager = localizationManager;
        _sessionManager = sessionManager;
        _activityManager = activityManager;
        _serverConfiguration = serverConfiguration;
        _logger = logger;
    }

    /// <summary>
    /// Marks the next full conversion as started by the user: it ignores the time window.
    /// </summary>
    public void RequestManualRun() => Interlocked.Exchange(ref _manualRunRequested, 1);

    /// <summary>
    /// Returns true once after <see cref="RequestManualRun"/>.
    /// </summary>
    /// <returns>Whether the current run was started by the user.</returns>
    public bool ConsumeManualRun() => Interlocked.Exchange(ref _manualRunRequested, 0) == 1;

    /// <summary>
    /// Returns true when background conversions are allowed now (always true without time window).
    /// </summary>
    /// <returns>Whether the current hour is in the configured time window.</returns>
    public static bool IsInTimeWindow()
    {
        var config = Config;
        if (!config.UseTimeWindow)
        {
            return true;
        }

        var hour = DateTime.Now.Hour;
        var start = Math.Clamp(config.WindowStartHour, 0, 23);
        var end = Math.Clamp(config.WindowEndHour, 0, 23);
        return start <= end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // List of the SRT files written by the plugin, so that they can be regenerated with new settings.
    private static string RegistryPath => Path.Combine(DataFolder, "created-subtitles.json");

    // Next to the plugin configuration: the plugin's own folder is replaced at every update.
    private static string DataFolder => Path.Combine(Path.GetDirectoryName(Plugin.Instance!.ConfigurationFilePath)!, "AssToSrt");

    // Videos whose ASS tracks are hidden from Jellyfin, so that they can be shown again.
    private static string HiddenPath => Path.Combine(DataFolder, "hidden-items.json");

    [GeneratedRegex(@"\b(forced|forc[ée]s?|foreign)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForcedTitle();

    [GeneratedRegex(@"\b(signs?|songs?|karaok[eé]|panneaux|chansons?|lyrics)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SignsTitle();

    [GeneratedRegex(@"\b(ass|ssa)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AssWord();

    [GeneratedRegex(@"[\\/:*?""<>|.\x00-\x1F]")]
    private static partial Regex UnsafeFileNameChars();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    /// <summary>
    /// Gets every video of the configured libraries.
    /// </summary>
    /// <returns>The videos to inspect.</returns>
    public IReadOnlyList<Video> GetCandidateItems()
    {
        var locations = GetLibraryLocations();
        if (locations.Count == 0)
        {
            return Array.Empty<Video>();
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                IsVirtualItem = false,
                IsFolder = false,
                Recursive = true
            })
            .OfType<Video>()
            .Where(v => IsInLibraries(v, locations))
            .ToList();
    }

    /// <summary>
    /// Returns true when the item belongs to one of the configured libraries.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns>Whether the plugin should handle this video.</returns>
    public bool IsHandled(Video video) => IsInLibraries(video, GetLibraryLocations());

    /// <summary>
    /// Converts every video of the configured libraries.
    /// </summary>
    /// <param name="progress">Progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="deletedItems">Videos whose SRT were deleted just before: refreshed even if nothing is recreated.</param>
    /// <param name="respectTimeWindow">Stop when the time window ends (scheduled runs).</param>
    /// <returns>A task.</returns>
    public async Task ConvertAllAsync(IProgress<double> progress, CancellationToken cancellationToken, IReadOnlySet<Guid>? deletedItems = null, bool respectTimeWindow = false)
    {
        var items = GetCandidateItems();
        var created = 0;
        var convertedVideos = 0;
        var failed = 0;

        if (!Config.HideConvertedAss)
        {
            RestoreHiddenTracks();
        }

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (respectTimeWindow && !IsInTimeWindow())
            {
                _logger.LogInformation("ASS to SRT: end of the time window, the conversion continues next time");
                break;
            }

            try
            {
                var count = await ProcessItemAsync(items[i], cancellationToken).ConfigureAwait(false);
                created += count;
                if (count > 0)
                {
                    convertedVideos++;
                }
                else if (deletedItems is not null && deletedItems.Contains(items[i].Id))
                {
                    // Otherwise Jellyfin would keep listing the deleted SRT until the next library scan.
                    QueueRefresh(items[i]);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogError(ex, "ASS to SRT: error while processing {Path}", items[i].Path);
            }

            progress.Report(100.0 * (i + 1) / items.Count);
        }

        _logger.LogInformation("ASS to SRT: {Count} videos checked, {Created} SRT created, {Failed} errors", items.Count, created, failed);
        SaveLastRun(items.Count, convertedVideos, created, failed);
        if (created > 0 || failed > 0)
        {
            await LogActivityAsync(Messages.FullRun(Culture, convertedVideos, created, failed)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes an entry in Jellyfin's activity log, when the option is on.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>A task.</returns>
    public async Task LogActivityAsync(string message)
    {
        if (!Config.LogActivity)
        {
            return;
        }

        try
        {
            await _activityManager.CreateAsync(new ActivityLog(message, "AssToSrt", Guid.Empty)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ASS to SRT: cannot write to the activity log");
        }
    }

    /// <summary>
    /// Gets the language of the server interface, used for activity messages.
    /// </summary>
    public string Culture => _serverConfiguration.Configuration.UICulture ?? "en";

    /// <summary>
    /// Returns the videos of a series, season, folder or the video itself.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The videos, in episode order.</returns>
    public List<Video> ResolveVideos(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is Video video)
        {
            return new List<Video> { video };
        }

        if (item is null)
        {
            return new List<Video>();
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = new[] { item.Id },
                MediaTypes = new[] { MediaType.Video },
                IsVirtualItem = false,
                IsFolder = false,
                Recursive = true
            })
            .OfType<Video>()
            .OrderBy(v => v.ParentIndexNumber ?? 0)
            .ThenBy(v => v.IndexNumber ?? 0)
            .ThenBy(v => v.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the episodes that follow the given one in its series.
    /// </summary>
    /// <param name="episode">The episode being watched.</param>
    /// <param name="count">How many episodes.</param>
    /// <returns>The following episodes.</returns>
    public List<Video> GetUpcomingEpisodes(Episode episode, int count)
    {
        if (count <= 0 || episode.SeriesId == Guid.Empty)
        {
            return new List<Video>();
        }

        var specials = (episode.ParentIndexNumber ?? 0) == 0;
        var episodes = ResolveVideos(episode.SeriesId)
            .Where(v => specials || (v.ParentIndexNumber ?? 0) != 0)
            .ToList();
        var index = episodes.FindIndex(v => v.Id == episode.Id);
        return index < 0 ? new List<Video>() : episodes.Skip(index + 1).Take(count).ToList();
    }

    /// <summary>
    /// Gets the statistics shown in the settings page.
    /// </summary>
    /// <returns>The statistics.</returns>
    public ConversionStats GetStats()
    {
        var registry = LoadRegistry();
        var existing = registry.Keys.Where(File.Exists).ToList();
        return new ConversionStats
        {
            SrtFiles = existing.Count,
            Videos = registry.Where(r => File.Exists(r.Key)).Select(r => ResolveItemId(r.Key, r.Value)).Where(g => g != Guid.Empty).Distinct().Count(),
            SizeBytes = existing.Sum(p => new FileInfo(p).Length),
            HiddenVideos = LoadIds(HiddenPath).Count
        };
    }

    /// <summary>
    /// Deletes the SRT files created for a video that was removed from the library.
    /// </summary>
    /// <param name="itemId">The removed video.</param>
    /// <returns>A task.</returns>
    public async Task CleanupRemovedAsync(Guid itemId)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var registry = LoadRegistry();
            var removed = 0;
            foreach (var (path, id) in registry.ToList())
            {
                if (ResolveItemId(path, id) != itemId)
                {
                    continue;
                }

                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    registry.Remove(path);
                    removed++;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    _logger.LogWarning(ex, "ASS to SRT: cannot delete {Path}", path);
                }
            }

            if (removed > 0)
            {
                SaveRegistry(registry);
                _logger.LogInformation("ASS to SRT: {Count} SRT files of a removed video deleted", removed);
            }

            var hidden = LoadIds(HiddenPath);
            if (hidden.Remove(itemId))
            {
                SaveIds(HiddenPath, hidden);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Converts the beginning of the ASS tracks of a video with the given settings, without writing anything.
    /// </summary>
    /// <param name="itemId">The video.</param>
    /// <param name="settings">Settings to try.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The preview, or null when the video has no ASS track.</returns>
    public async Task<List<PreviewTrack>?> PreviewAsync(Guid itemId, PluginConfiguration settings, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(itemId) is not Video video || string.IsNullOrEmpty(video.Path))
        {
            return null;
        }

        var streams = _mediaSourceManager.GetMediaStreams(video.Id).OrderBy(s => s.Index).ToList();
        var embeddedStreams = streams.Where(s => !s.IsExternal).ToList();
        var tracks = streams
            .Where(s => s.Type == MediaStreamType.Subtitle && IsCodec(s, AssCodecs))
            .OrderBy(IsForcedTrack)
            .ThenByDescending(s => s.IsDefault)
            .Take(2)
            .ToList();
        if (tracks.Count == 0)
        {
            return null;
        }

        var options = SrtOptions(settings);
        var result = new List<PreviewTrack>();
        const int PreviewMinutes = 10;
        foreach (var track in tracks)
        {
            string content;
            if (track.IsExternal)
            {
                content = await ReadTextAsync(track.Path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var temp = Path.Combine(Path.GetTempPath(), "jellyfin-asstosrt", Guid.NewGuid().ToString("N") + ".ass");
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                try
                {
                    // Only the first minutes: much faster than reading the whole file.
                    var args = new List<string>
                    {
                        "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-t", (PreviewMinutes * 60).ToString(CultureInfo.InvariantCulture), "-i", video.Path,
                        "-map", "0:" + embeddedStreams.IndexOf(track).ToString(CultureInfo.InvariantCulture), "-c:s", "copy", "-f", "ass", temp
                    };
                    var (exitCode, error) = await RunFfmpegAsync(args, cancellationToken, urgent: true).ConfigureAwait(false);
                    if (exitCode != 0 || !File.Exists(temp))
                    {
                        _logger.LogWarning("ASS to SRT: preview extraction failed for {Path}: {Error}", video.Path, error);
                        continue;
                    }

                    content = await ReadTextAsync(temp, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            var events = AssDocument.Parse(content).Where(e => e.Start < PreviewMinutes * 60_000).ToList();
            result.Add(new PreviewTrack
            {
                Title = string.IsNullOrEmpty(track.Title) ? track.Language ?? string.Empty : track.Title,
                Forced = IsForcedTrack(track),
                Original = events.Take(80).Select(e => new PreviewLine(e.Start, e.End, e.Kind.ToString(), e.Region.ToString(), e.Column.ToString(), string.Join(" / ", e.PlainText.Split('\n')))).ToList(),
                Converted = SrtWriter.BuildCues(events, options).Take(80).Select(c => new PreviewCue(c.Start, c.End, c.Row.ToString(), c.Column.ToString(), c.Lines)).ToList()
            });
        }

        return result;
    }

    /// <summary>
    /// Makes the hidden ASS tracks of the selected libraries visible again and waits for Jellyfin to list them,
    /// so that they can be converted again.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task ShowHiddenTracksAndWaitAsync(CancellationToken cancellationToken)
    {
        var hidden = LoadIds(HiddenPath);
        var locations = GetLibraryLocations();
        foreach (var id in hidden.ToList())
        {
            if (_libraryManager.GetItemById(id) is Video video && IsInLibraries(video, locations))
            {
                await _providerManager.RefreshSingleItem(video, ProbeOptions(), cancellationToken).ConfigureAwait(false);
                hidden.Remove(id);
            }
        }

        SaveIds(HiddenPath, hidden);
    }

    /// <summary>
    /// Shows again every ASS track hidden by the plugin (Jellyfin analyses the videos again).
    /// </summary>
    public void RestoreHiddenTracks()
    {
        var hidden = LoadIds(HiddenPath);
        if (hidden.Count == 0)
        {
            return;
        }

        foreach (var id in hidden)
        {
            _providerManager.QueueRefresh(id, ProbeOptions(), RefreshPriority.High);
        }

        SaveIds(HiddenPath, new HashSet<Guid>());
        _logger.LogInformation("ASS to SRT: showing the ASS tracks of {Count} videos again", hidden.Count);
    }

    /// <summary>
    /// Hides the converted ASS tracks of every video of the selected libraries.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task HideAllAsync(CancellationToken cancellationToken)
    {
        foreach (var video in GetCandidateItems())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await HideItemAsync(video, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hides the converted ASS tracks of one video, when the option is on.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task HideItemAsync(Video video, CancellationToken cancellationToken)
    {
        if (!Config.HideConvertedAss)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HideConvertedTracks(video);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Deletes the SRT files previously written by the plugin for the selected libraries,
    /// so that the next conversion applies the current settings.
    /// </summary>
    /// <returns>The videos whose SRT files were deleted.</returns>
    public async Task<IReadOnlySet<Guid>> DeleteCreatedSubtitlesAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var locations = GetLibraryLocations();
            var registry = LoadRegistry();
            var deletedItems = new HashSet<Guid>();
            var deleted = 0;

            foreach (var (path, itemId) in registry.ToList())
            {
                var id = ResolveItemId(path, itemId);
                var video = id == Guid.Empty ? null : _libraryManager.GetItemById(id) as Video;

                // Never touch the SRT of a library that is not selected: they would not be recreated.
                var handled = video is not null ? IsInLibraries(video, locations) : locations.Any(l => path.StartsWith(l, StringComparison.OrdinalIgnoreCase));
                if (!handled)
                {
                    continue;
                }

                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }

                    registry.Remove(path);
                    if (video is not null)
                    {
                        deletedItems.Add(video.Id);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    _logger.LogWarning(ex, "ASS to SRT: cannot delete {Path}", path);
                }
            }

            SaveRegistry(registry);
            _logger.LogInformation("ASS to SRT: {Count} previously created SRT files deleted", deleted);
            return deletedItems;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Converts the ASS tracks of one video that have no SRT equivalent yet.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="urgent">Asked by the user or for the next episodes: never paused, only slowed down during playback.</param>
    /// <returns>The number of SRT files created.</returns>
    public async Task<int> ProcessItemAsync(Video video, CancellationToken cancellationToken, bool urgent = false)
    {
        if (string.IsNullOrEmpty(video.Path) || video.IsShortcut || !File.Exists(video.Path))
        {
            return 0;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = PlanJobs(video);
            if (jobs.Count == 0)
            {
                if (Config.HideConvertedAss)
                {
                    HideConvertedTracks(video);
                }

                return 0;
            }

            var created = new List<string>();

            // Embedded tracks are extracted in a single ffmpeg pass: the file is read only once.
            var embedded = jobs.Where(j => !j.Source.IsExternal).ToList();
            if (embedded.Count > 0)
            {
                created.AddRange(await ConvertEmbeddedAsync(video, embedded, cancellationToken, urgent).ConfigureAwait(false));
            }

            foreach (var job in jobs.Where(j => j.Source.IsExternal))
            {
                var content = await ReadTextAsync(job.Source.Path, cancellationToken).ConfigureAwait(false);
                if (await WriteSrtAsync(video, job, content, cancellationToken).ConfigureAwait(false) is { } path)
                {
                    created.Add(path);
                }
            }

            if (created.Count > 0)
            {
                var registry = LoadRegistry();
                foreach (var path in created)
                {
                    registry[path] = video.Id.ToString("N", CultureInfo.InvariantCulture);
                }

                SaveRegistry(registry);

                // The ASS tracks are hidden after this refresh, when Jellyfin lists the new SRT.
                QueueRefresh(video);
            }

            return created.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    // Same as "Scan for new and updated files": makes Jellyfin pick up added or removed external subtitles.
    private void QueueRefresh(Video video)
        => _providerManager.QueueRefresh(
            video.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.Default
            },
            RefreshPriority.Normal);

    // Analyses the video file again: lists every track, including the hidden ones.
    private MetadataRefreshOptions ProbeOptions()
        => new(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.Default,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false
        };

    private List<Job> PlanJobs(Video video)
    {
        var config = Config;
        var streams = _mediaSourceManager.GetMediaStreams(video.Id).OrderBy(s => s.Index).ToList();
        var subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();

        var assTracks = subtitles.Where(s => IsCodec(s, AssCodecs)).ToList();
        if (assTracks.Count == 0)
        {
            return new List<Job>();
        }

        var covered = CoveredGroups(subtitles);

        var wantedLanguages = config.Languages
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(LanguageKey)
            .Where(l => l.Length > 0)
            .ToHashSet();

        // ffmpeg numbers the tracks of the file itself; Jellyfin numbers external subtitles first.
        var embeddedStreams = streams.Where(s => !s.IsExternal).ToList();

        var baseName = Path.GetFileNameWithoutExtension(video.Path);
        var jobs = new List<Job>();

        var groups = assTracks
            .Where(s => !(config.SkipSignsAndSongs && !string.IsNullOrEmpty(s.Title) && SignsTitle().IsMatch(s.Title)))
            .GroupBy(s => (Language: LanguageKey(s.Language), Forced: IsForcedTrack(s)));

        foreach (var group in groups)
        {
            if ((group.Key.Forced && !config.ConvertForcedTracks)
                || (wantedLanguages.Count > 0 && !wantedLanguages.Contains(group.Key.Language))
                || covered.Contains(group.Key))
            {
                continue;
            }

            // Prefer the default track, then the first one.
            var source = group.OrderByDescending(s => s.IsDefault).ThenBy(s => s.Index).First();
            var fileName = BuildFileName(baseName, source, group.Key.Forced, config.UseTrackTitle);

            if (File.Exists(Path.Combine(Path.GetDirectoryName(video.Path)!, fileName))
                || File.Exists(Path.Combine(video.GetInternalMetadataPath(), fileName)))
            {
                continue;
            }

            jobs.Add(new Job(source, fileName, source.IsExternal ? 0 : embeddedStreams.IndexOf(source)));
        }

        return jobs;
    }

    // Language + forced combinations that already have an SRT (ignoring external files deleted since the last scan).
    private HashSet<(string Language, bool Forced)> CoveredGroups(IEnumerable<MediaStream> subtitles)
        => subtitles
            .Where(s => IsCodec(s, SrtCodecs) && (!s.IsExternal || File.Exists(s.Path)))
            .Select(s => (LanguageKey(s.Language), IsForcedTrack(s)))
            .ToHashSet();

    // Removes from Jellyfin's list the ASS tracks that have an SRT equivalent. The video file is not modified.
    private void HideConvertedTracks(Video video)
    {
        var streams = _mediaSourceManager.GetMediaStreams(video.Id).OrderBy(s => s.Index).ToList();
        var covered = CoveredGroups(streams.Where(s => s.Type == MediaStreamType.Subtitle));
        var hideable = streams
            .Where(s => s.Type == MediaStreamType.Subtitle && IsCodec(s, AssCodecs) && covered.Contains((LanguageKey(s.Language), IsForcedTrack(s))))
            .ToHashSet();
        if (hideable.Count == 0)
        {
            return;
        }

        // Jellyfin finds a track of the file by its position in the list: only trailing tracks can be removed
        // without changing the position of the others.
        var hidden = hideable.Where(s => s.IsExternal).ToList();
        var embedded = streams.Where(s => !s.IsExternal).ToList();
        for (var i = embedded.Count - 1; i >= 0 && hideable.Contains(embedded[i]); i--)
        {
            hidden.Add(embedded[i]);
        }

        if (hidden.Count == 0)
        {
            _logger.LogInformation("ASS to SRT: the ASS tracks of {Path} cannot be hidden (other tracks follow them in the file)", video.Path);
            return;
        }

        _mediaStreamRepository.SaveMediaStreams(video.Id, streams.Except(hidden).ToList(), CancellationToken.None);

        var ids = LoadIds(HiddenPath);
        if (ids.Add(video.Id))
        {
            SaveIds(HiddenPath, ids);
        }

        _logger.LogInformation("ASS to SRT: {Count} ASS tracks hidden for {Path}", hidden.Count, video.Path);
    }

    // Jellyfin reads "<video>.<title>.<language>.<flags>.srt": unknown parts become the displayed track name.
    private static string BuildFileName(string baseName, MediaStream source, bool forced, bool useTrackTitle)
    {
        var name = new StringBuilder(baseName);

        var title = useTrackTitle ? TrackTitle(source.Title) : string.Empty;
        if (title.Length > 0)
        {
            name.Append('.').Append(title);
        }

        var language = FileLanguage(source.Language);
        if (language.Length > 0)
        {
            name.Append('.').Append(language);
        }

        if (forced)
        {
            name.Append(".forced");
        }

        // An external ASS is ranked like an external SRT by Jellyfin: the default flag makes the SRT win.
        if (source.IsExternal)
        {
            name.Append(".default");
        }

        return name.Append(".srt").ToString();
    }

    // "FR Full [ASS] - Crunchyroll" becomes "FR Full [SRT] - Crunchyroll"; a title without "ASS" gets " [SRT]".
    private static string TrackTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var cleaned = Spaces().Replace(UnsafeFileNameChars().Replace(title, " "), " ").Trim();
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        cleaned = AssWord().IsMatch(cleaned) ? AssWord().Replace(cleaned, "SRT") : cleaned + " [SRT]";
        return cleaned.Length > 100 ? cleaned[..100].Trim() : cleaned;
    }

    // Extracts the ASS tracks as they are (styles and positions included) in a single pass.
    private async Task<List<string>> ConvertEmbeddedAsync(Video video, List<Job> jobs, CancellationToken cancellationToken, bool urgent)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "jellyfin-asstosrt");
        Directory.CreateDirectory(tempDir);
        var temps = jobs.Select(_ => Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".ass")).ToList();

        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", video.Path };
        for (var i = 0; i < jobs.Count; i++)
        {
            args.AddRange(new[] { "-map", "0:" + jobs[i].FfmpegIndex.ToString(CultureInfo.InvariantCulture), "-c:s", "copy", "-f", "ass", temps[i] });
        }

        var created = new List<string>();
        try
        {
            var (exitCode, error) = await RunFfmpegAsync(args, cancellationToken, urgent).ConfigureAwait(false);
            if (exitCode != 0)
            {
                _logger.LogWarning("ASS to SRT: ffmpeg failed on {Path} (code {Code}): {Error}", video.Path, exitCode, error);
                return created;
            }

            for (var i = 0; i < jobs.Count; i++)
            {
                if (!File.Exists(temps[i]))
                {
                    continue;
                }

                var content = await ReadTextAsync(temps[i], cancellationToken).ConfigureAwait(false);
                if (await WriteSrtAsync(video, jobs[i], content, cancellationToken).ConfigureAwait(false) is { } path)
                {
                    created.Add(path);
                }
            }

            return created;
        }
        finally
        {
            foreach (var temp in temps)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private async Task<string?> WriteSrtAsync(Video video, Job job, string assContent, CancellationToken cancellationToken)
    {
        var srt = SrtWriter.Write(AssDocument.Parse(assContent), SrtOptions(Config));
        if (srt.Length == 0)
        {
            _logger.LogInformation("ASS to SRT: track {Index} of {Path} has nothing left to display, skipped", job.Source.Index, video.Path);
            return null;
        }

        var destination = await WriteSubtitleAsync(video, job.FileName, srt, cancellationToken).ConfigureAwait(false);
        if (destination is not null)
        {
            _logger.LogInformation("ASS to SRT: created {Destination}", destination);
        }

        return destination;
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SrtOptions SrtOptions(PluginConfiguration config) => new()
    {
        KeepItalic = config.KeepItalic,
        KeepBold = config.KeepBold,
        KeepUnderline = config.KeepUnderline,
        KeepColors = config.KeepColors,
        KeepPositions = config.KeepPositions,
        SingleLine = config.SingleLine,
        PreventOverlap = config.PreventOverlap,
        Signs = Enum.TryParse<SignsMode>(config.SignsMode, true, out var signs) ? signs : SignsMode.Top,
        SignColor = config.ColorSigns && Regex.IsMatch(config.SignColor ?? string.Empty, "^#[0-9a-fA-F]{6}$") ? config.SignColor! : string.Empty,
        RemoveSongs = config.RemoveSongs,
        MarkSongs = config.MarkSongs,
        MaxLineLength = Math.Max(0, config.MaxLineLength),
        MinDurationMs = Math.Max(0, config.MinDurationMs),
        ShowSpeakerNames = config.ShowSpeakerNames
    };

    private async Task<string?> WriteSubtitleAsync(Video video, string fileName, string content, CancellationToken cancellationToken)
    {
        var encoding = new UTF8Encoding(false);

        // Next to the video first, like a subtitle added by hand.
        var nextToVideo = Path.Combine(Path.GetDirectoryName(video.Path)!, fileName);
        try
        {
            await File.WriteAllTextAsync(nextToVideo, content, encoding, cancellationToken).ConfigureAwait(false);
            return nextToVideo;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "ASS to SRT: media folder not writable for {Path}, using the Jellyfin metadata folder", video.Path);
        }

        // Read-only media folder: Jellyfin also reads external subtitles from the item's metadata folder.
        try
        {
            var metadataFolder = video.GetInternalMetadataPath();
            Directory.CreateDirectory(metadataFolder);
            var inMetadata = Path.Combine(metadataFolder, fileName);
            await File.WriteAllTextAsync(inMetadata, content, encoding, cancellationToken).ConfigureAwait(false);
            return inMetadata;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "ASS to SRT: cannot write {FileName} for {Path}", fileName, video.Path);
            return null;
        }
    }

    // SRT path -> id of the video it belongs to ("" for files listed by version 1.1.0).
    private Dictionary<string, string> LoadRegistry()
    {
        try
        {
            if (File.Exists(RegistryPath))
            {
                var json = File.ReadAllText(RegistryPath);
                if (json.TrimStart().StartsWith('['))
                {
                    var paths = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                    return paths.ToDictionary(p => p, _ => string.Empty);
                }

                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "ASS to SRT: cannot read {Path}", RegistryPath);
        }

        return new Dictionary<string, string>();
    }

    private void SaveRegistry(Dictionary<string, string> registry) => WriteJson(RegistryPath, registry);

    private HashSet<Guid> LoadIds(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>())
                    .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty)
                    .ToHashSet();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "ASS to SRT: cannot read {Path}", path);
        }

        return new HashSet<Guid>();
    }

    private void SaveIds(string path, HashSet<Guid> ids)
        => WriteJson(path, ids.Select(g => g.ToString("N", CultureInfo.InvariantCulture)).ToList());

    private void WriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "ASS to SRT: cannot write {Path}", path);
        }
    }

    // Files listed without their video id: a file in Jellyfin's metadata folder sits in a folder named after the item id.
    private static Guid ResolveItemId(string path, string itemId)
    {
        if (Guid.TryParse(itemId, out var id))
        {
            return id;
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(path));
        return folder is { Length: 32 } && Guid.TryParseExact(folder, "N", out id) ? id : Guid.Empty;
    }

    private void SaveLastRun(int checkedVideos, int convertedVideos, int created, int failed)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        var config = plugin.Configuration;
        config.LastRunUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        config.LastRunLibraries = string.Join(", ", GetSelectedLibraries().Select(f => f.Name));
        config.LastRunChecked = checkedVideos;
        config.LastRunConverted = convertedVideos;
        config.LastRunCreated = created;
        config.LastRunErrors = failed;
        plugin.SaveConfiguration();
    }

    private bool IsPlaybackActive()
        => _sessionManager.Sessions.Any(s => s.NowPlayingItem is not null);

    private async Task<(int ExitCode, string Error)> RunFfmpegAsync(List<string> args, CancellationToken cancellationToken, bool urgent)
    {
        // Reading a whole video at full speed can disturb a playback on a small server or a network share.
        var behavior = Config.PlaybackBehavior;
        if (!urgent && string.Equals(behavior, "Pause", StringComparison.OrdinalIgnoreCase))
        {
            while (IsPlaybackActive())
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            }
        }

        if (!string.Equals(behavior, "Normal", StringComparison.OrdinalIgnoreCase) && IsPlaybackActive())
        {
            // At most 8x the speed of the video.
            args = args.ToList();
            args.InsertRange(args.IndexOf("-i"), new[] { "-readrate", "8" });
        }

        var startInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FfmpegTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (-1, "timeout");
        }

        await stdout.ConfigureAwait(false);
        return (process.ExitCode, (await stderr.ConfigureAwait(false)).Trim());
    }

    private List<VirtualFolderInfo> GetSelectedLibraries()
    {
        var selected = Config.LibraryIds
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();

        return _libraryManager.GetVirtualFolders()
            .Where(f => selected.Count == 0
                ? f.CollectionType is null || VideoCollectionTypes.Contains(f.CollectionType.ToString()!.ToLowerInvariant())
                : Guid.TryParse(f.ItemId, out var id) && selected.Contains(id))
            .ToList();
    }

    private List<string> GetLibraryLocations()
        => GetSelectedLibraries()
            .SelectMany(f => f.Locations)
            .Select(l => l.EndsWith(Path.DirectorySeparatorChar) ? l : l + Path.DirectorySeparatorChar)
            .ToList();

    private static bool IsInLibraries(Video video, List<string> locations)
        => !string.IsNullOrEmpty(video.Path)
           && locations.Any(l => video.Path.StartsWith(l, StringComparison.OrdinalIgnoreCase));

    private static bool IsCodec(MediaStream stream, string[] codecs)
        => !string.IsNullOrEmpty(stream.Codec) && codecs.Contains(stream.Codec.ToLowerInvariant());

    private static bool IsForcedTrack(MediaStream stream)
        => stream.IsForced || (!string.IsNullOrEmpty(stream.Title) && ForcedTitle().IsMatch(stream.Title));

    private string LanguageKey(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("und", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var culture = _localizationManager.FindLanguageInfo(language);
        return (culture?.TwoLetterISOLanguageName ?? language).ToLowerInvariant();
    }

    private static string FileLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("und", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return new string(language.Where(char.IsLetter).ToArray()).ToLowerInvariant();
    }

    private sealed record Job(MediaStream Source, string FileName, int FfmpegIndex);
}
