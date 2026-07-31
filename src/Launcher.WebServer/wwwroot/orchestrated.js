// Control mode, which is the part of this panel that has to be right.
//
// An instance is local or orchestrated, never both. While orchestrated this panel is read-only on it
// and holds exactly two actions: return to local control, and shut down. Every mutating call the
// runner refuses comes back as 409 with an `orchestrated` object naming the controlling Conductor and
// both exits, so the banner is rendered out of the error body itself and no endpoint needs a special
// case here.

import { errorText, get, instancePath, post, request } from "./client.js";
import { arm, fmtDateTime, html, raw } from "./util.js";

/** instance name -> the orchestrated detail we last learned, from a spec read or from a 409. */
const known = new Map();

/**
 * instance name -> the outcome of the last exit the operator took.
 *
 * Kept outside the DOM because the screen re-renders straight after an exit. A release scheduled for
 * the end of the match changes nothing visible until the match ends, and an operator who is told
 * nothing concludes the button is broken and clicks it again.
 */
const exitNotes = new Map();

function drop(name) {
  known.delete(name);
  exitNotes.delete(name);
}

/**
 * How long a refusal outranks a plain read.
 *
 * A 409 is the runner's own answer about this exact instance, and the poll that lands a second later
 * must not be able to drop the banner: that is precisely how an operator ends up staring at a button
 * that does nothing. A read that agrees costs nothing, and anything the operator does that actually
 * succeeds clears the pin immediately, so this only holds while the two sources disagree.
 */
const REFUSAL_PIN_MS = 15000;

/** Merge rather than replace: a 409 body carries the full detail, an InstanceStatus carries only the
 *  mode, and a status refresh must not blank out the controller we already know. */
function remember(name, detail, pin = false) {
  const previous = known.get(name) ?? {};
  known.set(name, {
    controller_url: detail.controller_url ?? previous.controller_url ?? null,
    controlled_since: detail.controlled_since ?? previous.controlled_since ?? null,
    granted_scopes: detail.granted_scopes ?? previous.granted_scopes ?? null,
    release_path: detail.release_path ?? previous.release_path ?? "release",
    stop_path: detail.stop_path ?? previous.stop_path ?? "stop",
    pinned_until: pin ? Date.now() + REFUSAL_PIN_MS : (previous.pinned_until ?? 0),
  });
}

export const detailFor = (name) => known.get(name) ?? null;
export const isOrchestrated = (name) => known.has(name);
export const forget = (name) => drop(name);

/**
 * Learn from any body that carries a control mode: an InstanceSpec (which has the full detail) or an
 * InstanceStatus (which has only the mode). Returns true when the instance is orchestrated.
 *
 * `authoritative` marks a body that came back from a call the runner accepted, which is proof of the
 * mode rather than a report of it, and is the one thing that clears a pinned refusal.
 */
export function note(body, { authoritative = false } = {}) {
  if (!body?.name || !body.control_mode) return false;

  if (body.control_mode !== "orchestrated") {
    const pinnedUntil = known.get(body.name)?.pinned_until ?? 0;
    if (!authoritative && Date.now() < pinnedUntil) return true;
    drop(body.name);
    return false;
  }

  remember(body.name, {
    controller_url: body.controller_url,
    controlled_since: body.controlled_since,
    granted_scopes: body.granted_scopes,
  });
  return true;
}

/** The detail carried by a refused mutating call, or null when this was some other failure. */
export function noteError(name, result) {
  const detail = result?.status === 409 && result.body?.code === "instance_orchestrated"
    ? result.body.orchestrated
    : null;
  if (!detail) return null;

  remember(name, detail, true);
  return detailFor(name);
}

/**
 * Fill in a detail we only half know.
 *
 * The instance list reports the mode but not who holds it, so the first time an instance shows up
 * orchestrated we read its spec once, which carries the controller, the grant and the date. Later
 * polls hit the cache and cost nothing.
 */
export async function ensureDetail(name) {
  const cached = detailFor(name);
  if (cached?.controller_url) return cached;

  const result = await get(instancePath(name));
  if (result.ok) note(result.body);
  return detailFor(name);
}

/**
 * Every mutating call in the panel goes through here.
 *
 * One place records the 409, so a screen only has to ask "am I gated" and re-render. That is the
 * whole reason the error body carries the banner: no endpoint gets a special case.
 */
export async function mutate(name, path, init) {
  const result = await request(path, init);
  if (noteError(name, result)) return { ...result, gated: true };

  // A call that succeeded is proof of the mode, whatever we believed a moment ago: the runner refuses
  // the other plane outright, so there is no "it worked anyway" case. Stop is the exception that
  // proves it, since it succeeds in either mode and its body says which one.
  if (result.ok) {
    if (!note(result.body, { authoritative: true })) drop(name);
  }
  return { ...result, gated: false };
}

export const mutatePost = (name, path, body) => mutate(name, path, { method: "POST", body });
export const mutatePatch = (name, path, body) => mutate(name, path, { method: "PATCH", body });
export const mutateDelete = (name, path) => mutate(name, path, { method: "DELETE" });

