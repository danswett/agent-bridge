const LIMITS = { bytes: 65_536, ageMs: 10 * 60_000, heartbeatMinutes: 10, runningCiMinutes: 60, queuedActions: 5, githubRemainingPercent: 10 };
const MAX_COUNT = 1_000_000;
const LABELS = new Set([
  'Merges delayed', 'Builds or tests failing', 'Fleet capacity reduced',
  'Messaging delayed', 'Updates delayed', 'Usage limit reached',
  'Status data delayed', 'Other',
]);

const unavailable = reason => ({ state: 'unavailable', reason });
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const keys = (value, required, optional = []) => record(value)
  && required.every(key => Object.hasOwn(value, key))
  && Object.keys(value).every(key => required.includes(key) || optional.includes(key));
const integer = (value, max = MAX_COUNT) => Number.isSafeInteger(value) && value >= 0 && value <= max;
const countMap = (value, names) => keys(value, names) && names.every(name => integer(value[name]));
const timestamp = value => typeof value === 'string'
  && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,3})?Z$/.test(value)
  && Number.isFinite(Date.parse(value))
  && new Date(value).toISOString().startsWith(value.slice(0, 19));
const freshness = (value, now) => {
  const age = now - Date.parse(value);
  return age < 0 ? 'future' : age > LIMITS.ageMs ? 'stale' : 'fresh';
};
const observed = value => timestamp(value.observedAt);
const freshGroup = (group, now) => freshness(group.observedAt, now) === 'fresh';
const bands = value => countMap(value, ['low', 'medium', 'high']);

function validIncident(value, now) {
  if (!keys(value, ['state', 'startedAt'], ['resolvedAt', 'blockedMerges', 'label'])
      || !['active', 'resolved'].includes(value.state) || !timestamp(value.startedAt)) { return false; }
  if (Date.parse(value.startedAt) > now) { return false; }
  if ((value.state === 'resolved') !== Object.hasOwn(value, 'resolvedAt')) { return false; }
  if (value.resolvedAt !== undefined && (!timestamp(value.resolvedAt) || Date.parse(value.resolvedAt) < Date.parse(value.startedAt) || Date.parse(value.resolvedAt) > now)) { return false; }
  if (value.blockedMerges !== undefined && typeof value.blockedMerges !== 'boolean') { return false; }
  return value.label === undefined || LABELS.has(value.label);
}

function validSystems(value) {
  if (!record(value) || typeof value.enabled !== 'boolean') { return false; }
  if (!value.enabled) { return keys(value, ['enabled']); }
  const names = ['f2', 'f3', 'f4', 'f5', 'f6', 'f7', 'f8', 'countsProducer', 'peopleProducer', 'telemetry'];
  return keys(value, ['enabled', 'observedAt', ...names]) && observed(value)
    && names.every(name => {
      const row = value[name];
      const states = name === 'f7' ? ['live', 'shadow'] : ['live', 'standby'];
      return keys(row, ['state', 'heartbeatAgeMinutes'])
        && states.includes(row.state) && Number.isFinite(row.heartbeatAgeMinutes)
        && row.heartbeatAgeMinutes >= 0 && row.heartbeatAgeMinutes <= MAX_COUNT;
    });
}

function validPrograms(value) {
  if (!record(value) || typeof value.enabled !== 'boolean') { return false; }
  if (!value.enabled) { return keys(value, ['enabled']); }
  return keys(value, ['enabled', 'observedAt', 'haRetirement', 'fleetView', 'f7'])
    && observed(value)
    && keys(value.haRetirement, ['done', 'total', 'parity'])
    && integer(value.haRetirement.done) && integer(value.haRetirement.total, MAX_COUNT)
    && value.haRetirement.total > 0 && value.haRetirement.done <= value.haRetirement.total
    && ['proved', 'pending'].includes(value.haRetirement.parity)
    && ['ready', 'degraded'].includes(value.fleetView)
    && ['live', 'shadow'].includes(value.f7);
}

