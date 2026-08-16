export function bindWorkspaceTabInteractions({
  tabs,
  workspaces,
  getActiveKey,
  activate,
  closeWorkspace,
  closeWorkspaces,
  duplicateWorkspace,
  reorderWorkspace
}) {
  let draggedWorkspaceKey = null;

  tabs.addEventListener("click", event => {
    const tab = event.target.closest("[data-workspace-key]");
    if (!tab) return;
    if (event.target.closest(".database-workspace-tab-close")) closeWorkspace(tab.dataset.workspaceKey);
    else activate(tab.dataset.workspaceKey);
  });
  tabs.addEventListener("auxclick", event => {
    if (event.button === 1) closeWorkspace(event.target.closest("[data-workspace-key]")?.dataset.workspaceKey);
  });

  const contextMenu = document.createElement("div");
  contextMenu.className = "database-workspace-context-menu hidden";
  contextMenu.setAttribute("role", "menu");
  contextMenu.setAttribute("aria-label", "Tác vụ không gian làm việc");
  contextMenu.innerHTML = `<button type="button" role="menuitem" data-tab-action="close"><i class="fa fa-times" aria-hidden="true"></i><span>Đóng tab</span><kbd>Ctrl+W</kbd></button>
    <button type="button" role="menuitem" data-tab-action="close-others"><i class="fa fa-times-circle-o" aria-hidden="true"></i><span>Đóng mọi tab trừ tab này</span></button>
    <button type="button" role="menuitem" data-tab-action="close-right"><i class="fa fa-step-forward" aria-hidden="true"></i><span>Đóng tab bên phải</span></button>
    <button type="button" role="menuitem" data-tab-action="close-left"><i class="fa fa-step-backward" aria-hidden="true"></i><span>Đóng tab bên trái</span></button>
    <div role="separator"></div>
    <button type="button" role="menuitem" data-tab-action="duplicate"><i class="fa fa-clone" aria-hidden="true"></i><span>Nhân bản</span></button>
    <div role="separator"></div>
    <button type="button" role="menuitem" data-tab-action="close-all"><i class="fa fa-window-close-o" aria-hidden="true"></i><span>Đóng toàn bộ tab</span></button>`;
  document.body.appendChild(contextMenu);

  const hideContextMenu = () => {
    contextMenu.classList.add("hidden");
    contextMenu.removeAttribute("data-workspace-key");
  };
  const showContextMenu = (event, key) => {
    const keys = [...workspaces.keys()], index = keys.indexOf(key);
    if (index < 0) return;
    event.preventDefault();
    contextMenu.dataset.workspaceKey = key;
    contextMenu.querySelector('[data-tab-action="close-left"]').disabled = index === 0;
    contextMenu.querySelector('[data-tab-action="close-right"]').disabled = index === keys.length - 1;
    contextMenu.querySelector('[data-tab-action="close-others"]').disabled = keys.length === 1;
    contextMenu.classList.remove("hidden");
    contextMenu.style.left = "0px";
    contextMenu.style.top = "0px";
    const bounds = contextMenu.getBoundingClientRect(), gutter = 8;
    contextMenu.style.left = `${Math.max(gutter, Math.min(event.clientX, innerWidth - bounds.width - gutter))}px`;
    contextMenu.style.top = `${Math.max(gutter, Math.min(event.clientY, innerHeight - bounds.height - gutter))}px`;
    contextMenu.querySelector("button:not(:disabled)")?.focus();
  };

  tabs.addEventListener("contextmenu", event => {
    const tab = event.target.closest("[data-workspace-key]");
    if (tab) showContextMenu(event, tab.dataset.workspaceKey);
  });
  tabs.addEventListener("keydown", event => {
    if (!(event.key === "ContextMenu" || (event.shiftKey && event.key === "F10"))) return;
    const tab = event.target.closest("[data-workspace-key]");
    if (!tab) return;
    const bounds = tab.getBoundingClientRect();
    showContextMenu({ preventDefault: () => event.preventDefault(), clientX: bounds.left + 12, clientY: bounds.bottom - 2 }, tab.dataset.workspaceKey);
  });
  contextMenu.addEventListener("click", event => {
    const action = event.target.closest("[data-tab-action]")?.dataset.tabAction;
    const key = contextMenu.dataset.workspaceKey;
    if (!action || !key) return;
    const keys = [...workspaces.keys()], index = keys.indexOf(key);
    hideContextMenu();
    if (action === "close") closeWorkspace(key);
    else if (action === "close-all") closeWorkspaces(keys);
    else if (action === "close-others") closeWorkspaces(keys.filter(candidate => candidate !== key));
    else if (action === "close-right") closeWorkspaces(keys.slice(index + 1));
    else if (action === "close-left") closeWorkspaces(keys.slice(0, index));
    else if (action === "duplicate") duplicateWorkspace(key);
  });
  contextMenu.addEventListener("keydown", event => {
    if (event.key === "Escape") {
      event.preventDefault();
      const key = contextMenu.dataset.workspaceKey;
      hideContextMenu();
      tabs.querySelector(`[data-workspace-key="${CSS.escape(key || "")}"]`)?.focus();
      return;
    }
    if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const items = [...contextMenu.querySelectorAll("button:not(:disabled)")], current = items.indexOf(document.activeElement);
    const next = event.key === "Home" ? 0 : event.key === "End" ? items.length - 1 : (current + (event.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
    items[next]?.focus();
  });
  document.addEventListener("pointerdown", event => {
    if (!contextMenu.classList.contains("hidden") && !contextMenu.contains(event.target)) hideContextMenu();
  });
  addEventListener("resize", hideContextMenu);
  tabs.addEventListener("scroll", hideContextMenu, { passive: true });

  tabs.addEventListener("dragstart", event => {
    const tab = event.target.closest("[data-workspace-key]");
    if (!tab) return;
    hideContextMenu();
    draggedWorkspaceKey = tab.dataset.workspaceKey;
    tab.classList.add("is-dragging");
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", draggedWorkspaceKey);
  });
  tabs.addEventListener("dragover", event => {
    const target = event.target.closest("[data-workspace-key]");
    if (!target || target.dataset.workspaceKey === draggedWorkspaceKey) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
    tabs.querySelectorAll(".is-drop-before,.is-drop-after").forEach(tab => tab.classList.remove("is-drop-before", "is-drop-after"));
    target.classList.add(event.clientX >= target.getBoundingClientRect().left + target.offsetWidth / 2 ? "is-drop-after" : "is-drop-before");
  });
  tabs.addEventListener("drop", event => {
    const target = event.target.closest("[data-workspace-key]");
    if (!target || !draggedWorkspaceKey) return;
    event.preventDefault();
    const sourceKey = draggedWorkspaceKey;
    draggedWorkspaceKey = null;
    reorderWorkspace(sourceKey, target.dataset.workspaceKey, event.clientX >= target.getBoundingClientRect().left + target.offsetWidth / 2);
  });
  tabs.addEventListener("dragend", () => {
    draggedWorkspaceKey = null;
    tabs.querySelectorAll(".is-dragging,.is-drop-before,.is-drop-after").forEach(tab => tab.classList.remove("is-dragging", "is-drop-before", "is-drop-after"));
  });
  tabs.addEventListener("keydown", event => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const keys = [...workspaces.keys()], current = keys.indexOf(getActiveKey());
    const next = event.key === "Home" ? 0 : event.key === "End" ? keys.length - 1 : (current + (event.key === "ArrowRight" ? 1 : -1) + keys.length) % keys.length;
    activate(keys[next]);
    tabs.querySelector(`[data-workspace-key="${CSS.escape(keys[next])}"]`)?.focus();
  });
  document.addEventListener("keydown", event => {
    const activeKey = getActiveKey();
    if (event.ctrlKey && event.key.toLowerCase() === "w" && activeKey) { event.preventDefault(); closeWorkspace(activeKey); }
    if (event.ctrlKey && (event.key === "PageDown" || event.key === "PageUp") && workspaces.size) {
      event.preventDefault();
      const keys = [...workspaces.keys()], current = keys.indexOf(activeKey);
      activate(keys[(current + (event.key === "PageDown" ? 1 : -1) + keys.length) % keys.length]);
    }
  });
}
