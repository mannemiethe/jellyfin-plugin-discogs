using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Discogs.ExternalIds;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Discogs.Providers;

/// <summary>
/// Discogs album provider.
/// </summary>
public class DiscogsAlbumProvider : IRemoteMetadataProvider<MusicAlbum, AlbumInfo>
{
    private static readonly Regex DiscogsDisambiguationSuffixRegex = new(@"\s\(\d+\)$", RegexOptions.Compiled);
    private static readonly char[] ArtistNameSplitSeparators = { ',', '/', '&' };
    private readonly DiscogsApi _api;
    private readonly ILogger<DiscogsAlbumProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscogsAlbumProvider"/> class.
    /// </summary>
    /// <param name="api">The Discogs API.</param>
    /// <param name="logger">The logger.</param>
    public DiscogsAlbumProvider(DiscogsApi api, ILogger<DiscogsAlbumProvider> logger)
    {
        _api = api;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Discogs";

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(AlbumInfo searchInfo, CancellationToken cancellationToken)
    {
        var releaseId = searchInfo.GetProviderId(DiscogsReleaseExternalId.ProviderKey);
        var masterId = searchInfo.GetProviderId(DiscogsMasterExternalId.ProviderKey);

        if (releaseId != null)
        {
            var result = await _api.GetRelease(releaseId, cancellationToken).ConfigureAwait(false);
            return new[]
            {
                new RemoteSearchResult
                {
                    ProviderIds = new Dictionary<string, string> { { DiscogsReleaseExternalId.ProviderKey, result!["id"]!.ToString() } },
                    Name = result["title"]!.ToString(),
                    ImageUrl = result["thumb"]?.ToString() ?? result["cover_image"]?.ToString()
                }
            };
        }

        if (masterId != null)
        {
            var result = await _api.GetMaster(masterId, cancellationToken).ConfigureAwait(false);
            return new[]
            {
                new RemoteSearchResult
                {
                    ProviderIds = new Dictionary<string, string> { { DiscogsMasterExternalId.ProviderKey, result!["id"]!.ToString() } },
                    Name = result["title"]!.ToString(),
                    ImageUrl = result["thumb"]?.ToString() ?? result["cover_image"]?.ToString()
                }
            };
        }

        var response = await _api.Search(searchInfo.Name, "release", cancellationToken).ConfigureAwait(false);
        return response!["results"]!.AsArray().Select(result =>
        {
            var searchResult = new RemoteSearchResult();
            searchResult.ProviderIds = new Dictionary<string, string> { { DiscogsReleaseExternalId.ProviderKey, result!["id"]!.ToString() } };
            if (result["master_id"] != null && result["master_url"] != null)
            {
                searchResult.ProviderIds.Add(DiscogsMasterExternalId.ProviderKey, result["master_id"]!.ToString());
            }

            searchResult.Name = result["title"]!.ToString();
            searchResult.ImageUrl = result["thumb"]?.ToString() ?? result["cover_image"]?.ToString();
            if (result["year"] != null)
            {
                searchResult.ProductionYear = int.Parse(result["year"]!.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture);
            }

            return searchResult;
        });
    }

    /// <inheritdoc />
    public async Task<MetadataResult<MusicAlbum>> GetMetadata(AlbumInfo info, CancellationToken cancellationToken)
    {
        var releaseId = info.GetProviderId(DiscogsReleaseExternalId.ProviderKey);
        if (releaseId != null)
        {
            var result = await _api.GetRelease(releaseId, cancellationToken).ConfigureAwait(false);
            var resolvedReleaseId = result!["id"]!.ToString();
            var resolvedAlbumName = result["title"]!.ToString();

            _logger.LogInformation(
                "Discogs album metadata selected (release) - RequestedReleaseId={RequestedReleaseId}, ResolvedReleaseId={ResolvedReleaseId}, ResolvedAlbumName={ResolvedAlbumName}",
                releaseId,
                resolvedReleaseId,
                resolvedAlbumName);

            var canonicalArtists = await ResolveCanonicalArtistNames(result, cancellationToken).ConfigureAwait(false);
            var providerIds = BuildAlbumProviderIds(result, DiscogsReleaseExternalId.ProviderKey, resolvedReleaseId);
            var remoteImages = GetRemoteImages(result);

            _logger.LogInformation(
                "Discogs release metadata mapped - Album={Album}, Artists={ArtistCount}, RemoteImages={ImageCount}, PremiereDate={PremiereDate}",
                resolvedAlbumName,
                canonicalArtists.Length,
                remoteImages?.Count ?? 0,
                TryGetPremiereDate(result));

            var metadataResult = new MetadataResult<MusicAlbum>
            {
                Item = new MusicAlbum
                {
                    ProviderIds = providerIds,
                    Name = resolvedAlbumName,
                    Overview = BuildAlbumOverview(result),
                    Artists = canonicalArtists.ToList(),
                    AlbumArtists = canonicalArtists.ToList(),
                    Genres = result["genres"]?.AsArray().Select(genre => genre!.ToString()).ToArray(),
                    ProductionYear = TryGetProductionYear(result),
                    CommunityRating = TryGetCommunityRating(result),
                    Studios = GetStudios(result),
                    PremiereDate = TryGetPremiereDate(result),
                },
                RemoteImages = remoteImages,
                QueriedById = true,
                HasMetadata = true,
            };

            metadataResult.People = GetContributors(result);
            return metadataResult;
        }

        var masterId = info.GetProviderId(DiscogsMasterExternalId.ProviderKey);
        if (masterId != null)
        {
            var result = await _api.GetMaster(masterId, cancellationToken).ConfigureAwait(false);
            var resolvedMasterId = result!["id"]!.ToString();
            var resolvedAlbumName = result["title"]!.ToString();

            _logger.LogInformation(
                "Discogs album metadata selected (master) - RequestedMasterId={RequestedMasterId}, ResolvedMasterId={ResolvedMasterId}, ResolvedAlbumName={ResolvedAlbumName}",
                masterId,
                resolvedMasterId,
                resolvedAlbumName);

            var canonicalArtists = await ResolveCanonicalArtistNames(result, cancellationToken).ConfigureAwait(false);
            var providerIds = BuildAlbumProviderIds(result, DiscogsMasterExternalId.ProviderKey, resolvedMasterId);
            var remoteImages = GetRemoteImages(result);

            _logger.LogInformation(
                "Discogs master metadata mapped - Album={Album}, Artists={ArtistCount}, RemoteImages={ImageCount}, PremiereDate={PremiereDate}",
                resolvedAlbumName,
                canonicalArtists.Length,
                remoteImages?.Count ?? 0,
                TryGetPremiereDate(result));

            var metadataResult = new MetadataResult<MusicAlbum>
            {
                Item = new MusicAlbum
                {
                    ProviderIds = providerIds,
                    Name = resolvedAlbumName,
                    Artists = canonicalArtists.ToList(),
                    AlbumArtists = canonicalArtists.ToList(),
                    Genres = result["genres"]?.AsArray().Select(genre => genre!.ToString()).ToArray(),
                    ProductionYear = TryGetProductionYear(result),
                    CommunityRating = TryGetCommunityRating(result),
                    Studios = GetStudios(result),
                    PremiereDate = TryGetPremiereDate(result),
                },
                RemoteImages = remoteImages,
                QueriedById = true,
                HasMetadata = true,
            };

            metadataResult.People = GetContributors(result);
            return metadataResult;
        }

        return new MetadataResult<MusicAlbum>();
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) => _api.GetImage(url, cancellationToken);

    private async Task<string[]> ResolveCanonicalArtistNames(JsonNode? result, CancellationToken cancellationToken)
    {
        var artists = result?["artists"]?.AsArray();
        if (artists is null || artists.Count == 0)
        {
            return GetFallbackArtistNames(result);
        }

        var names = new List<string>();
        foreach (var artist in artists)
        {
            var fallbackName = NormalizeArtistName(artist?["name"]?.ToString());
            var artistId = artist?["id"]?.ToString();
            var resolvedName = fallbackName;

            if (!string.IsNullOrWhiteSpace(artistId))
            {
                try
                {
                    var artistResult = await _api.GetArtist(artistId, cancellationToken).ConfigureAwait(false);
                    var canonicalName = NormalizeArtistName(artistResult?["name"]?.ToString());
                    if (!string.IsNullOrWhiteSpace(canonicalName))
                    {
                        resolvedName = canonicalName;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to resolve canonical Discogs artist name for ArtistId={ArtistId}", artistId);
                }
            }

            if (string.IsNullOrWhiteSpace(resolvedName))
            {
                continue;
            }

            if (names.Any(name => string.Equals(name, resolvedName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            names.Add(resolvedName);
        }

        if (names.Count == 0)
        {
            return GetFallbackArtistNames(result);
        }

        return names.ToArray();
    }

    private static string[] GetFallbackArtistNames(JsonNode? result)
    {
        var artists = result?["artists"]?.AsArray()
            ?.Select(artist => NormalizeArtistName(artist?["name"]?.ToString()))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (artists is { Length: > 0 })
        {
            return artists;
        }

        var artistsSort = result?["artists_sort"]?.ToString();
        if (string.IsNullOrWhiteSpace(artistsSort))
        {
            return Array.Empty<string>();
        }

        return artistsSort
            .Split(ArtistNameSplitSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeArtistName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeArtistName(string? name)
    {
        var value = name ?? string.Empty;
        value = DiscogsDisambiguationSuffixRegex.Replace(value, string.Empty);
        value = value.Trim();

        // Discogs may append '*' to artist names (name variation marker) which should not become a Jellyfin artist name.
        value = value.TrimEnd('*').Trim();

        return value;
    }

    private static int? TryGetProductionYear(JsonNode? result)
    {
        var yearText = result?["year"]?.ToString();
        if (int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year > 0)
        {
            return year;
        }

        var premiereDate = TryGetPremiereDate(result);
        return premiereDate?.Year;
    }

    private static DateTime? TryGetPremiereDate(JsonNode? result)
    {
        var released = result?["released"]?.ToString();
        if (string.IsNullOrWhiteSpace(released))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(released, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.UtcDateTime;
        }

        if (DateTime.TryParseExact(released, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
        {
            return DateTime.SpecifyKind(dateOnly, DateTimeKind.Utc);
        }

        return null;
    }

    private static float? TryGetCommunityRating(JsonNode? result)
    {
        var averageText = result?["community"]?["rating"]?["average"]?.ToString();
        if (float.TryParse(averageText, NumberStyles.Float, CultureInfo.InvariantCulture, out var average) && average > 0)
        {
            return average;
        }

        return null;
    }

    private static string[]? GetStudios(JsonNode? result)
    {
        var labels = result?["labels"]?.AsArray()
            .Select(label => label?["name"]?.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return labels is { Length: > 0 } ? labels : null;
    }

    private static Dictionary<string, string> BuildAlbumProviderIds(JsonNode? result, string primaryKey, string primaryValue)
    {
        var providerIds = new Dictionary<string, string>
        {
            { primaryKey, primaryValue }
        };

        var artistId = result?["artists"]?.AsArray().FirstOrDefault()?["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(artistId))
        {
            providerIds[DiscogsArtistExternalId.ProviderKey] = artistId;
        }

        var masterId = result?["master_id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(masterId))
        {
            providerIds[DiscogsMasterExternalId.ProviderKey] = masterId;
        }

        return providerIds;
    }

    private static string BuildAlbumOverview(JsonNode? result)
    {
        var baseOverview = result?["notes_html"]?.ToString()
            ?? result?["notes_plaintext"]?.ToString()
            ?? result?["notes"]?.ToString()
            ?? string.Empty;

        var contributors = result?["extraartists"]?.AsArray()
            ?.Select(entry =>
            {
                var name = NormalizeArtistName(entry?["name"]?.ToString());
                var role = entry?["role"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                return string.IsNullOrWhiteSpace(role)
                    ? name
                    : $"{name} ({role})";
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (contributors is not { Length: > 0 })
        {
            return baseOverview;
        }

        var contributorSection = $"Contributors: {string.Join(", ", contributors)}";
        if (string.IsNullOrWhiteSpace(baseOverview))
        {
            return contributorSection;
        }

        return $"{baseOverview.Trim()}\n\n{contributorSection}";
    }

    private static PersonInfo[]? GetContributors(JsonNode? result)
    {
        var contributors = new List<PersonInfo>();
        AddContributors(contributors, result?["artists"]?.AsArray(), "Artist");
        AddContributors(contributors, result?["extraartists"]?.AsArray(), null);
        return contributors.Count > 0 ? contributors.ToArray() : null;
    }

    private static void AddContributors(List<PersonInfo> contributors, JsonArray? source, string? defaultRole)
    {
        if (source is null)
        {
            return;
        }

        foreach (var node in source)
        {
            var name = NormalizeArtistName(node?["name"]?.ToString());
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var role = node?["role"]?.ToString();
            if (string.IsNullOrWhiteSpace(role))
            {
                role = defaultRole;
            }

            if (contributors.Any(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(existing.Role ?? string.Empty, role ?? string.Empty, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            contributors.Add(new PersonInfo
            {
                Name = name,
                Role = role,
            });
        }
    }

    private static List<(string Url, ImageType Type)>? GetRemoteImages(JsonNode? result)
    {
        var images = result?["images"]?.AsArray()
            .Where(image => !string.IsNullOrWhiteSpace(image?["uri"]?.ToString()))
            .Select(image =>
            {
                var imageType = string.Equals(image?["type"]?.ToString(), "secondary", StringComparison.OrdinalIgnoreCase)
                    ? ImageType.Backdrop
                    : ImageType.Primary;
                return (image!["uri"]!.ToString(), imageType);
            })
            .ToList();

        if (images is { Count: > 0 })
        {
            return images;
        }

        var fallbackUrl = result?["cover_image"]?.ToString() ?? result?["thumb"]?.ToString();
        if (!string.IsNullOrWhiteSpace(fallbackUrl))
        {
            return new List<(string Url, ImageType Type)> { (fallbackUrl, ImageType.Primary) };
        }

        return null;
    }
}
