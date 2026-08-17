using System.Collections.Concurrent;
using CitusManager.Contracts;

namespace CitusManager.Services;

public interface IClusterTopologyCache
{
    bool TryGet(Guid clusterId, out ClusterInventoryResponse inventory);
    void Set(Guid clusterId, ClusterInventoryResponse inventory);
    void Remove(Guid clusterId);
}

public sealed class ClusterTopologyCache(IConfiguration configuration) : IClusterTopologyCache
{
    private sealed record Entry(ClusterInventoryResponse Inventory, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<Guid, Entry> entries = new();
    private readonly TimeSpan lifetime = TimeSpan.FromSeconds(Math.Clamp(
        configuration.GetValue("Monitoring:TopologyCacheSeconds", 30), 5, 300));

    public bool TryGet(Guid clusterId, out ClusterInventoryResponse inventory)
    {
        if (entries.TryGetValue(clusterId, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            inventory = entry.Inventory;
            return true;
        }

        entries.TryRemove(clusterId, out _);
        inventory = null!;
        return false;
    }

    public void Set(Guid clusterId, ClusterInventoryResponse inventory) =>
        entries[clusterId] = new(inventory, DateTimeOffset.UtcNow.Add(lifetime));

    public void Remove(Guid clusterId) => entries.TryRemove(clusterId, out _);
}
