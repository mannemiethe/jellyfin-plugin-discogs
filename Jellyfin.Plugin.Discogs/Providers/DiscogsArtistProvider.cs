using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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
            return new[] { new RemoteSearchResult { ProviderIds = new Dictionary<string, string> { { DiscogsArtistExternalId.ProviderKey, result!["id"]!.ToString() }, }, Name = result!["name"]!.ToString(), ImageUrl = result!["images"]!.AsArray().FirstOrDefault()?["uri150"]?.ToString() } };
        }
        else
        {
            var response = await _api.Search(searchInfo.Name, "artist", cancellationToken).ConfigureAwait(false);
            return response!["results"]!.AsArray().Select(result =>
            {
                var searchResult = new RemoteSearchResult();
                searchResult.ProviderIds = new Dictionary<string, string> { { DiscogsArtistExternalId.ProviderKey, result!["id"]!.ToString() }, };
                if (result["master_id"] != null && result["master_url"] != null)
                {
                    searchResult.ProviderIds.Add(DiscogsMasterExternalId.ProviderKey, result["master_id"]!.ToString());
                }

                searchResult.Name = NormalizeArtistName(result["title"]?.ToString());
                searchResult.ImageUrl = result!["thumb"]?.ToString() ?? result!["cover_image_url"]?.ToString();
                if (result["year"] != null)
                {
                    searchResult.ProductionYear = int.Parse(result["year"]!.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture);
                }

                return searchResult;
            });
        }
    }

    /// <inheritdoc />
    public async Task<MetadataResult<MusicArtist>> GetMetadata(ArtistInfo info, CancellationToken cancellationToken)
    {
        var artistId = info.GetProviderId(DiscogsArtistExternalId.ProviderKey);
        if (artistId != null)
        {
            var result = await _api.GetArtist(artistId, cancellationToken).ConfigureAwait(false);
            var resolvedArtistId = result!["id"]!.ToString();
            var resolvedArtistName = NormalizeArtistName(result["name"]?.ToString());

            _logger.LogInformation(
                "Discogs artist metadata selected - RequestedArtistId={RequestedArtistId}, ResolvedArtistId={ResolvedArtistId}, ResolvedArtistName={ResolvedArtistName}",
                artistId,
                resolvedArtistId,
                resolvedArtistName);

            return new MetadataResult<MusicArtist>
            {
                Item = new MusicArtist { ProviderIds = new Dictionary<string, string> { { DiscogsArtistExternalId.ProviderKey, resolvedArtistId } }, Name = resolvedArtistName, Overview = result!["profile_html"]?.ToString() ?? result!["profile_plaintext"]?.ToString() ?? result!["profile"]?.ToString(), },
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

        return new MetadataResult<MusicArtist>();
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) => _api.GetImage(url, cancellationToken);

    private static string NormalizeArtistName(string? name)
    {
        var value = name ?? string.Empty;
        return DiscogsDisambiguationSuffixRegex.Replace(value, string.Empty);
    }
}
