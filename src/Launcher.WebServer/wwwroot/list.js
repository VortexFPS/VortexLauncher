// Instance list: the landing screen, and the only one that polls the whole box.

import { errorText, runnerStatus } from "./client.js";
import { bannersMarkup, bindExits, ensureDetail, isOrchestrated, note } from "./orchestrated.js";
import { dash, fmtBytes, fmtPercent, fmtPlayers, html, raw } from "./util.js";

const POLL_MS = 5000;

export async function listView(root) {
  let disposed = false;

  /**
   * Whether the operator is part-way through an exit in one of the banners.
   *
   * This screen rebuilds its whole DOM on every poll, and the banner holds a text field, a select and
   * a two-click confirm. A poll landing between the two clicks of "Shut down" throws the armed state
   * away, so the second click only re-arms and the button reads as dead: exactly the failure the
   * banner exists to prevent, on the one action that has to work whether or not the orchestrator
   * does. A half-typed release reason goes the same way. Player counts can wait a poll; an exit the
   * operator is halfway through cannot, and this resumes the moment they click away.
   */
  function midExit() {
    const active = document.activeElement;
    return Boolean(root.querySelector("[data-banner] .armed")
      || (active && root.contains(active) && active.closest("[data-banner]")));
  }

  // `force` is the refresh that follows an exit the operator just took: it has to redraw even though
  // focus is still on the button they pressed, or the banner outlives the release that removed it.
  async function load({ force = false } = {}) {
    const result = await runnerStatus();
    if (disposed || (!force && midExit())) return;

    if (!result.ok) {
      root.innerHTML = html`
        <h1>Instances</h1>
        <div class="notice bad">${errorText(result)}
          ${result.status === 503
            ? raw(html`<p class="muted small">Start one with <code>vortex runner run</code>.</p>`)
            : ""}
        </div>`;
      return;
    }

    const instances = result.body?.instances ?? [];

    // The list reports the mode but not who holds it, so fill in the controlling plane once per
    // instance that turns up orchestrated. Everything after that is served from the cache.
    instances.forEach(note);
    await Promise.all(instances
      .filter((i) => isOrchestrated(i.name))
      .map((i) => ensureDetail(i.name)));
    // Re-checked: those reads take a round trip, and the operator can have armed a confirm meanwhile.
    if (disposed || (!force && midExit())) return;

    render(instances, result.body);
  }

  function render(instances, runner) {
    root.innerHTML = html`
      <h1>Instances</h1>
      <p class="muted small">${runner?.hostname ?? "this box"} is running
        ${instances.length} instance${instances.length === 1 ? "" : "s"}.</p>

      ${raw(bannersMarkup(instances.map((i) => i.name)))}

      <div class="actions"><a class="button primary" href="#/new">New instance</a></div>

      ${instances.length === 0
        ? raw(html`<p class="muted">No instances yet.</p>`)
        : raw(html`
          <table>
            <thead><tr>
              <th>Instance</th><th>State</th><th>Map</th><th>Players</th>
              <th>CPU</th><th>Memory</th><th>Control</th>
            </tr></thead>
            <tbody>${instances.map((i) => raw(row(i)))}</tbody>
          </table>`)}`;

    bindExits(root, () => load({ force: true }));
  }

  function row(status) {
    return html`
      <tr>
        <td><a href="#/i/${encodeURIComponent(status.name)}"><strong>${status.name}</strong></a></td>
        <td><span class="chip ${status.state}">${status.state}</span></td>
        <td>${dash(status.map)}</td>
        <td>${fmtPlayers(status)}</td>
        <td>${fmtPercent(status.cpu_percent)}</td>
        <td>${fmtBytes(status.memory_bytes)}</td>
        <td>${status.control_mode === "orchestrated"
              ? raw(html`<span class="chip orchestrated">orchestrated</span>`)
              : raw(html`<span class="chip">local</span>`)}</td>
      </tr>`;
  }

  await load({ force: true });
  const timer = setInterval(() => load(), POLL_MS);

  return () => {
    disposed = true;
    clearInterval(timer);
  };
}
