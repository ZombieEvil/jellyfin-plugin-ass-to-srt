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
using Jellyfin.Plugin.AssToSrt.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
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
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalizationManager _localizationManager;
    private readonly ILogger<AssToSrtConverter> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="AssToSrtConverter"/> class.
    /// </summary>
    public AssToSrtConverter(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILocalizationManager localizationManager,
        ILogger<AssToSrtConverter> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _localizationManager = localizationManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    // List of the SRT files written by the plugin, so that they can be regenerated with new settings.
    private static string RegistryPath => Path.Combine(Plugin.Instance!.DataFolderPath, "created-subtitles.json");

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
    /// <returns>A task.</returns>
    public async Task ConvertAllAsync(IProgress<double> progress, CancellationToken cancellationToken, IReadOnlySet<Guid>? deletedItems = null)
    {
        var items = GetCandidateItems();
        var created = 0;
        var convertedVideos = 0;
        var failed = 0;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
    /// <returns>The number of SRT files created.</returns>
    public async Task<int> ProcessItemAsync(Video video, CancellationToken cancellationToken)
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
                return 0;
            }

            var created = new List<string>();

            // Embedded tracks are extracted in a single ffmpeg pass: the file is read only once.
            var embedded = jobs.Where(j => !j.Source.IsExternal).ToList();
            if (embedded.Count > 0)
            {
                created.AddRange(await ConvertAsync(video, video.Path, embedded, cancellationToken).ConfigureAwait(false));
            }

            foreach (var job in jobs.Where(j => j.Source.IsExternal))
            {
                created.AddRange(await ConvertAsync(video, job.Source.Path, new List<Job> { job }, cancellationToken).ConfigureAwait(false));
            }

            if (created.Count > 0)
            {
                var registry = LoadRegistry();
                foreach (var path in created)
                {
                    registry[path] = video.Id.ToString("N", CultureInfo.InvariantCulture);
                }

                SaveRegistry(registry);
                QueueRefresh(video);
            }

            return created.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

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

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private List<Job> PlanJobs(Video video)
    {
        var config = Config;
        var subtitles = _mediaSourceManager.GetMediaStreams(video.Id)
            .Where(s => s.Type == MediaStreamType.Subtitle)
            .ToList();

        var assTracks = subtitles.Where(s => IsCodec(s, AssCodecs)).ToList();
        if (assTracks.Count == 0)
        {
            return new List<Job>();
        }

        // Language + forced combinations that already have an SRT (ignoring external files deleted since the last scan).
        var covered = subtitles
            .Where(s => IsCodec(s, SrtCodecs) && (!s.IsExternal || File.Exists(s.Path)))
            .Select(s => (LanguageKey(s.Language), IsForcedTrack(s)))
            .ToHashSet();

        var wantedLanguages = config.Languages
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(LanguageKey)
            .Where(l => l.Length > 0)
            .ToHashSet();

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

            jobs.Add(new Job(source, fileName));
        }

        return jobs;
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

    private async Task<List<string>> ConvertAsync(Video video, string inputPath, List<Job> jobs, CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "jellyfin-asstosrt");
        Directory.CreateDirectory(tempDir);
        var temps = jobs.Select(_ => Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".srt")).ToList();

        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", inputPath };
        for (var i = 0; i < jobs.Count; i++)
        {
            args.Add("-map");
            args.Add(jobs[i].Source.IsExternal ? "0:s:0" : "0:" + jobs[i].Source.Index.ToString(CultureInfo.InvariantCulture));
            args.Add("-c:s");
            args.Add("srt");
            args.Add(temps[i]);
        }

        var created = new List<string>();
        try
        {
            var (exitCode, error) = await RunFfmpegAsync(args, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                _logger.LogWarning("ASS to SRT: ffmpeg failed on {Path} (code {Code}): {Error}", inputPath, exitCode, error);
                return created;
            }

            var style = StyleOptions(Config);
            for (var i = 0; i < jobs.Count; i++)
            {
                if (!File.Exists(temps[i]))
                {
                    continue;
                }

                var cleaned = SrtCleaner.Clean(await File.ReadAllTextAsync(temps[i], cancellationToken).ConfigureAwait(false), style);
                if (cleaned.Length == 0)
                {
                    _logger.LogInformation("ASS to SRT: track {Index} of {Path} has no readable text, skipped", jobs[i].Source.Index, video.Path);
                    continue;
                }

                var destination = await WriteSubtitleAsync(video, jobs[i].FileName, cleaned, cancellationToken).ConfigureAwait(false);
                if (destination is not null)
                {
                    _logger.LogInformation("ASS to SRT: created {Destination}", destination);
                    created.Add(destination);
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

    private static SrtStyleOptions StyleOptions(PluginConfiguration config) => new()
    {
        KeepItalic = config.KeepItalic,
        KeepBold = config.KeepBold,
        KeepUnderline = config.KeepUnderline,
        KeepColors = config.KeepColors,
        KeepPositions = config.KeepPositions,
        SingleLine = config.SingleLine
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

    private void SaveRegistry(Dictionary<string, string> registry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
            File.WriteAllText(RegistryPath, JsonSerializer.Serialize(registry));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "ASS to SRT: cannot write {Path}", RegistryPath);
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

    private async Task<(int ExitCode, string Error)> RunFfmpegAsync(IEnumerable<string> args, CancellationToken cancellationToken)
    {
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

    private List<MediaBrowser.Model.Entities.VirtualFolderInfo> GetSelectedLibraries()
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

    private sealed record Job(MediaStream Source, string FileName);
}
