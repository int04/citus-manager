using System.Net;
using CitusManager.Contracts;
using CitusManager.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CitusManager.Tests;

public sealed class ApplicationUpdateReleaseTests
{
    [Fact]
    public async Task Release_check_uses_cache_and_refresh_bypasses_it()
    {
        var handler = new RegistryHandler("{\"tags\":[\"latest\",\"26.08.18.1028\",\"26.08.19.0900\"]}");
        var service = Create(handler);

        var first = await service.GetAsync(false, CancellationToken.None);
        var cached = await service.GetAsync(false, CancellationToken.None);
        var refreshed = await service.GetAsync(true, CancellationToken.None);

        Assert.Equal(ApplicationUpdateState.Available, first.State);
        Assert.Equal("26.08.19.0900", first.LatestVersion);
        Assert.Equal(first, cached);
        Assert.Equal(2, handler.TagRequests);
        Assert.Equal(ApplicationUpdateState.Available, refreshed.State);
    }

    [Fact]
    public async Task Malformed_registry_response_is_reported_as_unavailable()
    {
        var service = Create(new RegistryHandler("not-json"));

        var response = await service.GetAsync(false, CancellationToken.None);

        Assert.Equal(ApplicationUpdateState.Unavailable, response.State);
        Assert.Null(response.LatestVersion);
    }

    [Fact]
    public void Update_gate_recovers_from_shared_files_after_process_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"citus-manager-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var service = Create(new RegistryHandler("{}"), new Dictionary<string, string?>
            {
                ["Updates:StatePath"] = directory,
                ["Updates:ExecutionEnabled"] = "true"
            });
            Assert.False(service.IsClosed);

            File.WriteAllText(Path.Combine(directory, "request.json"),
                "{\"requestId\":\"98e344c6-c46a-4a1a-b400-b3a8f3d1f757\",\"targetVersion\":\"26.08.19.0900\"}");
            Assert.True(service.IsClosed);

            File.Delete(Path.Combine(directory, "request.json"));
            File.WriteAllText(Path.Combine(directory, "status.json"),
                "{\"requestId\":\"98e344c6-c46a-4a1a-b400-b3a8f3d1f757\",\"targetVersion\":\"26.08.19.0900\",\"state\":\"Restarting\",\"updatedAtUtc\":\"2026-08-19T09:00:00Z\"}");
            Assert.True(service.IsClosed);

            File.WriteAllText(Path.Combine(directory, "status.json"),
                "{\"requestId\":\"98e344c6-c46a-4a1a-b400-b3a8f3d1f757\",\"targetVersion\":\"26.08.19.0900\",\"state\":\"Succeeded\",\"updatedAtUtc\":\"2026-08-19T09:01:00Z\"}");
            Assert.False(service.IsClosed);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Execution_requires_a_current_compatible_updater_heartbeat()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"citus-manager-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var service = Create(new RegistryHandler("{\"tags\":[\"26.08.19.0900\"]}"), new Dictionary<string, string?>
            {
                ["Updates:StatePath"] = directory,
                ["Updates:ExecutionEnabled"] = "true"
            });
            Assert.False((await service.GetAsync(false, CancellationToken.None)).ExecutionAvailable);

            WriteHeartbeat(directory, 1, 1, DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.False((await service.GetAsync(false, CancellationToken.None)).ExecutionAvailable);

            WriteHeartbeat(directory, 2, 1, DateTimeOffset.UtcNow);
            Assert.False((await service.GetAsync(false, CancellationToken.None)).ExecutionAvailable);

            WriteHeartbeat(directory, 1, 1, DateTimeOffset.UtcNow);
            Assert.True((await service.GetAsync(false, CancellationToken.None)).ExecutionAvailable);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void WriteHeartbeat(string directory, int protocol, int generation, DateTimeOffset timestamp) =>
        File.WriteAllText(Path.Combine(directory, "updater-heartbeat.json"),
            $"{{\"protocol\":{protocol},\"composeGeneration\":{generation},\"updatedAtUtc\":\"{timestamp:O}\"}}");

    private static ApplicationUpdateService Create(RegistryHandler handler,
        IDictionary<string, string?>? settings = null) => new(
        new ClientFactory(new HttpClient(handler)), null!, new QueryConsoleExecutionRegistry(),
        new FixedVersionProvider(), new ConfigurationBuilder().AddInMemoryCollection(settings ??
            new Dictionary<string, string?>()).Build(), TimeProvider.System,
        NullLogger<ApplicationUpdateService>.Instance);

    private sealed class FixedVersionProvider : IApplicationVersionProvider
    {
        public string CurrentVersion => "26.08.18.1028";
        public string DisplayVersion => "v26.08.18.1028";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RegistryHandler(string tagsJson) : HttpMessageHandler
    {
        public int TagRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/token")
                return Json("{\"token\":\"test-token\"}");
            TagRequests++;
            return Json(tagsJson);
        }

        private static Task<HttpResponseMessage> Json(string value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json")
        });
    }
}
