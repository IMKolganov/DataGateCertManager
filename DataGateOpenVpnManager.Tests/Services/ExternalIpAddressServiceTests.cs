using System.Net;
using DataGateOpenVpnManager.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace DataGateOpenVpnManager.Tests.Services;

public class ExternalIpAddressServiceTests
{
    private readonly Mock<ILogger<ExternalIpAddressService>> _logger = new();

    private static IConfiguration BuildConfig(params string[] urls)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < urls.Length; i++)
            dict[$"ExternalIpServices:{i}"] = urls[i];
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public async Task GetPublicIpAddressAsync_WhenNoServices_ReturnsNull()
    {
        var sut = new ExternalIpAddressService(
            _logger.Object,
            BuildConfig(),
            new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            new MemoryCache(new MemoryCacheOptions()));

        Assert.Null(await sut.GetPublicIpAddressAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetPublicIpAddressAsync_ReturnsTrimmedIp_AndCaches()
    {
        const string url = "https://example.test/ip";
        var calls = 0;
        var handler = new FakeHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(" 203.0.113.55 \n")
            });
        });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new ExternalIpAddressService(_logger.Object, BuildConfig(url), new HttpClient(handler), cache);

        Assert.Equal("203.0.113.55", await sut.GetPublicIpAddressAsync(CancellationToken.None));
        Assert.Equal("203.0.113.55", await sut.GetPublicIpAddressAsync(CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetPublicIpAddressAsync_WhenFirstFails_UsesSecond()
    {
        const string url1 = "https://a.test/ip";
        const string url2 = "https://b.test/ip";
        var handler = new FakeHandler((req, _) =>
        {
            if (req.RequestUri!.ToString() == url1)
                throw new HttpRequestException("down");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("198.51.100.7")
            });
        });
        var sut = new ExternalIpAddressService(
            _logger.Object, BuildConfig(url1, url2), new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal("198.51.100.7", await sut.GetPublicIpAddressAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetPublicIpAddressAsync_WhenAllFail_ReturnsNull_AndCachesNegative()
    {
        var calls = 0;
        var handler = new FakeHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("down");
        });
        var sut = new ExternalIpAddressService(
            _logger.Object,
            BuildConfig("https://a.test/ip"),
            new HttpClient(handler),
            new MemoryCache(new MemoryCacheOptions()));

        Assert.Null(await sut.GetPublicIpAddressAsync(CancellationToken.None));
        Assert.Null(await sut.GetPublicIpAddressAsync(CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetPublicIpAddressAsync_SkipsBlankResponses()
    {
        const string url1 = "https://a.test/ip";
        const string url2 = "https://b.test/ip";
        var handler = new FakeHandler((req, _) =>
        {
            var body = req.RequestUri!.ToString() == url1 ? "  \n" : "10.20.30.40";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        });
        var sut = new ExternalIpAddressService(
            _logger.Object, BuildConfig(url1, url2), new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal("10.20.30.40", await sut.GetPublicIpAddressAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetPublicIpAddressAsync_SkipsHtmlAndUsesNextProvider()
    {
        const string url1 = "https://a.test/ip";
        const string url2 = "https://b.test/ip";
        var handler = new FakeHandler((req, _) =>
        {
            var body = req.RequestUri!.ToString() == url1
                ? "<html>rate limited</html>"
                : "203.0.113.9";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        });
        var sut = new ExternalIpAddressService(
            _logger.Object, BuildConfig(url1, url2), new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal("203.0.113.9", await sut.GetPublicIpAddressAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("not-an-ip")]
    [InlineData("2a04:3540:1000:310:549d:1fff:fe9e:7afb")]
    [InlineData("::1")]
    public void TryParsePublicIp_RejectsInvalid(string raw)
    {
        Assert.False(ExternalIpAddressService.TryParsePublicIp(raw, out _));
    }

    [Fact]
    public async Task GetPublicIpAddressAsync_SkipsIpv6AndUsesNextProvider()
    {
        const string url1 = "https://a.test/ip";
        const string url2 = "https://b.test/ip";
        var handler = new FakeHandler((req, _) =>
        {
            var body = req.RequestUri!.ToString() == url1
                ? "2a04:3540:1000:310:549d:1fff:fe9e:7afb"
                : "212.147.237.141";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        });
        var sut = new ExternalIpAddressService(
            _logger.Object, BuildConfig(url1, url2), new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()));

        Assert.Equal("212.147.237.141", await sut.GetPublicIpAddressAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetPublicIpAddressAsync_ConcurrentCalls_SingleFlight()
    {
        const string url = "https://example.test/ip";
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("203.0.113.44")
            };
        });
        var sut = new ExternalIpAddressService(
            _logger.Object, BuildConfig(url), new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()));

        var t1 = sut.GetPublicIpAddressAsync(CancellationToken.None);
        var t2 = sut.GetPublicIpAddressAsync(CancellationToken.None);
        await Task.Delay(50);
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);

        Assert.All(results, r => Assert.Equal("203.0.113.44", r));
        Assert.Equal(1, calls);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
