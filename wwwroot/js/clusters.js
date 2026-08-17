(() => {
  "use strict";

  const root = document.querySelector("[data-cluster-operations]");
  if (!root) return;

  const apiRoot = root.dataset.apiRoot.replace(/\/$/, "");
  const operationUrl = root.dataset.operationUrl || "/Operations/Details/{id}";
  const t = (name, fallback) => root.dataset[`i18n${name}`] || fallback;
  const token = document.querySelector("#cluster-operation-token input[name='__RequestVerificationToken']")?.value || "";
  const dialogs = new Map([...root.querySelectorAll("[data-operation-dialog]")].map(dialog => [dialog.dataset.operationDialog, dialog]));
  const formatNumber = value => new Intl.NumberFormat(document.documentElement.lang || undefined).format(Number(value || 0));
  const formatBytes = value => {
    let bytes = Number(value || 0);
    if (!Number.isFinite(bytes)) return "—";
    const units = ["B", "KiB", "MiB", "GiB", "TiB"];
    let unit = 0;
    while (bytes >= 1024 && unit < units.length - 1) { bytes /= 1024; unit += 1; }
    return `${new Intl.NumberFormat(document.documentElement.lang || undefined, { maximumFractionDigits: unit ? 1 : 0 }).format(bytes)} ${units[unit]}`;
  };
  const problemText = async response => {
    let body = null;
    try { body = await response.json(); } catch { /* non-JSON response */ }
    if (body?.errors) return Object.values(body.errors).flat().join(" ");
    return body?.detail || body?.title || `Request failed (${response.status}).`;
  };
  const operationId = body => body?.operationId || body?.id || body?.operation?.id;
  const newIdempotencyKey = () => globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(16).slice(2)}`;

  const showFeedback = (form, message) => {
    const feedback = form.querySelector("[data-dialog-feedback]");
    feedback.textContent = message;
    feedback.hidden = !message;
    if (message) feedback.focus?.();
  };

  const setBusy = (form, busy) => {
    form.setAttribute("aria-busy", String(busy));
    form.querySelectorAll("button").forEach(button => {
      if (button.value === "cancel") return;
      button.disabled = busy;
    });
  };

  const previewEndpoint = (form) => {
    const params = new URLSearchParams({ drainOnly: String(form.querySelector("[data-preview]")?.dataset.drainOnly === "true") });
    const host = form.elements.host?.value;
    const port = form.elements.port?.value;
    if (host) params.set("workerHost", host);
    if (port) params.set("workerPort", port);
    return `${apiRoot}/previews/rebalance?${params}`;
  };

  const renderPreview = (target, preview) => {
    target.replaceChildren();
    const summary = document.createElement("div");
    summary.className = "preview-metrics";
    const metrics = [
      [target.dataset.drainOnly === "true" ? t("PlacementsToMove", "Placements to move") : t("Moves", "Moves"), preview.moveCount ?? preview.totalMoves ?? preview.placementCount ?? 0],
      [t("DataToMove", "Data to move"), formatBytes(preview.totalBytes ?? preview.bytesToMove)],
      [t("AvailableTargets", "Available targets"), preview.availableTargetCount ?? preview.targetCount ?? "—"]
    ];
    metrics.forEach(([label, value]) => {
      const item = document.createElement("div"), term = document.createElement("span"), amount = document.createElement("strong");
      term.textContent = label; amount.textContent = typeof value === "number" ? formatNumber(value) : String(value); item.append(term, amount); summary.appendChild(item);
    });
    target.appendChild(summary);

    const rows = preview.summaries || preview.moves || preview.sourceTargetSummaries || [];
    if (rows.length) {
      const wrap = document.createElement("div"), table = document.createElement("table"), head = document.createElement("thead"), body = document.createElement("tbody");
      wrap.className = "preview-table";
      const headerRow = document.createElement("tr");
      [t("Source", "Source"), t("Target", "Target"), t("Table", "Table"), t("Shard", "Shard"), t("Bytes", "Bytes")].forEach(label => { const th = document.createElement("th"); th.textContent = label; headerRow.appendChild(th); });
      head.appendChild(headerRow);
      rows.slice(0, 20).forEach(row => {
        const tr = document.createElement("tr");
        [row.source || row.sourceNode || (row.sourceHost ? `${row.sourceHost}:${row.sourcePort || 5432}` : "—"), row.target || row.targetNode || (row.targetHost ? `${row.targetHost}:${row.targetPort || 5432}` : "—"), row.table || row.tableName || "—", row.shardId ?? row.moveCount ?? row.moves ?? "—", formatBytes(row.bytes)].forEach(value => { const td = document.createElement("td"); td.textContent = String(value); tr.appendChild(td); });
        body.appendChild(tr);
      });
      table.append(head, body); wrap.appendChild(table); target.appendChild(wrap);
    }

    const warnings = preview.warnings || [];
    warnings.forEach(message => { const warning = document.createElement("p"); warning.className = "preview-warning"; warning.textContent = message; target.appendChild(warning); });
    const stamp = document.createElement("small");
    stamp.className = "preview-stamp";
    stamp.textContent = `${t("Snapshot", "Snapshot")}: ${new Date(preview.snapshotAt || preview.collectedAt || Date.now()).toLocaleString()}`;
    target.appendChild(stamp);
    target.dataset.fingerprint = preview.topologyFingerprint || "";
  };

  const loadPreview = async form => {
    const target = form.querySelector("[data-preview]");
    if (!target) return;
    target.setAttribute("aria-busy", "true");
    target.innerHTML = '<div class="preview-loading"><span class="spinner" aria-hidden="true"></span><span>Loading safe movement preview…</span></div>';
    const controller = new AbortController();
    form._previewController?.abort();
    form._previewController = controller;
    try {
      const response = await fetch(previewEndpoint(form), { headers: { Accept: "application/json" }, signal: controller.signal });
      if (!response.ok) throw new Error(await problemText(response));
      renderPreview(target, await response.json());
    } catch (error) {
      if (error.name === "AbortError") return;
      target.innerHTML = "";
      const message = document.createElement("div");
      message.className = "preview-error"; message.setAttribute("role", "alert"); message.textContent = error.message;
      target.appendChild(message);
    } finally { target.removeAttribute("aria-busy"); }
  };

  const openDialog = trigger => {
    const dialog = dialogs.get(trigger.dataset.openOperation);
    if (!dialog) return;
    const form = dialog.querySelector("[data-operation-form]");
    form.reset();
    showFeedback(form, "");
    form.dataset.idempotencyKey = newIdempotencyKey();
    const host = trigger.dataset.host || "", port = trigger.dataset.port || "5432";
    if (form.elements.host) form.elements.host.value = host;
    if (form.elements.port) form.elements.port.value = port;
    dialog.querySelectorAll("[data-target-node]").forEach(node => node.textContent = `${host}:${port}`);
    dialog.querySelectorAll("[data-confirm-label]").forEach(node => node.textContent = host);
    dialog.dataset.confirmValue = host;
    dialog._trigger = trigger;
    dialog.showModal();
    requestAnimationFrame(() => form.querySelector("input:not([type='hidden']), button[type='submit']")?.focus());
    if (form.querySelector("[data-preview]")) loadPreview(form);
  };

  const requestFor = form => {
    const kind = form.dataset.operation;
    const common = {
      externalCapacityAndBackupChecksAcknowledged: Boolean(form.elements.ack?.checked),
      idempotencyKey: form.dataset.idempotencyKey
    };
    if (kind === "add-worker" || kind === "add-query") return {
      path: "add-node", body: { ...common, role: kind === "add-worker" ? "Worker" : "QueryCoordinator", host: form.elements.host.value.trim(), port: Number(form.elements.port.value), rebalanceAfterAdd: kind === "add-worker" && Boolean(form.elements.rebalanceAfterAdd?.checked) }
    };
    if (kind === "rebalance") return { path: "rebalance", body: common };
    const node = { ...common, host: form.elements.host.value, port: Number(form.elements.port.value) };
    if (kind === "retire") node.typedConfirmation = form.elements.typedConfirmation.value;
    return { path: kind === "drain" ? "drain-worker" : "retire-worker", body: node };
  };

  root.addEventListener("click", event => {
    const trigger = event.target.closest("[data-open-operation]");
    if (trigger) { event.preventDefault(); trigger.closest("details")?.removeAttribute("open"); openDialog(trigger); return; }
    const refresh = event.target.closest("[data-refresh-preview]");
    if (refresh) { event.preventDefault(); loadPreview(refresh.closest("form")); }
  });

  root.querySelectorAll("[data-operation-dialog]").forEach(dialog => {
    dialog.addEventListener("click", event => {
      if (event.target === dialog) dialog.close("cancel");
    });
    dialog.addEventListener("close", () => {
      dialog.querySelector("form")?._previewController?.abort();
      dialog._trigger?.focus();
    });
  });

  root.querySelectorAll("[data-operation-form]").forEach(form => form.addEventListener("submit", async event => {
    if (event.submitter?.value === "cancel") return;
    event.preventDefault();
    if (!form.reportValidity()) return;
    const dialog = form.closest("dialog");
    if (form.dataset.operation === "retire" && form.elements.typedConfirmation.value !== dialog.dataset.confirmValue) {
      showFeedback(form, t("ConfirmationMismatch", "Confirmation must exactly match worker host."));
      form.elements.typedConfirmation.focus();
      return;
    }
    setBusy(form, true); showFeedback(form, "");
    try {
      const request = requestFor(form);
      const response = await fetch(`${apiRoot}/${request.path}`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json", RequestVerificationToken: token, "X-CSRF-TOKEN": token },
        body: JSON.stringify(request.body)
      });
      if (!response.ok) throw new Error(await problemText(response));
      const body = await response.json();
      const id = operationId(body);
      if (!id) throw new Error(t("MissingOperationId", "Operation queued, but response did not include its identifier."));
      dialog.close("submitted");
      location.assign(operationUrl.replace("{id}", encodeURIComponent(id)));
    } catch (error) {
      showFeedback(form, error.message);
    } finally { setBusy(form, false); }
  }, { capture: true }));

  // Compact active-only polling: one request, pauses in hidden tabs, backs off on failure.
  const live = document.querySelector("#topology-live-status");
  if (!live) return;
  let timer = 0, controller = null, failures = 0, stopped = false, hadActive = false;
  const terminal = new Set(["Succeeded", "Failed", "Cancelled", "RecoveryRequired", "PartialSucceeded"]);
  const schedule = delay => { clearTimeout(timer); if (!stopped && !document.hidden) timer = setTimeout(pollActive, delay); };
  const renderActive = summary => {
    const active = summary?.operation || summary?.activeOperation || (summary?.id ? summary : null);
    if (!active) { live.hidden = true; stopped = true; return true; }
    hadActive = true;
    const progress = active.progress || active.progressSnapshot || active;
    const percent = Math.max(0, Math.min(100, Number(progress.percent ?? 0)));
    live.hidden = false;
    live.querySelector("[data-live-status]").textContent = active.status || "Running";
    live.querySelector("[data-live-title]").textContent = active.kind || "Topology operation";
    live.querySelector("[data-live-detail]").textContent = progress.phase || progress.currentPhase || progress.detail || t("PreparingOperation", "Preparing operation");
    const bar = live.querySelector("[data-live-progress]");
    if (progress.percent == null) bar.removeAttribute("value"); else bar.value = percent;
    live.querySelector("[data-live-percent]").textContent = progress.percent == null ? "…" : `${Math.round(percent)}%`;
    live.querySelector("[data-live-link]").href = operationUrl.replace("{id}", encodeURIComponent(active.id || active.operationId));
    root.querySelectorAll("[data-open-operation]").forEach(button => { button.disabled = true; button.title = t("ActiveConflict", "Another topology operation is active."); });
    root.querySelectorAll("[data-node-card]").forEach(card => card.classList.toggle("has-active-operation", !progress.host || card.dataset.nodeHost === progress.host));
    return terminal.has(active.status);
  };
  async function pollActive() {
    if (document.hidden || controller) return;
    controller = new AbortController();
    try {
      const response = await fetch(`${apiRoot}/active-summary`, { headers: { Accept: "application/json" }, signal: controller.signal });
      if (!response.ok) throw new Error(String(response.status));
      failures = 0;
      const done = renderActive(await response.json());
      if (done) { stopped = true; if (hadActive) setTimeout(() => location.reload(), 800); return; }
      schedule(4000);
    } catch (error) {
      if (error.name !== "AbortError") { failures += 1; schedule(Math.min(30000, 4000 * (2 ** failures))); }
    } finally { controller = null; }
  }
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) { clearTimeout(timer); controller?.abort(); }
    else { failures = 0; pollActive(); }
  });
  pollActive();
})();
