import { html, problem } from "./shared.js";
import { createQueryHistory } from "./query-console-history.js";
import { createConsoleResultGrid } from "./console-result-grid.js";

const SQL_KEYWORDS = ["SELECT","FROM","WHERE","JOIN","LEFT JOIN","RIGHT JOIN","FULL JOIN","INNER JOIN","ON","GROUP BY","ORDER BY","HAVING","LIMIT","OFFSET","INSERT INTO","VALUES","UPDATE","SET","DELETE FROM","RETURNING","WITH","AS","DISTINCT","UNION ALL","CASE","WHEN","THEN","ELSE","END","NULL","IS NULL","IS NOT NULL","AND","OR","NOT","EXISTS","CREATE TABLE","ALTER TABLE","DROP TABLE","TRUNCATE","BEGIN","COMMIT","ROLLBACK","EXPLAIN","ANALYZE"];

function jsonHeaders(token) { return { "Content-Type": "application/json", "RequestVerificationToken": token }; }
function stamp(value = Date.now()) { return new Intl.DateTimeFormat("sv-SE", { dateStyle: "short", timeStyle: "medium" }).format(new Date(value)); }
function scopeLabel(scope, database = "Database") { return [database, scope.schema, scope.objectName].filter(Boolean).join("."); }

export function createQueryConsoleRenderer({ stage, explorer, token, nodeId, updateFooter, showError }) {
  const grid = createConsoleResultGrid({ explorer, token });

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
    modal.querySelector("h3").textContent = destructive ? "Xác nhận lệnh có thể phá hủy dữ liệu" : "Xác nhận mutation";
    modal.querySelector("p").textContent = destructive ? "UPDATE/DELETE không WHERE hoặc DDL phá hủy có thể ảnh hưởng toàn object." : "Các statement sau sẽ thay đổi dữ liệu hoặc schema.";
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
    tabs.innerHTML = `<button class="${workspace.activeResult == null ? "is-active" : ""}" data-output-tab>Output</button>${workspace.results.map((result,index) => `<button class="${workspace.activeResult === index ? "is-active" : ""}" data-result-tab="${index}">Result ${index+1} <small>${result.rows.length}</small></button>`).join("")}`;
    const output = root.querySelector("[data-console-output]"), data = root.querySelector("[data-console-data]");
    output.classList.toggle("hidden", workspace.activeResult != null); data.classList.toggle("hidden", workspace.activeResult == null);
    output.innerHTML = workspace.output.map(line => `<div class="console-output-line ${line.kind || ""}"><time>[${html(stamp(line.time))}]</time> <span>${html(line.text)}</span></div>`).join("");
    output.scrollTop = output.scrollHeight;
    if (workspace.activeResult != null) grid.render(data, workspace.results[workspace.activeResult]);
    tabs.querySelector("[data-output-tab]").onclick = () => { workspace.activeResult = null; renderResults(workspace, root); };
    tabs.querySelectorAll("[data-result-tab]").forEach(button => button.onclick = () => { workspace.activeResult = Number(button.dataset.resultTab); renderResults(workspace, root); });
  }

  async function renderHistory(workspace, root, search = "") {
    const host = root.querySelector("[data-console-history-list]");
    try {
      const rows = await workspace.history.list(search);
      host.innerHTML = rows.length ? rows.map(item => `<button type="button" data-history-id="${item.id}" title="Nạp query vào editor"><i class="fa ${item.success ? "fa-check-circle" : "fa-times-circle"}"></i><span><b>${html(item.command || "SQL")}</b><small>${html(stamp(item.timestamp))} · ${item.duration || 0} ms</small><code>${html(item.sql.slice(0,160))}</code></span><i class="fa fa-trash" data-history-delete="${item.id}" title="Xóa"></i></button>`).join("") : '<p class="console-history-empty">Chưa có lịch sử query.</p>';
      host.querySelectorAll("[data-history-id]").forEach(button => button.onclick = event => {
        if (event.target.closest("[data-history-delete]")) return;
        const item = rows.find(x => x.id === Number(button.dataset.historyId));
        if (!item || (workspace.editor.getValue().trim() && workspace.editor.getValue() !== item.sql && !confirm("Thay nội dung editor hiện tại?"))) return;
        workspace.editor.setValue(item.sql); workspace.editor.focus();
      });
      host.querySelectorAll("[data-history-delete]").forEach(button => button.onclick = async event => { event.stopPropagation(); await workspace.history.remove(Number(button.dataset.historyDelete)); renderHistory(workspace, root, search); });
    } catch { host.innerHTML = '<p class="console-history-empty">Không mở được IndexedDB history.</p>'; }
  }

  function metadataCompletions(metadata) {
    const values = SQL_KEYWORDS.map(label => ({ label, type: "keyword" }));
    metadata.schemas.forEach(label => values.push({ label, type: "namespace" }));
    metadata.relations.forEach(relation => {
      values.push({ label: `${relation.schema}.${relation.name}`, detail: relation.kind, type: "class" });
      values.push({ label: relation.name, detail: relation.schema, type: "class", boost: relation.schema === metadata.scope.schema ? 30 : 0 });
      relation.columns.forEach(column => values.push({ label: column, detail: relation.name, type: "property", boost: relation.name === metadata.scope.objectName ? 50 : 0 }));
    });
    metadata.functions.forEach(label => values.push({ label, type: "function", apply: `${label}()` }));
    metadata.dataTypes.forEach(label => values.push({ label, type: "type" }));
    metadata.joinSuggestions.forEach(label => values.push({ label, type: "keyword", detail: "foreign key JOIN" }));
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
          <button type="button" class="is-primary" data-console-run title="Ctrl+Enter"><i class="fa fa-play"></i> Run</button>
          <button type="button" data-console-run-all><i class="fa fa-forward"></i> Run All</button>
          <button type="button" data-console-stop disabled><i class="fa fa-stop"></i> Stop</button>
          <button type="button" data-console-clear><i class="fa fa-eraser"></i> Clear</button>
          <span class="query-console-target"><i class="fa fa-database"></i> ${html(scopeLabel(workspace.scope))}</span>
          <span class="query-console-mode ${nodeId ? "is-readonly" : ""}">${nodeId ? "Worker · read-only" : "Coordinator"}</span>
        </div><div class="query-console-editor" data-console-editor></div>
      </section>
      <div class="query-console-splitter horizontal" role="separator" aria-orientation="horizontal" aria-label="Thay đổi chiều cao SQL editor" tabindex="0"></div>
      <section class="query-console-bottom" style="height:${100-workspace.editorPercent}%">
        <aside class="query-console-history" style="width:${workspace.historyPercent}%"><header><b>Query History</b><button type="button" data-history-clear title="Xóa toàn bộ"><i class="fa fa-trash"></i></button></header><label><i class="fa fa-search"></i><input data-history-search type="search" placeholder="Tìm query…"></label><div data-console-history-list></div></aside>
        <div class="query-console-splitter vertical" role="separator" aria-orientation="vertical" aria-label="Thay đổi chiều rộng history" tabindex="0"></div>
        <main class="query-console-results" style="width:${100-workspace.historyPercent}%"><nav data-console-result-tabs></nav><div class="query-console-output" data-console-output></div><div class="query-console-data hidden" data-console-data></div></main>
      </section>
      <div class="query-console-confirm hidden" data-console-confirm role="dialog" aria-modal="true"><div><i class="fa fa-exclamation-triangle"></i><h3>Xác nhận mutation</h3><p></p><ul></ul><footer><button type="button" data-confirm-cancel>Hủy</button><button type="button" data-confirm-run>Chạy statement</button></footer></div></div>
    </div>`;
    const root = stage.querySelector("[data-query-console]"), editorPane = root.querySelector(".query-console-editor-pane"), bottom = root.querySelector(".query-console-bottom"), historyPane = root.querySelector(".query-console-history"), resultPane = root.querySelector(".query-console-results");
    bindSplitter(workspace, root.querySelector(".query-console-splitter.horizontal"), "y", "editorPercent", editorPane, bottom);
    bindSplitter(workspace, root.querySelector(".query-console-splitter.vertical"), "x", "historyPercent", historyPane, resultPane);
    if (!window.CitusQueryEditor) { root.querySelector("[data-console-editor]").innerHTML = '<p class="database-workspace-error">SQL editor bundle chưa tải.</p>'; return; }
    workspace.editor = window.CitusQueryEditor.create({ parent: root.querySelector("[data-console-editor]"), value: workspace.sql || "", onChange: value => { workspace.sql = value; workspace.dirty = !!value.trim(); }, onRun: () => execute(false) });
    renderResults(workspace, root); renderHistory(workspace, root);

    const metadataUrl = new URL(explorer.dataset.consoleMetadataUrl, location.origin);
    Object.entries(workspace.scope).forEach(([key,value]) => { if (value != null) metadataUrl.searchParams.set(key === "objectName" ? "name" : key, value); });
    const applyMetadata = metadata => { workspace.metadata = metadata; workspace.editor.setCompletions(metadataCompletions(metadata)); root.querySelector(".query-console-target").innerHTML = `<i class="fa fa-database"></i> ${html(scopeLabel(workspace.scope, metadata.database))}`; };
    if (workspace.metadata) applyMetadata(workspace.metadata);
    else fetch(metadataUrl).then(async response => { if (!response.ok) throw new Error(await problem(response)); return response.json(); }).then(applyMetadata).catch(error => showError(error.message));

    async function execute(all) {
      const sql = workspace.editor.getValue(); if (!sql.trim() || workspace.sqlAbort) return;
      try {
        const analysis = await api(explorer.dataset.consoleAnalyzeUrl, { sql, nodeId: workspace.scope.nodeId });
        const selection = workspace.editor.getSelection();
        let selected = analysis.statements;
        if (!all) selected = selection.empty ? analysis.statements.filter(x => selection.cursor >= x.start && selection.cursor <= x.start + x.length) : analysis.statements.filter(x => x.start < selection.to && x.start + x.length > selection.from);
        if (!selected.length) { showError("Đặt cursor trong statement hoặc bôi vùng SQL cần chạy."); return; }
        const risky = selected.filter(x => x.requiresConfirmation);
        if (!(await confirmRisk(root, risky))) return;
        workspace.results = []; workspace.activeResult = null;
        workspace.output.push({ time: Date.now(), text: `${scopeLabel(workspace.scope, workspace.metadata?.database || "database")}> ${selected.map(x => x.command).join(", ")}` });
        workspace.editor.setStatuses(selected.map(x => ({ line: x.startLine, status: "queued", title: "Đang chờ" })));
        const statuses = new Map(selected.map(x => [x.index, { line: x.startLine, status: "queued", title: "Đang chờ" }]));
        workspace.sqlAbort = new AbortController(); root.querySelector("[data-console-run]").disabled = true; root.querySelector("[data-console-run-all]").disabled = true; root.querySelector("[data-console-stop]").disabled = false;
        const started = performance.now(); let success = true;
        const response = await fetch(explorer.dataset.consoleExecuteUrl, { method: "POST", headers: jsonHeaders(token), signal: workspace.sqlAbort.signal, body: JSON.stringify({ sql, nodeId: workspace.scope.nodeId, scope: workspace.scope, statementIndexes: selected.map(x => x.index), confirmedStatementIndexes: risky.filter(x => String(x.risk).toLowerCase() === "write").map(x => x.index), destructiveConfirmedStatementIndexes: risky.filter(x => String(x.risk).toLowerCase() === "destructive").map(x => x.index), analysisHash: analysis.queryHash }) });
        if (!response.ok) throw new Error(await problem(response));
        const reader = response.body.getReader(), decoder = new TextDecoder(); let buffer = "";
        const accept = event => {
          if (event.type === "statementStarted") statuses.set(event.statementIndex, { line: analysis.statements[event.statementIndex].startLine, status: "running", title: "Đang chạy" });
          if (event.type === "statementSucceeded") { statuses.set(event.statementIndex, { line: analysis.statements[event.statementIndex].startLine, status: "success", title: `Thành công · ${event.durationMilliseconds} ms` }); workspace.output.push({ time: event.timestamp, kind: "success", text: `${event.command}: ${event.message} (${event.durationMilliseconds} ms)` }); }
          if (event.type === "statementFailed") { success = false; if (event.statementIndex != null) statuses.set(event.statementIndex, { line: analysis.statements[event.statementIndex].startLine, status: "error", title: event.message }); workspace.output.push({ time: event.timestamp, kind: "error", text: `${event.sqlState ? `[${event.sqlState}] ` : ""}${event.message}` }); }
          if (event.type === "connected") workspace.output.push({ time: event.timestamp, text: `Connected · ${event.message}` });
          if (event.type === "resultPage") { const descriptor = analysis.statements[event.statementIndex]; workspace.output.push({ time: event.timestamp, text: `${event.rows?.length || 0} rows retrieved in ${event.durationMilliseconds} ms (execution: ${event.executionMilliseconds || 0} ms, fetching: ${event.fetchingMilliseconds || 0} ms)` }); workspace.results.push({ sql: sql.substring(descriptor.start, descriptor.start + descriptor.length), nodeId: workspace.scope.nodeId, scope: workspace.scope, columns: event.columns || [], rows: event.rows || [], page: 1, pageSize: 20, hasPrevious: false, hasNext: !!event.isTruncated, widths: {} }); workspace.activeResult = workspace.results.length - 1; }
          workspace.editor.setStatuses([...statuses.values()]); renderResults(workspace, root);
        };
        while (true) { const chunk = await reader.read(); if (chunk.done) break; buffer += decoder.decode(chunk.value, { stream: true }); const lines = buffer.split("\n"); buffer = lines.pop(); lines.filter(Boolean).forEach(line => accept(JSON.parse(line))); }
        if (buffer.trim()) accept(JSON.parse(buffer));
        await workspace.history.add({ sql: selected.map(x => sql.substring(x.start, x.start+x.length)).join("\n\n"), context: scopeLabel(workspace.scope), command: selected.map(x => x.command).join(", "), success, duration: Math.round(performance.now()-started), queryHash: analysis.queryHash });
        renderHistory(workspace, root);
      } catch (error) { if (error.name !== "AbortError") { showError(error.message); workspace.output.push({ time: Date.now(), kind: "error", text: error.message }); renderResults(workspace, root); } }
      finally { workspace.sqlAbort = null; root.querySelector("[data-console-run]").disabled = false; root.querySelector("[data-console-run-all]").disabled = false; root.querySelector("[data-console-stop]").disabled = true; }
    }
    root.querySelector("[data-console-run]").onclick = () => execute(false);
    root.querySelector("[data-console-run-all]").onclick = () => execute(true);
    root.querySelector("[data-console-stop]").onclick = () => workspace.sqlAbort?.abort();
    root.querySelector("[data-console-clear]").onclick = () => { workspace.editor.setValue(""); workspace.output = []; workspace.results = []; workspace.activeResult = null; workspace.editor.setStatuses([]); renderResults(workspace, root); };
    root.querySelector("[data-history-search]").oninput = event => renderHistory(workspace, root, event.target.value);
    root.querySelector("[data-history-clear]").onclick = async () => { if (confirm("Xóa toàn bộ query history của target này?")) { await workspace.history.clear(); renderHistory(workspace, root); } };
    workspace.editor.focus(); updateFooter(workspace);
  }
  return { renderSqlWorkspace };
}
