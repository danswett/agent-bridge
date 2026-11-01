// The sessions on screen and everything a card or the launch panel sends, for the machine
// chosen or for every machine at once: the same wiring in the live app and in the demo
// (../main.js). Each card is its own machine's: what it shows it may do comes from that
// machine's hello (capabilities, read-only and why), and what it sends goes to that machine's
// channel and no other (../answer-service.js, current(hostId)).
//
// With every machine shown (ALL, ../host-session.js), as the Windows app's "All machines":
//   * every online machine's sessions, most recently updated first, each card naming its
//     machine;
//   * a machine that can't be reached says so in a line of its own, and the others go on;
//   * the launch panel asks which machine to launch on, starting from the one last used.
import { h } from './dom.js';
import { SessionListView, renderMachines } from './views.js';
import { LaunchPanel } from './launch-view.js';
import { AnswerService } from '../answer-service.js';
import { ReplyService } from '../reply-service.js';
import { StopService } from '../stop-service.js';
import { LaunchService } from '../launch-service.js';
import { LAUNCH, TRUST } from '../launch.js';
import { TRANSFER } from '../transfer.js';
import { Capabilities } from '../protocol.js';
import { AttachmentLimits } from '../attachments.js';
import { maxPlain } from '../blobs.js';

// The machine last launched on, or picked to launch on, with every machine shown.
export const LAUNCH_HOST = 'agent-bridge.app.launchHost';

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

// The largest file a reply can carry through the blob store: what the machine and the relay
// both take, and never over the protocol's 100 MiB; 0 when either has none.
export function largeLimit(capabilities, relayMax) {
  if (!capabilities?.includes(Capabilities.sessionsAttachBlob) || !(relayMax > 0)) { return 0; }
  return Math.min(AttachmentLimits.blobBytes, maxPlain(relayMax));
}

// What the sessions list says about a machine shown that isn't open, or '' when it is.
// named: with other machines shown, so every line says which machine it is about.
export function hostNote(link, name, { online, named = false }) {
  const about = (text) => (named ? `${name}: ${text}` : text);
  switch (link.phase) {
    case 'waiting':
      return link.error ? about(link.error) : online ? `Connecting to ${name}…` : `${name} is offline. Its sessions will show when it is back.`;
    case 'opening': return `Opening an end-to-end encrypted channel to ${name}…`;
    case 'unsupported': return about(link.error);
    case 'failed': return `Couldn't reach ${name}: ${link.error}. Trying again.`;
    default: return '';
  }
}

export class SessionsPage {
  #shownCaps = new Map();
  // The machine last launched on or picked to launch on: kept here, and on the device as far as
  // its storage allows, so a picker never depends on a write that may fail.
  #launchHost = null;

