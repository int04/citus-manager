const STORAGE_KEY = "citus-manager-database-time:v1";
const canonicalZone = value => value === "Asia/Saigon" ? "Asia/Ho_Chi_Minh" : value;
const browserZone = canonicalZone(Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC");
let preference = readPreference();

function readPreference() {
  try {
    const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) || "{}");
    return { mode: saved.mode === "user" ? "user" : "native", timeZone: validZone(saved.timeZone) ? canonicalZone(saved.timeZone) : browserZone };
  } catch { return { mode: "native", timeZone: browserZone }; }
}

function validZone(value) {
  if (!value) return false;
  try { new Intl.DateTimeFormat("en", { timeZone: value }).format(); return true; } catch { return false; }
}

function zones() {
  const values = typeof Intl.supportedValuesOf === "function" ? Intl.supportedValuesOf("timeZone").map(canonicalZone) : ["UTC", "Asia/Ho_Chi_Minh", "Asia/Bangkok", "Asia/Singapore", "Asia/Tokyo", "Europe/London", "Europe/Paris", "America/New_York", "America/Los_Angeles"];
  return [...new Set([browserZone, "Asia/Ho_Chi_Minh", ...values])];
}

function offsetLabel(timeZone, date = new Date()) {
  try {
    const value = new Intl.DateTimeFormat("en", { timeZone, timeZoneName: "longOffset" }).formatToParts(date).find(part => part.type === "timeZoneName")?.value || "GMT";
    return value.replace(/GMT([+-])0?(\d{1,2})(?::00)?$/, "GMT$1$2").replace("GMT+0", "GMT");
  } catch { return "GMT"; }
}

export function temporalDisplay(value, dataType) {
  if (preference.mode !== "user" || value == null || !/^(timestamp with time zone|timestamptz)$/i.test(String(dataType).trim())) return value ?? "";
  const date = parseInstant(value);
  if (Number.isNaN(date.valueOf())) return value;
  try {
    const parts = new Intl.DateTimeFormat(window.CitusI18n?.locale || navigator.language, { timeZone: preference.timeZone, year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", fractionalSecondDigits: 3, hourCycle: "h23", timeZoneName: "shortOffset" }).formatToParts(date);
    const take = type => parts.find(part => part.type === type)?.value || "";
    return `${take("year")}-${take("month")}-${take("day")} ${take("hour")}:${take("minute")}:${take("second")}.${take("fractionalSecond")} ${take("timeZoneName")}`.trim();
  } catch { return value; }
}

export const temporalEditorValue = temporalDisplay;

function timeZoneOffsetMinutes(timeZone, instant) {
  const label = new Intl.DateTimeFormat("en", { timeZone, timeZoneName: "longOffset" }).formatToParts(instant).find(part => part.type === "timeZoneName")?.value || "GMT";
  if (label === "GMT" || label === "UTC") return 0;
  const match = /^GMT([+-])(\d{1,2})(?::(\d{2}))?$/.exec(label);
  if (!match) throw new RangeError(`Unsupported time-zone offset: ${label}`);
  return (match[1] === "+" ? 1 : -1) * (Number(match[2]) * 60 + Number(match[3] || 0));
}

function invalidTimeError() {
  return new Error(window.CitusI18n?.t("time.invalidForZone", preference.timeZone) || `Invalid date/time for ${preference.timeZone}.`);
}

function parseInstant(value) {
  let text = String(value ?? "").trim();
  text = text.replace(/^(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2})/, "$1T$2");
  text = text.replace(/([+-]\d{2})$/, "$1:00").replace(/([+-]\d{2})(\d{2})$/, "$1:$2");
  return new Date(text);
}

export function temporalStorageValue(value, dataType) {
  if (preference.mode !== "user" || !/^(timestamp with time zone|timestamptz)$/i.test(String(dataType).trim())) return value;
  const text = String(value ?? "").trim();
  if (!text) return text;
  if (/^[+-]?infinity$/i.test(text)) return text;
  if (!/\sGMT[+-]/i.test(text) && /(?:Z|[+-]\d{2}(?::?\d{2})?)$/i.test(text)) {
    const explicit = parseInstant(text);
    if (Number.isNaN(explicit.valueOf())) throw invalidTimeError();
    return explicit.toISOString();
  }
  const match = /^(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,9}))?)?(?:\s+GMT(?:[+-]\d{1,2}(?::?\d{2})?)?)?$/i.exec(text);
  if (!match) throw invalidTimeError();
  const year = Number(match[1]), month = Number(match[2]), day = Number(match[3]), hour = Number(match[4]), minute = Number(match[5]), second = Number(match[6] || 0);
  const millisecond = Number((match[7] || "").padEnd(3, "0").slice(0, 3));
  const wallUtc = Date.UTC(year, month - 1, day, hour, minute, second, millisecond);
  const wallCheck = new Date(wallUtc);
  if (wallCheck.getUTCFullYear() !== year || wallCheck.getUTCMonth() !== month - 1 || wallCheck.getUTCDate() !== day || wallCheck.getUTCHours() !== hour || wallCheck.getUTCMinutes() !== minute || wallCheck.getUTCSeconds() !== second) throw invalidTimeError();
  let instantMs = wallUtc - timeZoneOffsetMinutes(preference.timeZone, wallCheck) * 60_000;
  instantMs = wallUtc - timeZoneOffsetMinutes(preference.timeZone, new Date(instantMs)) * 60_000;
  const instant = new Date(instantMs);
  if (Number.isNaN(instant.valueOf())) throw invalidTimeError();
  const roundTrip = temporalDisplay(instant.toISOString(), dataType);
  const expectedPrefix = `${match[1]}-${match[2]}-${match[3]} ${match[4]}:${match[5]}:${String(second).padStart(2, "0")}.${String(millisecond).padStart(3, "0")}`;
  if (!roundTrip.startsWith(expectedPrefix)) throw invalidTimeError();
  return instant.toISOString();
}

