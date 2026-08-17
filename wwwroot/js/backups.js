(() => {
  "use strict";
  const token = document.querySelector("#backup-antiforgery input")?.value;
  const feedback = document.getElementById("backup-feedback");
  const live = document.getElementById("backup-live-status");
  const headers = { "Content-Type": "application/json", "RequestVerificationToken": token || "" };
  const terminal = new Set(["Succeeded", "PartialSucceeded", "Failed", "Cancelled", "RecoveryRequired"]);
  const i18nNode = document.getElementById("backup-i18n-data");
  let i18n = {};
  try { i18n = JSON.parse(i18nNode?.textContent || "{}"); } catch { /* Resource JSON is optional. */ }
  const message = (key, ...values) => values.reduce(
    (text, value, index) => text.replaceAll(`{${index}}`, String(value)),
    i18n[key] || key);
  const localizedStatus = status => i18n[`status${status}`] || status;
  const localizedPhase = phase => {
    const key = `phase${String(phase || "").replace(/[^a-z0-9]/gi, "")}`;
    return i18n[key] || i18n[`status${phase}`] || phase;
  };

  const formatBytes = value => {
    let bytes = Math.max(0, Number(value) || 0), unit = 0;
    const units = ["B", "KiB", "MiB", "GiB", "TiB"];
    while (bytes >= 1024 && unit < units.length - 1) { bytes /= 1024; unit += 1; }
    return `${bytes.toLocaleString(undefined, { maximumFractionDigits: 2 })} ${units[unit]}`;
  };
  const browserTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const timeZoneOffset = timeZone => {
    try {
      const value = new Intl.DateTimeFormat("en", { timeZone, timeZoneName: "longOffset" })
        .formatToParts(new Date()).find(part => part.type === "timeZoneName")?.value;
      return value === "GMT" ? "UTC+00:00" : String(value || "").replace("GMT", "UTC");
    } catch { return ""; }
  };
  const syncTimeZoneOffset = input => {
    const output = input?.closest(".field")?.querySelector("[data-timezone-offset]");
    if (output) output.textContent = timeZoneOffset(input.value);
  };
  const applyBrowserTimeZone = input => {
    if (!input || input.dataset.timezoneDefault !== "true" || !browserTimeZone) return;
    input.value = browserTimeZone;
    const list = input.list;
    if (list && !Array.from(list.options).some(option => option.value === browserTimeZone)) {
      const option = document.createElement("option");
      option.value = browserTimeZone;
      list.append(option);
    }
    syncTimeZoneOffset(input);
  };
  document.querySelectorAll('[name="timeZone"]').forEach(input => {
    applyBrowserTimeZone(input);
    syncTimeZoneOffset(input);
    input.addEventListener("input", () => syncTimeZoneOffset(input));
    input.addEventListener("change", () => syncTimeZoneOffset(input));
  });
  const showFeedback = (message, danger = false) => {
    const target = document.querySelector("dialog[open] [data-dialog-feedback]") || feedback;
    if (!target) return;
    target.textContent = message;
    target.classList.remove("hidden", "success", "danger");
    target.classList.add(danger ? "danger" : "success");
    target.focus();
  };
  const problem = async response => {
    try { const body = await response.json(); return body.detail || body.title || message("requestFailed", response.status); }
    catch { return message("requestFailed", response.status); }
  };
  const request = async (url, method, body = {}) => {
    const response = await fetch(url, { method, credentials: "same-origin", headers, body: JSON.stringify(body) });
    if (!response.ok) throw new Error(await problem(response));
    return response.status === 204 ? null : response.json();
  };
  const setBusy = (button, busy, busyLabel) => {
    if (!button) return;
    const label = button.querySelector("span") || button;
    if (!button.dataset.idleLabel) button.dataset.idleLabel = label.textContent;
    button.disabled = busy;
    button.setAttribute("aria-busy", String(busy));
    label.textContent = busy ? busyLabel : button.dataset.idleLabel;
  };
  const statusClass = status => status === "Succeeded" ? "success" :
    ["PartialSucceeded", "RetryScheduled", "RecoveryRequired"].includes(status) ? "warning" :
      ["Failed", "Cancelled"].includes(status) ? "danger" : "neutral";
  const updateText = (row, field, text) => { const node = row.querySelector(`[data-field="${field}"]`); if (node) node.textContent = text || ""; };
  const renderDestinations = (row, destinations) => {
    const stack = row.querySelector('[data-field="destinations"]');
    if (!stack || !Array.isArray(destinations)) return;
    stack.replaceChildren(...destinations.map(destination => {
      const item = document.createElement("span");
      const dot = document.createElement("i");
      dot.className = `status-dot ${["Succeeded", "Committed"].includes(destination.status) ? "ok" : destination.status === "Failed" ? "bad" : ""}`;
      dot.setAttribute("aria-hidden", "true");
      item.append(dot, document.createTextNode(destination.name || destination.type || message("destination")));
      const detail = document.createElement("small");
      detail.textContent = `${localizedStatus(destination.status)} · ${formatBytes(destination.bytesUploaded)}`;
      item.append(detail);
      return item;
    }));
  };

  const updateSchedule = form => {
    const unit = form?.querySelector('[name="unit"]');
    if (!unit) return;
    const current = unit.value;
    form.querySelectorAll("[data-schedule-field]").forEach(field => {
      const kind = field.dataset.scheduleField;
      const visible = kind === "minute" || (kind === "hour" && current !== "Hour") ||
        (kind === "week" && current === "Week") || (kind === "month" && current === "Month");
      field.classList.toggle("hidden", !visible);
      field.querySelectorAll("input,select").forEach(input => input.disabled = !visible);
    });
  };
  document.querySelectorAll("[data-schedule-form]").forEach(form => {
    form.querySelector('[name="unit"]')?.addEventListener("change", () => updateSchedule(form));
    updateSchedule(form);
  });

  document.getElementById("backup-policy")?.addEventListener("submit", async event => {
    event.preventDefault();
    const form = event.currentTarget;
    if (!form.reportValidity()) return;
    const submit = form.querySelector("button[type=submit]");
    const data = new FormData(form);
    const body = {
      enabled: data.has("enabled"), templateId: data.get("templateId") || null,
      interval: Number(data.get("interval")), unit: data.get("unit"), minute: Number(data.get("minute") || 0),
      hour: Number(data.get("hour") || 0), dayOfWeek: data.get("unit") === "Week" ? Number(data.get("dayOfWeek")) : null,
      dayOfMonth: data.get("unit") === "Month" ? Number(data.get("dayOfMonth")) : null, timeZone: data.get("timeZone"),
      retryCount: Number(data.get("retryCount")), retentionDays: Number(data.get("retentionDays")),
      retentionMinimum: Number(data.get("retentionMinimum")), retentionMaximum: Number(data.get("retentionMaximum")),
      encryptionEnabled: data.has("encryptionEnabled"), storageProfileIds: data.getAll("storageProfileIds"),
      notificationProfileIds: data.getAll("notificationProfileIds")
    };
    if (!body.encryptionEnabled && !window.confirm(message("confirmUnencrypted"))) return;
    setBusy(submit, true, message("saving"));
    try { await request(form.dataset.url, "PUT", body); window.location.reload(); }
    catch (error) { showFeedback(error.message, true); }
    finally { setBusy(submit, false); }
  });

  const backupNow = document.getElementById("backup-now");
  if (backupNow?.dataset.configurationRequired !== "true") backupNow?.addEventListener("click", async () => {
    setBusy(backupNow, true, message("queueing"));
    try { await request(backupNow.dataset.url, "POST"); window.location.reload(); }
    catch (error) { showFeedback(error.message, true); setBusy(backupNow, false); }
  });

  document.querySelectorAll("[data-profile-form]").forEach(form => form.addEventListener("submit", async event => {
    event.preventDefault();
    if (!form.reportValidity()) return;
    const data = new FormData(form);
    let body;
    if (form.dataset.kind === "storage") {
      body = Object.fromEntries(data.entries());
    } else {
      body = Object.fromEntries(data.entries());
      body.smtpPort = Number(body.smtpPort || 587);
      body.smtpUseTls = data.has("smtpUseTls");
      body.emailRecipients = String(data.get("emailRecipients") || "").split(",").map(value => value.trim()).filter(Boolean);
      body.telegramTargets = String(data.get("telegramTargets") || "").split(",").map(value => value.trim()).filter(Boolean).map(value => {
        const [chatId, threadId] = value.split(":");
        return { chatId, threadId: threadId ? Number(threadId) : null };
      });
    }
    const submit = form.querySelector("button[type=submit]");
    setBusy(submit, true, message("saving"));
    try { await request(form.dataset.url, "POST", body); window.location.reload(); }
    catch (error) { showFeedback(error.message, true); setBusy(submit, false); }
  }));

  const syncProfileFields = form => {
    const type = form.querySelector('[name="type"]')?.value;
    form.querySelectorAll("[data-profile-types]").forEach(group => {
      const visible = String(group.dataset.profileTypes).split(",").includes(type);
      group.classList.toggle("hidden", !visible);
      group.querySelectorAll("input,select,textarea").forEach(input => { input.disabled = !visible; });
    });
  };
  document.querySelectorAll("[data-profile-form]").forEach(form => {
    form.querySelector('[name="type"]')?.addEventListener("change", () => syncProfileFields(form));
    syncProfileFields(form);
  });

  const templateForm = document.getElementById("backup-template-form");
  const templateTitle = document.querySelector("[data-template-form-title]");
  let templateItems = [];
  try { templateItems = JSON.parse(document.getElementById("backup-template-data")?.textContent || "[]"); } catch { /* No templates. */ }
  const templateById = id => templateItems.find(item => item.id === id);
  const setFormValue = (form, name, value) => {
    const input = form?.elements.namedItem(name);
    if (input && "value" in input) input.value = value ?? "";
  };
  const setProfileChecks = (form, name, values) => {
    const selected = new Set((values || []).map(String));
    form?.querySelectorAll(`[name="${name}"]`).forEach(input => { input.checked = selected.has(input.value); });
  };
  const fillTemplateFields = (form, item) => {
    ["name", "unit", "interval", "minute", "hour", "dayOfWeek", "dayOfMonth", "timeZone", "retryCount",
      "retentionDays", "retentionMinimum", "retentionMaximum"].forEach(name => setFormValue(form, name, item[name]));
    const encryption = form?.elements.namedItem("encryptionEnabled");
    if (encryption) encryption.checked = Boolean(item.encryptionEnabled);
    setProfileChecks(form, "storageProfileIds", item.storageProfileIds);
    setProfileChecks(form, "notificationProfileIds", item.notificationProfileIds);
    syncTimeZoneOffset(form?.elements.namedItem("timeZone"));
    updateSchedule(form);
  };
  const resetTemplateForm = () => {
    if (!templateForm) return;
    templateForm.reset();
    applyBrowserTimeZone(templateForm.elements.namedItem("timeZone"));
    setFormValue(templateForm, "templateId", "");
    if (templateTitle) templateTitle.textContent = templateTitle.dataset.newLabel;
    const submit = templateForm.querySelector('button[type="submit"]');
    if (submit) (submit.querySelector("span") || submit).textContent = submit.dataset.createLabel;
    setProfileChecks(templateForm, "storageProfileIds", templateForm.dataset.defaultStorageId ? [templateForm.dataset.defaultStorageId] : []);
    setProfileChecks(templateForm, "notificationProfileIds", []);
    updateSchedule(templateForm);
  };
  document.querySelectorAll("[data-template-edit]").forEach(button => button.addEventListener("click", () => {
    const item = templateById(button.dataset.templateEdit);
    if (!item || !templateForm) return;
    resetTemplateForm();
    setFormValue(templateForm, "templateId", item.id);
    fillTemplateFields(templateForm, item);
    if (templateTitle) templateTitle.textContent = templateTitle.dataset.editLabel.replace("{0}", item.name);
    const submit = templateForm.querySelector('button[type="submit"]');
    if (submit) (submit.querySelector("span") || submit).textContent = submit.dataset.updateLabel;
    templateForm.scrollIntoView({ block: "start" });
    templateForm.elements.namedItem("name")?.focus();
  }));
  document.querySelector("[data-template-new]")?.addEventListener("click", () => {
    resetTemplateForm();
    templateForm?.elements.namedItem("name")?.focus();
  });
  document.querySelectorAll("[data-template-delete]").forEach(button => button.addEventListener("click", async () => {
    const item = templateById(button.dataset.templateDelete);
    if (!window.confirm(message("confirmDeleteTemplate", item?.name || ""))) return;
    setBusy(button, true, message("deleting"));
    try { await request(button.dataset.url, "DELETE"); window.location.reload(); }
    catch (error) { showFeedback(error.message, true); setBusy(button, false); }
  }));
  templateForm?.addEventListener("submit", async event => {
    event.preventDefault();
    if (!templateForm.reportValidity()) return;
    const data = new FormData(templateForm);
    const id = data.get("templateId");
    const body = {
      name: data.get("name"), unit: data.get("unit"), interval: Number(data.get("interval")),
      minute: Number(data.get("minute") || 0), hour: Number(data.get("hour") || 0),
      dayOfWeek: Number(data.get("dayOfWeek") || 0), dayOfMonth: Number(data.get("dayOfMonth") || 1),
      timeZone: data.get("timeZone"), retryCount: Number(data.get("retryCount")),
      retentionDays: Number(data.get("retentionDays")), retentionMinimum: Number(data.get("retentionMinimum")),
      retentionMaximum: Number(data.get("retentionMaximum")), encryptionEnabled: data.has("encryptionEnabled"),
      storageProfileIds: data.getAll("storageProfileIds"), notificationProfileIds: data.getAll("notificationProfileIds")
    };
    if (!body.encryptionEnabled && !window.confirm(message("confirmUnencrypted"))) return;
    const submit = templateForm.querySelector('button[type="submit"]');
    setBusy(submit, true, message("saving"));
    try { await request(`${templateForm.dataset.url}${id ? `/${id}` : ""}`, id ? "PUT" : "POST", body); window.location.reload(); }
    catch (error) { showFeedback(error.message, true); setBusy(submit, false); }
  });

  const policyForm = document.getElementById("backup-policy");
  policyForm?.elements.namedItem("templateId")?.addEventListener("change", event => {
    const item = templateById(event.currentTarget.value);
    if (!item) return;
    fillTemplateFields(policyForm, item);
  });

  document.querySelectorAll("[data-profile-test]").forEach(button => button.addEventListener("click", async () => {
    setBusy(button, true, message("testing"));
    try { await request(button.dataset.profileTest, "POST"); showFeedback(message("profileTestSucceeded")); }
    catch (error) { showFeedback(error.message, true); }
    finally { setBusy(button, false); }
  }));

  const restoreDialog = document.getElementById("restore-dialog");
  const restoreForm = document.getElementById("restore-form");
  const targetKind = document.getElementById("targetKind");
  const syncRestoreTarget = () => {
    if (!targetKind) return;
    const option = targetKind.selectedOptions[0];
    const external = targetKind.value === "external";
    const same = option?.dataset.sameTarget === "true";
    document.querySelector("[data-external-target]")?.classList.toggle("hidden", !external);
    document.querySelector("[data-same-target-panel]")?.classList.toggle("hidden", !same);
    document.querySelectorAll("[data-external-target] input,[data-external-target] select").forEach(input => input.required = external && ["host", "port", "database"].includes(input.name));
  };
  targetKind?.addEventListener("change", syncRestoreTarget);
  document.querySelectorAll("[data-dialog-open]").forEach(button => button.addEventListener("click", () => {
    const dialog = document.getElementById(button.dataset.dialogOpen);
    if (!dialog) return;
    dialog.showModal();
    window.setTimeout(() => (dialog.querySelector("input:not([disabled]),select:not([disabled])") || dialog.querySelector("button"))?.focus(), 0);
  }));
  document.querySelectorAll("[data-dialog-close]").forEach(button => button.addEventListener("click", () => button.closest("dialog")?.close()));
  document.querySelectorAll("dialog.backup-dialog").forEach(dialog => dialog.addEventListener("click", event => { if (event.target === dialog) dialog.close(); }));

  document.addEventListener("click", async event => {
    const button = event.target.closest("[data-backup-action]");
    if (!button) return;
    const action = button.dataset.backupAction;
    if (action === "restore") {
      restoreForm.reset();
      restoreForm.elements.backupRunId.value = button.dataset.backupId;
      const confirmation = restoreForm.querySelector("[data-confirmation-value]");
      if (confirmation) confirmation.textContent = `${restoreForm.dataset.sourceDatabase} ${button.dataset.backupId}`;
      syncRestoreTarget();
      restoreDialog.showModal();
      targetKind.focus();
      return;
    }
    if (action === "cancel" && !window.confirm(message("confirmCancel"))) return;
    if (action === "delete" && !window.confirm(message("confirmDelete"))) return;
    setBusy(button, true, action === "pin" ? message("saving") : action === "delete" ? message("deleting") : message("cancelling"));
    try {
      const body = action === "pin" ? { pinned: button.dataset.pinned !== "true" } : {};
      await request(button.dataset.url, action === "pin" ? "PUT" : action === "delete" ? "DELETE" : "POST", body);
      window.location.reload();
    } catch (error) { showFeedback(error.message, true); setBusy(button, false); }
  });

  restoreForm?.addEventListener("submit", async event => {
    event.preventDefault();
    if (!restoreForm.reportValidity()) return;
    const data = new FormData(restoreForm);
    const selected = targetKind.selectedOptions[0];
    const same = selected?.dataset.sameTarget === "true";
    if (same && !data.has("maintenanceAcknowledged")) { showFeedback(message("confirmMaintenance"), true); return; }
    const expectedConfirmation = `${restoreForm.dataset.sourceDatabase} ${data.get("backupRunId")}`;
    if (same && data.get("typedConfirmation") !== expectedConfirmation) { showFeedback(message("typeExactly", expectedConfirmation), true); return; }
    const body = {
      targetClusterId: targetKind.value !== "external" ? targetKind.value : null,
      host: targetKind.value === "external" ? data.get("host") : null, port: targetKind.value === "external" ? Number(data.get("port")) : null,
      database: targetKind.value === "external" ? data.get("database") : null, username: targetKind.value === "external" ? data.get("username") : null,
      password: targetKind.value === "external" ? data.get("password") : null, sslMode: targetKind.value === "external" ? data.get("sslMode") : null,
      maintenanceAcknowledged: data.has("maintenanceAcknowledged"), typedConfirmation: data.get("typedConfirmation") || null
    };
    const submit = restoreForm.querySelector("button[type=submit]");
    setBusy(submit, true, message("validating"));
    try { await request(`/api/backup-runs/${data.get("backupRunId")}/restores`, "POST", body); restoreDialog.close(); window.location.reload(); }
    catch (error) { showFeedback(error.message, true); setBusy(submit, false); }
  });

  const pollRow = async row => {
    try {
      const response = await fetch(row.dataset.progressUrl, { credentials: "same-origin", cache: "no-store" });
      if (!response.ok) return;
      const progress = await response.json();
      const status = row.querySelector('[data-field="status"]');
      if (status) { status.textContent = localizedStatus(progress.status); status.dataset.status = progress.status; status.className = `status-pill ${statusClass(progress.status)}`; }
      updateText(row, "phase", localizedPhase(progress.phase));
      updateText(row, "bytes", formatBytes(progress.bytesProcessed));
      updateText(row, "estimate", progress.sourceBytesEstimate == null ? message("rawEstimateUnavailable") : message("rawEstimate", formatBytes(progress.sourceBytesEstimate)));
      updateText(row, "artifact", progress.artifactBytes == null ? message("archivePending") : message("archive", formatBytes(progress.artifactBytes)));
      updateText(row, "throughput", progress.bytesPerSecond ? `${formatBytes(progress.bytesPerSecond)}/s` : message("waitingByteStream"));
      const objectValue = progress.objectsTotal == null ? (progress.objectsCompleted || 0) : `${progress.objectsCompleted || 0}/${progress.objectsTotal}`;
      updateText(row, "objects", message("objects", objectValue));
      updateText(row, "retry", progress.retryAt ? message("retry", new Date(progress.retryAt).toLocaleString()) : "");
      const error = row.querySelector('[data-field="error"]');
      if (error) { error.textContent = progress.safeError || ""; error.classList.toggle("hidden", !progress.safeError); }
      renderDestinations(row, progress.destinations);
      if (live) live.textContent = message("processed", localizedPhase(progress.phase), formatBytes(progress.bytesProcessed));
      if (terminal.has(progress.status)) window.setTimeout(() => window.location.reload(), 750);
    } catch { /* A transient poll failure must not overwrite durable server state. */ }
  };
  const poll = () => document.querySelectorAll("[data-progress-url]").forEach(row => {
    const statusNode = row.querySelector('[data-field="status"]');
    const status = statusNode?.dataset.status || statusNode?.textContent.trim();
    if (!terminal.has(status)) pollRow(row);
  });
  if (document.querySelector("[data-progress-url]")) { poll(); window.setInterval(poll, 5000); }
})();
