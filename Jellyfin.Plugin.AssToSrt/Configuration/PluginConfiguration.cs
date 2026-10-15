using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AssToSrt.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the ids of the libraries to process. Empty means every video library.
    /// </summary>
    public string[] LibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets a value indicating whether new or updated items are converted automatically.
    /// </summary>
    public bool AutoConvertNewItems { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether forced ASS tracks are converted too (written as .forced.srt).
    /// </summary>
    public bool ConvertForcedTracks { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether "Signs / Songs" style tracks are skipped.
    /// </summary>
    public bool SkipSignsAndSongs { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether bold / italic / underline tags are kept in the SRT.
    /// </summary>
    public bool KeepBasicFormatting { get; set; } = true;
}
