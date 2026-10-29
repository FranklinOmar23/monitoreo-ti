using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;

namespace MonitoreoTI.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class HealthEndpointTests(TestDatabase database)
{
    [DatabaseFact]
    public async Task Health_reports_healthy_when_sql_server_responds()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            TestHost.Configure(builder, database.ConnectionString);
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"status\":\"Healthy\"").And.Contain("\"name\":\"database\"");
    }
}
