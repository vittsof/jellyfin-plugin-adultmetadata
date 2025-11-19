using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdultMetadata.Movies
{
    /// <summary>
    /// Movie provider for adult content from GEVI and AEBN.
    /// </summary>
    public class AdultMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IMetadataProvider<Movie>, IHasOrder
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<AdultMovieProvider> _logger;
        private readonly HttpClientHandler _handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        private readonly HttpClient _client;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdultMovieProvider"/> class.
        /// </summary>
        /// <param name="libraryManager">The <see cref="ILibraryManager"/>.</param>
        /// <param name="httpClientFactory">The <see cref="IHttpClientFactory"/>.</param>
        /// <param name="logger">The <see cref="ILogger{AdultMovieProvider}"/>.</param>
        public AdultMovieProvider(
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory,
            ILogger<AdultMovieProvider> logger)
        {
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _client = new HttpClient(_handler);
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
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
                // Add cookie for GEVI age-gate bypass
                _handler.CookieContainer.Add(new Uri("https://gayeroticvideoindex.com"), new Cookie("entered", (DateTimeOffset.Now.ToUnixTimeMilliseconds() + 86400000 * 2).ToString()));
                var geviResults = await SearchGevi(searchInfo.Name, cancellationToken);
                results.AddRange(geviResults);
            }

            if (config.EnableAebn)
            {
                // First, bypass AEBN age-gate
                await _client.GetAsync("https://gay.aebn.com/avs/gate-redirect?f=%2Fgay", cancellationToken);
                var aebnResults = await SearchAebn(searchInfo.Name, cancellationToken);
                results.AddRange(aebnResults);
            }

            _logger.LogInformation("Found {Count} search results for '{Name}'", results.Count, searchInfo.Name);
            foreach (var result in results)
            {
                _logger.LogDebug("Result: {Title} - {Url}", result.Name, result.SearchProviderName);
            }

            // Deduplicate results by provider URL
            var deduped = results
                .GroupBy(r => r.ProviderIds != null && r.ProviderIds.ContainsKey("Aebn") ? r.ProviderIds["Aebn"] : r.Name)
                .Select(g => g.First())
                .ToList();

            return deduped;
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
            return _client.GetAsync(url, cancellationToken);
        }

        private async Task<IEnumerable<RemoteSearchResult>> SearchGevi(string name, CancellationToken cancellationToken)
        {
            var results = new List<RemoteSearchResult>();
            try
            {
                // GEVI (gayeroticvideoindex) uses a search endpoint with form/query params: type=t, where=b, query=...
                var searchUrl = $"https://gayeroticvideoindex.com/search?type=t&where=b&query={Uri.EscapeDataString(name)}";

                var response = await _client.GetStringAsync(searchUrl, cancellationToken);

                // If the response still contains age-gate phrases, skip
                if (Regex.IsMatch(response, "(are you over|age verification|age_gate|over 18|please verify your age|enter your birth)", RegexOptions.IgnoreCase))
                {
                    return results;
                }

                // Parse HTML for movie links using a more flexible anchor regex and filtering.
                var anchorMatches = Regex.Matches(response, "<a[^>]*href\\s*=\\s*\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (Match match in anchorMatches)
                {
                    var href = match.Groups[1].Value.Trim();
                    var title = Regex.Replace(match.Groups[2].Value ?? string.Empty, "<[^>]+>", string.Empty).Trim();

                    if (string.IsNullOrEmpty(href)) continue;

                    // Resolve relative URLs
                    string url;
                    try
                    {
                        url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : new Uri(new Uri(searchUrl), href).ToString();
                    }
                    catch
                    {
                        continue;
                    }

                    // Filter likely movie links by common path segments or by host
                    var lower = url.ToLowerInvariant();
                    if (!(lower.Contains("title") || lower.Contains("video") || lower.Contains("watch") || lower.Contains("movie") || lower.Contains("aebn") || lower.Contains("gayeroticvideoindex")))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(title))
                    {
                        // Use last segment as fallback
                        try { title = Uri.UnescapeDataString(new Uri(url).Segments.Last()).Trim('/'); } catch { title = url; }
                    }

                    results.Add(new RemoteSearchResult
                    {
                        Name = title,
                        ProviderIds = new Dictionary<string, string> { { "Gevi", url } },
                        SearchProviderName = Name
                    });
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
                // AEBN (gay.aebn.com) search uses queryType and query params
                var searchUrl = $"https://gay.aebn.com/gay/search?queryType=Free+Form&query={Uri.EscapeDataString(name)}";

                var response = await _client.GetStringAsync(searchUrl, cancellationToken);

                // If the response still contains age-gate phrases, skip
                if (Regex.IsMatch(response, "(are you over|age verification|age_gate|over 18|please verify your age|enter your birth)", RegexOptions.IgnoreCase))
                {
                    return results;
                }

                // First collect movie anchors (direct links to movie pages) so we can match cards to URLs
                var anchorMatches = Regex.Matches(response, "<a[^>]*href\\s*=\\s*\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var movieAnchors = new List<(string Url, string Title)>();
                foreach (Match match in anchorMatches)
                {
                    var href = match.Groups[1].Value.Trim();
                    var inner = Regex.Replace(match.Groups[2].Value ?? string.Empty, "<[^>]+>", string.Empty).Trim();
                    if (string.IsNullOrEmpty(href)) continue;

                    string url;
                    try
                    {
                        url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : new Uri(new Uri(searchUrl), href).ToString();
                    }
                    catch
                    {
                        continue;
                    }

                    var lower = url.ToLowerInvariant();
                    // keep only direct movie links (avoid category/navigation links)
                    if (!(lower.Contains("/movies/") || lower.Contains("/video/") || lower.Contains("/watch/")))
                    {
                        continue;
                    }

                    var title = inner;
                    if (string.IsNullOrEmpty(title))
                    {
                        try { title = Uri.UnescapeDataString(new Uri(url).Segments.Last()).Trim('/'); } catch { title = url; }
                    }

                    movieAnchors.Add((url, title));
                }

                // Parse visible card titles and optional image/style within button/card blocks - prefer these for display names
                var cardMatches = Regex.Matches(response, "<button[^>]*class=[^>]*card[^>]*>(.*?)</button>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (Match card in cardMatches)
                {
                    var block = card.Groups[1].Value;

                    // Try common title locations inside card
                    string displayTitle = null;
                    var m = Regex.Match(block, "<div[^>]*class=[^>]*cardText[^>]*cardText-first[^>]*>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (m.Success) displayTitle = Regex.Replace(m.Groups[1].Value ?? string.Empty, "<[^>]+>", string.Empty).Trim();

                    if (string.IsNullOrEmpty(displayTitle))
                    {
                        m = Regex.Match(block, "<div[^>]*class=[^>]*cardText[^>]*cardCenteredText[^>]*>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        if (m.Success) displayTitle = Regex.Replace(m.Groups[1].Value ?? string.Empty, "<[^>]+>", string.Empty).Trim();
                    }

                    if (string.IsNullOrEmpty(displayTitle)) continue;

                    // Try to find an image URL from style or img tag inside the block
                    string imageUrl = null;
                    var styleMatch = Regex.Match(block, @"background-image\s*:\s*url\(['""]?(.*?)['""]?\)", RegexOptions.IgnoreCase);
                    if (styleMatch.Success) imageUrl = styleMatch.Groups[1].Value;
                    else
                    {
                        var imgMatch = Regex.Match(block, "<img[^>]*src\\s*=\\s*([^\\s>]+)[^>]*>", RegexOptions.IgnoreCase);
                        if (imgMatch.Success)
                        {
                            var raw = imgMatch.Groups[1].Value.Trim();
                            imageUrl = raw.Trim('"', '\'');
                        }
                    }

                    // Match displayTitle to one of the movie anchors by slug (last segment)
                    string slug = ToSlug(displayTitle);
                    var matched = movieAnchors.FirstOrDefault(a => ToSlug(a.Title).Equals(slug, StringComparison.OrdinalIgnoreCase) || a.Url.TrimEnd('/').EndsWith('/' + slug, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(matched.Url))
                    {
                        results.Add(new RemoteSearchResult
                        {
                            Name = displayTitle,
                            ProviderIds = new Dictionary<string, string> { { "Aebn", matched.Url } },
                            SearchProviderName = Name
                        });
                    }
                }

                // Fallback: if we didn't find cards matched to anchors, add the anchors directly (use their parsed titles)
                if (!results.Any())
                {
                    foreach (var a in movieAnchors)
                    {
                        results.Add(new RemoteSearchResult
                        {
                            Name = a.Title,
                            ProviderIds = new Dictionary<string, string> { { "Aebn", a.Url } },
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

        // Helper: make a simple slug from a title to compare with URL segments
        private static string ToSlug(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var lower = s.ToLowerInvariant();
            // replace non alnum with hyphen
            lower = Regex.Replace(lower, "[^a-z0-9]+", "-");
            lower = Regex.Replace(lower, "-+", "-").Trim('-');
            return lower;
        }

        private async Task<AdultMovieData?> FetchGeviMetadata(string identifier, CancellationToken cancellationToken)
        {
            try
            {
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

                var response = await _client.GetStringAsync(url, cancellationToken);

                // If the response still contains age-gate phrases, skip
                if (Regex.IsMatch(response, "(are you over|age verification|age_gate|over 18|please verify your age|enter your birth)", RegexOptions.IgnoreCase))
                {
                    return null;
                }

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

                var response = await _client.GetStringAsync(url, cancellationToken);

                // If the response still contains age-gate phrases, skip
                if (Regex.IsMatch(response, "(are you over|age verification|age_gate|over 18|please verify your age|enter your birth)", RegexOptions.IgnoreCase))
                {
                    return null;
                }

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
        /// Heuristic: detect common age-gate pages and try to submit an acceptance form or attach returned cookies
        /// so subsequent requests will be allowed. This is best-effort and uses simple form detection.
        /// </summary>
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