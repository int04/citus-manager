import { html, problem } from "./shared.js";
import { createQueryHistory } from "./query-console-history.js";
import { createConsoleResultGrid } from "./console-result-grid.js";

const SQL_KEYWORDS = ["SELECT","FROM","WHERE","JOIN","LEFT JOIN","RIGHT JOIN","FULL JOIN","INNER JOIN","ON","GROUP BY","ORDER BY","HAVING","LIMIT","OFFSET","INSERT INTO","VALUES","UPDATE","SET","DELETE FROM","RETURNING","WITH","AS","DISTINCT","UNION ALL","CASE","WHEN","THEN","ELSE","END","NULL","IS NULL","IS NOT NULL","AND","OR","NOT","EXISTS","CREATE TABLE","ALTER TABLE","DROP TABLE","TRUNCATE","BEGIN","COMMIT","ROLLBACK","EXPLAIN","ANALYZE"];
const RESERVED_IDENTIFIERS = new Set(SQL_KEYWORDS.flatMap(value => value.toLowerCase().split(/\s+/)).concat(["user","current_user","session_user","table","column","constraint","primary","references"]));
const sqlIdentifier = value => /^[a-z_][a-z0-9_$]*$/.test(value) && !RESERVED_IDENTIFIERS.has(value) ? value : `"${value.replaceAll('"','""')}"`;
const qualifiedIdentifier = (...parts) => parts.map(sqlIdentifier).join(".");
const t = (key, ...args) => window.CitusI18n?.t(key, ...args) ?? key;

function jsonHeaders(token) { return { "Content-Type": "application/json", "RequestVerificationToken": token }; }
function stamp(value = Date.now()) { return window.CitusI18n?.date(value, { dateStyle: "short", timeStyle: "medium" }) ?? new Date(value).toLocaleString(); }
function scopeLabel(scope, database = "Database") { return [database, scope.schema, scope.objectName].filter(Boolean).join("."); }

