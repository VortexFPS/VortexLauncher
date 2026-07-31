// Transport. Every call in the panel goes through here, and nothing here knows what a screen is.
//
// Thin client of the runner API by design: anything this page can do, `vortex` and Conductor can do,
// because all three go through the same protocol to the same runner.

const params = new URLSearchParams(location.search);

/** Bearer token, taken from ?token= the way the runner prints the panel URL at install time. */
export const token = params.get("token") ?? "";

/**
 * One request. Never throws and never redirects on an error status: every caller wants the status
 * and the body, because the interesting bodies here are the error ones (a 409 carries the whole
 * orchestrated banner).
 */
export async function request(path, { method = "GET", body, signal } = {}) {
  const init = {
    method,
    signal,
    headers: {
      "Authorization": `Bearer ${token}`,
      // The runner writes an audit entry per mutating call and keeps it independently of any plane.
      // Naming the panel makes that log answer "who did this" rather than "some process on the box".
      "X-Actor": "web-panel",
    },
  };

  if (body !== undefined) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }

  let response;
  try {
    response = await fetch(path, init);
  } catch (err) {
    if (err?.name === "AbortError") throw err;
    return {
      status: 0, ok: false,
      body: { code: "unreachable", message: `could not reach this box: ${err.message}` },
    };
  }

  const text = await response.text();
  let parsed = null;
  if (text) {
    try {
      parsed = JSON.parse(text);
    } catch {
      parsed = { code: "invalid_response", message: text.slice(0, 400) };
    }
  }

  return { status: response.status, ok: response.ok, body: parsed };
}

export const get = (path, init) => request(path, init);
export const post = (path, body) => request(path, { method: "POST", body });
export const patch = (path, body) => request(path, { method: "PATCH", body });
// No put. The runner API documents no PUT on any route, so a helper for one is a trap: it reads as
// available, and what it produces is a 404 that looks like a missing feature rather than a wrong verb.
export const del = (path) => request(path, { method: "DELETE" });

export const instancePath = (name) => `/api/v1/instances/${encodeURIComponent(name)}`;

/**
 * The runner snapshot, shared between the header and the instance list.
 *
 * It is the plane's cached copy of the last heartbeat rather than a round trip to the runner, so
 * polling it twice a second would still be cheap; the short dedupe just keeps two screens that both
 * want it from issuing two requests.
 */
let snapshot = { at: 0, result: null };
let inflight = null;

export function runnerStatus() {
  if (Date.now() - snapshot.at < 2000 && snapshot.result) return Promise.resolve(snapshot.result);
  if (inflight) return inflight;

  inflight = request("/api/v1/status").then((result) => {
    snapshot = { at: Date.now(), result };
    inflight = null;
    return result;
  });
  return inflight;
}

/** WebSocket URL for the live console. The token goes in the query because a browser cannot set a
 *  header on a WebSocket upgrade, and the plane accepts it there for exactly that reason. */
export function consoleUrl(name) {
  const scheme = location.protocol === "https:" ? "wss:" : "ws:";
  return `${scheme}//${location.host}/api/v1/console/${encodeURIComponent(name)}`
    + `?token=${encodeURIComponent(token)}`;
}

/** A sentence for the operator out of any failure shape this API produces. */
export function errorText(result) {
  if (!result) return "no response";
  const body = result.body ?? {};

  if (result.status === 401) {
    return token
      ? "the token in this URL was rejected; open the panel with the link the runner printed"
      : "no token in the URL; open the panel with the link the runner printed at install time";
  }
  if (result.status === 503) return body.message ?? "no runner is linked to this control plane";
  if (result.status === 504) return body.message ?? "the runner did not answer in time";

  return body.message ?? `request failed with status ${result.status}`;
}