function valid(value, now) {
  const groups = ['delivery', 'fleet', 'quota', 'incidents'];
  if (!keys(value, ['schemaVersion', 'generatedAt', ...groups, 'systems', 'programs'])
      || value.schemaVersion !== 1 || !timestamp(value.generatedAt)
      || !validSystems(value.systems) || !validPrograms(value.programs)) { return false; }
  const d = value.delivery, f = value.fleet, q = value.quota, i = value.incidents;
  return keys(d, ['observedAt', 'mergesPerHour', 'minutesSinceLastMerge', 'mainCi', 'actionsQueue', 'pullRequests', 'flakeCount'])
    && observed(d)
    && countMap(d.mergesPerHour, ['agentBridge', 'fleet', 'roadmap'])
    && keys(d.minutesSinceLastMerge, ['agentBridge', 'fleet', 'roadmap'])
    && ['agentBridge', 'fleet', 'roadmap'].every(key => d.minutesSinceLastMerge[key] === null || integer(d.minutesSinceLastMerge[key]))
    && keys(d.mainCi, ['state', 'ageMinutes']) && ['green', 'red', 'running'].includes(d.mainCi.state) && integer(d.mainCi.ageMinutes)
    && countMap(d.actionsQueue, ['queued', 'inProgress'])
    && countMap(d.pullRequests, ['ready', 'armed', 'blocked']) && integer(d.flakeCount)
    && keys(f, ['observedAt', 'liveSessions', 'builders', 'reviewers', 'agentErrors', 'machinesOnline', 'cpuBands', 'memoryBands', 'readyIssues'])
    && observed(f) && integer(f.liveSessions) && countMap(f.builders, ['working', 'idle'])
    && countMap(f.reviewers, ['working', 'idle']) && integer(f.agentErrors) && integer(f.machinesOnline)
    && bands(f.cpuBands) && bands(f.memoryBands) && integer(f.readyIssues)
    && f.cpuBands.low + f.cpuBands.medium + f.cpuBands.high === f.machinesOnline
    && f.memoryBands.low + f.memoryBands.medium + f.memoryBands.high === f.machinesOnline
    && keys(q, ['observedAt', 'githubRemainingPercent', 'modelConcurrency', 'vmLoadBands'])
    && observed(q) && Array.isArray(q.githubRemainingPercent) && q.githubRemainingPercent.length > 0
    && q.githubRemainingPercent.length <= 8 && q.githubRemainingPercent.every(n => integer(n, 100))
    && keys(q.modelConcurrency, ['active', 'cap']) && integer(q.modelConcurrency.active) && integer(q.modelConcurrency.cap, MAX_COUNT)
    && q.modelConcurrency.cap > 0 && bands(q.vmLoadBands)
    && keys(i, ['observedAt', 'items']) && observed(i) && Array.isArray(i.items)
    && i.items.length <= 20 && i.items.every(item => validIncident(item, now));
}

function group(value, now, data) {
  const state = freshness(value.observedAt, now);
  if (state !== 'fresh') { return { state: 'unavailable', counted: true }; }
  return { state: 'fresh', counted: true, data };
}

function banner(groups) {
  const required = ['delivery', 'fleet', 'quota', 'incidents'];
  if ((groups.incidents.state === 'fresh' && groups.incidents.data.items.some(item => item.state === 'active' && item.blockedMerges === true))
      || (groups.fleet.state === 'fresh' && groups.fleet.data.liveSessions > 0 && groups.fleet.data.machinesOnline === 0)) { return 'Outage'; }
  if (required.some(name => groups[name].state !== 'fresh')) { return 'Degraded'; }
  const d = groups.delivery.data, f = groups.fleet.data, q = groups.quota.data, incidents = groups.incidents.data.items;
  const stale = ['systems', 'programs'].some(name => groups[name].counted && groups[name].state !== 'fresh')
    || (groups.systems.state === 'fresh' && Object.values(groups.systems.rows).some(row => row.state === 'unavailable'));
  const degraded = stale || d.mainCi.state === 'red'
    || (groups.programs.state === 'fresh' && groups.programs.data.fleetView === 'degraded')
    || (d.mainCi.state === 'running' && d.mainCi.ageMinutes > LIMITS.runningCiMinutes)
    || d.actionsQueue.queued > LIMITS.queuedActions || d.pullRequests.blocked > 0 || d.flakeCount > 0
    || f.agentErrors > 0 || f.readyIssues > f.builders.working + f.builders.idle
    || f.cpuBands.high > 0 || f.memoryBands.high > 0
    || q.githubRemainingPercent.some(percent => percent < LIMITS.githubRemainingPercent)
    || q.modelConcurrency.active >= q.modelConcurrency.cap || q.vmLoadBands.high > 0
    || incidents.some(item => item.state === 'active');
  return degraded ? 'Degraded' : 'Operational';
}

export function readStatusJson(text, now = Date.now()) {
  if (typeof text !== 'string' || !Number.isFinite(now) || new TextEncoder().encode(text).length > LIMITS.bytes) { return unavailable('invalid'); }
  let value;
  try { value = JSON.parse(text); } catch { return unavailable('invalid'); }
  if (!valid(value, now)) { return unavailable('invalid'); }
  const age = freshness(value.generatedAt, now);
  if (age !== 'fresh') { return unavailable(age); }

  const groups = {};
  for (const name of ['delivery', 'fleet', 'quota', 'incidents']) {
    const data = { ...value[name] };
    delete data.observedAt;
    groups[name] = group(value[name], now, data);
  }
  for (const name of ['systems', 'programs']) {
    const source = value[name];
    if (!source.enabled) { groups[name] = { state: 'unavailable', counted: false }; continue; }
    if (!freshGroup(source, now)) { groups[name] = { state: 'unavailable', counted: true }; continue; }
    if (name === 'systems') {
      const rows = {};
      for (const [key, row] of Object.entries(source)) {
        if (key !== 'enabled' && key !== 'observedAt') {
          const heartbeatAgeMs = row.heartbeatAgeMinutes * 60_000 + (now - Date.parse(source.observedAt));
          rows[key] = heartbeatAgeMs > LIMITS.heartbeatMinutes * 60_000 ? { state: 'unavailable' } : { ...row };
        }
      }
      groups.systems = { state: 'fresh', counted: true, rows };
    } else {
      const data = { ...source };
      delete data.enabled;
      delete data.observedAt;
      groups.programs = { state: 'fresh', counted: true, data };
    }
  }
  return { state: 'available', banner: banner(groups), groups };
}
