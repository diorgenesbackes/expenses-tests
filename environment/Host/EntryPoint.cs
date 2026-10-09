using System.Net;
using System.Security.Cryptography;
using Expenses.Application.Identity;
using Expenses.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Expenses.Laboratory;

public static class EntryPoint
{
    public static async Task Main(string[] args)
    {
        if (args.Contains("identity")) { await IdentityServer.RunAsync(); return; }
        await using var factory = new LaboratoryFactory();
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, int.Parse(Required("LAB_API_PORT"))));
        factory.StartServer();
        var configured = factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Expenses");
        if (configured != Required("EXPENSES_TEST_CONNECTION_STRING") ||
            !new Npgsql.NpgsqlConnectionStringBuilder(configured).Database!.StartsWith("expenses_users_check_", StringComparison.Ordinal))
            throw new InvalidOperationException("Laboratory refuses an unowned database configuration.");
        try { await Task.Delay(Timeout.Infinite, factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped); }
        catch (OperationCanceledException) { }
    }
    public static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing laboratory setting: {name}");
}

internal sealed class LaboratoryFactory : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Laboratory");
        builder.UseContentRoot(EntryPoint.Required("LAB_API_ROOT"));
        builder.UseSetting("ConnectionStrings:Expenses", EntryPoint.Required("EXPENSES_TEST_CONNECTION_STRING"));
        builder.UseSetting("Identity:EmailHmacKey", EntryPoint.Required("LAB_HMAC"));
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(ProtectedController).Assembly);
            services.RemoveAll<IIdentityProvider>();
            services.RemoveAll<IAccessTokenValidator>();
            services.AddSingleton<IIdentityProvider, LocalIdentityClient>();
            services.AddSingleton<IAccessTokenValidator>(provider =>
            {
                var rsa = RSA.Create();
                rsa.ImportFromPem(File.ReadAllText(EntryPoint.Required("LAB_PUBLIC_KEY")));
                var metadata = new OpenIdConnectConfiguration { Issuer = IdentityServer.Issuer };
                metadata.SigningKeys.Add(new RsaSecurityKey(rsa) { KeyId = "local-only" });
                return new CognitoAccessTokenValidator(IdentityServer.Issuer, IdentityServer.ClientId,
                    provider.GetRequiredService<TimeProvider>(), new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata));
            });
            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(EntryPoint.Required("LAB_KEYRING")));
            services.AddSingleton<IStartupFilter, LaboratoryPipeline>();
        });
    }
}

internal sealed class LaboratoryPipeline : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            ForwardLimit = 1
        };
        // Kestrel is loopback-only; the private proxy is the sole forwarded-header trust boundary.
        forwarded.KnownProxies.Clear();
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Add(IPAddress.Loopback);
        app.UseForwardedHeaders(forwarded);
        app.UseSwagger(options => options.RouteTemplate = "test/openapi/{documentName}.json");
        app.Use(async (context, continuation) =>
        {
            context.Response.Headers["X-Lab-Instance"] = EntryPoint.Required("LAB_API_PORT");
            await continuation();
        });
        next(app);
    };
}

[ApiController, Route("test/session-protected"), Authorize]
public sealed class ProtectedController : ControllerBase
{
    [HttpGet] public IActionResult Get() => Ok(new { authenticated = true });
}
