using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Discogs.ExternalIds;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Discogs.Providers;

/// <summary>
/// Discogs artist provider.
/// </summary>
public class DiscogsArtistProvider : IRemoteMetadataProvider<MusicArtist, ArtistInfo>
{
    private static readonly Regex DiscogsDisambiguationSuffixRegex = new(@"\s\(\d+\)$", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumericRegex = new(@"[^\p{L}\p{Nd}]", RegexOptions.Compiled);
    private readonly DiscogsApi _api;
    private readonly ILogger<DiscogsArtistProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscogsArtistProvider"/> class.
    /// </summary>
    /// <param name="api">The Discogs API.</param>
    /// <param name="logger">The logger.</param>
    public DiscogsArtistProvider(DiscogsApi api, ILogger<DiscogsArtistProvider> logger)
    {
        _api = api;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Discogs";

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(ArtistInfo searchInfo, CancellationToken cancellationToken)
    {
        var artistId = searchInfo.GetProviderId(DiscogsArtistExternalId.ProviderKey);
        if (artistId != null)
        {
            var result = await _api.GetArtist(artistId, cancellationToken).ConfigureAwait(false);
            var name = NormalizeArtistName(result!["name"]?.ToString());
            return new[]
            {
                new RemoteSearchResult
                {
                    ProviderIds = new Dictionary<string, string> { { DiscogsArtistExternalId.ProviderKey, result["id"]!.ToString() } },
                    Name = name,
                    ImageUrl = result["images"]?.AsArray().FirstOrDefault()?["uri150"]?.ToString()
                }
            };
        }

        var response = await _api.Search(searchInfo.Name, "artist", cancellationToken).ConfigureAwait(false);
        var results = response?["results"]?.AsArray();
        if (results is null)
        {
            return Array.Empty<RemoteSearchResult>();
        }

        return results
            .Select(result =>
            {
                var searchResult = new RemoteSearchResult
                {
                    ProviderIds = new Dictionary<string, string> { { DiscogsArtistExternalId.ProviderKey, result!["id"]!.ToString() } },
                    Name = NormalizeArtistName(result["title"]?.ToString()),
                    ImageUrl = result["thumb"]?.ToString() ?? result["cover_image_url"]?.ToString()
                };

                if (result["master_id"] != null && result["master_url"] != null)
                {
                    searchResult.ProviderIds.Add(DiscogsMasterExternalId.ProviderKey, result["master_id"]!.ToString());
                }

                if (result["year"] != null)
                {
                    searchResult.ProductionYear = int.Parse(result["year"]!.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture);
                }

                return searchResult;
            })
            .GroupBy(result => NormalizeArtistNameKey(result.Name ?? string.Empty), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    /// <inheritdoc />
    public async Task<MetadataResult<MusicArtist>> GetMetadata(ArtistInfo info, CancellationToken cancellationToken)
    {
        var artistId = info.GetProviderId(DiscogsArtistExternalId.ProviderKey);
        JsonNode? result = null;
        var queriedById = false;

        if (!string.IsNullOrWhiteSpace(artistId))
        {
            result = await _api.GetArtist(artistId, cancellationToken).ConfigureAwait(false);
            queriedById = true;
        }
        else if (!string.IsNullOrWhiteSpace(info.Name))
        {
            result = await ResolveArtistByNameAsync(info.Name, cancellationToken).ConfigureAwait(false);
        }

        if (result is null)
        {
            return new MetadataResult<MusicArtist>();
        }

        var resolvedId = result["id"]?.ToString();
        var resolvedName = queriedById
            ? NormalizeArtistName(result["name"]?.ToString())
            : NormalizeArtistName(info.Name);

        _logger.LogInformation(
            "Discogs artist metadata selected - RequestedArtistId={RequestedArtistId}, ResolvedArtistId={ResolvedArtistId}, ResolvedArtistName={ResolvedArtistName}",
            artistId,
            resolvedId,
            resolvedName);

        return new MetadataResult<MusicArtist>
        {
            Item = new MusicArtist
            {
                ProviderIds = string.IsNullOrWhiteSpace(resolvedId)
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { { DiscogsArtistExternalId.ProviderKey, resolvedId } },
                Name = resolvedName,
                Overview = BuildArtistOverview(result)
            },
            RemoteImages = result["images"]?.AsArray()
                .Where(image => !string.IsNullOrWhiteSpace(image?["uri"]?.ToString()))
                .Select(image =>
                {
                    var imageType = string.Equals(image?["type"]?.ToString(), "secondary", StringComparison.OrdinalIgnoreCase)
                        ? ImageType.Backdrop
                        : ImageType.Primary;
                    return (image!["uri"]!.ToString(), imageType);
                })
                .ToList(),
            QueriedById = queriedById,
            HasMetadata = true,
        };
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) => _api.GetImage(url, cancellationToken);

    private async Task<JsonNode?> ResolveArtistByNameAsync(string requestedName, CancellationToken cancellationToken)
    {
        var search = await _api.Search(requestedName, "artist", cancellationToken).ConfigureAwait(false);
        var searchResults = search?["results"]?.AsArray();
        if (searchResults is null || searchResults.Count == 0)
        {
            return null;
        }

        var requestedKey = NormalizeArtistNameKey(requestedName);

        var direct = searchResults.FirstOrDefault(node => string.Equals(NormalizeArtistNameKey(node?["title"]?.ToString() ?? string.Empty), requestedKey, StringComparison.Ordinal));
        if (direct is not null)
        {
            var directId = direct["id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(directId))
            {
                _logger.LogInformation("Discogs artist fallback direct match - RequestedName={RequestedName}, ResolvedArtistId={ResolvedArtistId}", requestedName, directId);
                return await _api.GetArtist(directId, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var candidate in searchResults.Take(5))
        {
            var candidateId = candidate?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(candidateId))
            {
                continue;
            }

            var candidateArtist = await _api.GetArtist(candidateId, cancellationToken).ConfigureAwait(false);
            if (candidateArtist is null)
            {
                continue;
            }

            var canonicalKey = NormalizeArtistNameKey(candidateArtist["name"]?.ToString());
            if (string.Equals(canonicalKey, requestedKey, StringComparison.Ordinal))
            {
                _logger.LogInformation("Discogs artist fallback canonical match - RequestedName={RequestedName}, ResolvedArtistId={ResolvedArtistId}", requestedName, candidateId);
                return candidateArtist;
            }

            var variations = candidateArtist["namevariations"]?.AsArray();
            if (variations is not null && variations.Any(variation => string.Equals(NormalizeArtistNameKey(variation?.ToString()), requestedKey, StringComparison.Ordinal)))
            {
                _logger.LogInformation("Discogs artist fallback variation match - RequestedName={RequestedName}, ResolvedArtistId={ResolvedArtistId}", requestedName, candidateId);
                return candidateArtist;
            }
        }

        var fallbackId = searchResults.FirstOrDefault()?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(fallbackId))
        {
            return null;
        }

        _logger.LogInformation("Discogs artist fallback first result - RequestedName={RequestedName}, ResolvedArtistId={ResolvedArtistId}", requestedName, fallbackId);
        return await _api.GetArtist(fallbackId, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildArtistOverview(JsonNode? result)
    {
        var baseOverview = result?["profile_html"]?.ToString()
            ?? result?["profile_plaintext"]?.ToString()
            ?? result?["profile"]?.ToString()
            ?? string.Empty;

        var lines = new List<string>();

        var nameVariations = result?["namevariations"]?.AsArray()
            ?.Select(node => NormalizeArtistName(node?.ToString()))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (nameVariations is { Length: > 0 })
        {
            lines.Add($"Name variants: {string.Join(", ", nameVariations)}");
        }

        var groups = result?["groups"]?.AsArray()
            ?.Select(node => NormalizeArtistName(node?["name"]?.ToString()))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (groups is { Length: > 0 })
        {
            lines.Add($"Groups: {string.Join(", ", groups)}");
        }

        var members = result?["members"]?.AsArray()
            ?.Select(node => NormalizeArtistName(node?["name"]?.ToString()))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (members is { Length: > 0 })
        {
            lines.Add($"Members: {string.Join(", ", members)}");
        }

        if (lines.Count == 0)
        {
            return baseOverview;
        }

        var extra = string.Join(Environment.NewLine, lines);
        if (string.IsNullOrWhiteSpace(baseOverview))
        {
            return extra;
        }

        var sb = new StringBuilder(baseOverview.Length + extra.Length + 4);
        sb.Append(baseOverview.Trim());
        sb.Append(Environment.NewLine);
        sb.Append(Environment.NewLine);
        sb.Append(extra);
        return sb.ToString();
    }

    private static string NormalizeArtistName(string? name)
    {
        var value = name ?? string.Empty;
        value = DiscogsDisambiguationSuffixRegex.Replace(value, string.Empty);
        value = value.Trim();
        value = value.TrimEnd('*').Trim();
        return value;
    }

    private static string NormalizeArtistNameKey(string? name)
    {
        var normalized = NormalizeArtistName(name).ToUpperInvariant();
        return NonAlphaNumericRegex.Replace(normalized, string.Empty);
    }
}