export function createQueryConsoleRenderer({ stage, explorer, token, nodeId, updateFooter, showError }) {
  const grid = createConsoleResultGrid({ explorer, token, showError });

  async function api(url, body, signal) {
    const response = await fetch(url, { method: "POST", headers: jsonHeaders(token), body: JSON.stringify(body), signal });
    if (!response.ok) throw new Error(await problem(response));
    return response.json();
  }

  function bindSplitter(workspace, element, axis, key, before, after) {
    const move = delta => {
      const host = element.parentElement.getBoundingClientRect();
      const size = axis === "y" ? host.height : host.width;
      workspace[key] = Math.max(18, Math.min(82, (workspace[key] || 50) + delta / size * 100));
      before.style[axis === "y" ? "height" : "width"] = `${workspace[key]}%`;
      after.style[axis === "y" ? "height" : "width"] = `${100 - workspace[key]}%`;
    };
    element.onpointerdown = event => {
      event.preventDefault(); const start = axis === "y" ? event.clientY : event.clientX; let last = start; element.setPointerCapture(event.pointerId);
      element.onpointermove = current => { const point = axis === "y" ? current.clientY : current.clientX; move(point - last); last = point; };
      element.onpointerup = () => { element.onpointermove = null; };
    };
    element.onkeydown = event => { if (!["ArrowUp","ArrowDown","ArrowLeft","ArrowRight"].includes(event.key)) return; event.preventDefault(); move((event.key === "ArrowDown" || event.key === "ArrowRight" ? 1 : -1) * 16); };
    element.ondblclick = () => { workspace[key] = key === "editorPercent" ? 48 : 26; before.style[axis === "y" ? "height" : "width"] = `${workspace[key]}%`; after.style[axis === "y" ? "height" : "width"] = `${100-workspace[key]}%`; };
  }

  function confirmRisk(root, statements) {
    if (!statements.length) return Promise.resolve(true);
    const destructive = statements.some(x => String(x.risk).toLowerCase() === "destructive");
    const modal = root.querySelector("[data-console-confirm]");
    modal.classList.toggle("is-destructive", destructive);
    modal.querySelector("h3").textContent = destructive ? t("console.confirmDestructive") : t("console.confirmMutation");
    modal.querySelector("p").textContent = destructive ? t("console.destructiveHelp") : t("console.mutationHelp");
    modal.querySelector("ul").innerHTML = statements.map(x => `<li><b>${html(x.command)}</b> · line ${x.startLine}–${x.endLine}</li>`).join("");
    modal.classList.remove("hidden");
    return new Promise(resolve => {
      const finish = value => { modal.classList.add("hidden"); modal.querySelector("[data-confirm-run]").onclick = null; modal.querySelector("[data-confirm-cancel]").onclick = null; resolve(value); };
      modal.querySelector("[data-confirm-run]").onclick = () => finish(true);
      modal.querySelector("[data-confirm-cancel]").onclick = () => finish(false);
      modal.querySelector("[data-confirm-cancel]").focus();
    });
  }

  function renderResults(workspace, root) {
    const tabs = root.querySelector("[data-console-result-tabs]");
    tabs.innerHTML = `<button class="${workspace.activeResult == null ? "is-active" : ""}" data-output-tab>${t("console.output")}</button>${workspace.results.map((result,index) => `<button class="${workspace.activeResult === index ? "is-active" : ""}" data-result-tab="${index}">Result ${index+1} <small>${result.rows.length}</small></button>`).join("")}`;
    const output = root.querySelector("[data-console-output]"), data = root.querySelector("[data-console-data]");
    output.classList.toggle("hidden", workspace.activeResult != null); data.classList.toggle("hidden", workspace.activeResult == null);
    output.innerHTML = workspace.output.map(line => `<div class="console-output-line ${line.kind || ""}"><time>[${html(stamp(line.time))}]</time> <span>${html(line.text)}</span></div>`).join("");
    output.scrollTop = output.scrollHeight;
    if (workspace.activeResult != null) grid.render(data, workspace.results[workspace.activeResult], () => updateFooter(workspace));
    tabs.querySelector("[data-output-tab]").onclick = () => { workspace.activeResult = null; renderResults(workspace, root); };
    tabs.querySelectorAll("[data-result-tab]").forEach(button => button.onclick = () => { workspace.activeResult = Number(button.dataset.resultTab); renderResults(workspace, root); });
  }

  async function renderHistory(workspace, root, search = "") {
    const host = root.querySelector("[data-console-history-list]");
    try {
      const rows = await workspace.history.list(search);
      host.innerHTML = rows.length ? rows.map(item => `<button type="button" data-history-id="${item.id}" title="${t("console.loadHistory")}"><i class="fa ${item.success ? "fa-check-circle" : "fa-times-circle"}"></i><span><b>${html(item.command || "SQL")}</b><small>${html(stamp(item.timestamp))} · ${item.duration || 0} ms</small><code>${html(item.sql.slice(0,160))}</code></span><i class="fa fa-trash" data-history-delete="${item.id}" title="${t("common.delete")}"></i></button>`).join("") : `<p class="console-history-empty">${t("console.noHistory")}</p>`;
      host.querySelectorAll("[data-history-id]").forEach(button => button.onclick = event => {
        if (event.target.closest("[data-history-delete]")) return;
        const item = rows.find(x => x.id === Number(button.dataset.historyId));
        if (!item || (workspace.editor.getValue().trim() && workspace.editor.getValue() !== item.sql && !confirm(t("console.replaceEditor")))) return;
        workspace.editor.setValue(item.sql); workspace.editor.focus();
      });
      host.querySelectorAll("[data-history-delete]").forEach(button => button.onclick = async event => { event.stopPropagation(); await workspace.history.remove(Number(button.dataset.historyDelete)); renderHistory(workspace, root, search); });
    } catch { host.innerHTML = `<p class="console-history-empty">${t("console.historyUnavailable")}</p>`; }
  }

  function metadataCompletions(metadata) {
    const values = SQL_KEYWORDS.map(label => ({ label, type: "keyword", boost: 30 }));
    const activeSchema = metadata.scope.schema || null;
    metadata.schemas.forEach(schema => values.push({ label: sqlIdentifier(schema), apply: sqlIdentifier(schema), detail: "schema", type: "namespace", boost: schema === activeSchema ? 90 : 5 }));
    metadata.relations.forEach(relation => {
      const sameSchema = activeSchema != null && relation.schema === activeSchema;
      const relationName = sameSchema ? sqlIdentifier(relation.name) : qualifiedIdentifier(relation.schema, relation.name);
      values.push({ label: relationName, apply: relationName, detail: `${relation.kind} · ${relation.schema}`, type: "class", boost: sameSchema ? 80 : 10 });
      relation.columns.forEach(column => {
        const isActiveObject = sameSchema && relation.name === metadata.scope.objectName;
        const columnName = isActiveObject ? sqlIdentifier(column) : sameSchema ? qualifiedIdentifier(relation.name, column) : qualifiedIdentifier(relation.schema, relation.name, column);
        values.push({ label: columnName, apply: columnName, detail: `${relation.schema}.${relation.name}`, type: "property", boost: isActiveObject ? 110 : sameSchema ? 70 : 0 });
      });
    });
    metadata.joinSuggestions.forEach(item => {
      const source = `${item.sourceSchema}.${item.sourceRelation}`.toLowerCase();
      const target = `${item.targetSchema}.${item.targetRelation}`.toLowerCase();
      values.push({ label: item.sql, apply: item.sql, type: "keyword", detail: `FK · ${source} → ${target}`, boost: 100,
        joinSource: [source, item.sourceRelation.toLowerCase()], joinTarget: [target, item.targetRelation.toLowerCase()] });
    });
    metadata.functions.forEach(label => values.push({ label, type: "function", apply: `${label}()`, boost: 20 }));
    metadata.dataTypes.forEach(label => values.push({ label, type: "type", boost: 15 }));
    return values;
  }

  async function renderSqlWorkspace(workspace) {
    workspace.editor?.destroy?.();
    workspace.scope ||= { kind: "database", schema: null, objectName: null, nodeId: nodeId ? Number(nodeId) : null };
    workspace.editorPercent ||= 48; workspace.historyPercent ||= 26; workspace.output ||= []; workspace.results ||= []; workspace.activeResult ??= null;
    workspace.history ||= createQueryHistory(`${explorer.dataset.workspaceUser || "anonymous"}:${explorer.dataset.clusterId}:${workspace.scope.nodeId || "coordinator"}`);
    stage.innerHTML = `<div class="database-query-console" data-query-console>
      <section class="query-console-editor-pane" style="height:${workspace.editorPercent}%">
        <div class="query-console-toolbar">
          <button type="button" class="is-primary" data-console-run title="Ctrl+Enter"><i class="fa fa-play"></i> ${t("console.run")}</button>
          <button type="button" class="is-stop hidden" data-console-stop><i class="fa fa-stop"></i> ${t("console.stop")}</button>
          <button type="button" data-console-format title="Ctrl+Shift+F"><i class="fa fa-indent"></i> ${t("console.format")}</button>
          <button type="button" data-console-clear><i class="fa fa-eraser"></i> ${t("console.clear")}</button>
          <span class="query-console-target"><i class="fa fa-database"></i> ${html(scopeLabel(workspace.scope))}</span>
          <span class="query-console-mode ${nodeId ? "is-readonly" : ""}">${nodeId ? t("console.workerReadOnly") : "Coordinator"}</span>
        </div><div class="query-console-editor" data-console-editor></div>
      </section>
      <div class="query-console-splitter horizontal" role="separator" aria-orientation="horizontal" aria-label="${t("console.resizeEditor")}" tabindex="0"></div>
      <section class="query-console-bottom" style="height:${100-workspace.editorPercent}%">
        <aside class="query-console-history" style="width:${workspace.historyPercent}%"><header><b>${t("console.history")}</b><button type="button" data-history-clear title="${t("common.delete")}"><i class="fa fa-trash"></i></button></header><label><i class="fa fa-search"></i><input data-history-search type="search"></label><div data-console-history-list></div></aside>
        <div class="query-console-splitter vertical" role="separator" aria-orientation="vertical" aria-label="${t("console.resizeHistory")}" tabindex="0"></div>
        <main class="query-console-results" style="width:${100-workspace.historyPercent}%"><nav data-console-result-tabs></nav><div class="query-console-output" data-console-output></div><div class="query-console-data hidden" data-console-data></div></main>
      </section>
      <div class="query-console-confirm hidden" data-console-confirm role="dialog" aria-modal="true"><div><i class="fa fa-exclamation-triangle"></i><h3></h3><p></p><ul></ul><footer><button type="button" data-confirm-cancel>${t("common.cancel")}</button><button type="button" data-confirm-run>${t("console.run")}</button></footer></div></div>
    </div>`;
    const root = stage.querySelector("[data-query-console]"), editorPane = root.querySelector(".query-console-editor-pane"), bottom = root.querySelector(".query-console-bottom"), historyPane = root.querySelector(".query-console-history"), resultPane = root.querySelector(".query-console-results");
    bindSplitter(workspace, root.querySelector(".query-console-splitter.horizontal"), "y", "editorPercent", editorPane, bottom);
    bindSplitter(workspace, root.querySelector(".query-console-splitter.vertical"), "x", "historyPercent", historyPane, resultPane);
    if (!window.CitusQueryEditor) { root.querySelector("[data-console-editor]").innerHTML = `<p class="database-workspace-error">${t("console.editorUnavailable")}</p>`; return; }
    workspace.editor = window.CitusQueryEditor.create({ parent: root.querySelector("[data-console-editor]"), value: workspace.sql || "", onChange: value => { workspace.sql = value; workspace.dirty = !!value.trim(); }, onRun: () => execute(), onSkipStatement: index => workspace.skipQueuedStatement?.(index) });
    renderResults(workspace, root); renderHistory(workspace, root);

    const metadataUrl = new URL(explorer.dataset.consoleMetadataUrl, location.origin);
    Object.entries(workspace.scope).forEach(([key,value]) => { if (value != null) metadataUrl.searchParams.set(key === "objectName" ? "name" : key, value); });
    const applyMetadata = metadata => { workspace.metadata = metadata; workspace.editor.setCompletions(metadataCompletions(metadata)); root.querySelector(".query-console-target").innerHTML = `<i class="fa fa-database"></i> ${html(scopeLabel(workspace.scope, metadata.database))}`; };
    if (workspace.metadata) applyMetadata(workspace.metadata);
    else fetch(metadataUrl).then(async response => { if (!response.ok) throw new Error(await problem(response)); return response.json(); }).then(applyMetadata).catch(error => showError(error.message));

    async function execute() {
      const editorSql = workspace.editor.getValue(), selection = workspace.editor.getSelection();
      const sql = selection.empty ? editorSql : editorSql.slice(selection.from, selection.to);
      if (!sql.trim() || workspace.sqlAbort) return;
      const lineOffset = selection.empty ? 0 : editorSql.slice(0, selection.from).split("\n").length - 1;
      const editorLine = statement => statement.startLine + lineOffset;
      try {
        const analysis = await api(explorer.dataset.consoleAnalyzeUrl, { sql, nodeId: workspace.scope.nodeId });
        const selected = analysis.statements;
        const risky = selected.filter(x => x.requiresConfirmation);
        if (!(await confirmRisk(root, risky))) return;
        workspace.results = []; workspace.activeResult = null;
        workspace.output.push({ time: Date.now(), text: `${scopeLabel(workspace.scope, workspace.metadata?.database || "database")}> ${selected.map(x => x.command).join(", ")}` });
        const executionId = crypto.randomUUID();
        const statuses = new Map(selected.map(x => [x.index, { statementIndex: x.index, line: editorLine(x), status: "queued", title: t("console.skipQueued") }]));
        const refreshStatuses = () => workspace.editor.setStatuses([...statuses.values()]);
        refreshStatuses();
        workspace.sqlAbort = new AbortController();
        const runButton = root.querySelector("[data-console-run]"), stopButton = root.querySelector("[data-console-stop]");
        runButton.classList.add("hidden"); stopButton.classList.remove("hidden");
        workspace.skipQueuedStatement = async statementIndex => {
          const current = statuses.get(statementIndex);
          if (!current || current.status !== "queued" || current.skipRequested) return;
          current.skipRequested = true; current.title = t("console.skipping"); refreshStatuses();
          try {
            await api(explorer.dataset.consoleSkipUrl, { executionId, statementIndex });
            statuses.set(statementIndex, { ...current, status: "skipped", title: t("console.skipped"), skipRequested: false });
            workspace.output.push({ time: Date.now(), text: `${selected.find(x => x.index === statementIndex)?.command || "Statement"}: skipped` });
          } catch (error) {
            current.skipRequested = false; current.title = t("console.skipQueued");
            showError(error.message);
          }
          refreshStatuses(); renderResults(workspace, root);
        };
        workspace.cancelExecution = () => {
          statuses.forEach((status, index) => {
            if (status.status === "queued" || status.status === "running")
              statuses.set(index, { ...status, status: "skipped", title: t("console.stopped") });
          });
          refreshStatuses(); workspace.sqlAbort?.abort();
        };
        const started = performance.now(); let success = true;
        const response = await fetch(explorer.dataset.consoleExecuteUrl, { method: "POST", headers: jsonHeaders(token), signal: workspace.sqlAbort.signal, body: JSON.stringify({ executionId, sql, nodeId: workspace.scope.nodeId, scope: workspace.scope, statementIndexes: selected.map(x => x.index), confirmedStatementIndexes: risky.filter(x => String(x.risk).toLowerCase() === "write").map(x => x.index), destructiveConfirmedStatementIndexes: risky.filter(x => String(x.risk).toLowerCase() === "destructive").map(x => x.index), analysisHash: analysis.queryHash }) });
        if (!response.ok) throw new Error(await problem(response));
        const reader = response.body.getReader(), decoder = new TextDecoder(); let buffer = "";
        const accept = event => {
          const descriptor = event.statementIndex == null ? null : analysis.statements.find(x => x.index === event.statementIndex);
          if (event.type === "statementStarted" && descriptor) statuses.set(event.statementIndex, { statementIndex: event.statementIndex, line: editorLine(descriptor), status: "running", title: t("console.runningShort") });
          if (event.type === "statementSkipped" && descriptor) statuses.set(event.statementIndex, { statementIndex: event.statementIndex, line: editorLine(descriptor), status: "skipped", title: t("console.skipped") });
          if (event.type === "statementSucceeded" && descriptor) { statuses.set(event.statementIndex, { statementIndex: event.statementIndex, line: editorLine(descriptor), status: "success", title: t("console.successDuration", event.durationMilliseconds) }); workspace.output.push({ time: event.timestamp, kind: "success", text: `${event.command}: ${event.message} (${event.durationMilliseconds} ms)` }); }
          if (event.type === "statementFailed") {
            success = false;
            if (descriptor) statuses.set(event.statementIndex, { statementIndex: event.statementIndex, line: editorLine(descriptor), status: "error", title: event.message });
            const prefix = [event.serverSeverity, event.sqlState].filter(Boolean).join(" · ");
            workspace.output.push({ time: event.timestamp, kind: "error", text: `${prefix ? `[${prefix}] ` : ""}${event.message}` });
            if (event.serverDetail) workspace.output.push({ time: event.timestamp, kind: "error-detail", text: `DETAIL: ${event.serverDetail}` });
            if (event.serverHint) workspace.output.push({ time: event.timestamp, kind: "error-detail", text: `HINT: ${event.serverHint}` });
            if (event.errorPosition) workspace.output.push({ time: event.timestamp, kind: "error-detail", text: `POSITION: ${event.errorPosition}` });
            const object = [event.schemaName, event.tableName].filter(Boolean).join(".");
            if (object) workspace.output.push({ time: event.timestamp, kind: "error-detail", text: `OBJECT: ${object}` });
            if (event.constraintName) workspace.output.push({ time: event.timestamp, kind: "error-detail", text: `CONSTRAINT: ${event.constraintName}` });
          }
          if (event.type === "connected") workspace.output.push({ time: event.timestamp, text: t("console.connected", event.message) });
          if (event.type === "resultPage") { const descriptor = analysis.statements[event.statementIndex]; workspace.output.push({ time: event.timestamp, text: t("console.rowsRetrieved", event.rows?.length || 0, event.durationMilliseconds, event.executionMilliseconds || 0, event.fetchingMilliseconds || 0) }); workspace.results.push({ sql: sql.substring(descriptor.start, descriptor.start + descriptor.length), nodeId: workspace.scope.nodeId, scope: workspace.scope, columns: event.columns || [], rows: event.rows || [], page: 1, pageSize: 20, hasPrevious: false, hasNext: !!event.isTruncated, widths: {} }); workspace.activeResult = workspace.results.length - 1; }
          refreshStatuses(); renderResults(workspace, root);
        };
        while (true) { const chunk = await reader.read(); if (chunk.done) break; buffer += decoder.decode(chunk.value, { stream: true }); const lines = buffer.split("\n"); buffer = lines.pop(); lines.filter(Boolean).forEach(line => accept(JSON.parse(line))); }
        if (buffer.trim()) accept(JSON.parse(buffer));
        await workspace.history.add({ sql, context: scopeLabel(workspace.scope), command: selected.map(x => x.command).join(", "), success, duration: Math.round(performance.now()-started), queryHash: analysis.queryHash });
        renderHistory(workspace, root);
      } catch (error) { if (error.name !== "AbortError") { showError(error.message); workspace.output.push({ time: Date.now(), kind: "error", text: error.message }); renderResults(workspace, root); } }
      finally { workspace.sqlAbort = null; workspace.skipQueuedStatement = null; workspace.cancelExecution = null; root.querySelector("[data-console-run]").classList.remove("hidden"); root.querySelector("[data-console-stop]").classList.add("hidden"); }
    }
    root.querySelector("[data-console-run]").onclick = () => execute();
    root.querySelector("[data-console-stop]").onclick = () => workspace.cancelExecution?.();
    root.querySelector("[data-console-format]").onclick = () => { try { workspace.editor.formatSql(); } catch (error) { showError(t("console.formatFailed", error.message)); } };
    root.querySelector("[data-console-clear]").onclick = () => { workspace.editor.setValue(""); workspace.output = []; workspace.results = []; workspace.activeResult = null; workspace.editor.setStatuses([]); renderResults(workspace, root); };
    root.querySelector("[data-history-search]").oninput = event => renderHistory(workspace, root, event.target.value);
    root.querySelector("[data-history-clear]").onclick = async () => { if (confirm(t("console.clearHistory"))) { await workspace.history.clear(); renderHistory(workspace, root); } };
    workspace.editor.focus(); updateFooter(workspace);
  }
  return { renderSqlWorkspace };
}
