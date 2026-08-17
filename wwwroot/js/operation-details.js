(() => {
    "use strict";

    const panel = document.querySelector("[data-operation-progress]");
    if (!panel || panel.dataset.active !== "true") return;

    const terminal = new Set(["Succeeded", "Failed", "Cancelled", "RecoveryRequired", "PartialSucceeded"]);
    const phase = panel.querySelector("h2");
    const detail = panel.querySelector("[data-progress-detail]");
    const bar = panel.querySelector("progress");
    const percentLabel = panel.querySelector(".operation-progress-percent");
    const statusLabel = document.querySelector("[data-operation-status]");
    const elapsedLabel = panel.querySelector("[data-progress-elapsed]");
    const updatedLabel = panel.querySelector("[data-progress-updated]");
    let timer;
    let request;
    let failures = 0;

    function percentage(progress) {
        const explicit = progress.topologyProgress?.percent;
        if (Number.isFinite(explicit)) return explicit;
        if (progress.totalBytes > 0 && Number.isFinite(progress.processedBytes))
            return progress.processedBytes * 100 / progress.totalBytes;
        if (progress.totalItems > 0 && Number.isFinite(progress.currentItems))
            return progress.currentItems * 100 / progress.totalItems;
        return null;
    }

    function render(progress) {
        const percent = percentage(progress);
        phase.textContent = progress.phase || progress.status;
        if (detail) detail.textContent = progress.warning || progress.safeError || progress.topologyProgress?.currentTable || "Đang xử lý…";
        if (statusLabel) statusLabel.textContent = progress.status;
        if (elapsedLabel && progress.elapsed) elapsedLabel.textContent = `Elapsed ${progress.elapsed}`;
        if (updatedLabel) updatedLabel.textContent = `Updated ${new Date().toLocaleTimeString()}`;

        if (percent === null) {
            bar.removeAttribute("value");
            percentLabel.textContent = "…";
            return;
        }
        const safe = Math.max(0, Math.min(100, percent));
        bar.value = safe;
        percentLabel.textContent = `${Math.round(safe)}%`;
    }

    function schedule() {
        const delay = Math.min(30000, 4000 * (2 ** failures));
        timer = window.setTimeout(poll, delay);
    }

    async function poll() {
        if (document.hidden || request) return;
        request = new AbortController();
        try {
            const response = await fetch(`/api/operations/${panel.dataset.operationId}/progress`, {
                signal: request.signal,
                headers: { Accept: "application/json" }
            });
            if (!response.ok) throw new Error(`Progress request failed: ${response.status}`);
            const progress = await response.json();
            failures = 0;
            render(progress);
            if (terminal.has(progress.status)) {
                window.location.reload();
                return;
            }
            schedule();
        } catch (error) {
            if (error.name !== "AbortError") {
                failures++;
                schedule();
            }
        } finally {
            request = null;
        }
    }

    document.addEventListener("visibilitychange", () => {
        if (document.hidden) {
            window.clearTimeout(timer);
            request?.abort();
            return;
        }
        failures = 0;
        poll();
    });

    poll();
})();
