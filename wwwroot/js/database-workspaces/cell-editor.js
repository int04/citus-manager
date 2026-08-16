import { html } from "./shared.js";

function showExpandedCellEditor({ workspace, column, value, allowModes, onApply, onCancel }) {
  const isJson = /\bjsonb?\b/i.test(column.dataType || "");
  const modal = document.createElement("div");
  modal.className = "database-modal database-expanded-cell-modal";
  modal.setAttribute("role", "dialog");
  modal.setAttribute("aria-modal", "true");
  modal.setAttribute("aria-labelledby", "expanded-cell-title");
  modal.innerHTML = `<div class="database-modal-card"><header><div><p>${html(workspace.schema)}.${html(workspace.name)}</p><h2 id="expanded-cell-title">Chỉnh sửa ${html(column.name)}</h2></div><button type="button" data-cell-modal-close aria-label="Đóng"><i class="fa fa-times" aria-hidden="true"></i></button></header><div class="database-expanded-cell-meta"><span>${html(column.dataType)}</span>${column.isNullable ? "<span>NULLABLE</span>" : "<span>NOT NULL</span>"}<b data-cell-length>0 ký tự</b></div>${isJson ? `<div class="database-json-editor-toolbar"><div role="tablist" aria-label="Kiểu hiển thị JSON"><button type="button" role="tab" aria-selected="true" data-json-view="raw" class="is-active">Raw text</button><button type="button" role="tab" aria-selected="false" data-json-view="json">JSON</button></div><span data-json-status>Raw text</span><button type="button" data-json-format class="hidden"><i class="fa fa-indent" aria-hidden="true"></i> Format</button><button type="button" data-json-compact class="hidden"><i class="fa fa-compress" aria-hidden="true"></i> Compact</button></div>` : ""}<textarea data-cell-modal-value spellcheck="false" aria-label="Giá trị cột ${html(column.name)}"></textarea>${isJson ? `<div class="database-json-editor-error hidden" data-json-error role="alert"></div>` : ""}${allowModes ? `<div class="database-expanded-cell-modes"><button type="button" data-cell-mode="value" class="is-active">VALUE</button><button type="button" data-cell-mode="null" ${column.isNullable ? "" : "disabled"}>NULL</button><button type="button" data-cell-mode="default">DEFAULT</button></div>` : ""}<footer><span>Ctrl+Enter để áp dụng · Escape để hủy</span><div><button type="button" class="btn btn-ghost" data-cell-modal-close>Hủy</button><button type="button" class="btn btn-primary" data-cell-modal-apply>Áp dụng</button></div></footer></div>`;

  document.body.appendChild(modal);
  document.body.classList.add("database-modal-open");
  const textarea = modal.querySelector("[data-cell-modal-value]");
  const counter = modal.querySelector("[data-cell-length]");
  textarea.value = value ?? "";
  let mode = "value";
  let editorView = "raw";

  const updateCounter = () => { counter.textContent = `${textarea.value.length.toLocaleString()} ký tự`; };
  const jsonError = modal.querySelector("[data-json-error]");
  const jsonStatus = modal.querySelector("[data-json-status]");
  const validateJson = (showError = true) => {
    if (!isJson) return true;
    try {
      JSON.parse(textarea.value);
      jsonStatus.textContent = editorView === "json" ? "JSON hợp lệ" : "Raw text";
      jsonStatus.classList.toggle("is-valid", editorView === "json");
      jsonStatus.classList.remove("is-invalid");
      jsonError.classList.add("hidden");
      return true;
    } catch (error) {
      jsonStatus.textContent = "JSON không hợp lệ";
      jsonStatus.classList.remove("is-valid");
      jsonStatus.classList.add("is-invalid");
      if (showError) { jsonError.textContent = error.message; jsonError.classList.remove("hidden"); }
      return false;
    }
  };
  const replaceJson = spacing => {
    if (!validateJson()) return;
    textarea.value = JSON.stringify(JSON.parse(textarea.value), null, spacing);
    updateCounter();
    validateJson();
    textarea.focus();
  };
  const switchJsonView = nextView => {
    if (!isJson || nextView === editorView) return;
    if (nextView === "json" && validateJson()) textarea.value = JSON.stringify(JSON.parse(textarea.value), null, 2);
    else if (nextView === "raw" && validateJson(false)) textarea.value = JSON.stringify(JSON.parse(textarea.value));
    editorView = nextView;
    modal.querySelectorAll("[data-json-view]").forEach(button => { const active = button.dataset.jsonView === editorView; button.classList.toggle("is-active", active); button.setAttribute("aria-selected", String(active)); });
    modal.querySelectorAll("[data-json-format],[data-json-compact]").forEach(button => button.classList.toggle("hidden", editorView !== "json"));
    updateCounter();
    validateJson(editorView === "json");
    textarea.focus();
  };
  const remove = () => { modal.remove(); document.body.classList.remove("database-modal-open"); };
  const close = () => { remove(); onCancel?.(); };
  const apply = () => {
    if (mode === "value" && isJson && !validateJson()) { textarea.focus(); return; }
    const nextValue = textarea.value, nextMode = mode;
    remove();
    onApply(nextValue, nextMode);
  };

  updateCounter();
  textarea.oninput = () => { updateCounter(); if (isJson && editorView === "json") validateJson(); };
  modal.querySelectorAll("[data-json-view]").forEach(button => { button.onclick = () => switchJsonView(button.dataset.jsonView); });
  modal.querySelector("[data-json-format]")?.addEventListener("click", () => replaceJson(2));
  modal.querySelector("[data-json-compact]")?.addEventListener("click", () => replaceJson(0));
  modal.querySelectorAll("[data-cell-modal-close]").forEach(button => { button.onclick = close; });
  modal.querySelector("[data-cell-modal-apply]").onclick = apply;
  modal.onclick = event => { if (event.target === modal) close(); };
  modal.querySelectorAll("[data-cell-mode]").forEach(button => {
    button.onclick = () => {
      mode = button.dataset.cellMode;
      modal.querySelectorAll("[data-cell-mode]").forEach(item => item.classList.toggle("is-active", item === button));
      textarea.disabled = mode !== "value";
      modal.querySelectorAll("[data-json-view],[data-json-format],[data-json-compact]").forEach(item => { item.disabled = mode !== "value"; });
      if (mode === "value") textarea.focus();
    };
  });
  modal.onkeydown = event => {
    if (event.key === "Escape") { event.preventDefault(); close(); }
    else if (event.ctrlKey && event.key === "Enter") { event.preventDefault(); apply(); }
    else if (event.key === "Tab") {
      const focusable = [...modal.querySelectorAll("button:not(:disabled),textarea:not(:disabled)")];
      const first = focusable[0], last = focusable.at(-1);
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }
  };
  textarea.focus();
  textarea.select();
}

export function attachExpandedEditorButton({ workspace, column, input, container, allowModes, onApply }) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "database-cell-editor-expand";
  button.title = "Mở trình chỉnh sửa đầy đủ";
  button.setAttribute("aria-label", "Mở trình chỉnh sửa đầy đủ");
  button.innerHTML = '<i class="fa fa-expand" aria-hidden="true"></i>';
  container.appendChild(button);

  let expanded = false;
  button.onpointerdown = event => event.preventDefault();
  button.onclick = event => {
    event.stopPropagation();
    expanded = true;
    showExpandedCellEditor({
      workspace,
      column,
      value: input.value,
      allowModes,
      onApply: (value, mode) => { expanded = false; onApply(value, mode); },
      onCancel: () => { expanded = false; if (input.isConnected) input.focus(); }
    });
  };
  return () => expanded;
}
