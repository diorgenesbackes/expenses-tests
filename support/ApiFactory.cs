using Expenses.Domain.Repository;
using Expenses.Application.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Expenses.Test.Support;

internal sealed class ApiFactory(
    string environment = "Development",
    IUserRepository? repository = null,
    string? connectionString = null,
    IIdentityProvider? identityProvider = null,
    IRegistrationStore? registrationStore = null,
    IAccessTokenValidator? tokenValidator = null, TimeProvider? clock = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Logging:LogLevel:Default", "None");
        builder.UseSetting("ConnectionStrings:Expenses", connectionString ??
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        builder.UseSetting("Identity:EmailHmacKey", Convert.ToBase64String(new byte[32]));
        builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(SessionProtectedProbeController).Assembly);
            if (clock is not null) { services.RemoveAll<TimeProvider>(); services.AddSingleton(clock); }
            if (tokenValidator is not null) { services.RemoveAll<IAccessTokenValidator>(); services.AddSingleton(tokenValidator); }
            if (identityProvider is not null)
            {
                services.RemoveAll<IIdentityProvider>();
                services.AddSingleton(identityProvider);
            }
            if (registrationStore is not null)
            {
                services.RemoveAll<IRegistrationStore>();
                services.AddSingleton(registrationStore);
            }
            if (repository is not null)
            {
                services.RemoveAll<IUserRepository>();
                services.AddSingleton(repository);
            }
        });
    }

    public HttpClient CreateHttpsClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });
}
