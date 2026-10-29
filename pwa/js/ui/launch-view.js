// Start a session: agent-ha-bridge's launch card (frontend/agent-bridge-reply-card.js, the
// launch controls), launching or resuming a session on the machine shown (session.launch).
// Shown when the machine says it can (`sessions.launch`). What it offers comes from the
// machine (launch.options), never from here: an agent, workspace or model the machine does not
// list cannot be picked. ../launch-service.js sends each press, and resends it with the same
// request until there is an outcome; once a session has started the panel jumps to its card.
//
// Unlike the card, the prompt has no 255-character limit: it goes whole, up to 16 KiB.
//
// Where the machine takes sessions from the account's other machines (sessions.transfer), Resume
// also lists the sessions they offer (sessions.transfer.serve), by machine. Picking one brings
// its transcript here as a new session, in the workspace picked here, with a command this device
// signs for the machine it is on (../transfer.js, docs/protocol/v1-transfer.md); the original
// stays where it was.
import { h } from './dom.js';
import { ASK, ALLOW_ALL, MAX_PROMPT, checkLaunch } from '../launch.js';

export const APPROVE_HINT = 'agent-bridge-host workspace add <folder>';
const AGENT_DEFAULT = '';

const sentence = (s) => (s && !/[.!?]$/.test(s) ? `${s}.` : s);
const bytes = (s) => new TextEncoder().encode(s).length;

// What a launch's progress or outcome says. tone: progress | ok | warn | bad.
export function describeLaunch(entry, { machineName } = {}) {
  const on = machineName ? ` on ${machineName}` : '';
  switch (entry.state) {
    case 'sending':
      return entry.answer?.transfer
        ? { text: `Bringing the session over and starting it${on}… A long session can take a few minutes.`, tone: 'progress' }
        : { text: `Starting the session${on}… This waits for it to start, up to a minute.`, tone: 'progress' };
    case 'waiting':
      return { text: `Waiting for the connection to the machine${on}. The launch goes as soon as it is back.`, tone: 'progress' };
    case 'failed':
      return { text: `Couldn't send it: ${entry.reason ?? 'the machine said no'}.`, tone: 'bad' };
    default:
      break;
  }
  const d = entry.detail ?? {};
  const how = d.launched?.worktree ? ' in a new worktree' : '';
  switch (entry.outcome) {
    case 'started':
      return { text: d.launched?.transfer ? `Session brought over and started${how}. The original is still on its machine.` : `Session started${how}.`, tone: 'ok' };
    case 'unconfirmed':
      return { text: `Launched${how}, but not confirmed${entry.reason ? `: ${sentence(entry.reason)}` : '. Check at the machine.'}`, tone: 'warn' };
    case 'busy':
      return { text: `Not launched: ${sentence(entry.reason ?? 'the machine is busy with another launch')} Try again in a moment.`, tone: 'warn' };
    case 'refused':
      return { text: `Not launched: ${sentence(entry.reason ?? 'the machine refused it')}`, tone: 'bad' };
    default:
      return { text: `Couldn't launch${entry.reason ? `: ${sentence(entry.reason)}` : '. Check at the machine.'}`, tone: 'bad' };
  }
}

// The choices for a model, effort or context: the agent's own default first, then what the
// machine offers, with its configured default among them.
function tuning(values, preferred) {
  const list = [...values];
  if (preferred && !list.includes(preferred)) { list.unshift(preferred); }
  return list;
}

export class LaunchPanel {
  #target = null;
  #options = null;
  // Sessions the account's other machines offer: [{ hostId, machineName, key, resumable }].
  #remote = [];
  #loading = null;
  #error = null;
  #unwatch = null;
  #jumped = null;
  #open = false;
  #signing = false;

  // root: the #launch section. service: a LaunchService. jump(sessionId): shows a session's
  // card, true once it is on screen.
  constructor(root, { service, jump = () => false }) {
    Object.assign(this, { root, service, jump });
    this.#build();
  }