export function temporalValuesEquivalent(left, right, dataType) {
  if (!/^(timestamp with time zone|timestamptz)$/i.test(String(dataType).trim())) return left === right;
  const leftDate = parseInstant(left), rightDate = parseInstant(right);
  if (Number.isNaN(leftDate.valueOf()) || Number.isNaN(rightDate.valueOf())) return String(left) === String(right);
  return leftDate.valueOf() === rightDate.valueOf();
}

export function refreshTemporalDisplays(root = document) {
  root.querySelectorAll("[data-temporal-value][data-temporal-type]").forEach(element => { element.textContent = temporalDisplay(element.dataset.temporalValue, element.dataset.temporalType); });
}

export function initializeDatabasePreferences() {
  const modal = document.getElementById("database-preferences-modal"), openButton = document.getElementById("database-preferences-open");
  if (!modal || !openButton) return;
  const card = modal.querySelector(".database-preferences-card"), status = modal.querySelector("[data-preferences-status]");
  const timezoneField = modal.querySelector("[data-timezone-field]"), timezoneToggle = modal.querySelector("[data-timezone-toggle]"), current = modal.querySelector("[data-timezone-current]");
  const popover = modal.querySelector("[data-timezone-popover]"), search = modal.querySelector("#database-timezone-search"), options = modal.querySelector("[data-timezone-options]"), empty = modal.querySelector("[data-timezone-empty]");
  let lastFocus = null;
  const timezoneChoices = zones().map(zone => ({ zone, offset: offsetLabel(zone), search: `${zone} ${offsetLabel(zone)}`.toLocaleLowerCase() }));
  const announce = () => { status.textContent = modal.dataset.savedMessage || "Saved"; clearTimeout(announce.timer); announce.timer = setTimeout(() => status.textContent = "", 1800); };
  const save = () => { localStorage.setItem(STORAGE_KEY, JSON.stringify(preference)); refreshTemporalDisplays(); document.dispatchEvent(new CustomEvent("database:preferences-changed", { detail: preference })); announce(); };
  const sync = () => {
    modal.querySelector(`input[name=database-theme][value=${document.documentElement.dataset.theme === "light" ? "light" : "dark"}]`).checked = true;
    modal.querySelector(`input[name=database-time-mode][value=${preference.mode}]`).checked = true;
    current.textContent = `${preference.timeZone} · ${offsetLabel(preference.timeZone)}`;
    const disabled = preference.mode !== "user"; timezoneField.classList.toggle("is-disabled", disabled); timezoneToggle.disabled = disabled;
  };
  const renderOptions = query => {
    const normalized = query.trim().toLocaleLowerCase();
    const matches = timezoneChoices.filter(item => item.search.includes(normalized));
    options.innerHTML = matches.map(item => `<button type="button" role="option" data-zone="${item.zone}" aria-selected="${item.zone === preference.timeZone}" tabindex="${item.zone === preference.timeZone ? "0" : "-1"}"><b>${item.zone}</b><small>${item.offset}</small></button>`).join("");
    empty.classList.toggle("hidden", matches.length > 0);
    options.querySelectorAll("[data-zone]").forEach(button => {
      button.onclick = () => { preference.timeZone = button.dataset.zone; popover.classList.add("hidden"); timezoneToggle.setAttribute("aria-expanded", "false"); sync(); save(); timezoneToggle.focus(); };
      button.onkeydown = event => {
        if (!(["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key))) return;
        const items = [...options.querySelectorAll("[data-zone]")], currentIndex = items.indexOf(button);
        const nextIndex = event.key === "Home" ? 0 : event.key === "End" ? items.length - 1 : (currentIndex + (event.key === "ArrowDown" ? 1 : -1) + items.length) % items.length;
        items[nextIndex]?.focus(); event.preventDefault();
      };
    });
  };
  const closePopover = () => { popover.classList.add("hidden"); timezoneToggle.setAttribute("aria-expanded", "false"); };
  const close = () => { closePopover(); modal.classList.add("hidden"); document.body.classList.remove("database-modal-open"); lastFocus?.focus(); };
  const open = () => { lastFocus = document.activeElement; sync(); modal.classList.remove("hidden"); document.body.classList.add("database-modal-open"); requestAnimationFrame(() => card.focus()); };
  openButton.onclick = open; modal.querySelectorAll("[data-preferences-close]").forEach(button => button.onclick = close); modal.onclick = event => { if (event.target === modal) close(); };
  modal.querySelectorAll("input[name=database-theme]").forEach(input => input.onchange = () => { document.documentElement.dataset.theme = input.value; localStorage.setItem("citus-manager-theme", input.value); save(); });
  modal.querySelectorAll("input[name=database-time-mode]").forEach(input => input.onchange = () => { preference.mode = input.value; sync(); save(); });
  const systemObjects = modal.querySelector("[data-system-objects]");
  if (systemObjects) systemObjects.onchange = () => { systemObjects.disabled = true; status.textContent = modal.dataset.applyingMessage || "Applying…"; location.assign(systemObjects.dataset.url); };
  timezoneToggle.onclick = () => { const opening = popover.classList.contains("hidden"); popover.classList.toggle("hidden", !opening); timezoneToggle.setAttribute("aria-expanded", String(opening)); if (opening) { search.value = ""; renderOptions(""); requestAnimationFrame(() => search.focus()); } };
  search.oninput = () => renderOptions(search.value);
  search.onkeydown = event => { if (event.key === "ArrowDown") { options.querySelector("[data-zone]")?.focus(); event.preventDefault(); } };
  modal.onkeydown = event => {
    if (event.key === "Escape") { if (!popover.classList.contains("hidden")) { closePopover(); timezoneToggle.focus(); } else close(); event.preventDefault(); return; }
    if (event.key !== "Tab") return;
    const focusable = [...modal.querySelectorAll("button:not([disabled]),input:not([disabled]),select:not([disabled])")].filter(element => element.offsetParent !== null);
    if (!focusable.length) return;
    const first = focusable[0], last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) { last.focus(); event.preventDefault(); }
    else if (!event.shiftKey && document.activeElement === last) { first.focus(); event.preventDefault(); }
  };
  document.addEventListener("pointerdown", event => { if (!popover.classList.contains("hidden") && !event.target.closest(".database-timezone-combobox")) closePopover(); });
  sync(); refreshTemporalDisplays();
}
