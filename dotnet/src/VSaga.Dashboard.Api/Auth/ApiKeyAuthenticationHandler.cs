using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace VSaga.Dashboard.Api.Auth;

public static class ApiKeyAuthenticationDefaults
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    /// <summary>SignalR's JS client sends the <c>accessTokenFactory</c> token as this header
    /// (<c>Bearer &lt;token&gt;</c>) on plain HTTP calls — notably the negotiate POST.</summary>
    public const string BearerPrefix = "Bearer ";

    /// <summary>SignalR clients can't set custom headers (including Authorization) on the WebSocket
    /// upgrade itself — only on the initial negotiate call — so the JS client falls back to sending
    /// the <c>accessTokenFactory</c> token via this query string parameter for the upgrade request.</summary>
    public const string QueryStringParameterName = "access_token";
}

#pragma warning disable S2094 // no options of our own to add — required as a distinct type by AddScheme<TOptions, THandler>
public sealed class ApiKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions;
#pragma warning restore S2094

/// <summary>
/// Validates a single shared secret (<c>Dashboard:ApiKey</c> in configuration) sent via the
/// <see cref="ApiKeyAuthenticationDefaults.HeaderName"/> header, an <c>Authorization: Bearer</c>
/// header, or the <see cref="ApiKeyAuthenticationDefaults.QueryStringParameterName"/> query string
/// (the latter two for SignalR — see <see cref="ApiKeyAuthenticationDefaults"/>). Fails closed: an
/// unconfigured key denies every request rather than silently disabling auth.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredKey = configuration["Dashboard:ApiKey"];
        if (string.IsNullOrEmpty(configuredKey))
            return Task.FromResult(AuthenticateResult.Fail("Dashboard:ApiKey is not configured."));

        var providedKey = Request.Headers[ApiKeyAuthenticationDefaults.HeaderName].FirstOrDefault()
            ?? GetBearerToken(Request.Headers.Authorization.FirstOrDefault())
            ?? Request.Query[ApiKeyAuthenticationDefaults.QueryStringParameterName].FirstOrDefault();

        if (string.IsNullOrEmpty(providedKey) || !FixedTimeEquals(providedKey, configuredKey))
            return Task.FromResult(AuthenticateResult.Fail("Missing or invalid API key."));

        var identity = new ClaimsIdentity(ApiKeyAuthenticationDefaults.SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, ApiKeyAuthenticationDefaults.SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// The default challenge writes a bare 401 with no body, which leaves the most common setup
    /// mistake — a caller who simply forgot the key — with nothing to go on but a status code.
    /// This says which credentials the endpoint accepts and where they are documented.
    /// </summary>
    /// <remarks>
    /// Deliberately identical for a missing key, a wrong key, and an unconfigured server: the reason
    /// is logged server-side (<see cref="AuthenticateResult.Fail(string)"/>) but never echoed, so a
    /// response can't be used to probe whether a particular key was close to correct.
    /// </remarks>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = ApiKeyAuthenticationDefaults.SchemeName;

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail =
                $"Provide the dashboard API key as the '{ApiKeyAuthenticationDefaults.HeaderName}' header, "
                + $"an 'Authorization: {ApiKeyAuthenticationDefaults.BearerPrefix.Trim()} <key>' header, or the "
                + $"'{ApiKeyAuthenticationDefaults.QueryStringParameterName}' query string. "
                + "See docs/dashboard.md#authentication.",
            Instance = Request.Path,
        };

        // The contentType argument is load-bearing: WriteAsJsonAsync sets "application/json" itself and
        // would overwrite a Response.ContentType assigned before the call.
        return Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }

    private static string? GetBearerToken(string? authorizationHeader) =>
        authorizationHeader?.StartsWith(ApiKeyAuthenticationDefaults.BearerPrefix, StringComparison.OrdinalIgnoreCase) == true
            ? authorizationHeader[ApiKeyAuthenticationDefaults.BearerPrefix.Length..]
            : null;

    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
