using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LocalAudiobookStudio.Web.Hosting;
using LocalAudiobookStudio.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Web.Tests;

public sealed class LocalSessionSecurityTests
{
    private const string ExpectedContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; "
        + "media-src 'self'; connect-src 'self'; object-src 'none'; frame-src 'none'; "
        + "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    [Fact]
    public void Launch_ticket_is_single_use_and_expired_tickets_are_rejected()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var sessions = new LocalSessionStore();
        var launches = new LaunchTokenStore(time, sessions);

        var ticket = launches.Issue(TimeSpan.FromSeconds(60));

        Assert.True(launches.TryRedeem(ticket.Plaintext, out var session));
        Assert.True(sessions.IsValid(session.Plaintext));
        Assert.False(launches.TryRedeem(ticket.Plaintext, out _));
        Assert.False(launches.TryRedeem("not-a-launch-token", out _));

        var expired = launches.Issue(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(61));

        Assert.False(launches.TryRedeem(expired.Plaintext, out _));
    }

    [Fact]
    public async Task Concurrent_launch_redemption_succeeds_exactly_once()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var launches = new LaunchTokenStore(time, new LocalSessionStore());
        var ticket = launches.Issue(TimeSpan.FromSeconds(60));
        using var start = new ManualResetEventSlim(initialState: false);
        var attempts = Enumerable.Range(0, 16)
            .Select(_attempt => Task.Run(
                () =>
                {
                    start.Wait();
                    return launches.TryRedeem(ticket.Plaintext, out _);
                }))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, succeeded => succeeded);
    }

    [Fact]
    public async Task Launch_exchange_sets_strict_cookie_redirects_cleanly_and_never_logs_token()
    {
        var logs = new CollectingLoggerProvider();
        await using var host = await RunningHost.StartAsync(
            loggerProvider: logs,
            issueLaunchBeforeStart: true);
        var ticket = host.PreissuedLaunch
            ?? throw new Xunit.Sdk.XunitException("Expected a launch ticket issued before host startup.");

        using var response = await host.Client.GetAsync(
            $"/_session/launch?token={Uri.EscapeDataString(ticket.Plaintext)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var sessionCookie = GetCookie(response, "las_session");
        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("las_session=", StringComparison.Ordinal));
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ticket.Plaintext, response.Headers.Location?.OriginalString ?? string.Empty, StringComparison.Ordinal);
        AssertSecurityHeaders(response);

        using var bootstrap = await host.SendAsync(HttpMethod.Get, "/api/bootstrap", sessionCookie);
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        Assert.False(bootstrap.Headers.Contains("Access-Control-Allow-Origin"));
        using var body = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("antiforgeryRequestToken").GetString()));
        Assert.False(body.RootElement.GetProperty("capabilities").GetProperty("generation").GetBoolean());
        Assert.False(body.RootElement.GetProperty("capabilities").GetProperty("training").GetBoolean());

        Assert.DoesNotContain(
            logs.Messages,
            message => message.Contains(ticket.Plaintext, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_replayed_and_expired_launch_tokens_return_stable_problems()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var host = await RunningHost.StartAsync(time);

        using var invalid = await host.Client.GetAsync(
            "/_session/launch?token=invalid",
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(invalid, HttpStatusCode.Unauthorized, "invalid_launch_token");

        var redeemed = host.Launches.Issue(TimeSpan.FromSeconds(60));
        using var first = await host.Client.GetAsync(
            $"/_session/launch?token={Uri.EscapeDataString(redeemed.Plaintext)}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        using var replay = await host.Client.GetAsync(
            $"/_session/launch?token={Uri.EscapeDataString(redeemed.Plaintext)}",
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(replay, HttpStatusCode.Unauthorized, "invalid_launch_token");

        var expired = host.Launches.Issue(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(61));
        using var expiredResponse = await host.Client.GetAsync(
            $"/_session/launch?token={Uri.EscapeDataString(expired.Plaintext)}",
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(expiredResponse, HttpStatusCode.Unauthorized, "invalid_launch_token");
    }

    [Fact]
    public async Task Health_is_public_but_static_and_api_resources_require_a_session()
    {
        await using var host = await RunningHost.StartAsync();

        using var health = await host.Client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var staticFile = await host.Client.GetAsync("/index.html", TestContext.Current.CancellationToken);
        await AssertProblemAsync(staticFile, HttpStatusCode.Unauthorized, "session_required");

        using var api = await host.Client.GetAsync("/api/bootstrap", TestContext.Current.CancellationToken);
        await AssertProblemAsync(api, HttpStatusCode.Unauthorized, "session_required");
    }

    [Fact]
    public async Task Anonymous_exemptions_apply_only_to_the_exact_get_routes()
    {
        await using var host = await RunningHost.StartAsync();

        using var postHealth = await host.SendAsync(HttpMethod.Post, "/healthz");
        await AssertProblemAsync(postHealth, HttpStatusCode.Unauthorized, "session_required");

        var launch = host.Launches.Issue(TimeSpan.FromSeconds(60));
        using var postLaunch = await host.SendAsync(
            HttpMethod.Post,
            $"/_session/launch?token={Uri.EscapeDataString(launch.Plaintext)}");
        await AssertProblemAsync(postLaunch, HttpStatusCode.Unauthorized, "session_required");

        using var trailingHealth = await host.Client.GetAsync("/healthz/", TestContext.Current.CancellationToken);
        await AssertProblemAsync(trailingHealth, HttpStatusCode.Unauthorized, "session_required");
    }

    [Fact]
    public async Task A_session_cookie_is_invalid_after_a_fresh_host_process_starts()
    {
        string sessionCookie;
        await using (var firstHost = await RunningHost.StartAsync())
        {
            sessionCookie = await firstHost.RedeemAsync();
        }

        await using var freshHost = await RunningHost.StartAsync();
        using var response = await freshHost.SendAsync(HttpMethod.Get, "/api/bootstrap", sessionCookie);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "session_required");
    }

    [Fact]
    public async Task Every_request_requires_the_exact_runtime_host()
    {
        await using var host = await RunningHost.StartAsync();
        var sessionCookie = await host.RedeemAsync();
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/bootstrap");
        apiRequest.Headers.Host = $"localhost:{host.BaseAddress.Port}";
        apiRequest.Headers.Add("Cookie", sessionCookie);

        using var apiResponse = await host.Client.SendAsync(apiRequest, TestContext.Current.CancellationToken);
        await AssertProblemAsync(apiResponse, HttpStatusCode.BadRequest, "invalid_host");

        using var healthRequest = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        healthRequest.Headers.Host = $"localhost:{host.BaseAddress.Port}";
        using var healthResponse = await host.Client.SendAsync(healthRequest, TestContext.Current.CancellationToken);
        await AssertProblemAsync(healthResponse, HttpStatusCode.BadRequest, "invalid_host");

        var launch = host.Launches.Issue(TimeSpan.FromSeconds(60));
        using var launchRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/_session/launch?token={Uri.EscapeDataString(launch.Plaintext)}");
        launchRequest.Headers.Host = $"localhost:{host.BaseAddress.Port}";
        using var launchResponse = await host.Client.SendAsync(launchRequest, TestContext.Current.CancellationToken);
        await AssertProblemAsync(launchResponse, HttpStatusCode.BadRequest, "invalid_host");
    }

    [Fact]
    public async Task Unsafe_api_requests_require_exact_origin_and_valid_antiforgery()
    {
        await using var host = await RunningHost.StartAsync(mapUnsafeEndpoint: true);
        var sessionCookie = await host.RedeemAsync();
        using var bootstrap = await host.SendAsync(HttpMethod.Get, "/api/bootstrap", sessionCookie);
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        var antiforgeryCookie = GetCookie(bootstrap, cookieNamePrefix: ".AspNetCore.Antiforgery.");
        using var body = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var requestToken = body.RootElement.GetProperty("antiforgeryRequestToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestToken));
        var cookies = $"{sessionCookie}; {antiforgeryCookie}";

        using var valid = await host.SendAsync(
            HttpMethod.Post,
            "/api/unsafe-test",
            cookies,
            host.BaseAddress.GetLeftPart(UriPartial.Authority),
            requestToken);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        using var missingOrigin = await host.SendAsync(
            HttpMethod.Post,
            "/api/unsafe-test",
            cookies,
            antiforgeryToken: requestToken);
        await AssertProblemAsync(missingOrigin, HttpStatusCode.Forbidden, "invalid_origin");

        using var wrongOrigin = await host.SendAsync(
            HttpMethod.Post,
            "/api/unsafe-test",
            cookies,
            "http://localhost:12345",
            requestToken);
        await AssertProblemAsync(wrongOrigin, HttpStatusCode.Forbidden, "invalid_origin");

        using var missingToken = await host.SendAsync(
            HttpMethod.Post,
            "/api/unsafe-test",
            cookies,
            host.BaseAddress.GetLeftPart(UriPartial.Authority));
        await AssertProblemAsync(missingToken, HttpStatusCode.BadRequest, "antiforgery_failed");

        using var wrongToken = await host.SendAsync(
            HttpMethod.Post,
            "/api/unsafe-test",
            cookies,
            host.BaseAddress.GetLeftPart(UriPartial.Authority),
            "wrong-antiforgery-token");
        await AssertProblemAsync(wrongToken, HttpStatusCode.BadRequest, "antiforgery_failed");

        using var formFallbackRequest = new HttpRequestMessage(HttpMethod.Post, "/api/unsafe-test");
        formFallbackRequest.Headers.Add("Cookie", cookies);
        formFallbackRequest.Headers.Add("Origin", host.BaseAddress.GetLeftPart(UriPartial.Authority));
        formFallbackRequest.Content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = requestToken! });
        using var formFallback = await host.Client.SendAsync(
            formFallbackRequest,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(formFallback, HttpStatusCode.BadRequest, "antiforgery_failed");

        using var multipleTokensRequest = new HttpRequestMessage(HttpMethod.Post, "/api/unsafe-test");
        multipleTokensRequest.Headers.Add("Cookie", cookies);
        multipleTokensRequest.Headers.Add("Origin", host.BaseAddress.GetLeftPart(UriPartial.Authority));
        multipleTokensRequest.Headers.TryAddWithoutValidation(
            "X-CSRF-TOKEN",
            [requestToken!, requestToken!]);
        multipleTokensRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var multipleTokens = await host.Client.SendAsync(
            multipleTokensRequest,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(multipleTokens, HttpStatusCode.BadRequest, "antiforgery_failed");

        using var multipleOriginsRequest = new HttpRequestMessage(HttpMethod.Post, "/api/unsafe-test");
        multipleOriginsRequest.Headers.Add("Cookie", cookies);
        multipleOriginsRequest.Headers.TryAddWithoutValidation(
            "Origin",
            [host.BaseAddress.GetLeftPart(UriPartial.Authority), host.BaseAddress.GetLeftPart(UriPartial.Authority)]);
        multipleOriginsRequest.Headers.Add("X-CSRF-TOKEN", requestToken);
        multipleOriginsRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var multipleOrigins = await host.Client.SendAsync(
            multipleOriginsRequest,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(multipleOrigins, HttpStatusCode.Forbidden, "invalid_origin");
    }

    [Fact]
    public async Task Responses_have_browser_security_headers_and_no_cors_allowance()
    {
        await using var host = await RunningHost.StartAsync();

        using var response = await host.Client.GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSecurityHeaders(response);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal((int)expectedStatus, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.TryGetProperty("detail", out _));
        AssertSecurityHeaders(response);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(
            ExpectedContentSecurityPolicy,
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static string GetCookie(HttpResponseMessage response, string? exactName = null, string? cookieNamePrefix = null)
    {
        foreach (var value in response.Headers.GetValues("Set-Cookie"))
        {
            var pair = value.Split(';', 2)[0];
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var name = pair[..separator];
            if (string.Equals(name, exactName, StringComparison.Ordinal)
                || (cookieNamePrefix is not null && name.StartsWith(cookieNamePrefix, StringComparison.Ordinal)))
            {
                return pair;
            }
        }

        throw new Xunit.Sdk.XunitException("Expected response cookie was not present.");
    }

    private sealed class RunningHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string root;

        private RunningHost(
            WebApplication app,
            HttpClient client,
            Uri baseAddress,
            LaunchTokenStore launches,
            LaunchTicket? preissuedLaunch,
            string root)
        {
            _app = app;
            this.root = root;
            Client = client;
            BaseAddress = baseAddress;
            Launches = launches;
            PreissuedLaunch = preissuedLaunch;
        }

        public HttpClient Client { get; }

        public Uri BaseAddress { get; }

        public LaunchTokenStore Launches { get; }

        public LaunchTicket? PreissuedLaunch { get; }

        public static async Task<RunningHost> StartAsync(
            TimeProvider? timeProvider = null,
            ILoggerProvider? loggerProvider = null,
            bool mapUnsafeEndpoint = false,
            bool issueLaunchBeforeStart = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "las-session-" + Guid.NewGuid().ToString("N"));
            var app = LocalWebHost.Build(
                new LocalWebHostOptions(root),
                services =>
                {
                    services.AddLogging(logging =>
                    {
                        logging.ClearProviders();
                        if (loggerProvider is not null)
                        {
                            logging.AddProvider(loggerProvider);
                        }
                    });

                    if (timeProvider is not null)
                    {
                        services.AddSingleton<TimeProvider>(timeProvider);
                    }
                });
            if (mapUnsafeEndpoint)
            {
                app.MapPost("/api/unsafe-test", () => Results.Ok(new { accepted = true }));
            }

            var launches = app.Services.GetRequiredService<LaunchTokenStore>();
            var preissuedLaunch = issueLaunchBeforeStart
                ? launches.Issue(TimeSpan.FromSeconds(60))
                : null;
            await app.StartAsync(TestContext.Current.CancellationToken);
            var server = app.Services.GetRequiredService<IServer>();
            var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                BaseAddress = new Uri(address),
            };
            return new RunningHost(app, client, client.BaseAddress, launches, preissuedLaunch, root);
        }

        public async Task<string> RedeemAsync()
        {
            var ticket = Launches.Issue(TimeSpan.FromSeconds(60));
            using var response = await Client.GetAsync(
                $"/_session/launch?token={Uri.EscapeDataString(ticket.Plaintext)}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return GetCookie(response, "las_session");
        }

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            string? cookies = null,
            string? origin = null,
            string? antiforgeryToken = null)
        {
            var request = new HttpRequestMessage(method, path);
            if (cookies is not null)
            {
                request.Headers.Add("Cookie", cookies);
            }

            if (origin is not null)
            {
                request.Headers.Add("Origin", origin);
            }

            if (antiforgeryToken is not null)
            {
                request.Headers.Add("X-CSRF-TOKEN", antiforgeryToken);
            }

            if (method != HttpMethod.Get && method != HttpMethod.Head)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            return await Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Enqueue(exception.ToString());
                }
            }
        }
    }
}
