import { html, problem } from "./shared.js";

export function createConsoleResultGrid({ explorer, token }) {
  async function post(url, body, signal) {
    const response = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json", "RequestVerificationToken": token }, body: JSON.stringify(body), signal });
    if (!response.ok) throw new Error(await problem(response));
    return response.json();
  }
  function render(host, result, onChange) {
    const columns = result.columns || [], rows = result.rows || [];
    host.innerHTML = `<div class="console-result-grid">
      <div class="database-grid-toolbar console-result-toolbar">
        <button type="button" data-result-first title="Trang đầu"><i class="fa fa-step-backward"></i></button>
        <button type="button" data-result-prev title="Trang trước"><i class="fa fa-chevron-left"></i></button>
        <span>Page <b>${result.page || 1}</b></span>
        <button type="button" data-result-next title="Trang sau"><i class="fa fa-chevron-right"></i></button>
        <label>Rows <select data-result-size>${[5,10,15,20,25,50,100,200,500].map(x => `<option${x === (result.pageSize || 20) ? " selected" : ""}>${x}</option>`).join("")}</select></label>
        <button type="button" data-result-count><i class="fa fa-calculator"></i> Count</button>
        <span data-result-total>${result.exactCount == null ? "~" : result.exactCount.toLocaleString()}</span>
        <button type="button" data-result-refresh title="Chạy lại trang"><i class="fa fa-refresh"></i></button>
        <button type="button" data-result-csv><i class="fa fa-download"></i> CSV</button>
        <span class="console-replay-note">Paging chạy lại SELECT</span>
      </div>
      <div class="console-result-filter"><label><i class="fa fa-filter"></i><input data-result-where value="${html(result.where || "")}" placeholder="WHERE…"></label><label><i class="fa fa-sort-amount-asc"></i><input data-result-order value="${html(result.orderBy || "")}" placeholder="ORDER BY…"></label><button type="button" data-result-apply>Apply</button></div>
      <div class="console-result-scroll"><table><thead><tr><th class="console-row-number">#</th>${columns.map((c,i) => `<th data-sort-column="${i}" style="width:${result.widths?.[i] || 180}px"><span>${html(c.name)}${result.sortColumn === i ? ` <i class="fa fa-sort-${result.sortDirection === "DESC" ? "desc" : "asc"}"></i>` : ""}</span><small>${html(c.dataType)}</small><i data-resize="${i}"></i></th>`).join("")}</tr></thead>
      <tbody>${rows.map((row,ri) => `<tr><th class="console-row-number">${(result.page-1)*result.pageSize+ri+1}</th>${row.map((cell,ci) => `<td data-result-cell data-row="${ri}" data-column="${ci}" class="${cell.isNull ? "is-null" : ""}" title="Double-click để xem full value${cell.isTruncated ? " · Giá trị đang bị cắt" : ""}">${cell.isNull ? "NULL" : html(cell.value ?? "")}</td>`).join("")}</tr>`).join("")}</tbody></table></div>
      <div class="console-result-summary">${rows.length} row · ${columns.length} column</div></div>`;
    host.querySelector("[data-result-first]").disabled = !result.hasPrevious;
    host.querySelector("[data-result-prev]").disabled = !result.hasPrevious;
    host.querySelector("[data-result-next]").disabled = !result.hasNext;
    const reload = async (page = result.page, pageSize = result.pageSize) => {
      result.abort?.abort(); result.abort = new AbortController(); host.classList.add("is-loading");
      try { Object.assign(result, await post(explorer.dataset.consoleResultQueryUrl, { sql: result.sql, nodeId: result.nodeId, scope: result.scope, page, pageSize, where: result.where || null, orderBy: result.orderBy || null }, result.abort.signal)); render(host, result, onChange); onChange?.(result); }
      finally { host.classList.remove("is-loading"); }
    };
    host.querySelector("[data-result-first]").onclick = () => reload(1);
    host.querySelector("[data-result-prev]").onclick = () => reload(Math.max(1, result.page - 1));
    host.querySelector("[data-result-next]").onclick = () => reload(result.page + 1);
    host.querySelector("[data-result-refresh]").onclick = () => reload();
    host.querySelector("[data-result-size]").onchange = event => reload(1, Number(event.target.value));
    host.querySelector("[data-result-count]").onclick = async () => { const data = await post(explorer.dataset.consoleResultCountUrl, { sql: result.sql, nodeId: result.nodeId, scope: result.scope, page: 1, pageSize: result.pageSize, where: result.where || null }); result.exactCount = data.count; render(host, result, onChange); };
    host.querySelector("[data-result-csv]").onclick = async () => {
      const response = await fetch(explorer.dataset.consoleResultExportUrl, { method: "POST", headers: { "Content-Type": "application/json", "RequestVerificationToken": token }, body: JSON.stringify({ sql: result.sql, nodeId: result.nodeId, scope: result.scope, page: 1, pageSize: result.pageSize, where: result.where || null, orderBy: result.orderBy || null }) });
      if (!response.ok) throw new Error(await problem(response)); const link = document.createElement("a"); link.href = URL.createObjectURL(await response.blob()); link.download = "console-result.csv"; link.click(); URL.revokeObjectURL(link.href);
    };
    const apply = () => { result.where = host.querySelector("[data-result-where]").value.trim(); result.orderBy = host.querySelector("[data-result-order]").value.trim(); result.exactCount = null; reload(1); };
    host.querySelector("[data-result-apply]").onclick = apply;
    host.querySelectorAll("[data-result-where],[data-result-order]").forEach(input => input.onkeydown = event => { if (event.key === "Enter") { event.preventDefault(); apply(); } });
    host.querySelectorAll("[data-sort-column]").forEach(header => header.onclick = event => {
      if (event.target.closest("[data-resize]")) return; const index = Number(header.dataset.sortColumn), name = columns[index].name.replaceAll('"','""');
      if (result.sortColumn !== index) { result.sortColumn = index; result.sortDirection = "ASC"; }
      else if (result.sortDirection === "ASC") result.sortDirection = "DESC";
      else { result.sortColumn = null; result.sortDirection = null; }
      result.orderBy = result.sortColumn == null ? "" : `"${name}" ${result.sortDirection}`; result.exactCount = null; reload(1);
    });
    host.querySelectorAll("[data-result-cell]").forEach(cell => cell.ondblclick = async () => {
      const rowOffset = (result.page - 1) * result.pageSize + Number(cell.dataset.row), columnIndex = Number(cell.dataset.column);
      const value = await post(explorer.dataset.consoleResultCellUrl, { sql: result.sql, nodeId: result.nodeId, scope: result.scope, rowOffset, columnIndex, where: result.where || null, orderBy: result.orderBy || null });
      const modal = document.createElement("div"); modal.className = "console-cell-modal"; modal.innerHTML = `<div role="dialog" aria-modal="true" aria-label="Full cell value"><header><b>${html(columns[columnIndex].name)}</b><button type="button" data-close-cell aria-label="Đóng"><i class="fa fa-times"></i></button></header><pre></pre><footer><button type="button" data-copy-cell><i class="fa fa-copy"></i> Copy</button><span>${value.isNull ? "NULL" : value.isTruncated ? "Truncated by safety limit" : columns[columnIndex].dataType}</span></footer></div>`;
      modal.querySelector("pre").textContent = value.isNull ? "NULL" : value.value || ""; document.body.appendChild(modal);
      const close = () => { modal.remove(); cell.focus?.(); }; modal.querySelector("[data-close-cell]").onclick = close; modal.onclick = event => { if (event.target === modal) close(); };
      modal.querySelector("[data-copy-cell]").onclick = () => navigator.clipboard.writeText(value.isNull ? "NULL" : value.value || ""); modal.querySelector("button").focus();
    });
    host.querySelectorAll("[data-resize]").forEach(handle => handle.onpointerdown = event => {
      event.preventDefault(); const index = Number(handle.dataset.resize), th = handle.closest("th"), start = event.clientX, width = th.getBoundingClientRect().width;
      handle.setPointerCapture(event.pointerId); handle.onpointermove = move => { result.widths ||= {}; result.widths[index] = Math.max(48, width + move.clientX - start); th.style.width = `${result.widths[index]}px`; };
      handle.onpointerup = () => { handle.onpointermove = null; onChange?.(result); };
    });
  }
  return { render };
}
