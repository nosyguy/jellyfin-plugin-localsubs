using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalSubs;

/// <summary>
/// Local file subtitle provider.
/// </summary>
public class LocalSubsProvider : ISubtitleProvider
{
    private readonly ILogger<LocalSubsProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalSubsProvider"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{LocalSubsProvider}"/> interface.</param>
    /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/> for creating Http Clients.</param>
    public LocalSubsProvider(ILogger<LocalSubsProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => LocalSubsConstants.PLUGINNAME;

    /// <inheritdoc/>
    public IEnumerable<VideoContentType> SupportedMediaTypes => LocalSubsConstants.MEDIATYPES;

    /// <inheritdoc/>
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        _logger.LogDebug("GetSubtitles id: {Id}", id);
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("Missing param", nameof(id));
        }

        string[] parts = id.Split(LocalSubsConstants.IDSEPARATOR);
        if (parts.Length < 3)
        {
            throw new ArgumentException("Invalid id", nameof(id));
        }

        string ext = parts[0];
        string lang = parts[1];
        if (string.IsNullOrEmpty(ext) || string.IsNullOrEmpty(lang))
        {
            throw new ArgumentException("Invalid id (extension and/or language is invalid)", nameof(id));
        }

        // Decode the hex path back to a normal string
        string pathHex = id.Substring(ext.Length + lang.Length + 2);
        string path;
        try
        {
            path = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(pathHex));
        }
        catch
        {
            // Fallback just in case there are old IDs floating around that aren't hex encoded
            path = pathHex;
        }

        if (!File.Exists(path))
        {
            throw new ArgumentException($"File does not exist: {path}", nameof(id));
        }

        _logger.LogInformation("GetSubtitles reading file into memory: {Path}", path);

        byte[] fileBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var memoryStream = new MemoryStream(fileBytes);

        return new SubtitleResponse
        {
            Format = ext,
            Language = lang,
            Stream = memoryStream,
        };
    }

    [SuppressMessage("StyleCop.CSharp.SpacingRules",
