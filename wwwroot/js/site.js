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
  const databaseExplorer = $("[data-database-explorer]");
  if (databaseExplorer.length) {
    const result = $("#database-result");
    const feedback = $("#database-feedback");
    const sqlConsole = $("#sql-console");
    const token = $("#database-antiforgery input[name='__RequestVerificationToken']").val();
    const nodeId = databaseExplorer.data("node-id") || null;
    const navigation = $("#database-navigation");
    const navigationToggle = $("#database-navigation-toggle");
    const navigationScrim = $("#database-navigation-scrim");
    const mobileNavigation = window.matchMedia("(max-width: 1023px)");
    let selectedSchema = null;
    let selectedTable = null;
    let activeTab = "data";
    let activeSqlRequest = null;

    const setNavigationOpen = open => {
      const isOpen = mobileNavigation.matches && open;
      navigation.toggleClass("is-open", isOpen);
      navigationScrim.toggleClass("hidden", !isOpen);
      navigationToggle.attr("aria-expanded", String(isOpen));
      if (mobileNavigation.matches) navigation.attr("aria-hidden", String(!isOpen)).prop("inert", !isOpen);
      else navigation.removeAttr("aria-hidden").prop("inert", false);
    };
    navigationToggle.on("click", () => setNavigationOpen(!navigation.hasClass("is-open")));
    $("#database-navigation-close, #database-navigation-scrim").on("click", () => setNavigationOpen(false));
    mobileNavigation.addEventListener("change", () => setNavigationOpen(false));
    setNavigationOpen(false);

    const problemText = xhr => {
      const body = xhr.responseJSON;
      if (body?.errors) return Object.values(body.errors).flat().join(" ");
      const position = body?.position ? ` (vị trí ${body.position})` : "";
      const state = body?.sqlState ? ` [${body.sqlState}]` : "";
      return `${body?.detail || "Yêu cầu database thất bại."}${state}${position}`;
    };
    const showLoading = target => target.html(
      '<div class="database-loading"><div><div class="database-spinner"></div><p>Đang truy vấn database…</p></div></div>');
    const showError = xhr => feedback.removeClass("hidden").text(problemText(xhr)).trigger("focus");
    const requestData = extra => ({
      __RequestVerificationToken: token,
      Schema: selectedSchema,
      Table: selectedTable,
      NodeId: nodeId,
      ...extra
    });

    const loadBrowse = page => {
      if (!selectedTable) return;
      feedback.addClass("hidden").empty();
      showLoading(result);
      $.ajax({
        url: databaseExplorer.data("browse-url"), method: "POST",
        data: requestData({ Page: page || 1, PageSize: Number($("#database-page-size").val()) || 50 })
      }).done(html => result.html(html)).fail(xhr => { result.empty(); showError(xhr); });
    };
    const loadStructure = () => {
      if (!selectedTable) return;
      feedback.addClass("hidden").empty();
      showLoading(result);
      $.ajax({
        url: databaseExplorer.data("structure-url"), method: "POST", data: requestData({})
      }).done(html => result.html(html)).fail(xhr => { result.empty(); showError(xhr); });
    };
    const activateTab = tab => {
      activeTab = tab;
      $("[data-database-tab]").removeClass("is-active").attr("aria-selected", "false")
        .filter(`[data-database-tab='${tab}']`).addClass("is-active").attr("aria-selected", "true");
      if (tab === "sql") {
        result.addClass("hidden");
        sqlConsole.removeClass("hidden");
        $("#sql-editor").trigger("focus");
      } else {
        sqlConsole.addClass("hidden");
        result.removeClass("hidden");
        if (!selectedTable) {
          result.html('<div class="empty-state"><h3>Chưa chọn object</h3><p>Chọn table, view hoặc sequence ở sidebar.</p></div>');
        } else if (tab === "structure") loadStructure();
        else loadBrowse(1);
      }
    };

    $("[data-database-object]").on("click", function () {
      const button = $(this);
      selectedSchema = button.data("schema");
      selectedTable = button.data("table");
      $("[data-database-object]").removeClass("is-active");
      button.addClass("is-active");
      $("#selected-database-object").text(`${selectedSchema}.${selectedTable}`);
      setNavigationOpen(false);
      if (activeTab === "sql") activateTab("data");
      else activateTab(activeTab);
    });
    $("[data-database-tab]").on("click", function () { activateTab($(this).data("database-tab")); });
    result.on("click", "[data-database-page]", function () {
      if (!this.disabled) loadBrowse(Number($(this).data("database-page")));
    });
    $("#database-page-size").on("change", () => { if (selectedTable && activeTab === "data") loadBrowse(1); });
    $(".database-schema-toggle").on("click", function () {
      const expanded = $(this).attr("aria-expanded") === "true";
      $(this).attr("aria-expanded", String(!expanded)).next().toggleClass("hidden", expanded);
    });
    let searchTimer = null;
    $("#database-object-search").on("input", function () {
      const input = this;
      clearTimeout(searchTimer);
      searchTimer = setTimeout(() => {
        const query = String($(input).val()).trim().toLocaleLowerCase();
        $("[data-database-object]").each(function () {
          $(this).toggleClass("hidden", !String($(this).data("search-text")).toLocaleLowerCase().includes(query));
        });
        $("[data-object-kind]").each(function () {
          $(this).toggleClass("hidden", $(this).find("[data-database-object]:not(.hidden)").length === 0);
        });
        $("[data-schema-group]").each(function () {
          $(this).toggleClass("hidden", $(this).find("[data-database-object]:not(.hidden)").length === 0);
        });
      }, 120);
    });

    const modal = $("#sql-confirm-modal");
    const closeModal = () => { modal.addClass("hidden"); $("#prepare-sql-button").trigger("focus"); };
    $("#prepare-sql-button").on("click", () => {
      if (!String($("#sql-editor").val()).trim()) {
        feedback.removeClass("hidden").text("Nhập SQL trước khi thực thi.").trigger("focus");
        return;
      }
      feedback.addClass("hidden");
      modal.removeClass("hidden");
      $("#confirm-sql-button").trigger("focus");
    });
    $("#close-sql-modal").on("click", closeModal);
    modal.on("click", event => { if (event.target === modal[0]) closeModal(); });
    $(document).on("keydown", event => {
      if (event.key !== "Escape") return;
      if (!modal.hasClass("hidden")) closeModal();
      else if (navigation.hasClass("is-open")) {
        setNavigationOpen(false);
        navigationToggle.trigger("focus");
      }
    });
    $("#confirm-sql-button").on("click", () => {
      modal.addClass("hidden");
      const sqlResult = $("#sql-result");
      const runButton = $("#prepare-sql-button");
      const cancelButton = $("#cancel-sql-button");
      runButton.prop("disabled", true);
      cancelButton.removeClass("hidden");
      showLoading(sqlResult);
      activeSqlRequest = $.ajax({
        url: databaseExplorer.data("sql-url"), method: "POST",
        data: { __RequestVerificationToken: token, Sql: $("#sql-editor").val(), Confirmed: true }
      }).done(html => sqlResult.html(html)).fail(xhr => {
        if (xhr.statusText !== "abort") sqlResult.html($("<div>").addClass("connection-result error").text(problemText(xhr)));
      }).always(() => {
        activeSqlRequest = null;
        runButton.prop("disabled", false);
        cancelButton.addClass("hidden");
      });
    });
    $("#cancel-sql-button").on("click", () => activeSqlRequest?.abort());
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
