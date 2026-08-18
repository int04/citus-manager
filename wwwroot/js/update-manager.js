(() => {
  "use strict";

  const root = document.querySelector("[data-update-manager]");
  if (!root) return;

  const endpoint = root.dataset.endpoint || "/api/system/update";
  const statusElement = root.querySelector("[data-update-status]");
  const currentVersionElement = root.querySelector("[data-current-version]");
  const refreshButton = root.querySelector("[data-update-refresh]");
  const updateButton = root.querySelector("[data-update-start]");
  const restartNote = root.querySelector("[data-update-restart-note]");
  const token = root.querySelector("input[name='__RequestVerificationToken']")?.value || "";
  const messages = JSON.parse(root.querySelector("[data-update-messages]")?.textContent || "{}");
  const activeStates = new Set(["queued", "backingup", "pulling", "restarting"]);
  const initialVersion = normalize(currentVersionElement?.textContent);
  let polling = false;
  let targetVersion = null;

  function normalize(value) {
    return String(value || "").replace(/^v/i, "").trim();
  }

  function statusKey(value) {
    return String(value || "unavailable").replace(/[^a-z]/gi, "").toLowerCase();
  }

  function setBusy(busy) {
    for (const button of [refreshButton, updateButton]) {
      if (!button) continue;
      button.disabled = busy;
    }
    refreshButton?.classList.toggle("is-loading", busy);
    root.setAttribute("aria-busy", String(busy));
  }

  function setStatus(message, tone = "neutral", isError = false) {
    if (!statusElement) return;
    statusElement.textContent = message;
    statusElement.dataset.tone = tone;
    statusElement.setAttribute("role", isError ? "alert" : "status");
  }

  function messageFor(state, data) {
    if (state === "available") {
      return (messages.available || "{0}").replace("{0}", data.latestVersion || "");
    }
    return messages[state] || data.message || messages.unavailable || "";
  }

  function render(data) {
    const reportedState = statusKey(data.state ?? data.status);
    const executionAvailable = data.executionAvailable !== false;
    const state = !executionAvailable && (reportedState === "current" || reportedState === "available")
      ? "unavailable"
      : reportedState;
    const currentVersion = data.currentVersion || currentVersionElement?.textContent;
    const displayVersion = normalize(currentVersion).toLowerCase() === "development"
      ? "Development"
      : `v${normalize(currentVersion)}`;
    if (currentVersionElement && currentVersion) currentVersionElement.textContent = displayVersion;
    targetVersion = data.targetVersion || data.latestVersion || targetVersion;

    const canRetryFailedUpdate = state === "failed" && data.latestVersion &&
      normalize(data.latestVersion) !== normalize(currentVersion);
    const canUpdate = (state === "available" || canRetryFailedUpdate) && executionAvailable;
    if (updateButton) {
      updateButton.hidden = !canUpdate;
      updateButton.disabled = !canUpdate;
    }
    if (restartNote) restartNote.hidden = !(canUpdate || activeStates.has(state));

    const tone = state === "current" || state === "succeeded"
      ? "success"
      : state === "available" || state === "blocked"
        ? "warning"
        : state === "failed" || state === "unavailable"
          ? "error"
          : "neutral";
    setStatus(messageFor(state, data), tone, state === "failed" || state === "unavailable");

    const reachedTarget = targetVersion && normalize(currentVersion) === normalize(targetVersion);
    if ((state === "succeeded" || (polling && reachedTarget && normalize(currentVersion) !== initialVersion)) && reachedTarget) {
      setStatus(messages.succeeded, "success");
      window.setTimeout(() => window.location.reload(), 800);
    }
    return state;
  }

  async function problem(response) {
    try {
      const body = await response.json();
      return body.detail || body.title || messages.requestfailed;
    } catch {
      return messages.requestfailed;
    }
  }

  async function check(refresh = false, tolerateRestart = false) {
    try {
      const response = await fetch(`${endpoint}?refresh=${refresh}`, {
        credentials: "same-origin",
        cache: "no-store",
        headers: { Accept: "application/json" }
      });
      if (!response.ok) throw new Error(await problem(response));
      const data = await response.json();
      const state = render(data);
      if (activeStates.has(state)) schedulePoll();
      else polling = false;
    } catch (error) {
      if (tolerateRestart || polling) {
        setStatus(messages.retrying, "neutral");
        schedulePoll();
      } else {
        setStatus(error.message || messages.requestfailed, "error", true);
      }
    } finally {
      setBusy(false);
    }
  }

  function schedulePoll() {
    polling = true;
    window.setTimeout(() => check(false, true), 2000);
  }

  refreshButton?.addEventListener("click", () => {
    setBusy(true);
    setStatus(messages.checking, "neutral");
    check(true);
  });

  updateButton?.addEventListener("click", async () => {
    setBusy(true);
    try {
      const response = await fetch(endpoint, {
        method: "POST",
        credentials: "same-origin",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
          RequestVerificationToken: token,
          "X-CSRF-TOKEN": token
        },
        body: "{}"
      });
      if (response.status === 409) {
        setStatus(messages.blocked, "warning", true);
        setBusy(false);
        return;
      }
      if (response.status === 503) {
        setStatus(messages.unavailable, "error", true);
        setBusy(false);
        return;
      }
      if (!response.ok) throw new Error(await problem(response));
      const data = await response.json();
      const state = render(data);
      if (activeStates.has(state) || response.status === 202) schedulePoll();
    } catch (error) {
      setStatus(error.message || messages.requestfailed, "error", true);
      setBusy(false);
    }
  });

  setBusy(true);
  setStatus(messages.checking, "neutral");
  check(false);
})();
