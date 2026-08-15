using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CitusManager.Domain;
using CitusManager.Security;

namespace CitusManager.Services;

public interface IPrometheusCollector
{
    Task<IReadOnlyDictionary<string, double>> CollectAsync(
        ClusterProfile cluster, CancellationToken cancellationToken);
}

public sealed class PrometheusCollector(
    IHttpClientFactory clients,
    IClusterSecretProtector secrets) : IPrometheusCollector
{
    private static readonly IReadOnlyDictionary<string, string> Queries = new Dictionary<string, string>
    {
        ["prometheus.targets.up"] = "sum(up)",
        ["prometheus.targets.down"] = "sum(up == 0)",
        ["host.cpu.busy_cores"] = "sum(rate(node_cpu_seconds_total{mode!=\"idle\"}[5m]))",
        ["host.memory.available_bytes"] = "sum(node_memory_MemAvailable_bytes)",
        ["host.filesystem.available_bytes"] = "sum(node_filesystem_avail_bytes{fstype!~\"tmpfs|overlay\"})"
    };

    public async Task<IReadOnlyDictionary<string, double>> CollectAsync(
        ClusterProfile cluster, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(cluster.PrometheusBaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Prometheus URL is invalid.");

        var client = clients.CreateClient("prometheus");
        var result = new Dictionary<string, double>();
        foreach (var (name, query) in Queries)
        {
            var endpoint = new Uri(baseUri, $"/api/v1/query?query={Uri.EscapeDataString(query)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrWhiteSpace(cluster.ProtectedPrometheusToken))
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", secrets.Unprotect(cluster.ProtectedPrometheusToken));
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!string.Equals(document.RootElement.GetProperty("status").GetString(), "success", StringComparison.Ordinal))
                throw new InvalidOperationException("Prometheus query failed.");
            var values = document.RootElement.GetProperty("data").GetProperty("result");
            double total = 0;
            foreach (var series in values.EnumerateArray())
            {
                var value = series.GetProperty("value")[1].GetString();
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) total += parsed;
            }
            result[name] = total;
        }
        return result;
    }
}
