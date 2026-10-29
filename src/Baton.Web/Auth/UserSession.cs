using Baton.Web.Api;

namespace Baton.Web.Auth;

/// <summary>The roles Baton grants the signed-in person.</summary>
public static class Roles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";
}

/// <summary>
/// Who is signed in and which roles the API grants them, read once per circuit from <c>GET /api/me</c> so the
/// claim-to-role mapping stays in the API (ADR-0012). The console hides actions the role lacks; the API still
/// enforces every policy.
/// </summary>
public sealed class UserSession(IBatonApi api)
{
    private Task<CurrentUser?>? _user;

    /// <returns><c>null</c> when the API refuses the caller (no Baton role, or the session expired).</returns>
    public Task<CurrentUser?> GetAsync() => _user ??= LoadAsync();

    public async Task<bool> HasRoleAsync(string role) => (await GetAsync())?.Roles.Contains(role) == true;

    private async Task<CurrentUser?> LoadAsync()
    {
        try
        {
            return await api.GetMeAsync();
        }
        catch (ApiException ex) when (ex.StatusCode is 401 or 403)
        {
            return null;
        }
    }
}
