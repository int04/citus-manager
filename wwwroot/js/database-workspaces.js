import { createJsonApi, html, problem } from "./database-workspaces/shared.js";
import { attachExpandedEditorButton } from "./database-workspaces/cell-editor.js";
import { bindWorkspaceTabInteractions } from "./database-workspaces/tabs.js";
import { createCsvActions } from "./database-workspaces/csv.js";
import { createSpecialWorkspaceRenderers } from "./database-workspaces/special-workspaces.js";
import { createRowInspector } from "./database-workspaces/row-inspector.js";
import { createRowLocationLoader, rowLocationPresentation } from "./database-workspaces/row-locations.js";
import { cycleGridSort, gridSelectionStatistics, gridSortState, normalizeColumnOrder, orderedColumnEntries, reorderGridColumn, selectGridRange } from "./database-workspaces/data-grid-core.js";

(() => {
  const t = (key, ...args) => window.CitusI18n?.t(key, ...args) ?? key;
  const number = value => window.CitusI18n?.number(value) ?? Number(value).toLocaleString();
  const explorer = document.querySelector("[data-database-explorer]");
  if (!explorer) return;
  const tabs = document.getElementById("database-workspace-tabs");
  const stage = document.getElementById("database-workspace-content");
  const empty = document.getElementById("database-workspace-empty");
  const footerPath = document.querySelector(".database-workspace-path");
  const statistics = document.getElementById("database-workspace-statistics");
  const feedback = document.getElementById("database-feedback");
  const token = document.querySelector("#database-antiforgery input[name='__RequestVerificationToken']")?.value || "";
  const nodeId = explorer.dataset.nodeId || null;
  const clusterKey = location.pathname.toLowerCase();
  const storageKey = `cm-workspaces:v2:${explorer.dataset.workspaceUser || "anonymous"}:${clusterKey}:${nodeId || "coordinator"}`;
  const workspaces = new Map();
  let activeKey = null;
  let consoleSequence = 0;
  const defaultPageSize = 20;
  const defaultRowHeight = 36;
  const pageSizeOptions = [5, 10, 15, 20, 25, 50, 100, 200, 500];
  const columnCommentTooltip = document.createElement("div");
  columnCommentTooltip.id = "database-column-comment-tooltip";
  columnCommentTooltip.className = "database-column-comment-tooltip hidden";
  columnCommentTooltip.role = "tooltip";
  document.body.appendChild(columnCommentTooltip);
  let activeColumnCommentButton = null;

  function closeColumnComment() {
    activeColumnCommentButton?.setAttribute("aria-expanded", "false");
    activeColumnCommentButton = null;
    columnCommentTooltip.classList.add("hidden");
  }
  function toggleColumnComment(button) {
    if (activeColumnCommentButton === button) { closeColumnComment(); return; }
    closeColumnComment();
    activeColumnCommentButton = button;
    button.setAttribute("aria-expanded", "true");
    columnCommentTooltip.textContent = button.dataset.columnComment || "";
    columnCommentTooltip.classList.remove("hidden");
    columnCommentTooltip.style.visibility = "hidden";
    const anchor = button.getBoundingClientRect();
    const tooltip = columnCommentTooltip.getBoundingClientRect();
    const left = Math.max(8, Math.min(innerWidth - tooltip.width - 8, anchor.left + anchor.width / 2 - tooltip.width / 2));
    const below = anchor.bottom + 8;
    const top = below + tooltip.height <= innerHeight - 8 ? below : Math.max(8, anchor.top - tooltip.height - 8);
    columnCommentTooltip.style.left = `${left}px`;
    columnCommentTooltip.style.top = `${top}px`;
    columnCommentTooltip.style.visibility = "visible";
  }

  const jsonApi = createJsonApi(token);
  const showError = message => window.CitusConnectionResult.showError(feedback, message);
  const reportError = error => { if (error?.name !== "AbortError") showError(error?.message || String(error)); };
  const clearError = () => window.CitusConnectionResult.clear(feedback);
  const { openRowInspector } = createRowInspector({ explorer, token, showError });
  const { loadRowLocations, invalidateRowLocations } = createRowLocationLoader({ explorer, jsonApi, nodeId, render: ws => { if (activeKey === ws.key) renderDataWorkspace(ws); }, reportError });
  const { exportCsv, previewCsvImport } = createCsvActions({ stage, explorer, token, showError, loadRows });
  const { renderChartWorkspace, renderSqlWorkspace } = createSpecialWorkspaceRenderers({ stage, explorer, token, nodeId, updateFooter, showError });
  const keyOf = (schema, name, type = "data") => `${nodeId || "coordinator"}:${schema}.${name}:${type}`;
  const icon = type => type === "sql" ? "⌘" : type === "structure" ? "▦" : type === "ddl" ? "DDL" : type === "chart" ? "⌁" : "▤";

  function persist() {
    const safe = [...workspaces.values()].filter(x => !x.dirty || x.type === "sql").map(x => ({ key: x.key, type: x.type, schema: x.schema, name: x.name, displayName: x.displayName,
      page: x.page, pageSize: x.pageSize, where: x.where, orderBy: x.orderBy, widths: x.widths, rowHeights: x.rowHeights, rowNumberWidth: x.rowNumberWidth,
      hidden: x.hidden, columnOrder: x.columnOrder, scope: x.scope, editorPercent: x.editorPercent, historyPercent: x.historyPercent }));
    sessionStorage.setItem(storageKey, JSON.stringify({ activeKey, workspaces: safe }));
  }
  function ensureCapacity() {
    if (workspaces.size < 20) return true;
    const candidate = [...workspaces.values()].filter(x => !x.dirty && x.key !== activeKey).sort((a, b) => a.used - b.used)[0];
    if (!candidate) { showError(t("workspace.limit")); return false; }
    closeWorkspace(candidate.key, true); return true;
  }
  function renderTabs(revealKey = null) {
    const previousScrollLeft = tabs.scrollLeft;
    tabs.replaceChildren();
    workspaces.forEach(ws => {
      const button = document.createElement("button"); button.type = "button"; button.role = "tab";
      button.className = `database-workspace-tab${ws.key === activeKey ? " is-active" : ""}`;
      button.draggable = true;
      button.dataset.workspaceKey = ws.key; button.setAttribute("aria-selected", String(ws.key === activeKey));
      const mark = document.createElement("span"); mark.className = "database-workspace-tab-icon"; mark.textContent = icon(ws.type);
      const label = document.createElement("span"); const title = ws.displayName || ws.name; label.textContent = ws.type === "sql" ? title : `${title}${ws.type === "data" ? "" : ` · ${ws.type.toUpperCase()}`}`; button.title = t("workspace.tabTitle", label.textContent);
      const dirty = document.createElement("i"); dirty.textContent = ws.dirty ? "●" : "";
      const close = document.createElement("span"); close.className = "database-workspace-tab-close"; close.textContent = "×"; close.title = t("workspace.close");
      button.append(mark, label, dirty, close); tabs.appendChild(button);
    });
    tabs.scrollLeft = previousScrollLeft;
    if (revealKey) requestAnimationFrame(() => tabs.querySelector(`[data-workspace-key="${CSS.escape(revealKey)}"]`)?.scrollIntoView({ block: "nearest", inline: "nearest" }));
  }
  async function activate(key) {
    const ws = workspaces.get(key); if (!ws) return;
    const switchingWorkspace = activeKey !== null && activeKey !== key;
    activeKey = key; ws.used = Date.now(); renderTabs(key); empty.classList.add("hidden"); stage.classList.remove("hidden");
    if (!ws.loaded && ws.type !== "sql" && ws.type !== "chart") {
      showWorkspaceLoading();
      try { await hydrateWorkspace(ws); } catch (error) { showError(error.message); stage.innerHTML = `<div class="database-workspace-error">${html(error.message)}</div>`; }
    } else if (switchingWorkspace && ws.type === "data" && !ws.dirty) {
      renderWorkspace(ws);
      try { await loadRows(ws, t("workspace.reloadSwitch")); }
      catch (error) { reportError(error); }
    } else renderWorkspace(ws);
    persist();
  }
  async function hydrateWorkspace(ws) {
    if (ws.type === "data") {
      const url = new URL(explorer.dataset.workspaceMetadataUrl, location.origin);
      url.searchParams.set("schema", ws.schema); url.searchParams.set("name", ws.name); if (nodeId) url.searchParams.set("nodeId", nodeId);
      const response = await fetch(url); if (!response.ok) throw new Error(await problem(response)); ws.metadata = await response.json(); await loadRows(ws);
    } else if (ws.type === "structure") await loadStructure(ws);
    else if (ws.type === "ddl") await loadDdl(ws);
    ws.loaded = true;
  }
  function closeWorkspaces(keys, force = false) {
    const orderedKeys = [...workspaces.keys()], targets = [...new Set(keys)].filter(key => workspaces.has(key)); if (!targets.length) return;
    const dirtyCount = targets.filter(key => workspaces.get(key).dirty).length;
    if (dirtyCount && !force && !window.confirm(t("workspace.dirtyClose", dirtyCount))) return;
    const activeIndex = orderedKeys.indexOf(activeKey), activeWasClosed = targets.includes(activeKey);
    targets.forEach(key => { const ws = workspaces.get(key); clearInterval(ws.autoTimer); ws.results?.forEach(result=>{clearInterval(result.autoTimer);result.abort?.abort();}); ws.queryAbort?.abort(); ws.countAbort?.abort(); ws.sqlAbort?.abort(); ws.editor?.destroy?.(); workspaces.delete(key); });
    if (activeWasClosed) { const remaining = [...workspaces.keys()]; activeKey = remaining[Math.min(Math.max(activeIndex, 0), remaining.length - 1)] || null; }
    if (activeKey && activeWasClosed) activate(activeKey);
    else if (activeKey) { renderTabs(activeKey); persist(); }
    else { renderTabs(); stage.classList.add("hidden"); empty.classList.remove("hidden"); updateFooter(null); persist(); }
  }
  function closeWorkspace(key, force = false) { closeWorkspaces([key], force); }
  function duplicateWorkspace(key) {
    const source = workspaces.get(key); if (!source || !ensureCapacity()) return;
    const duplicateKey = `${source.key}:copy:${Date.now()}`, copyNumber = [...workspaces.values()].filter(ws => ws.key.startsWith(`${source.key}:copy:`)).length + 1;
    const duplicate = { ...source, key: duplicateKey, displayName: `${source.displayName || source.name} (copy${copyNumber > 1 ? ` ${copyNumber}` : ""})`, widths: {...(source.widths||{})}, rowHeights: {...(source.rowHeights||{})}, hidden: [...(source.hidden||[])], columnOrder: [...(source.columnOrder||[])], dirty: false, used: Date.now(), autoTimer: null, queryAbort: null, countAbort: null, sqlAbort: null };
    if (source.type === "data") Object.assign(duplicate, { rows: [], metadata: null, loaded: false, pending: new Map(), deleted: new Set(), inserted: [], selected: new Set(), autoRefresh: 0 });
    else if (source.type === "structure" || source.type === "ddl") Object.assign(duplicate, { loaded: false, html: null, ddl: null });
    else if (source.type === "chart") Object.assign(duplicate, { rows: [...source.rows], columns: [...source.columns], loaded: true });
    else Object.assign(duplicate, { loaded: true, editor: null, sqlAbort: null, output: [...(source.output || [])], results: [] });
    workspaces.set(duplicateKey, duplicate); activate(duplicateKey);
  }
  function reorderWorkspace(sourceKey, targetKey, after) {
    if (!sourceKey || sourceKey === targetKey || !workspaces.has(sourceKey) || !workspaces.has(targetKey)) return;
    const ordered = [...workspaces.entries()], source = ordered.find(([key]) => key === sourceKey), without = ordered.filter(([key]) => key !== sourceKey);
    let targetIndex = without.findIndex(([key]) => key === targetKey); if (after) targetIndex++;
    without.splice(targetIndex, 0, source); workspaces.clear(); without.forEach(([key, ws]) => workspaces.set(key, ws)); renderTabs(sourceKey); persist();
  }
  bindWorkspaceTabInteractions({ tabs, workspaces, getActiveKey: () => activeKey, activate, closeWorkspace, closeWorkspaces, duplicateWorkspace, reorderWorkspace });
  tabs.addEventListener("wheel", event => {
    if (tabs.scrollWidth <= tabs.clientWidth) return;
    const rawDelta = Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY;
    const unit = event.deltaMode === WheelEvent.DOM_DELTA_LINE ? 16
      : event.deltaMode === WheelEvent.DOM_DELTA_PAGE ? tabs.clientWidth : 1;
    const delta = rawDelta * unit;
    const maximum = tabs.scrollWidth - tabs.clientWidth;
    const next = Math.max(0, Math.min(maximum, tabs.scrollLeft + delta));
    if (next === tabs.scrollLeft) return;
    tabs.scrollLeft = next;
    event.preventDefault();
  }, { passive: false });
  document.addEventListener("pointerdown", event => {
    document.querySelectorAll(".database-query-suggestions:not(.hidden)").forEach(box => { if (!box.parentElement.contains(event.target)) box.classList.add("hidden"); });
    if (activeColumnCommentButton && !event.target.closest("[data-column-comment]") && !columnCommentTooltip.contains(event.target)) closeColumnComment();
  });
  document.addEventListener("keydown", event => { if (event.key === "Escape") closeColumnComment(); });
  document.addEventListener("scroll", closeColumnComment, true);
  window.addEventListener("resize", closeColumnComment);

  async function openObject(schema, name, type = "data") {
    const key = keyOf(schema, name, type); if (workspaces.has(key)) return activate(key); if (!ensureCapacity()) return;
    const ws = { key, type, schema, name, page: 1, pageSize: defaultPageSize, where: "", orderBy: "", widths: {}, rowHeights: {}, hidden: [], columnOrder: [], rows: [],
      metadata: null, dirty: false, pending: new Map(), deleted: new Set(), inserted: [], selected: new Set(), used: Date.now(), exactCount: null };
    workspaces.set(key, ws); activeKey = key; renderTabs(); empty.classList.add("hidden"); stage.classList.remove("hidden"); showWorkspaceLoading();
    try { await hydrateWorkspace(ws); }
    catch (error) { showError(error.message); stage.innerHTML = `<div class="database-workspace-error">${html(error.message)}</div>`; }
    persist();
  }
  function openQuery(scope = {}) {
    if (!ensureCapacity()) return; const id = ++consoleSequence, key = `sql:${Date.now()}:${id}`;
    const normalizedScope = { kind: scope.kind || "database", schema: scope.schema || null, objectName: scope.name || scope.objectName || null, nodeId: nodeId ? Number(nodeId) : null };
    const context = [normalizedScope.schema, normalizedScope.objectName].filter(Boolean).join(".");
    workspaces.set(key, { key, type: "sql", schema: normalizedScope.schema || "", name: `console ${id} · ${context || (nodeId ? "worker" : "database")}`, sql: "", scope: normalizedScope, dirty: false, loaded: true, used: Date.now() }); activate(key);
  }
  function showWorkspaceLoading() { stage.innerHTML = `<div class="database-loading"><div><div class="database-spinner"></div><p>${t("workspace.opening")}</p></div></div>`; }
  function setGridLoading(ws, loading, message = t("grid.loading")) {
    ws.rowsLoading = loading; ws.loadingMessage = message;
    if (activeKey !== ws.key) return;
    const overlay = stage.querySelector("[data-grid-loading]"), grid = stage.querySelector(".database-workspace-grid-scroll");
    overlay?.classList.toggle("hidden", !loading); if (overlay) overlay.querySelector("p").textContent = message;
    grid?.setAttribute("aria-busy", String(loading));
  }

  async function loadRows(ws, message = t("grid.loading")) {
    clearError(); ws.queryAbort?.abort(); invalidateRowLocations(ws); const controller = new AbortController(); let queryCompleted=false; ws.queryAbort = controller; setGridLoading(ws, true, message);
    try {
      const data = await jsonApi(explorer.dataset.workspaceQueryUrl, { schema: ws.schema, objectName: ws.name, nodeId: nodeId ? Number(nodeId) : null,
        page: ws.page, pageSize: ws.pageSize, where: ws.where || null, orderBy: ws.orderBy || null }, controller.signal);
      ws.rows = data.rows; ws.columns = data.columns; normalizeColumnOrder(ws); ws.hasPrevious = data.hasPrevious; ws.hasNext = data.hasNext; ws.estimatedRows = data.estimatedRows;
      const loadedEnd=ws.rows.length?(ws.page-1)*ws.pageSize+ws.rows.length:0,knownMinimum=loadedEnd+(ws.hasNext?1:0);
      ws.observedMinimum=Math.max(ws.observedMinimum||0,knownMinimum);ws.selected.clear();ws.activeRow=null;ws.loaded=true;queryCompleted=true;
    } finally {
      if (ws.queryAbort === controller) { ws.queryAbort = null; setGridLoading(ws, false, message); if(activeKey===ws.key&&ws.loaded)renderDataWorkspace(ws); if(queryCompleted&&ws.showRowLocations)loadRowLocations(ws); }
    }
  }
  async function loadStructure(ws) {
    const body = new URLSearchParams({ __RequestVerificationToken: token, Schema: ws.schema, Table: ws.name }); if (nodeId) body.set("NodeId", nodeId);
    const response = await fetch(explorer.dataset.structureUrl, { method: "POST", body }); if (!response.ok) throw new Error(await problem(response));
    ws.html = await response.text(); if(activeKey===ws.key)renderWorkspace(ws);
  }
  async function loadDdl(ws) {
    const url = new URL(explorer.dataset.workspaceDdlUrl, location.origin); url.searchParams.set("schema", ws.schema); url.searchParams.set("name", ws.name);
    const response = await fetch(url); if (!response.ok) throw new Error(await problem(response)); ws.ddl = (await response.json()).sql; if(activeKey===ws.key)renderWorkspace(ws);
  }
  function renderWorkspace(ws) {
    clearError();
    if (ws.type === "data") return ws.metadata && ws.rows ? renderDataWorkspace(ws) : showWorkspaceLoading();
    if (ws.type === "structure") stage.innerHTML = `<div class="database-simple-workspace">${ws.html || ""}</div>`;
    else if (ws.type === "ddl") stage.innerHTML = `<div class="database-ddl-workspace"><div class="database-grid-toolbar"><button data-copy-ddl>Copy</button><button data-ddl-console>Open copy in SQL Console</button><button data-download-ddl>Download</button></div><pre><code>${html(ws.ddl || "")}</code></pre></div>`;
    else if (ws.type === "sql") renderSqlWorkspace(ws);
    else if (ws.type === "chart") renderChartWorkspace(ws);
    updateFooter(ws);
  }
  function reorderColumn(ws, sourceName, targetName, after) {
    if (!reorderGridColumn(ws, sourceName, targetName, after)) return;
    persist();
    renderDataWorkspace(ws);
  }
  function dataToolbar(ws) {
    const start = ws.rows.length ? (ws.page - 1) * ws.pageSize + 1 : 0, end = start ? start + ws.rows.length - 1 : 0;
    const estimated=ws.estimatedRows==null?null:Math.max(Number(ws.estimatedRows),ws.observedMinimum||0),total=ws.exactCount??estimated;
    const totalLabel=total==null?"?":number(total),approximate=ws.exactCount==null?" ~":"";
    return `<div class="database-grid-toolbar">
      <button data-page="1" ${ws.hasPrevious ? "" : "disabled"} title="${t("grid.firstPage")}" aria-label="${t("grid.firstPage")}"><i class="fa fa-fast-backward" aria-hidden="true"></i></button><button data-page="${ws.page - 1}" ${ws.hasPrevious ? "" : "disabled"} title="${t("grid.previousPage")}" aria-label="${t("grid.previousPage")}"><i class="fa fa-chevron-left" aria-hidden="true"></i></button>
      <details class="database-toolbar-menu database-range-menu"><summary title="${t("grid.rowsPerPage")}"><i class="fa fa-list-ol" aria-hidden="true"></i><span>${start}–${end}</span></summary><div><p>${t("grid.pageRanges")}</p><div class="database-page-range-list">${pageRangeOptions(ws)}</div><hr><label class="database-page-size-select"><span>${t("grid.rowsPerPage")}</span><select data-page-size-select aria-label="${t("grid.rowsPerPage")}">${pageSizeOptions.map(size=>`<option value="${size}" ${ws.pageSize===size?"selected":""}>${size}</option>`).join("")}<option value="custom" ${pageSizeOptions.includes(ws.pageSize)?"":"selected"}>${t("grid.custom")}…</option></select></label><label class="database-custom-page-size ${pageSizeOptions.includes(ws.pageSize)?"hidden":""}"><span>${t("grid.custom")}</span><input data-custom-page-size type="number" min="1" max="500" step="1" placeholder="1–500" value="${pageSizeOptions.includes(ws.pageSize)?"":ws.pageSize}"><button type="button" data-apply-custom-page-size title="${t("grid.customApply")}" aria-label="${t("grid.customApply")}"><i class="fa fa-check" aria-hidden="true"></i></button></label></div></details>
      <button class="database-total-count" data-total-count ${nodeId?"disabled":""} title="${nodeId?t("grid.countCoordinator"):t("grid.exactCount")}"><i class="fa ${ws.counting?"fa-times":"fa-calculator"}" aria-hidden="true"></i><span>${ws.counting?t("grid.cancelCount"):`of ${totalLabel}${approximate}`}</span></button>
      <button data-page="${ws.page + 1}" ${ws.hasNext ? "" : "disabled"} title="${t("grid.nextPage")}" aria-label="${t("grid.nextPage")}"><i class="fa fa-chevron-right" aria-hidden="true"></i></button><button data-last-page ${ws.exactCount != null && ws.hasNext ? "" : "disabled"} title="${t("grid.lastPage")}" aria-label="${t("grid.lastPage")}"><i class="fa fa-fast-forward" aria-hidden="true"></i></button><button data-refresh title="${t("grid.reload")}" aria-label="${t("grid.reload")}"><i class="fa fa-refresh" aria-hidden="true"></i></button>
      <label title="${t("grid.autoRefresh")}"><i class="fa fa-clock-o" aria-hidden="true"></i><select data-auto-refresh aria-label="${t("grid.autoRefresh")}"><option value="0">${t("common.off")}</option><option value="5" ${ws.autoRefresh===5?"selected":""}>5s</option><option value="15" ${ws.autoRefresh===15?"selected":""}>15s</option><option value="30" ${ws.autoRefresh===30?"selected":""}>30s</option><option value="60" ${ws.autoRefresh===60?"selected":""}>60s</option></select></label>
      <span class="database-toolbar-separator"></span><button data-add ${ws.metadata.canEdit ? "" : "disabled"} title="${t("grid.addRow")}" aria-label="${t("grid.addRow")}"><i class="fa fa-plus" aria-hidden="true"></i></button><button data-delete ${ws.metadata.canEdit ? "" : "disabled"} title="${t("grid.deleteRows")}" aria-label="${t("grid.deleteRows")}"><i class="fa fa-minus" aria-hidden="true"></i></button>
      <button data-save ${ws.dirty ? "" : "disabled"} title="${t("common.save")}"><i class="fa fa-floppy-o" aria-hidden="true"></i><span>${t("common.save")}</span></button><button data-revert ${ws.dirty ? "" : "disabled"} title="${t("common.revert")}"><i class="fa fa-undo" aria-hidden="true"></i><span>${t("common.revert")}</span></button>
      <span class="database-toolbar-spacer"></span><details class="database-toolbar-menu"><summary><i class="fa fa-columns" aria-hidden="true"></i><span>Columns</span></summary><div class="database-column-menu">${orderedColumnEntries(ws).map(({c})=>`<label><input type="checkbox" data-column-visible="${html(c.name)}" ${ws.hidden.includes(c.name)?"":"checked"}> ${html(c.name)}</label>`).join("")}</div></details><details class="database-toolbar-menu"><summary><i class="fa fa-file-text-o" aria-hidden="true"></i><span>CSV</span></summary><div><button data-csv-page><i class="fa fa-download" aria-hidden="true"></i><span>Export page</span></button><button data-csv-all><i class="fa fa-cloud-download" aria-hidden="true"></i><span>Export all filter</span></button><button data-csv-import ${ws.metadata.canEdit ? "" : "disabled"}><i class="fa fa-upload" aria-hidden="true"></i><span>Import…</span></button></div></details><input class="hidden" data-csv-file type="file" accept=".csv,text/csv"><button data-open-ddl title="Open DDL"><i class="fa fa-code" aria-hidden="true"></i><span>DDL</span></button><button data-chart title="Create chart"><i class="fa fa-bar-chart" aria-hidden="true"></i><span>Chart</span></button>
    </div>`;
  }
  function pageRangeOptions(ws){const total=ws.exactCount??Math.max(Number(ws.estimatedRows)||0,ws.observedMinimum||0),exactLast=ws.exactCount==null?null:Math.max(1,Math.ceil(ws.exactCount/ws.pageSize)),knownLast=Math.max(exactLast??Math.ceil(total/ws.pageSize),ws.page+(ws.hasNext?1:0),1);let pages;if(knownLast<=500)pages=Array.from({length:knownLast},(_,index)=>index+1);else{const candidates=new Set([1,2,3,knownLast-2,knownLast-1,knownLast]);for(let page=Math.max(1,ws.page-100);page<=Math.min(knownLast,ws.page+100);page++)candidates.add(page);pages=[...candidates].sort((a,b)=>a-b);}return pages.map((page,index)=>{const from=(page-1)*ws.pageSize+1,to=exactLast?Math.min(page*ws.pageSize,ws.exactCount):page*ws.pageSize,previous=pages[index-1],gap=previous&&page-previous>1?'<span class="database-page-range-gap">…</span>':"";return `${gap}<button data-page="${page}" class="${page===ws.page?"is-active":""}" ${page===ws.page?"disabled":""}>${ws.exactCount===0?"0–0":`${from}–${to}`}</button>`;}).join("");}
  function rowNumberContent(ws, rowIndex, label, inspectAttribute) {
    const location = rowIndex == null ? (ws.showRowLocations ? { label: t("grid.unsaved"), title: t("grid.unsavedWorker"), available: false } : null) : rowLocationPresentation(ws, rowIndex);
    return `<span class="database-row-number-content ${location ? "has-location" : ""}"><span class="database-row-index">${html(label)}</span>${location ? `<span class="database-row-location ${location.available ? "is-available" : ""}" title="${html(location.title)}"><i class="fa fa-map-marker" aria-hidden="true"></i>${html(location.label)}</span>` : ""}<button type="button" ${inspectAttribute} aria-label="${html(t("grid.inspectRow", label))}" title="${t("grid.rowDetails")}"><i class="fa fa-info-circle" aria-hidden="true"></i></button></span>`;
  }
  function renderDataWorkspace(ws) {
    ws.rowHeights ||= {};
    const visible = orderedColumnEntries(ws).filter(x => !ws.hidden.includes(x.c.name));
    const defaultRowHeaderWidth = ws.showRowLocations ? 240 : 58;
    const rowHeaderWidth = Number.isFinite(ws.rowNumberWidth) ? Math.min(600, Math.max(42, ws.rowNumberWidth)) : defaultRowHeaderWidth;
    const tableWidth = rowHeaderWidth + visible.reduce((total, { c }) => total + (ws.widths[c.name] || 180), 0);
    stage.innerHTML = `<div class="database-data-workspace">${dataToolbar(ws)}
      <div class="database-query-strip"><label><b>WHERE</b><input data-where value="${html(ws.where)}" placeholder="tenant_id = 42" autocomplete="off"><div class="database-query-suggestions hidden"></div></label>
      <label><b>ORDER BY</b><input data-order value="${html(ws.orderBy)}" placeholder="created_at DESC" autocomplete="off"><div class="database-query-suggestions hidden"></div></label><button data-apply-filter>Apply</button></div>
      <div class="database-workspace-grid-shell"><div class="database-workspace-grid-scroll" aria-busy="${ws.rowsLoading ? "true" : "false"}"><table class="database-workspace-grid ${ws.showRowLocations ? "has-row-locations" : ""}" style="width:${tableWidth}px;--database-row-number-width:${rowHeaderWidth}px"><colgroup><col data-row-number-width style="width:${rowHeaderWidth}px">${visible.map(({c})=>`<col data-column-width="${html(c.name)}" style="width:${ws.widths[c.name]||180}px">`).join("")}</colgroup><thead><tr><th class="database-row-number"><span class="database-row-number-head"><span>#</span><button type="button" data-load-row-locations ${ws.rows.length&&!ws.rowLocationsLoading?"":"disabled"} aria-label="${ws.rowLocationsLoading?t("grid.loadingWorkers"):t("grid.loadWorkers")}" title="${t("grid.workerTitle")}"><i class="fa ${ws.rowLocationsLoading?"fa-spinner fa-spin":"fa-server"}" aria-hidden="true"></i></button></span><i class="database-column-resizer database-row-number-resizer" data-row-number-resizer title="${t("grid.resizeNumber")}"></i></th>${visible.map(({c}) => columnHeaderHtml(ws,c)).join("")}</tr></thead>
      <tbody>${ws.rows.map((row, ri) => {const heightKey=`page:${ws.page}:${ri}`,height=ws.rowHeights[heightKey]||defaultRowHeight,rowNumber=(ws.page-1)*ws.pageSize+ri+1;return `<tr data-row="${ri}" data-visual-row="${ri}" class="${ws.deleted.has(ri) ? "is-deleted " : ""}${ws.activeRow===ri?"is-active-row ":""}${height!==defaultRowHeight?"is-row-resized":""}" style="height:${height}px"><th class="database-row-number" data-select-row="${ri}">${rowNumberContent(ws,ri,rowNumber,`data-inspect-row="${ri}"`)}<i class="database-row-resizer" data-row-height-key="${heightKey}" title="${t("grid.resizeRow")}"></i></th>${visible.map(({c,i}) => cellHtml(ws,row,ri,c,i)).join("")}</tr>`;}).join("")}${ws.inserted.map((row, ii) => {const visualRow=ws.rows.length+ii,heightKey=`insert:${ii}`,height=ws.rowHeights[heightKey]||defaultRowHeight;return `<tr class="is-inserted ${ws.activeRow===visualRow?"is-active-row ":""}${height!==defaultRowHeight?"is-row-resized":""}" data-insert="${ii}" data-visual-row="${visualRow}" style="height:${height}px"><th class="database-row-number" data-select-row="${visualRow}">${rowNumberContent(ws,null,"+",`data-inspect-insert="${ii}"`)}<i class="database-row-resizer" data-row-height-key="${heightKey}" title="${t("grid.resizeRow")}"></i></th>${visible.map(({c,i}) => `<td tabindex="0" data-cell data-row="${visualRow}" data-col="${i}" data-insert-cell="${ii}" data-column="${html(c.name)}" class="${ws.selected.has(`${visualRow}:${i}`)?"is-selected":""}">${html(row[c.name] ?? "")}</td>`).join("")}</tr>`;}).join("")}</tbody></table></div><div class="database-grid-loading ${ws.rowsLoading ? "" : "hidden"}" data-grid-loading role="status" aria-live="polite"><div><div class="database-spinner"></div><p>${html(ws.loadingMessage || t("grid.loading"))}</p></div></div></div>
      ${ws.metadata.canEdit ? "" : `<div class="database-readonly-note">${html(t("grid.readOnly", ws.metadata.readOnlyReason || t("grid.noEdit")))}</div>`}</div>`;
    bindDataWorkspace(ws); updateFooter(ws);
  }
  function columnHeaderHtml(ws,column){const sort=sortState(ws,column.name),keyIcon='<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="8" cy="10" r="4"/><path d="m11 13 8 8m-3-3 2-2m-5-1 2-2"/></svg>';const badges=column.isPrimaryKey?`<i class="database-column-key is-primary" title="Primary key">${keyIcon}</i>`:column.isIndexed?`<i class="database-column-key is-indexed" title="${column.isUnique?t("grid.uniqueIndex"):t("grid.indexed")}">${keyIcon}</i>`:"";const required=!column.isNullable?'<i class="database-column-required" title="NOT NULL"></i>':"";const comment=column.comment?.trim()?`<button type="button" class="database-column-comment" data-column-comment="${html(column.comment)}" aria-label="${html(t("grid.columnComment", column.name))}" aria-controls="database-column-comment-tooltip" aria-expanded="false"><i class="fa fa-info-circle" aria-hidden="true"></i></button>`:"";const sortIcon=sort?`<i class="database-sort-indicator is-${sort.direction.toLowerCase()}" title="Sort ${sort.direction}">${sort.direction==="ASC"?"↑":"↓"}${sort.priority>1?`<sup>${sort.priority}</sup>`:""}</i>`:"";return `<th tabindex="0" draggable="true" data-column="${html(column.name)}" aria-label="${html(t("grid.columnLabel", column.name))}" title="${t("grid.columnTitle")}" style="width:${ws.widths[column.name]||180}px"><span class="database-column-title">${badges}${required}<b>${html(column.name)}</b>${comment}${sortIcon}</span><small>${html(column.dataType)}${column.isUnique&&!column.isPrimaryKey?" · UNIQUE":""}</small><i class="database-column-resizer" draggable="false"></i></th>`;}
  const sortState = gridSortState;
  function cellHtml(ws, row, ri, column, ci) {
    const pending = ws.pending.get(`${ri}:${column.name}`); const cell = row.cells[ci]; const value = pending ? pending.value : cell.value;
    const display=pending?.useDefault?'<span class="database-default">DEFAULT</span>':pending?.isNull||(!pending&&cell.isNull)?'<span class="database-null">NULL</span>':`<span>${html(value)}</span>`;
    return `<td tabindex="0" data-cell data-row="${ri}" data-col="${ci}" data-column="${html(column.name)}" data-truncated="${cell.isTruncated}" class="${pending ? "is-pending" : ""}${ws.selected.has(`${ri}:${ci}`) ? " is-selected" : ""}">${display}${cell.isTruncated&&!pending ? '<small>…</small>' : ""}</td>`;
  }
  function bindDataWorkspace(ws) {
    stage.querySelector("[data-load-row-locations]").onclick = () => loadRowLocations(ws);
    bindRowNumberResize(ws, stage.querySelector("[data-row-number-resizer]"));
    stage.querySelectorAll("[data-page]").forEach(b => b.onclick = () => { ws.page = Number(b.dataset.page); loadRows(ws, t("grid.pageChanging")).catch(reportError); });
    const pageSizeSelect=stage.querySelector("[data-page-size-select]"),customPageSize=stage.querySelector("[data-custom-page-size]"),customPageSizeRow=customPageSize.closest(".database-custom-page-size");pageSizeSelect.onchange=()=>{if(pageSizeSelect.value==="custom"){customPageSizeRow.classList.remove("hidden");customPageSize.focus();}else changePageSize(ws,Number(pageSizeSelect.value));};stage.querySelector("[data-apply-custom-page-size]").onclick=()=>changePageSize(ws,Number(customPageSize.value));customPageSize.onkeydown=event=>{if(event.key==="Enter"){event.preventDefault();changePageSize(ws,Number(customPageSize.value));}};
    stage.querySelector("[data-last-page]").onclick = () => { if(ws.exactCount != null){ws.page=Math.max(1,Math.ceil(ws.exactCount/ws.pageSize));loadRows(ws,t("grid.pageChanging")).catch(reportError);} };
    stage.querySelector("[data-refresh]").onclick = () => ws.dirty ? showError(t("grid.saveBeforeRefresh")) : loadRows(ws,t("grid.refreshing")).catch(reportError);
    stage.querySelector("[data-total-count]").onclick = () => ws.counting ? ws.countAbort?.abort() : countRows(ws);
    stage.querySelector("[data-auto-refresh]").onchange = e => setAutoRefresh(ws, Number(e.target.value));
    stage.querySelector("[data-apply-filter]").onclick = () => applyFilter(ws);
    stage.querySelectorAll(".database-query-strip input").forEach(input => bindSuggestions(ws, input));
    stage.querySelector("[data-add]").onclick = () => addRow(ws); stage.querySelector("[data-delete]").onclick = () => deleteRows(ws);
    stage.querySelector("[data-save]").onclick = () => saveRows(ws); stage.querySelector("[data-revert]").onclick = () => { ws.pending.clear(); ws.deleted.clear(); ws.inserted=[]; setDirty(ws,false); renderDataWorkspace(ws); };
    stage.querySelector("[data-open-ddl]").onclick = () => openObject(ws.schema, ws.name, "ddl"); stage.querySelector("[data-chart]").onclick = () => openChart(ws);
    stage.querySelector("[data-csv-page]").onclick = () => exportCsv(ws,true);stage.querySelector("[data-csv-all]").onclick = () => exportCsv(ws,false);
    stage.querySelectorAll("[data-column-visible]").forEach(input=>input.onchange=()=>{ws.hidden=input.checked?ws.hidden.filter(name=>name!==input.dataset.columnVisible):[...new Set([...ws.hidden,input.dataset.columnVisible])];persist();renderDataWorkspace(ws);});
    stage.querySelector("[data-csv-import]").onclick=()=>stage.querySelector("[data-csv-file]").click();stage.querySelector("[data-csv-file]").onchange=e=>{const file=e.target.files[0];if(file)previewCsvImport(ws,file);};
    stage.querySelectorAll("[data-column-comment]").forEach(button => { button.onclick = event => { event.stopPropagation(); toggleColumnComment(button); }; });
    const columnHeaders = [...stage.querySelectorAll("thead th[data-column]")];
    let draggedColumnName = null;
    const clearColumnDropState = () => columnHeaders.forEach(header => header.classList.remove("is-column-dragging", "is-column-drop-before", "is-column-drop-after"));
    columnHeaders.forEach(th => {
      th.addEventListener("pointerdown", event => {
        th.dataset.blockColumnDrag = String(Boolean(event.target.closest(".database-column-resizer,[data-column-comment]")));
      }, true);
      th.ondragstart = event => {
        if (th.dataset.blockColumnDrag === "true") { event.preventDefault(); return; }
        draggedColumnName = th.dataset.column;
        th.classList.add("is-column-dragging");
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", draggedColumnName);
      };
      th.ondragover = event => {
        if (!draggedColumnName || draggedColumnName === th.dataset.column) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
        columnHeaders.forEach(header => header.classList.remove("is-column-drop-before", "is-column-drop-after"));
        const after = event.clientX >= th.getBoundingClientRect().left + th.offsetWidth / 2;
        th.classList.add(after ? "is-column-drop-after" : "is-column-drop-before");
      };
      th.ondrop = event => {
        if (!draggedColumnName || draggedColumnName === th.dataset.column) return;
        event.preventDefault();
        const source = draggedColumnName;
        const after = event.clientX >= th.getBoundingClientRect().left + th.offsetWidth / 2;
        draggedColumnName = null;
        clearColumnDropState();
        reorderColumn(ws, source, th.dataset.column, after);
      };
      th.ondragend = () => { draggedColumnName = null; clearColumnDropState(); };
      th.onclick = event => {
        if(event.target.closest(".database-column-resizer,[data-column-comment]"))return;
        if(event.ctrlKey||event.metaKey){const ci=ws.columns.findIndex(c=>c.name===th.dataset.column);ws.selected.clear();ws.rows.forEach((_,ri)=>ws.selected.add(`${ri}:${ci}`));paintSelection(ws);}
        else sortColumn(ws, th.dataset.column, event.shiftKey);
      };
      th.onkeydown = event => {
        if (event.target.closest("[data-column-comment]")) return;
        if (event.altKey && (event.key === "ArrowLeft" || event.key === "ArrowRight")) {
          const visibleNames = orderedColumnEntries(ws).filter(entry => !ws.hidden.includes(entry.c.name)).map(entry => entry.c.name);
          const current = visibleNames.indexOf(th.dataset.column);
          const direction = event.key === "ArrowLeft" ? -1 : 1;
          const target = visibleNames[current + direction];
          if (target) reorderColumn(ws, th.dataset.column, target, direction > 0);
          event.preventDefault();
        } else if (event.key === "Enter" || event.key === " ") {
          sortColumn(ws, th.dataset.column, event.shiftKey);
          event.preventDefault();
        }
      };
      bindResize(ws, th);
    });
    stage.querySelectorAll("[data-cell]").forEach(cell => {
      cell.onpointerdown = event => beginCellSelection(ws, cell, event);
      cell.ondblclick = () => editCell(ws, cell); cell.onkeydown = e => { if (e.key === "F2" || e.key === "Enter") editCell(ws, cell); };
    });
    stage.querySelectorAll("[data-insert-cell]").forEach(cell => { cell.ondblclick=()=>editInsertedCell(ws,cell); cell.tabIndex=0; cell.onkeydown=e=>{if(e.key==="F2"||e.key==="Enter")editInsertedCell(ws,cell);}; });
    stage.querySelectorAll("[data-inspect-row]").forEach(button => button.onclick = event => { event.preventDefault();event.stopPropagation();const ri=Number(button.dataset.inspectRow),row=ws.rows[ri];openRowInspector({workspace:ws,nodeId:nodeId?Number(nodeId):null,label:(ws.page-1)*ws.pageSize+ri+1,identity:row.identity,columns:ws.columns,values:ws.columns.map((_,ci)=>workspaceCellValue(ws,ri,ci)),truncated:ws.columns.map((_,ci)=>Boolean(row.cells[ci]?.isTruncated)&&!ws.pending.has(`${ri}:${ws.columns[ci].name}`)),unsaved:false},button); });
    stage.querySelectorAll("[data-inspect-insert]").forEach(button => button.onclick = event => { event.preventDefault();event.stopPropagation();const ii=Number(button.dataset.inspectInsert),row=ws.inserted[ii];openRowInspector({workspace:ws,nodeId:nodeId?Number(nodeId):null,label:t("grid.unsaved"),identity:null,columns:ws.columns,values:ws.columns.map(column=>row[column.name]??null),truncated:[],unsaved:true},button); });
    stage.querySelectorAll(".database-row-resizer").forEach(grip=>bindRowResize(ws,grip));
    stage.querySelectorAll("[data-select-row]").forEach(head => head.onclick = event => { if(event.target.closest(".database-row-resizer,[data-inspect-row],[data-inspect-insert]"))return;const ri=Number(head.dataset.selectRow);ws.activeRow=ri;ws.columns.forEach((_,ci)=>ws.selected.add(`${ri}:${ci}`));renderDataWorkspace(ws); });
  }
  function applyFilter(ws) { const nextWhere=stage.querySelector("[data-where]").value.trim();ws.orderBy=stage.querySelector("[data-order]").value.trim();if(nextWhere!==ws.where){ws.exactCount=null;ws.observedMinimum=0;}ws.where=nextWhere;ws.page=1;loadRows(ws,t("grid.filtering")).catch(reportError);persist(); }
  function changePageSize(ws,size){if(!Number.isInteger(size)||size<1||size>500){showError(t("grid.pageSizeInvalid"));return;}ws.pageSize=size;ws.page=1;ws.observedMinimum=0;loadRows(ws,t("grid.loading")).catch(reportError);persist();}
  function bindSuggestions(ws, input) {
    const box = input.nextElementSibling;
    const values = [...ws.columns.map(column => column.name), "AND", "OR", "NOT", "NULL", "IS NULL", "IN ()", "LIKE", "ILIKE", "ASC", "DESC", "NULLS LAST", "count()", "lower()", "now()"];
    let part = "", active = -1;
    const choose = button => {
      input.value = input.value.slice(0, input.value.length - part.length) + button.textContent;
      box.classList.add("hidden");
      active = -1;
      input.focus();
    };
    input.oninput = () => {
      part = input.value.split(/[^\w.]+/).pop().toLowerCase();
      const matches = values.filter(value => value.toLowerCase().startsWith(part)).slice(0, 10);
      active = -1;
      box.innerHTML = matches.map(value => `<button type="button">${html(value)}</button>`).join("");
      box.classList.toggle("hidden", !matches.length);
      box.querySelectorAll("button").forEach(button => { button.onclick = () => choose(button); });
    };
    input.onblur = () => setTimeout(() => { if (!input.closest("label")?.contains(document.activeElement)) box.classList.add("hidden"); }, 0);
    input.onkeydown = event => {
      const buttons = [...box.querySelectorAll("button")];
      if (event.key === "Escape") { box.classList.add("hidden"); active = -1; return; }
      if (event.key === "Enter") {
        event.preventDefault();
        if (!box.classList.contains("hidden") && active >= 0 && buttons[active]) choose(buttons[active]);
        else { box.classList.add("hidden"); applyFilter(ws); }
        return;
      }
      if ((event.key === "ArrowDown" || event.key === "ArrowUp") && buttons.length) {
        event.preventDefault();
        active = (active + (event.key === "ArrowDown" ? 1 : -1) + buttons.length) % buttons.length;
        buttons.forEach((button, index) => button.classList.toggle("is-active", index === active));
        buttons[active].scrollIntoView({ block: "nearest" });
      }
    };
  }
  async function countRows(ws) { ws.countAbort=new AbortController();ws.counting=true;renderDataWorkspace(ws);try { const response=await fetch(explorer.dataset.workspaceCountUrl,{method:"POST",headers:{"Content-Type":"application/json","RequestVerificationToken":token},body:JSON.stringify({schema:ws.schema,objectName:ws.name,nodeId:nodeId?Number(nodeId):null,where:ws.where||null}),signal:ws.countAbort.signal});if(!response.ok)throw new Error(await problem(response));ws.exactCount=(await response.json()).count;}catch(e){if(e.name!=="AbortError")showError(e.message);}finally{ws.counting=false;ws.countAbort=null;renderDataWorkspace(ws);} }
  function setAutoRefresh(ws,seconds){clearInterval(ws.autoTimer);ws.autoRefresh=seconds;if(seconds>0)ws.autoTimer=setInterval(()=>{if(activeKey===ws.key&&!ws.dirty&&!stage.querySelector(".database-cell-editor"))loadRows(ws).catch(reportError);},seconds*1000);persist();}
  function sortColumn(ws,name,multi){cycleGridSort(ws,name,multi);ws.page=1;loadRows(ws,t("grid.sorting")).catch(reportError);}
  function bindResize(ws,th){ const grip=th.querySelector(".database-column-resizer"); grip.onpointerdown=e=>{e.stopPropagation();const start=e.clientX,width=th.offsetWidth,table=th.closest("table"),tableWidth=table.offsetWidth,column=table.querySelector(`col[data-column-width="${CSS.escape(th.dataset.column)}"]`);grip.setPointerCapture(e.pointerId);grip.onpointermove=m=>{const next=Math.max(32,width+m.clientX-start);th.style.width=`${next}px`;if(column)column.style.width=`${next}px`;table.style.width=`${tableWidth+next-width}px`;ws.widths[th.dataset.column]=next;};grip.onpointerup=()=>persist();};grip.ondblclick=e=>{e.stopPropagation();ws.widths[th.dataset.column]=Math.min(520,Math.max(48,th.scrollWidth+24));renderDataWorkspace(ws);};}
  function bindRowNumberResize(ws,grip){if(!grip)return;grip.onpointerdown=event=>{event.preventDefault();event.stopPropagation();const table=grip.closest("table"),column=table.querySelector("col[data-row-number-width]"),start=event.clientX,width=parseFloat(getComputedStyle(table).getPropertyValue("--database-row-number-width"))||58,tableWidth=table.offsetWidth;grip.setPointerCapture(event.pointerId);grip.onpointermove=move=>{const next=Math.min(600,Math.max(42,width+move.clientX-start));table.style.setProperty("--database-row-number-width",`${next}px`);if(column)column.style.width=`${next}px`;table.style.width=`${tableWidth+next-width}px`;ws.rowNumberWidth=next;};grip.onpointerup=()=>persist();};grip.ondblclick=event=>{event.preventDefault();event.stopPropagation();delete ws.rowNumberWidth;persist();renderDataWorkspace(ws);};}
  function bindRowResize(ws,grip){grip.onpointerdown=event=>{event.preventDefault();event.stopPropagation();const row=grip.closest("tr"),start=event.clientY,height=row.offsetHeight,key=grip.dataset.rowHeightKey;grip.setPointerCapture(event.pointerId);grip.onpointermove=move=>{const next=Math.min(600,Math.max(28,height+move.clientY-start));row.style.height=`${next}px`;row.classList.toggle("is-row-resized",next!==defaultRowHeight);ws.rowHeights[key]=next;};grip.onpointerup=()=>persist();};grip.ondblclick=event=>{event.preventDefault();event.stopPropagation();delete ws.rowHeights[grip.dataset.rowHeightKey];persist();renderDataWorkspace(ws);};}
  function paintSelection(ws){stage.querySelectorAll("[data-cell]").forEach(cell=>cell.classList.toggle("is-selected",ws.selected.has(`${cell.dataset.row}:${cell.dataset.col}`)));updateFooter(ws);}
  function paintActiveRow(ws){stage.querySelectorAll("tbody tr[data-visual-row]").forEach(row=>row.classList.toggle("is-active-row",Number(row.dataset.visualRow)===ws.activeRow));}
  function beginCellSelection(ws,cell,event){const startRow=Number(cell.dataset.row),startCol=Number(cell.dataset.col),additive=event.ctrlKey||event.metaKey;ws.activeRow=startRow;paintActiveRow(ws);selectGridRange(ws,startRow,startCol,startRow,startCol,additive);paintSelection(ws);const move=target=>{const current=target.closest?.("[data-cell]");if(!current)return;selectGridRange(ws,startRow,startCol,Number(current.dataset.row),Number(current.dataset.col),additive);paintSelection(ws);};const over=e=>{if(e.buttons===1)move(e.target);};const up=()=>{stage.removeEventListener("pointerover",over);document.removeEventListener("pointerup",up);};stage.addEventListener("pointerover",over);document.addEventListener("pointerup",up,{once:true});}
  async function editCell(ws,cell){
    const ci=Number(cell.dataset.col),col=ws.columns[ci];if(!ws.metadata.canEdit||!col.canEdit)return;const ri=Number(cell.dataset.row),key=`${ri}:${col.name}`,pending=ws.pending.get(key),originalCell=ws.rows[ri].cells[ci];let originalValue=originalCell.value??"",current=pending?.value??originalValue;
    if(!pending&&cell.dataset.truncated==="true"){try{const full=await jsonApi(explorer.dataset.workspaceCellUrl,{schema:ws.schema,objectName:ws.name,column:col.name,identity:ws.rows[ri].identity});originalValue=full.value??"";current=originalValue;}catch(e){showError(e.message);return;}}
    cell.innerHTML="";const container=document.createElement("div");container.className="database-cell-editor-shell";let input;if(/^bool/i.test(col.dataType)){input=document.createElement("select");["true","false"].forEach(value=>{const option=document.createElement("option");option.value=value;option.textContent=value;input.appendChild(option);});}else{input=document.createElement(/json|text|char/i.test(col.dataType)?"textarea":"input");if(col.isNumeric)input.type="number";}input.className="database-cell-editor";input.value=current;container.appendChild(input);cell.appendChild(container);
    let done=false,touched=false,isExpanded=()=>false;input.oninput=()=>{touched=true;};input.onchange=()=>{touched=true;};const finish=(save,mode="value",nextValue=input.value)=>{if(done)return;done=true;if(save){const untouched=mode==="value"&&!pending&&!touched,unchanged=untouched||mode==="value"&&!originalCell.isNull&&nextValue===originalValue||mode==="null"&&originalCell.isNull;if(unchanged)ws.pending.delete(key);else ws.pending.set(key,{column:col.name,value:mode==="value"?nextValue:null,isNull:mode==="null",useDefault:mode==="default"});syncDirty(ws);}renderDataWorkspace(ws);};
    isExpanded=attachExpandedEditorButton({workspace:ws,column:col,input,container,allowModes:true,onApply:(value,mode)=>{touched=true;input.value=value;finish(true,mode,value);}});input.focus();input.select?.();input.onkeydown=e=>{if(e.ctrlKey&&e.key==="0"){e.preventDefault();finish(true,"null");}else if(e.ctrlKey&&e.key.toLowerCase()==="d"){e.preventDefault();finish(true,"default");}else if(e.key==="Enter"&&!e.shiftKey){e.preventDefault();finish(true);}else if(e.key==="Escape")finish(false);};input.onblur=()=>{if(!isExpanded())finish(true);};
  }
  function setDirty(ws,value){ws.dirty=value;renderTabs();persist();}
  function syncDirty(ws){setDirty(ws,ws.pending.size>0||ws.deleted.size>0||ws.inserted.length>0);}
  function addRow(ws){ if(!ws.metadata.canEdit)return;const row={};ws.columns.filter(c=>!c.isGenerated).forEach(c=>row[c.name]="");const index=ws.inserted.push(row)-1;ws.activeRow=ws.rows.length+index;setDirty(ws,true);renderDataWorkspace(ws);requestAnimationFrame(()=>{const insertedRow=stage.querySelector(`[data-insert="${index}"]`);insertedRow?.scrollIntoView({block:"end",inline:"nearest"});const firstEditable=[...(insertedRow?.querySelectorAll("[data-insert-cell]")||[])].find(cell=>{const column=ws.columns.find(item=>item.name===cell.dataset.column);return column&&!column.isGenerated&&!column.isIdentity;});if(firstEditable)editInsertedCell(ws,firstEditable);});}
  function editInsertedCell(ws,cell){const col=ws.columns.find(c=>c.name===cell.dataset.column);if(!col||col.isGenerated||col.isIdentity)return;const row=ws.inserted[Number(cell.dataset.insertCell)],current=row[col.name]??"";cell.innerHTML="";const container=document.createElement("div");container.className="database-cell-editor-shell";const input=document.createElement("input");input.className="database-cell-editor";input.value=current;container.appendChild(input);cell.appendChild(container);let done=false,isExpanded=()=>false;const finish=(save,value=input.value)=>{if(done)return;done=true;if(save){row[col.name]=value;setDirty(ws,true);}renderDataWorkspace(ws);};isExpanded=attachExpandedEditorButton({workspace:ws,column:col,input,container,allowModes:false,onApply:value=>finish(true,value)});input.focus();input.select();input.onkeydown=e=>{if(e.key==="Enter")finish(true);if(e.key==="Escape")finish(false);};input.onblur=()=>{if(!isExpanded())finish(true);};}
  function deleteRows(ws){
    const rows=[...new Set([...ws.selected].map(x=>Number(x.split(":")[0])))]; if(!rows.length)return;
    rows.filter(row=>row<ws.rows.length).forEach(row=>ws.deleted.add(row));
    const removedInserts=new Set(rows.filter(row=>row>=ws.rows.length).map(row=>row-ws.rows.length));
    if(removedInserts.size){const heights=ws.inserted.map((_,index)=>ws.rowHeights[`insert:${index}`]);ws.inserted=ws.inserted.filter((_,index)=>!removedInserts.has(index));Object.keys(ws.rowHeights).filter(key=>key.startsWith("insert:")).forEach(key=>delete ws.rowHeights[key]);let next=0;heights.forEach((height,index)=>{if(!removedInserts.has(index)){if(height)ws.rowHeights[`insert:${next}`]=height;next++;}});}
    ws.selected.clear();ws.activeRow=null;syncDirty(ws);renderDataWorkspace(ws);
  }
  async function saveRows(ws){ if(ws.saving)return;ws.saving=true;setGridLoading(ws,true,t("grid.saving"));const saveButton=stage.querySelector("[data-save]");if(saveButton)saveButton.disabled=true;try{const grouped=new Map();ws.pending.forEach((value,key)=>{const ri=Number(key.split(":")[0]);if(!grouped.has(ri))grouped.set(ri,[]);grouped.get(ri).push(value);});const body={schema:ws.schema,objectName:ws.name,
    updates:[...grouped].filter(([ri])=>!ws.deleted.has(ri)).map(([ri,changes])=>({keys:ws.rows[ri].identity.keys,fingerprint:ws.rows[ri].identity.fingerprint,changes})),
    deletes:[...ws.deleted].map(ri=>({keys:ws.rows[ri].identity.keys,fingerprint:ws.rows[ri].identity.fingerprint})),
    inserts:ws.inserted.map(row=>({values:Object.entries(row).filter(([,v])=>v!=="").map(([column,value])=>({column,value,isNull:false,useDefault:false}))}))};
    await jsonApi(explorer.dataset.workspaceApplyUrl,body);ws.pending.clear();ws.deleted.clear();ws.inserted=[];ws.exactCount=null;ws.observedMinimum=0;setDirty(ws,false);await loadRows(ws,t("grid.reloadAfterSave"));
  }catch(e){showError(e.message);}finally{ws.saving=false;setGridLoading(ws,false);if(saveButton?.isConnected)saveButton.disabled=!ws.dirty;} }
  function openChart(source){const key=keyOf(source.schema,source.name,`chart:${Date.now()}`),selected=[...source.selected].map(x=>x.split(":").map(Number)),selectedCols=[...new Set(selected.map(x=>x[1]))];const numeric=(selectedCols.find(index=>source.columns[index]?.isNumeric)??source.columns.findIndex(c=>c.isNumeric));if(numeric<0){showError(t("chart.noNumeric"));return;}const selectedRows=new Set(selected.map(x=>x[0])),allRows=[...source.rows,...source.inserted.map(row=>({cells:source.columns.map(column=>({value:row[column.name]??null}))}))],rows=selectedRows.size?allRows.filter((_,index)=>selectedRows.has(index)):allRows;workspaces.set(key,{key,type:"chart",schema:source.schema,name:`${source.name} chart`,columns:source.columns,rows,numeric,chartType:"bar",dirty:false,used:Date.now(),loaded:true});activate(key);}
  function workspaceCellValue(ws,rowIndex,columnIndex){if(rowIndex<ws.rows.length){const column=ws.columns[columnIndex],pending=ws.pending.get(`${rowIndex}:${column?.name}`);return pending?pending.isNull?null:pending.useDefault?"DEFAULT":pending.value:ws.rows[rowIndex]?.cells[columnIndex]?.value;}return ws.inserted[rowIndex-ws.rows.length]?.[ws.columns[columnIndex]?.name]??null;}
  function updateFooter(ws){if(!ws){statistics.textContent=t("footer.empty");return;}const consoleResult=ws.type==="sql"&&ws.activeResult!=null?ws.results?.[ws.activeResult]:null,schema=consoleResult?.origin?.schema||ws.schema||"",objectName=consoleResult?.origin?.objectName||ws.name;footerPath.innerHTML=`<span>Database</span><b>›</b><span>${html(nodeId?"worker":"coordinator")}</span><b>›</b><span>${html(schema)}</span><b>›</b><strong>${html(objectName)}</strong><b>›</b><span>${html(consoleResult?`result ${ws.activeResult+1}`:ws.type)}</span>`;const setStats=value=>{statistics.textContent=t("footer.stats",value.rows,value.columns,value.cells)+(value.numeric?t("footer.numeric",number(value.sum),number(value.average),number(value.minimum),number(value.maximum)):"");};if(consoleResult){const valueAt=(row,column)=>{const name=consoleResult.columns[column]?.name,pending=consoleResult.pending?.get(`${row}:${name}`);if(pending)return pending.isNull?null:pending.useDefault?"DEFAULT":pending.value;if(row>=consoleResult.rows.length)return consoleResult.inserted?.[row-consoleResult.rows.length]?.[name]??null;return consoleResult.rows[row]?.[column]?.value??null;};setStats(gridSelectionStatistics(consoleResult.selected||new Set(),valueAt));return;}if(ws.type!=="data"){statistics.textContent=ws.type.toUpperCase();return;}setStats(gridSelectionStatistics(ws.selected,(row,column)=>workspaceCellValue(ws,row,column)));}

  stage.addEventListener("click",event=>{const ws=workspaces.get(activeKey);if(!ws)return;if(event.target.closest("[data-copy-ddl]"))navigator.clipboard.writeText(ws.ddl||"");if(event.target.closest("[data-download-ddl]")){const a=document.createElement("a");a.href=URL.createObjectURL(new Blob([ws.ddl||""],{type:"text/sql"}));a.download=`${ws.schema}.${ws.name}.sql`;a.click();URL.revokeObjectURL(a.href);}if(event.target.closest("[data-ddl-console]")){openQuery();const consoleWs=workspaces.get(activeKey);consoleWs.sql=ws.ddl||"";renderWorkspace(consoleWs);}});
  document.getElementById("database-tree-content")?.addEventListener("click",event=>{const object=event.target.closest("[data-database-object]");if(!object)return;event.stopImmediatePropagation();event.preventDefault();document.querySelectorAll("[data-database-object]").forEach(x=>x.classList.remove("is-active"));object.classList.add("is-active");openObject(object.dataset.schema,object.dataset.table,object.dataset.nodeKind==="sequence"?"ddl":"data");},true);
  window.databaseWorkspaces={openObject,openStructure:(s,n)=>openObject(s,n,"structure"),openDdl:(s,n)=>openObject(s,n,"ddl"),openQuery,openChart};
  document.addEventListener("copy",event=>{const ws=workspaces.get(activeKey);if(!ws||ws.type!=="data"||!ws.selected.size||event.target.matches?.("input,textarea"))return;const cells=[...ws.selected].map(k=>k.split(":").map(Number));const minR=Math.min(...cells.map(x=>x[0])),maxR=Math.max(...cells.map(x=>x[0])),minC=Math.min(...cells.map(x=>x[1])),maxC=Math.max(...cells.map(x=>x[1]));const text=[];for(let r=minR;r<=maxR;r++){const line=[];for(let c=minC;c<=maxC;c++)line.push(ws.selected.has(`${r}:${c}`)?(workspaceCellValue(ws,r,c)??""):"");text.push(line.join("\t"));}event.clipboardData.setData("text/plain",text.join("\r\n"));event.preventDefault();});
  try{const saved=JSON.parse(sessionStorage.getItem(storageKey)||"{}");(saved.workspaces||[]).slice(0,20).forEach(ws=>{ws.rows=[];ws.metadata=null;ws.pending=new Map();ws.deleted=new Set();ws.inserted=[];ws.selected=new Set();ws.rowHeights||={};ws.columnOrder||=[];ws.dirty=false;ws.loaded=false;ws.used=Date.now();workspaces.set(ws.key,ws);});renderTabs();if(saved.activeKey&&workspaces.has(saved.activeKey))activate(saved.activeKey);}catch{}
  addEventListener("beforeunload",event=>{if([...workspaces.values()].some(x=>x.dirty)){event.preventDefault();event.returnValue="";}});
})();
