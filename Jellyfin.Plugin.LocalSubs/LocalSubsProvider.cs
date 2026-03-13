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

public class LocalSubsProvider : ISubtitleProvider
{
    private readonly ILogger<LocalSubsProvider> _logger;

    public LocalSubsProvider(ILogger<LocalSubsProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
    }

    public string Name => LocalSubsConstants.PLUGINNAME;

    public IEnumerable<VideoContentType> SupportedMediaTypes => LocalSubsConstants.MEDIATYPES;

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        _logger.LogDebug("GetSubtitles id: {Id}", id);
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("Missing param", nameof(id));

        string[] parts = id.Split(LocalSubsConstants.IDSEPARATOR);
        if (parts.Length < 3) throw new ArgumentException("Invalid id", nameof(id));

        string ext = parts[0];
        string lang = parts[1];
        string pathHex = id.Substring(ext.Length + lang.Length + 2);
        string path;

        try
        {
            path = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(pathHex));
        }
        catch
        {
            path = pathHex;
        }

        if (!File.Exists(path)) throw new ArgumentException($"File does not exist: {path}", nameof(id));

        _logger.LogInformation("GetSubtitles reading file into memory: {Path}", path);
        byte[] fileBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new SubtitleResponse
        {
            Format = ext,
            Language = lang,
            Stream = new MemoryStream(fileBytes),
        };
    }

    private static string GeneratePattern(string text, IDictionary<string, string> dict)
    {
        // REMOVED the ^ and $ anchors to allow fuzzy matching by default
        return Regex.Replace(text, "(" + string.Join("|", dict.Keys.Select(Regex.Escape)) + ")", delegate (Match m)
        {
            return dict[m.Value];
        });
    }

    private IEnumerable<string> MatchFile(string mediaDir, string template, IDictionary<string, string> placeholders)
    {
        string[] parts = template.Split(Path.DirectorySeparatorChar);
        if (parts.Length < 1) return Enumerable.Empty<string>();

        List<string> dirs = [mediaDir];
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string dirPattern = "^" + GeneratePattern(parts[i], placeholders) + "$";
            Regex dirRegex = new Regex(dirPattern, RegexOptions.IgnoreCase);
            List<string> subDirs = [];
            foreach (string dir in dirs)
            {
                if (Directory.Exists(dir))
                {
                    subDirs.AddRange(Directory.EnumerateDirectories(dir).Where(d => dirRegex.IsMatch(Path.GetFileName(d) ?? string.Empty)));
                }
            }
            dirs = subDirs.Distinct().ToList();
        }

        List<string> files = [];
        string filePattern = "^" + GeneratePattern(parts[parts.Length - 1], placeholders) + "$";
        Regex fileRegex = new Regex(filePattern, RegexOptions.IgnoreCase);
        foreach (string dir in dirs)
        {
            if (Directory.Exists(dir))
            {
                files.AddRange(Directory.EnumerateFiles(dir).Where(f => fileRegex.IsMatch(Path.GetFileName(f))));
            }
        }
        return files.Distinct();
    }

    public Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        string[] templates = LocalSubsPlugin.Instance!.Configuration.Templates;
        if (string.IsNullOrEmpty(request.MediaPath) || templates == null || templates.Length < 1)
        {
            return Task.FromResult(Enumerable.Empty<RemoteSubtitleInfo>());
        }

        // ULTIMATE FIX: Hardcoded English variations to bypass CultureInfo issues
        List<string> langStrings = ["eng", "en", "english"];
        
        // Still try to add CultureInfo names but wrap in try-catch
        try {
            var culture = CultureInfo.GetCultureInfo(request.TwoLetterISOLanguageName);
            langStrings.Add(culture.EnglishName.ToLowerInvariant());
            langStrings.Add(request.Language.ToLowerInvariant());
        } catch { }

        langStrings = langStrings.Distinct().ToList();

        string dir = Path.GetDirectoryName(request.MediaPath) ?? string.Empty;
        string fn = Path.GetFileNameWithoutExtension(request.MediaPath);
        
        Dictionary<string, string> placeholders = new Dictionary<string, string>
        {
            { "%f%", Regex.Escape(Path.GetFileName(request.MediaPath)) },
            { "%fn%", Regex.Escape(fn) },
            { "%fe%", Regex.Escape(Path.GetExtension(request.MediaPath)) },
            { "%n%", "[0-9]+" },
            { "%l%", ".*(" + string.Join("|", langStrings) + ").*" }, // FUZZY language matching
            { "%any%", ".*" }
        };

        List<RemoteSubtitleInfo> matches = [];
        foreach (string template in templates)
        {
            if (string.IsNullOrEmpty(template)) continue;

            foreach (string match in MatchFile(dir, template, placeholders))
            {
                string ext = (Path.GetExtension(match) ?? "srt").ToLowerInvariant().Replace(".", string.Empty);
                string pathHex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(match));
                string id = string.Join(LocalSubsConstants.IDSEPARATOR, ext, request.Language, pathHex);

                matches.Add(new RemoteSubtitleInfo
                {
                    Id = id,
                    ProviderName = LocalSubsConstants.PLUGINNAME,
                    Format = ext,
                    ThreeLetterISOLanguageName = request.Language,
                    DateCreated = new FileInfo(match).CreationTime,
                });
            }
        }
        return Task.FromResult(matches.AsEnumerable());
    }
}
