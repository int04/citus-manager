import { html, problem } from "./shared.js";

const t = (key, ...args) => window.CitusI18n?.t(key, ...args) ?? key;
const text = value => value == null || value === "" ? t("inspector.unconfigured") : String(value);
const bytes = value => value == null ? t("inspector.unavailable") : (window.CitusI18n?.number(value, { notation: "compact", maximumFractionDigits: 2 }) ?? value) + " B";
const badge = (label, tone = "") => `<span class="database-row-inspector-badge ${tone}">${html(label)}</span>`;
const field = (label, value) => `<div><dt>${html(label)}</dt><dd>${html(text(value))}</dd></div>`;

export function createRowInspector({ explorer, token, showError }) {
  let active = null;

  function close() {
    if (!active) return;
    const current = active;
    active = null;
    current.controller.abort();
    current.modal.remove();
    document.body.classList.remove("database-modal-open");
    current.trigger?.focus();
  }

  async function copy(value, button) {
    try {
      await navigator.clipboard.writeText(value ?? "");
      const previous = button.innerHTML;
      button.innerHTML = `<i class="fa fa-check" aria-hidden="true"></i> ${t("inspector.copied")}`;
      setTimeout(() => { if (button.isConnected) button.innerHTML = previous; }, 1200);
    } catch { showError(t("inspector.copyFailed")); }
  }

  function snapshotValues(context) {
    return context.columns.map((column, index) => ({
      name: column.name,
      dataType: column.dataType,
      value: context.values[index] == null ? null : String(context.values[index]),
      isNull: context.values[index] == null,
      isTruncated: Boolean(context.truncated?.[index])
    }));
  }

  function valuesHtml(values) {
    return `<section class="database-row-inspector-section"><div class="database-row-inspector-section-title"><div><i class="fa fa-list-alt" aria-hidden="true"></i><h3>${t("inspector.rowValues")}</h3><span>${values.length}</span></div><button type="button" data-copy-json><i class="fa fa-files-o" aria-hidden="true"></i> ${t("inspector.copyJson")}</button></div>
      <div class="database-row-inspector-table-wrap"><table><thead><tr><th>${t("inspector.column")}</th><th>PostgreSQL type</th><th>${t("inspector.value")}</th><th></th></tr></thead><tbody>${values.map((item, index) => `<tr><td><b>${html(item.name)}</b></td><td><code>${html(item.dataType)}</code></td><td class="database-row-inspector-value">${item.isNull ? '<em>NULL</em>' : `<pre>${html(item.value)}</pre>${item.isTruncated ? badge(t("inspector.truncated"), "is-warning") : ""}`}</td><td><button type="button" data-copy-value="${index}" aria-label="${t("common.copy")} ${html(item.name)}"><i class="fa fa-copy" aria-hidden="true"></i></button></td></tr>`).join("")}</tbody></table></div></section>`;
  }

  function partitionsHtml(items) {
    return `<section class="database-row-inspector-section"><div class="database-row-inspector-section-title"><div><i class="fa fa-sitemap" aria-hidden="true"></i><h3>Partition</h3></div></div>${items.length ? `<ol class="database-row-inspector-chain">${items.map((item, index) => `<li><span>${index + 1}</span><div><b>${html(item.schema)}.${html(item.name)}</b><small>${html(item.strategy || (item.isLeaf ? "LEAF" : "PARENT"))}${item.isDefault ? " · DEFAULT" : ""}</small><p>${html(item.keyDefinition || item.bound || t("inspector.noPartitionExpression"))}</p><p>${html(item.bound || "")}</p></div><strong>${html(bytes(item.totalBytes))}</strong></li>`).join("")}</ol>` : `<p class="database-row-inspector-empty">${t("inspector.noPartition")}</p>`}</section>`;
  }

  function shardHtml(data) {
    const shard = data.shard;
    return `<section class="database-row-inspector-section"><div class="database-row-inspector-section-title"><div><i class="fa fa-cubes" aria-hidden="true"></i><h3>Distribution & shard</h3></div></div>
      <dl class="database-row-inspector-fields">${field("Table mode", data.tableMode)}${field("Distribution", data.distributionMethod)}${field("Distribution column", data.distributionColumn)}${field("Distribution value", data.distributionValue)}${field("Colocation ID", data.colocationId)}${field("Replication model", data.replicationModel)}${field("Shard", shard?.shardId)}${field("Resolution", shard?.status || "Unavailable")}</dl>
      ${shard?.candidateShardIds?.length ? `<details><summary>Candidate shards (${shard.candidateShardIds.length})</summary><p class="database-row-inspector-candidates">${shard.candidateShardIds.map(id => badge(id)).join("")}</p></details>` : ""}</section>`;
  }

  function placementsHtml(placements) {
    return `<section class="database-row-inspector-section"><div class="database-row-inspector-section-title"><div><i class="fa fa-server" aria-hidden="true"></i><h3>${t("inspector.placements")}</h3><span>${placements.length}</span></div></div>${placements.length ? `<div class="database-row-inspector-placements">${placements.map(item => `<article><header><div><i class="fa fa-server" aria-hidden="true"></i><b>${html(item.host)}:${item.port}</b></div>${badge(item.placementState || t("inspector.unavailable"), item.isActive ? "is-ok" : "is-warning")}</header><dl>${field("Shard / placement", `${text(item.shardId)} / ${text(item.placementId)}`)}${field("Node / group", `${text(item.nodeId)} / ${text(item.groupId)}`)}${field(t("inspector.role"), item.role)}${field(t("inspector.physicalRelation"), item.physicalRelation)}${field(t("inspector.shardSize"), bytes(item.shardBytes))}${field(t("inspector.regionRack"), item.rack)}${field(t("inspector.zoneCluster"), item.nodeCluster)}${field("Metadata", item.hasMetadata ? item.metadataSynced ? t("inspector.synced") : t("inspector.notSynced") : t("inspector.noMetadata"))}${field(t("inspector.shardEligible"), item.shouldHaveShards ? t("common.yes") : t("common.no"))}</dl></article>`).join("")}</div>` : `<p class="database-row-inspector-empty">${t("inspector.noPlacement")}</p>`}</section>`;
  }

  function internalsHtml(item) {
    return `<details class="database-row-inspector-advanced"><summary><i class="fa fa-database" aria-hidden="true"></i> ${t("inspector.internals")}</summary>${item ? `<dl class="database-row-inspector-fields">${field("tableoid", item.tableOid)}${field(t("inspector.physicalTable"), item.physicalTable)}${field("ctid", item.ctid)}${field("xmin", item.xmin)}${field("xmax", item.xmax)}${field(t("inspector.rowBytes"), item.rowBytes)}${field("Fingerprint", item.fingerprint)}</dl>` : `<p class="database-row-inspector-empty">${t("inspector.internalsUnavailable")}</p>`}</details>`;
  }

  function render(modal, data, context) {
    const values = data.values?.length ? data.values : snapshotValues(context);
    const warnings = [...(data.warnings || [])];
    if (context.unsaved) warnings.unshift(t("inspector.unsavedWarning"));
    modal.querySelector("[data-row-inspector-body]").innerHTML = `<div class="database-row-inspector-summary"><div><span>Database</span><b>${html(data.database || "—")}</b></div><i class="fa fa-angle-right"></i><div><span>Schema</span><b>${html(data.schema)}</b></div><i class="fa fa-angle-right"></i><div><span>Object</span><b>${html(data.objectName)}</b></div><div class="database-row-inspector-status">${badge(context.unsaved ? t("grid.unsaved") : data.rowResolved ? t("inspector.rowResolved") : t("inspector.metadataOnly"), context.unsaved || !data.rowResolved ? "is-warning" : "is-ok")}</div></div>
      ${warnings.length ? `<aside class="database-row-inspector-warnings"><i class="fa fa-exclamation-triangle"></i><div>${warnings.map(item => `<p>${html(item)}</p>`).join("")}</div></aside>` : ""}
      <section class="database-row-inspector-section"><div class="database-row-inspector-section-title"><div><i class="fa fa-info-circle"></i><h3>Logical object</h3></div></div><dl class="database-row-inspector-fields">${field("Target", data.targetLabel)}${field("Kind", data.objectKind)}${field("Persistence", data.persistence)}${field("Access method", data.accessMethod)}${field("Owner", data.owner)}${field("Tablespace", data.tablespace)}${field("Estimated rows", data.estimatedRows)}${field("Total size", bytes(data.totalBytes))}${field("Replica identity", data.replicaIdentity)}</dl>${data.resolutionReason ? `<p class="database-row-inspector-note">${html(data.resolutionReason)}</p>` : ""}</section>
      ${valuesHtml(values)}${partitionsHtml(data.partitions || [])}${shardHtml(data)}${placementsHtml(data.shard?.placements || [])}${internalsHtml(data.internals)}`;
    modal.querySelectorAll("[data-copy-value]").forEach(button => button.onclick = () => copy(values[Number(button.dataset.copyValue)]?.value, button));
    modal.querySelector("[data-copy-json]").onclick = event => copy(JSON.stringify(Object.fromEntries(values.map(item => [item.name, item.isNull ? null : item.value])), null, 2), event.currentTarget);
  }

  async function load(modal, context, controller) {
    const body = modal.querySelector("[data-row-inspector-body]");
    body.innerHTML = `<div class="database-row-inspector-loading"><div class="database-spinner"></div><b>${t("inspector.loading")}</b><span>Read-only · timeout 10s</span></div>`;
    try {
      const response = await fetch(explorer.dataset.workspaceInspectUrl, {
        method: "POST", headers: { "Content-Type": "application/json", "RequestVerificationToken": token },
        body: JSON.stringify({ schema: context.workspace.schema, objectName: context.workspace.name, nodeId: context.nodeId, identity: context.unsaved ? null : context.identity }), signal: controller.signal
      });
      if (!response.ok) throw new Error(await problem(response));
      if (!modal.isConnected) return;
      render(modal, await response.json(), context);
    } catch (error) {
      if (error.name === "AbortError" || !modal.isConnected) return;
      body.innerHTML = `<div class="database-row-inspector-error"><i class="fa fa-exclamation-circle"></i><h3>${t("inspector.loadFailed")}</h3><p>${html(error.message)}</p><button type="button" data-retry-inspection><i class="fa fa-refresh"></i> ${t("inspector.retry")}</button></div>`;
      body.querySelector("[data-retry-inspection]").onclick = () => load(modal, context, controller);
    }
  }

  function open(context, trigger) {
    close();
    const controller = new AbortController();
    const modal = document.createElement("div");
    modal.className = "database-modal database-row-inspector-modal";
    modal.setAttribute("role", "dialog"); modal.setAttribute("aria-modal", "true"); modal.setAttribute("aria-labelledby", "database-row-inspector-title");
    modal.innerHTML = `<div class="database-modal-card"><header><div><span>ROW INSPECTOR</span><h2 id="database-row-inspector-title"><i class="fa fa-info-circle"></i> ${html(context.workspace.schema)}.${html(context.workspace.name)} · row ${html(context.label)}</h2></div><button type="button" data-close-inspector aria-label="${t("common.close")}"><i class="fa fa-times"></i></button></header><main data-row-inspector-body></main><footer><span><i class="fa fa-lock"></i> Read-only diagnostics</span><button type="button" data-close-inspector>${t("common.close")}</button></footer></div>`;
    active = { modal, controller, trigger };
    document.body.appendChild(modal); document.body.classList.add("database-modal-open");
    modal.querySelectorAll("[data-close-inspector]").forEach(button => button.onclick = close);
    modal.onpointerdown = event => { if (event.target === modal) close(); };
    modal.onkeydown = event => {
      if (event.key === "Escape") { event.preventDefault(); close(); return; }
      if (event.key !== "Tab") return;
      const focusable = [...modal.querySelectorAll('button:not([disabled]),[href],input:not([disabled]),summary,[tabindex]:not([tabindex="-1"])')];
      if (!focusable.length) return;
      const first = focusable[0], last = focusable.at(-1);
      if (event.shiftKey && document.activeElement === first) { last.focus(); event.preventDefault(); }
      else if (!event.shiftKey && document.activeElement === last) { first.focus(); event.preventDefault(); }
    };
    modal.querySelector("[data-close-inspector]").focus();
    load(modal, context, controller);
  }

  return { openRowInspector: open, closeRowInspector: close };
}
