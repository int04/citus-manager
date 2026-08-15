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
  document.body.addEventListener("htmx:beforeRequest", event => {
    const button = event.detail.elt.querySelector?.("button[type=submit]");
    if (button) { button.disabled = true; button.setAttribute("aria-busy", "true"); }
  });
  document.querySelectorAll("form").forEach(form => form.addEventListener("submit", () => {
    form.setAttribute("aria-busy", "true");
    requestAnimationFrame(() => form.querySelectorAll("button[type=submit]").forEach(button => {
      button.disabled = true;
      button.setAttribute("aria-busy", "true");
    }));
  }));
})();
