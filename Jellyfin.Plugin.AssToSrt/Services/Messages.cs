using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.AssToSrt.Services;

/// <summary>
/// Activity log messages, in the language of the server interface.
/// </summary>
public static class Messages
{
    // {0} videos, {1} SRT files, {2} errors.
    private static readonly Dictionary<string, string> FullRunTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "ASS to SRT: {0} videos converted ({1} SRT files, {2} errors)",
        ["fr"] = "ASS to SRT : {0} vidéos converties ({1} fichiers SRT, {2} erreurs)",
        ["es"] = "ASS to SRT: {0} vídeos convertidos ({1} archivos SRT, {2} errores)",
        ["de"] = "ASS to SRT: {0} Videos konvertiert ({1} SRT-Dateien, {2} Fehler)",
        ["pt"] = "ASS to SRT: {0} vídeos convertidos ({1} arquivos SRT, {2} erros)",
        ["it"] = "ASS to SRT: {0} video convertiti ({1} file SRT, {2} errori)"
    };

    // {0} videos, {1} names.
    private static readonly Dictionary<string, string> BatchTexts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "ASS to SRT: subtitles ready for {0} videos ({1})",
        ["fr"] = "ASS to SRT : sous-titres prêts pour {0} vidéos ({1})",
        ["es"] = "ASS to SRT: subtítulos listos para {0} vídeos ({1})",
        ["de"] = "ASS to SRT: Untertitel für {0} Videos bereit ({1})",
        ["pt"] = "ASS to SRT: legendas prontas para {0} vídeos ({1})",
        ["it"] = "ASS to SRT: sottotitoli pronti per {0} video ({1})"
    };

    /// <summary>
    /// Message written at the end of a full conversion.
    /// </summary>
    /// <param name="culture">Server UI culture.</param>
    /// <param name="videos">Converted videos.</param>
    /// <param name="files">Created SRT files.</param>
    /// <param name="errors">Errors.</param>
    /// <returns>The message.</returns>
    public static string FullRun(string culture, int videos, int files, int errors)
        => string.Format(CultureInfo.InvariantCulture, Pick(FullRunTexts, culture), videos, files, errors);

    /// <summary>
    /// Message written after automatic or requested conversions.
    /// </summary>
    /// <param name="culture">Server UI culture.</param>
    /// <param name="videos">Converted videos.</param>
    /// <param name="names">Names of the series or videos.</param>
    /// <returns>The message.</returns>
    public static string Batch(string culture, int videos, string names)
        => string.Format(CultureInfo.InvariantCulture, Pick(BatchTexts, culture), videos, names);

    private static string Pick(Dictionary<string, string> texts, string culture)
    {
        var language = string.IsNullOrEmpty(culture) ? "en" : culture.Split('-')[0];
        return texts.TryGetValue(language, out var text) ? text : texts["en"];
    }
}
