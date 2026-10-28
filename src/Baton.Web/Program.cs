using Baton.Web.Api;
using Baton.Web.Auth;
using Baton.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddBatonSignIn(builder.Configuration);

// The console reaches data only through the Control Plane API (ADR-0012). No client-wide timeout: log tails
// stream for as long as a task runs; BatonApiClient bounds every other request itself.
var apiBaseUrl = builder.Configuration["ControlPlaneApi:BaseUrl"]
    ?? throw new InvalidOperationException("ControlPlaneApi:BaseUrl is not configured.");
builder.Services.AddHttpClient<IBatonApi, BatonApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/");
    client.Timeout = Timeout.InfiniteTimeSpan;
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "web" })).AllowAnonymous();
app.MapStaticAssets().AllowAnonymous();
app.MapBatonSignIn();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