  #build() {
    const e = this.els = {};
    e.toggle = this.root.querySelector('.launch-toggle');
    e.body = this.root.querySelector('.launch-body');
    e.toggle.addEventListener('click', () => this.setOpen(!this.#open));
    const field = (label, control, hint = null) => h('label', { class: 'launch-field' }, h('span', { class: 'launch-label' }, label), control, hint);
    e.resume = h('select', { name: 'resume', onchange: () => this.#draw() });
    e.agent = h('select', { name: 'agent', onchange: () => this.#draw() });
    e.workspace = h('select', { name: 'workspace' });
    e.profile = h('select', { name: 'profile' });
    e.model = h('select', { name: 'model' });
    e.effort = h('select', { name: 'effort' });
    e.context = h('select', { name: 'context' });
    e.permissions = h('select', { name: 'permissions' },
      h('option', { value: ASK }, 'Ask permission'),
      h('option', { value: ALLOW_ALL }, 'Allow all'));
    e.prompt = h('textarea', { name: 'prompt', rows: 4, placeholder: 'What should the session do first? (optional)', oninput: () => this.#drawCount() });
    e.count = h('span', { class: 'launch-count note', 'aria-live': 'polite' });
    for (const c of [e.resume, e.agent, e.workspace, e.profile, e.model, e.effort, e.context, e.permissions, e.prompt]) { c.classList.add('text-input'); }
    e.fields = {
      resume: field('Resume', e.resume),
      agent: field('Agent', e.agent),
      workspace: field('Workspace', e.workspace),
      profile: field('Profile', e.profile),
      model: field('Model', e.model),
      effort: field('Effort', e.effort),
      context: field('Context', e.context),
      permissions: field('Permissions', e.permissions),
      prompt: field('First message', e.prompt, e.count),
    };
    e.note = h('p', { class: 'note launch-note', role: 'status' });
    e.busy = h('p', { class: 'note warn launch-busy', role: 'status', hidden: true });
    e.form = h('form', { class: 'launch-form', novalidate: true, onsubmit: (ev) => { ev.preventDefault(); this.#press(); } },
      Object.values(e.fields),
      h('div', { class: 'row' },
        e.launch = h('button', { type: 'submit', class: 'button launch-button' }, 'Launch'),
        e.resend = h('button', { type: 'button', class: 'button secondary resend', hidden: true, onclick: () => this.#resend() }, 'Try again'),
        e.open = h('button', { type: 'button', class: 'button secondary launch-open', hidden: true, onclick: () => this.#openSession(true) }, 'Open session')));
    e.reload = h('button', { type: 'button', class: 'button secondary launch-reload', hidden: true, onclick: () => this.refresh() }, 'Reload choices');
    e.status = h('p', { class: 'answer-status launch-status', role: 'status', 'aria-live': 'polite' });
    e.body.replaceChildren(e.note, e.busy, e.form, e.status, h('div', { class: 'row' }, e.reload));
  }

  get open() { return this.#open; }

  get hostId() { return this.#target?.hostId ?? null; }

  setOpen(open) {
    this.#open = open;
    this.els.toggle.setAttribute('aria-expanded', String(open));
    this.els.body.hidden = !open;
    // Every time it opens: a workspace approved or a session ended since.
    if (open && this.#target && !this.#loading) { this.refresh(); }
  }

  // The machine shown, or null when it can't launch: { hostId, machineName, load, transfers }
  // where load() is launch.options from it, and transfers, when it takes sessions from the
  // account's other machines, is { remotes(), sign(remote, session) }: what they offer, and a
  // signed command for one (../transfer.js).
  setTarget(target) {
    const same = target && this.#target && target.hostId === this.#target.hostId;
    if (same) { this.#target = target; this.#title(); this.#draw(); return; }
    this.#unwatch?.();
    this.#unwatch = null;
    this.#target = target;
    this.#options = null;
    this.#remote = [];
    this.#error = null;
    this.#loading = null;
    // Another machine's choices: nothing picked on the last one carries over but the prompt.
    for (const s of this.els.form.querySelectorAll('select')) { delete s.dataset.scope; }
    this.root.hidden = !target;
    if (!target) { return; }
    this.#unwatch = this.service.watch(target.hostId, (entry) => this.#settled(entry));
    this.#title();
    this.setOpen(this.#open);
    this.#draw();
  }

  #title() {
    const name = this.#target?.machineName;
    this.root.querySelector('.launch-title').textContent = name ? `Start a session on ${name}` : 'Start a session';
  }

  // launch.options again: a workspace approved, an agent installed, a session ended since.
  refresh() {
    const target = this.#target;
    if (!target) { return Promise.resolve(); }
    this.#error = null;
    // The other machines' sessions come with the options, or not at all: one asleep is skipped.
    const remotes = target.transfers ? target.transfers.remotes().catch(() => []) : Promise.resolve([]);
    const loading = this.#loading = Promise.all([target.load(), remotes]).then(([o, remote]) => {
      if (this.#loading !== loading) { return; }
      this.#options = o;
      this.#remote = remote;
    }, (e) => {
      if (this.#loading !== loading) { return; }
      this.#error = e?.reason ?? e?.message ?? String(e);
    }).finally(() => {
      if (this.#loading === loading) { this.#loading = null; this.#draw(); }
    });
    this.#draw();
    return loading;
  }

  #agent(id) { return this.#options?.agents.find((a) => a.id === id) ?? null; }

  #resumable() {
    const value = this.els.resume.value;
    if (!value) { return null; }
    if (value.startsWith('@')) {
      const [hostId, id] = value.slice(1).split('/');
      const machine = this.#remote.find((m) => m.hostId === hostId);
      const r = machine?.resumable.find((x) => x.id === id);
      return r ? { ...r, remote: machine } : null;
    }
    return this.#options?.resumable.find((r) => r.id === value) ?? null;
  }

  // Replaces a select's options, keeping what was picked if it is still there and still about
  // the same thing (scope: the agent a model was picked for).
  static #fill(select, choices, preferred, scope = '') {
    const before = select.value;
    const keep = select.dataset.scope === scope;
    select.replaceChildren(...choices.map(([value, label]) => h('option', { value }, label)));
    const values = choices.map(([value]) => value);
    select.value = keep && values.includes(before) ? before : values.includes(preferred) ? preferred : values[0] ?? '';
    select.dataset.scope = scope;
  }

  #draw() {
    const e = this.els;
    const o = this.#options;
    const name = this.#target?.machineName || 'this machine';
    e.form.hidden = !o;
    e.reload.hidden = !o && !this.#error;
    e.reload.disabled = !!this.#loading;
    if (!o) {
      e.note.hidden = false;
      e.note.textContent = this.#error ? `Couldn't get the choices from ${name}: ${sentence(this.#error)}` : `Getting the choices from ${name}…`;
      e.note.className = `note launch-note${this.#error ? ' warn' : ''}`;
      e.busy.hidden = true;
      return;
    }

    const away = this.#remote.flatMap((m) => m.resumable.map((r) => [`@${m.hostId}/${r.id}`, `${r.title} · on ${m.machineName}`]));
    LaunchPanel.#fill(e.resume, [['', 'New session'], ...o.resumable.map((r) => [r.id, `${r.title}${r.workspace ? ` · ${r.workspace}` : ''}`]), ...away], '');
    const resume = this.#resumable();
    const agents = resume ? o.agents.filter((a) => a.kind === resume.agent) : o.agents;
    LaunchPanel.#fill(e.agent, agents.map((a) => [a.id, a.label]), resume?.launcher ?? o.defaults.agent);
    const agent = this.#agent(e.agent.value);
    LaunchPanel.#fill(e.workspace, o.workspaces.map((w) => [w.label, w.isolate ? `${w.label} (new worktree)` : w.label]), o.defaults.workspace);
    if (agent?.profiles) { LaunchPanel.#fill(e.profile, [[AGENT_DEFAULT, 'Default profile'], ...agent.profiles.map((p) => [p, p])], agent.defaults.profile ?? AGENT_DEFAULT, agent.id); }
    for (const axis of ['model', 'effort', 'context']) {
      const values = tuning(agent?.[`${axis}s`] ?? [], agent?.defaults[axis]);
      LaunchPanel.#fill(e[axis], [[AGENT_DEFAULT, 'Agent default'], ...values.map((v) => [v, v])], agent?.defaults[axis] ?? AGENT_DEFAULT, agent?.id ?? '');
      e.fields[axis].hidden = values.length === 0;
    }
    if (e.permissions.dataset.scope !== 'set') { e.permissions.value = o.defaults.permissions; e.permissions.dataset.scope = 'set'; }

    e.fields.resume.hidden = o.resumable.length === 0 && away.length === 0;
    e.fields.agent.hidden = false;
    // A session from another machine has no folder here: it opens in the workspace picked.
    e.fields.workspace.hidden = (!!resume && !resume.remote) || o.workspaces.length === 0;
    e.fields.profile.hidden = !agent?.profiles;
    e.fields.permissions.hidden = false;
    e.fields.prompt.hidden = false;
    e.prompt.placeholder = agent?.needsPrompt && !resume ? `${agent.label} starts on a first message: what should it do?` : 'What should the session do first? (optional)';

    const noAgent = o.agents.length === 0;
    const noWorkspace = (!resume || !!resume.remote) && o.workspaces.length === 0;
    e.note.className = `note launch-note${noAgent || noWorkspace ? ' warn' : ''}`;
    e.note.textContent = noAgent
      ? `No agent is installed on ${name}: install Copilot, Claude or Codex there.`
      : noWorkspace
        ? `No folder on ${name} is approved for launching yet. At the machine, run: ${APPROVE_HINT}`
        : resume?.remote
          ? `This copies the session's whole conversation, with its checkpoints and files, from ${resume.remote.machineName} to ${name}, where it opens as a new session in the workspace picked. It stays on ${resume.remote.machineName} as it was.`
          : resume ? 'A resumed session reopens in its own folder.' : '';
    e.note.hidden = !e.note.textContent;
    e.busy.textContent = o.busy ? `${name} is busy: ${sentence(o.busy)}` : '';
    e.busy.hidden = !o.busy;
    this.#drawCount();
    this.#show(this.#target ? this.service.get(this.#target.hostId) : null);
  }

  #drawCount() {
    const e = this.els;
    const n = bytes(e.prompt.value);
    e.count.textContent = n > MAX_PROMPT * 0.8 ? `${Math.ceil(n / 1024)} of ${MAX_PROMPT / 1024} KiB` : '';
    e.count.classList.toggle('warn', n > MAX_PROMPT);
  }

  // session.launch's settings, as picked. Model, effort and context always go: "" is the
  // agent's own default, which the picker offers alongside the machine's.
  settings() {
    const e = this.els;
    const resume = this.#resumable();
    const agent = this.#agent(e.agent.value);
    const launch = { agent: e.agent.value, permissions: e.permissions.value };
    if (resume?.remote) {
      if (e.workspace.value) { launch.workspace = e.workspace.value; }
      launch.transfer = { from: resume.remote.hostId, session: resume.id };
    } else if (resume) { launch.resume = resume.id; } else if (e.workspace.value) { launch.workspace = e.workspace.value; }
    if (agent?.profiles) { launch.profile = e.profile.value; }
    for (const axis of ['model', 'effort', 'context']) {
      if (!e.fields[axis].hidden) { launch[axis] = e[axis].value; }
    }
    if (e.prompt.value.trim()) { launch.prompt = e.prompt.value; }
    return launch;
  }

  #pending(entry) { return !!entry && (entry.state === 'sending' || entry.state === 'waiting'); }

  async #press() {
    const target = this.#target;
    if (!target || !this.#options) { return; }
    if (this.#pending(this.service.get(target.hostId)) || this.#signing) { return; }
    const launch = this.settings();
    const agent = this.#agent(launch.agent);
    const problem = (!launch.agent ? 'no agent is installed on this machine' : null)
      ?? (!launch.resume && !launch.workspace
        ? this.#options.workspaces.length ? 'pick a workspace' : `no workspace is approved on this machine yet; approve one at the machine (${APPROVE_HINT})`
        : null)
      ?? (launch.transfer && !target.transfers ? `this machine does not take sessions from other machines` : null)
      ?? checkLaunch(launch)
      ?? (agent?.needsPrompt && !launch.resume && !launch.transfer && !launch.prompt ? `${agent.label} starts its session only on a first message; give it a prompt` : null);
    if (problem) {
      this.#local({ text: `Not launched: ${sentence(problem)}`, tone: 'bad' });
      return;
    }
    if (launch.transfer) {
      // Signed once, for this press: a resend carries the same command, which the machine the
      // session is on acts on once.
      const remote = this.#resumable()?.remote;
      this.#signing = true;
      this.els.launch.disabled = true;
      try { launch.transfer.command = await target.transfers.sign(remote, launch.transfer.session); }
      catch (e) {
        this.#local({ text: `Not launched: couldn't sign the request for ${remote?.machineName ?? 'the other machine'}: ${sentence(e?.message ?? String(e))}`, tone: 'bad' });
        return;
      } finally {
        this.#signing = false;
        this.els.launch.disabled = false;
      }
      if (this.#target !== target) { return; }
    }
    this.#jumped = null;
    this.service.submit({ hostId: target.hostId, launch });
  }


  #resend() { if (this.#target) { this.service.resend(this.#target.hostId); } }

  #local(shown) {
    const e = this.els;
    e.status.textContent = shown.text;
    e.status.className = `answer-status launch-status ${shown.tone}`;
    this.root.dataset.launch = 'invalid';
  }

  #settled(entry) {
    this.#show(entry);
    if (entry?.state !== 'done') { return; }
    if (entry.outcome === 'started') { this.els.prompt.value = ''; this.#drawCount(); this.#openSession(false); }
    // What it offers has changed: a resumed session is no longer resumable, a worktree counts.
    if (['started', 'unconfirmed'].includes(entry.outcome)) { this.refresh(); }
  }

  // Jumps to the new session's card, which may arrive (session.upsert) after the outcome.
  #openSession(pressed) {
    const entry = this.#target ? this.service.get(this.#target.hostId) : null;
    const id = entry?.detail?.session;
    if (!id) { return; }
    if (!pressed && this.#jumped === entry.request) { return; }
    this.#jumped = entry.request;
    if (this.jump(id)) { return; }
    const until = Date.now() + 15000;
    const timer = setInterval(() => {
      if (this.jump(id) || Date.now() > until) { clearInterval(timer); }
    }, 250);
  }

  #show(entry) {
    const e = this.els;
    const shown = entry ? describeLaunch(entry, { machineName: this.#target?.machineName }) : { text: '', tone: '' };
    this.root.dataset.launch = !entry ? 'none' : entry.state !== 'done' ? entry.state : entry.outcome;
    e.status.textContent = shown.text;
    e.status.className = `answer-status launch-status${shown.tone ? ` ${shown.tone}` : ''}`;
    this.root.setAttribute('aria-busy', shown.tone === 'progress' ? 'true' : 'false');
    e.resend.hidden = entry?.state !== 'failed';
    e.open.hidden = !(entry?.state === 'done' && entry.detail?.session);
    e.launch.disabled = this.#pending(entry);
  }
}
