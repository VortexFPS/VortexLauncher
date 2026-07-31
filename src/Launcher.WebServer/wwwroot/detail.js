// One instance: live status, the actions, and the orchestrated banner when it applies.

import { errorText, get, instancePath } from "./client.js";
import {
  applyGate, bannerMarkup, bindExits, detailFor, ensureDetail, isOrchestrated, mutateDelete,
  mutatePost, note,
} from "./orchestrated.js";
import {
  arm, dash, fmtBytes, fmtDateTime, fmtPercent, fmtPlayers, fmtUptime, html, raw,
} from "./util.js";

const POLL_MS = 5000;

export async function detailView(root, name) {
  let disposed = false;
  let spec = null;
  let status = null;
  let loadError = null;
  let message = null;
  let auditText = null;
  let openSections = { drain: false, audit: false };

  async function loadSpec() {
    const result = await get(instancePath(name));
    if (result.ok) {
      spec = result.body;
      note(spec);
    } else if (!spec) {
      loadError = result;
    }
  }

  // The per-instance status probes the game server, which is also the liveness check: a process that
  // is alive but silent reports players as null rather than as zero.
  async function loadStatus() {
    const result = await get(`${instancePath(name)}/status`);
    if (result.ok) {
      status = result.body;
      note(status);
    } else if (!status) {
      loadError = result;
    }
  }

  async function refresh() {
    await Promise.all([loadSpec(), loadStatus()]);
    if (!disposed) render();
  }

  function statsMarkup() {
    const uptime = status?.state === "running" ? fmtUptime(status?.started_at) : "—";
    return html`
      <div class="stat"><dt>State</dt>
        <dd><span class="chip ${status?.state}">${dash(status?.state)}</span></dd></div>
      <div class="stat"><dt>Map</dt><dd>${dash(status?.map)}</dd></div>
      <div class="stat"><dt>Players</dt><dd>${fmtPlayers(status)}</dd></div>
      <div class="stat"><dt>Bots</dt><dd>${status?.bots ?? "—"}</dd></div>
      <div class="stat"><dt>Uptime</dt><dd>${uptime}</dd></div>
      <div class="stat"><dt>CPU</dt><dd>${fmtPercent(status?.cpu_percent)}</dd></div>
      <div class="stat"><dt>Memory</dt><dd>${fmtBytes(status?.memory_bytes)}</dd></div>
      <div class="stat"><dt>Restarts</dt><dd>${status?.restart_count ?? 0}</dd></div>`;
  }

  function specMarkup() {
    if (!spec) return "";
    const schedule = spec.restart_at
      ? `${spec.restart_at} local time${spec.restart_only_when_empty ? ", only when empty" : ""}`
      : "none";
    const control = spec.control_mode === "orchestrated"
      ? `orchestrated by ${spec.controller_url ?? "an orchestrator"}`
        + ` since ${fmtDateTime(spec.controlled_since)}`
      : "local";

    return html`
      <h2>Configuration</h2>
      <dl class="pairs">
        <div><dt>Port</dt><dd>${spec.port}</dd></div>
        <div><dt>Gametype</dt><dd>${dash(spec.gametype)}</dd></div>
        <div><dt>Max players</dt><dd>${dash(spec.max_players)}</dd></div>
        <div><dt>Hostname</dt><dd>${dash(spec.hostname)}</dd></div>
        <div><dt>Build</dt><dd>${spec.build_id ?? "store default, pinned at start"}</dd></div>
        <div><dt>Restart policy</dt><dd>${dash(spec.restart_policy)}</dd></div>
        <div><dt>Scheduled restart</dt><dd>${schedule}</dd></div>
        <div><dt>Content packages</dt><dd>${spec.content_set?.length ?? 0}</dd></div>
        <div><dt>Process</dt><dd>${status?.pid ? `pid ${status.pid}` : "not running"}</dd></div>
        <div><dt>Last exit</dt><dd>${dash(status?.last_exit_reason)}</dd></div>
        <div><dt>Control</dt><dd>${control}</dd></div>
      </dl>`;
  }

  function messageMarkup() {
    if (!message) return "";
    return html`<div class="notice ${message.bad ? "bad" : ""}">${message.text}</div>`;
  }

  function setMessage(text, bad) {
    message = text ? { text, bad } : null;
    const slot = root.querySelector("#msg");
    if (slot) slot.innerHTML = messageMarkup();
  }

  function render() {
    const tab = (path, label, here) =>
      html`<a class="${here ? "here" : ""}"
             href="#/i/${encodeURIComponent(name)}${path}">${label}</a>`;

    root.innerHTML = html`
      <p class="crumbs"><a href="#/">All instances</a></p>
      <h1>${name}</h1>
      <div class="tabs">
        ${raw(tab("", "Status", true))}
        ${raw(tab("/edit", "Edit"))}
        ${raw(tab("/config", "server.cfg"))}
        ${raw(tab("/console", "Console"))}
      </div>

      ${isOrchestrated(name) ? raw(bannerMarkup(name, detailFor(name))) : ""}
      ${loadError && !status ? raw(html`<div class="notice bad">${errorText(loadError)}</div>`) : ""}
      <div id="msg">${raw(messageMarkup())}</div>

      <dl class="stats" id="stats">${raw(statsMarkup())}</dl>

      <div class="actions">
        <button data-act="start" data-mutating>Start</button>
        <button data-act="restart" data-mutating>Restart</button>
        <button data-act="stop" class="danger">Stop</button>
        <button data-act="delete" class="danger" data-mutating>Delete</button>
      </div>
      <div data-gate-note></div>
      <p class="muted small">Stop stays available in either control mode: it is your hardware.
         Delete needs the instance stopped first and removes its data directory.</p>

      <details class="drain" ${openSections.drain ? raw("open") : ""}>
        <summary>Drain: warn players, wait for the server to empty, then stop</summary>
        <div class="field">
          <label><span>Broadcast message</span>
            <input type="text" data-drain-message value="Server restarting shortly" data-mutating>
          </label>
        </div>
        <div class="field">
          <label><span>Give up waiting after (seconds)</span>
            <input type="number" data-drain-timeout value="300" min="1" data-mutating>
          </label>
        </div>
        <div class="actions"><button data-act="drain" data-mutating>Drain and stop</button></div>
      </details>

      ${raw(specMarkup())}

      <details class="audit" ${openSections.audit ? raw("open") : ""}>
        <summary>Audit trail: what was done to this instance, and by whom</summary>
        <pre data-audit>open to load</pre>
      </details>`;

    bindExits(root, refresh);
    bindActions();
    bindDisclosures();
    applyGate(root, name);
  }

  function paint() {
    const stats = root.querySelector("#stats");
    if (stats) stats.innerHTML = statsMarkup();
  }

  async function act(label, run) {
    setMessage(`${label}…`, false);
    const result = await run();
    if (disposed) return result;

    // A refused call carries the banner in its own body, so re-rendering is all it takes: the banner
    // appears and the mutating controls go quiet with the reason next to them.
    if (result.gated)
      setMessage(`${label} refused: this instance is under orchestrator control.`, true);
    else
      setMessage(result.ok ? `${label} accepted.` : errorText(result), !result.ok);

    // render() reads the message back out of state, so the outcome survives the re-render.
    await refresh();
    return result;
  }

  function bindActions() {
    const verb = (action) => `${instancePath(name)}/${action}`;

    root.querySelector('[data-act="start"]').addEventListener("click", () =>
      act("start", () => mutatePost(name, verb("start"), {})));

    root.querySelector('[data-act="restart"]').addEventListener("click", () =>
      act("restart", () => mutatePost(name, verb("restart"), {})));

    root.querySelector('[data-act="drain"]').addEventListener("click", () => act("drain", () =>
      mutatePost(name, verb("drain"), {
        message: root.querySelector("[data-drain-message]").value,
        timeout_seconds: Number(root.querySelector("[data-drain-timeout]").value) || 300,
      })));

    // Stop is an exit rather than a mutation: available in either control mode, so it is never gated.
    arm(root.querySelector('[data-act="stop"]'), "Confirm stop", () =>
      act("stop", () => mutatePost(name, verb("stop"), {})));

    arm(root.querySelector('[data-act="delete"]'), "Confirm delete", async () => {
      const result = await act("delete", () => mutateDelete(name, instancePath(name)));
      if (result?.ok && !disposed) location.hash = "#/";
    });
  }

  function bindDisclosures() {
    const drain = root.querySelector("details.drain");
    drain.addEventListener("toggle", () => { openSections.drain = drain.open; });

    const audit = root.querySelector("details.audit");
    const target = audit.querySelector("[data-audit]");
    if (auditText !== null) target.textContent = auditText;

    audit.addEventListener("toggle", async () => {
      openSections.audit = audit.open;
      if (!audit.open || auditText !== null) return;

      target.textContent = "loading…";
      const result = await get(`${instancePath(name)}/audit`);
      auditText = result.ok
        ? (result.body ?? []).join("\n") || "nothing recorded yet"
        : errorText(result);
      target.textContent = auditText;
    });
  }

  await refresh();
  const timer = setInterval(async () => {
    const wasOrchestrated = isOrchestrated(name);
    await loadStatus();
    if (disposed) return;

    // A mode flip changes which controls are live, so that one needs the whole screen back.
    if (isOrchestrated(name) !== wasOrchestrated) {
      // The status carries the mode but not who holds it. An instance adopted while this screen is
      // open is exactly when the owner wants the controlling plane named, so read the spec once
      // before drawing the banner rather than showing it as "an orchestrator".
      if (isOrchestrated(name)) await ensureDetail(name);
      if (disposed) return;
      render();
    } else {
      paint();
    }
  }, POLL_MS);

  return () => {
    disposed = true;
    clearInterval(timer);
  };
}
