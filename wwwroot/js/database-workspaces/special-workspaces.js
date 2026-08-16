import { html, problem } from "./shared.js";

function pieSlices(values) {
  const positive = values.map(value => Math.max(0, value));
  const total = positive.reduce((left, right) => left + right, 0) || 1;
  let angle = -Math.PI / 2;
  return positive.slice(0, 24).map((value, index) => {
    const next = angle + value / total * Math.PI * 2;
    const x1 = 400 + 130 * Math.cos(angle), y1 = 180 + 130 * Math.sin(angle);
    const x2 = 400 + 130 * Math.cos(next), y2 = 180 + 130 * Math.sin(next);
    const large = next - angle > Math.PI ? 1 : 0;
    const path = `M 400 180 L ${x1} ${y1} A 130 130 0 ${large} 1 ${x2} ${y2} Z`;
    angle = next;
    return `<path d="${path}" fill="hsl(${index * 47 % 360} 70% 55%)"><title>${value}</title></path>`;
  }).join("");
}

export function createSpecialWorkspaceRenderers({ stage, explorer, token, nodeId, updateFooter }) {
  function renderChartWorkspace(workspace) {
    const values = workspace.rows.map(row => Number(row.cells[workspace.numeric].value)).filter(Number.isFinite).slice(0, 50);
    const max = Math.max(...values.map(Math.abs), 1);
    const points = values.map((value, index) => `${30 + index * (740 / Math.max(values.length - 1, 1))},${330 - (value / max) * 290}`).join(" ");
    const drawing = workspace.chartType === "line"
      ? `<polyline points="${points}" fill="none" stroke="#38bdf8" stroke-width="3"/>`
      : workspace.chartType === "scatter"
        ? values.map((value, index) => `<circle cx="${30 + index * (740 / Math.max(values.length - 1, 1))}" cy="${330 - (value / max) * 290}" r="5"><title>${value}</title></circle>`).join("")
        : workspace.chartType === "pie"
          ? pieSlices(values)
          : values.map((value, index) => `<rect x="${index * (740 / Math.max(values.length, 1)) + 30}" y="${330 - value / max * 290}" width="${Math.max(4, 700 / Math.max(values.length, 1))}" height="${Math.abs(value / max * 290)}" rx="2"><title>${value}</title></rect>`).join("");
    stage.innerHTML = `<div class="database-chart-workspace"><div class="database-grid-toolbar"><strong>${html(workspace.columns[workspace.numeric].name)}</strong><select data-chart-kind><option value="bar">Bar</option><option value="line">Line</option><option value="pie">Pie</option><option value="scatter">Scatter</option></select><span>Current page/selection only · ${values.length} values</span></div><svg viewBox="0 0 800 360" role="img" aria-label="${html(workspace.chartType)} chart">${drawing}</svg></div>`;
    stage.querySelector("[data-chart-kind]").value = workspace.chartType;
    stage.querySelector("[data-chart-kind]").onchange = event => { workspace.chartType = event.target.value; renderChartWorkspace(workspace); };
    updateFooter(workspace);
  }

  function confirmCoordinatorSql() {
    const modal = document.getElementById("sql-confirm-modal");
    if (!modal) return Promise.resolve(false);
    if (!modal.dataset.workspaceReady) {
      ["confirm-sql-button", "close-sql-modal"].forEach(id => { const old = document.getElementById(id); old.replaceWith(old.cloneNode(true)); });
      modal.dataset.workspaceReady = "true";
    }
    return new Promise(resolve => {
      modal.classList.remove("hidden");
      const confirm = document.getElementById("confirm-sql-button"), cancel = document.getElementById("close-sql-modal");
      const finish = value => { modal.classList.add("hidden"); confirm.onclick = null; cancel.onclick = null; modal.onclick = null; resolve(value); };
      confirm.onclick = () => finish(true);
      cancel.onclick = () => finish(false);
      modal.onclick = event => { if (event.target === modal) finish(false); };
      confirm.focus();
    });
  }

  function renderSqlWorkspace(workspace) {
    stage.innerHTML = `<div class="database-console-workspace"><div class="database-grid-toolbar"><button data-run-sql>Run</button><button data-stop-sql disabled>Stop</button><span>${nodeId ? "Worker · read-only" : "Coordinator · mutation requires confirmation"}</span></div><textarea data-console-editor spellcheck="false" placeholder="SELECT * FROM public.table LIMIT 100;">${html(workspace.sql)}</textarea><div data-console-result></div></div>`;
    const editor = stage.querySelector("[data-console-editor]"), run = stage.querySelector("[data-run-sql]"), stop = stage.querySelector("[data-stop-sql]"), result = stage.querySelector("[data-console-result]");
    editor.oninput = () => { workspace.sql = editor.value; };
    run.onclick = async () => {
      if (!editor.value.trim()) return;
      const confirmed = nodeId ? false : await confirmCoordinatorSql();
      if (!nodeId && !confirmed) return;
      workspace.sqlAbort = new AbortController();
      run.disabled = true;
      stop.disabled = false;
      result.innerHTML = '<div class="database-loading"><div><div class="database-spinner"></div><p>Đang chạy SQL…</p></div></div>';
      try {
        const body = new URLSearchParams({ __RequestVerificationToken: token, Sql: editor.value, Confirmed: String(confirmed) });
        if (nodeId) body.set("NodeId", nodeId);
        const response = await fetch(explorer.dataset.sqlUrl, { method: "POST", body, signal: workspace.sqlAbort.signal });
        result.innerHTML = response.ok ? await response.text() : `<div class="database-workspace-error">${html(await problem(response))}</div>`;
      } catch (error) {
        if (error.name !== "AbortError") result.innerHTML = `<div class="database-workspace-error">${html(error.message)}</div>`;
      } finally {
        run.disabled = false;
        stop.disabled = true;
        workspace.sqlAbort = null;
      }
    };
    stop.onclick = () => workspace.sqlAbort?.abort();
    updateFooter(workspace);
  }

  return { renderChartWorkspace, renderSqlWorkspace };
}
