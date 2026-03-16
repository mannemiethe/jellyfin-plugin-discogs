using System.Collections.Generic;
using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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
/// Discogs album provider.
/// </summary>
public class DiscogsAlbumProvider : IRemoteMetadataProvider<MusicAlbum, AlbumInfo>
{
    private static readonly Regex DiscogsDisambiguationSuffixRegex = new(@"\s\(\d+\)$", RegexOptions.Compiled);
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

            return new MetadataResult<MusicAlbum>
            {
                Item = new MusicAlbum
                {
                    ProviderIds = new Dictionary<string, string> { { DiscogsReleaseExternalId.ProviderKey, resolvedReleaseId } },
                    Name = resolvedAlbumName,
                    Overview = result["notes_html"]?.ToString() ?? result["notes_plaintext"]?.ToString() ?? result["notes"]?.ToString(),
                    Artists = result["artists"]?.AsArray().Select(artist => NormalizeArtistName(artist!["name"]?.ToString())).ToList(),
                    AlbumArtists = result["artists"]?.AsArray().Select(artist => NormalizeArtistName(artist!["name"]?.ToString())).ToList(),
                    Genres = result["genres"]?.AsArray().Select(genre => genre!.ToString()).ToArray(),
                    ProductionYear = TryGetProductionYear(result),
                    CommunityRating = TryGetCommunityRating(result),
                    Studios = GetStudios(result),
                },
                RemoteImages = result["images"]?.AsArray()
                    .Where(image => image!["uri"]!.ToString().Length > 0)
                    .Select(image =>
                    {
                        var imageType = image!["type"]!.ToString() == "secondary"
                            ? ImageType.Backdrop
                            : ImageType.Primary;
                        return (image!["uri"]!.ToString(), imageType);
                    })
                    .ToList(),
                QueriedById = true,
                HasMetadata = true,
            };
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

            return new MetadataResult<MusicAlbum>
            {
                Item = new MusicAlbum
                {
                    ProviderIds = new Dictionary<string, string> { { DiscogsMasterExternalId.ProviderKey, resolvedMasterId } },
                    Name = resolvedAlbumName,
                    Artists = result["artists"]?.AsArray().Select(artist => NormalizeArtistName(artist!["name"]?.ToString())).ToList(),
                    AlbumArtists = result["artists"]?.AsArray().Select(artist => NormalizeArtistName(artist!["name"]?.ToString())).ToList(),
                    Genres = result["genres"]?.AsArray().Select(genre => genre!.ToString()).ToArray(),
                    ProductionYear = TryGetProductionYear(result),
                    CommunityRating = TryGetCommunityRating(result),
                    Studios = GetStudios(result),
                },
                RemoteImages = result["images"]?.AsArray()
                    .Where(image => image!["uri"]!.ToString().Length > 0)
                    .Select(image =>
                    {
                        var imageType = image!["type"]!.ToString() == "secondary"
                            ? ImageType.Backdrop
                            : ImageType.Primary;
                        return (image!["uri"]!.ToString(), imageType);
                    })
                    .ToList(),
                QueriedById = true,
                HasMetadata = true,
            };
        }

        return new MetadataResult<MusicAlbum>();
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) => _api.GetImage(url, cancellationToken);

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
}
