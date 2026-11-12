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
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
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
        }
        finally { Directory.Delete(fixture, true); }
    }
}
