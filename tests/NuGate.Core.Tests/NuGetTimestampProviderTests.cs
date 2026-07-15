using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGate.Core;
using Xunit;

namespace NuGate.Core.Tests;

public class NuGetTimestampProviderTests : IDisposable
{
    private const string Base = "https://test.example/reg/";
    private readonly string _cacheDir;

    public NuGetTimestampProviderTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "nugate-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private NuGetTimestampProvider CreateProvider(StubHttpMessageHandler handler)
        => new(new HttpClient(handler), _cacheDir, Base);

    private static string IndexUrl(string id) => Base + id + "/index.json";

    private static string InlinedIndex(string catalogUrl, string version, bool listed, string? createdInline = null)
    {
        var created = createdInline is null ? string.Empty : $"\"created\": \"{createdInline}\",";
        return $$"""
        {
          "count": 1,
          "items": [
            {
              "lower": "{{version}}",
              "upper": "{{version}}",
              "items": [
                {
                  "catalogEntry": {
                    "@id": "{{catalogUrl}}",
                    {{created}}
                    "version": "{{version}}",
                    "listed": {{(listed ? "true" : "false")}},
                    "published": "2020-01-01T00:00:00Z"
                  }
                }
              ]
            }
          ]
        }
        """;
    }

    private static string CatalogLeaf(string created, bool listed, string published)
        => $$"""
        { "created": "{{created}}", "listed": {{(listed ? "true" : "false")}}, "published": "{{published}}" }
        """;

    [Fact]
    public async Task Resolves_created_from_catalog_leaf()
    {
        var catalogUrl = "https://test.example/catalog/newtonsoft.json.13.0.3.json";
        var handler = new StubHttpMessageHandler(url =>
        {
            if (url == IndexUrl("newtonsoft.json"))
            {
                return (HttpStatusCode.OK, InlinedIndex(catalogUrl, "13.0.3", listed: true));
            }

            if (url == catalogUrl)
            {
                return (HttpStatusCode.OK, CatalogLeaf("2023-03-08T03:00:00Z", listed: true, published: "2023-03-08T00:00:00Z"));
            }

            return (HttpStatusCode.NotFound, null);
        });

        var provider = CreateProvider(handler);
        var result = await provider.GetTimestampAsync(new PackageIdentity("Newtonsoft.Json", "13.0.3"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2023, 3, 8, 3, 0, 0, TimeSpan.Zero), result!.Created);
        Assert.True(result.IsListed);
    }

    [Fact]
    public async Task Caches_result_and_does_not_refetch()
    {
        var catalogUrl = "https://test.example/catalog/a.1.0.0.json";
        var handler = new StubHttpMessageHandler(url =>
        {
            if (url == IndexUrl("a"))
            {
                return (HttpStatusCode.OK, InlinedIndex(catalogUrl, "1.0.0", listed: true));
            }

            if (url == catalogUrl)
            {
                return (HttpStatusCode.OK, CatalogLeaf("2024-05-01T00:00:00Z", listed: true, published: "2024-05-01T00:00:00Z"));
            }

            return (HttpStatusCode.NotFound, null);
        });

        var provider = CreateProvider(handler);
        var pkg = new PackageIdentity("A", "1.0.0");

        var first = await provider.GetTimestampAsync(pkg, CancellationToken.None);
        var requestsAfterFirst = handler.Requests.Count;

        // A brand-new provider sharing the same cache directory must hit disk, not the network.
        var second = await new NuGetTimestampProvider(new HttpClient(handler), _cacheDir, Base)
            .GetTimestampAsync(pkg, CancellationToken.None);

        Assert.Equal(first!.Created, second!.Created);
        Assert.Equal(first.IsListed, second.IsListed);
        Assert.Equal(requestsAfterFirst, handler.Requests.Count); // no further network calls
        Assert.True(requestsAfterFirst >= 2); // index + catalog leaf on the first lookup
    }

