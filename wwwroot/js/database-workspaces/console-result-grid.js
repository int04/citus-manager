import { html, problem, createJsonApi } from "./shared.js";
import { attachExpandedEditorButton } from "./cell-editor.js";
import { createRowInspector } from "./row-inspector.js";
import { rowLocationPresentation } from "./row-locations.js";
import { cycleGridSort, gridSortState, normalizeColumnOrder, orderedColumnEntries, reorderGridColumn, selectGridRange } from "./data-grid-core.js";

const PAGE_SIZES = [5, 10, 15, 20, 25, 50, 100, 200, 500];
const DEFAULT_ROW_HEIGHT = 36;

export function createConsoleResultGrid({ explorer, token, showError = () => {} }) {
  const post = createJsonApi(token);
  const { openRowInspector } = createRowInspector({ explorer, token, showError });

  function initialize(result) {
    result.page ||= 1; result.pageSize ||= 20; result.widths ||= {}; result.rowHeights ||= {};
    result.hidden ||= []; result.columnOrder ||= []; result.selected ||= new Set(); result.pending ||= new Map();
    result.deleted ||= new Set(); result.inserted ||= []; result.autoRefresh ||= 0;
    normalizeColumnOrder(result);
  }

  const identityAt = (result, rowIndex) => result.identities?.[rowIndex] || null;
  const columnAt = (result, columnIndex) => {
    const name = result.columns[columnIndex]?.name;
    const metadata = result.metadata?.columns?.find(column => column.name === name);
    if (!metadata) return { ...result.columns[columnIndex], isNullable: true, isPrimaryKey: false, isIndexed: false, isUnique: false,
      isNumeric: /^(smallint|integer|bigint|numeric|decimal|real|double precision)/i.test(result.columns[columnIndex]?.dataType || ""), canEdit: false };
    return { ...metadata, canEdit: metadata.canEdit && Boolean(result.origin?.editableColumns?.includes(name)) };
  };
  function cellValue(result, rowIndex, columnIndex) {
    const name = result.columns[columnIndex]?.name;
    if (rowIndex >= result.rows.length) return result.inserted[rowIndex - result.rows.length]?.[name] ?? null;
    const pending = result.pending.get(`${rowIndex}:${name}`);
    if (pending) return pending.isNull ? null : pending.useDefault ? "DEFAULT" : pending.value;
    return result.rows[rowIndex]?.[columnIndex]?.value ?? null;
  }
  const dirty = result => result.pending.size > 0 || result.deleted.size > 0 || result.inserted.length > 0;

  async function ensureMetadata(host, result, rerender) {
    if (!result.origin || result.metadata || result.metadataLoading) return;
    result.metadataLoading = true;
    try {
      const url = new URL(explorer.dataset.workspaceMetadataUrl, location.origin);
      url.searchParams.set("schema", result.origin.schema); url.searchParams.set("name", result.origin.objectName);
      if (result.nodeId != null) url.searchParams.set("nodeId", result.nodeId);
      const response = await fetch(url);
      if (!response.ok) throw new Error(await problem(response));
      result.metadata = await response.json();
    } catch (error) { showError(error.message); }
    finally { result.metadataLoading = false; if (host.isConnected) rerender(); }
  }

  function columnHeader(result, column, originalIndex) {
    const metadata = columnAt(result, originalIndex), sort = gridSortState(result, column.name);
    const key = metadata.isPrimaryKey ? '<i class="fa fa-key database-column-key is-primary" title="Primary key"></i>'
      : metadata.isIndexed ? '<i class="fa fa-key database-column-key is-indexed" title="Indexed column"></i>' : "";
    const required = metadata.isNullable === false ? '<i class="database-column-required" title="NOT NULL"></i>' : "";
    const comment = metadata.comment ? `<button type="button" class="database-column-comment" data-result-comment title="${html(metadata.comment)}" aria-label="Comment: ${html(metadata.comment)}"><i class="fa fa-info-circle"></i></button>` : "";
    const sortIcon = sort ? `<i class="database-sort-indicator is-${sort.direction.toLowerCase()}" title="Sort ${sort.direction}"><i class="fa fa-sort-${sort.direction === "ASC" ? "asc" : "desc"}"></i>${sort.priority > 1 ? `<sup>${sort.priority}</sup>` : ""}</i>` : "";
    return `<th tabindex="0" draggable="true" data-column="${html(column.name)}" data-column-index="${originalIndex}" style="width:${result.widths[column.name] || 180}px" title="Kéo đổi vị trí · click sort"><span class="database-column-title">${key}${required}<b>${html(column.name)}</b>${comment}${sortIcon}</span><small>${html(column.dataType)}</small><i class="database-column-resizer" data-result-resize></i></th>`;
  }

  function toolbar(result) {
    const start = result.rows.length ? (result.page - 1) * result.pageSize + 1 : 0;
    const end = start ? start + result.rows.length - 1 : 0;
    const canMutate = Boolean(result.metadata?.canEdit && result.origin?.editableColumns?.length);
    const canEditRows = Boolean(canMutate && result.identities?.some(Boolean));
    return `<div class="database-grid-toolbar console-result-toolbar">
      <button type="button" data-result-first ${result.hasPrevious ? "" : "disabled"} title="Trang đầu"><i class="fa fa-fast-backward"></i></button>
      <button type="button" data-result-prev ${result.hasPrevious ? "" : "disabled"} title="Trang trước"><i class="fa fa-chevron-left"></i></button>
      <span class="console-result-range"><i class="fa fa-list-ol"></i> ${start}–${end}</span>
      <button type="button" data-result-count title="Exact count"><i class="fa fa-calculator"></i> ${result.exactCount == null ? "Count" : result.exactCount.toLocaleString()}</button>
      <button type="button" data-result-next ${result.hasNext ? "" : "disabled"} title="Trang sau"><i class="fa fa-chevron-right"></i></button>
      <label title="Rows per page"><i class="fa fa-th-list"></i><select data-result-size>${PAGE_SIZES.map(size => `<option value="${size}"${size === result.pageSize ? " selected" : ""}>${size}</option>`).join("")}</select></label>
      <button type="button" data-result-refresh title="Chạy lại trang"><i class="fa fa-refresh"></i></button>
      <label title="Tự động làm mới"><i class="fa fa-clock-o"></i><select data-result-auto><option value="0">Off</option>${[5,15,30,60].map(seconds => `<option value="${seconds}"${seconds === result.autoRefresh ? " selected" : ""}>${seconds}s</option>`).join("")}</select></label>
      <span class="database-toolbar-separator"></span>
      <button type="button" data-result-add ${canMutate ? "" : "disabled"} title="Add row"><i class="fa fa-plus"></i></button>
      <button type="button" data-result-delete ${canEditRows ? "" : "disabled"} title="Delete selected rows"><i class="fa fa-minus"></i></button>
      <button type="button" data-result-save ${dirty(result) ? "" : "disabled"} title="Save"><i class="fa fa-floppy-o"></i> Save</button>
      <button type="button" data-result-revert ${dirty(result) ? "" : "disabled"} title="Revert"><i class="fa fa-undo"></i> Revert</button>
      <span class="database-toolbar-spacer"></span>
      <details class="database-toolbar-menu"><summary><i class="fa fa-columns"></i> Columns</summary><div class="database-column-menu">${orderedColumnEntries(result).map(({c}) => `<label><input type="checkbox" data-result-visible="${html(c.name)}" ${result.hidden.includes(c.name) ? "" : "checked"}> ${html(c.name)}</label>`).join("")}</div></details>
      <button type="button" data-result-csv><i class="fa fa-download"></i> CSV</button>
      <button type="button" data-result-ddl ${result.origin ? "" : "disabled"}><i class="fa fa-code"></i> DDL</button>
      <button type="button" data-result-chart><i class="fa fa-bar-chart"></i> Chart</button>
      <span class="console-replay-note">Paging chạy lại SELECT${canMutate ? " · editable base table" : " · read-only result"}</span>
    </div>`;
  }

  function render(host, result, onChange) {
    initialize(result);
    const rerender = () => render(host, result, onChange);
    const visible = orderedColumnEntries(result).filter(({c}) => !result.hidden.includes(c.name));
    const rowHeaderWidth = result.showRowLocations ? 230 : (result.rowNumberWidth || 58);
    const tableWidth = rowHeaderWidth + visible.reduce((sum, {c}) => sum + (result.widths[c.name] || 180), 0);
    host.innerHTML = `<div class="console-result-grid database-data-workspace">${toolbar(result)}
      <div class="database-query-strip console-result-filter"><label><b>WHERE</b><input data-result-where value="${html(result.where || "")}" placeholder="WHERE…"></label><label><b>ORDER BY</b><input data-result-order value="${html(result.orderBy || "")}" placeholder="ORDER BY…"></label><button type="button" data-result-apply>Apply</button></div>
      <div class="database-workspace-grid-shell"><div class="database-workspace-grid-scroll"><table class="database-workspace-grid ${result.showRowLocations ? "has-row-locations" : ""}" style="width:${tableWidth}px;--database-row-number-width:${rowHeaderWidth}px"><colgroup><col style="width:${rowHeaderWidth}px">${visible.map(({c}) => `<col style="width:${result.widths[c.name] || 180}px">`).join("")}</colgroup><thead><tr><th class="database-row-number"><span class="database-row-number-head"><span>#</span><button type="button" data-result-locations ${result.origin && result.rows.length ? "" : "disabled"} title="Tải worker của page"><i class="fa ${result.rowLocationsLoading ? "fa-spinner fa-spin" : "fa-server"}"></i></button></span><i class="database-column-resizer database-row-number-resizer" data-result-number-resize></i></th>${visible.map(({c,i}) => columnHeader(result,c,i)).join("")}</tr></thead>
      <tbody>${result.rows.map((row,rowIndex) => rowHtml(result,row,rowIndex,visible)).join("")}${result.inserted.map((row,index) => insertedRowHtml(result,row,index,visible)).join("")}</tbody></table></div><div class="database-grid-loading ${result.loading ? "" : "hidden"}" role="status"><div><div class="database-spinner"></div><p>${html(result.loadingMessage || "Đang tải result…")}</p></div></div></div>
      ${result.metadata?.canEdit && !result.identities?.some(Boolean) ? '<div class="database-readonly-note">Read-only: SELECT cần trả đủ primary key để sửa row an toàn.</div>' : ""}</div>`;

    const reload = async (page = result.page, pageSize = result.pageSize, message = "Đang tải result…") => {
      result.abort?.abort(); result.abort = new AbortController(); result.loading = true; result.loadingMessage = message; rerender();
      try {
        const data = await post(explorer.dataset.consoleResultQueryUrl, { sql: result.sql, nodeId: result.nodeId, scope: result.scope, page, pageSize, where: result.where || null, orderBy: result.orderBy || null }, result.abort.signal);
        Object.assign(result, data); result.hydrated = true; result.selected.clear(); result.pending.clear(); result.deleted.clear(); result.inserted = []; result.rowLocations = null;
      } catch (error) { if (error.name !== "AbortError") showError(error.message); }
      finally { result.loading = false; result.abort = null; if (host.isConnected) { rerender(); onChange?.(result); } }
    };

    bind(host, result, rerender, reload, onChange);
    ensureMetadata(host, result, rerender);
    if (!result.hydrated && !result.loading) { result.hydrated = true; queueMicrotask(() => reload(1, result.pageSize, "Đang nạp metadata và identity…")); }
  }

  function rowHtml(result, row, rowIndex, visible) {
    const height = result.rowHeights[`page:${result.page}:${rowIndex}`] || DEFAULT_ROW_HEIGHT;
    const location = rowLocationPresentation(result, rowIndex), label = (result.page - 1) * result.pageSize + rowIndex + 1;
    const locationHtml = location ? `<span class="database-row-location ${location.available ? "is-available" : ""}" title="${html(location.title)}"><i class="fa fa-map-marker"></i>${html(location.label)}</span>` : "";
    const inspect = result.origin ? `<button type="button" data-result-inspect="${rowIndex}" title="Row details & placement"><i class="fa fa-info-circle"></i></button>` : '<button type="button" disabled title="Query không có single-table provenance"><i class="fa fa-info-circle"></i></button>';
    return `<tr data-visual-row="${rowIndex}" class="${result.activeRow === rowIndex ? "is-active-row " : ""}${result.deleted.has(rowIndex) ? "is-deleted" : ""}" style="height:${height}px"><th class="database-row-number" data-result-select-row="${rowIndex}"><span class="database-row-number-content ${location ? "has-location" : ""}"><span class="database-row-index">${label}</span>${locationHtml}${inspect}</span><i class="database-row-resizer" data-result-row-resize="page:${result.page}:${rowIndex}"></i></th>${visible.map(({c,i}) => cellHtml(result,row,rowIndex,c,i)).join("")}</tr>`;
  }

  function insertedRowHtml(result, row, insertedIndex, visible) {
    const visualRow = result.rows.length + insertedIndex, height = result.rowHeights[`insert:${insertedIndex}`] || DEFAULT_ROW_HEIGHT;
    return `<tr data-visual-row="${visualRow}" class="is-inserted ${result.activeRow === visualRow ? "is-active-row" : ""}" style="height:${height}px"><th class="database-row-number" data-result-select-row="${visualRow}"><span class="database-row-number-content"><span class="database-row-index">+</span></span><i class="database-row-resizer" data-result-row-resize="insert:${insertedIndex}"></i></th>${visible.map(({c,i}) => `<td tabindex="0" data-result-cell data-result-insert="${insertedIndex}" data-row="${visualRow}" data-col="${i}" data-column="${html(c.name)}" class="${result.selected.has(`${visualRow}:${i}`) ? "is-selected" : ""}">${html(row[c.name] ?? "")}</td>`).join("")}</tr>`;
  }

  function cellHtml(result, row, rowIndex, column, columnIndex) {
    const pending = result.pending.get(`${rowIndex}:${column.name}`), cell = row[columnIndex];
    const value = pending ? pending.value : cell?.value;
    const display = pending?.useDefault ? '<span class="database-default">DEFAULT</span>' : pending?.isNull || (!pending && cell?.isNull) ? '<span class="database-null">NULL</span>' : `<span>${html(value ?? "")}</span>`;
    return `<td tabindex="0" data-result-cell data-row="${rowIndex}" data-col="${columnIndex}" data-column="${html(column.name)}" data-truncated="${Boolean(cell?.isTruncated)}" class="${pending ? "is-pending " : ""}${result.selected.has(`${rowIndex}:${columnIndex}`) ? "is-selected" : ""}">${display}</td>`;
  }

  function bind(host, result, rerender, reload, onChange) {
    const apply = () => { result.where = host.querySelector("[data-result-where]").value.trim(); result.orderBy = host.querySelector("[data-result-order]").value.trim(); result.exactCount = null; reload(1); };
    host.querySelector("[data-result-first]").onclick = () => reload(1);
    host.querySelector("[data-result-prev]").onclick = () => reload(Math.max(1, result.page - 1));
    host.querySelector("[data-result-next]").onclick = () => reload(result.page + 1);
    host.querySelector("[data-result-refresh]").onclick = () => dirty(result) ? showError("Save/Revert trước khi refresh.") : reload();
    host.querySelector("[data-result-size]").onchange = event => reload(1, Number(event.target.value));
    host.querySelector("[data-result-apply]").onclick = apply;
    host.querySelectorAll("[data-result-where],[data-result-order]").forEach(input => input.onkeydown = event => { if (event.key === "Enter") { event.preventDefault(); apply(); } });
    host.querySelector("[data-result-count]").onclick = async () => { try { const data = await post(explorer.dataset.consoleResultCountUrl, { sql: result.sql, nodeId: result.nodeId, scope: result.scope, page: 1, pageSize: result.pageSize, where: result.where || null }); result.exactCount = data.count; rerender(); } catch (error) { showError(error.message); } };
    host.querySelector("[data-result-auto]").onchange = event => { clearInterval(result.autoTimer); result.autoRefresh = Number(event.target.value); if (result.autoRefresh) result.autoTimer = setInterval(() => { if (!dirty(result)) reload(); }, result.autoRefresh * 1000); onChange?.(result); };
    host.querySelectorAll("[data-result-visible]").forEach(input => input.onchange = () => { result.hidden = input.checked ? result.hidden.filter(name => name !== input.dataset.resultVisible) : [...new Set([...result.hidden,input.dataset.resultVisible])]; rerender(); onChange?.(result); });
    host.querySelector("[data-result-csv]").onclick = () => exportCsv(result);
    host.querySelector("[data-result-ddl]").onclick = () => result.origin && window.databaseWorkspaces?.openDdl(result.origin.schema, result.origin.objectName);
    host.querySelector("[data-result-chart]").onclick = () => window.databaseWorkspaces?.openChart?.({schema:result.origin?.schema||result.scope?.schema||"result",name:result.origin?.objectName||"console result",columns:result.columns.map((column,index)=>({...column,isNumeric:columnAt(result,index).isNumeric})),rows:result.rows.map(cells=>({cells})),inserted:result.inserted,selected:result.selected});
    host.querySelector("[data-result-add]").onclick = () => { const row = {}; result.columns.forEach(column => row[column.name] = ""); result.inserted.push(row); result.activeRow = result.rows.length + result.inserted.length - 1; rerender(); requestAnimationFrame(() => host.querySelector("tbody tr:last-child")?.scrollIntoView({block:"end"})); };
    host.querySelector("[data-result-delete]").onclick = () => { const rows = [...new Set([...result.selected].map(key => Number(key.split(":")[0])))]; rows.filter(index => index < result.rows.length && identityAt(result,index)).forEach(index => result.deleted.add(index)); const inserts = new Set(rows.filter(index => index >= result.rows.length).map(index => index-result.rows.length)); result.inserted = result.inserted.filter((_,index) => !inserts.has(index)); result.selected.clear(); rerender(); };
    host.querySelector("[data-result-save]").onclick = () => save(result, reload);
    host.querySelector("[data-result-revert]").onclick = () => { result.pending.clear(); result.deleted.clear(); result.inserted=[]; rerender(); };
    host.querySelector("[data-result-locations]").onclick = () => loadLocations(result, rerender);

    bindHeaders(host, result, rerender, reload, onChange);
    bindRows(host, result, rerender, onChange);
    host.oncopy = event => copySelection(event, result);
  }

  function bindHeaders(host, result, rerender, reload, onChange) {
    const headers = [...host.querySelectorAll("thead th[data-column]")]; let dragged = null;
    headers.forEach(header => {
      header.ondragstart = event => { if (event.target.closest(".database-column-resizer,[data-result-comment]")) { event.preventDefault(); return; } dragged=header.dataset.column; event.dataTransfer.setData("text/plain",dragged); };
      header.ondragover = event => { if (dragged && dragged !== header.dataset.column) event.preventDefault(); };
      header.ondrop = event => { event.preventDefault(); if (reorderGridColumn(result,dragged,header.dataset.column,event.clientX > header.getBoundingClientRect().left+header.offsetWidth/2)) { rerender(); onChange?.(result); } dragged=null; };
      header.onclick = event => { if (event.target.closest(".database-column-resizer,[data-result-comment]")) return; const index=Number(header.dataset.columnIndex); if(event.ctrlKey||event.metaKey){result.selected.clear();result.rows.forEach((_,row)=>result.selected.add(`${row}:${index}`));rerender();return;} cycleGridSort(result,header.dataset.column,event.shiftKey);result.exactCount=null;reload(1,result.pageSize,"Đang sắp xếp result…"); };
      const handle=header.querySelector("[data-result-resize]");handle.onpointerdown=event=>{event.preventDefault();event.stopPropagation();const start=event.clientX,width=header.offsetWidth;handle.setPointerCapture(event.pointerId);handle.onpointermove=move=>{result.widths[header.dataset.column]=Math.max(32,width+move.clientX-start);header.style.width=`${result.widths[header.dataset.column]}px`;};handle.onpointerup=()=>onChange?.(result);};
    });
    const numberHandle=host.querySelector("[data-result-number-resize]");numberHandle.onpointerdown=event=>{event.preventDefault();const start=event.clientX,width=result.rowNumberWidth||58;numberHandle.setPointerCapture(event.pointerId);numberHandle.onpointermove=move=>{result.rowNumberWidth=Math.min(600,Math.max(42,width+move.clientX-start));};numberHandle.onpointerup=()=>{rerender();onChange?.(result);};};
  }

  function bindRows(host, result, rerender, onChange) {
    host.querySelectorAll("[data-result-cell]").forEach(cell => {
      cell.onpointerdown = event => { const startRow=Number(cell.dataset.row),startColumn=Number(cell.dataset.col),additive=event.ctrlKey||event.metaKey;result.activeRow=startRow;selectGridRange(result,startRow,startColumn,startRow,startColumn,additive);paintSelection(host,result);onChange?.(result);const move=target=>{const current=target.closest?.("[data-result-cell]");if(!current)return;selectGridRange(result,startRow,startColumn,Number(current.dataset.row),Number(current.dataset.col),additive);paintSelection(host,result);onChange?.(result);};const over=moveEvent=>{if(moveEvent.buttons===1)move(moveEvent.target);};const up=()=>{host.removeEventListener("pointerover",over);document.removeEventListener("pointerup",up);};host.addEventListener("pointerover",over);document.addEventListener("pointerup",up,{once:true}); };
      cell.ondblclick = () => editCell(host,result,cell,rerender);
      cell.onkeydown = event => { if(event.key==="F2"||event.key==="Enter")editCell(host,result,cell,rerender); };
    });
    host.querySelectorAll("[data-result-select-row]").forEach(header => header.onclick=event=>{if(event.target.closest("[data-result-inspect],.database-row-resizer"))return;const row=Number(header.dataset.resultSelectRow);result.activeRow=row;result.selected.clear();result.columns.forEach((_,column)=>result.selected.add(`${row}:${column}`));rerender();onChange?.(result);});
    host.querySelectorAll("[data-result-inspect]").forEach(button=>button.onclick=event=>{event.stopPropagation();const rowIndex=Number(button.dataset.resultInspect);openRowInspector({workspace:{schema:result.origin?.schema||result.scope?.schema||"",name:result.origin?.objectName||result.scope?.objectName||"result"},nodeId:result.nodeId,label:(result.page-1)*result.pageSize+rowIndex+1,identity:identityAt(result,rowIndex),columns:result.columns,values:result.columns.map((_,column)=>cellValue(result,rowIndex,column)),truncated:result.columns.map((_,column)=>Boolean(result.rows[rowIndex]?.[column]?.isTruncated)),unsaved:false},button);});
    host.querySelectorAll("[data-result-row-resize]").forEach(handle=>handle.onpointerdown=event=>{event.preventDefault();const row=handle.closest("tr"),start=event.clientY,height=row.offsetHeight,key=handle.dataset.resultRowResize;handle.setPointerCapture(event.pointerId);handle.onpointermove=move=>{const next=Math.min(600,Math.max(28,height+move.clientY-start));row.style.height=`${next}px`;result.rowHeights[key]=next;};});
  }

  async function editCell(host,result,cell,rerender) {
    const rowIndex=Number(cell.dataset.row),columnIndex=Number(cell.dataset.col),column=columnAt(result,columnIndex),insertIndex=cell.dataset.resultInsert;
    if (insertIndex == null && result.origin && !result.metadata) { showError("Đang tải metadata để mở inline editor. Thử lại sau một lát."); return; }
    if(insertIndex == null && (!result.metadata?.canEdit || !column.canEdit || !identityAt(result,rowIndex))) return openFullCell(result,rowIndex,columnIndex,cell);
    const name=result.columns[columnIndex].name,original=result.rows[rowIndex]?.[columnIndex];let current=cellValue(result,rowIndex,columnIndex)??"";
    if(insertIndex==null&&!result.pending.has(`${rowIndex}:${name}`)&&original?.isTruncated){try{const full=await post(explorer.dataset.consoleResultCellUrl,{sql:result.sql,nodeId:result.nodeId,scope:result.scope,rowOffset:(result.page-1)*result.pageSize+rowIndex,columnIndex,where:result.where||null,orderBy:result.orderBy||null});current=full.value??"";}catch(error){showError(error.message);return;}}
    cell.innerHTML="";const shell=document.createElement("div");shell.className="database-cell-editor-shell";const input=document.createElement(/json|text|char/i.test(column.dataType)?"textarea":"input");input.className="database-cell-editor";input.value=current;shell.appendChild(input);cell.appendChild(shell);let done=false,touched=false,expanded=()=>false;input.oninput=()=>{touched=true;};const finish=(save,value=input.value,mode="value")=>{if(done)return;done=true;if(save){if(insertIndex!=null)result.inserted[Number(insertIndex)][name]=value;else{const key=`${rowIndex}:${name}`,originalValue=original?.isTruncated?current:(original?.value??""),unchanged=!touched&&mode==="value"||mode==="value"&&!original?.isNull&&value===originalValue||mode==="null"&&original?.isNull;if(unchanged)result.pending.delete(key);else result.pending.set(key,{column:name,value:mode==="value"?value:null,isNull:mode==="null",useDefault:mode==="default"});}}rerender();};expanded=attachExpandedEditorButton({workspace:{schema:result.origin?.schema,name:result.origin?.objectName},column,input,container:shell,allowModes:insertIndex==null,onApply:(value,mode)=>{touched=true;finish(true,value,mode);}});input.focus();input.select();input.onkeydown=event=>{if(event.key==="Escape")finish(false);else if(event.key==="Enter"&&!event.shiftKey){event.preventDefault();finish(true);}};input.onblur=()=>{if(!expanded())finish(true);};
  }

  function paintSelection(host,result){host.querySelectorAll("[data-result-cell]").forEach(cell=>cell.classList.toggle("is-selected",result.selected.has(`${cell.dataset.row}:${cell.dataset.col}`)));host.querySelectorAll("tbody tr[data-visual-row]").forEach(row=>row.classList.toggle("is-active-row",Number(row.dataset.visualRow)===result.activeRow));}

  async function openFullCell(result,rowIndex,columnIndex,anchor) {
    try { const value=await post(explorer.dataset.consoleResultCellUrl,{sql:result.sql,nodeId:result.nodeId,scope:result.scope,rowOffset:(result.page-1)*result.pageSize+rowIndex,columnIndex,where:result.where||null,orderBy:result.orderBy||null});const modal=document.createElement("div");modal.className="console-cell-modal";modal.innerHTML=`<div role="dialog" aria-modal="true"><header><b>${html(result.columns[columnIndex].name)}</b><button data-close-cell><i class="fa fa-times"></i></button></header><pre></pre><footer><button data-copy-cell><i class="fa fa-copy"></i> Copy</button></footer></div>`;modal.querySelector("pre").textContent=value.isNull?"NULL":value.value||"";document.body.appendChild(modal);const close=()=>{modal.remove();anchor.focus();};modal.querySelector("[data-close-cell]").onclick=close;modal.querySelector("[data-copy-cell]").onclick=()=>navigator.clipboard.writeText(value.value||"");modal.onclick=event=>{if(event.target===modal)close();}; } catch(error){showError(error.message);}
  }

  async function save(result,reload) {
    if(!result.origin)return;const updates=new Map();result.pending.forEach((change,key)=>{const row=Number(key.split(":")[0]);if(!updates.has(row))updates.set(row,[]);updates.get(row).push(change);});
    try { await post(explorer.dataset.workspaceApplyUrl,{schema:result.origin.schema,objectName:result.origin.objectName,updates:[...updates].filter(([row])=>!result.deleted.has(row)&&identityAt(result,row)).map(([row,changes])=>({keys:identityAt(result,row).keys,fingerprint:identityAt(result,row).fingerprint,changes})),deletes:[...result.deleted].filter(row=>identityAt(result,row)).map(row=>({keys:identityAt(result,row).keys,fingerprint:identityAt(result,row).fingerprint})),inserts:result.inserted.map(row=>({values:Object.entries(row).filter(([,value])=>value!=="").map(([column,value])=>({column,value,isNull:false,useDefault:false}))}))});result.pending.clear();result.deleted.clear();result.inserted=[];result.exactCount=null;await reload(); } catch(error){showError(error.message);}
  }

  async function loadLocations(result,rerender) {
    if(!result.origin)return;result.showRowLocations=true;result.rowLocationsLoading=true;rerender();try{const data=await post(explorer.dataset.workspaceLocationsUrl,{schema:result.origin.schema,objectName:result.origin.objectName,nodeId:result.nodeId,identities:result.rows.map((_,index)=>identityAt(result,index))});result.rowLocations=Object.fromEntries((data.locations||[]).map(item=>[item.rowIndex,item]));}catch(error){showError(error.message);}finally{result.rowLocationsLoading=false;rerender();}
  }

  async function exportCsv(result) { try { const response=await fetch(explorer.dataset.consoleResultExportUrl,{method:"POST",headers:{"Content-Type":"application/json","RequestVerificationToken":token},body:JSON.stringify({sql:result.sql,nodeId:result.nodeId,scope:result.scope,page:1,pageSize:result.pageSize,where:result.where||null,orderBy:result.orderBy||null})});if(!response.ok)throw new Error(await problem(response));const link=document.createElement("a");link.href=URL.createObjectURL(await response.blob());link.download="console-result.csv";link.click();URL.revokeObjectURL(link.href);}catch(error){showError(error.message);} }
  function copySelection(event,result){if(!result.selected.size)return;const cells=[...result.selected].map(key=>key.split(":").map(Number)),minRow=Math.min(...cells.map(item=>item[0])),maxRow=Math.max(...cells.map(item=>item[0])),minColumn=Math.min(...cells.map(item=>item[1])),maxColumn=Math.max(...cells.map(item=>item[1])),lines=[];for(let row=minRow;row<=maxRow;row++){const values=[];for(let column=minColumn;column<=maxColumn;column++)values.push(result.selected.has(`${row}:${column}`)?cellValue(result,row,column)??"":"");lines.push(values.join("\t"));}event.clipboardData.setData("text/plain",lines.join("\r\n"));event.preventDefault();}

  return { render };
}
