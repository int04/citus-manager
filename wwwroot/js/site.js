(() => {
  const root = document.documentElement;
  const saved = localStorage.getItem("citus-manager-theme");
  const preferred = window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  root.dataset.theme = saved === "light" || saved === "dark" ? saved : preferred;
  const updateThemeLabel = () => {
    const toggle = document.getElementById("theme-toggle");
    if (toggle) toggle.setAttribute("aria-label", root.dataset.theme === "dark" ? "Chuyển sang giao diện sáng" : "Chuyển sang giao diện tối");
  };
  updateThemeLabel();
  document.getElementById("theme-toggle")?.addEventListener("click", () => {
    const next = root.dataset.theme === "light" ? "dark" : "light";
    root.dataset.theme = next;
    localStorage.setItem("citus-manager-theme", next);
    updateThemeLabel();
  });
  document.querySelectorAll("[data-password-toggle]").forEach(button => {
    button.addEventListener("click", () => {
      const input = document.getElementById(button.dataset.passwordToggle);
      if (!input) return;
      const show = input.type === "password";
      input.type = show ? "text" : "password";
      button.textContent = show ? "Ẩn" : "Hiện";
      button.setAttribute("aria-label", show ? "Ẩn mật khẩu" : "Hiện mật khẩu");
      button.setAttribute("aria-pressed", show ? "true" : "false");
    });
  });
  const clusterForm = $("[data-cluster-create-form]");
  if (clusterForm.length) {
    const testButton = $("#test-connection-button");
    const registerButton = $("#register-cluster-button");
    const result = $("#connection-test-result");
    const setBusy = (busy, activeButton) => {
      testButton.prop("disabled", busy);
      registerButton.prop("disabled", busy);
      clusterForm.attr("aria-busy", busy ? "true" : "false");
      if (!activeButton) return;
      const label = activeButton.find("span");
      if (!label.data("idle-label")) label.data("idle-label", label.text());
      label.text(busy ? "Đang xử lý…" : label.data("idle-label"));
    };
    const showResult = (kind, title, detail) => {
      const success = kind === "success";
      result
        .removeClass("hidden success error")
        .addClass(kind)
        .attr("role", success ? "status" : "alert")
        .empty()
        .append($("<strong>").addClass("block text-sm font-semibold").text(title))
        .append($("<p>").addClass("mt-1 text-xs leading-5").text(detail))
        .trigger("focus");
    };
    clusterForm.find("#Host,#Port,#Database,#Username,#Password,#SslMode").on("input change", () => {
      result.addClass("hidden").removeClass("success error").empty();
    });
    const problemText = xhr => {
      const body = xhr.responseJSON;
      if (body?.errors) {
        return Object.values(body.errors).flat().join(" ");
      }
      return body?.detail || "Yêu cầu thất bại. Kiểm tra dữ liệu và thử lại.";
    };

    testButton.on("click", () => {
      if (clusterForm.valid && !clusterForm.valid()) return;
      setBusy(true, testButton);
      result.addClass("hidden");
      $.ajax({
        url: clusterForm.data("test-url"),
        method: "POST",
        data: clusterForm.serialize(),
        headers: { "X-Requested-With": "XMLHttpRequest" }
      }).done(data => {
        showResult("success", "Kết nối thành công",
          `PostgreSQL: ${data.postgreSqlVersion} · Citus: ${data.citusVersion} · Database/User: ${data.database}/${data.user} · ${data.nodeCount} node · ${data.distributedTableCount} bảng Citus`);
      }).fail(xhr => showResult("error", "Không thể kết nối", problemText(xhr)))
        .always(() => setBusy(false, testButton));
    });

    clusterForm.on("submit", event => {
      event.preventDefault();
      if (clusterForm.valid && !clusterForm.valid()) return;
      setBusy(true, registerButton);
      result.addClass("hidden");
      $.ajax({
        url: clusterForm.attr("action"),
        method: "POST",
        data: clusterForm.serialize(),
        headers: { "X-Requested-With": "XMLHttpRequest" }
      }).done(data => {
        showResult("success", "Đăng ký thành công", "Đang chuyển tới trang chi tiết coordinator…");
        window.location.assign(data.redirectUrl);
      }).fail(xhr => {
        showResult("error", "Không thể đăng ký coordinator", problemText(xhr));
        setBusy(false, registerButton);
      });
    });
  }
  document.body.addEventListener("htmx:beforeRequest", event => {
    const button = event.detail.elt.querySelector?.("button[type=submit]");
    if (button) { button.disabled = true; button.setAttribute("aria-busy", "true"); }
  });
  document.querySelectorAll("form").forEach(form => form.addEventListener("submit", event => {
    if (event.defaultPrevented) return;
    form.setAttribute("aria-busy", "true");
    requestAnimationFrame(() => form.querySelectorAll("button[type=submit]").forEach(button => {
      button.disabled = true;
      button.setAttribute("aria-busy", "true");
    }));
  }));
})();
