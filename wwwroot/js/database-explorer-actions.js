(() => {
  const explorer = document.querySelector("[data-database-explorer]");
  if (!explorer) return;

  const $explorer = $(explorer);
  const menu = document.getElementById("database-context-menu");
  const modal = document.getElementById("database-action-modal");
  const modalCard = modal.querySelector(".database-modal-card");
  const form = document.getElementById("database-action-form");
  const fields = document.getElementById("database-action-fields");
  const error = document.getElementById("database-action-error");
  const submit = document.getElementById("database-action-submit");
  const toast = document.getElementById("database-toast");
  const token = document.querySelector("#database-antiforgery input[name='__RequestVerificationToken']")?.value;
  let contextNode = null;
  let menuTrigger = null;
  let modalTrigger = null;
  let modalSubmit = null;
  let metadataPromise = null;
  let longPressTimer = null;
  let longPressPoint = null;
  let suppressNextClick = false;

  const html = value => String(value ?? "").replace(/[&<>'"]/g, char => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;"
  })[char]);
  const bool = value => String(value).toLowerCase() === "true";
  const kindName = kind => ({
    schema: "Schema", table: "Table", partitionedtable: "PartitionedTable", foreigntable: "ForeignTable",
    view: "View", materializedview: "MaterializedView", sequence: "Sequence"
  })[kind] || "Table";
  const currentTarget = () => ({
    kind: contextNode?.dataset.nodeKind,
    schema: contextNode?.dataset.schema || "",
    name: contextNode?.dataset.table || contextNode?.dataset.name || "",
    tableMode: contextNode?.dataset.tableMode || "notApplicable",
    canOperate: bool(contextNode?.dataset.canOperate),
    canAdmin: bool(contextNode?.dataset.canAdmin),
    coordinator: bool(contextNode?.dataset.isCoordinator)
  });
  const getMetadata = () => metadataPromise ||= $.getJSON($explorer.data("metadata-url"));
  const problemText = xhr => {
    const body = xhr.responseJSON;
    if (body?.errors) return Object.values(body.errors).flat().join(" ");
    const state = body?.sqlState ? ` [${body.sqlState}]` : "";
    return `${body?.detail || "Yêu cầu database thất bại."}${state}`;
  };
  const showToast = message => {
    toast.textContent = message;
    toast.classList.remove("hidden");
    clearTimeout(showToast.timer);
    showToast.timer = setTimeout(() => toast.classList.add("hidden"), 3500);
  };

  const icon = (action, dangerous, hasChildren) => {
    const icons = {
      "query": ["blue", '<path d="m8 9-4 3 4 3M16 9l4 3-4 3M14 5l-4 14"/>'],
      "refresh": ["cyan", '<path d="M20 11a8 8 0 1 0-2.3 5.7L20 14"/><path d="M20 4v7h-7"/>'],
      "browse": ["blue", '<path d="M4 5h16v14H4zM4 9h16M9 9v10"/>'],
      "structure": ["purple", '<path d="M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z"/>'],
      "inspect-sequence": ["amber", '<path d="M4 6h4M4 12h7M4 18h10"/><path d="m16 8 4 4-4 4"/>'],
      "rename": ["blue", '<path d="m4 20 4.5-1 10-10-3.5-3.5-10 10L4 20Z"/><path d="m13.5 7 3.5 3.5"/>'],
      "edit-view": ["purple", '<path d="M3 5h18v14H3z"/><path d="m7 10 2 2-2 2M11 15h5"/>'],
      "convert": ["cyan", '<ellipse cx="12" cy="5" rx="7" ry="3"/><path d="M5 5v6c0 1.7 3.1 3 7 3s7-1.3 7-3V5"/><path d="m15 16 3 3 3-3M18 19v-6"/>'],
      "refresh-materialized": ["cyan", '<path d="M20 11a8 8 0 1 0-2.3 5.7L20 14"/><path d="M20 4v7h-7"/>'],
      "restart-sequence": ["amber", '<path d="M4 6h4M4 12h7M4 18h10"/><path d="M20 8v8M17 13l3 3 3-3"/>'],
      "truncate": ["red", '<path d="M4 6h16M7 10h10M9 14h6M11 18h2"/>'],
      "drop": ["red", '<path d="M3 6h18M8 6V4h8v2M6 6l1 15h10l1-15M10 10v7M14 10v7"/>'],
      "create-schema": ["amber", '<path d="M3 7h7l2 2h9v10H3V7Z"/><path d="M17 3v6M14 6h6"/>'],
      "create-table": ["green", '<path d="M4 5h16v14H4zM4 9h16M9 9v10"/><path d="M17 12v5M14.5 14.5h5"/>'],
      "create-view": ["purple", '<path d="M2.5 12s3.5-6 9.5-6 9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6Z"/><circle cx="12" cy="12" r="2.5"/><path d="M19 3v5M16.5 5.5h5"/>'],
      "create-sequence": ["amber", '<path d="M4 6h4M4 12h7M4 18h10"/><path d="M19 3v6M16 6h6"/>']
    };
    const selected = icons[action] || (dangerous ? icons.drop : null) || (hasChildren
      ? ["green", '<path d="M12 5v14M5 12h14"/>']
      : ["muted", '<circle cx="12" cy="12" r="8"/>']);
    return `<svg class="dg-icon-${selected[0]}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" aria-hidden="true">${selected[1]}</svg>`;
  };
  const item = (label, action, options = {}) => ({ label, action, ...options });
  const submenu = (label, children) => ({ label, children });
  const separator = () => ({ separator: true });

  function menuItems(target) {
    const readOnly = !target.coordinator;
    const create = target.canOperate && !readOnly;
    const admin = target.canAdmin && !readOnly;
    const newItems = [
      item("Schema", "create-schema"), item("Table", "create-table"),
      item("View", "create-view"), item("Sequence", "create-sequence")
    ];
    switch (target.kind) {
      case "database": return [
        ...(create ? [submenu("New", newItems), item("Query Console", "query", { shortcut: "Ctrl+Shift+Q" })] : []),
        item("Refresh", "refresh", { shortcut: "Ctrl+F5" }), ...(readOnly ? [separator(), item("Worker read-only", "noop", { disabled: true })] : [])
      ];
      case "schema": return [
        ...(create ? [submenu("New", newItems.slice(1)), item("Rename", "rename", { shortcut: "Shift+F6" })] : []),
        item("Refresh", "refresh", { shortcut: "Ctrl+F5" }), ...(admin ? [separator(), item("Drop schema", "drop", { dangerous: true, shortcut: "Delete" })] : [])
      ];
      case "table-category": return [...(create ? [item("New table", "create-table")] : []), item("Refresh", "refresh")];
      case "view-category": return [...(create ? [item("New view", "create-view")] : []), item("Refresh", "refresh")];
      case "sequence-category": return [...(create ? [item("New sequence", "create-sequence")] : []), item("Refresh", "refresh")];
      case "table":
      case "partitionedtable": return [
        item("Browse Data", "browse"), item("Modify / Structure", "structure", { shortcut: "Ctrl+F6" }), item("Refresh", "refresh", { shortcut: "Ctrl+F5" }),
        ...(create ? [separator(), item("Rename", "rename", { shortcut: "Shift+F6" }),
          ...(target.tableMode === "local" ? [item("Citus Convert", "convert")] : [])] : []),
        ...(admin ? [separator(), item("Truncate", "truncate", { dangerous: true }), item("Drop", "drop", { dangerous: true, shortcut: "Delete" })] : [])
      ];
      case "foreigntable": return [item("Browse Data", "browse"), item("Modify / Structure", "structure"), item("Refresh", "refresh"),
        ...(create ? [separator(), item("Rename", "rename", { shortcut: "Shift+F6" })] : []),
        ...(admin ? [separator(), item("Drop", "drop", { dangerous: true, shortcut: "Delete" })] : [])];
      case "view": return [item("Browse Data", "browse"), item("Modify / Structure", "structure"), item("Refresh", "refresh"),
        ...(create ? [separator(), item("Edit SQL", "edit-view"), item("Rename", "rename", { shortcut: "Shift+F6" })] : []),
        ...(admin ? [separator(), item("Drop", "drop", { dangerous: true, shortcut: "Delete" })] : [])];
      case "materializedview": return [item("Browse Data", "browse"), item("Modify / Structure", "structure"),
        ...(create ? [item("Refresh Data", "refresh-materialized"), separator(), item("Rename", "rename", { shortcut: "Shift+F6" })] : []),
        ...(admin ? [separator(), item("Drop", "drop", { dangerous: true, shortcut: "Delete" })] : [])];
      case "sequence": return [item("Inspect", "inspect-sequence"), item("Refresh", "refresh"),
        ...(create ? [separator(), item("Rename", "rename", { shortcut: "Shift+F6" })] : []),
        ...(admin ? [item("Restart", "restart-sequence", { dangerous: true }), separator(), item("Drop", "drop", { dangerous: true, shortcut: "Delete" })] : [])];
      default: return [item("Refresh", "refresh")];
    }
  }

  function renderMenu(items, root = true) {
    const container = document.createElement("div");
    container.className = root ? "database-context-list" : "database-context-submenu";
    container.setAttribute("role", "menu");
    for (const definition of items) {
      if (definition.separator) {
        const line = document.createElement("div");
        line.className = "database-context-separator";
        line.setAttribute("role", "separator");
        container.appendChild(line);
        continue;
      }
      const button = document.createElement("button");
      button.type = "button";
      button.className = `database-context-item${definition.dangerous ? " is-danger" : ""}`;
      button.setAttribute("role", "menuitem");
      button.tabIndex = -1;
      button.disabled = !!definition.disabled;
        button.innerHTML = `${icon(definition.action, definition.dangerous, !!definition.children)}<span>${html(definition.label)}</span>${definition.shortcut ? `<kbd>${html(definition.shortcut)}</kbd>` : ""}${definition.children ? '<span class="database-context-arrow">›</span>' : ""}`;
      if (definition.children) {
        button.setAttribute("aria-haspopup", "menu");
        button.setAttribute("aria-expanded", "false");
        const wrapper = document.createElement("div");
        wrapper.className = "database-context-submenu-wrap";
        wrapper.append(button, renderMenu(definition.children, false));
        container.appendChild(wrapper);
      } else {
        button.dataset.action = definition.action;
        container.appendChild(button);
      }
    }
    return container;
  }

  function positionMenu(x, y) {
    menu.classList.toggle("submenu-left", x > window.innerWidth - 480);
    menu.style.left = `${x}px`;
    menu.style.top = `${y}px`;
    menu.classList.remove("hidden");
    const rect = menu.getBoundingClientRect();
    menu.style.left = `${Math.max(8, Math.min(x, window.innerWidth - rect.width - 8))}px`;
    menu.style.top = `${Math.max(8, Math.min(y, window.innerHeight - rect.height - 8))}px`;
  }
  function openMenu(node, x, y) {
    closeMenu(false);
    contextNode = node;
    menuTrigger = node;
    document.querySelectorAll("[data-context-node].is-context-target").forEach(x => x.classList.remove("is-context-target"));
    node.classList.add("is-context-target");
    menu.replaceChildren(renderMenu(menuItems(currentTarget())));
    positionMenu(x, y);
    const first = menu.querySelector("[role=menuitem]:not(:disabled)");
    if (first) { first.tabIndex = 0; first.focus(); }
  }
  function closeMenu(restore = false) {
    menu.classList.add("hidden");
    contextNode?.classList.remove("is-context-target");
    if (restore) menuTrigger?.focus();
  }

  document.getElementById("database-tree-content")?.addEventListener("contextmenu", event => {
    const node = event.target.closest("[data-context-node]");
    if (!node) return;
    event.preventDefault();
    openMenu(node, event.clientX, event.clientY);
  });
  document.getElementById("database-tree-content")?.addEventListener("keydown", event => {
    const node = event.target.closest("[data-context-node]");
    if (!node || !(event.key === "ContextMenu" || (event.shiftKey && event.key === "F10"))) return;
    event.preventDefault();
    const rect = node.getBoundingClientRect();
    openMenu(node, rect.left + Math.min(rect.width, 36), rect.bottom);
  });
  document.getElementById("database-tree-content")?.addEventListener("pointerdown", event => {
    if (event.pointerType === "mouse") return;
    const node = event.target.closest("[data-context-node]");
    if (!node) return;
    longPressPoint = { x: event.clientX, y: event.clientY };
    longPressTimer = setTimeout(() => { suppressNextClick = true; openMenu(node, event.clientX, event.clientY); }, 550);
  });
  document.getElementById("database-tree-content")?.addEventListener("click", event => {
    if (!suppressNextClick) return;
    suppressNextClick = false;
    event.preventDefault(); event.stopImmediatePropagation();
  }, true);
  document.addEventListener("pointermove", event => {
    if (!longPressTimer || !longPressPoint) return;
    if (Math.hypot(event.clientX - longPressPoint.x, event.clientY - longPressPoint.y) > 8) {
      clearTimeout(longPressTimer); longPressTimer = null;
    }
  });
  document.addEventListener("pointerup", () => { clearTimeout(longPressTimer); longPressTimer = null; });

  menu.addEventListener("mouseover", event => {
    const parent = event.target.closest(".database-context-submenu-wrap");
    parent?.querySelector(":scope > .database-context-item")?.setAttribute("aria-expanded", "true");
  });
  menu.addEventListener("mouseout", event => {
    const parent = event.target.closest(".database-context-submenu-wrap");
    if (parent && !parent.contains(event.relatedTarget))
      parent.querySelector(":scope > .database-context-item")?.setAttribute("aria-expanded", "false");
  });
  menu.addEventListener("keydown", event => {
    const active = document.activeElement;
    if (!menu.contains(active)) return;
    const scope = active.closest("[role=menu]");
    const buttons = [...scope.querySelectorAll(":scope > .database-context-item:not(:disabled), :scope > .database-context-submenu-wrap > .database-context-item:not(:disabled)")];
    const index = buttons.indexOf(active);
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const next = (index + (event.key === "ArrowDown" ? 1 : -1) + buttons.length) % buttons.length;
      buttons.forEach(x => x.tabIndex = -1); buttons[next].tabIndex = 0; buttons[next].focus();
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault(); const next = event.key === "Home" ? buttons[0] : buttons.at(-1); next?.focus();
    } else if (event.key === "ArrowRight" && active.getAttribute("aria-haspopup") === "menu") {
      event.preventDefault(); active.setAttribute("aria-expanded", "true");
      active.parentElement.querySelector(".database-context-submenu [role=menuitem]:not(:disabled)")?.focus();
    } else if (event.key === "ArrowLeft" && scope.classList.contains("database-context-submenu")) {
      event.preventDefault(); const parent = scope.parentElement.querySelector(":scope > .database-context-item");
      parent.setAttribute("aria-expanded", "false"); parent.focus();
    } else if (event.key === "Escape") { event.preventDefault(); closeMenu(true); }
    else if ((event.key === "Enter" || event.key === " ") && active.matches("button")) { event.preventDefault(); active.click(); }
  });
  menu.addEventListener("click", event => {
    const submenuButton = event.target.closest("[aria-haspopup=menu]");
    if (submenuButton) {
      const expanded = submenuButton.getAttribute("aria-expanded") === "true";
      submenuButton.setAttribute("aria-expanded", String(!expanded));
      if (!expanded) submenuButton.parentElement.querySelector(".database-context-submenu [role=menuitem]:not(:disabled)")?.focus();
      return;
    }
    const button = event.target.closest("[data-action]");
    if (!button || button.disabled) return;
    const action = button.dataset.action;
    closeMenu(false);
    handleAction(action);
  });
  document.addEventListener("mousedown", event => { if (!menu.classList.contains("hidden") && !menu.contains(event.target)) closeMenu(false); });
  document.addEventListener("scroll", () => closeMenu(false), true);
  window.addEventListener("resize", () => closeMenu(false));

  function field(label, control, hint = "") {
    return `<label class="database-action-field"><span>${html(label)}</span>${control}${hint ? `<small>${html(hint)}</small>` : ""}</label>`;
  }
  function input(name, value = "", type = "text", attrs = "") {
    return `<input name="${name}" type="${type}" value="${html(value)}" ${attrs}/>`;
  }
  function schemaSelect(schemas, selected) {
    const values = schemas.length ? schemas : [selected || "public"];
    return `<select name="Schema">${values.map(x => `<option value="${html(x)}" ${x === selected ? "selected" : ""}>${html(x)}</option>`).join("")}</select>`;
  }
  function openModal({ title, eyebrow = "DATABASE ACTION", description = "", body, button = "Xác nhận", danger = false, variant = "compact", onSubmit }) {
    modalTrigger = menuTrigger;
    document.getElementById("database-action-title").textContent = title;
    document.getElementById("database-action-eyebrow").textContent = eyebrow;
    document.getElementById("database-action-description").textContent = description;
    fields.innerHTML = body;
    modalCard.classList.toggle("is-table-designer", variant === "table");
    modalCard.classList.toggle("is-destructive", danger);
    error.classList.add("hidden"); error.textContent = "";
    submit.textContent = button;
    submit.className = danger ? "btn btn-danger" : "btn btn-primary";
    submit.disabled = false; submit.removeAttribute("aria-busy");
    modalSubmit = onSubmit;
    modal.classList.remove("hidden");
    document.body.classList.add("database-modal-open");
    requestAnimationFrame(() => modal.querySelector("input:not([type=hidden]), select, textarea, button")?.focus());
  }
  function closeModal() {
    modal.classList.add("hidden");
    document.body.classList.remove("database-modal-open");
    modalSubmit = null;
    modalTrigger?.focus();
  }
  modal.querySelectorAll(".database-action-close,.database-action-cancel").forEach(x => x.addEventListener("click", closeModal));
  modal.addEventListener("mousedown", event => { if (event.target === modal) closeModal(); });
  modal.addEventListener("keydown", event => {
    if (event.key === "Escape") { event.preventDefault(); closeModal(); return; }
    if (event.key !== "Tab") return;
    const focusable = [...modal.querySelectorAll("button:not(:disabled),input:not(:disabled),select:not(:disabled),textarea:not(:disabled),[tabindex]:not([tabindex='-1'])")];
    if (!focusable.length) return;
    const first = focusable[0], last = focusable.at(-1);
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  });
  form.addEventListener("submit", async event => {
    event.preventDefault();
    if (!form.reportValidity() || !modalSubmit) return;
    error.classList.add("hidden");
    submit.disabled = true; submit.setAttribute("aria-busy", "true");
    try { await modalSubmit(); }
    catch (xhr) { error.textContent = problemText(xhr); error.classList.remove("hidden"); error.focus(); }
    finally { submit.disabled = false; submit.removeAttribute("aria-busy"); }
  });

  const post = (url, data) => $.ajax({ url, method: "POST", data: { __RequestVerificationToken: token, ...data } });
  const finish = async response => {
    closeModal();
    showToast(response.message || "Thao tác thành công.");
    await refreshTree(response.schema, response.name);
    if (response.redirectUrl) window.location.assign(response.redirectUrl);
  };
  async function refreshTree(selectSchema, selectName) {
    const expanded = [...document.querySelectorAll("[data-schema-group]")]
      .filter(x => x.querySelector(".database-schema-toggle")?.getAttribute("aria-expanded") === "true")
      .map(x => x.dataset.schemaName);
    const active = document.querySelector("[data-database-object].is-active");
    const schema = selectSchema ?? active?.dataset.schema;
    const name = selectName ?? active?.dataset.table;
    const tree = await $.get($explorer.data("tree-url"), {
      nodeId: $explorer.data("node-id") || null, showSystem: bool($explorer.data("show-system"))
    });
    document.getElementById("database-tree-content").innerHTML = tree;
    document.querySelectorAll("[data-schema-group]").forEach(group => {
      if (!expanded.includes(group.dataset.schemaName)) {
        group.querySelector(".database-schema-toggle")?.setAttribute("aria-expanded", "false");
        group.querySelector(".database-schema-items")?.classList.add("hidden");
      }
    });
    if (schema && name) {
      [...document.querySelectorAll("[data-database-object]")].find(x => x.dataset.schema === schema && x.dataset.table === name)?.classList.add("is-active");
    }
    $("#database-object-search").trigger("input");
  }

  function columnRowData(row) {
    return { name: row.dataset.columnName || "", dataType: row.dataset.columnType || "text", nullable: row.dataset.columnNullable !== "false",
      primaryKey: row.dataset.columnPrimary === "true", defaultLiteral: row.dataset.columnDefault || "", currentTimestamp: row.dataset.columnCurrentTimestamp === "true" };
  }
  function tableColumnRows() { return [...document.querySelectorAll(".database-column-row")]; }
  function tableColumnNames() { return tableColumnRows().map(row => columnRowData(row).name.trim()).filter(Boolean); }
  function renderColumnSummary(row) {
    const column = columnRowData(row);
    row.innerHTML = `<span class="dg-column-icon ${column.primaryKey ? "is-primary" : ""}" aria-hidden="true"></span><span><strong>${html(column.name || "column_name")}</strong><small>${html(column.dataType)}${column.nullable ? "" : " · not null"}</small></span>${column.primaryKey ? '<span class="dg-key-kind-badge">PK</span>' : ""}`;
  }
  function refreshColumnCount() {
    document.getElementById("database-column-count")?.replaceChildren(document.createTextNode(String(tableColumnRows().length)));
  }
  function selectColumnRow(row) {
    document.querySelector("[data-designer-section='columns']")?.click();
    tableColumnRows().forEach(item => { item.classList.toggle("is-active", item === row); item.setAttribute("aria-selected", item === row ? "true" : "false"); });
    document.getElementById("database-column-empty")?.classList.add("hidden");
    document.getElementById("database-column-editor")?.classList.remove("hidden");
    const column = columnRowData(row);
    document.getElementById("database-column-name").value = column.name;
    document.getElementById("database-column-type").value = column.dataType;
    document.getElementById("database-column-nullable").checked = column.nullable;
    document.getElementById("database-column-primary").checked = column.primaryKey;
    document.getElementById("database-column-default").value = column.defaultLiteral;
    document.getElementById("database-column-now").checked = column.currentTimestamp;
    document.querySelector("[data-column-editor-title]").textContent = column.name || "column_name";
    refreshColumnControls();
  }
  function activeColumnRow() { return document.querySelector(".database-column-row.is-active"); }
  function renameColumnReferences(previousName, nextName) {
    if (!previousName || previousName === nextName) return;
    document.querySelectorAll(".dg-key-row").forEach(row => { const key = keyRowData(row); key.columns = key.columns.map(column => column === previousName ? nextName : column); row.dataset.keyColumns = JSON.stringify(key.columns); });
    document.querySelectorAll(".dg-index-row").forEach(row => { const index = indexRowData(row); index.columns = index.columns.map(column => column === previousName ? nextName : column); row.dataset.indexColumns = JSON.stringify(index.columns); });
    document.querySelectorAll(".dg-foreign-key-row").forEach(row => { const fk = foreignKeyRowData(row); fk.mappings = fk.mappings.map(mapping => ({ ...mapping, local: mapping.local === previousName ? nextName : mapping.local })); row.dataset.foreignKeyMappings = JSON.stringify(fk.mappings); });
  }
  function updateColumnFromEditor() {
    const row = activeColumnRow();
    if (!row) return;
    const previousName = row.dataset.columnName || "", nextName = document.getElementById("database-column-name").value.trim();
    renameColumnReferences(previousName, nextName);
    row.dataset.columnName = nextName;
    row.dataset.columnType = document.getElementById("database-column-type").value;
    row.dataset.columnNullable = document.getElementById("database-column-nullable").checked;
    row.dataset.columnPrimary = document.getElementById("database-column-primary").checked;
    row.dataset.columnDefault = document.getElementById("database-column-default").value;
    row.dataset.columnCurrentTimestamp = document.getElementById("database-column-now").checked;
    if (row.dataset.columnPrimary === "true") {
      document.querySelectorAll(".dg-key-row[data-key-kind='Primary']").forEach(keyRow => { keyRow.dataset.keyKind = "Unique"; renderKeySummary(keyRow); });
      const activeKey = activeKeyRow(); if (activeKey) selectKeyRow(activeKey);
    }
    renderColumnSummary(row);
    document.querySelector("[data-column-editor-title]").textContent = row.dataset.columnName || "column_name";
    syncDistributionColumns(); updateAutoObjectNames(); updateTableSqlPreview();
  }
  function refreshColumnControls() {
    const rows = tableColumnRows(), active = activeColumnRow(), index = rows.indexOf(active);
    const disable = (id, value) => { const button = document.getElementById(id); if (button) button.disabled = value; };
    disable("database-remove-column", !active || rows.length <= 1);
    disable("database-move-column-up", index <= 0);
    disable("database-move-column-down", index < 0 || index >= rows.length - 1);
  }
  function moveColumn(direction) {
    const row = activeColumnRow(); if (!row) return;
    const sibling = direction < 0 ? row.previousElementSibling : row.nextElementSibling;
    if (!sibling?.classList.contains("database-column-row")) return;
    row.parentElement.insertBefore(row, direction < 0 ? sibling : sibling.nextElementSibling);
    refreshColumnControls(); updateTableSqlPreview();
  }
  function removeActiveColumn() {
    const row = activeColumnRow(); if (!row || tableColumnRows().length <= 1) return;
    const next = row.nextElementSibling || row.previousElementSibling;
    row.remove();
    if (next?.classList.contains("database-column-row")) selectColumnRow(next);
    refreshColumnCount(); syncDistributionColumns(); updateAutoObjectNames(); updateTableSqlPreview();
  }
  function addColumnRow(metadata, initial = {}) {
    const row = document.createElement("button");
    row.type = "button";
    row.className = "database-column-row dg-tree-object-row";
    row.setAttribute("role", "option");
    row.dataset.columnName = initial.name || "";
    row.dataset.columnType = initial.dataType || metadata.columnTypes[0]?.name || "text";
    row.dataset.columnNullable = initial.nullable === false ? "false" : "true";
    row.dataset.columnPrimary = initial.primaryKey ? "true" : "false";
    row.dataset.columnDefault = initial.defaultLiteral || "";
    row.dataset.columnCurrentTimestamp = initial.currentTimestamp ? "true" : "false";
    renderColumnSummary(row);
    row.addEventListener("click", () => selectColumnRow(row));
    row.addEventListener("keydown", event => {
      if (event.key === "Delete") { event.preventDefault(); selectColumnRow(row); removeActiveColumn(); return; }
      if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
      event.preventDefault(); const sibling = event.key === "ArrowUp" ? row.previousElementSibling : row.nextElementSibling;
      if (sibling?.classList.contains("database-column-row")) { selectColumnRow(sibling); sibling.focus(); }
    });
    document.getElementById("database-column-list").appendChild(row);
    selectColumnRow(row); refreshColumnCount(); syncDistributionColumns(); updateAutoObjectNames(); updateTableSqlPreview();
  }
  function syncDistributionColumns() {
    const select = document.querySelector("[name=DistributionColumn]");
    if (!select) return;
    const current = select.value;
    const names = tableColumnNames();
    select.innerHTML = `<option value="">Chọn column…</option>${names.map(x => `<option value="${html(x)}">${html(x)}</option>`).join("")}`;
    if (names.includes(current)) select.value = current;
    syncDesignerColumnSelectors(names);
  }

  function syncDesignerColumnSelectors(names = tableColumnNames()) {
    document.querySelectorAll(".dg-key-row").forEach(row => { row.dataset.keyColumns = JSON.stringify(keyRowData(row).columns.filter(column => names.includes(column))); renderKeySummary(row); });
    document.querySelectorAll(".dg-index-row").forEach(row => { row.dataset.indexColumns = JSON.stringify(indexRowData(row).columns.filter(column => names.includes(column))); renderIndexSummary(row); });
    document.querySelectorAll(".dg-foreign-key-row").forEach(row => { const fk = foreignKeyRowData(row); fk.mappings = fk.mappings.filter(mapping => names.includes(mapping.local)); row.dataset.foreignKeyMappings = JSON.stringify(fk.mappings); renderForeignKeySummary(row); });
    const activeKey = activeKeyRow(); if (activeKey) renderKeyColumns(activeKey);
    const activeIndex = activeIndexRow(); if (activeIndex) renderIndexColumns(activeIndex);
    const activeFk = activeForeignKeyRow(); if (activeFk) renderForeignKeyMappings(activeFk);
  }
  function truncatePgIdentifier(value, maxBytes = 63) {
    const encoder = new TextEncoder(); let result = "";
    for (const character of value) { if (encoder.encode(result + character).length > maxBytes) break; result += character; }
    return result;
  }
  function autoObjectBase(columns) {
    const table = form.elements.Name?.value.trim() || "table";
    const parts = [table, ...columns.filter(Boolean)].map(value => value.replace(/[^\p{L}\p{N}_$]/gu, "_").replace(/_+/g, "_").replace(/^_|_$/g, "")).filter(Boolean);
    return truncatePgIdentifier(parts.join("_") || "table_object");
  }
  function wireObjectTreeKeyboard(row, className, selectRow, removeRow) {
    row.addEventListener("keydown", event => {
      if (event.key === "Delete") { event.preventDefault(); selectRow(row); removeRow(); return; }
      if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
      event.preventDefault(); const sibling = event.key === "ArrowUp" ? row.previousElementSibling : row.nextElementSibling;
      if (sibling?.classList.contains(className)) { selectRow(sibling); sibling.focus(); }
    });
  }
  function uniqueAutoObjectName(base, current) {
    const rows = [...document.querySelectorAll(".dg-key-row, .dg-index-row")].filter(row => row !== current);
    const used = new Set(rows.map(row => row.dataset.keyName || row.dataset.indexName).filter(Boolean));
    if (!used.has(base)) return base;
    const suffix = current.classList.contains("dg-index-row") ? "_idx" : "_key";
    const candidate = `${truncatePgIdentifier(base, 63 - suffix.length)}${suffix}`;
    if (!used.has(candidate)) return candidate;
    let number = 2;
    while (used.has(`${truncatePgIdentifier(candidate, 62 - String(number).length)}_${number}`)) number++;
    return `${truncatePgIdentifier(candidate, 62 - String(number).length)}_${number}`;
  }
  function updateAutoObjectNames() {
    document.querySelectorAll(".dg-key-row").forEach(row => {
      if (row.dataset.keyAutoName !== "false") row.dataset.keyName = uniqueAutoObjectName(autoObjectBase(keyRowData(row).columns), row);
      renderKeySummary(row);
    });
    document.querySelectorAll(".dg-index-row").forEach(row => {
      if (row.dataset.indexAutoName !== "false") row.dataset.indexName = uniqueAutoObjectName(autoObjectBase(indexRowData(row).columns), row);
      renderIndexSummary(row);
    });
    const activeKey = activeKeyRow();
    if (activeKey && activeKey.dataset.keyAutoName !== "false") {
      document.getElementById("database-key-name").value = activeKey.dataset.keyName;
      document.querySelector("[data-key-editor-title]").textContent = activeKey.dataset.keyName;
    }
    const activeIndex = activeIndexRow();
    if (activeIndex && activeIndex.dataset.indexAutoName !== "false") {
      document.getElementById("database-index-name").value = activeIndex.dataset.indexName;
      document.querySelector("[data-index-editor-title]").textContent = activeIndex.dataset.indexName;
    }
    refreshKeyCount();
  }
  function keyRowData(row) {
    let columns = [];
    try { columns = JSON.parse(row.dataset.keyColumns || "[]"); } catch { columns = []; }
    return { name: row.dataset.keyName || "", kind: row.dataset.keyKind || "Unique", columns };
  }
  function renderKeySummary(row) {
    const key = keyRowData(row);
    const fallback = key.kind === "Primary" ? "primary_key" : "unique_key";
    row.innerHTML = `<span class="dg-key-icon" aria-hidden="true"></span><span><strong>${html(key.name || fallback)}</strong><small>${html(key.columns.join(", ") || "No columns")}</small></span><span class="dg-key-kind-badge">${key.kind === "Primary" ? "PK" : "UQ"}</span>`;
  }
  function refreshKeyCount() {
    const rows = [...document.querySelectorAll(".dg-key-row")];
    const count = rows.length;
    document.getElementById("database-key-count")?.replaceChildren(document.createTextNode(String(count)));
    const mobileSelect = document.getElementById("database-key-mobile-select");
    if (mobileSelect) {
      const activeIndex = rows.indexOf(activeKeyRow());
      mobileSelect.innerHTML = rows.length
        ? rows.map((row, index) => `<option value="${index}" ${index === activeIndex ? "selected" : ""}>${html(keyRowData(row).name || `Key ${index + 1}`)}</option>`).join("")
        : '<option value="">No keys</option>';
      mobileSelect.disabled = !rows.length;
    }
  }
  function refreshKeyControls() {
    const row = activeKeyRow(), selected = selectedKeyColumnRow();
    const key = row ? keyRowData(row) : { columns: [] };
    const names = tableColumnNames();
    const setDisabled = (id, disabled) => { const button = document.getElementById(id); if (button) button.disabled = disabled; };
    setDisabled("database-remove-key", !row);
    setDisabled("database-add-key-column", !row || key.columns.length >= names.length);
    setDisabled("database-remove-key-column", !selected);
    const index = selected ? Number(selected.dataset.keyColumnIndex) : -1;
    setDisabled("database-move-key-column-up", index <= 0);
    setDisabled("database-move-key-column-down", index < 0 || index >= key.columns.length - 1);
  }
  function selectKeyRow(row) {
    const keysTab = document.querySelector("[data-designer-section='keys']");
    if (keysTab && !keysTab.classList.contains("is-active")) keysTab.click();
    document.querySelectorAll(".dg-key-row").forEach(item => { item.classList.toggle("is-active", item === row); item.setAttribute("aria-selected", item === row ? "true" : "false"); });
    refreshKeyCount();
    document.getElementById("database-key-empty")?.classList.add("hidden");
    const editor = document.getElementById("database-key-editor");
    if (!editor) return;
    editor.classList.remove("hidden");
    const key = keyRowData(row);
    document.getElementById("database-key-name").value = key.name;
    const autoName = row.dataset.keyAutoName !== "false";
    document.getElementById("database-key-auto-name").checked = autoName;
    document.getElementById("database-key-name").readOnly = autoName;
    document.querySelector(`[name=KeyKindEditor][value=${key.kind}]`).checked = true;
    document.querySelector("[data-key-editor-title]").textContent = key.name || (key.kind === "Primary" ? "primary_key" : "unique_key");
    renderKeyColumns(row);
    refreshKeyControls();
  }
  function activeKeyRow() { return document.querySelector(".dg-key-row.is-active"); }
  function renderKeyColumns(row) {
    const list = document.getElementById("database-key-column-list");
    if (!list) return;
    const columns = keyRowData(row).columns;
    list.innerHTML = columns.length ? columns.map((column, index) => `<button type="button" class="dg-key-column-row ${index === 0 ? "is-active" : ""}" data-key-column-index="${index}" role="option" aria-selected="${index === 0}"><span>${index + 1}</span><strong>${html(column)}</strong></button>`).join("") : '<div class="dg-key-column-empty">Add at least one column</div>';
    list.querySelectorAll(".dg-key-column-row").forEach(item => {
      item.addEventListener("click", () => selectKeyColumn(item));
      item.addEventListener("keydown", event => {
        if (event.key === "Delete") { event.preventDefault(); removeKeyColumn(); return; }
        if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
        event.preventDefault();
        const sibling = event.key === "ArrowUp" ? item.previousElementSibling : item.nextElementSibling;
        if (sibling?.classList.contains("dg-key-column-row")) { selectKeyColumn(sibling); sibling.focus(); }
      });
    });
    const first = list.querySelector(".dg-key-column-row");
    if (first) selectKeyColumn(first); else syncKeyColumnEditor();
  }
  function selectedKeyColumnRow() { return document.querySelector(".dg-key-column-row.is-active"); }
  function selectKeyColumn(item) {
    document.querySelectorAll(".dg-key-column-row").forEach(row => { row.classList.toggle("is-active", row === item); row.setAttribute("aria-selected", row === item ? "true" : "false"); });
    syncKeyColumnEditor();
    refreshKeyControls();
  }
  function syncKeyColumnEditor() {
    const select = document.getElementById("database-key-column-name");
    if (!select) return;
    const names = tableColumnNames();
    const active = activeKeyRow();
    const selected = selectedKeyColumnRow();
    const value = active && selected ? keyRowData(active).columns[Number(selected.dataset.keyColumnIndex)] : "";
    select.innerHTML = `<option value="">Select column…</option>${names.map(name => `<option value="${html(name)}" ${name === value ? "selected" : ""}>${html(name)}</option>`).join("")}`;
    select.disabled = !selected;
  }
  function updateKeyFromEditor() {
    const row = activeKeyRow();
    if (!row) return;
    row.dataset.keyName = document.getElementById("database-key-name").value.trim();
    row.dataset.keyKind = document.querySelector("[name=KeyKindEditor]:checked").value;
    if (row.dataset.keyKind === "Primary") {
      document.querySelectorAll(".dg-key-row").forEach(other => {
        if (other !== row && other.dataset.keyKind === "Primary") { other.dataset.keyKind = "Unique"; renderKeySummary(other); }
      });
      tableColumnRows().forEach(columnRow => { columnRow.dataset.columnPrimary = "false"; renderColumnSummary(columnRow); });
    }
    if (row.dataset.keyAutoName !== "false") row.dataset.keyName = uniqueAutoObjectName(autoObjectBase(keyRowData(row).columns), row);
    renderKeySummary(row);
    refreshKeyCount();
    document.querySelector("[data-key-editor-title]").textContent = row.dataset.keyName || (row.dataset.keyKind === "Primary" ? "primary_key" : "unique_key");
    updateTableSqlPreview();
  }
  function addKeyColumn() {
    const row = activeKeyRow();
    if (!row) return;
    const key = keyRowData(row);
    const names = tableColumnNames();
    const next = names.find(name => !key.columns.includes(name));
    if (!next) return;
    key.columns.push(next);
    row.dataset.keyColumns = JSON.stringify(key.columns);
    updateAutoObjectNames(); renderKeyColumns(row); renderKeySummary(row); updateTableSqlPreview();
  }
  function removeKeyColumn() {
    const row = activeKeyRow(), selected = selectedKeyColumnRow();
    if (!row || !selected) return;
    const key = keyRowData(row);
    key.columns.splice(Number(selected.dataset.keyColumnIndex), 1);
    row.dataset.keyColumns = JSON.stringify(key.columns);
    updateAutoObjectNames(); renderKeyColumns(row); renderKeySummary(row); updateTableSqlPreview();
  }
  function moveKeyColumn(direction) {
    const row = activeKeyRow(), selected = selectedKeyColumnRow();
    if (!row || !selected) return;
    const key = keyRowData(row), from = Number(selected.dataset.keyColumnIndex), to = from + direction;
    if (to < 0 || to >= key.columns.length) return;
    [key.columns[from], key.columns[to]] = [key.columns[to], key.columns[from]];
    row.dataset.keyColumns = JSON.stringify(key.columns);
    updateAutoObjectNames();
    renderKeyColumns(row);
    selectKeyColumn(document.querySelector(`.dg-key-column-row[data-key-column-index='${to}']`));
    renderKeySummary(row); updateTableSqlPreview();
  }
  function addKeyRow(initial = {}) {
    const table = form.elements.Name?.value.trim() || "table";
    const hasPrimary = tableColumnRows().some(row => columnRowData(row).primaryKey) || document.querySelector(".dg-key-row[data-key-kind='Primary']");
    const kind = initial.kind || (hasPrimary ? "Unique" : "Primary");
    const names = tableColumnNames();
    const row = document.createElement("button");
    row.type = "button";
    row.className = "dg-key-row";
    row.setAttribute("role", "option");
    row.dataset.keyAutoName = initial.name ? "false" : "true";
    row.dataset.keyName = initial.name || "";
    row.dataset.keyKind = kind;
    row.dataset.keyColumns = JSON.stringify(initial.columns || names.slice(0, 1));
    if (row.dataset.keyAutoName !== "false") row.dataset.keyName = uniqueAutoObjectName(autoObjectBase(JSON.parse(row.dataset.keyColumns)), row);
    renderKeySummary(row);
    row.addEventListener("click", () => selectKeyRow(row));
    row.addEventListener("keydown", event => {
      if (event.key === "Delete") { event.preventDefault(); selectKeyRow(row); removeActiveKey(); return; }
      if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
      event.preventDefault();
      const sibling = event.key === "ArrowUp" ? row.previousElementSibling : row.nextElementSibling;
      if (sibling?.classList.contains("dg-key-row")) { selectKeyRow(sibling); sibling.focus(); }
    });
    document.getElementById("database-key-list").appendChild(row);
    selectKeyRow(row); refreshKeyCount(); updateTableSqlPreview();
    document.getElementById("database-key-column-name").focus();
  }
  function removeActiveKey() {
    const row = activeKeyRow();
    if (!row) return;
    const next = row.nextElementSibling || row.previousElementSibling;
    row.remove(); refreshKeyCount();
    if (next?.classList.contains("dg-key-row")) { selectKeyRow(next); next.focus(); }
    else { document.getElementById("database-key-editor")?.classList.add("hidden"); document.getElementById("database-key-empty")?.classList.remove("hidden"); }
    refreshKeyControls(); updateTableSqlPreview();
  }
  function referentialOptions(selected = "NoAction") {
    return [["NoAction", "NO ACTION"], ["Restrict", "RESTRICT"], ["Cascade", "CASCADE"], ["SetNull", "SET NULL"], ["SetDefault", "SET DEFAULT"]]
      .map(([value, label]) => `<option value="${value}" ${value === selected ? "selected" : ""}>${label}</option>`).join("");
  }
  function foreignKeyRowData(row) {
    let mappings = []; try { mappings = JSON.parse(row.dataset.foreignKeyMappings || "[]"); } catch { mappings = []; }
    return { name: row.dataset.foreignKeyName || "", referencedSchema: row.dataset.referencedSchema || "public", referencedTable: row.dataset.referencedTable || "",
      onUpdate: row.dataset.onUpdate || "NoAction", onDelete: row.dataset.onDelete || "NoAction", mappings };
  }
  function renderForeignKeySummary(row) {
    const fk = foreignKeyRowData(row), locals = fk.mappings.map(mapping => mapping.local).filter(Boolean);
    row.innerHTML = `<span class="dg-foreign-key-icon" aria-hidden="true"></span><span><strong>${html(fk.name || "foreign_key")}</strong><small>${html(locals.join(", ") || "No columns")} → ${html(fk.referencedSchema)}.${html(fk.referencedTable || "table")}</small></span>`;
  }
  function activeForeignKeyRow() { return document.querySelector(".dg-foreign-key-row.is-active"); }
  function refreshForeignKeyCount() { document.getElementById("database-foreign-key-count")?.replaceChildren(document.createTextNode(String(document.querySelectorAll(".dg-foreign-key-row").length))); }
  function selectForeignKeyRow(row) {
    document.querySelector("[data-designer-section='foreign-keys']")?.click();
    document.querySelectorAll(".dg-foreign-key-row").forEach(item => item.classList.toggle("is-active", item === row));
    document.getElementById("database-foreign-key-empty")?.classList.add("hidden"); document.getElementById("database-foreign-key-editor")?.classList.remove("hidden");
    const fk = foreignKeyRowData(row);
    document.getElementById("database-foreign-key-name").value = fk.name; document.getElementById("database-fk-schema").value = fk.referencedSchema;
    document.getElementById("database-fk-table").value = fk.referencedTable; document.getElementById("database-fk-on-update").value = fk.onUpdate; document.getElementById("database-fk-on-delete").value = fk.onDelete;
    document.querySelector("[data-foreign-key-editor-title]").textContent = fk.name || "foreign_key"; renderForeignKeyMappings(row);
    document.getElementById("database-remove-foreign-key").disabled = false;
  }
  function updateForeignKeyFromEditor() {
    const row = activeForeignKeyRow(); if (!row) return;
    row.dataset.foreignKeyName = document.getElementById("database-foreign-key-name").value.trim(); row.dataset.referencedSchema = document.getElementById("database-fk-schema").value;
    row.dataset.referencedTable = document.getElementById("database-fk-table").value.trim(); row.dataset.onUpdate = document.getElementById("database-fk-on-update").value; row.dataset.onDelete = document.getElementById("database-fk-on-delete").value;
    renderForeignKeySummary(row); document.querySelector("[data-foreign-key-editor-title]").textContent = row.dataset.foreignKeyName || "foreign_key"; updateTableSqlPreview();
  }
  function renderForeignKeyMappings(row) {
    const list = document.getElementById("database-fk-mapping-list"), mappings = foreignKeyRowData(row).mappings;
    list.innerHTML = mappings.length ? mappings.map((mapping, index) => `<div class="dg-fk-mapping-row" data-fk-mapping-index="${index}"><span>${index + 1}</span><select data-fk-local aria-label="Local column">${tableColumnNames().map(name => `<option value="${html(name)}" ${name === mapping.local ? "selected" : ""}>${html(name)}</option>`).join("")}</select><span class="dg-mapping-arrow">→</span><input data-fk-referenced value="${html(mapping.referenced || "")}" maxlength="63" placeholder="id" aria-label="Referenced column"><button type="button" data-remove-fk-mapping aria-label="Remove mapping">×</button></div>`).join("") : '<div class="dg-key-column-empty">Add at least one column mapping</div>';
    list.querySelectorAll(".dg-fk-mapping-row").forEach(mappingRow => {
      mappingRow.addEventListener("input", updateForeignKeyMappings); mappingRow.addEventListener("change", updateForeignKeyMappings);
      mappingRow.querySelector("[data-remove-fk-mapping]").addEventListener("click", () => { mappingRow.remove(); updateForeignKeyMappings(); });
    });
  }
  function updateForeignKeyMappings() {
    const row = activeForeignKeyRow(); if (!row) return;
    row.dataset.foreignKeyMappings = JSON.stringify([...document.querySelectorAll(".dg-fk-mapping-row")].map(mappingRow => ({ local: mappingRow.querySelector("[data-fk-local]").value, referenced: mappingRow.querySelector("[data-fk-referenced]").value.trim() })));
    renderForeignKeySummary(row); updateTableSqlPreview();
  }
  function addForeignKeyRow(metadata, initial = {}) {
    const row = document.createElement("button"); row.type = "button"; row.className = "dg-foreign-key-row dg-tree-object-row"; row.setAttribute("role", "option");
    row.dataset.foreignKeyName = initial.name || ""; row.dataset.referencedSchema = initial.referencedSchema || metadata.schemas[0] || "public"; row.dataset.referencedTable = initial.referencedTable || "";
    row.dataset.onUpdate = initial.onUpdate || "NoAction"; row.dataset.onDelete = initial.onDelete || "NoAction";
    row.dataset.foreignKeyMappings = JSON.stringify(initial.mappings || tableColumnNames().slice(0, 1).map(local => ({ local, referenced: "id" })));
    renderForeignKeySummary(row); row.addEventListener("click", () => selectForeignKeyRow(row)); wireObjectTreeKeyboard(row, "dg-foreign-key-row", selectForeignKeyRow, removeActiveForeignKey); document.getElementById("database-foreign-key-list").appendChild(row);
    selectForeignKeyRow(row); refreshForeignKeyCount(); updateTableSqlPreview(); document.getElementById("database-foreign-key-name").focus();
  }
  function removeActiveForeignKey() {
    const row = activeForeignKeyRow(); if (!row) return; const next = row.nextElementSibling || row.previousElementSibling; row.remove();
    if (next?.classList.contains("dg-foreign-key-row")) selectForeignKeyRow(next); else { document.getElementById("database-foreign-key-editor")?.classList.add("hidden"); document.getElementById("database-foreign-key-empty")?.classList.remove("hidden"); }
    refreshForeignKeyCount(); updateTableSqlPreview();
    document.getElementById("database-remove-foreign-key").disabled = !activeForeignKeyRow();
  }
  function indexRowData(row) {
    let columns = []; try { columns = JSON.parse(row.dataset.indexColumns || "[]"); } catch { columns = []; }
    return { name: row.dataset.indexName || "", unique: row.dataset.indexUnique === "true", method: row.dataset.indexMethod || "Btree", columns };
  }
  function renderIndexSummary(row) {
    const index = indexRowData(row);
    row.innerHTML = `<span class="dg-index-icon" aria-hidden="true"></span><span><strong>${html(index.name || "index")}</strong><small>${html(index.columns.join(", ") || "No columns")} · ${html(index.method.toLowerCase())}</small></span>${index.unique ? '<span class="dg-key-kind-badge">UQ</span>' : ""}`;
  }
  function activeIndexRow() { return document.querySelector(".dg-index-row.is-active"); }
  function refreshIndexCount() { document.getElementById("database-index-count")?.replaceChildren(document.createTextNode(String(document.querySelectorAll(".dg-index-row").length))); }
  function selectIndexRow(row) {
    document.querySelector("[data-designer-section='indexes']")?.click();
    document.querySelectorAll(".dg-index-row").forEach(item => { item.classList.toggle("is-active", item === row); item.setAttribute("aria-selected", item === row ? "true" : "false"); });
    document.getElementById("database-index-empty")?.classList.add("hidden"); document.getElementById("database-index-editor")?.classList.remove("hidden");
    const index = indexRowData(row), auto = row.dataset.indexAutoName !== "false";
    document.getElementById("database-index-name").value = index.name; document.getElementById("database-index-name").readOnly = auto;
    document.getElementById("database-index-auto-name").checked = auto; document.getElementById("database-index-method").value = index.method;
    document.getElementById("database-index-unique").checked = index.unique; document.querySelector("[data-index-editor-title]").textContent = index.name;
    renderIndexColumns(row);
    document.getElementById("database-remove-index").disabled = false;
  }
  function updateIndexFromEditor() {
    const row = activeIndexRow(); if (!row) return;
    row.dataset.indexName = document.getElementById("database-index-name").value.trim(); row.dataset.indexMethod = document.getElementById("database-index-method").value;
    row.dataset.indexUnique = document.getElementById("database-index-unique").checked;
    if (row.dataset.indexAutoName !== "false") row.dataset.indexName = uniqueAutoObjectName(autoObjectBase(indexRowData(row).columns), row);
    renderIndexSummary(row); document.querySelector("[data-index-editor-title]").textContent = row.dataset.indexName; updateTableSqlPreview();
  }
  function renderIndexColumns(row) {
    const list = document.getElementById("database-index-column-list"), columns = indexRowData(row).columns;
    list.innerHTML = columns.length ? columns.map((column, index) => `<button type="button" class="dg-index-column-row dg-key-column-row ${index === 0 ? "is-active" : ""}" data-index-column-index="${index}" role="option"><span>${index + 1}</span><strong>${html(column)}</strong></button>`).join("") : '<div class="dg-key-column-empty">Add at least one column</div>';
    list.querySelectorAll(".dg-index-column-row").forEach(item => item.addEventListener("click", () => {
      list.querySelectorAll(".dg-index-column-row").forEach(other => other.classList.toggle("is-active", other === item)); syncIndexColumnEditor();
    }));
    syncIndexColumnEditor();
  }
  function syncIndexColumnEditor() {
    const row = activeIndexRow(), selected = document.querySelector(".dg-index-column-row.is-active"), select = document.getElementById("database-index-column-name");
    const value = row && selected ? indexRowData(row).columns[Number(selected.dataset.indexColumnIndex)] : "";
    select.innerHTML = `<option value="">Select column…</option>${tableColumnNames().map(name => `<option value="${html(name)}" ${name === value ? "selected" : ""}>${html(name)}</option>`).join("")}`;
    select.disabled = !selected;
  }
  function mutateIndexColumns(action) {
    const row = activeIndexRow(); if (!row) return;
    const index = indexRowData(row), selected = document.querySelector(".dg-index-column-row.is-active"), position = selected ? Number(selected.dataset.indexColumnIndex) : -1;
    if (action === "add") { const next = tableColumnNames().find(name => !index.columns.includes(name)); if (next) index.columns.push(next); }
    if (action === "remove" && position >= 0) index.columns.splice(position, 1);
    if ((action === "up" || action === "down") && position >= 0) { const target = position + (action === "up" ? -1 : 1); if (target >= 0 && target < index.columns.length) [index.columns[position], index.columns[target]] = [index.columns[target], index.columns[position]]; }
    row.dataset.indexColumns = JSON.stringify(index.columns); updateAutoObjectNames(); renderIndexColumns(row); renderIndexSummary(row); updateTableSqlPreview();
  }
  function addIndexRow(initial = {}) {
    const row = document.createElement("button"); row.type = "button"; row.className = "dg-index-row dg-tree-object-row"; row.setAttribute("role", "option");
    row.dataset.indexAutoName = initial.name ? "false" : "true"; row.dataset.indexName = initial.name || ""; row.dataset.indexUnique = initial.unique ? "true" : "false";
    row.dataset.indexMethod = initial.method || "Btree"; row.dataset.indexColumns = JSON.stringify(initial.columns || tableColumnNames().slice(0, 1));
    if (row.dataset.indexAutoName !== "false") row.dataset.indexName = uniqueAutoObjectName(autoObjectBase(indexRowData(row).columns), row);
    renderIndexSummary(row); row.addEventListener("click", () => selectIndexRow(row)); wireObjectTreeKeyboard(row, "dg-index-row", selectIndexRow, removeActiveIndex); document.getElementById("database-index-list").appendChild(row);
    selectIndexRow(row); refreshIndexCount(); updateAutoObjectNames(); updateTableSqlPreview(); document.getElementById("database-index-column-name").focus();
  }
  function removeActiveIndex() {
    const row = activeIndexRow(); if (!row) return; const next = row.nextElementSibling || row.previousElementSibling; row.remove();
    if (next?.classList.contains("dg-index-row")) selectIndexRow(next); else { document.getElementById("database-index-editor")?.classList.add("hidden"); document.getElementById("database-index-empty")?.classList.remove("hidden"); }
    refreshIndexCount(); updateAutoObjectNames(); updateTableSqlPreview();
    document.getElementById("database-remove-index").disabled = !activeIndexRow();
  }
  function checkRowData(row) { return { name: row.dataset.checkName || "", expression: row.dataset.checkExpression || "" }; }
  function renderCheckSummary(row) {
    const check = checkRowData(row);
    row.innerHTML = `<span class="dg-check-icon" aria-hidden="true"></span><span><strong>${html(check.name || "check")}</strong><small>${html(check.expression || "No expression")}</small></span>`;
  }
  function activeCheckRow() { return document.querySelector(".dg-check-row.is-active"); }
  function refreshCheckCount() { document.getElementById("database-check-count")?.replaceChildren(document.createTextNode(String(document.querySelectorAll(".dg-check-row").length))); }
  function selectCheckRow(row) {
    document.querySelector("[data-designer-section='checks']")?.click(); document.querySelectorAll(".dg-check-row").forEach(item => item.classList.toggle("is-active", item === row));
    document.getElementById("database-check-empty")?.classList.add("hidden"); document.getElementById("database-check-editor")?.classList.remove("hidden");
    const check = checkRowData(row); document.getElementById("database-check-name").value = check.name; document.getElementById("database-check-expression").value = check.expression;
    document.querySelector("[data-check-editor-title]").textContent = check.name || "check";
    document.getElementById("database-remove-check").disabled = false;
  }
  function updateCheckFromEditor() {
    const row = activeCheckRow(); if (!row) return;
    row.dataset.checkName = document.getElementById("database-check-name").value.trim(); row.dataset.checkExpression = document.getElementById("database-check-expression").value;
    renderCheckSummary(row); document.querySelector("[data-check-editor-title]").textContent = row.dataset.checkName || "check"; updateTableSqlPreview();
  }
  function addCheckRow(initial = {}) {
    const row = document.createElement("button"); row.type = "button"; row.className = "dg-check-row dg-tree-object-row"; row.setAttribute("role", "option");
    const ordinal = document.querySelectorAll(".dg-check-row").length + 1; row.dataset.checkName = initial.name || `${form.elements.Name?.value.trim() || "table"}_check_${ordinal}`; row.dataset.checkExpression = initial.expression || "";
    renderCheckSummary(row); row.addEventListener("click", () => selectCheckRow(row)); wireObjectTreeKeyboard(row, "dg-check-row", selectCheckRow, removeActiveCheck); document.getElementById("database-check-list").appendChild(row);
    selectCheckRow(row); refreshCheckCount(); updateTableSqlPreview(); document.getElementById("database-check-expression").focus();
  }
  function removeActiveCheck() {
    const row = activeCheckRow(); if (!row) return; const next = row.nextElementSibling || row.previousElementSibling; row.remove();
    if (next?.classList.contains("dg-check-row")) selectCheckRow(next); else { document.getElementById("database-check-editor")?.classList.add("hidden"); document.getElementById("database-check-empty")?.classList.remove("hidden"); }
    refreshCheckCount(); updateTableSqlPreview();
    document.getElementById("database-remove-check").disabled = !activeCheckRow();
  }
  function bindDesignerSections(metadata) {
    document.querySelectorAll("[data-designer-section]").forEach(button => button.addEventListener("click", () => {
      document.querySelectorAll("[data-designer-section]").forEach(item => { item.classList.toggle("is-active", item === button); item.setAttribute("aria-selected", item === button ? "true" : "false"); });
      document.querySelectorAll("[data-designer-panel]").forEach(panel => panel.classList.toggle("hidden", panel.dataset.designerPanel !== button.dataset.designerSection));
    }));
    document.getElementById("database-add-column").addEventListener("click", () => addColumnRow(metadata));
    document.getElementById("database-empty-add-column").addEventListener("click", () => addColumnRow(metadata));
    document.getElementById("database-remove-column").addEventListener("click", removeActiveColumn);
    document.getElementById("database-move-column-up").addEventListener("click", () => moveColumn(-1));
    document.getElementById("database-move-column-down").addEventListener("click", () => moveColumn(1));
    ["database-column-name", "database-column-type", "database-column-nullable", "database-column-primary", "database-column-default", "database-column-now"].forEach(id => {
      document.getElementById(id).addEventListener("input", updateColumnFromEditor); document.getElementById(id).addEventListener("change", updateColumnFromEditor);
    });
    document.getElementById("database-column-default").addEventListener("input", event => { if (event.target.value) document.getElementById("database-column-now").checked = false; updateColumnFromEditor(); });
    document.getElementById("database-column-now").addEventListener("change", event => { if (event.target.checked) document.getElementById("database-column-default").value = ""; updateColumnFromEditor(); });
    document.getElementById("database-add-key").addEventListener("click", () => addKeyRow());
    document.getElementById("database-empty-add-key").addEventListener("click", () => addKeyRow());
    document.getElementById("database-remove-key").addEventListener("click", removeActiveKey);
    document.getElementById("database-key-name").addEventListener("input", () => {
      const row = activeKeyRow(); if (!row) return;
      row.dataset.keyAutoName = "false"; document.getElementById("database-key-auto-name").checked = false; updateKeyFromEditor();
    });
    document.getElementById("database-key-auto-name").addEventListener("change", event => {
      const row = activeKeyRow(); if (!row) return;
      row.dataset.keyAutoName = event.target.checked ? "true" : "false";
      document.getElementById("database-key-name").readOnly = event.target.checked;
      if (event.target.checked) updateAutoObjectNames(); else updateKeyFromEditor();
    });
    document.getElementById("database-key-mobile-select").addEventListener("change", event => {
      const row = [...document.querySelectorAll(".dg-key-row")][Number(event.target.value)];
      if (row) selectKeyRow(row);
    });
    document.querySelectorAll("[name=KeyKindEditor]").forEach(input => input.addEventListener("change", updateKeyFromEditor));
    document.getElementById("database-add-key-column").addEventListener("click", addKeyColumn);
    document.getElementById("database-remove-key-column").addEventListener("click", removeKeyColumn);
    document.getElementById("database-move-key-column-up").addEventListener("click", () => moveKeyColumn(-1));
    document.getElementById("database-move-key-column-down").addEventListener("click", () => moveKeyColumn(1));
    document.getElementById("database-key-column-name").addEventListener("change", event => {
      const row = activeKeyRow(), selected = selectedKeyColumnRow();
      if (!row || !selected || !event.target.value) return;
      const key = keyRowData(row), index = Number(selected.dataset.keyColumnIndex);
      if (key.columns.some((column, columnIndex) => column === event.target.value && columnIndex !== index)) { syncKeyColumnEditor(); return; }
      key.columns[index] = event.target.value;
      row.dataset.keyColumns = JSON.stringify(key.columns);
      updateAutoObjectNames(); renderKeyColumns(row); renderKeySummary(row); updateTableSqlPreview();
    });
    document.getElementById("database-add-foreign-key").addEventListener("click", () => addForeignKeyRow(metadata));
    document.getElementById("database-empty-add-foreign-key").addEventListener("click", () => addForeignKeyRow(metadata));
    document.getElementById("database-remove-foreign-key").addEventListener("click", removeActiveForeignKey);
    ["database-foreign-key-name", "database-fk-schema", "database-fk-table", "database-fk-on-update", "database-fk-on-delete"].forEach(id => {
      document.getElementById(id).addEventListener("input", updateForeignKeyFromEditor); document.getElementById(id).addEventListener("change", updateForeignKeyFromEditor);
    });
    document.getElementById("database-add-fk-mapping").addEventListener("click", () => {
      const row = activeForeignKeyRow(); if (!row) return; const fk = foreignKeyRowData(row), next = tableColumnNames().find(name => !fk.mappings.some(mapping => mapping.local === name)) || tableColumnNames()[0];
      if (!next) return; fk.mappings.push({ local: next, referenced: "id" }); row.dataset.foreignKeyMappings = JSON.stringify(fk.mappings); renderForeignKeyMappings(row); renderForeignKeySummary(row); updateTableSqlPreview();
    });
    document.getElementById("database-add-index").addEventListener("click", () => addIndexRow());
    document.getElementById("database-empty-add-index").addEventListener("click", () => addIndexRow());
    document.getElementById("database-remove-index").addEventListener("click", removeActiveIndex);
    document.getElementById("database-index-name").addEventListener("input", () => { const row = activeIndexRow(); if (!row) return; row.dataset.indexAutoName = "false"; document.getElementById("database-index-auto-name").checked = false; updateIndexFromEditor(); });
    document.getElementById("database-index-auto-name").addEventListener("change", event => { const row = activeIndexRow(); if (!row) return; row.dataset.indexAutoName = event.target.checked ? "true" : "false"; document.getElementById("database-index-name").readOnly = event.target.checked; if (event.target.checked) updateAutoObjectNames(); else updateIndexFromEditor(); });
    ["database-index-method", "database-index-unique"].forEach(id => { document.getElementById(id).addEventListener("input", updateIndexFromEditor); document.getElementById(id).addEventListener("change", updateIndexFromEditor); });
    document.getElementById("database-add-index-column").addEventListener("click", () => mutateIndexColumns("add")); document.getElementById("database-remove-index-column").addEventListener("click", () => mutateIndexColumns("remove"));
    document.getElementById("database-move-index-column-up").addEventListener("click", () => mutateIndexColumns("up")); document.getElementById("database-move-index-column-down").addEventListener("click", () => mutateIndexColumns("down"));
    document.getElementById("database-index-column-name").addEventListener("change", event => { const row = activeIndexRow(), selected = document.querySelector(".dg-index-column-row.is-active"); if (!row || !selected || !event.target.value) return; const index = indexRowData(row), position = Number(selected.dataset.indexColumnIndex); if (index.columns.some((column, i) => column === event.target.value && i !== position)) return syncIndexColumnEditor(); index.columns[position] = event.target.value; row.dataset.indexColumns = JSON.stringify(index.columns); updateAutoObjectNames(); renderIndexColumns(row); renderIndexSummary(row); updateTableSqlPreview(); });
    document.getElementById("database-add-check").addEventListener("click", () => addCheckRow());
    document.getElementById("database-empty-add-check").addEventListener("click", () => addCheckRow());
    document.getElementById("database-remove-check").addEventListener("click", removeActiveCheck);
    ["database-check-name", "database-check-expression"].forEach(id => { document.getElementById(id).addEventListener("input", updateCheckFromEditor); document.getElementById(id).addEventListener("change", updateCheckFromEditor); });
    refreshKeyControls();
    document.getElementById("database-remove-foreign-key").disabled = true;
    document.getElementById("database-remove-index").disabled = true;
    document.getElementById("database-remove-check").disabled = true;
    refreshColumnControls();
  }
  function bindTableMode() {
    const update = () => {
      const distributed = document.querySelector("[name=Mode]").value === "Distributed";
      document.getElementById("database-distribution-options").classList.toggle("hidden", !distributed);
      document.querySelector("[name=DistributionColumn]").required = distributed;
    };
    document.querySelector("[name=Mode]").addEventListener("change", update); update();
  }

  function updateTableSqlPreview() {
    const preview = document.getElementById("database-sql-preview");
    if (!preview) return;
    const schema = form.elements.Schema?.value || "public";
    const table = form.elements.Name?.value.trim() || "table_name";
    const quoteId = value => `"${String(value).replaceAll('"', '""')}"`;
    const quoteLiteral = value => `'${String(value).replaceAll("'", "''")}'`;
    const rows = tableColumnRows();
    const primary = rows.map(columnRowData).filter(column => column.primaryKey).map(column => column.name).filter(Boolean);
    const definitions = rows.map(row => {
      const column = columnRowData(row), name = column.name || "column_name", type = column.dataType, nullable = column.nullable;
      const literal = column.defaultLiteral, current = column.currentTimestamp, isPrimary = column.primaryKey;
      return `  ${quoteId(name)} ${type}${nullable && !isPrimary ? "" : " NOT NULL"}${current ? " DEFAULT CURRENT_TIMESTAMP" : literal ? ` DEFAULT ${quoteLiteral(literal)}::${type}` : ""}`;
    });
    if (primary.length) definitions.push(`  PRIMARY KEY (${primary.map(quoteId).join(", ")})`);
    document.querySelectorAll(".dg-key-row").forEach(row => {
      const key = keyRowData(row);
      const name = key.name;
      const kind = key.kind === "Primary" ? "PRIMARY KEY" : "UNIQUE";
      const columns = key.columns;
      definitions.push(`  ${name ? `CONSTRAINT ${quoteId(name)} ` : ""}${kind} (${(columns.length ? columns : ["column_name"]).map(quoteId).join(", ")})`);
    });
    const actionSql = value => ({ NoAction: "NO ACTION", Restrict: "RESTRICT", Cascade: "CASCADE", SetNull: "SET NULL", SetDefault: "SET DEFAULT" })[value] || "NO ACTION";
    document.querySelectorAll(".dg-foreign-key-row").forEach(row => {
      const fk = foreignKeyRowData(row), local = fk.mappings.map(mapping => mapping.local), referenced = fk.mappings.map(mapping => mapping.referenced);
      definitions.push(`  ${fk.name ? `CONSTRAINT ${quoteId(fk.name)} ` : ""}FOREIGN KEY (${(local.length ? local : ["column_name"]).map(quoteId).join(", ")}) REFERENCES ${quoteId(fk.referencedSchema)}.${quoteId(fk.referencedTable || "referenced_table")} (${(referenced.length ? referenced : ["id"]).map(quoteId).join(", ")}) ON UPDATE ${actionSql(fk.onUpdate)} ON DELETE ${actionSql(fk.onDelete)}`);
    });
    document.querySelectorAll(".dg-check-row").forEach(row => {
      const check = checkRowData(row);
      definitions.push(`  ${check.name ? `CONSTRAINT ${quoteId(check.name)} ` : ""}CHECK (${check.expression.trim() || "expression"})`);
    });
    let sql = `CREATE TABLE ${quoteId(schema)}.${quoteId(table)} (\n${definitions.join(",\n")}\n);`;
    document.querySelectorAll(".dg-index-row").forEach(row => {
      const index = indexRowData(row);
      sql += `\n\nCREATE ${index.unique ? "UNIQUE " : ""}INDEX ${quoteId(index.name || "index_name")} ON ${quoteId(schema)}.${quoteId(table)} USING ${index.method.toLowerCase()} (${(index.columns.length ? index.columns : ["column_name"]).map(quoteId).join(", ")});`;
    });
    const mode = form.elements.Mode?.value;
    if (mode === "Reference") sql += `\n\nSELECT create_reference_table('${schema.replaceAll("'", "''")}.${table.replaceAll("'", "''")}');`;
    if (mode === "Distributed") {
      const distribution = form.elements.DistributionColumn?.value || "distribution_column";
      const colocate = form.elements.ColocateWith?.value;
      const shards = form.elements.ShardCount?.value;
      const options = [`colocate_with => '${(colocate || "none").replaceAll("'", "''")}'`];
      if (shards) options.push(`shard_count => ${Number(shards)}`);
      sql += `\n\nSELECT create_distributed_table(\n  '${schema.replaceAll("'", "''")}.${table.replaceAll("'", "''")}',\n  '${distribution.replaceAll("'", "''")}',\n  ${options.join(",\n  ")}\n);`;
    }
    const keywords = new Set(["CREATE", "TABLE", "PRIMARY", "KEY", "NOT", "NULL", "DEFAULT", "CURRENT_TIMESTAMP", "SELECT", "CONSTRAINT", "UNIQUE", "FOREIGN", "REFERENCES", "CHECK", "INDEX", "ON", "USING", "UPDATE", "DELETE", "ACTION", "CASCADE", "RESTRICT", "SET"]);
    const types = new Set(["BIGINT", "INTEGER", "INT", "TEXT", "BOOLEAN", "UUID", "JSON", "JSONB", "TIMESTAMP", "TIMESTAMPTZ", "NUMERIC"]);
    preview.innerHTML = sql.split(/('(?:''|[^'])*'|\b[A-Za-z_][A-Za-z_0-9]*\b)/g).map(token => {
      if (token.startsWith("'")) return `<span class="dg-sql-string">${html(token)}</span>`;
      const upper = token.toUpperCase();
      if (keywords.has(upper)) return `<span class="dg-sql-keyword">${html(token)}</span>`;
      if (types.has(upper)) return `<span class="dg-sql-type">${html(token)}</span>`;
      if (token.startsWith("create_") || token === "colocate_with" || token === "shard_count")
        return `<span class="dg-sql-function">${html(token)}</span>`;
      return html(token);
    }).join("");
    document.querySelector("[data-designer-table-name]")?.replaceChildren(document.createTextNode(table));
  }

  async function openCreate(type, target) {
    const metadata = await getMetadata();
    const schema = target.schema || metadata.schemas[0] || "public";
    if (type === "schema") {
      openModal({ title: "Tạo schema", description: "Tạo schema mới trên coordinator.",
        body: field("Tên schema", input("Name", "", "text", "required maxlength=63 autocomplete=off")), button: "Tạo schema",
        onSubmit: async () => finish(await post($explorer.data("create-schema-url"), { Name: form.elements.Name.value })) });
      return;
    }
    if (type === "view") {
      openModal({ title: "Tạo view", description: "Definition phải là một SELECT/WITH, không có dấu chấm phẩy.",
        body: field("Schema", schemaSelect(metadata.schemas, schema)) + field("Tên view", input("Name", "", "text", "required maxlength=63")) +
          field("SQL definition", '<textarea name="Definition" required rows="10" spellcheck="false" placeholder="SELECT …"></textarea>'), button: "Tạo view",
        onSubmit: async () => finish(await post($explorer.data("create-view-url"), {
          Schema: form.elements.Schema.value, Name: form.elements.Name.value, Definition: form.elements.Definition.value, Replace: false
        })) });
      return;
    }
    if (type === "sequence") {
      openModal({ title: "Tạo sequence", description: "Thông số bỏ trống dùng PostgreSQL default.",
        body: field("Schema", schemaSelect(metadata.schemas, schema)) + field("Tên sequence", input("Name", "", "text", "required maxlength=63")) +
          `<div class="database-action-grid">${field("Start", input("Start", "", "number"))}${field("Increment", input("Increment", "", "number"))}${field("Minimum", input("Minimum", "", "number"))}${field("Maximum", input("Maximum", "", "number"))}${field("Cache", input("Cache", "", "number", "min=1"))}</div>` +
          '<label class="database-action-check"><input name="Cycle" type="checkbox"/> Cycle</label>', button: "Tạo sequence",
        onSubmit: async () => {
          const nullable = name => form.elements[name].value === "" ? null : Number(form.elements[name].value);
          await finish(await post($explorer.data("create-sequence-url"), { Schema: form.elements.Schema.value, Name: form.elements.Name.value,
            Start: nullable("Start"), Increment: nullable("Increment"), Minimum: nullable("Minimum"), Maximum: nullable("Maximum"),
            Cache: nullable("Cache"), Cycle: form.elements.Cycle.checked }));
        } });
      return;
    }
    const modeOptions = [`<option value="Local">Local</option>`,
      `<option value="Reference" ${metadata.canCreateReferenceTable ? "" : "disabled"}>Reference${metadata.canCreateReferenceTable ? "" : " (unsupported)"}</option>`,
      `<option value="Distributed" ${metadata.canCreateDistributedTable ? "" : "disabled"}>Distributed${metadata.canCreateDistributedTable ? "" : " (unsupported)"}</option>`].join("");
    openModal({ title: "Create Table", eyebrow: `POSTGRESQL · CITUS ${metadata.citusVersion || "N/A"}`,
      description: "Thiết kế table, columns và Citus placement. SQL preview cập nhật ngay khi chỉnh sửa.", variant: "table",
      body: `<div class="dg-table-designer">
        <aside class="dg-designer-sidebar" aria-label="Table object sections">
          <div class="dg-designer-toolbar"><button type="button" aria-label="Thêm object">+</button><button type="button" disabled aria-label="Xóa object">−</button><span></span><button type="button" aria-label="Edit"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="m4 20 4.5-1 10-10-3.5-3.5-10 10L4 20Z"/></svg></button></div>
          <div class="dg-designer-object"><span class="dg-table-glyph" aria-hidden="true"></span><span><strong data-designer-table-name>table_name</strong><small>${html(schema)} · coordinator</small></span></div>
          <nav class="dg-designer-tree" role="tablist" aria-label="Table definition">
            <div class="dg-designer-tree-group">
              <button type="button" class="is-active" role="tab" aria-selected="true" data-designer-section="columns"><span class="dg-folder-glyph"></span>Columns <span id="database-column-count" class="dg-tree-count">0</span></button>
              <div id="database-column-list" class="dg-object-tree-list dg-designer-tree-children" role="listbox" aria-label="Table columns"></div>
            </div>
            <div class="dg-designer-tree-group">
              <button type="button" role="tab" aria-selected="false" data-designer-section="keys"><span class="dg-folder-glyph"></span>Keys <span id="database-key-count" class="dg-tree-count">0</span></button>
              <div id="database-key-list" class="dg-key-list dg-designer-tree-children" role="listbox" aria-label="Table keys"></div>
            </div>
            <div class="dg-designer-tree-group"><button type="button" role="tab" aria-selected="false" data-designer-section="foreign-keys"><span class="dg-folder-glyph"></span>Foreign keys <span id="database-foreign-key-count" class="dg-tree-count">0</span></button><div id="database-foreign-key-list" class="dg-object-tree-list dg-designer-tree-children" role="listbox" aria-label="Foreign keys"></div></div>
            <div class="dg-designer-tree-group"><button type="button" role="tab" aria-selected="false" data-designer-section="indexes"><span class="dg-folder-glyph"></span>Indexes <span id="database-index-count" class="dg-tree-count">0</span></button><div id="database-index-list" class="dg-object-tree-list dg-designer-tree-children" role="listbox" aria-label="Indexes"></div></div>
            <div class="dg-designer-tree-group"><button type="button" role="tab" aria-selected="false" data-designer-section="checks"><span class="dg-folder-glyph"></span>Checks <span id="database-check-count" class="dg-tree-count">0</span></button><div id="database-check-list" class="dg-object-tree-list dg-designer-tree-children" role="listbox" aria-label="Check constraints"></div></div>
          </nav>
        </aside>
        <section class="dg-designer-main">
          <div class="dg-object-identity">
            ${field("Schema", schemaSelect(metadata.schemas, schema))}
            ${field("Name", input("Name", "table_name", "text", "required maxlength=63 autocomplete=off"))}
            ${field("Persistence", `<select name="Mode">${modeOptions}</select>`)}
          </div>
          <div class="dg-designer-panels">
            <section data-designer-panel="columns" class="dg-designer-panel">
              <div class="database-column-toolbar dg-object-panel-toolbar"><div><strong>Column Designer</strong><small>PostgreSQL column properties</small></div><div><button type="button" id="database-add-column" class="dg-toolbar-button">+ Add</button><button type="button" id="database-remove-column" class="dg-toolbar-button is-danger">− Remove</button><button type="button" id="database-move-column-up" class="dg-toolbar-button">↑</button><button type="button" id="database-move-column-down" class="dg-toolbar-button">↓</button></div></div>
              <div id="database-column-empty" class="dg-key-empty-state"><span class="dg-key-empty-icon" aria-hidden="true"></span><strong>No column selected</strong><p>Add a column, then configure its name, catalog type, nullability and default.</p><button type="button" id="database-empty-add-column" class="dg-toolbar-button">+ Add column</button></div>
              <div id="database-column-editor" class="dg-object-editor hidden">
                <header class="dg-key-editor-heading"><span class="dg-column-icon" aria-hidden="true"></span><strong data-column-editor-title>column_name</strong><span>column</span></header>
                <div class="dg-property-grid">
                  <label><span>Name</span><input id="database-column-name" required maxlength="63" autocomplete="off" placeholder="column_name"></label>
                  <label><span>Data type</span><select id="database-column-type">${metadata.columnTypes.map(type => `<option value="${html(type.name)}">${html(type.displayName)}</option>`).join("")}</select></label>
                  <fieldset class="dg-option-group"><legend>Constraints</legend><label><input id="database-column-nullable" type="checkbox" checked> Nullable</label><label><input id="database-column-primary" type="checkbox"> Primary key</label></fieldset>
                  <label><span>Default literal <em>optional</em></span><input id="database-column-default" maxlength="4000" placeholder="value"></label>
                  <fieldset class="dg-option-group"><legend>Default preset</legend><label><input id="database-column-now" type="checkbox"> CURRENT_TIMESTAMP</label></fieldset>
                </div>
              </div>
            </section>
            <section data-designer-panel="keys" class="dg-designer-panel hidden">
              <div class="database-column-toolbar dg-key-panel-toolbar"><div><strong>Key Designer</strong><small>Primary và unique constraints</small></div><select id="database-key-mobile-select" class="dg-key-mobile-select" aria-label="Selected key" disabled><option>No keys</option></select><div><button type="button" id="database-add-key" class="dg-toolbar-button">+ Add key</button><button type="button" id="database-remove-key" class="dg-toolbar-button is-danger">− Remove</button></div></div>
                <section class="dg-key-detail dg-key-detail-standalone">
                  <div id="database-key-empty" class="dg-key-empty-state">
                    <span class="dg-key-empty-icon" aria-hidden="true"></span>
                    <strong>No key selected</strong>
                    <p>Add a primary or unique key to configure its ordered columns.</p>
                    <button type="button" id="database-empty-add-key" class="dg-toolbar-button">+ Add key</button>
                  </div>
                  <div id="database-key-editor" class="dg-key-editor hidden">
                    <header class="dg-key-editor-heading"><span class="dg-key-icon" aria-hidden="true"></span><strong data-key-editor-title>primary_key</strong><span>constraint</span></header>
                    <div class="dg-key-properties">
                      <label class="dg-key-name-field"><span>Name <span class="dg-auto-name-toggle"><input id="database-key-auto-name" type="checkbox" checked> Auto</span></span><input id="database-key-name" maxlength="63" autocomplete="off" placeholder="users_id_createdAt" readonly></label>
                      <fieldset class="dg-key-kind-control"><legend>Constraint type</legend>
                        <label><input type="radio" name="KeyKindEditor" value="Primary"><span><strong>Primary key</strong><small>One per table</small></span></label>
                        <label><input type="radio" name="KeyKindEditor" value="Unique"><span><strong>Unique</strong><small>Enforce unique values</small></span></label>
                      </fieldset>
                    </div>
                    <section class="dg-key-columns-section">
                      <div class="dg-key-columns-heading"><div><strong>Columns</strong><small>Order affects the generated constraint</small></div>
                        <div class="dg-key-column-toolbar" role="toolbar" aria-label="Key column actions">
                          <button type="button" id="database-add-key-column" aria-label="Add column" title="Add column">+</button>
                          <button type="button" id="database-remove-key-column" aria-label="Remove selected column" title="Remove column">−</button>
                          <span></span>
                          <button type="button" id="database-move-key-column-up" aria-label="Move column up" title="Move up">↑</button>
                          <button type="button" id="database-move-key-column-down" aria-label="Move column down" title="Move down">↓</button>
                        </div>
                      </div>
                      <div class="dg-key-columns-workspace">
                        <div id="database-key-column-list" class="dg-key-column-list" role="listbox" aria-label="Ordered key columns"></div>
                        <label class="dg-key-column-property"><span>Column name</span><select id="database-key-column-name" disabled></select><small>Select a row on the left, then choose its column.</small></label>
                      </div>
                    </section>
                  </div>
                </section>
            </section>
            <section data-designer-panel="foreign-keys" class="dg-designer-panel hidden">
              <div class="database-column-toolbar dg-object-panel-toolbar"><div><strong>Foreign Key Designer</strong><small>Map local columns to a referenced table</small></div><div><button type="button" id="database-add-foreign-key" class="dg-toolbar-button">+ Add</button><button type="button" id="database-remove-foreign-key" class="dg-toolbar-button is-danger">− Remove</button></div></div>
              <div id="database-foreign-key-empty" class="dg-key-empty-state"><span class="dg-key-empty-icon" aria-hidden="true"></span><strong>No foreign key selected</strong><p>Add a foreign key to configure its target and column mappings.</p><button type="button" id="database-empty-add-foreign-key" class="dg-toolbar-button">+ Add foreign key</button></div>
              <div id="database-foreign-key-editor" class="dg-object-editor hidden">
                <header class="dg-key-editor-heading"><span class="dg-foreign-key-icon" aria-hidden="true"></span><strong data-foreign-key-editor-title>foreign_key</strong><span>constraint</span></header>
                <div class="dg-property-grid dg-fk-properties">
                  <label><span>Name <em>optional</em></span><input id="database-foreign-key-name" maxlength="63" placeholder="orders_customer_fkey"></label>
                  <label><span>Referenced schema</span><select id="database-fk-schema">${(metadata.schemas.length ? metadata.schemas : ["public"]).map(item => `<option value="${html(item)}">${html(item)}</option>`).join("")}</select></label>
                  <label><span>Referenced table</span><input id="database-fk-table" maxlength="63" placeholder="customers"></label>
                  <label><span>On update</span><select id="database-fk-on-update">${referentialOptions()}</select></label>
                  <label><span>On delete</span><select id="database-fk-on-delete">${referentialOptions()}</select></label>
                </div>
                <section class="dg-key-columns-section"><div class="dg-key-columns-heading"><div><strong>Column mappings</strong><small>Local column → referenced column</small></div><div class="dg-key-column-toolbar"><button type="button" id="database-add-fk-mapping" aria-label="Add mapping">+</button></div></div><div id="database-fk-mapping-list" class="dg-fk-mapping-list"></div></section>
              </div>
            </section>
            <section data-designer-panel="indexes" class="dg-designer-panel hidden">
              <div class="database-column-toolbar dg-object-panel-toolbar"><div><strong>Index Designer</strong><small>Ordered columns and PostgreSQL access method</small></div><div><button type="button" id="database-add-index" class="dg-toolbar-button">+ Add</button><button type="button" id="database-remove-index" class="dg-toolbar-button is-danger">− Remove</button></div></div>
              <div id="database-index-empty" class="dg-key-empty-state"><span class="dg-key-empty-icon" aria-hidden="true"></span><strong>No index selected</strong><p>Add an index to configure method, uniqueness and ordered columns.</p><button type="button" id="database-empty-add-index" class="dg-toolbar-button">+ Add index</button></div>
              <div id="database-index-editor" class="dg-object-editor hidden">
                <header class="dg-key-editor-heading"><span class="dg-index-icon" aria-hidden="true"></span><strong data-index-editor-title>index</strong><span>index</span></header>
                <div class="dg-property-grid">
                  <label><span>Name <span class="dg-auto-name-toggle"><input id="database-index-auto-name" type="checkbox" checked> Auto</span></span><input id="database-index-name" maxlength="63" placeholder="users_id_createdAt" readonly></label>
                  <label><span>Access method</span><select id="database-index-method">${["Btree", "Hash", "Gin", "Gist", "Brin"].map(value => `<option value="${value}">${value.toLowerCase()}</option>`).join("")}</select></label>
                  <fieldset class="dg-option-group"><legend>Options</legend><label><input id="database-index-unique" type="checkbox"> Unique</label></fieldset>
                </div>
                <section class="dg-key-columns-section"><div class="dg-key-columns-heading"><div><strong>Columns</strong><small>Order affects lookup and sort behavior</small></div><div class="dg-key-column-toolbar" role="toolbar" aria-label="Index column actions"><button type="button" id="database-add-index-column" aria-label="Add index column" title="Add column">+</button><button type="button" id="database-remove-index-column" aria-label="Remove selected index column" title="Remove column">−</button><span></span><button type="button" id="database-move-index-column-up" aria-label="Move index column up" title="Move up">↑</button><button type="button" id="database-move-index-column-down" aria-label="Move index column down" title="Move down">↓</button></div></div><div class="dg-key-columns-workspace"><div id="database-index-column-list" class="dg-key-column-list"></div><label class="dg-key-column-property"><span>Column name</span><select id="database-index-column-name" disabled></select><small>Select a row on the left, then choose its column.</small></label></div></section>
              </div>
            </section>
            <section data-designer-panel="checks" class="dg-designer-panel hidden">
              <div class="database-column-toolbar dg-object-panel-toolbar"><div><strong>Check Designer</strong><small>One safe PostgreSQL boolean expression</small></div><div><button type="button" id="database-add-check" class="dg-toolbar-button">+ Add</button><button type="button" id="database-remove-check" class="dg-toolbar-button is-danger">− Remove</button></div></div>
              <div id="database-check-empty" class="dg-key-empty-state"><span class="dg-key-empty-icon" aria-hidden="true"></span><strong>No check selected</strong><p>Add a check constraint, then enter its boolean expression.</p><button type="button" id="database-empty-add-check" class="dg-toolbar-button">+ Add check</button></div>
              <div id="database-check-editor" class="dg-object-editor hidden">
                <header class="dg-key-editor-heading"><span class="dg-check-icon" aria-hidden="true"></span><strong data-check-editor-title>check</strong><span>constraint</span></header>
                <div class="dg-property-grid dg-check-properties"><label><span>Name <em>optional</em></span><input id="database-check-name" maxlength="63" placeholder="users_age_check"></label><label class="dg-wide-property"><span>Expression</span><textarea id="database-check-expression" rows="8" maxlength="4000" spellcheck="false" placeholder="age &gt;= 18"></textarea><small>No semicolon; expression is validated again by the server.</small></label></div>
              </div>
            </section>
          </div>
          <div id="database-distribution-options" class="dg-distribution-panel hidden">
            ${field("Distribution column", '<select name="DistributionColumn"></select>')}
            ${field("Colocate with", `<select name="ColocateWith"><option value="">none</option>${metadata.distributedTables.map(x => `<option value="${html(x)}">${html(x)}</option>`).join("")}</select>`)}
            ${field("Shard count", input("ShardCount", "", "number", "min=1 max=4096 placeholder='server default'"))}
          </div>
        </section>
        <section class="dg-sql-preview" aria-label="SQL Preview">
          <header><span class="dg-preview-caret">⌄</span><strong>SQL Preview</strong><small>generated · read-only</small></header>
          <pre><code id="database-sql-preview"></code></pre>
        </section>
      </div>`, button: "Create",
      onSubmit: async () => {
        const data = { Schema: form.elements.Schema.value, Name: form.elements.Name.value, Mode: form.elements.Mode.value,
          DistributionColumn: form.elements.DistributionColumn?.value || null, ColocateWith: form.elements.ColocateWith?.value || null,
          ShardCount: form.elements.ShardCount?.value ? Number(form.elements.ShardCount.value) : null };
        [...document.querySelectorAll(".database-column-row")].forEach((row, index) => {
          const column = columnRowData(row);
          data[`Columns[${index}].Name`] = column.name; data[`Columns[${index}].DataType`] = column.dataType; data[`Columns[${index}].Nullable`] = column.nullable;
          data[`Columns[${index}].PrimaryKey`] = column.primaryKey; data[`Columns[${index}].DefaultLiteral`] = column.defaultLiteral || null; data[`Columns[${index}].DefaultCurrentTimestamp`] = column.currentTimestamp;
        });
        [...document.querySelectorAll(".dg-key-row")].forEach((row, index) => {
          const key = keyRowData(row);
          if (!key.columns.length) throw { responseJSON: { detail: `Key ${key.name || index + 1} phải có ít nhất một column.` } };
          data[`Keys[${index}].Name`] = key.name || null;
          data[`Keys[${index}].Kind`] = key.kind;
          key.columns.forEach((column, columnIndex) => { data[`Keys[${index}].Columns[${columnIndex}]`] = column; });
        });
        [...document.querySelectorAll(".dg-foreign-key-row")].forEach((row, index) => {
          const fk = foreignKeyRowData(row);
          if (!fk.referencedTable || !fk.mappings.length || fk.mappings.some(mapping => !mapping.local || !mapping.referenced)) throw { responseJSON: { detail: `Foreign key ${fk.name || index + 1} chưa đủ table/column mapping.` } };
          data[`ForeignKeys[${index}].Name`] = fk.name || null; data[`ForeignKeys[${index}].ReferencedSchema`] = fk.referencedSchema; data[`ForeignKeys[${index}].ReferencedTable`] = fk.referencedTable;
          fk.mappings.forEach((mapping, mappingIndex) => { data[`ForeignKeys[${index}].Columns[${mappingIndex}]`] = mapping.local; data[`ForeignKeys[${index}].ReferencedColumns[${mappingIndex}]`] = mapping.referenced; });
          data[`ForeignKeys[${index}].OnUpdate`] = fk.onUpdate; data[`ForeignKeys[${index}].OnDelete`] = fk.onDelete;
        });
        [...document.querySelectorAll(".dg-index-row")].forEach((row, index) => {
          const item = indexRowData(row); if (!item.columns.length) throw { responseJSON: { detail: `Index ${item.name || index + 1} phải có ít nhất một column.` } };
          data[`Indexes[${index}].Name`] = item.name; data[`Indexes[${index}].Unique`] = item.unique; data[`Indexes[${index}].Method`] = item.method;
          item.columns.forEach((column, columnIndex) => { data[`Indexes[${index}].Columns[${columnIndex}]`] = column; });
        });
        [...document.querySelectorAll(".dg-check-row")].forEach((row, index) => {
          const check = checkRowData(row); if (!check.expression.trim()) throw { responseJSON: { detail: `Check ${check.name || index + 1} cần expression.` } };
          data[`Checks[${index}].Name`] = check.name || null; data[`Checks[${index}].Expression`] = check.expression;
        });
        await finish(await post($explorer.data("create-table-url"), data));
      } });
    bindDesignerSections(metadata);
    addColumnRow(metadata, { name: "id", nullable: false });
    form.elements.Name.addEventListener("input", () => { updateAutoObjectNames(); updateTableSqlPreview(); });
    bindTableMode();
    fields.oninput = updateTableSqlPreview;
    fields.onchange = updateTableSqlPreview;
    updateTableSqlPreview();
  }

  async function handleAction(action) {
    const target = currentTarget();
    try {
      if (action === "refresh") { await refreshTree(); showToast("Đã refresh cây database."); return; }
      if (action === "query") { document.querySelector("[data-database-tab=sql]")?.click(); return; }
      if (action === "browse" || action === "structure") {
        contextNode.click(); document.querySelector(`[data-database-tab=${action === "browse" ? "data" : "structure"}]`)?.click(); return;
      }
      if (action.startsWith("create-")) { await openCreate(action.slice(7), target); return; }
      if (action === "inspect-sequence") {
        const data = await $.getJSON($explorer.data("sequence-inspect-url"), { schema: target.schema, name: target.name });
        document.getElementById("database-result").innerHTML = `<section class="pma-structure-result"><div class="pma-section-title"><div><h2>${html(target.schema)}.${html(target.name)}</h2><p>Sequence metadata</p></div></div><dl class="database-inspection-list">${Object.entries(data).map(([key,value]) => `<div><dt>${html(key)}</dt><dd>${html(value ?? "—")}</dd></div>`).join("")}</dl></section>`;
        document.getElementById("sql-console")?.classList.add("hidden"); document.getElementById("database-result").classList.remove("hidden"); return;
      }
      if (action === "rename") {
        openModal({ title: `Rename ${target.kind}`, description: `${target.schema}${target.kind === "schema" ? "" : "." + target.name}`,
          body: field("Tên mới", input("NewName", target.name, "text", "required maxlength=63 autocomplete=off")), button: "Rename",
          onSubmit: async () => finish(await post($explorer.data("rename-url"), { Kind: kindName(target.kind), Schema: target.schema,
            Name: target.kind === "schema" ? null : target.name, NewName: form.elements.NewName.value })) }); return;
      }
      if (action === "edit-view") {
        const definition = await $.getJSON($explorer.data("view-definition-url"), { schema: target.schema, name: target.name });
        openModal({ title: "Edit view SQL", description: `${target.schema}.${target.name}`,
          body: field("SQL definition", `<textarea name="Definition" required rows="12" spellcheck="false">${html(definition.definition)}</textarea>`), button: "Cập nhật view",
          onSubmit: async () => finish(await post($explorer.data("create-view-url"), { Schema: target.schema, Name: target.name,
            Definition: form.elements.Definition.value, Replace: true })) }); return;
      }
      if (action === "drop") {
        const dependencies = await $.getJSON($explorer.data("dependencies-url"), { kind: kindName(target.kind), schema: target.schema,
          name: target.kind === "schema" ? null : target.name });
        const qualified = target.kind === "schema" ? target.schema : `${target.schema}.${target.name}`;
        openModal({ title: `Drop ${target.kind}`, eyebrow: "DESTRUCTIVE", description: `Xóa vĩnh viễn ${qualified}. Dependencies phát hiện: ${dependencies.count}.`,
          body: (dependencies.items.length ? `<div class="database-dependency-list">${dependencies.items.map(x => `<code>${html(x)}</code>`).join("")}</div>` : "") +
            field(`Gõ ${qualified} để xác nhận`, input("TypedConfirmation", "", "text", `required autocomplete=off data-confirm='${html(qualified)}'`)) +
            '<label class="database-action-check"><input name="Cascade" type="checkbox"/> CASCADE dependencies</label>', button: "Drop vĩnh viễn", danger: true,
          onSubmit: async () => {
            if (form.elements.TypedConfirmation.value !== qualified) throw { responseJSON: { detail: `Phải gõ chính xác ${qualified}.` } };
            await finish(await post($explorer.data("drop-url"), { Kind: kindName(target.kind), Schema: target.schema,
              Name: target.kind === "schema" ? null : target.name, Cascade: form.elements.Cascade.checked,
              TypedConfirmation: form.elements.TypedConfirmation.value }));
          } }); return;
      }
      if (action === "truncate") {
        const qualified = `${target.schema}.${target.name}`;
        openModal({ title: "Truncate table", eyebrow: "DESTRUCTIVE", description: `Xóa toàn bộ rows trong ${qualified}.`,
          body: field(`Gõ ${qualified} để xác nhận`, input("TypedConfirmation", "", "text", "required autocomplete=off")) +
            '<label class="database-action-check"><input name="RestartIdentity" type="checkbox"/> Restart identity</label><label class="database-action-check"><input name="Cascade" type="checkbox"/> CASCADE</label>',
          button: "Truncate", danger: true, onSubmit: async () => {
            if (form.elements.TypedConfirmation.value !== qualified) throw { responseJSON: { detail: `Phải gõ chính xác ${qualified}.` } };
            await finish(await post($explorer.data("truncate-url"), { Schema: target.schema, Name: target.name,
              RestartIdentity: form.elements.RestartIdentity.checked, Cascade: form.elements.Cascade.checked,
              TypedConfirmation: form.elements.TypedConfirmation.value }));
          } }); return;
      }
      if (action === "restart-sequence") {
        openModal({ title: "Restart sequence", eyebrow: "DATA CHANGE", description: `${target.schema}.${target.name}`,
          body: field("Restart with", input("RestartWith", "1", "number", "required")), button: "Restart", danger: true,
          onSubmit: async () => finish(await post($explorer.data("restart-sequence-url"), { Schema: target.schema, Name: target.name,
            RestartWith: Number(form.elements.RestartWith.value) })) }); return;
      }
      if (action === "refresh-materialized") {
        openModal({ title: "Refresh materialized view", description: `${target.schema}.${target.name}`,
          body: '<label class="database-action-check"><input name="Concurrently" type="checkbox"/> CONCURRENTLY (cần unique index phù hợp)</label>', button: "Refresh data",
          onSubmit: async () => finish(await post($explorer.data("refresh-materialized-view-url"), { Schema: target.schema, Name: target.name,
            Concurrently: form.elements.Concurrently.checked })) }); return;
      }
      if (action === "convert") {
        const metadata = await getMetadata();
        const qualified = `${target.schema}.${target.name}`;
        openModal({ title: "Citus table conversion", eyebrow: "IMPACT OPERATION",
          description: "Tạo immutable plan. Admin khác phải approve trước khi background runner chuyển dữ liệu.",
          body: field("Target mode", `<select name="TargetMode"><option value="Distributed">Distributed</option><option value="Reference">Reference</option></select>`) +
            field("Distribution column", input("DistributionColumn", "", "text", "required maxlength=63")) +
            field("Colocate with", `<select name="ColocateWith"><option value="">none</option>${metadata.distributedTables.map(x => `<option value="${html(x)}">${html(x)}</option>`).join("")}</select>`) +
            field("Shard count", input("ShardCount", "", "number", "min=1 max=4096 placeholder='server default'")) +
            '<label class="database-action-check database-impact-check"><input name="Acknowledged" type="checkbox" required/> Đã kiểm tra backup/PITR, disk, WAL, network, connection budget và rollback owner.</label>' +
            field(`Gõ ${qualified} để xác nhận`, input("TypedConfirmation", "", "text", "required autocomplete=off")), button: "Tạo conversion plan",
          onSubmit: async () => {
            if (form.elements.TypedConfirmation.value !== qualified) throw { responseJSON: { detail: `Phải gõ chính xác ${qualified}.` } };
            const reference = form.elements.TargetMode.value === "Reference";
            await finish(await post($explorer.data("convert-table-url"), { Schema: target.schema, Table: target.name,
              TargetMode: form.elements.TargetMode.value, DistributionColumn: reference ? null : form.elements.DistributionColumn.value,
              ColocateWith: reference ? null : form.elements.ColocateWith.value || null,
              ShardCount: reference || !form.elements.ShardCount.value ? null : Number(form.elements.ShardCount.value),
              ExternalCapacityAndBackupChecksAcknowledged: form.elements.Acknowledged.checked,
              TypedConfirmation: form.elements.TypedConfirmation.value }));
          } });
        const mode = form.elements.TargetMode;
        mode.addEventListener("change", () => {
          const reference = mode.value === "Reference";
          [form.elements.DistributionColumn, form.elements.ColocateWith, form.elements.ShardCount].forEach(x => x.disabled = reference);
          form.elements.DistributionColumn.required = !reference;
        });
      }
    } catch (xhr) {
      showToast(problemText(xhr));
    }
  }
})();