    [Fact]
    public async Task Stale_listed_flag_is_revalidated_and_cached_created_is_reused()
    {
        var catalogUrl = "https://test.example/catalog/a.1.0.0.json";
        var listedNow = true;
        var handler = new StubHttpMessageHandler(url =>
        {
            if (url == IndexUrl("a"))
            {
                return (HttpStatusCode.OK, InlinedIndex(catalogUrl, "1.0.0", listed: listedNow));
            }

            if (url == catalogUrl)
            {
                return (HttpStatusCode.OK, CatalogLeaf("2024-05-01T00:00:00Z", listed: true, published: "2024-05-01T00:00:00Z"));
            }

            return (HttpStatusCode.NotFound, null);
        });

        var pkg = new PackageIdentity("A", "1.0.0");
        var t0 = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        var first = await new NuGetTimestampProvider(new HttpClient(handler), _cacheDir, Base, clock: () => t0)
            .GetTimestampAsync(pkg, CancellationToken.None);
        Assert.True(first!.IsListed);

        // The version is unlisted after a takedown; the cached listed flag is now 25h old (TTL 24h).
        listedNow = false;
        var requestsBefore = handler.Requests.Count;

        var second = await new NuGetTimestampProvider(new HttpClient(handler), _cacheDir, Base, clock: () => t0.AddHours(25))
            .GetTimestampAsync(pkg, CancellationToken.None);

        Assert.False(second!.IsListed);              // the takedown is seen despite the cache
        Assert.Equal(first.Created, second.Created); // immutable created reused from cache
        var revalidationRequests = handler.Requests.Skip(requestsBefore).ToList();
        Assert.Contains(IndexUrl("a"), revalidationRequests);   // registration refetched
        Assert.DoesNotContain(catalogUrl, revalidationRequests); // catalog hop skipped

        // And the refreshed flag is cached again: a third provider inside the new TTL window
        // must answer from disk.
        var requestsAfterSecond = handler.Requests.Count;
        var third = await new NuGetTimestampProvider(new HttpClient(handler), _cacheDir, Base, clock: () => t0.AddHours(26))
            .GetTimestampAsync(pkg, CancellationToken.None);

        Assert.False(third!.IsListed);
        Assert.Equal(requestsAfterSecond, handler.Requests.Count);
    }

    [Fact]
    public async Task Unknown_package_returns_null()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.NotFound, null));
        var provider = CreateProvider(handler);

        var result = await provider.GetTimestampAsync(new PackageIdentity("Ghost.Pkg", "9.9.9"), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Uses_created_never_published_for_unlisted_version()
    {
        // Unlisted packages have published reset to 1900-01-01; created stays immutable.
        var catalogUrl = "https://test.example/catalog/yanked.5.0.0.json";
        var handler = new StubHttpMessageHandler(url =>
        {
            if (url == IndexUrl("yanked"))
            {
                return (HttpStatusCode.OK, InlinedIndex(catalogUrl, "5.0.0", listed: false));
            }

            if (url == catalogUrl)
            {
                return (HttpStatusCode.OK, CatalogLeaf("2025-06-10T00:00:00Z", listed: false, published: "1900-01-01T00:00:00Z"));
            }

            return (HttpStatusCode.NotFound, null);
        });

        var provider = CreateProvider(handler);
        var result = await provider.GetTimestampAsync(new PackageIdentity("Yanked", "5.0.0"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.IsListed);
        // The real creation date, not the 1900 published sentinel.
        Assert.Equal(new DateTimeOffset(2025, 6, 10, 0, 0, 0, TimeSpan.Zero), result.Created);
    }

    [Fact]
    public async Task Inlined_created_on_registration_entry_skips_catalog_fetch()
    {
        var catalogUrl = "https://test.example/catalog/should-not-be-fetched.json";
        var handler = new StubHttpMessageHandler(url =>
        {
            if (url == IndexUrl("inline"))
            {
                return (HttpStatusCode.OK, InlinedIndex(catalogUrl, "2.0.0", listed: true, createdInline: "2022-02-02T00:00:00Z"));
            }

            // Any fetch of the catalog URL would be a bug — fail loudly if attempted.
            return (HttpStatusCode.InternalServerError, null);
        });

        var provider = CreateProvider(handler);
        var result = await provider.GetTimestampAsync(new PackageIdentity("Inline", "2.0.0"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(new DateTimeOffset(2022, 2, 2, 0, 0, 0, TimeSpan.Zero), result!.Created);
        Assert.DoesNotContain(catalogUrl, handler.Requests);
    }

    [Fact]
    public async Task Retries_transient_server_error_then_succeeds()
    {
        var catalogUrl = "https://test.example/catalog/retry.1.0.0.json";
        var indexUrl = IndexUrl("retry");
        var attempts = 0;

        var handler = new StubHttpMessageHandler(url =>
        {
            if (url == indexUrl)
            {
                attempts++;
                if (attempts < 3)
                {
                    return (HttpStatusCode.ServiceUnavailable, null);
                }

                return (HttpStatusCode.OK, InlinedIndex(catalogUrl, "1.0.0", listed: true));
            }

            if (url == catalogUrl)
            {
                return (HttpStatusCode.OK, CatalogLeaf("2024-01-01T00:00:00Z", listed: true, published: "2024-01-01T00:00:00Z"));
            }

            return (HttpStatusCode.NotFound, null);
        });

        var provider = CreateProvider(handler);
        var result = await provider.GetTimestampAsync(new PackageIdentity("Retry", "1.0.0"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Persistent_server_error_throws_lookup_exception()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.ServiceUnavailable, null));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<TimestampLookupException>(
            () => provider.GetTimestampAsync(new PackageIdentity("Down.Pkg", "1.0.0"), CancellationToken.None));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<string, (HttpStatusCode Status, string? Body)> _responder;

        public StubHttpMessageHandler(Func<string, (HttpStatusCode, string?)> responder)
        {
            _responder = responder;
        }

        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            var (status, body) = _responder(url);
            var response = new HttpResponseMessage(status);
            if (body != null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }
}