/**
 * The two exits, out of the paths the error body named.
 *
 * Only a plain path segment is accepted. The body comes from the runner over a trusted link, but a
 * panel that will POST wherever a field points is one malformed value away from posting somewhere
 * else, and the contract's own default is a bare segment.
 */
function exitPath(name, segment, fallback) {
  const safe = /^[A-Za-z0-9._-]{1,64}$/.test(segment ?? "") ? segment : fallback;
  return `${instancePath(name)}/${safe}`;
}

export function bannerMarkup(name, detail) {
  const controller = detail?.controller_url;
  const since = fmtDateTime(detail?.controlled_since);
  const scopes = detail?.granted_scopes?.length ? detail.granted_scopes.join(", ") : "none recorded";

  return html`
    <section class="banner" data-banner="${name}">
      <h2>${name} is under orchestrator control</h2>
      <p>Operated by ${controller ? raw(html`<code>${controller}</code>`) : "an orchestrator"}
         since ${since}. Scopes granted at adoption:
         <code>${scopes}</code>.</p>
      <p>While it is orchestrated this panel is read-only on it: configuration, start, restart, drain
         and console commands are refused by the runner. The two actions below are yours and stay
         available, whether or not the orchestrator is reachable.</p>
      <p><strong>Server logs stay readable by you, the host.</strong> That includes player chat.
         The logs are files on your own disk, so nothing about being orchestrated hides them, and
         players on an officially operated server may assume otherwise.</p>
      <div class="exits">
        <label class="small">Release
          <select data-exit-when>
            <option value="end_of_match">at end of match</option>
            <option value="now">now</option>
          </select>
        </label>
        <input type="text" data-exit-reason placeholder="Reason (optional, sent with the alert)">
        <button data-exit="release" class="primary">Return to local control</button>
        <button data-exit="stop" class="danger">Shut down</button>
      </div>
      <p class="muted small">Both exits notify the orchestrator first and then proceed regardless of
         the answer.</p>
      <p class="small ${exitNotes.get(name)?.bad ? "error" : ""}" data-exit-result
         ${exitNotes.has(name) ? "" : raw("hidden")}>${exitNotes.get(name)?.text ?? ""}</p>
    </section>`;
}

/** Banners for a set of instances, or nothing when none of them is orchestrated. */
export function bannersMarkup(names) {
  return names.filter(isOrchestrated).map((name) => bannerMarkup(name, detailFor(name))).join("");
}

/** Wire the exit buttons inside a rendered banner. `refresh` re-renders the screen afterwards. */
export function bindExits(root, refresh) {
  root.querySelectorAll("[data-banner]").forEach((banner) => {
    const name = banner.dataset.banner;
    const output = banner.querySelector("[data-exit-result]");

    const report = (text, bad) => {
      exitNotes.set(name, { text, bad });
      output.hidden = false;
      output.textContent = text;
      output.classList.toggle("error", !!bad);
    };

    const release = banner.querySelector('[data-exit="release"]');
    release.addEventListener("click", async () => {
      const when = banner.querySelector("[data-exit-when]").value;
      const reason = banner.querySelector("[data-exit-reason]").value.trim();
      release.disabled = true;
      report("releasing…", false);

      const result = await post(exitPath(name, detailFor(name)?.release_path, "release"),
        reason ? { when, reason } : { when });
      release.disabled = false;

      if (!result.ok) return report(errorText(result), true);

      note(result.body, { authoritative: true });
      report(when === "now"
        ? "released; this instance is local again"
        : "release scheduled for the end of the current match", false);
      refresh?.();
    });

    // Shutting a server down drops the players on it, so the second click has to be deliberate.
    const stop = banner.querySelector('[data-exit="stop"]');
    arm(stop, "Confirm shut down", async () => {
      stop.disabled = true;
      report("stopping…", false);
      const result = await post(exitPath(name, detailFor(name)?.stop_path, "stop"), {});
      stop.disabled = false;
      report(result.ok ? "stop sent" : errorText(result), !result.ok);
      refresh?.();
    });
  });
}

/**
 * Disable the mutating controls for an instance and say why.
 *
 * A disabled button with no explanation is the failure this is meant to avoid: the operator is not
 * confused about whether the panel is broken, because the reason sits next to the control and the
 * banner above it names the plane that holds the instance.
 */
export function applyGate(root, name) {
  const detail = detailFor(name);
  const controller = detail?.controller_url ?? "an orchestrator";

  root.querySelectorAll("[data-mutating]").forEach((element) => {
    // data-keep-disabled is a screen's own reason for switching a control off (a route this runner
    // does not serve, say). The gate must not hand it back when the instance turns out to be local.
    element.disabled = Boolean(detail) || element.hasAttribute("data-keep-disabled");
    if (detail) element.title = `refused while ${controller} controls ${name}`;
    else element.removeAttribute("title");
  });

  root.querySelectorAll("[data-gate-note]").forEach((slot) => {
    slot.innerHTML = detail
      ? html`<p class="gate-reason">Disabled: ${name} is controlled by
             <code>${controller}</code>. The runner refuses these with 409. Use the two exits in the
             banner above to take it back or shut it down.</p>`
      : "";
  });
}
