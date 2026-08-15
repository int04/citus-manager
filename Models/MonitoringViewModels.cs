using CitusManager.Contracts;
using CitusManager.Domain;

namespace CitusManager.Models;

public sealed record MetricsViewModel(ClusterResponse Cluster, IReadOnlyList<MetricSample> Samples);
