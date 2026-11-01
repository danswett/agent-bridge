// Start a session: agent-ha-bridge's launch card (frontend/agent-bridge-reply-card.js, the
// launch controls), launching or resuming a session on the machine shown (session.launch).
// Shown when the machine says it can (`sessions.launch`). What it offers comes from the
// machine (launch.options), never from here: an agent, workspace or model the machine does not
// list cannot be picked. ../launch-service.js sends each press, and resends it with the same
// request until there is an outcome; once a session has started the panel jumps to its card.
//
// Unlike the card, the prompt has no 255-character limit: it goes whole, up to 16 KiB.
//
// With every machine shown, the panel first asks which one to launch on, as the Windows app's
// launch page does, starting from the one last launched on or picked here.
//
// Where the machine takes sessions from the account's other machines (sessions.transfer), Resume
// also lists the sessions they offer (sessions.transfer.serve), by machine. Picking one brings
// its transcript here as a new session, in the workspace picked here, with a command this device
// signs for the machine it is on (../transfer.js, docs/protocol/v1-transfer.md); the original
// stays where it was.
//
// A launched agent asking whether to trust its folder (launch.trust) is shown here in full: the
// agent, the whole folder path, the repository root it applies to, and the agent's own warning.
// Where the machine allows it, Trust and Quit answer it with a command this device signs for that
// choice (../transfer.js); otherwise it says to answer at the machine, and how to allow it.
import { h } from './dom.js';
import { ASK, ALLOW_ALL, MAX_PROMPT, TRUST, checkLaunch } from '../launch.js';

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

