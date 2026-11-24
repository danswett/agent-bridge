// What the sessions count says (#4547). The fleet roster (fleet.list) lists only live sessions, so the
// count must too: a row kept on screen for a machine that left (15 minutes, host-session.js) or whose
// channel dropped can name sessions the host has ended since, and counting it as live made the app say
// 107 where the roster said 68. Such rows stay in the list, read as stale; they are counted apart.
//
// A row is live when its machine can be acted on (staleOf gives null) or is in the grace a return
// holds (grace: true), the same test the Needs you lane uses ("current", views.js).

// entries: [{ hostId }]; staleOf(hostId): null or { reason, grace? } (HostSession.staleOf).
export function countSessions(entries, staleOf) {
  const machines = new Set();
  let live = 0;
  let offline = 0;
  let unconfirmed = 0;
  for (const { hostId } of entries) {
    const stale = staleOf(hostId);
    if (!stale || stale.grace === true) { live += 1; machines.add(hostId); }
    else if (stale.reason === 'offline') { offline += 1; }
    else { unconfirmed += 1; }
  }
  return { live, machines: machines.size, offline, unconfirmed };
}

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

// The line over the list. several: every machine is shown; one: the single machine's name, or null.
// Nothing at all when there is no row of any kind (the empty state speaks then).
export function countLine({ live, machines, offline, unconfirmed }, { several = false, one = null } = {}) {
  if (live + offline + unconfirmed === 0) { return ''; }
  const where = !live ? '' : several ? ` on ${plural(machines, 'machine', 'machines')}` : one ? ` on ${one}` : '';
  return [
    `${live} live${where}`,
    ...(offline ? [`${offline} offline`] : []),
    ...(unconfirmed ? [`${unconfirmed} not confirmed`] : []),
  ].join(' · ');
}
