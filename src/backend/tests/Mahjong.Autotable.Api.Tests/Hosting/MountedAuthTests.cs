using System.Net;
using Mahjong.Autotable.Api.Changsha.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Mahjong.Autotable.Api.Tests.Hosting;

public sealed class MountedAuthTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/mahjong")]
    [InlineData("/games/My%20Game")]
    public async Task ActualIdentityAndOAuthChallengeUseThePublicMount(string prefix)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "mount-auth-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var contentRoot = Path.Combine(fixture, "backend", "src", "Mahjong.Autotable.Api");
        var webRoot = Path.Combine(contentRoot, "wwwroot");
        var publicRoot = Path.GetFullPath(Path.Combine(contentRoot, "../../../frontend/autotable"));
        Directory.CreateDirectory(webRoot);
        Directory.CreateDirectory(publicRoot);
        const string gameHtml = "<title>DIRECT ROOT GAME</title><button id=\"new-game\">New Game</button>";
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "index.html"), gameHtml);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"),
            "<meta http-equiv=\"refresh\" content=\"0;url=/autotable/\">");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(contentRoot);
            builder.UseWebRoot(webRoot);
            builder.UseSetting("BASE_URL", prefix);
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(fixture, "fixture.db")}");
            builder.UseSetting("Authentication:Google:Enabled", "true");
            builder.UseSetting("Authentication:Google:ClientId", "synthetic-prefix-client");
            builder.UseSetting("Authentication:Google:ClientSecret", "synthetic-fixture-only-not-an-operator-key");
            builder.ConfigureServices(services => services.Configure<ChangshaRuntimeOptions>(options => options.PersistSnapshots = false));
        });
        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var entry = await client.GetAsync(prefix + "/?gameId=synthetic");
            Assert.Equal(HttpStatusCode.OK, entry.StatusCode);
            Assert.Null(entry.Headers.Location);
            Assert.Equal(gameHtml, await entry.Content.ReadAsStringAsync());
            using var identity = await client.PostAsync(prefix + "/api/identity", null);
            Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
            var cookie = identity.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("mahjong_pid="));
            Assert.Contains("path=" + prefix + "/", cookie, StringComparison.OrdinalIgnoreCase);
            using var login = await client.GetAsync(prefix + "/api/auth/login/google?returnUrl=" + Uri.EscapeDataString(prefix + "/"));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var destination = login.Headers.Location!;
            Assert.Equal("accounts.google.com", destination.Host);
            var query = QueryHelpers.ParseQuery(destination.Query);
            Assert.Equal("http://localhost" + prefix + "/api/auth/callback/google", query["redirect_uri"].Single());
            Assert.Contains("code_challenge", query.Keys);
            Assert.All(login.Headers.GetValues("Set-Cookie"),
                value => Assert.Contains("path=" + prefix + "/", value, StringComparison.OrdinalIgnoreCase));
            using var playlist = await client.GetAsync(prefix + "/api/tables/synthetic/livestream/playlist.m3u8");
            Assert.Equal(HttpStatusCode.MovedPermanently, playlist.StatusCode);
            Assert.Equal(prefix + "/api/voice/livestream/synthetic/playlist.m3u8", playlist.Headers.Location!.OriginalString);
            Assert.Equal("<" + prefix + "/api/voice/livestream>; rel=\"sunset\"", playlist.Headers.GetValues("Link").Single());
            using var streamStart = await client.PostAsync(prefix + "/api/tables/synthetic/livestream/start", null);
            Assert.Equal(HttpStatusCode.PermanentRedirect, streamStart.StatusCode);
            Assert.Equal(prefix + "/api/voice/livestream/synthetic/start", streamStart.Headers.Location!.OriginalString);
        }
        finally { Directory.Delete(fixture, true); }
    }
}