  // $: getElementById. hosts: a HostSession. connection: presence, known and ready.
  // blobs(): the relay's blob store, or null; relayMax(): the largest blob it takes.
  // transfers(hostId): what launching on that machine may bring from the others, or null.
  // trust(hostId): how a folder-trust question on that machine is answered from here, or null.
  // status(id): a machine's status line. forget: a ForgetControl for offline machines, or null.
  // updates: an UpdateControl for the machines that offer updates (#51), or null.
  constructor({ $, hosts, connection, me = null, machinesFold, blobs = () => null, relayMax = () => 0, retryMs = 0, transfers = () => null, trust = () => null, status = () => '', forget = null, updates = null, storage = globalThis.localStorage, onRendered = () => {} }) {
    Object.assign(this, { $, hosts, connection, me, machinesFold, relayMax, transfers, trust, status, forget, updates, storage, onRendered });
    const current = (hostId) => hosts.route(hostId);
    const timing = retryMs ? { retryMs } : {};
    this.answers = new AnswerService({ current, ...timing });
    this.replies = new ReplyService({ current, ...timing, blobs });
    this.stops = new StopService({ current, ...timing });
    this.launches = new LaunchService({ current, ...timing });
    this.launchPanel = new LaunchPanel($('launch'), {
      service: this.launches,
      jump: (id, hostId) => this.showSession(id, hostId),
      pick: (hostId) => { this.#rememberLaunch(hostId); this.#drawLaunch(); },
      // A launch is remembered by the machine it was pressed for: the next one starts there.
      // At the press, not when it is sent: one resent after its machine comes back must not
      // take the picker from a machine picked since.
      launched: (hostId) => this.#rememberLaunch(hostId),
    });
    this.view = new SessionListView($('session-list'), hosts.sessions, {
      fetchDetail: (hostId, id) => {
        const client = hosts.link(hostId)?.client;
        return client ? client.getSession(id) : Promise.reject(new Error('not connected'));
      },
      machine: (hostId) => ({ name: hosts.nameOf(hostId) ?? 'another machine', show: this.#several() }),
      answering: (hostId) => this.answering(hostId),
    });
    this.view.onRender = (n) => this.#drawCount(n);
    // A new channel to a machine: what waits to be resent to it goes now, and only to it.
    hosts.onOpen((id) => {
      for (const s of [this.answers, this.replies, this.stops, this.launches]) { s.ready(id); }
      this.launchPanel.reopened(id);
    });
    hosts.onChange(() => this.refresh());
  }

  // Every machine shown, more than one of them: the cards say which machine each is on.
  #several() { return this.hosts.all && this.hosts.shownIds().length > 1; }

  // Who answers a machine's questions, sends its replies and ends its sessions, and whether
  // it takes each: null for a machine not shown.
  answering(hostId) {
    const link = this.hosts.link(hostId);
    if (!link) { return null; }
    return {
      service: this.answers, replies: this.replies, stops: this.stops, hostId,
      capabilities: link.capabilities, readOnly: link.machine?.readOnly ?? null,
      largeAttachments: () => largeLimit(link.capabilities, this.relayMax()),
    };
  }

  // A session's card, once it has arrived: unfolded and scrolled to. False until then.
  showSession(id, hostId = null) {
    const card = this.view.card(hostId, id);
    if (!card) { return false; }
    if (card.folded) { card.setFolded(false); }
    const el = card.element;
    el.classList.add('linked');
    setTimeout(() => el.classList.remove('linked'), 4000);
    el.scrollIntoView({ block: 'start' });
    el.querySelector('.fold')?.focus({ preventScroll: true });
    return true;
  }

  // Everything tied to the signed-in account goes with it.
  clear() {
    this.answers.clear();
    this.replies.clear();
    this.stops.clear();
    this.launches.clear();
    this.launchPanel.setMachines(null);
    this.launchPanel.setTarget(null);
  }

  // A line in place of the sessions, for something that stopped the page altogether.
  setHostState(text) {
    const s = this.$('host-state');
    s.replaceChildren(...(text ? [h('span', { class: 'host-note' }, text)] : []));
    s.hidden = !text;
    this.#drawCount(this.hosts.sessions.size);
  }

  refresh() {
    const { hosts } = this;
    const links = hosts.links();
    for (const id of [...this.#shownCaps.keys()]) { if (!hosts.link(id)) { this.#shownCaps.delete(id); } }
    for (const link of links) {
      const caps = `${link.capabilities.join(' ')}:${link.machine?.readOnly ?? ''}`;
      if (this.#shownCaps.get(link.id) !== caps) { this.#shownCaps.set(link.id, caps); this.view.refreshQuestions(link.id); }
    }
    this.view.refreshMachines();
    this.$('sessions').hidden = hosts.selection === null;
    this.$('sessions-h').textContent = this.#heading();
    this.#drawNotes(links);
    this.#drawLaunch();
    this.drawMachines();
  }

  #heading() {
    const { hosts } = this;
    if (hosts.selection === null) { return 'Sessions'; }
    const ids = hosts.shownIds();
    if (hosts.all && ids.length !== 1) { return ids.length ? 'Sessions on all machines' : 'Sessions'; }
    return `Sessions on ${hosts.nameOf(ids[0]) ?? 'your machine'}`;
  }

  #drawNotes(links) {
    const { hosts, connection } = this;
    let lines;
    if (hosts.all && links.length > 1 && !connection.ready) {
      // One line for the relay, not one per machine.
      lines = [[null, 'The relay connection dropped; reconnecting.']];
    } else {
      const named = this.#several();
      lines = links.map((l) => [l.id, hostNote(l, hosts.nameOf(l.id) ?? 'your machine', { online: hosts.isOnline(l.id), named })]).filter(([, text]) => text);
    }
    const s = this.$('host-state');
    s.replaceChildren(...lines.map(([id, text]) => h('span', { class: 'host-note', dataset: id ? { hostId: id } : null }, text)));
    s.hidden = lines.length === 0;
    this.#drawCount(hosts.sessions.size);
  }

  #drawCount(n) {
    const { hosts, $ } = this;
    const several = this.#several();
    const machines = new Set(several ? hosts.sessions.list().map((e) => e.hostId) : []).size;
    $('session-count').textContent = !n ? '' : several ? `${n} live on ${plural(machines, 'machine', 'machines')}` : `${n} live`;
    const links = hosts.links();
    const synced = (l) => l.phase === 'open' && l.client?.synced !== false;
    // Only once every machine shown has said what it has, or that it can't: an empty list is
    // not news while one is still connecting.
    const settled = links.length
      ? links.some(synced) && links.every((l) => synced(l) || l.phase === 'unsupported')
      : hosts.all && this.connection.ready;
    const empty = $('sessions-empty');
    empty.hidden = n !== 0 || !settled;
    empty.textContent = hosts.all && links.length !== 1
      ? links.length ? 'No live sessions on any of your machines.' : 'No live sessions: none of your machines is online.'
      : `No live sessions on ${hosts.nameOf(links[0]?.id) ?? 'this machine'}.`;
    this.onRendered(n);
  }

  // The launch panel: for the machine chosen, or with every machine shown, for the one picked
  // in it. While a machine reconnects its panel stays, with any launch still waiting to be
  // resent; one that comes back without sessions.launch takes it away. A machine that takes no
  // launches now (read-only, or agent-ha-bridge runs there) still gets a panel, to show a
  // folder-trust question waiting on it read-only, while it reconnects too; but with every
  // machine shown, only when none of them launches, so the picker offers only machines that do.
  #drawLaunch() {
    const { hosts, launchPanel: panel } = this;
    if (hosts.selection === null) { panel.setMachines(null); panel.setTarget(null); return; }
    const links = hosts.links();
    const launches = links.filter((l) => this.#launches(l));
    const inspect = hosts.all && launches.length ? [] : links.filter((l) => !this.#launches(l) && this.#inspectOnly(l));
    const can = [...launches, ...inspect];
    const has = (id) => !!id && can.some((l) => l.id === id);
    const pick = hosts.all ? [this.#launchHost, this.#read(LAUNCH_HOST), panel.hostId].find(has) ?? can[0]?.id ?? null : can[0]?.id ?? null;
    panel.setMachines(hosts.all ? can.map((l) => ({ hostId: l.id, name: hosts.nameOf(l.id) ?? l.id.slice(0, 8) })) : null, pick);
    panel.setTarget(pick ? this.launchTarget(pick) : null);
  }

  #launches(l) {
    const panel = this.launchPanel;
    return l.capabilities.includes(LAUNCH.capability) && (l.phase === 'open' || (l.id === panel.hostId && !panel.inspectOnly));
  }

  #inspectOnly(l) {
    const panel = this.launchPanel;
    return l.phase === 'open' ? !!l.machine?.readOnly : l.id === panel.hostId && panel.inspectOnly;
  }

  // What the launch panel is told about a machine: a new object each time, for the same machine,
  // as a presence update hands in.
  launchTarget(id) {
    const { hosts, launchPanel: panel } = this;
    const link = hosts.link(id);
    const base = {
      hostId: id,
      machineName: hosts.nameOf(id) ?? 'your machine',
      load: () => (hosts.link(id)?.client ? hosts.link(id).client.launchOptions() : Promise.reject(new Error('not connected'))),
    };
    if (link && !this.#launches(link) && this.#inspectOnly(link)) {
      return { ...base, transfers: null, trust: null, inspectOnly: true, readOnly: link.phase === 'open' ? link.machine?.readOnly ?? null : panel.readOnly };
    }
    return {
      ...base,
      // The account's other machines' sessions, each signed for when picked: only where this one
      // takes them, and only what they hand on.
      transfers: link?.capabilities.includes(TRANSFER.capability) ? this.transfers(id) : null,
      trust: link?.capabilities.includes(TRUST.capability) ? this.trust(id) : null,
    };
  }

  drawMachines() {
    const { hosts, connection, $ } = this;
    const presence = connection.presence;
    const n = renderMachines($('machine-list'), {
      presence, me: this.me, current: hosts.selection,
      onSelect: (id) => { hosts.select(id); $('sessions').focus(); },
      status: (id) => this.status(id),
      known: connection.known ?? null,
      forget: this.forget,
      updates: this.updates,
      allStatus: hosts.all ? plural(hosts.sessions.size, 'session', 'sessions') : '',
    });
    $('machines-empty').hidden = n !== 0 || !connection.ready;
    // The machine whose sessions are on screen stays named after it drops out of presence,
    // as the sessions heading keeps it: the host session holds on to its metadata.
    const ids = hosts.shownIds();
    const viewing = hosts.selection === null ? null
      : hosts.all ? (ids.length > 1 ? 'all' : ids.length ? hosts.nameOf(ids[0]) : null)
        : hosts.nameOf(hosts.targetId);
    this.machinesFold.update(n, viewing);
  }

  #rememberLaunch(hostId) {
    this.#launchHost = hostId;
    this.#write(LAUNCH_HOST, hostId);
  }

  #read(key) { try { return this.storage?.getItem(key) ?? null; } catch { return null; } }
  #write(key, value) { try { this.storage?.setItem(key, value); } catch { /* a nicety */ } }
}

