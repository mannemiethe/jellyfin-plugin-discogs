using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <inheritdoc />
public class DiscogsImageProvider : IRemoteImageProvider
{
    private readonly DiscogsApi _api;
    private readonly ILogger<DiscogsImageProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscogsImageProvider"/> class.
    /// </summary>
    /// <param name="api">The Discogs API.</param>
    /// <param name="logger">The logger.</param>
    public DiscogsImageProvider(DiscogsApi api, ILogger<DiscogsImageProvider> logger)
    {
        _api = api;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Plugin.Instance!.Name;

    /// <inheritdoc />
    public bool Supports(BaseItem item)
        => item is MusicArtist or MusicAlbum;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => new[] { ImageType.Primary, ImageType.Backdrop };

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var artistId = item.GetProviderId(DiscogsArtistExternalId.ProviderKey);
        var releaseId = item.GetProviderId(DiscogsReleaseExternalId.ProviderKey);
        var masterId = item.GetProviderId(DiscogsMasterExternalId.ProviderKey);

        _logger.LogInformation(
            "Discogs image lookup started for item '{ItemName}' ({ItemType}) - ArtistId={ArtistId}, ReleaseId={ReleaseId}, MasterId={MasterId}",
            item.Name,
            item.GetType().Name,
            artistId,
            releaseId,
            masterId);

        var images = new List<RemoteImageInfo>();

        if (item is MusicArtist)
        {
            if (artistId != null)
            {
                var artistResult = await _api.GetArtist(artistId, cancellationToken).ConfigureAwait(false);
                images.AddRange(ParseImages(artistResult));
            }

            if (images.Count == 0)
            {
                var search = await _api.Search(item.Name, "artist", cancellationToken).ConfigureAwait(false);
                var first = search?["results"]?.AsArray().FirstOrDefault();
                var resolvedArtistId = first?["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(resolvedArtistId))
                {
                    _logger.LogInformation(
                        "Discogs image fallback resolved artist by name - ItemName={ItemName}, ResolvedArtistId={ResolvedArtistId}",
                        item.Name,
                        resolvedArtistId);

                    var artistResult = await _api.GetArtist(resolvedArtistId, cancellationToken).ConfigureAwait(false);
                    images.AddRange(ParseImages(artistResult));
                }
            }
        }
        else if (item is MusicAlbum)
        {
            if (releaseId != null)
            {
                var releaseResult = await _api.GetRelease(releaseId, cancellationToken).ConfigureAwait(false);
                images.AddRange(ParseImages(releaseResult));
            }

            if (masterId != null)
            {
                var masterResult = await _api.GetMaster(masterId, cancellationToken).ConfigureAwait(false);
                images.AddRange(ParseImages(masterResult));
            }

            if (images.Count == 0)
            {
                var search = await _api.Search(item.Name, "release", cancellationToken).ConfigureAwait(false);
                var first = search?["results"]?.AsArray().FirstOrDefault();
                var resolvedReleaseId = first?["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(resolvedReleaseId))
                {
                    _logger.LogInformation(
                        "Discogs image fallback resolved album by name - ItemName={ItemName}, ResolvedReleaseId={ResolvedReleaseId}",
                        item.Name,
                        resolvedReleaseId);

                    var releaseResult = await _api.GetRelease(resolvedReleaseId, cancellationToken).ConfigureAwait(false);
                    images.AddRange(ParseImages(releaseResult));
                }
            }
        }

        var resultImages = images
            .Where(i => !string.IsNullOrWhiteSpace(i.Url))
            .GroupBy(i => i.Url, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (item is MusicAlbum)
        {
            var albumAdjustedImages = new List<RemoteImageInfo>(resultImages.Count);

            for (var index = 0; index < resultImages.Count; index++)
            {
                var image = resultImages[index];
                image.Type = index == 0
                    ? ImageType.Primary
                    : ImageType.Backdrop;
                albumAdjustedImages.Add(image);
            }

            resultImages = albumAdjustedImages;
        }

        _logger.LogInformation(
            "Discogs image lookup finished for item '{ItemName}' ({ItemType}) - FoundImages={FoundImages}",
            item.Name,
            item.GetType().Name,
            resultImages.Count);

        foreach (var image in resultImages)
        {
            _logger.LogInformation(
                "Discogs image candidate for item '{ItemName}' ({ItemType}) - ArtistId={ArtistId}, ReleaseId={ReleaseId}, MasterId={MasterId}, ImageType={ImageType}, Url={ImageUrl}",
                item.Name,
                item.GetType().Name,
                artistId,
                releaseId,
                masterId,
                image.Type,
                image.Url);
        }

        return resultImages;
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) => _api.GetImage(url, cancellationToken);

    private RemoteImageInfo[] ParseImages(JsonNode? result)
    {
        var imageArray = result?["images"]?.AsArray();
        if (imageArray is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        return imageArray
            .Where(image => image is not null && !string.IsNullOrWhiteSpace(image!["uri"]?.ToString()))
            .Select(image =>
            {
                var imageType = string.Equals(image!["type"]?.ToString(), "secondary", StringComparison.OrdinalIgnoreCase)
                    ? ImageType.Backdrop
                    : ImageType.Primary;

                return new RemoteImageInfo
                {
                    Url = image["uri"]!.ToString(),
                    ProviderName = Name,
                    Type = imageType,
                    ThumbnailUrl = image["uri150"]?.ToString(),
                    Width = image["width"]?.Deserialize<int>(),
                    Height = image["height"]?.Deserialize<int>()
                };
            })
            .ToArray();
    }
}
