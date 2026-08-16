(() => {
  const source = document.getElementById("citus-i18n-data");
  let data = { locale: "en-US", messages: {} };
  try { if (source?.textContent) data = JSON.parse(source.textContent); } catch { /* English key fallback remains available. */ }
  const format = (value, args) => String(value).replace(/\{(\d+)\}/g, (match, index) =>
    Number(index) < args.length ? String(args[Number(index)] ?? "") : match);
  window.CitusI18n = Object.freeze({
    locale: data.locale || "en-US",
    messages: data.messages || {},
    t(key, ...args) { return format(this.messages[key] ?? key, args); },
    number(value, options) { return new Intl.NumberFormat(this.locale, options).format(value); },
    date(value, options) { return new Intl.DateTimeFormat(this.locale, options).format(new Date(value)); }
  });
})();
