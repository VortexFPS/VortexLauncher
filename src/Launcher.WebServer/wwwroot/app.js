// Shell: the hash router and the header. Every screen lives in its own module.
//
// Hash routes rather than paths, for one reason: the bearer token rides in the query string, and a
// hash link carries it along untouched without the panel having to rewrite URLs.

import { errorText, runnerStatus, token } from "./client.js";
import { configView } from "./config.js";
import { consoleView } from "./console.js";
import { detailView } from "./detail.js";
import { formView } from "./form.js";
import { listView } from "./list.js";
import { html } from "./util.js";

const view = document.getElementById("view");
const summary = document.getElementById("runner-summary");

let cleanup = null;
let generation = 0;

/** Screens return an optional teardown, which is how the console socket and the pollers stop. */
function dispatch(parts) {
  if (parts.length === 0) return listView(view);
  if (parts.length === 1 && parts[0] === "new") return formView(view, null);

  if (parts[0] === "i" && parts[1]) {
    const name = parts[1];
    switch (parts[2]) {
      case undefined: return detailView(view, name);
      case "edit": return formView(view, name);
      case "config": return configView(view, name);
      case "console": return consoleView(view, name);
    }
  }

  view.innerHTML = html`<div class="notice bad">No such screen.
    <a href="#/">Back to the instance list</a>.</div>`;
  return Promise.resolve(null);
}

async function route() {
  const mine = ++generation;

  if (cleanup) {
    try { cleanup(); } catch { /* a screen that fails to tear down must not block the next one */ }
    cleanup = null;
  }

  view.className = "";
  view.innerHTML = html`<p class="muted">Loading…</p>`;

  const parts = location.hash
    .replace(/^#\/?/, "")
    .split("/")
    .filter(Boolean)
    .map(decodeURIComponent);

  let teardown = null;
  try {
    teardown = await dispatch(parts);
  } catch (err) {
    view.innerHTML = html`<div class="notice bad">This screen failed to load: ${err.message}</div>`;
  }

  // Navigated away while the screen was still loading: tear the late arrival down instead of
  // leaving its pollers running behind the screen the operator is actually looking at.
  if (mine !== generation) {
    teardown?.();
    return;
  }
  cleanup = teardown ?? null;
}

async function paintHeader() {
  const result = await runnerStatus();

  if (!result.ok) {
    summary.textContent = errorText(result);
    return;
  }

  const runner = result.body ?? {};
  const parts = [
    runner.hostname,
    `runner ${runner.runner_id ?? "?"}`,
    runner.version ? `v${runner.version}` : null,
    `${runner.instances?.length ?? 0} instances`,
    runner.conductor_url ? `linked to ${runner.conductor_url}` : null,
  ].filter(Boolean);

  summary.textContent = parts.join(" · ");
}

if (!token) {
  summary.textContent = "no ?token= in this URL, so every API call will be refused";
}

window.addEventListener("hashchange", route);
route();
paintHeader();
setInterval(paintHeader, 10000);
