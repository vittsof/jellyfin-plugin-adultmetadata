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

                // Prefer extracting movie tiles directly from the movies grid.
                // Find anchors that link to movie pages and extract title from contained <img title="..."> or fallback to slug -> readable title.
                var movieAnchors = new List<(string Url, string Title, string Image)>();
                var movieAnchorMatches = Regex.Matches(response, "<a[^>]*href\\s*=\\s*\"(?<href>/gay/movies/[^"]+)\"[^>]*>(?<inner>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (Match match in movieAnchorMatches)
                {
                    var href = match.Groups["href"].Value.Trim();
                    var inner = match.Groups["inner"].Value ?? string.Empty;

                    if (string.IsNullOrEmpty(href)) continue;

                    // Skip scene anchors and fragment links
                    if (href.Contains("#scene") || href.Contains("#")) continue;

                    string url;
                    try
                    {
                        url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : new Uri(new Uri("https://gay.aebn.com"), href).ToString();
                    }
                    catch
                    {
                        continue;
                    }

                    // Try to extract image title/alt inside the anchor (picture > img)
                    string title = null;
                    string image = null;

                    var imgMatch = Regex.Match(inner, "<img[^>]*title\\s*=\\s*\"(?<t>[^\"]+)\"[^>]*src\\s*=\\s*\"(?<s>[^\"]+)\"[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (!imgMatch.Success)
                    {
                        // title attribute might come after src or use alt
                        imgMatch = Regex.Match(inner, "<img[^>]*(?:src\\s*=\\s*\"(?<s>[^\"]+)\"[^>]*title\\s*=\\s*\"(?<t>[^\"]+)\"|title\\s*=\\s*\"(?<t2>[^\"]+)\"[^>]*src\\s*=\\s*\"(?<s2>[^\"]+)\")[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    }

                    if (imgMatch.Success)
                    {
                        title = imgMatch.Groups["t"].Success ? imgMatch.Groups["t"].Value : (imgMatch.Groups["t2"].Success ? imgMatch.Groups["t2"].Value : null);
                        image = imgMatch.Groups["s"].Success ? imgMatch.Groups["s"].Value : (imgMatch.Groups["s2"].Success ? imgMatch.Groups["s2"].Value : null);
                    }

                    if (string.IsNullOrEmpty(title))
                    {
                        // Fallback: derive from slug
                        try
                        {
                            var seg = new Uri(url).Segments.Last().Trim('/');
                            title = SlugToTitle(seg);
                        }
                        catch
                        {
                            title = url;
                        }
                    }

                    movieAnchors.Add((url, title, image));
                }

                // Add unique results preserving order
                foreach (var a in movieAnchors)
                {
                    if (results.Any(r => r.ProviderIds != null && r.ProviderIds.ContainsKey("Aebn") && r.ProviderIds["Aebn"].Equals(a.Url, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    results.Add(new RemoteSearchResult
                    {
                        Name = a.Title,
                        ProviderIds = new Dictionary<string, string> { { "Aebn", a.Url } },
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

        // Convert a slug like "armed-services" or "at-arm-s-length" into a readable title
        private static string SlugToTitle(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return string.Empty;

            // Replace dashes with spaces
            var t = slug.Replace('-', ' ');

            // Common pattern: "-s-" often represents possessive in some slugs (e.g. at-arm-s-length -> at arm's length)
            t = Regex.Replace(t, "\b(s)\b", "'s", RegexOptions.IgnoreCase);

            // Collapse multiple spaces
            t = Regex.Replace(t, "\\s+", " ").Trim();

            // Decode any HTML entities that might appear
            try
            {
                t = System.Net.WebUtility.HtmlDecode(t);
            }
            catch
            {
                // ignore
            }

            // Title-case
            try
            {
                t = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t.ToLowerInvariant());
            }
            catch
            {
                // ignore
            }

            return t;
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