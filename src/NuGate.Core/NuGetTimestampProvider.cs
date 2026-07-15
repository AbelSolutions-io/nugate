using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NuGate.Core;

/// <summary>
/// Resolves the immutable catalog <c>created</c> timestamp (and listed status) for a package
/// version from the nuget.org V3 API, backed by a local disk cache.
///
/// Lookup path: registration index → registration page (inlined or fetched) → registration leaf
/// → catalog leaf. The registration leaf's <c>catalogEntry</c> carries <c>published</c> and
/// <c>listed</c>, but nuget.org resets <c>published</c> to 1900-01-01 when a version is unlisted,
/// so the immutable <c>created</c> is read from the catalog leaf that <c>catalogEntry.@id</c>
/// points to. (If a future feed inlines <c>created</c> on the registration entry, that is used
/// directly and the extra fetch is skipped.)
///
/// Caching (<c>%LOCALAPPDATA%/nugate/cache</c>, atomic temp-file+rename writes, safe enough for
/// parallel builds): the <c>created</c> timestamp is immutable and cached forever, but the
/// <c>listed</c> flag is mutable — takedowns unlist a version AFTER it was cached — so a cache
/// entry only short-circuits the network while its listed check is younger than
/// <see cref="DefaultListedRevalidationTtl"/>. Revalidation refetches the registration leaf for a
/// fresh <c>listed</c> and reuses the cached <c>created</c> (no catalog hop).
/// </summary>
public sealed class NuGetTimestampProvider : INuGetTimestampProvider
{
    /// <summary>SemVer2 registration base (gzip variant — decompression is enabled on the default client).</summary>
    public const string DefaultRegistrationBaseUrl = "https://api.nuget.org/v3/registration5-gz-semver2/";

    /// <summary>How long a cached <c>listed</c> flag is trusted before it is revalidated.</summary>
    public static readonly TimeSpan DefaultListedRevalidationTtl = TimeSpan.FromHours(24);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly string _registrationBaseUrl;
    private readonly int _maxAttempts;
    private readonly TimeSpan _listedTtl;
    private readonly Func<DateTimeOffset> _clock;

