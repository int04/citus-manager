export function rowLocationPresentation(workspace, rowIndex) {
  if (!workspace.showRowLocations) return null;
  if (workspace.rowLocationsLoading) return { label: "Đang tải…", title: "Đang xác định worker", available: false };
  const location = workspace.rowLocations?.[rowIndex];
  if (!location) return { label: "Chưa xác định", title: "Bấm icon server ở head # để tải lại", available: false };
  const servers = [...new Set((location.placements || []).map(item => `${item.host}:${item.port}`))];
  if (!location.resolved || !servers.length)
    return { label: "Không xác định", title: location.status || "Không có worker metadata", available: false };
  return {
    label: `${servers[0]}${servers.length > 1 ? ` +${servers.length - 1}` : ""}`,
    title: `${location.status}${location.shardId == null ? "" : ` · shard ${location.shardId}`} · ${servers.join(", ")}`,
    available: true
  };
}

export function createRowLocationLoader({ explorer, jsonApi, nodeId, render, reportError }) {
  function invalidate(workspace) {
    workspace.locationAbort?.abort();
    workspace.locationAbort = null;
    workspace.rowLocations = null;
    workspace.rowLocationsLoading = false;
  }

  async function load(workspace) {
    if (!workspace.rows?.length) return;
    workspace.locationAbort?.abort();
    const controller = new AbortController();
    workspace.locationAbort = controller;
    workspace.showRowLocations = true;
    workspace.rowLocationsLoading = true;
    render(workspace);
    try {
      const response = await jsonApi(explorer.dataset.workspaceLocationsUrl, {
        schema: workspace.schema,
        objectName: workspace.name,
        nodeId: nodeId ? Number(nodeId) : null,
        identities: workspace.rows.map(row => row.identity || null)
      }, controller.signal);
      if (workspace.locationAbort !== controller) return;
      workspace.rowLocations = Object.fromEntries((response.locations || []).map(item => [item.rowIndex, item]));
    } catch (error) {
      if (error.name !== "AbortError") reportError(error);
    } finally {
      if (workspace.locationAbort === controller) {
        workspace.locationAbort = null;
        workspace.rowLocationsLoading = false;
        render(workspace);
      }
    }
  }

  return { loadRowLocations: load, invalidateRowLocations: invalidate };
}
