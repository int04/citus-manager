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
  const readProblem = async response => {
    let body = null;
    try { body = await response.json(); } catch { /* non-JSON response */ }
    const message = body?.errors ? Object.values(body.errors).flat().join(" ") :
      body?.detail || body?.title || `Request failed (${response.status}).`;
    return { body: body || {}, message };
  };
  const problemText = async response => (await readProblem(response)).message;
  const operationId = body => body?.operationId || body?.id || body?.operation?.id;
  const newIdempotencyKey = () => globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const endpointText = (host, port) => host && port ? `${host}:${port}` : "";
  const workerEndpointKey = form => {
    const host = form.elements.host?.value.trim().toLowerCase() || "";
    const port = Number(form.elements.port?.value || 0);
    return host && Number.isInteger(port) && port > 0 ? `${host}:${port}` : "";
  };

  const syncWorkerSubmit = form => {
    const submit = form.querySelector("[data-worker-submit]");
    if (!submit) return;
    const validConnection = form.dataset.workerConnectionState === "success" &&
      form.dataset.testedWorkerEndpoint === workerEndpointKey(form);
    submit.disabled = form.getAttribute("aria-busy") === "true" || !validConnection;
  };

  const setWorkerConnectionState = (form, state, message) => {
    const status = form.querySelector("[data-worker-connection-status]");
    form.dataset.workerConnectionState = state;
    if (state !== "success") delete form.dataset.testedWorkerEndpoint;
    if (status) {
      status.className = `worker-connection-status ${state}`;
      status.textContent = message;
      status.setAttribute("role", state === "error" ? "alert" : "status");
    }
    syncWorkerSubmit(form);
  };

  const invalidateWorkerConnection = (form, changed = false) => {
    form._workerConnectionController?.abort();
    setWorkerConnectionState(form, "pending", changed
      ? t("WorkerConnectionChanged", "Host or port changed. Test the connection again.")
      : t("WorkerConnectionRequired", "Test this endpoint successfully before adding the worker."));
  };

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
    syncWorkerSubmit(form);
  };

  const testWorkerConnection = async form => {
    const hostInput = form.elements.host, portInput = form.elements.port;
    if (!hostInput.reportValidity() || !portInput.reportValidity()) return;
    const endpoint = workerEndpointKey(form);
    const button = form.querySelector("[data-test-worker-connection]");
    const label = button?.querySelector("span");
    const controller = new AbortController();
    form._workerConnectionController?.abort();
    form._workerConnectionController = controller;
    button.disabled = true;
    button.setAttribute("aria-busy", "true");
    if (label) label.textContent = t("TestingWorkerConnection", "Testing connection…");
    setWorkerConnectionState(form, "checking", t("TestingWorkerConnection", "Testing connection…"));
    try {
      const response = await fetch(`${apiRoot}/test-worker-connection`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json", RequestVerificationToken: token, "X-CSRF-TOKEN": token },
        body: JSON.stringify({ host: hostInput.value.trim(), port: Number(portInput.value) }),
        signal: controller.signal
      });
      if (!response.ok) throw new Error(await problemText(response));
      const result = await response.json();
      if (!result.success) throw new Error(result.message || t("WorkerConnectionFailed", "Connection check failed."));
      if (endpoint !== workerEndpointKey(form)) return;
      form.dataset.testedWorkerEndpoint = endpoint;
      const detail = `${result.host}:${result.port} · PostgreSQL ${result.postgreSqlVersion} · Citus ${result.citusVersion}`;
      setWorkerConnectionState(form, "success", `${t("WorkerConnectionSucceeded", "Connection and compatibility checks succeeded.")} ${detail}`);
    } catch (error) {
      if (error.name === "AbortError") return;
      setWorkerConnectionState(form, "error", `${t("WorkerConnectionFailed", "Connection check failed.")} ${error.message}`);
    } finally {
      if (form._workerConnectionController === controller) {
        form._workerConnectionController = null;
        button.disabled = false;
        button.removeAttribute("aria-busy");
        if (label) label.textContent = t("TestWorkerConnection", "Test connection");
      }
    }
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
    if (form.dataset.operation === "add-worker") invalidateWorkerConnection(form);
    form.dataset.idempotencyKey = newIdempotencyKey();
    const host = trigger.dataset.host || "", port = trigger.dataset.port || "5432";
    if (form.elements.host) form.elements.host.value = host;
    if (form.elements.port) form.elements.port.value = port;
    if (form.dataset.operation === "change-coordinator") {
      const currentHost = trigger.dataset.currentHost || "";
      const currentPort = trigger.dataset.currentPort || "5432";
      dialog.querySelectorAll("[data-current-coordinator]").forEach(node => node.textContent = endpointText(currentHost, currentPort));
      dialog.dataset.confirmValue = "";
      dialog.querySelectorAll("[data-confirm-label]").forEach(node => node.textContent = "");
      configureCoordinatorRecovery(form);
      updateCoordinatorTarget(form);
    }
    dialog.querySelectorAll("[data-target-node]").forEach(node => node.textContent = `${host}:${port}`);
    if (form.dataset.operation !== "change-coordinator") {
      dialog.querySelectorAll("[data-confirm-label]").forEach(node => node.textContent = host);
      dialog.dataset.confirmValue = host;
    }
    dialog._trigger = trigger;
    dialog.showModal();
    requestAnimationFrame(() => form.querySelector("input:not([type='hidden']), button[type='submit']")?.focus());
    if (form.querySelector("[data-preview]")) loadPreview(form);
  };

  const updateCoordinatorTarget = form => {
    const dialog = form.closest("dialog");
    const host = form.elements.targetHost?.value.trim() || "";
    const port = form.elements.targetPort?.value || "";
    const endpoint = endpointText(host, port);
    dialog.dataset.confirmValue = endpoint;
    dialog.querySelectorAll("[data-confirm-label]").forEach(node => node.textContent = endpoint);
    dialog.querySelectorAll("[data-target-coordinator]").forEach(node => node.textContent = endpoint || "—");
  };

  const configureCoordinatorRecovery = (form, problem = null) => {
    const panel = form.querySelector("[data-coordinator-recovery]");
    if (!panel) return;
    const enabled = Boolean(problem?.restoreRecoveryId && problem?.remediationEndpoint);
    panel.hidden = !enabled;
    panel.disabled = !enabled;
    panel.querySelectorAll("input,textarea").forEach(input => { input.disabled = !enabled; });
    form.dataset.recoveryRestoreId = enabled ? problem.restoreRecoveryId : "";
    form.dataset.recoveryEndpoint = enabled ? problem.remediationEndpoint : "";
    panel.querySelector("[data-coordinator-recovery-message]").textContent = enabled ? problem.detail || "" : "";
    panel.querySelector("[data-coordinator-recovery-id]").textContent = enabled ? problem.restoreRecoveryId : "";
    if (enabled) panel.querySelector("input")?.focus();
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
    if (kind === "change-coordinator") return {
      path: "coordinator-migrations", body: {
        ...common,
        targetHost: form.elements.targetHost.value.trim(),
        targetPort: Number(form.elements.targetPort.value),
        typedConfirmation: form.elements.typedConfirmation.value
      }
    };
    if (kind === "rebalance") return { path: "rebalance", body: common };
    const node = { ...common, host: form.elements.host.value, port: Number(form.elements.port.value) };
    if (kind === "retire") node.typedConfirmation = form.elements.typedConfirmation.value;
    return { path: kind === "drain" ? "drain-worker" : "retire-worker", body: node };
  };

  document.addEventListener("click", event => {
    const trigger = event.target.closest("[data-open-operation]");
    if (trigger && (root.contains(trigger) || trigger.hasAttribute("data-cluster-header-operation"))) { event.preventDefault(); trigger.closest("details")?.removeAttribute("open"); openDialog(trigger); return; }
    const refresh = event.target.closest("[data-refresh-preview]");
    if (refresh && root.contains(refresh)) { event.preventDefault(); loadPreview(refresh.closest("form")); }
    const testConnection = event.target.closest("[data-test-worker-connection]");
    if (testConnection && root.contains(testConnection)) { event.preventDefault(); testWorkerConnection(testConnection.closest("form")); }
  });

  root.querySelectorAll("[data-operation-dialog]").forEach(dialog => {
    dialog.addEventListener("click", event => {
      if (event.target === dialog) dialog.close("cancel");
    });
    dialog.addEventListener("close", () => {
      const form = dialog.querySelector("form");
      form?._previewController?.abort();
      form?._workerConnectionController?.abort();
      dialog._trigger?.focus();
    });
  });

  root.querySelectorAll("[data-operation-form]").forEach(form => form.addEventListener("submit", async event => {
    if (event.submitter?.value === "cancel") return;
    event.preventDefault();
    if (!form.reportValidity()) return;
    if (form.dataset.operation === "add-worker" &&
        (form.dataset.workerConnectionState !== "success" ||
         form.dataset.testedWorkerEndpoint !== workerEndpointKey(form))) {
      setWorkerConnectionState(form, "pending",
        t("WorkerConnectionRequired", "Test this endpoint successfully before adding the worker."));
      form.querySelector("[data-test-worker-connection]")?.focus();
      return;
    }
    const dialog = form.closest("dialog");
    const confirmation = form.querySelector("[data-confirm-input]");
    if (confirmation && confirmation.value !== dialog.dataset.confirmValue) {
      const message = form.dataset.operation === "change-coordinator"
        ? t("TargetConfirmationMismatch", "Confirmation must exactly match the target endpoint.")
        : t("ConfirmationMismatch", "Confirmation must exactly match worker host.");
      showFeedback(form, message);
      confirmation.focus();
      return;
    }
    setBusy(form, true); showFeedback(form, "");
    try {
      if (form.dataset.operation === "change-coordinator" && form.dataset.recoveryRestoreId) {
        if (form.elements.recoveryConfirmation.value !== form.dataset.recoveryRestoreId) {
          showFeedback(form, t("RecoveryConfirmationMismatch", "Confirmation must exactly match the restore ID."));
          form.elements.recoveryConfirmation.focus();
          return;
        }
        const recoveryResponse = await fetch(form.dataset.recoveryEndpoint, {
          method: "POST",
          headers: { "Content-Type": "application/json", Accept: "application/json", RequestVerificationToken: token, "X-CSRF-TOKEN": token },
          body: JSON.stringify({
            manualRecoveryCompleted: Boolean(form.elements.recoveryCompleted.checked),
            typedConfirmation: form.elements.recoveryConfirmation.value,
            resolutionNote: form.elements.recoveryNote.value
          })
        });
        if (!recoveryResponse.ok) throw new Error(await problemText(recoveryResponse));
        configureCoordinatorRecovery(form);
      }
      const request = requestFor(form);
      const response = await fetch(`${apiRoot}/${request.path}`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json", RequestVerificationToken: token, "X-CSRF-TOKEN": token },
        body: JSON.stringify(request.body)
      });
      if (!response.ok) {
        const problem = await readProblem(response);
        if (form.dataset.operation === "change-coordinator" &&
            problem.body.blockerKind === "RestoreRecoveryRequired") {
          showFeedback(form, problem.message);
          configureCoordinatorRecovery(form, { ...problem.body, detail: problem.message });
          return;
        }
        throw new Error(problem.message);
      }
      const body = await response.json();
      const id = operationId(body);
      if (!id) throw new Error(t("MissingOperationId", "Operation queued, but response did not include its identifier."));
      dialog.close("submitted");
      location.assign(operationUrl.replace("{id}", encodeURIComponent(id)));
    } catch (error) {
      showFeedback(form, error.message);
    } finally { setBusy(form, false); }
  }, { capture: true }));

  root.querySelectorAll('[data-operation-form][data-operation="change-coordinator"]').forEach(form => {
    [form.elements.targetHost, form.elements.targetPort].forEach(input => input?.addEventListener("input", () => {
      form.elements.typedConfirmation.value = "";
      updateCoordinatorTarget(form);
    }));
  });

  root.querySelectorAll('[data-operation-form][data-operation="add-worker"]').forEach(form => {
    [form.elements.host, form.elements.port].forEach(input => input?.addEventListener("input", () =>
      invalidateWorkerConnection(form, true)));
    invalidateWorkerConnection(form);
  });

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
    document.querySelectorAll("[data-cluster-header-operation],#cluster-operations [data-open-operation]").forEach(button => { button.disabled = true; button.title = t("ActiveConflict", "Another topology operation is active."); });
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
