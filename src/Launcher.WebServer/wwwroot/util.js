// Escaping and formatting. Nothing here talks to the network.

const RAW = Symbol("raw");

function esc(value) {
  return String(value)
    .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;").replaceAll("'", "&#39;");
}

/** Mark a string as already-safe markup, for the few places that compose fragments. */
export const raw = (markup) => ({ [RAW]: String(markup) });

/**
 * Tagged template that escapes every interpolation unless it is wrapped in raw().
 *
 * Escaping by default rather than at each call site: most of what this panel renders (instance names,
 * error messages, a controller URL out of a 409 body, map names a game server reported) arrives over
 * the network, and "remember to escape here" is a rule that holds until the day somebody adds a field.
 */
export function html(parts, ...values) {
  let out = parts[0];
  for (let i = 0; i < values.length; i++) out += render(values[i]) + parts[i + 1];
  return out;
}

function render(value) {
  if (value == null || value === false) return "";
  if (Array.isArray(value)) return value.map(render).join("");
  if (typeof value === "object" && RAW in value) return value[RAW];
  return esc(String(value));
}

export const dash = (value) => (value == null || value === "" ? "—" : value);

export function fmtUptime(startedAt) {
  if (!startedAt) return "—";
  const seconds = Math.floor((Date.now() - new Date(startedAt).getTime()) / 1000);
  if (!Number.isFinite(seconds) || seconds < 0) return "—";
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  if (d) return `${d}d ${h}h`;
  if (h) return `${h}h ${m}m`;
  if (m) return `${m}m ${seconds % 60}s`;
  return `${seconds}s`;
}

export function fmtBytes(bytes) {
  if (bytes == null) return "—";
  const units = ["B", "KiB", "MiB", "GiB", "TiB"];
  let value = Number(bytes);
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value < 10 && unit > 0 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

export function fmtPercent(value) {
  return value == null ? "—" : `${Number(value).toFixed(1)}%`;
}

export function fmtDateTime(iso) {
  if (!iso) return "an unknown date";
  const at = new Date(iso);
  return Number.isNaN(at.getTime()) ? "an unknown date" : at.toLocaleString();
}

export function fmtClock(iso) {
  const at = iso ? new Date(iso) : new Date();
  return Number.isNaN(at.getTime()) ? "--:--:--" : at.toLocaleTimeString();
}

/** Players as the API means it: humans, bots and the cap, with null distinct from zero. */
export function fmtPlayers(status) {
  if (!status || status.players == null) return "no probe answer";
  return `${status.players} + ${status.bots ?? 0} bots / ${status.max_players ?? "?"}`;
}

/**
 * Two-click confirmation for the actions that drop players or delete data.
 *
 * A dead-simple guard rather than a modal: the second click has to be deliberate, and the button says
 * what it is about to do while it is armed.
 */
export function arm(button, confirmLabel, run, needsConfirm = () => true) {
  const original = button.textContent;
  let armed = false;
  let timer = null;

  const disarm = () => {
    armed = false;
    button.textContent = original;
    button.classList.remove("armed");
    clearTimeout(timer);
  };

  button.addEventListener("click", () => {
    if (!needsConfirm()) return run();

    if (!armed) {
      armed = true;
      button.textContent = confirmLabel;
      button.classList.add("armed");
      timer = setTimeout(disarm, 5000);
      return;
    }
    disarm();
    run();
  });
}
