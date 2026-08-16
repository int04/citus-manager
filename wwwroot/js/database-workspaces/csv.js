import { html, problem } from "./shared.js";

export function createCsvActions({ stage, explorer, token, showError, loadRows }) {
  const t = (key, ...args) => window.CitusI18n?.t(key, ...args) ?? key;
  async function exportCsv(workspace, currentPageOnly) {
    try {
      const response = await fetch(explorer.dataset.workspaceCsvExportUrl, {
        method: "POST",
        headers: { "Content-Type": "application/json", "RequestVerificationToken": token },
        body: JSON.stringify({
          schema: workspace.schema,
          objectName: workspace.name,
          nodeId: explorer.dataset.nodeId ? Number(explorer.dataset.nodeId) : null,
          page: workspace.page,
          pageSize: workspace.pageSize,
          where: workspace.where || null,
          orderBy: workspace.orderBy || null,
          currentPageOnly
        })
      });
      if (!response.ok) throw new Error(await problem(response));
      const anchor = document.createElement("a");
      anchor.href = URL.createObjectURL(await response.blob());
      anchor.download = `${workspace.schema}.${workspace.name}${currentPageOnly ? `.page-${workspace.page}` : ""}.csv`;
      anchor.click();
      URL.revokeObjectURL(anchor.href);
    } catch (error) {
      showError(error.message);
    }
  }

  async function previewCsvImport(workspace, file) {
    if (file.size > 25 * 1024 * 1024) { showError(t("csv.tooLarge")); return; }
    const form = new FormData();
    form.append("file", file);
    try {
      const response = await fetch(explorer.dataset.workspaceCsvPreviewUrl, { method: "POST", headers: { "RequestVerificationToken": token }, body: form });
      if (!response.ok) throw new Error(await problem(response));
      showCsvImportModal(workspace, file, await response.json());
    } catch (error) {
      showError(error.message);
    }
  }

  function showCsvImportModal(workspace, file, preview) {
    const modal = document.createElement("div");
    modal.className = "database-modal";
    modal.setAttribute("role", "dialog");
    modal.setAttribute("aria-modal", "true");
    modal.innerHTML = `<div class="database-modal-card database-csv-card"><div class="database-action-heading"><div><p class="eyebrow">${t("csv.previewTitle")}</p><h2>${html(file.name)} → ${html(workspace.schema)}.${html(workspace.name)}</h2></div><button type="button" data-csv-close class="database-action-close" aria-label="${t("common.close")}">×</button></div><p class="pma-modal-copy">${t("csv.previewHelp")}</p><div class="database-csv-preview"><table><thead><tr>${preview.headers.map(header => `<th>${html(header)}</th>`).join("")}</tr></thead><tbody>${preview.rows.map(row => `<tr>${row.map(value => `<td>${html(value ?? "NULL")}</td>`).join("")}</tr>`).join("")}</tbody></table></div><div class="form-actions"><button type="button" class="btn btn-ghost" data-csv-close>${t("common.cancel")}</button><button type="button" class="btn btn-primary" data-csv-confirm>${t("csv.import")}${preview.isTruncated ? ` ${t("csv.entireFile")}` : ""}</button></div></div>`;
    document.body.appendChild(modal);
    const close = () => { modal.remove(); stage.querySelector("[data-csv-file]")?.setAttribute("value", ""); };
    modal.querySelectorAll("[data-csv-close]").forEach(button => { button.onclick = close; });
    modal.onclick = event => { if (event.target === modal) close(); };
    modal.querySelector("[data-csv-confirm]").onclick = async event => {
      const button = event.currentTarget;
      button.disabled = true;
      button.textContent = t("csv.importing");
      const form = new FormData();
      form.append("schema", workspace.schema);
      form.append("objectName", workspace.name);
      form.append("file", file);
      try {
        const response = await fetch(explorer.dataset.workspaceCsvImportUrl, { method: "POST", headers: { "RequestVerificationToken": token }, body: form });
        if (!response.ok) throw new Error(await problem(response));
        close();
        workspace.exactCount = null;
        workspace.observedMinimum = 0;
        await loadRows(workspace);
      } catch (error) {
        button.disabled = false;
        button.textContent = t("csv.import");
        showError(error.message);
      }
    };
    modal.querySelector("[data-csv-confirm]").focus();
  }

  return { exportCsv, previewCsvImport };
}
