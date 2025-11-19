using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Jellyfin.Plugin.AdultMetadata.Configuration;

namespace Jellyfin.Plugin.AdultMetadata.Movies
{
    /// <summary>
    /// Movie provider for adult content from GEVI and AEBN.
    /// </summary>
    public class AdultMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IMetadataProvider<Movie>, IHasOrder
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryManager _libraryManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdultMovieProvider"/> class.
        /// </summary>
        /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
        /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/>.</param>
        public AdultMovieProvider(
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory)
        {
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public int Order => 0;

        /// <inheritdoc />
        public string Name => "Adult Metadata Provider";

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();

            var config = Plugin.Instance.Configuration;

            if (config.EnableGevi)
            {
                var geviResults = await SearchGevi(searchInfo.Name, cancellationToken);
                results.AddRange(geviResults);
            }

            if (config.EnableAebn)
            {
                var aebnResults = await SearchAebn(searchInfo.Name, cancellationToken);
                results.AddRange(aebnResults);
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Movie>
            {
                Item = new Movie(),
                QueriedById = true
            };

            var config = Plugin.Instance.Configuration;

            // Check if we have a specific provider ID
            if (info.ProviderIds.TryGetValue("Gevi", out var geviId) && config.EnableGevi)
            {
                var geviData = await FetchGeviMetadata(geviId, cancellationToken);
                if (geviData != null)
                {
                    ApplyMetadata(result.Item, geviData);
                    return result;
                }
            }
            else if (info.ProviderIds.TryGetValue("Aebn", out var aebnId) && config.EnableAebn)
            {
                var aebnData = await FetchAebnMetadata(aebnId, cancellationToken);
                if (aebnData != null)
                {
                    ApplyMetadata(result.Item, aebnData);
                    return result;
                }
            }

            // Fallback to search-based fetching
            if (config.EnableGevi)
            {
                var geviData = await FetchGeviMetadata(info.Name, cancellationToken);
                if (geviData != null)
                {
                    ApplyMetadata(result.Item, geviData);
                    return result;
                }
            }

            if (config.EnableAebn)
            {
                var aebnData = await FetchAebnMetadata(info.Name, cancellationToken);
                if (aebnData != null)
                {
                    ApplyMetadata(result.Item, aebnData);
                    return result;
                }
            }

            return result;
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient();
            return client.GetAsync(url, cancellationToken);
        }

        private async Task<IEnumerable<RemoteSearchResult>> SearchGevi(string name, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                var searchUrl = $"https://www.gevi.gr/search?q={Uri.EscapeDataString(name)}";
                var response = await client.GetStringAsync(searchUrl, cancellationToken);

                // Parse HTML for movie links
                var movieLinks = Regex.Matches(response, @"<a[^>]*href=""([^""]*movie[^""]*)""[^>]*>([^<]*)</a>", RegexOptions.IgnoreCase);
                foreach (Match match in movieLinks)
                {
                    var url = match.Groups[1].Value;
                    var title = match.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(title))
                    {
                        results.Add(new RemoteSearchResult
                        {
                            Name = title,
                            ProviderIds = new Dictionary<string, string> { { "Gevi", url } },
                            SearchProviderName = Name
                        });
                    }
                }
            }
            catch
            {
                // Log error
            }
            return results;
        }

        private async Task<IEnumerable<RemoteSearchResult>> SearchAebn(string name, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                var searchUrl = $"https://www.aebn.net/search?q={Uri.EscapeDataString(name)}";
                var response = await client.GetStringAsync(searchUrl, cancellationToken);

                // Parse HTML for movie links
                var movieLinks = Regex.Matches(response, @"<a[^>]*href=""([^""]*movie[^""]*)""[^>]*>([^<]*)</a>", RegexOptions.IgnoreCase);
                foreach (Match match in movieLinks)
                {
                    var url = match.Groups[1].Value;
                    var title = match.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(title))
                    {
                        results.Add(new RemoteSearchResult
                        {
                            Name = title,
                            ProviderIds = new Dictionary<string, string> { { "Aebn", url } },
                            SearchProviderName = Name
                        });
                    }
                }
            }
            catch
            {
                // Log error
            }
            return results;
        }

        private async Task<AdultMovieData?> FetchGeviMetadata(string identifier, CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                string url;
                if (identifier.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    url = identifier;
                }
                else
                {
                    // Assume it's a name, search first
                    var searchResults = await SearchGevi(identifier, cancellationToken);
                    var first = searchResults.FirstOrDefault();
                    if (first == null) return null;
                    url = first.ProviderIds["Gevi"];
                }

                var response = await client.GetStringAsync(url, cancellationToken);

                // Parse HTML for metadata
                var titleMatch = Regex.Match(response, @"<h1[^>]*>([^<]*)</h1>", RegexOptions.IgnoreCase);
                if (!titleMatch.Success)
                {
                    titleMatch = Regex.Match(response, @"<title>([^<]*)</title>", RegexOptions.IgnoreCase);
                }
                var descriptionMatch = Regex.Match(response, @"<meta[^>]*name=""description""[^>]*content=""([^""]*)""[^>]*>", RegexOptions.IgnoreCase);

                return new AdultMovieData
                {
                    Title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : identifier,
                    Description = descriptionMatch.Success ? descriptionMatch.Groups[1].Value : string.Empty,
                    Genres = new List<string> { "Adult" },
                    Studios = new List<string>()
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<AdultMovieData?> FetchAebnMetadata(string identifier, CancellationToken cancellationToken)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                string url;
                if (identifier.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    url = identifier;
                }
                else
                {
                    // Assume it's a name, search first
                    var searchResults = await SearchAebn(identifier, cancellationToken);
                    var first = searchResults.FirstOrDefault();
                    if (first == null) return null;
                    url = first.ProviderIds["Aebn"];
                }

                var response = await client.GetStringAsync(url, cancellationToken);

                // Parse HTML for metadata
                var titleMatch = Regex.Match(response, @"<h1[^>]*>([^<]*)</h1>", RegexOptions.IgnoreCase);
                if (!titleMatch.Success)
                {
                    titleMatch = Regex.Match(response, @"<title>([^<]*)</title>", RegexOptions.IgnoreCase);
                }
                var descriptionMatch = Regex.Match(response, @"<meta[^>]*name=""description""[^>]*content=""([^""]*)""[^>]*>", RegexOptions.IgnoreCase);

                return new AdultMovieData
                {
                    Title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : identifier,
                    Description = descriptionMatch.Success ? descriptionMatch.Groups[1].Value : string.Empty,
                    Genres = new List<string> { "Adult" },
                    Studios = new List<string>()
                };
            }
            catch
            {
                return null;
            }
        }

        private void ApplyMetadata(Movie movie, AdultMovieData data)
        {
            movie.Name = data.Title;
            movie.Overview = data.Description;
            movie.Genres = data.Genres?.ToArray() ?? Array.Empty<string>();
            movie.Studios = data.Studios?.ToArray() ?? Array.Empty<string>();
            movie.OfficialRating = "XXX";
            // Add more fields as needed
        }

        /// <summary>
        /// Data class for adult movie metadata.
        /// </summary>
        private class AdultMovieData
        {
            public string? Title { get; set; }
            public string? Description { get; set; }
            public List<string>? Genres { get; set; }
            public List<string>? Studios { get; set; }
            // Add more properties as needed
        }
    }
}