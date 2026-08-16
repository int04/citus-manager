(() => {
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
  const storageKey = `cm-workspaces:${explorer.dataset.workspaceUser || "anonymous"}:${clusterKey}:${nodeId || "coordinator"}`;
  const workspaces = new Map();
  let activeKey = null;
  let consoleSequence = 0;

  const html = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  const problem = async response => {
    try { const body = await response.json(); return body.detail || body.title || "Database request failed."; }
    catch { return "Database request failed."; }
  };
  const jsonApi = async (url, body, signal) => {
    const response = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json", "RequestVerificationToken": token }, body: JSON.stringify(body), signal });
    if (!response.ok) throw new Error(await problem(response));
    return response.json();
  };
  const showError = message => { feedback.textContent = message; feedback.classList.remove("hidden"); feedback.focus(); };
  const reportError = error => { if (error?.name !== "AbortError") showError(error?.message || String(error)); };
  const clearError = () => { feedback.textContent = ""; feedback.classList.add("hidden"); };
  const keyOf = (schema, name, type = "data") => `${nodeId || "coordinator"}:${schema}.${name}:${type}`;
  const icon = type => type === "sql" ? "⌘" : type === "structure" ? "▦" : type === "ddl" ? "DDL" : type === "chart" ? "⌁" : "▤";

  function persist() {
    const safe = [...workspaces.values()].filter(x => !x.dirty).map(x => ({ key: x.key, type: x.type, schema: x.schema, name: x.name,
      page: x.page, pageSize: x.pageSize, where: x.where, orderBy: x.orderBy, widths: x.widths, hidden: x.hidden }));
    sessionStorage.setItem(storageKey, JSON.stringify({ activeKey, workspaces: safe }));
  }
  function ensureCapacity() {
    if (workspaces.size < 20) return true;
    const candidate = [...workspaces.values()].filter(x => !x.dirty && x.key !== activeKey).sort((a, b) => a.used - b.used)[0];
    if (!candidate) { showError("Đã đạt 20 workspace và tất cả đều có thay đổi chưa lưu."); return false; }
    closeWorkspace(candidate.key, true); return true;
  }
  function renderTabs() {
    tabs.replaceChildren();
    workspaces.forEach(ws => {
      const button = document.createElement("button"); button.type = "button"; button.role = "tab";
      button.className = `database-workspace-tab${ws.key === activeKey ? " is-active" : ""}`;
      button.dataset.workspaceKey = ws.key; button.setAttribute("aria-selected", String(ws.key === activeKey));
      const mark = document.createElement("span"); mark.className = "database-workspace-tab-icon"; mark.textContent = icon(ws.type);
      const label = document.createElement("span"); label.textContent = ws.type === "sql" ? ws.name : `${ws.name}${ws.type === "data" ? "" : ` · ${ws.type.toUpperCase()}`}`;
      const dirty = document.createElement("i"); dirty.textContent = ws.dirty ? "●" : "";
      const close = document.createElement("span"); close.className = "database-workspace-tab-close"; close.textContent = "×"; close.title = "Đóng workspace";
      button.append(mark, label, dirty, close); tabs.appendChild(button);
    });
  }
  async function activate(key) {
    const ws = workspaces.get(key); if (!ws) return;
    activeKey = key; ws.used = Date.now(); renderTabs(); empty.classList.add("hidden"); stage.classList.remove("hidden");
    if (!ws.loaded && ws.type !== "sql" && ws.type !== "chart") {
      showWorkspaceLoading();
      try { await hydrateWorkspace(ws); } catch (error) { showError(error.message); stage.innerHTML = `<div class="database-workspace-error">${html(error.message)}</div>`; }
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
  function closeWorkspace(key, force = false) {
    const ws = workspaces.get(key); if (!ws) return;
    if (ws.dirty && !force && !window.confirm("Workspace có thay đổi chưa lưu. Đóng và bỏ thay đổi?")) return;
    clearInterval(ws.autoTimer); ws.queryAbort?.abort(); ws.countAbort?.abort(); ws.sqlAbort?.abort();
    const keys = [...workspaces.keys()], index = keys.indexOf(key); workspaces.delete(key);
    if (activeKey === key) activeKey = [...workspaces.keys()][Math.max(0, index - 1)] || null;
    renderTabs();
    if (activeKey) activate(activeKey); else { stage.classList.add("hidden"); empty.classList.remove("hidden"); updateFooter(null); }
    persist();
  }
  tabs.addEventListener("click", event => {
    const tab = event.target.closest("[data-workspace-key]"); if (!tab) return;
    if (event.target.closest(".database-workspace-tab-close")) closeWorkspace(tab.dataset.workspaceKey); else activate(tab.dataset.workspaceKey);
  });
  tabs.addEventListener("auxclick", event => { if (event.button === 1) closeWorkspace(event.target.closest("[data-workspace-key]")?.dataset.workspaceKey); });
  tabs.addEventListener("keydown",event=>{if(!["ArrowLeft","ArrowRight","Home","End"].includes(event.key))return;event.preventDefault();const keys=[...workspaces.keys()],current=keys.indexOf(activeKey);const next=event.key==="Home"?0:event.key==="End"?keys.length-1:(current+(event.key==="ArrowRight"?1:-1)+keys.length)%keys.length;activate(keys[next]);tabs.querySelector(`[data-workspace-key="${CSS.escape(keys[next])}"]`)?.focus();});
  document.addEventListener("keydown", event => {
    if (event.ctrlKey && event.key.toLowerCase() === "w" && activeKey) { event.preventDefault(); closeWorkspace(activeKey); }
    if (event.ctrlKey && (event.key === "PageDown" || event.key === "PageUp") && workspaces.size) {
      event.preventDefault(); const keys = [...workspaces.keys()], current = keys.indexOf(activeKey);
      activate(keys[(current + (event.key === "PageDown" ? 1 : -1) + keys.length) % keys.length]);
    }
  });

  async function openObject(schema, name, type = "data") {
    const key = keyOf(schema, name, type); if (workspaces.has(key)) return activate(key); if (!ensureCapacity()) return;
    const ws = { key, type, schema, name, page: 1, pageSize: 50, where: "", orderBy: "", widths: {}, hidden: [], rows: [],
      metadata: null, dirty: false, pending: new Map(), deleted: new Set(), inserted: [], selected: new Set(), used: Date.now(), exactCount: null };
    workspaces.set(key, ws); activeKey = key; renderTabs(); empty.classList.add("hidden"); stage.classList.remove("hidden"); showWorkspaceLoading();
    try { await hydrateWorkspace(ws); }
    catch (error) { showError(error.message); stage.innerHTML = `<div class="database-workspace-error">${html(error.message)}</div>`; }
    persist();
  }
  function openQuery() {
    if (!ensureCapacity()) return; const id = ++consoleSequence, key = `sql:${Date.now()}:${id}`;
    workspaces.set(key, { key, type: "sql", schema: "", name: `console ${id} [${nodeId ? "worker" : "coordinator"}]`, sql: "", dirty: false, used: Date.now() }); activate(key);
  }
  function showWorkspaceLoading() { stage.innerHTML = '<div class="database-loading"><div><div class="database-spinner"></div><p>Đang mở workspace…</p></div></div>'; }

  async function loadRows(ws) {
    clearError(); ws.queryAbort?.abort(); ws.queryAbort = new AbortController();
    const data = await jsonApi(explorer.dataset.workspaceQueryUrl, { schema: ws.schema, objectName: ws.name, nodeId: nodeId ? Number(nodeId) : null,
      page: ws.page, pageSize: ws.pageSize, where: ws.where || null, orderBy: ws.orderBy || null }, ws.queryAbort.signal);
    ws.rows = data.rows; ws.columns = data.columns; ws.hasPrevious = data.hasPrevious; ws.hasNext = data.hasNext; ws.estimatedRows = data.estimatedRows;
    ws.selected.clear(); ws.exactCount = null; ws.loaded = true; if(activeKey===ws.key)renderDataWorkspace(ws);
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
  function dataToolbar(ws) {
    const start = ws.rows.length ? (ws.page - 1) * ws.pageSize + 1 : 0, end = start ? start + ws.rows.length - 1 : 0;
    const total = ws.exactCount ?? ws.estimatedRows; const totalLabel = total == null ? "?" : Number(total).toLocaleString();
    return `<div class="database-grid-toolbar">
      <button data-page="1" ${ws.hasPrevious ? "" : "disabled"} title="Trang đầu">«</button><button data-page="${ws.page - 1}" ${ws.hasPrevious ? "" : "disabled"}>‹</button>
      <label><input data-page-input type="number" min="1" value="${ws.page}"></label><span>${start}-${end} of ${totalLabel}${ws.exactCount == null ? " ~" : ""}</span>
      <button data-page="${ws.page + 1}" ${ws.hasNext ? "" : "disabled"}>›</button><button data-last-page ${ws.exactCount != null && ws.hasNext ? "" : "disabled"} title="Trang cuối">»</button><button data-refresh title="Reload">↻</button>
      <select data-page-size><option>25</option><option ${ws.pageSize === 50 ? "selected" : ""}>50</option><option>100</option></select>
      <button data-count>${ws.counting ? "Cancel Count" : "Count"}</button><label>Auto <select data-auto-refresh><option value="0">Off</option><option value="5" ${ws.autoRefresh===5?"selected":""}>5s</option><option value="15" ${ws.autoRefresh===15?"selected":""}>15s</option><option value="30" ${ws.autoRefresh===30?"selected":""}>30s</option><option value="60" ${ws.autoRefresh===60?"selected":""}>60s</option></select></label>
      <span class="database-toolbar-separator"></span><button data-add ${ws.metadata.canEdit ? "" : "disabled"} title="Add row">＋</button><button data-delete ${ws.metadata.canEdit ? "" : "disabled"} title="Delete selected rows">−</button>
      <button data-save ${ws.dirty ? "" : "disabled"}>Save</button><button data-revert ${ws.dirty ? "" : "disabled"}>Revert</button>
      <span class="database-toolbar-spacer"></span><details class="database-toolbar-menu"><summary>Columns</summary><div class="database-column-menu">${ws.columns.map(c=>`<label><input type="checkbox" data-column-visible="${html(c.name)}" ${ws.hidden.includes(c.name)?"":"checked"}> ${html(c.name)}</label>`).join("")}</div></details><details class="database-toolbar-menu"><summary>CSV</summary><div><button data-csv-page>Export page</button><button data-csv-all>Export all filter</button><button data-csv-import ${ws.metadata.canEdit ? "" : "disabled"}>Import…</button></div></details><input class="hidden" data-csv-file type="file" accept=".csv,text/csv"><button data-open-ddl>DDL</button><button data-chart>Chart</button>
    </div>`;
  }
  function renderDataWorkspace(ws) {
    const visible = ws.columns.map((c, i) => ({ c, i })).filter(x => !ws.hidden.includes(x.c.name));
    stage.innerHTML = `<div class="database-data-workspace">${dataToolbar(ws)}
      <div class="database-query-strip"><label><b>WHERE</b><input data-where value="${html(ws.where)}" placeholder="tenant_id = 42" autocomplete="off"><div class="database-query-suggestions hidden"></div></label>
      <label><b>ORDER BY</b><input data-order value="${html(ws.orderBy)}" placeholder="created_at DESC" autocomplete="off"><div class="database-query-suggestions hidden"></div></label><button data-apply-filter>Apply</button></div>
      <div class="database-workspace-grid-scroll"><table class="database-workspace-grid"><thead><tr><th class="database-row-number">#</th>${visible.map(({c}) => `<th data-column="${html(c.name)}" style="width:${ws.widths[c.name] || 180}px"><span>${html(c.name)}</span><small>${html(c.dataType)}</small><i class="database-column-resizer"></i></th>`).join("")}</tr></thead>
      <tbody>${ws.rows.map((row, ri) => `<tr data-row="${ri}" class="${ws.deleted.has(ri) ? "is-deleted" : ""}"><th class="database-row-number" data-select-row="${ri}">${(ws.page - 1) * ws.pageSize + ri + 1}</th>${visible.map(({c,i}) => cellHtml(ws,row,ri,c,i)).join("")}</tr>`).join("")}${ws.inserted.map((row, ii) => `<tr class="is-inserted" data-insert="${ii}"><th class="database-row-number">+</th>${visible.map(({c}) => `<td data-insert-cell="${ii}" data-column="${html(c.name)}">${html(row[c.name] ?? "")}</td>`).join("")}</tr>`).join("")}</tbody></table></div>
      ${ws.metadata.canEdit ? "" : `<div class="database-readonly-note">Read-only: ${html(ws.metadata.readOnlyReason || "object không hỗ trợ edit")}</div>`}</div>`;
    bindDataWorkspace(ws); updateFooter(ws);
  }
  function cellHtml(ws, row, ri, column, ci) {
    const pending = ws.pending.get(`${ri}:${column.name}`); const cell = row.cells[ci]; const value = pending ? pending.value : cell.value;
    const display=pending?.useDefault?'<span class="database-default">DEFAULT</span>':pending?.isNull||(!pending&&cell.isNull)?'<span class="database-null">NULL</span>':`<span>${html(value)}</span>`;
    return `<td tabindex="0" data-cell data-row="${ri}" data-col="${ci}" data-column="${html(column.name)}" data-truncated="${cell.isTruncated}" class="${pending ? "is-pending" : ""}${ws.selected.has(`${ri}:${ci}`) ? " is-selected" : ""}">${display}${cell.isTruncated&&!pending ? '<small>…</small>' : ""}</td>`;
  }
  function bindDataWorkspace(ws) {
    stage.querySelectorAll("[data-page]").forEach(b => b.onclick = () => { ws.page = Number(b.dataset.page); loadRows(ws).catch(reportError); });
    stage.querySelector("[data-page-input]").onchange = e => { ws.page = Math.max(1, Number(e.target.value)); loadRows(ws).catch(reportError); };
    stage.querySelector("[data-page-size]").onchange = e => { ws.pageSize = Number(e.target.value); ws.page = 1; loadRows(ws).catch(reportError); };
    stage.querySelector("[data-last-page]").onclick = () => { if(ws.exactCount != null){ws.page=Math.max(1,Math.ceil(ws.exactCount/ws.pageSize));loadRows(ws).catch(reportError);} };
    stage.querySelector("[data-refresh]").onclick = () => ws.dirty ? showError("Save/Revert thay đổi trước khi refresh.") : loadRows(ws).catch(reportError);
    stage.querySelector("[data-count]").onclick = () => ws.counting ? ws.countAbort?.abort() : countRows(ws);
    stage.querySelector("[data-auto-refresh]").onchange = e => setAutoRefresh(ws, Number(e.target.value));
    stage.querySelector("[data-apply-filter]").onclick = () => applyFilter(ws);
    stage.querySelectorAll(".database-query-strip input").forEach(input => bindSuggestions(ws, input));
    stage.querySelector("[data-add]").onclick = () => addRow(ws); stage.querySelector("[data-delete]").onclick = () => deleteRows(ws);
    stage.querySelector("[data-save]").onclick = () => saveRows(ws); stage.querySelector("[data-revert]").onclick = () => { ws.pending.clear(); ws.deleted.clear(); ws.inserted=[]; setDirty(ws,false); renderDataWorkspace(ws); };
    stage.querySelector("[data-open-ddl]").onclick = () => openObject(ws.schema, ws.name, "ddl"); stage.querySelector("[data-chart]").onclick = () => openChart(ws);
    stage.querySelector("[data-csv-page]").onclick = () => exportCsv(ws,true);stage.querySelector("[data-csv-all]").onclick = () => exportCsv(ws,false);
    stage.querySelectorAll("[data-column-visible]").forEach(input=>input.onchange=()=>{ws.hidden=input.checked?ws.hidden.filter(name=>name!==input.dataset.columnVisible):[...new Set([...ws.hidden,input.dataset.columnVisible])];persist();renderDataWorkspace(ws);});
    stage.querySelector("[data-csv-import]").onclick=()=>stage.querySelector("[data-csv-file]").click();stage.querySelector("[data-csv-file]").onchange=e=>{const file=e.target.files[0];if(file)previewCsvImport(ws,file);};
    stage.querySelectorAll("thead th[data-column]").forEach(th => { th.onclick = e => { if(e.target.closest(".database-column-resizer"))return;if(e.ctrlKey||e.metaKey){const ci=ws.columns.findIndex(c=>c.name===th.dataset.column);ws.selected.clear();ws.rows.forEach((_,ri)=>ws.selected.add(`${ri}:${ci}`));paintSelection(ws);}else sortColumn(ws, th.dataset.column, e.shiftKey); }; bindResize(ws, th); });
    stage.querySelectorAll("[data-cell]").forEach(cell => {
      cell.onpointerdown = event => beginCellSelection(ws, cell, event);
      cell.ondblclick = () => editCell(ws, cell); cell.onkeydown = e => { if (e.key === "F2" || e.key === "Enter") editCell(ws, cell); };
    });
    stage.querySelectorAll("[data-insert-cell]").forEach(cell => { cell.ondblclick=()=>editInsertedCell(ws,cell); cell.tabIndex=0; cell.onkeydown=e=>{if(e.key==="F2"||e.key==="Enter")editInsertedCell(ws,cell);}; });
    stage.querySelectorAll("[data-select-row]").forEach(head => head.onclick = () => { const ri=Number(head.dataset.selectRow); ws.columns.forEach((_,ci)=>ws.selected.add(`${ri}:${ci}`)); renderDataWorkspace(ws); });
  }
  function applyFilter(ws) { ws.where = stage.querySelector("[data-where]").value.trim(); ws.orderBy = stage.querySelector("[data-order]").value.trim(); ws.page=1; loadRows(ws).catch(reportError); persist(); }
  function bindSuggestions(ws,input) {
    const box=input.nextElementSibling; const values=[...ws.columns.map(c=>c.name),"AND","OR","NOT","NULL","IS NULL","IN ()","LIKE","ILIKE","ASC","DESC","NULLS LAST","count()","lower()","now()"];
    let part="",active=-1;const choose=button=>{input.value=input.value.slice(0,input.value.length-part.length)+button.textContent;box.classList.add("hidden");input.focus();};
    input.oninput=()=>{part=input.value.split(/[^\w.]+/).pop().toLowerCase();const matches=values.filter(x=>x.toLowerCase().startsWith(part)).slice(0,10);active=-1;box.innerHTML=matches.map(x=>`<button type="button">${html(x)}</button>`).join("");box.classList.toggle("hidden",!matches.length);box.querySelectorAll("button").forEach(b=>b.onclick=()=>choose(b));};
    input.onkeydown=e=>{const buttons=[...box.querySelectorAll("button")];if(e.ctrlKey&&e.key==="Enter"){e.preventDefault();applyFilter(ws);return;}if(e.key==="Escape"){box.classList.add("hidden");return;}if((e.key==="ArrowDown"||e.key==="ArrowUp")&&buttons.length){e.preventDefault();active=(active+(e.key==="ArrowDown"?1:-1)+buttons.length)%buttons.length;buttons.forEach((b,i)=>b.classList.toggle("is-active",i===active));buttons[active].scrollIntoView({block:"nearest"});}else if(e.key==="Enter"&&active>=0){e.preventDefault();choose(buttons[active]);}};
  }
  async function countRows(ws) { ws.countAbort=new AbortController();ws.counting=true;renderDataWorkspace(ws);try { const response=await fetch(explorer.dataset.workspaceCountUrl,{method:"POST",headers:{"Content-Type":"application/json","RequestVerificationToken":token},body:JSON.stringify({schema:ws.schema,objectName:ws.name,nodeId:nodeId?Number(nodeId):null,where:ws.where||null}),signal:ws.countAbort.signal});if(!response.ok)throw new Error(await problem(response));ws.exactCount=(await response.json()).count;}catch(e){if(e.name!=="AbortError")showError(e.message);}finally{ws.counting=false;ws.countAbort=null;renderDataWorkspace(ws);} }
  function setAutoRefresh(ws,seconds){clearInterval(ws.autoTimer);ws.autoRefresh=seconds;if(seconds>0)ws.autoTimer=setInterval(()=>{if(activeKey===ws.key&&!ws.dirty&&!stage.querySelector(".database-cell-editor"))loadRows(ws).catch(reportError);},seconds*1000);persist();}
  function sortColumn(ws,name,multi){ const parts=multi&&ws.orderBy?ws.orderBy.split(",").map(x=>x.trim()).filter(Boolean):[]; const at=parts.findIndex(x=>x.replace(/\s+(ASC|DESC).*$/i,"").replaceAll('"',"")===name); if(at<0)parts.push(`"${name.replaceAll('"','""')}" ASC`); else if(/\sASC/i.test(parts[at]))parts[at]=parts[at].replace(/\sASC/i," DESC"); else parts.splice(at,1); ws.orderBy=parts.join(", "); ws.page=1; loadRows(ws).catch(reportError); }
  function bindResize(ws,th){ const grip=th.querySelector(".database-column-resizer"); grip.onpointerdown=e=>{e.stopPropagation();const start=e.clientX,width=th.offsetWidth;grip.setPointerCapture(e.pointerId);grip.onpointermove=m=>{const next=Math.max(72,width+m.clientX-start);th.style.width=`${next}px`;ws.widths[th.dataset.column]=next;};grip.onpointerup=()=>persist();};grip.ondblclick=e=>{e.stopPropagation();ws.widths[th.dataset.column]=Math.min(520,Math.max(100,th.scrollWidth+24));renderDataWorkspace(ws);};}
  function paintSelection(ws){stage.querySelectorAll("[data-cell]").forEach(cell=>cell.classList.toggle("is-selected",ws.selected.has(`${cell.dataset.row}:${cell.dataset.col}`)));updateFooter(ws);}
  function beginCellSelection(ws,cell,event){const startRow=Number(cell.dataset.row),startCol=Number(cell.dataset.col);if(!(event.ctrlKey||event.metaKey))ws.selected.clear();ws.selected.add(`${startRow}:${startCol}`);paintSelection(ws);const move=target=>{const current=target.closest?.("[data-cell]");if(!current)return;const endRow=Number(current.dataset.row),endCol=Number(current.dataset.col);if(!(event.ctrlKey||event.metaKey))ws.selected.clear();for(let r=Math.min(startRow,endRow);r<=Math.max(startRow,endRow);r++)for(let c=Math.min(startCol,endCol);c<=Math.max(startCol,endCol);c++)ws.selected.add(`${r}:${c}`);paintSelection(ws);};const over=e=>{if(e.buttons===1)move(e.target);};const up=()=>{stage.removeEventListener("pointerover",over);document.removeEventListener("pointerup",up);};stage.addEventListener("pointerover",over);document.addEventListener("pointerup",up,{once:true});}
  async function editCell(ws,cell){const col=ws.columns[Number(cell.dataset.col)];if(!ws.metadata.canEdit||!col.canEdit)return;const ri=Number(cell.dataset.row),pending=ws.pending.get(`${ri}:${col.name}`);let current=pending?.value??ws.rows[ri].cells[Number(cell.dataset.col)].value??"";if(!pending&&cell.dataset.truncated==="true"){try{const full=await jsonApi(explorer.dataset.workspaceCellUrl,{schema:ws.schema,objectName:ws.name,column:col.name,identity:ws.rows[ri].identity});current=full.value??"";}catch(e){showError(e.message);return;}}cell.innerHTML="";let input;if(/^bool/i.test(col.dataType)){input=document.createElement("select");["true","false"].forEach(value=>{const option=document.createElement("option");option.value=value;option.textContent=value;input.appendChild(option);});}else{input=document.createElement(/json|text|char/i.test(col.dataType)?"textarea":"input");if(col.isNumeric)input.type="number";}input.className="database-cell-editor";input.value=current;cell.appendChild(input);input.focus();input.select?.();let done=false;const finish=(save,mode="value")=>{if(done)return;done=true;if(save){ws.pending.set(`${ri}:${col.name}`,{column:col.name,value:mode==="value"?input.value:null,isNull:mode==="null",useDefault:mode==="default"});setDirty(ws,true);}renderDataWorkspace(ws);};input.onkeydown=e=>{if(e.ctrlKey&&e.key==="0"){e.preventDefault();finish(true,"null");}else if(e.ctrlKey&&e.key.toLowerCase()==="d"){e.preventDefault();finish(true,"default");}else if(e.key==="Enter"&&!e.shiftKey){e.preventDefault();finish(true);}else if(e.key==="Escape")finish(false);};input.onblur=()=>finish(true);}
  function setDirty(ws,value){ws.dirty=value;renderTabs();persist();}
  function addRow(ws){ if(!ws.metadata.canEdit)return;const row={};ws.columns.filter(c=>!c.isGenerated).forEach(c=>row[c.name]="");ws.inserted.push(row);setDirty(ws,true);renderDataWorkspace(ws);}
  function editInsertedCell(ws,cell){const col=ws.columns.find(c=>c.name===cell.dataset.column);if(!col||col.isGenerated||col.isIdentity)return;const row=ws.inserted[Number(cell.dataset.insertCell)],current=row[col.name]??"";cell.innerHTML="";const input=document.createElement("input");input.className="database-cell-editor";input.value=current;cell.appendChild(input);input.focus();input.select();let done=false;const finish=save=>{if(done)return;done=true;if(save){row[col.name]=input.value;setDirty(ws,true);}renderDataWorkspace(ws);};input.onkeydown=e=>{if(e.key==="Enter")finish(true);if(e.key==="Escape")finish(false);};input.onblur=()=>finish(true);}
  function deleteRows(ws){ const rows=new Set([...ws.selected].map(x=>Number(x.split(":")[0])));rows.forEach(x=>ws.deleted.add(x));if(rows.size)setDirty(ws,true);renderDataWorkspace(ws);}
  async function saveRows(ws){ try{const grouped=new Map();ws.pending.forEach((value,key)=>{const ri=Number(key.split(":")[0]);if(!grouped.has(ri))grouped.set(ri,[]);grouped.get(ri).push(value);});const body={schema:ws.schema,objectName:ws.name,
    updates:[...grouped].filter(([ri])=>!ws.deleted.has(ri)).map(([ri,changes])=>({keys:ws.rows[ri].identity.keys,fingerprint:ws.rows[ri].identity.fingerprint,changes})),
    deletes:[...ws.deleted].map(ri=>({keys:ws.rows[ri].identity.keys,fingerprint:ws.rows[ri].identity.fingerprint})),
    inserts:ws.inserted.map(row=>({values:Object.entries(row).filter(([,v])=>v!=="").map(([column,value])=>({column,value,isNull:false,useDefault:false}))}))};
    await jsonApi(explorer.dataset.workspaceApplyUrl,body);ws.pending.clear();ws.deleted.clear();ws.inserted=[];setDirty(ws,false);await loadRows(ws);
  }catch(e){showError(e.message);} }
  async function exportCsv(ws,currentPageOnly){try{const response=await fetch(explorer.dataset.workspaceCsvExportUrl,{method:"POST",headers:{"Content-Type":"application/json","RequestVerificationToken":token},body:JSON.stringify({schema:ws.schema,objectName:ws.name,nodeId:nodeId?Number(nodeId):null,page:ws.page,pageSize:ws.pageSize,where:ws.where||null,orderBy:ws.orderBy||null,currentPageOnly})});if(!response.ok)throw new Error(await problem(response));const a=document.createElement("a");a.href=URL.createObjectURL(await response.blob());a.download=`${ws.schema}.${ws.name}${currentPageOnly?`.page-${ws.page}`:""}.csv`;a.click();URL.revokeObjectURL(a.href);}catch(e){showError(e.message);}}
  async function previewCsvImport(ws,file){if(file.size>25*1024*1024){showError("CSV vượt giới hạn 25 MiB.");return;}const form=new FormData();form.append("file",file);try{const response=await fetch(explorer.dataset.workspaceCsvPreviewUrl,{method:"POST",headers:{"RequestVerificationToken":token},body:form});if(!response.ok)throw new Error(await problem(response));const preview=await response.json();showCsvImportModal(ws,file,preview);}catch(e){showError(e.message);}}
  function showCsvImportModal(ws,file,preview){const modal=document.createElement("div");modal.className="database-modal";modal.setAttribute("role","dialog");modal.setAttribute("aria-modal","true");modal.innerHTML=`<div class="database-modal-card database-csv-card"><div class="database-action-heading"><div><p class="eyebrow">CSV IMPORT PREVIEW</p><h2>${html(file.name)} → ${html(ws.schema)}.${html(ws.name)}</h2></div><button type="button" data-csv-close class="database-action-close">×</button></div><p class="pma-modal-copy">Preview tối đa 100 rows. Header CSV map chính xác theo tên column. Import nguyên tử, tối đa 10.000 rows / 25 MiB.</p><div class="database-csv-preview"><table><thead><tr>${preview.headers.map(h=>`<th>${html(h)}</th>`).join("")}</tr></thead><tbody>${preview.rows.map(row=>`<tr>${row.map(v=>`<td>${html(v??"NULL")}</td>`).join("")}</tr>`).join("")}</tbody></table></div><div class="form-actions"><button type="button" class="btn btn-ghost" data-csv-close>Hủy</button><button type="button" class="btn btn-primary" data-csv-confirm>Import${preview.isTruncated?" toàn bộ file":""}</button></div></div>`;document.body.appendChild(modal);const close=()=>{modal.remove();stage.querySelector("[data-csv-file]")?.setAttribute("value","");};modal.querySelectorAll("[data-csv-close]").forEach(button=>button.onclick=close);modal.onclick=e=>{if(e.target===modal)close();};modal.querySelector("[data-csv-confirm]").onclick=async e=>{const button=e.currentTarget;button.disabled=true;button.textContent="Importing…";const form=new FormData();form.append("schema",ws.schema);form.append("objectName",ws.name);form.append("file",file);try{const response=await fetch(explorer.dataset.workspaceCsvImportUrl,{method:"POST",headers:{"RequestVerificationToken":token},body:form});if(!response.ok)throw new Error(await problem(response));close();await loadRows(ws);}catch(error){button.disabled=false;button.textContent="Import";showError(error.message);}};modal.querySelector("[data-csv-confirm]").focus();}
  function openChart(source){const key=keyOf(source.schema,source.name,`chart:${Date.now()}`),selected=[...source.selected].map(x=>x.split(":").map(Number)),selectedCols=[...new Set(selected.map(x=>x[1]))];const numeric=(selectedCols.find(index=>source.columns[index]?.isNumeric)??source.columns.findIndex(c=>c.isNumeric));if(numeric<0){showError("Page/selection hiện tại không có cột numeric để vẽ chart.");return;}const selectedRows=new Set(selected.map(x=>x[0])),rows=selectedRows.size?source.rows.filter((_,index)=>selectedRows.has(index)):source.rows;workspaces.set(key,{key,type:"chart",schema:source.schema,name:`${source.name} chart`,columns:source.columns,rows,numeric,chartType:"bar",dirty:false,used:Date.now(),loaded:true});activate(key);}
  function pieSlices(values){const positive=values.map(v=>Math.max(0,v)),total=positive.reduce((a,b)=>a+b,0)||1;let angle=-Math.PI/2;return positive.slice(0,24).map((value,index)=>{const next=angle+value/total*Math.PI*2,x1=400+130*Math.cos(angle),y1=180+130*Math.sin(angle),x2=400+130*Math.cos(next),y2=180+130*Math.sin(next),large=next-angle>Math.PI?1:0,path=`M 400 180 L ${x1} ${y1} A 130 130 0 ${large} 1 ${x2} ${y2} Z`;angle=next;return `<path d="${path}" fill="hsl(${index*47%360} 70% 55%)"><title>${value}</title></path>`;}).join("");}
  function renderChartWorkspace(ws){const values=ws.rows.map(r=>Number(r.cells[ws.numeric].value)).filter(Number.isFinite).slice(0,50),max=Math.max(...values.map(Math.abs),1),points=values.map((v,i)=>`${30+i*(740/Math.max(values.length-1,1))},${330-(v/max)*290}`).join(" ");const drawing=ws.chartType==="line"?`<polyline points="${points}" fill="none" stroke="#38bdf8" stroke-width="3"/>`:ws.chartType==="scatter"?values.map((v,i)=>`<circle cx="${30+i*(740/Math.max(values.length-1,1))}" cy="${330-(v/max)*290}" r="5"><title>${v}</title></circle>`).join(""):ws.chartType==="pie"?pieSlices(values):values.map((v,i)=>`<rect x="${i*(740/Math.max(values.length,1))+30}" y="${330-v/max*290}" width="${Math.max(4,700/Math.max(values.length,1))}" height="${Math.abs(v/max*290)}" rx="2"><title>${v}</title></rect>`).join("");stage.innerHTML=`<div class="database-chart-workspace"><div class="database-grid-toolbar"><strong>${html(ws.columns[ws.numeric].name)}</strong><select data-chart-kind><option value="bar">Bar</option><option value="line">Line</option><option value="pie">Pie</option><option value="scatter">Scatter</option></select><span>Current page/selection only · ${values.length} values</span></div><svg viewBox="0 0 800 360" role="img" aria-label="${html(ws.chartType)} chart">${drawing}</svg></div>`;stage.querySelector("[data-chart-kind]").value=ws.chartType;stage.querySelector("[data-chart-kind]").onchange=e=>{ws.chartType=e.target.value;renderChartWorkspace(ws);};updateFooter(ws);}
  function confirmCoordinatorSql(){const modal=document.getElementById("sql-confirm-modal");if(!modal)return Promise.resolve(false);if(!modal.dataset.workspaceReady){["confirm-sql-button","close-sql-modal"].forEach(id=>{const old=document.getElementById(id);old.replaceWith(old.cloneNode(true));});modal.dataset.workspaceReady="true";}return new Promise(resolve=>{modal.classList.remove("hidden");const confirm=document.getElementById("confirm-sql-button"),cancel=document.getElementById("close-sql-modal");const finish=value=>{modal.classList.add("hidden");confirm.onclick=null;cancel.onclick=null;modal.onclick=null;resolve(value);};confirm.onclick=()=>finish(true);cancel.onclick=()=>finish(false);modal.onclick=e=>{if(e.target===modal)finish(false);};confirm.focus();});}
  function renderSqlWorkspace(ws){stage.innerHTML=`<div class="database-console-workspace"><div class="database-grid-toolbar"><button data-run-sql>Run</button><button data-stop-sql disabled>Stop</button><span>${nodeId?"Worker · read-only":"Coordinator · mutation requires confirmation"}</span></div><textarea data-console-editor spellcheck="false" placeholder="SELECT * FROM public.table LIMIT 100;">${html(ws.sql)}</textarea><div data-console-result></div></div>`;const editor=stage.querySelector("[data-console-editor]"),run=stage.querySelector("[data-run-sql]"),stop=stage.querySelector("[data-stop-sql]"),result=stage.querySelector("[data-console-result]");editor.oninput=()=>{ws.sql=editor.value;};run.onclick=async()=>{if(!editor.value.trim())return;const confirmed=nodeId?false:await confirmCoordinatorSql();if(!nodeId&&!confirmed)return;ws.sqlAbort=new AbortController();run.disabled=true;stop.disabled=false;result.innerHTML='<div class="database-loading"><div><div class="database-spinner"></div><p>Đang chạy SQL…</p></div></div>';try{const body=new URLSearchParams({__RequestVerificationToken:token,Sql:editor.value,Confirmed:String(confirmed)});if(nodeId)body.set("NodeId",nodeId);const response=await fetch(explorer.dataset.sqlUrl,{method:"POST",body,signal:ws.sqlAbort.signal});result.innerHTML=response.ok?await response.text():`<div class="database-workspace-error">${html(await problem(response))}</div>`;}catch(e){if(e.name!=="AbortError")result.innerHTML=`<div class="database-workspace-error">${html(e.message)}</div>`;}finally{run.disabled=false;stop.disabled=true;ws.sqlAbort=null;}};stop.onclick=()=>ws.sqlAbort?.abort();updateFooter(ws);}
  function updateFooter(ws){if(!ws){statistics.textContent="0 row · 0 column · 0 cell";return;}footerPath.innerHTML=`<span>Database</span><b>›</b><span>${html(nodeId?"worker":"coordinator")}</span><b>›</b><span>${html(ws.schema||"")}</span><b>›</b><strong>${html(ws.name)}</strong><b>›</b><span>${html(ws.type)}</span>`;if(ws.type!=="data"){statistics.textContent=ws.type.toUpperCase();return;}const cells=[...ws.selected].map(k=>{const[ri,ci]=k.split(":").map(Number);return ws.rows[ri]?.cells[ci]?.value;}).filter(v=>v!=null);const nums=cells.map(Number).filter(Number.isFinite),rows=new Set([...ws.selected].map(k=>k.split(":")[0])).size,cols=new Set([...ws.selected].map(k=>k.split(":")[1])).size,sum=nums.reduce((a,b)=>a+b,0);statistics.textContent=`${rows} row · ${cols} column · Count ${cells.length}${nums.length?` · Sum ${sum.toLocaleString()} · Avg ${(sum/nums.length).toLocaleString()} · Min ${Math.min(...nums).toLocaleString()} · Max ${Math.max(...nums).toLocaleString()}`:""}`;}

  stage.addEventListener("click",event=>{const ws=workspaces.get(activeKey);if(!ws)return;if(event.target.closest("[data-copy-ddl]"))navigator.clipboard.writeText(ws.ddl||"");if(event.target.closest("[data-download-ddl]")){const a=document.createElement("a");a.href=URL.createObjectURL(new Blob([ws.ddl||""],{type:"text/sql"}));a.download=`${ws.schema}.${ws.name}.sql`;a.click();URL.revokeObjectURL(a.href);}if(event.target.closest("[data-ddl-console]")){openQuery();const consoleWs=workspaces.get(activeKey);consoleWs.sql=ws.ddl||"";renderWorkspace(consoleWs);}});
  document.getElementById("database-tree-content")?.addEventListener("click",event=>{const object=event.target.closest("[data-database-object]");if(!object)return;event.stopImmediatePropagation();event.preventDefault();document.querySelectorAll("[data-database-object]").forEach(x=>x.classList.remove("is-active"));object.classList.add("is-active");openObject(object.dataset.schema,object.dataset.table,object.dataset.nodeKind==="sequence"?"ddl":"data");},true);
  window.databaseWorkspaces={openObject,openStructure:(s,n)=>openObject(s,n,"structure"),openDdl:(s,n)=>openObject(s,n,"ddl"),openQuery};
  document.addEventListener("copy",event=>{const ws=workspaces.get(activeKey);if(!ws||ws.type!=="data"||!ws.selected.size||event.target.matches?.("input,textarea"))return;const cells=[...ws.selected].map(k=>k.split(":").map(Number));const minR=Math.min(...cells.map(x=>x[0])),maxR=Math.max(...cells.map(x=>x[0])),minC=Math.min(...cells.map(x=>x[1])),maxC=Math.max(...cells.map(x=>x[1]));const text=[];for(let r=minR;r<=maxR;r++){const line=[];for(let c=minC;c<=maxC;c++)line.push(ws.selected.has(`${r}:${c}`)?(ws.rows[r]?.cells[c]?.value??""):"");text.push(line.join("\t"));}event.clipboardData.setData("text/plain",text.join("\r\n"));event.preventDefault();});
  try{const saved=JSON.parse(sessionStorage.getItem(storageKey)||"{}");(saved.workspaces||[]).slice(0,20).forEach(ws=>{ws.rows=[];ws.metadata=null;ws.pending=new Map();ws.deleted=new Set();ws.inserted=[];ws.selected=new Set();ws.dirty=false;ws.loaded=false;ws.used=Date.now();workspaces.set(ws.key,ws);});renderTabs();if(saved.activeKey&&workspaces.has(saved.activeKey))activate(saved.activeKey);}catch{}
  addEventListener("beforeunload",event=>{if([...workspaces.values()].some(x=>x.dirty)){event.preventDefault();event.returnValue="";}});
})();
