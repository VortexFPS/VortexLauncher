// Create and edit, over the instance spec. Every field here is a field of InstanceSpec in
// protocol/runner-api-v1.yaml, and nothing else is invented.

import { errorText, get, instancePath, request } from "./client.js";
import {
  applyGate, bannerMarkup, bindExits, detailFor, isOrchestrated, mutate, note,
} from "./orchestrated.js";
import { html, raw } from "./util.js";

// The runner's own rule: instance names become directory names and command-line arguments, so they
// are restricted rather than sanitized. Rejecting here saves a round trip and says the same thing.
const NAME_PATTERN = /^[A-Za-z0-9._-]{1,64}$/;
const TIME_PATTERN = /^([01]\d|2[0-3]):[0-5]\d$/;
const SHA256_PATTERN = /^[0-9a-f]{64}$/;

const RESTART_POLICIES = ["always", "on_failure", "never"];

export async function formView(root, name) {
  const editing = Boolean(name);
  let spec = null;
  let builds = [];
  let message = null;

  if (editing) {
    const result = await get(instancePath(name));
    if (!result.ok) {
      root.innerHTML = html`
        <p class="crumbs"><a href="#/">All instances</a></p>
        <div class="notice bad">${errorText(result)}</div>`;
      return;
    }
    spec = result.body;
    note(spec);
  }

  const buildList = await get("/api/v1/builds");
  if (buildList.ok && Array.isArray(buildList.body)) builds = buildList.body;

  function buildOptions() {
    const ids = builds.map((b) => b.id);
    // Keep a pin that is no longer in the store as an option, so opening the form and saving does not
    // quietly move an instance onto a different build.
    if (spec?.build_id && !ids.includes(spec.build_id)) ids.unshift(spec.build_id);

    return html`
      <option value="">store default, pinned at start</option>
      ${ids.map((id) => {
        const build = builds.find((b) => b.id === id);
        const label = build ? `${id} (${build.provider} ${build.version})` : `${id} (not installed)`;
        return raw(html`<option value="${id}" ${id === spec?.build_id ? raw("selected") : ""}>
          ${label}</option>`);
      })}`;
  }

  function render() {
    root.innerHTML = html`
      <p class="crumbs"><a href="#/">All instances</a>
        ${editing ? raw(html` / <a href="#/i/${encodeURIComponent(name)}">${name}</a>`) : ""}</p>
      <h1>${editing ? `Edit ${name}` : "New instance"}</h1>

      ${editing && isOrchestrated(name) ? raw(bannerMarkup(name, detailFor(name))) : ""}
      <div id="msg">${message ? raw(html`<div class="notice bad">${message}</div>`) : ""}</div>

      <form id="spec-form" novalidate>
        <div class="grid2">
          <div class="field">
            <label><span>Name</span>
              <input type="text" name="name" value="${spec?.name ?? ""}"
                     ${editing ? raw("disabled") : ""} data-mutating>
            </label>
            <p class="hint">${editing
              ? "Fixed: the runner keys the instance by name, and a rename is ignored."
              : "Letters, digits, dot, dash and underscore. Up to 64 characters."}</p>
            <p class="error" data-error="name"></p>
          </div>

          <div class="field">
            <label><span>Port</span>
              <input type="number" name="port" value="${spec?.port ?? ""}" min="1" max="65535"
                     data-mutating>
            </label>
            <p class="hint">Always explicit. The pool the CLI allocates from is 26000 to 26099.</p>
            <p class="error" data-error="port"></p>
          </div>

          <div class="field">
            <label><span>Map</span>
              <input type="text" name="map" value="${spec?.map ?? ""}" data-mutating>
            </label>
            <p class="error" data-error="map"></p>
          </div>

          <div class="field">
            <label><span>Gametype</span>
              <input type="text" name="gametype" value="${spec?.gametype ?? "dm"}" data-mutating>
            </label>
          </div>

          <div class="field">
            <label><span>Max players</span>
              <input type="number" name="max_players" value="${spec?.max_players ?? 16}" min="1"
                     data-mutating>
            </label>
            <p class="error" data-error="max_players"></p>
          </div>

          <div class="field">
            <label><span>Hostname (server browser name)</span>
              <input type="text" name="hostname" value="${spec?.hostname ?? ""}" data-mutating>
            </label>
          </div>

          <div class="field">
            <label><span>Build</span>
              <select name="build_id" data-mutating>${raw(buildOptions())}</select>
            </label>
            ${builds.length === 0
              ? raw(html`<p class="hint">No builds listed by the runner.</p>`)
              : ""}
          </div>

          <div class="field">
            <label><span>Restart policy</span>
              <select name="restart_policy" data-mutating>
                ${RESTART_POLICIES.map((policy) => raw(html`
                  <option value="${policy}"
                    ${policy === (spec?.restart_policy ?? "on_failure") ? raw("selected") : ""}>
                    ${policy}</option>`))}
              </select>
            </label>
          </div>

          <div class="field">
            <label><span>Scheduled restart (HH:mm, local time)</span>
              <input type="text" name="restart_at" value="${spec?.restart_at ?? ""}"
                     placeholder="05:00" data-mutating>
            </label>
            <p class="hint">The box's local time, not UTC. Leave empty for none.</p>
            <p class="error" data-error="restart_at"></p>
          </div>

          <div class="field inline">
            <label>
              <input type="checkbox" name="restart_only_when_empty"
                     ${spec?.restart_only_when_empty === false ? "" : raw("checked")} data-mutating>
              <span>Skip a scheduled restart while players are connected</span>
            </label>
          </div>
        </div>

        <div class="field">
          <label><span>Extra arguments, one per line</span>
            <textarea name="extra_args" rows="3" data-mutating>${(spec?.extra_args ?? []).join("\n")}</textarea>
          </label>
          <p class="hint">Appended after the runner's own. One argument per line, no shell quoting.</p>
        </div>

        <div class="field">
          <label><span>Environment, one KEY=value per line</span>
            <textarea name="environment" rows="3" data-mutating>${envText(spec?.environment)}</textarea>
          </label>
          <p class="error" data-error="environment"></p>
        </div>

        <div class="field">
          <label><span>Content set: one package sha256 per line</span>
            <textarea name="content_set" rows="3" data-mutating>${(spec?.content_set ?? []).join("\n")}</textarea>
          </label>
          <p class="hint">The runner fetches what it is missing and verifies before installing.
             A package that fails leaves the instance on its previous set.</p>
          <p class="error" data-error="content_set"></p>
        </div>

        <div class="actions">
          <button type="submit" class="primary" data-mutating>
            ${editing ? "Save changes" : "Create instance"}</button>
          <a class="button" href="${editing ? `#/i/${encodeURIComponent(name)}` : "#/"}">Cancel</a>
        </div>
        <div data-gate-note></div>
      </form>`;

    root.querySelector("#spec-form").addEventListener("submit", submit);
    bindExits(root, () => render());
    if (editing) applyGate(root, name);
  }

  function fieldValues() {
    const form = root.querySelector("#spec-form");
    const value = (field) => form.elements[field].value.trim();
    return {
      name: editing ? name : value("name"),
      map: value("map"),
      gametype: value("gametype") || "dm",
      port: value("port"),
      max_players: value("max_players"),
      hostname: value("hostname"),
      build_id: value("build_id"),
      restart_policy: value("restart_policy"),
      restart_at: value("restart_at"),
      restart_only_when_empty: form.elements.restart_only_when_empty.checked,
      extra_args: lines(value("extra_args")),
      environment: value("environment"),
      content_set: lines(value("content_set")),
    };
  }

  /** Client-side checks for exactly what the API checks, so a refusal is not a round trip away. */
  function validate(values) {
    const errors = {};

    if (!NAME_PATTERN.test(values.name) || values.name === "." || values.name === "..")
      errors.name = "letters, digits, dot, dash and underscore only, up to 64 characters";

    if (!values.map) errors.map = "a map is required";

    const port = Number(values.port);
    if (!Number.isInteger(port) || port < 1 || port > 65535)
      errors.port = "a port between 1 and 65535 is required";

    const maxPlayers = Number(values.max_players);
    if (!Number.isInteger(maxPlayers) || maxPlayers < 1)
      errors.max_players = "at least one player slot is required";

    if (values.restart_at && !TIME_PATTERN.test(values.restart_at))
      errors.restart_at = "use HH:mm, for example 05:00";

    const badHash = values.content_set.find((hash) => !SHA256_PATTERN.test(hash));
    if (badHash) errors.content_set = `not a package sha256: ${badHash}`;

    const badEnv = lines(values.environment).find((line) => !/^[^=\s]+=/.test(line));
    if (badEnv) errors.environment = `expected KEY=value: ${badEnv}`;

    return errors;
  }

  function showErrors(errors) {
    root.querySelectorAll("[data-error]").forEach((slot) => {
      slot.textContent = errors[slot.dataset.error] ?? "";
    });
  }

  async function submit(event) {
    event.preventDefault();

    const values = fieldValues();
    const errors = validate(values);
    showErrors(errors);
    if (Object.keys(errors).length > 0) return;

    // The runner deserializes a full InstanceSpec on both verbs, so a patch sends every field rather
    // than a delta. Control fields are runner state and are deliberately not sent: a spec edit that
    // could set them would be a way to hand the box over without the release path.
    const body = {
      name: values.name,
      map: values.map,
      gametype: values.gametype,
      port: Number(values.port),
      max_players: Number(values.max_players),
      hostname: values.hostname || null,
      build_id: values.build_id || null,
      restart_policy: values.restart_policy,
      restart_at: values.restart_at || null,
      restart_only_when_empty: values.restart_only_when_empty,
      extra_args: values.extra_args,
      environment: parseEnv(values.environment),
      content_set: values.content_set,
    };

    const result = editing
      ? await mutate(name, instancePath(name), { method: "PATCH", body })
      : await request("/api/v1/instances", { method: "POST", body });

    if (result.ok) {
      location.hash = `#/i/${encodeURIComponent(body.name)}`;
      return;
    }

    message = result.gated
      ? "Refused: this instance is under orchestrator control, so its configuration is read-only here."
      : errorText(result);
    render();
  }

  render();
}

const lines = (text) => text.split("\n").map((line) => line.trim()).filter(Boolean);

function envText(environment) {
  return Object.entries(environment ?? {}).map(([key, value]) => `${key}=${value}`).join("\n");
}

function parseEnv(text) {
  const environment = {};
  for (const line of lines(text)) {
    const at = line.indexOf("=");
    environment[line.slice(0, at)] = line.slice(at + 1);
  }
  return environment;
}
