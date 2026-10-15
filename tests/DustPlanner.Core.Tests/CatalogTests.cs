using System.Net;
using DustPlanner.Core.Cards;

namespace DustPlanner.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void Parser_keeps_collectible_cards_and_multi_class_info()
    {
        var catalog = TestData.SyntheticCatalog();

        Assert.Equal(14, catalog.Count); // the hero power is not collectible
        Assert.Null(catalog.GetByDbfId(1011));
        var dual = catalog.GetByDbfId(1008)!;
        Assert.Equal(new[] { "PRIEST", "MAGE" }, dual.Classes);
        Assert.True(dual.IsPlayableBy("MAGE"));
        Assert.True(dual.IsPlayableBy("PRIEST"));
        Assert.False(dual.IsPlayableBy("ROGUE"));
        Assert.True(catalog.GetByDbfId(1004)!.IsPlayableBy("ROGUE")); // neutral
        Assert.True(catalog.GetByDbfId(1003)!.IsLegendary);
        Assert.Equal("SYN_003", catalog.GetByCardId("SYN_003")!.CardId);
        Assert.Single(catalog.FindByName("dream keeper (SYNTHETIC)"));
        Assert.Contains("FUTURE_SET", catalog.Sets);
        Assert.Equal("synthetic", catalog.Build);
    }

    [Fact]
    public async Task Provider_resolves_build_from_redirect_caches_and_falls_back_offline()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dustplanner-catalog-" + Guid.NewGuid().ToString("N"));
        var handler = new FakeHandler(TestData.Fixture("cards.synthetic.json"), build: "250000");
        var options = new HearthstoneJsonOptions { CacheDirectory = dir, MaxAge = TimeSpan.FromHours(1) };
        var time = new FakeTime(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var http = new HttpClient(handler);
            var provider = new HearthstoneJsonCatalogProvider(http, options, time);

            var first = await provider.GetAsync();
            Assert.False(first.FromCache);
            Assert.Equal("250000", first.Catalog.Build);
            Assert.Equal(14, first.Catalog.Count);
            Assert.True(File.Exists(Path.Combine(dir, "250000", "cards.collectible.enUS.json")));
            Assert.Equal(1, handler.Downloads);

            var second = await provider.GetAsync();
            Assert.True(second.FromCache);
            Assert.Equal(1, handler.Downloads);

            time.Advance(TimeSpan.FromHours(2));
            handler.Offline = true;
            var third = await provider.GetAsync();
            Assert.True(third.FromCache);
            Assert.Contains("cached build 250000", third.Warning);

            handler.Offline = false;
            handler.Build = "250001";
            var fourth = await provider.GetAsync();
            Assert.Equal("250001", fourth.Catalog.Build);
            Assert.Equal(2, handler.Downloads);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Provider_resolves_build_from_the_latest_index_when_there_is_no_redirect()
    {
        // Since 2026 HearthstoneJSON serves /v1/latest/... directly; the build is only visible in the /v1/latest/ directory index.
        var dir = Path.Combine(Path.GetTempPath(), "dustplanner-catalog-" + Guid.NewGuid().ToString("N"));
        var handler = new FakeHandler(TestData.Fixture("cards.synthetic.json"), build: "255026") { Redirects = false };
        var options = new HearthstoneJsonOptions { CacheDirectory = dir, MaxAge = TimeSpan.FromHours(1) };
        try
        {
            using var http = new HttpClient(handler);
            var provider = new HearthstoneJsonCatalogProvider(http, options);

            var first = await provider.GetAsync();
            Assert.Equal("255026", first.Catalog.Build);
            Assert.True(File.Exists(Path.Combine(dir, "255026", "cards.collectible.enUS.json")));
            Assert.Equal(1, handler.Downloads);

            handler.Build = "255100";
            var second = await provider.GetAsync(forceRefresh: true);
            Assert.Equal("255100", second.Catalog.Build);
            Assert.Equal(2, handler.Downloads);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeHandler(string json, string build) : HttpMessageHandler
    {
        public string Build { get; set; } = build;

        public bool Offline { get; set; }

        /// <summary>True: the old behaviour (302 to /v1/{build}/). False: today's behaviour (200 on /latest/, build only in the index page).</summary>
        public bool Redirects { get; set; } = true;

        public int Downloads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/latest/", StringComparison.Ordinal))
            {
                var html = $"<html><body><a href=\"/v1/{Build}\">..</a><a href=\"/v1/{Build}/all/cards.json\">cards.json</a></body></html>";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
            }

            if (path.Contains("/latest/", StringComparison.Ordinal))
            {
                if (!Redirects)
                {
                    // Served directly; the caller discards the body (headers-only read) and must look at the index instead.
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
                }

                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(request.RequestUri, path.Replace("/latest/", "/" + Build + "/", StringComparison.Ordinal));
                return Task.FromResult(redirect);
            }

            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
