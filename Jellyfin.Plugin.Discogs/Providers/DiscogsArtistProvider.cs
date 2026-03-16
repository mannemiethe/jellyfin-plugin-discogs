using System;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, string> ArtistHintByNormalizedName = new(StringComparer.OrdinalIgnoreCase);
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

        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            var nameKey = NormalizeArtistNameKey(info.Name);
            if (ArtistHintByNormalizedName.TryGetValue(nameKey, out var hintedArtistId)
                && !string.IsNullOrWhiteSpace(hintedArtistId)
                && !string.IsNullOrWhiteSpace(artistId)
                && !string.Equals(artistId, hintedArtistId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Discogs artist id mismatch detected - ArtistName={ArtistName}, ExistingArtistId={ExistingArtistId}, HintedArtistId={HintedArtistId}. Using hinted id for refresh.",
                    info.Name,
                    artistId,
                    hintedArtistId);
                artistId = hintedArtistId;
            }
        }

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

    /// <summary>
    /// Registers an artist name hint mapped to a Discogs artist id for later fallback resolution.
    /// </summary>
    /// <param name="artistName">Artist display name or variant.</param>
    /// <param name="artistId">Discogs artist id.</param>
    public static void RegisterArtistHint(string? artistName, string? artistId)
    {
        if (string.IsNullOrWhiteSpace(artistName) || string.IsNullOrWhiteSpace(artistId))
        {
            return;
        }

        var key = NormalizeArtistNameKey(artistName);
        if (!string.IsNullOrWhiteSpace(key))
        {
            ArtistHintByNormalizedName[key] = artistId;
        }
    }

    private async Task<JsonNode?> ResolveArtistByNameAsync(string requestedName, CancellationToken cancellationToken)
    {
        var requestedKey = NormalizeArtistNameKey(requestedName);

        if (ArtistHintByNormalizedName.TryGetValue(requestedKey, out var hintedArtistId) && !string.IsNullOrWhiteSpace(hintedArtistId))
        {
            _logger.LogInformation("Discogs artist fallback album hint match - RequestedName={RequestedName}, ResolvedArtistId={ResolvedArtistId}", requestedName, hintedArtistId);
            return await _api.GetArtist(hintedArtistId, cancellationToken).ConfigureAwait(false);
        }

        var search = await _api.Search(requestedName, "artist", cancellationToken).ConfigureAwait(false);
        var searchResults = search?["results"]?.AsArray();
        if (searchResults is null || searchResults.Count == 0)
        {
            return null;
        }

        _logger.LogInformation("Discogs artist fallback keys - RequestedName={RequestedName}, RequestedKey={RequestedKey}", requestedName, requestedKey);

        foreach (var candidate in searchResults.Take(10))
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

            var candidateName = NormalizeArtistName(candidateArtist["name"]?.ToString());
            var canonicalKey = NormalizeArtistNameKey(candidateName);
            var variationKeys = candidateArtist["namevariations"]?.AsArray()
                ?.Select(variation => NormalizeArtistNameKey(variation?.ToString()))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? Array.Empty<string>();

            _logger.LogInformation(
                "Discogs artist fallback candidate keys - CandidateId={CandidateId}, CandidateName={CandidateName}, CanonicalKey={CanonicalKey}, VariationKeys={VariationKeys}",
                candidateId,
                candidateName,
                canonicalKey,
                string.Join(",", variationKeys));

            if (string.Equals(canonicalKey, requestedKey, StringComparison.Ordinal))
            {
                _logger.LogInformation("Discogs artist fallback canonical match - RequestedName={RequestedName}, ResolvedArtistId={ResolvedArtistId}", requestedName, candidateId);
                return candidateArtist;
            }

            if (variationKeys.Any(variationKey => string.Equals(variationKey, requestedKey, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Discogs artist fallback variation match - RequestedName={RequestedName}, ResolvedArtistId={ResolvedArtistId}", requestedName, candidateId);
                return candidateArtist;
            }
        }

        _logger.LogWarning("Discogs artist fallback found no safe match - RequestedName={RequestedName}", requestedName);
        return null;
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