    public NuGetTimestampProvider(
        HttpClient? httpClient = null,
        string? cacheDirectory = null,
        string? registrationBaseUrl = null,
        int maxAttempts = 3,
        TimeSpan? listedRevalidationTtl = null,
        Func<DateTimeOffset>? clock = null)
    {
        _http = httpClient ?? CreateDefaultClient();
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory) ? DefaultCacheDirectory() : cacheDirectory!;
        var baseUrl = string.IsNullOrWhiteSpace(registrationBaseUrl) ? DefaultRegistrationBaseUrl : registrationBaseUrl!;
        _registrationBaseUrl = baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/";
        _maxAttempts = Math.Max(1, maxAttempts);
        _listedTtl = listedRevalidationTtl ?? DefaultListedRevalidationTtl;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static string DefaultCacheDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nugate",
            "cache");

    private static HttpClient CreateDefaultClient()
    {
        var handler = new HttpClientHandler();
        if (handler.SupportsAutomaticDecompression)
        {
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<PackageTimestamp?> GetTimestampAsync(PackageIdentity package, CancellationToken cancellationToken)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        var id = package.Id.Trim().ToLowerInvariant();
        var version = NuGetVersionUtil.Normalize(package.Version);

        var hasCached = TryReadCache(id, version, out var cached, out var listedIsFresh);
        if (hasCached && listedIsFresh)
        {
            return cached;
        }

        var indexUrl = _registrationBaseUrl + id + "/index.json";
        var indexDoc = await GetJsonAsync(indexUrl, cancellationToken).ConfigureAwait(false);
        if (indexDoc is null)
        {
            return null; // 404 — package/version unknown to nuget.org
        }

        LeafInfo? leaf;
        using (indexDoc)
        {
            leaf = await FindRegistrationLeafAsync(indexDoc.RootElement, version, cancellationToken).ConfigureAwait(false);
        }

        if (leaf is null)
        {
            return null; // version unknown to nuget.org
        }

        var created = leaf.Created;
        var listed = leaf.Listed;

        if (!created.HasValue && hasCached)
        {
            // Listed-revalidation pass: `created` is immutable, so the cached value is
            // authoritative and the catalog hop is skipped.
            created = cached!.Created;
        }

        if (!created.HasValue)
        {
            if (string.IsNullOrEmpty(leaf.CatalogUrl))
            {
                throw new TimestampLookupException(
                    $"No catalog 'created' timestamp available for {id} {version}.");
            }

            using var catalogDoc = await GetRequiredJsonAsync(leaf.CatalogUrl!, cancellationToken).ConfigureAwait(false);
            var catalogRoot = catalogDoc.RootElement;

            if (catalogRoot.TryGetProperty("created", out var createdElement)
                && createdElement.ValueKind == JsonValueKind.String
                && TryParseDate(createdElement.GetString(), out var parsed))
            {
                created = parsed;
            }
            else
            {
                throw new TimestampLookupException(
                    $"Catalog leaf for {id} {version} did not contain a 'created' timestamp.");
            }

            // The catalog leaf's listed flag, when present, is authoritative for this version.
            if (catalogRoot.TryGetProperty("listed", out var listedElement)
                && listedElement.ValueKind == JsonValueKind.False)
            {
                listed = false;
            }
        }

        var timestamp = new PackageTimestamp(created.Value, listed);
        WriteCache(id, version, timestamp);
        return timestamp;
    }

    private async Task<LeafInfo?> FindRegistrationLeafAsync(
        JsonElement index,
        string normalizedVersion,
        CancellationToken cancellationToken)
    {
        if (!index.TryGetProperty("items", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var page in pages.EnumerateArray())
        {
            if (page.TryGetProperty("items", out var inlineLeaves) && inlineLeaves.ValueKind == JsonValueKind.Array)
            {
                // Inlined page — free to scan, no range filtering needed.
                var hit = ScanLeaves(inlineLeaves, normalizedVersion);
                if (hit != null)
                {
                    return hit;
                }
            }
            else if (page.TryGetProperty("@id", out var pageIdElement) && pageIdElement.ValueKind == JsonValueKind.String)
            {
                // Non-inlined page — only fetch it when the version falls inside its declared range.
                if (!PageMightContain(page, normalizedVersion))
                {
                    continue;
                }

                using var pageDoc = await GetRequiredJsonAsync(pageIdElement.GetString()!, cancellationToken)
                    .ConfigureAwait(false);
                if (pageDoc.RootElement.TryGetProperty("items", out var leaves) && leaves.ValueKind == JsonValueKind.Array)
                {
                    var hit = ScanLeaves(leaves, normalizedVersion);
                    if (hit != null)
                    {
                        return hit;
                    }
                }
            }
        }

        return null;
    }

    private static bool PageMightContain(JsonElement page, string normalizedVersion)
    {
        if (page.TryGetProperty("lower", out var lower) && lower.ValueKind == JsonValueKind.String
            && page.TryGetProperty("upper", out var upper) && upper.ValueKind == JsonValueKind.String)
        {
            try
            {
                return NuGetVersionUtil.Compare(normalizedVersion, lower.GetString()!) >= 0
                    && NuGetVersionUtil.Compare(normalizedVersion, upper.GetString()!) <= 0;
            }
            catch (FormatException)
            {
                return true; // if the range can't be understood, inspect the page
            }
        }

        return true;
    }

    private static LeafInfo? ScanLeaves(JsonElement leaves, string normalizedVersion)
    {
        foreach (var leaf in leaves.EnumerateArray())
        {
            if (leaf.ValueKind != JsonValueKind.Object
                || !leaf.TryGetProperty("catalogEntry", out var catalogEntry)
                || catalogEntry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!catalogEntry.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String
                || !string.Equals(
                    NuGetVersionUtil.Normalize(versionElement.GetString()!),
                    normalizedVersion,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var info = new LeafInfo { Listed = true };

            // Listed unless explicitly false (registration view).
            if (catalogEntry.TryGetProperty("listed", out var listedElement)
                && listedElement.ValueKind == JsonValueKind.False)
            {
                info.Listed = false;
            }

            // Prefer an inlined immutable created if a feed ever provides one.
            if (catalogEntry.TryGetProperty("created", out var createdElement)
                && createdElement.ValueKind == JsonValueKind.String
                && TryParseDate(createdElement.GetString(), out var created))
            {
                info.Created = created;
            }

            if (catalogEntry.TryGetProperty("@id", out var catalogUrlElement)
                && catalogUrlElement.ValueKind == JsonValueKind.String)
            {
                info.CatalogUrl = catalogUrlElement.GetString();
            }

            return info;
        }

        return null;
    }

    private static bool TryParseDate(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    // ---- HTTP with modest retry ------------------------------------------------------------

    private async Task<JsonDocument> GetRequiredJsonAsync(string url, CancellationToken cancellationToken)
    {
        var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            throw new TimestampLookupException($"nuget.org resource unexpectedly missing (404): {url}");
        }

        return doc;
    }

    /// <returns>Parsed JSON, or null when the resource returns 404.</returns>
    /// <exception cref="TimestampLookupException">Persistent network/5xx/parse failure.</exception>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        TimestampLookupException? last = null;

        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using var response = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return JsonDocument.Parse(bytes, DocumentOptions);
                }

                last = new TimestampLookupException($"nuget.org returned HTTP {status} for {url}.");

                // Retry only transient server-side conditions; other 4xx are terminal.
                if (status != 429 && status < 500)
                {
                    throw last;
                }
            }
            catch (TimestampLookupException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // HttpRequestException, request-timeout TaskCanceledException, JsonException, etc.
                last = new TimestampLookupException($"Failed to query nuget.org at {url}: {ex.Message}", ex);
            }
        }

        throw last ?? new TimestampLookupException($"Failed to query nuget.org at {url}.");
    }

    // ---- Disk cache ------------------------------------------------------------------------

    private bool TryReadCache(string id, string version, out PackageTimestamp? timestamp, out bool listedIsFresh)
    {
        timestamp = null;
        listedIsFresh = false;
        var file = CacheFilePath(id, version);

        try
        {
            if (!File.Exists(file))
            {
                return false;
            }

            var record = JsonSerializer.Deserialize<CacheRecord>(File.ReadAllText(file));
            if (record?.Created is null)
            {
                return false;
            }

            timestamp = new PackageTimestamp(record.Created.Value, record.Listed);

            // `created` is immutable, but `listed` changes when a version is taken down. Entries
            // written before the listed check aged past the TTL must be revalidated (records from
            // older cache layouts have no ListedCheckedAtUtc and always revalidate).
            listedIsFresh = record.ListedCheckedAtUtc.HasValue
                && _clock() - record.ListedCheckedAtUtc.Value < _listedTtl;

            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false; // corrupt/locked cache entry — fall through to a fresh lookup
        }
    }

    private void WriteCache(string id, string version, PackageTimestamp timestamp)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var file = CacheFilePath(id, version);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";

            var json = JsonSerializer.Serialize(new CacheRecord
            {
                Created = timestamp.Created,
                Listed = timestamp.IsListed,
                ListedCheckedAtUtc = _clock(),
            });

            File.WriteAllText(temp, json);

            try
            {
                // Atomic when the destination does not yet exist; replace on revalidation so the
                // refreshed listed flag + check time land.
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                File.Move(temp, file);
            }
            catch (IOException)
            {
                // Another parallel build wrote the entry concurrently — discard our temp.
                TryDelete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache is best-effort; a failure here must never fail the lookup.
        }
    }

    private string CacheFilePath(string id, string version)
        => Path.Combine(_cacheDirectory, Sanitize(id) + "." + Sanitize(version) + ".json");

    private static string Sanitize(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var ok = (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '.' || c == '-' || c == '_';
            if (!ok)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore
        }
    }

    private sealed class LeafInfo
    {
        public string? CatalogUrl { get; set; }

        public bool Listed { get; set; }

        public DateTimeOffset? Created { get; set; }
    }

    private sealed class CacheRecord
    {
        public DateTimeOffset? Created { get; set; }

        public bool Listed { get; set; }

        /// <summary>When the listed flag was last confirmed against the registration API.</summary>
        public DateTimeOffset? ListedCheckedAtUtc { get; set; }
    }
}
