using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Baton.Web.Auth;

/// <summary>
/// The signed-in person's API access token for one circuit (scoped). Tokens are read once from the session
/// cookie through the circuit's <see cref="HttpContext"/> and refreshed with the refresh token shortly before
/// they expire; they never reach the browser (ADR-0012).
/// </summary>
public sealed partial class AccessTokenProvider(
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    TimeProvider time,
    ILogger<AccessTokenProvider> logger) : IDisposable
{
    /// <summary>Refresh this long before expiry, so a request never leaves with a token about to lapse.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset? _expiresAt;

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loaded)
                await LoadAsync();

            if (_accessToken is not null && _refreshToken is not null && _expiresAt is { } expires
                && expires - RefreshMargin <= time.GetUtcNow())
            {
                await RefreshAsync(ct);
            }

            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadAsync()
    {
        if (httpContextAccessor.HttpContext is not { } context || context.User.Identity?.IsAuthenticated != true)
            return;

        _accessToken = await context.GetTokenAsync("access_token");
        _refreshToken = await context.GetTokenAsync("refresh_token");
        _expiresAt = DateTimeOffset.TryParse(await context.GetTokenAsync("expires_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        _loaded = true;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var options = oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        if (options.ConfigurationManager is null)
            return;

        var configuration = await options.ConfigurationManager.GetConfigurationAsync(ct);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken!,
            ["client_id"] = options.ClientId!,
        };
        if (!string.IsNullOrEmpty(options.ClientSecret))
            form["client_secret"] = options.ClientSecret;

        using var response = await options.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            LogRefreshFailed(logger, (int)response.StatusCode);
            _accessToken = null; // the API answers 401 and the page asks to sign in again
            _refreshToken = null;
            return;
        }

        using var body = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = body.RootElement;
        _accessToken = root.GetProperty("access_token").GetString();
        if (root.TryGetProperty("refresh_token", out var refresh))
            _refreshToken = refresh.GetString();
        _expiresAt = root.TryGetProperty("expires_in", out var expiresIn)
            ? time.GetUtcNow().AddSeconds(expiresIn.GetInt32())
            : null;
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing the access token failed with {StatusCode}; the person has to sign in again")]
    private static partial void LogRefreshFailed(ILogger logger, int statusCode);
}
