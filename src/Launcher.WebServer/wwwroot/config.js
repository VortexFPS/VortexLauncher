// server.cfg: a plain textarea with an explicit save.
//
// No autosave on purpose. This file is executed by the game at boot, an operator edits it in passing,
// and a half-typed cvar written on a keystroke timer is a server that comes up wrong at 3am.

import { errorText, get, instancePath } from "./client.js";
import {
  applyGate, bannerMarkup, bindExits, detailFor, isOrchestrated, mutatePatch, note,
} from "./orchestrated.js";
import { arm, html, raw } from "./util.js";

const configPath = (name) => `${instancePath(name)}/config`;

export async function configView(root, name) {
  let loaded = "";
  let draft = null;
  let message = null;
  let missingRoute = false;

  // The banner needs the controlling plane, and a read is allowed in either mode.
  const specResult = await get(instancePath(name));
  if (specResult.ok) note(specResult.body);

  async function load() {
    const result = await get(configPath(name));

    // The runner answers an unknown path with 404 invalid_request, which is a different thing from
    // "no such instance". Say which one it is instead of showing an empty editor.
    if (result.status === 404 && result.body?.code === "invalid_request") {
      missingRoute = true;
      return;
    }
    if (!result.ok) {
      message = { text: errorText(result), bad: true };
      return;
    }

    missingRoute = false;
    loaded = readText(result.body);
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
    const dirty = () => root.querySelector("[data-config]")?.value !== loaded;

    // Whatever the operator has typed survives every re-render, including the one that follows a
    // refused save. Losing a config someone just wrote because a banner needed drawing is not a
    // trade worth making; only Reload throws the draft away, and it asks first.
    const current = root.querySelector("[data-config]");
    if (current && current.value !== loaded) draft = current.value;

    root.innerHTML = html`
      <p class="crumbs"><a href="#/">All instances</a> /
        <a href="#/i/${encodeURIComponent(name)}">${name}</a></p>
      <h1>server.cfg</h1>
      <div class="tabs">
        <a href="#/i/${encodeURIComponent(name)}">Status</a>
        <a href="#/i/${encodeURIComponent(name)}/edit">Edit</a>
        <a class="here" href="#/i/${encodeURIComponent(name)}/config">server.cfg</a>
        <a href="#/i/${encodeURIComponent(name)}/console">Console</a>
      </div>

      ${isOrchestrated(name) ? raw(bannerMarkup(name, detailFor(name))) : ""}

      ${missingRoute ? raw(html`
        <div class="notice bad">
          <p>This runner does not serve server.cfg over the API. The editor needs
             <code>GET</code> and <code>PATCH /api/v1/instances/${name}/config</code>, and the runner
             answered 404 for the path.</p>
          <p class="muted small">The file is on this box at
             <code>&lt;instances dir&gt;/${name}/VortexData/server.cfg</code> and the game executes it
             at boot.</p>
        </div>`) : ""}

      <div id="msg">${raw(messageMarkup())}</div>

      <div class="field">
        <label><span>Executed by the game at startup. Saved only when you press Save.</span>
          <textarea data-config rows="22" spellcheck="false" data-mutating
                    ${missingRoute ? raw("disabled data-keep-disabled") : ""}>${draft ?? loaded}</textarea>
        </label>
      </div>

      <div class="actions">
        <button data-act="save" class="primary" data-mutating
                ${missingRoute ? raw("disabled data-keep-disabled") : ""}>Save</button>
        <button data-act="reload" ${missingRoute ? raw("disabled") : ""}>Reload from disk</button>
        <span class="muted small" data-dirty></span>
      </div>
      <div data-gate-note></div>`;

    const editor = root.querySelector("[data-config]");
    const dirtyLabel = root.querySelector("[data-dirty]");
    const markDirty = () => {
      dirtyLabel.textContent = dirty() ? "unsaved changes" : "";
    };
    editor.addEventListener("input", markDirty);

    const save = root.querySelector('[data-act="save"]');
    save.addEventListener("click", async () => {
      const pending = editor.value;
      save.disabled = true;
      setMessage("saving…", false);

      const result = await mutatePatch(name, configPath(name), { text: pending });
      save.disabled = false;

      if (result.gated) {
        setMessage(
          "Refused: this instance is under orchestrator control, so server.cfg is read-only here.",
          true);
        // The 409 carried the banner, so this is the one outcome that needs the screen rebuilt.
        render();
        return;
      }

      if (result.ok) {
        loaded = pending;
        draft = null;
        setMessage("Saved. It takes effect the next time the server starts.", false);
      } else {
        setMessage(errorText(result), true);
      }
      // Deliberately no re-render: it would overwrite anything typed while the save was in flight.
      markDirty();
    });

    // Reloading throws away whatever is in the box, so it asks twice when there is something to lose.
    arm(root.querySelector('[data-act="reload"]'), "Discard edits and reload", async () => {
      draft = null;
      message = null;
      await load();
      render();
    }, dirty);

    bindExits(root, () => render());
    applyGate(root, name);
    markDirty();
  }

  await load();
  render();
}

/** Accept the shapes a config route could reasonably answer with, rather than guessing exactly one. */
function readText(body) {
  if (typeof body === "string") return body;
  if (typeof body?.text === "string") return body.text;
  if (typeof body?.content === "string") return body.content;
  return "";
}
