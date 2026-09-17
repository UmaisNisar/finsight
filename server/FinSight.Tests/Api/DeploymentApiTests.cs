using System.Net;
using FinSight.Api.Hosting;
using FinSight.Tests.TestHelpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FinSight.Tests.Api;

public sealed class HealthEndpointTests(ProductionApiFactory factory) : IClassFixture<ProductionApiFactory>
{
    [Theory]
    [InlineData(HealthEndpoints.LivePath)]
    [InlineData(HealthEndpoints.ReadyPath)]
    public async Task Health_probes_are_anonymous_uncached_and_reveal_nothing(string path)
    {
        var response = await factory.CreateSessionClient(withCsrfHeader: false).GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("Healthy");
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Health_probes_are_not_rate_limited()
    {
        var client = factory.CreateSessionClient(withCsrfHeader: false);

        // The global limit is 600 requests a minute per client address.
        for (var i = 0; i < 650; i++)
        {
            (await client.GetAsync(HealthEndpoints.LivePath)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}

/// <summary>The API with a database it can't reach: readiness must fail while liveness still answers.</summary>
public sealed class UnreachableDatabaseApiFactory : FinSightApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Database:MigrateOnStartup", "false");

        // A directory can't be opened as a SQLite database file.
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:FinSight", $"Data Source={Path.GetTempPath()};Mode=ReadOnly");
    }

    // FinSight's own background services would fail against this database and stop the host; the web host itself stays.
    protected override void ConfigureTestServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Namespace?.StartsWith("FinSight", StringComparison.Ordinal) == true).ToList())
        {
            services.Remove(descriptor);
        }
    }
}

public sealed class UnreachableDatabaseHealthTests(UnreachableDatabaseApiFactory factory) : IClassFixture<UnreachableDatabaseApiFactory>
{
    [Fact]
    public async Task Readiness_fails_without_details_when_the_database_is_unreachable()
    {
        var client = factory.CreateSessionClient(withCsrfHeader: false);

        (await client.GetAsync(HealthEndpoints.LivePath)).StatusCode.Should().Be(HttpStatusCode.OK);

        var ready = await client.GetAsync(HealthEndpoints.ReadyPath);
        ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ready.Content.ReadAsStringAsync()).Should().Be("Unhealthy");
    }
}

public sealed class KeyRingProtectionTests
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "FinSight.Api";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Production_on_linux_without_a_certificate_refuses_to_start()
    {
        var act = () => DataProtectionSetup.ResolveKeyRingProtection(Config(), new Environment(Environments.Production), isWindows: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:CertificatePath*DataProtection:AllowUnprotectedKeys*");
    }

    [Fact]
    public void An_unprotected_key_ring_needs_an_explicit_opt_out_outside_development()
    {
        DataProtectionSetup.ResolveKeyRingProtection(Config(("DataProtection:AllowUnprotectedKeys", "true")), new Environment(Environments.Production), isWindows: false)
            .Should().Be(KeyRingProtection.None);
        DataProtectionSetup.ResolveKeyRingProtection(Config(), new Environment(Environments.Development), isWindows: false)
            .Should().Be(KeyRingProtection.None);
    }

    [Fact]
    public void A_certificate_wins_and_windows_falls_back_to_dpapi()
    {
        DataProtectionSetup.ResolveKeyRingProtection(Config(("DataProtection:CertificatePath", "/run/secrets/keyring.pfx")), new Environment(Environments.Production), isWindows: false)
            .Should().Be(KeyRingProtection.Certificate);
        DataProtectionSetup.ResolveKeyRingProtection(Config(), new Environment(Environments.Production), isWindows: true)
            .Should().Be(KeyRingProtection.Dpapi);
    }
}
