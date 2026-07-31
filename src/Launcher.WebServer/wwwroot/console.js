// Live console: log frames down the socket, command lines up it.
//
// Reading stays available in either control mode. The logs are files on the owner's own disk, so
// there is nothing to engineer around, and the banner says in plain words that this includes chat.

import { consoleUrl, errorText, get, instancePath } from "./client.js";
import { bannerMarkup, bindExits, detailFor, isOrchestrated, note } from "./orchestrated.js";
import { fmtClock, html } from "./util.js";

const MAX_LINES = 2000;
const BACKOFF_SECONDS = [1, 2, 4, 8, 15, 30];
const GATE_POLL_MS = 5000;

export async function consoleView(root, name) {
  let disposed = false;
  let socket = null;
  let attempt = 0;
  let reconnectTimer = null;
  let countdownTimer = null;

  const specResult = await get(instancePath(name));
  if (specResult.ok) note(specResult.body);
  if (disposed) return;

  root.innerHTML = html`
    <p class="crumbs"><a href="#/">All instances</a> /
      <a href="#/i/${encodeURIComponent(name)}">${name}</a></p>
    <h1>Console</h1>
    <div class="tabs">
      <a href="#/i/${encodeURIComponent(name)}">Status</a>
      <a href="#/i/${encodeURIComponent(name)}/edit">Edit</a>
      <a href="#/i/${encodeURIComponent(name)}/config">server.cfg</a>
      <a class="here" href="#/i/${encodeURIComponent(name)}/console">Console</a>
    </div>

    <div id="banner-slot"></div>

    <div class="actions">
      <span class="link-status down" data-link>connecting…</span>
      <button data-act="reconnect">Reconnect now</button>
    </div>

    <div class="console-log" data-log></div>

    <form class="console-form">
      <input type="text" data-command placeholder="say hello" autocomplete="off" data-mutating>
      <button type="submit" class="primary" data-mutating>Send</button>
    </form>
    <div data-gate-note></div>
    <p class="muted small">Commands go to the server the same way the runner's own console does.
       A command containing a semicolon or a control character is refused by the runner.</p>`;

  const logPane = root.querySelector("[data-log]");
  const linkLabel = root.querySelector("[data-link]");
  const commandInput = root.querySelector("[data-command]");
  const sendButton = root.querySelector('button[type="submit"]');

  function setLink(text, up) {
    linkLabel.textContent = text;
    linkLabel.classList.toggle("up", up);
    linkLabel.classList.toggle("down", !up);
  }

  /**
   * Lines are written as text, never as markup.
   *
   * Log lines carry player chat from the public internet. innerHTML here would be a scripting hole
   * with a chat message as the payload, and no amount of care elsewhere would close it.
   */
  function append(line, extraClass) {
    const pinned = logPane.scrollHeight - logPane.scrollTop - logPane.clientHeight < 40;

    const row = document.createElement("div");
    row.className = `log-line stream-${line.stream ?? "stdout"}`
      + (line.is_chat ? " chat" : "") + (extraClass ? ` ${extraClass}` : "");

    const time = document.createElement("span");
    time.className = "log-time";
    time.textContent = fmtClock(line.timestamp);

    const text = document.createElement("span");
    text.className = "log-text";
    text.textContent = line.text ?? "";

    row.append(time, text);
    logPane.append(row);

    while (logPane.childElementCount > MAX_LINES) logPane.firstElementChild.remove();
    if (pinned) logPane.scrollTop = logPane.scrollHeight;
  }

  // The socket only carries what happens from now on, so the recent tail comes from the log route.
  async function prime() {
    const result = await get(`${instancePath(name)}/logs`);
    if (disposed) return;

    if (!result.ok) {
      append({ stream: "runner", text: `could not load recent output: ${errorText(result)}` });
      return;
    }
    (result.body ?? []).forEach((line) => append(line));
  }

  function connect() {
    if (disposed) return;
    clearTimers();
    setLink("connecting…", false);

    let opened;
    try {
      opened = new WebSocket(consoleUrl(name));
    } catch {
      scheduleReconnect();
      return;
    }
    socket = opened;

    opened.addEventListener("open", () => {
      if (disposed) return;
      attempt = 0;
      setLink("connected", true);
      updateSendState();
    });

    opened.addEventListener("message", (event) => {
      try {
        append(JSON.parse(event.data));
      } catch {
        append({ stream: "runner", text: String(event.data) });
      }
    });

    opened.addEventListener("close", () => {
      // A socket that has already been superseded (Reconnect now, or teardown) must not schedule
      // anything: otherwise the manual button leaves a stray timer that replaces the live socket.
      if (socket !== opened) return;
      socket = null;
      updateSendState();
      scheduleReconnect();
    });

    // A failed upgrade fires error and then close, so the reconnect is scheduled in one place.
    opened.addEventListener("error", () => setLink("connection failed", false));
  }

  /** Reconnect for as long as the screen is open: the plane restarting, or the runner reconnecting
   *  behind it, is a normal condition rather than a reason to make the operator reload the page. */
  function scheduleReconnect() {
    if (disposed || reconnectTimer) return;

    const seconds = BACKOFF_SECONDS[Math.min(attempt, BACKOFF_SECONDS.length - 1)];
    attempt++;
    let left = seconds;

    const tick = () => setLink(`disconnected, retrying in ${left}s (attempt ${attempt})`, false);
    tick();

    countdownTimer = setInterval(() => {
      left--;
      if (left > 0) tick();
    }, 1000);

    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      connect();
    }, seconds * 1000);
  }

  function clearTimers() {
    clearTimeout(reconnectTimer);
    clearInterval(countdownTimer);
    reconnectTimer = null;
    countdownTimer = null;
  }

  /**
   * Sending is a mutating call, so it is refused while the instance is orchestrated.
   *
   * The refusal does come back down this socket and prints, so nothing typed here vanishes. Disabling
   * is still the better answer: a control that only says no after you have used it teaches the rule by
   * refusing people, and the reason sits next to the box before they type a word.
   */
  function updateSendState() {
    if (disposed) return;
    const gated = isOrchestrated(name);
    const live = socket?.readyState === WebSocket.OPEN;

    commandInput.disabled = gated || !live;
    sendButton.disabled = gated || !live;
    commandInput.placeholder = gated
      ? "read-only while orchestrated"
      : live ? "say hello" : "waiting for the console socket";

    root.querySelector("[data-gate-note]").innerHTML = gated
      ? html`<p class="gate-reason">Commands are disabled: ${name} is controlled by
             <code>${detailFor(name)?.controller_url ?? "an orchestrator"}</code>. The log above keeps
             streaming, because the logs are yours.</p>`
      : "";
  }

  function renderBanner() {
    if (disposed) return;
    const slot = root.querySelector("#banner-slot");
    slot.innerHTML = isOrchestrated(name) ? bannerMarkup(name, detailFor(name)) : "";
    bindExits(root, async () => {
      await refreshMode();
    });
  }

  // The spec rather than the runner snapshot: it costs one cheap round trip, it is current rather
  // than up to a heartbeat old, and it carries the controller and the grant the banner needs.
  async function refreshMode() {
    const before = isOrchestrated(name);
    const result = await get(instancePath(name));
    if (!result.ok || disposed) return;

    note(result.body);
    if (isOrchestrated(name) !== before) renderBanner();
    updateSendState();
  }

  root.querySelector(".console-form").addEventListener("submit", (event) => {
    event.preventDefault();
    const command = commandInput.value.trim();
    // Belt and braces on top of the disabled input. The runner's refusal would print rather than
    // vanish, but a command the panel already knows will be refused is not worth the round trip.
    if (!command || isOrchestrated(name) || socket?.readyState !== WebSocket.OPEN) return;

    socket.send(command);
    // Echo locally: the socket carries the game's output back, not an acknowledgement of the line.
    append({ stream: "runner", text: `> ${command}`, timestamp: new Date().toISOString() }, "sent");
    commandInput.value = "";
  });

  root.querySelector('[data-act="reconnect"]').addEventListener("click", () => {
    attempt = 0;
    if (socket) socket.close();
    clearTimers();
    connect();
  });

  renderBanner();
  updateSendState();
  await prime();
  connect();
  const gateTimer = setInterval(refreshMode, GATE_POLL_MS);

  return () => {
    disposed = true;
    clearInterval(gateTimer);
    clearTimers();
    if (socket) {
      const closing = socket;
      socket = null;
      closing.close();
    }
  };
}