// What a folder-trust answer's progress or outcome says. tone: progress | ok | warn | bad.
export function describeTrust(answer, question) {
  const label = question?.label ?? 'The agent';
  switch (answer.state) {
    case 'signing':
    case 'sending':
      return { text: answer.choice === TRUST.trust ? `Trusting the folder and waiting for ${label} to start… This takes up to a minute.` : `Telling ${label} not to trust the folder…`, tone: 'progress' };
    case 'failed':
      return { text: `Couldn't send it: ${sentence(answer.reason ?? 'the machine did not answer')} Nothing was typed if the machine never got it; try again.`, tone: 'bad' };
    default:
      break;
  }
  const r = answer.result ?? {};
  switch (r.outcome) {
    case 'started':
      return { text: `Trusted. ${label} started.`, tone: 'ok' };
    case 'declined':
      return { text: `Not trusted. ${sentence(r.reason ?? `${label} closed; nothing was launched`)}`, tone: 'ok' };
    case 'refused':
      return { text: `Not answered: ${sentence(r.reason ?? 'the machine refused it')}`, tone: 'bad' };
    default:
      return { text: sentence(r.reason ?? 'Not confirmed: answer at the machine.'), tone: 'warn' };
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
  #asking = null;
  #loading = null;
  #error = null;
  #unwatch = null;
  #jumped = null;
  #open = false;
  #signing = false;
  // Counts changes of the machine picked, which presence updates for the same one do not.
  #selection = 0;
  // Folder-trust answers pressed here, by machine and then question id: { hostId, question,
  // choice, state, data, result, reason, trust }. Kept across machines picked, so a signed answer
  // whose response was lost can still be resent, and one that may have typed is never offered
  // again. A question answered stays shown, with its outcome, after the machine stops listing it.
  #trustAnswers = new Map();

  // root: the #launch section. service: a LaunchService. jump(sessionId, hostId): shows a
  // session's card, true once it is on screen. pick(hostId): another machine was picked.
  // launched(hostId): Launch was pressed for that machine.
  constructor(root, { service, jump = () => false, pick = () => {}, launched = () => {} }) {
    Object.assign(this, { root, service, jump, pick, launched });
    this.#build();
  }

  #build() {
    const e = this.els = {};
    e.toggle = this.root.querySelector('.launch-toggle');
    e.body = this.root.querySelector('.launch-body');
    e.toggle.addEventListener('click', () => this.setOpen(!this.#open));
    const field = (label, control, hint = null) => h('label', { class: 'launch-field' }, h('span', { class: 'launch-label' }, label), control, hint);
    e.machine = h('select', { name: 'machine', class: 'text-input', onchange: () => this.pick(e.machine.value) });
    e.machineField = h('label', { class: 'launch-field launch-machine', hidden: true }, h('span', { class: 'launch-label' }, 'Machine'), e.machine);
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
    e.trust = h('div', { class: 'launch-trust', hidden: true });
    e.form = h('form', { class: 'launch-form', novalidate: true, onsubmit: (ev) => { ev.preventDefault(); this.#press(); } },
      Object.values(e.fields),
      h('div', { class: 'row' },
        e.launch = h('button', { type: 'submit', class: 'button launch-button' }, 'Launch'),
        e.resend = h('button', { type: 'button', class: 'button secondary resend', hidden: true, onclick: () => this.#resend() }, 'Try again'),
        e.open = h('button', { type: 'button', class: 'button secondary launch-open', hidden: true, onclick: () => this.#openSession(true) }, 'Open session')));
    e.reload = h('button', { type: 'button', class: 'button secondary launch-reload', hidden: true, onclick: () => this.refresh() }, 'Reload choices');
    e.status = h('p', { class: 'answer-status launch-status', role: 'status', 'aria-live': 'polite' });
    e.body.replaceChildren(e.machineField, e.note, e.busy, e.trust, e.form, e.status, h('div', { class: 'row' }, e.reload));
  }

  get open() { return this.#open; }

  get hostId() { return this.#target?.hostId ?? null; }
  // Whether the machine shown is shown read-only (it takes no launches now), and why.
  get inspectOnly() { return !!this.#target?.inspectOnly; }
  get readOnly() { return this.#target?.readOnly ?? null; }

  setOpen(open) {
    this.#open = open;
    this.els.toggle.setAttribute('aria-expanded', String(open));
    this.els.body.hidden = !open;
    // Every time it opens: a workspace approved or a session ended since.
    if (open && this.#target && !this.#loading) { this.refresh(); }
  }

  // The machines it can launch on, when there is a choice: [{ hostId, name }], or null; picked:
  // the one whose choices are shown (setTarget). Outside the form, so another can be picked
  // while one's choices are still coming, or could not be had.
  setMachines(list, picked) {
    const e = this.els;
    const choices = list?.length > 1 ? list : [];
    e.machineField.hidden = choices.length === 0;
    const key = JSON.stringify(choices.map((m) => [m.hostId, m.name]));
    if (e.machine.dataset.list !== key) {
      e.machine.replaceChildren(...choices.map((m) => h('option', { value: m.hostId }, m.name)));
      e.machine.dataset.list = key;
    }
    if (choices.length) { e.machine.value = picked; }
  }

  // The machine shown, or null when it can't launch: { hostId, machineName, load, transfers,
  // trust } where load() is launch.options from it, and transfers, when it takes sessions from
  // the account's other machines, is { remotes(), sign(remote, session) }: what they offer, and a
  // signed command for one (../transfer.js). trust, when it takes launch.trust.answer, is
  // { sign(question, choice), answer(data) }: a command this device signs for that choice on that
  // machine, and launch.trust.answer sent with it. inspectOnly: the machine does not take
  // launches now (read-only, or agent-ha-bridge runs there), but a folder-trust question waiting
  // on it is still shown, read-only, with why; readOnly says why when it is known. The panel
  // shows only when there is such a question.
  setTarget(target) {
    const same = target && this.#target && target.hostId === this.#target.hostId;
    if (same) {
      const was = !!this.#target.inspectOnly;
      this.#target = target;
      this.#title();
      this.#draw();
      // Read-only begun or ended: what the machine says about its questions (answerable, why
      // not) has changed, so it is asked again, superseding any answer still on its way.
      if (!!target.inspectOnly !== was) { this.refresh(); }
      // A machine shown only for its questions, with none shown, is asked again on its next
      // update: at once when its list could not be read, else at most every so often. Nothing
      // else would ask: with no question the panel is hidden, and its Reload with it, and a
      // question can begin there at any time (a launch at its terminal).
      else if (target.inspectOnly && !this.#loading && this.#trustQuestions().length === 0
        && ((this.#error && !this.#options) || Date.now() - this.#askedAt >= LaunchPanel.recheckMs)) { this.refresh(); }
      return;
    }
    this.#unwatch?.();
    this.#unwatch = null;
    this.#selection++;
    this.#target = target;
    this.#options = null;
    this.#remote = [];
    this.#asking = null;
    this.#error = null;
    this.#loading = null;
    // The last machine's questions go at once: their buttons must never act on this one.
    this.els.trust.replaceChildren();
    this.els.trust.hidden = true;
    // Another machine's choices: nothing picked on the last one carries over but the prompt.
    for (const s of this.els.form.querySelectorAll('select')) { delete s.dataset.scope; }
    this.root.hidden = !target;
    if (!target) { return; }
    this.#unwatch = this.service.watch(target.hostId, (entry) => this.#settled(entry));
    // One that finished while another machine was picked was not watched then: the list asked
    // for next supersedes the question it named, as it would have.
    this.#track(target.hostId, this.service.get(target.hostId));
    this.#title();
    this.setOpen(this.#open);
    // Its questions are looked for even with the panel closed: it shows only if there is one.
    if (target.inspectOnly && !this.#loading) { this.refresh(); }
    this.#draw();
  }

  // A channel to that machine opened again: one shown only for its questions, with none shown,
  // is asked for them again.
  reopened(hostId) {
    const target = this.#target;
    if (target?.hostId === hostId && target.inspectOnly && !this.#loading && this.#trustQuestions().length === 0) { this.refresh(); }
  }

  #title() {
    const name = this.#target?.machineName;
    const waiting = !this.#target?.inspectOnly || this.#trustQuestions().length > 0;
    this.root.querySelector('.launch-title').textContent = this.#target?.inspectOnly ? (waiting ? `Waiting on ${name}` : `Nothing waiting on ${name}`) : name ? `Start a session on ${name}` : 'Start a session';
  }

  // launch.options again: a workspace approved, an agent installed, a session ended since.
  refresh() {
    const target = this.#target;
    if (!target) { return Promise.resolve(); }
    this.#error = null;
    // The other machines' sessions are added when they arrive: asking each takes a handshake,
    // and one that is slow to answer must not hold up this machine's own choices.
    if (target.transfers) {
      const asking = this.#asking = target.transfers.remotes().catch(() => []).then((remote) => {
        // Only a newer ask, or another machine picked, makes this one stale: a presence update
        // hands in a new target object for the same machine.
        if (this.#asking !== asking) { return; }
        this.#remote = remote;
        this.#draw();
      });
    }
    const asked = ++this.#loads;
    this.#askedAt = Date.now();
    const loading = this.#loading = target.load().then((o) => {
      if (this.#loading !== loading) { return; }
      this.#options = o;
      // Asked for after this machine's last launch outcome: from now on this list says whether
      // the question it met still waits. Only this machine's: another's list says nothing of it.
      const seen = this.#results.get(target.hostId);
      if (seen && asked > seen.loads) { seen.superseded = true; }
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
    if (this.#target?.inspectOnly) {
      const questions = this.#trustQuestions();
      // Kept, with nothing waiting, while it offers other machines: one of them may have one.
      this.root.hidden = questions.length === 0 && e.machineField.hidden;
      this.#title();
      e.form.hidden = true;
      e.reload.hidden = questions.length > 0;
      e.reload.disabled = !!this.#loading;
      e.busy.hidden = true;
      e.note.hidden = false;
      e.note.className = 'note launch-note warn';
      e.note.textContent = `Sessions can't be started on ${name} now${this.#target.readOnly ? `: ${sentence(this.#target.readOnly)}` : '.'}`;
      this.#drawTrust();
      this.#show(this.service.get(this.#target.hostId));
      return;
    }
    if (this.#target) { this.root.hidden = false; }
    e.form.hidden = !o;
    e.reload.hidden = !o && !this.#error;
    e.reload.disabled = !!this.#loading;
    if (!o) {
      e.note.hidden = false;
      e.note.textContent = this.#error ? `Couldn't get the choices from ${name}: ${sentence(this.#error)}` : `Getting the choices from ${name}…`;
      e.note.className = `note launch-note${this.#error ? ' warn' : ''}`;
      e.busy.hidden = true;
      // An answer pressed here stays in view, with its Try again, while the choices load or
      // fail to: the machine still answers a resend with its first outcome.
      this.#drawTrust();
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
    this.#drawTrust();
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
      const selection = this.#selection;
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
      if (this.#selection !== selection) { return; }
    }
    this.#jumped = null;
    this.launched(target.hostId);
    this.service.submit({ hostId: target.hostId, launch });
  }


  #resend() { if (this.#target) { this.service.resend(this.#target.hostId); } }

  // ---- folder trust ----

  // The questions to show: what the machine lists now, the one the last launch met, and any
  // answered from here since, newest first.
  #trustQuestions() {
    const seen = new Map();
    const add = (q) => { if (q && !seen.has(q.id)) { seen.set(q.id, q); } };
    for (const q of this.#options?.trust ?? []) { add(q); }
    const entry = this.#target ? this.service.get(this.#target.hostId) : null;
    // The question a launch's outcome named counts only until a list asked for after it arrives.
    const last = this.#target ? this.#results.get(this.#target.hostId) : null;
    if (entry?.state === 'done' && !(last?.request === entry.request && last.superseded)) { add(entry.detail?.trust); }
    // What the machine says waits now: an answer remembered here for a question it no longer
    // lists is shown for its outcome, but never offered again.
    this.#live = new Set(seen.keys());
    for (const a of this.#answersFor(this.#target?.hostId).values()) { add(a.question); }
    return [...seen.values()];
  }

  #live = new Set();
  // launch.options asked for so far; and by machine, its last launch outcome seen, the count
  // when it came, and whether a list asked for since has superseded it.
  #loads = 0;
  #askedAt = 0;
  // How often a machine shown only for its questions, with none shown, is asked again on updates.
  static recheckMs = 30000;
  #results = new Map();

  #answersFor(hostId) {
    let answers = this.#trustAnswers.get(hostId);
    if (!answers) { answers = new Map(); this.#trustAnswers.set(hostId, answers); }
    return answers;
  }

  #drawTrust() {
    const e = this.els;
    const questions = this.#trustQuestions();
    // Redrawn on every presence update: a button that had focus keeps it, if it is still there.
    const had = e.trust.contains(document.activeElement) ? document.activeElement : null;
    const focus = had ? { question: had.closest('[data-question]')?.dataset.question, button: [...had.classList].find((c) => c.startsWith('trust-')) } : null;
    e.trust.hidden = questions.length === 0;
    e.trust.replaceChildren(...questions.map((q) => this.#trustCard(q)));
    if (focus?.question && focus.button) {
      const again = [...e.trust.querySelectorAll('[data-question]')].find((s) => s.dataset.question === focus.question)?.querySelector(`.${focus.button}`);
      if (again && !again.disabled) { again.focus(); }
    }
  }

  #trustCard(q) {
    const hostId = this.#target?.hostId;
    const answer = this.#answersFor(hostId).get(q.id);
    const name = this.#target?.machineName || 'the machine';
    // Once something may have been typed, it is never offered again from here: the machine then
    // refuses another answer anyway, and a person at the machine finishes it.
    const settled = answer?.state === 'done' && ['started', 'declined', 'unconfirmed'].includes(answer.result?.outcome);
    const pending = answer && ['signing', 'sending'].includes(answer.state);
    // Sent, and its response lost: only that same command again, which the machine answers with
    // its first outcome, whether or not the question can still be answered afresh.
    const retry = answer?.state === 'failed' && !!answer.data;
    const canAnswer = q.answerable && this.#live.has(q.id) && !!this.#target?.trust && !settled && !retry;
    const shown = answer ? describeTrust(answer, q) : null;
    return h('section', { class: 'trust-question', dataset: { question: q.id, trust: answer ? (answer.state === 'done' ? answer.result.outcome : answer.state) : 'waiting' }, 'aria-label': `${q.label} folder trust` },
      h('h3', { class: 'trust-title' }, `${q.label} on ${name} is asking whether to trust a folder`),
      h('p', { class: 'trust-line' }, h('span', { class: 'launch-label' }, 'Folder'), h('code', { class: 'trust-path trust-folder' }, q.folder)),
      q.root ? h('p', { class: 'trust-line' }, h('span', { class: 'trust-root-note' }, q.rootNote ?? 'Trusting applies to the repository root:'), h('code', { class: 'trust-path trust-root' }, q.root)) : null,
      q.warning ? h('p', { class: 'trust-warning' }, q.warning) : null,
      canAnswer
        ? h('div', { class: 'row' },
          h('button', { type: 'button', class: 'button trust-yes', disabled: pending, onclick: () => this.#answerTrust(q, TRUST.trust) }, 'Trust'),
          h('button', { type: 'button', class: 'button secondary trust-no', disabled: pending, onclick: () => this.#answerTrust(q, TRUST.quit) }, 'Quit'))
        : retry
          ? h('div', { class: 'row' }, h('button', { type: 'button', class: 'button secondary trust-resend', onclick: () => this.#sendTrust(hostId, q.id) }, 'Try again'))
          : settled ? null : h('p', { class: 'note warn trust-at-machine' }, this.#options && !this.#live.has(q.id) ? `${name} no longer lists this question as waiting.` : q.atMachine ?? (q.answerable ? `Answer at the machine: this app cannot answer it on ${name}.` : 'Answer at the machine.')),
      shown ? h('p', { class: `answer-status trust-status ${shown.tone}`, role: 'status', 'aria-live': 'polite' }, shown.text) : null);
  }

  // A press: signed once, for this choice; Try again resends that same command and request,
  // which the machine answers with the first outcome rather than typing again.
  async #answerTrust(q, choice) {
    const target = this.#target;
    const answers = this.#answersFor(target?.hostId);
    const known = answers.get(q.id);
    if (!target?.trust || (known && (['signing', 'sending'].includes(known.state) || (known.state === 'failed' && known.data)))) { return; }
    const answer = { hostId: target.hostId, question: q, choice, state: 'signing', data: null, result: null, reason: null, trust: target.trust };
    answers.set(q.id, answer);
    this.#drawTrust();
    try {
      answer.data = { question: q.id, choice, command: await answer.trust.sign(q.id, choice) };
    } catch (e) {
      Object.assign(answer, { state: 'failed', reason: `couldn't sign the answer: ${e?.message ?? String(e)}`, data: null });
      if (this.#target?.hostId === answer.hostId) { this.#drawTrust(); }
      return;
    }
    await this.#sendTrust(answer.hostId, q.id);
  }

  // Sends an answer's signed command, to the machine it was signed for, however the machine shown
  // has changed meanwhile.
  async #sendTrust(hostId, id) {
    const answer = this.#answersFor(hostId).get(id);
    if (!answer?.data || answer.state === 'sending') { return; }
    const trust = (this.#target?.hostId === hostId ? this.#target.trust : null) ?? answer.trust;
    const resend = (answer.sent ?? 0) > 0;
    answer.sent = (answer.sent ?? 0) + 1;
    answer.state = 'sending';
    if (this.#target?.hostId === hostId) { this.#drawTrust(); }
    try {
      answer.result = await trust.answer(answer.data);
      // A resend the machine refuses (the command expired, say) says nothing about the first
      // send, which may have typed: never read as "not answered", which would offer Trust again.
      if (resend && answer.result?.outcome === 'refused') {
        answer.result = { outcome: 'unconfirmed', reason: `Not confirmed: answer at the machine. The first answer may have reached it, and the resend was refused (${answer.result.reason ?? 'no reason given'})` };
      }
      answer.state = 'done';
    } catch (e) {
      answer.state = 'failed';
      answer.reason = e?.reason ?? e?.message ?? String(e);
    }
    if (this.#target?.hostId !== hostId) { return; }
    this.#drawTrust();
    if (answer.state !== 'done') { return; }
    if (answer.result.outcome === 'started') {
      // The launch from here that started the agent asking this question has now started: its
      // first message went with it, as with a launch that starts at once, and must not go again
      // with the next launch. Only that launch, however many busy ones (which send nothing)
      // came after it, and only its message unchanged: what is written since stays.
      const launched = this.#asked.get(hostId);
      if (launched?.question === id) {
        this.#asked.delete(hostId);
        if (this.els.prompt.value === launched.prompt) { this.els.prompt.value = ''; this.#drawCount(); }
      }
      if (answer.result.session) { this.#jumpTo(answer.result.session, hostId); }
    }
    // The machine's list, and whether it is still busy, have changed.
    this.refresh();
  }
  #local(shown) {
    const e = this.els;
    e.status.textContent = shown.text;
    e.status.className = `answer-status launch-status ${shown.tone}`;
    this.root.dataset.launch = 'invalid';
  }

  // A launch outcome for that machine, remembered from when it came: only a list asked for after
  // it says whether the question it named still waits.
  #track(hostId, entry) {
    if (entry?.state === 'done' && hostId && this.#results.get(hostId)?.request !== entry.request) { this.#results.set(hostId, { request: entry.request, loads: this.#loads, superseded: false }); }
    // A launch that started an agent now asking about its folder, by machine: the question, and
    // the first message that went with it. A later busy launch naming it does not replace it.
    if (entry?.state === 'done' && hostId && entry.outcome === 'unconfirmed' && entry.detail?.trust?.id) {
      this.#asked.set(hostId, { question: entry.detail.trust.id, prompt: entry.answer?.prompt ?? '' });
    }
  }

  #asked = new Map();

  #settled(entry) {
    const hostId = entry?.hostId ?? this.#target?.hostId;
    this.#track(hostId, entry);
    this.#show(entry);
    this.#drawTrust();
    if (entry?.state !== 'done') { return; }
    if (entry.outcome === 'started') { this.els.prompt.value = ''; this.#drawCount(); this.#openSession(false); }
    // What it offers has changed: a resumed session is no longer resumable, a worktree counts. A
    // busy that names a folder-trust question may name one that can be answered now, which the
    // choices shown from before may still list as not.
    if (['started', 'unconfirmed'].includes(entry.outcome) || (entry.outcome === 'busy' && entry.detail?.trust)) { this.refresh(); }
  }

  // Jumps to the new session's card, which may arrive (session.upsert) after the outcome.
  #openSession(pressed) {
    const entry = this.#target ? this.service.get(this.#target.hostId) : null;
    const id = entry?.detail?.session;
    if (!id) { return; }
    if (!pressed && this.#jumped === entry.request) { return; }
    this.#jumped = entry.request;
    this.#jumpTo(id, entry.hostId);
  }

  #jumpTo(id, hostId) {
    if (this.jump(id, hostId)) { return; }
    const until = Date.now() + 15000;
    const timer = setInterval(() => {
      if (this.jump(id, hostId) || Date.now() > until) { clearInterval(timer); }
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
