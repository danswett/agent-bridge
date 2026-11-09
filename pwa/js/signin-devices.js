// Who can sign Copilot in on a machine (#972 slice 2, ui.md §6.6): read-only, from the machine's
// signin.devices, which it offers only with its "Let my devices sign Copilot in" switch on. The
// names are the roster's, each device's own words, shown as plain text. No Add, no Remove and no
// disabled button: the setting is the Windows app's, and Remove waits for #55's single Remove.
export const SIGN_IN_DEVICES = Object.freeze({ op: 'signin.devices', capability: 'signin.devices' });

const KINDS = new Set(['app', 'pwa', 'phone']);
const MAX_DEVICES = 64;
const SHOWN = 3;
const text = (v) => (typeof v === 'string' ? v : '');

// The machine's answer, read as any host record is (v1-sessions.md, Malformed records): a field of
// the wrong type reads as empty. What it may show is decided by signInDevicesLines, not here.
export function readSignInDevices(r) {
  if (!r || typeof r !== 'object' || Array.isArray(r)) { return null; }
  const devices = (Array.isArray(r.devices) ? r.devices : [])
    .filter((d) => d && typeof d === 'object')
    .slice(0, MAX_DEVICES)
    .map((d) => ({ name: text(d.name), kind: text(d.kind), platform: text(d.platform), thisDevice: d.thisDevice === true }));
  return { state: text(r.state), devices };
}

// The machine row's lines, in Creative's words (#972, 6700914747), or null for none: the line,
// a second one, and every device's name when the line can't hold them all (its tooltip and
// accessible description). Only a roster read lists anyone, and only a person's device with a
// name; a state this client doesn't know is one it can't read.
export function signInDevicesLines(info) {
  if (!info) { return null; }
  const unreadable = { line: "Can't read the device list on this machine.", note: null, all: null };
  if (info.state !== 'ready' && info.state !== 'none') { return unreadable; }
  const devices = info.state === 'ready' ? info.devices.filter((d) => d.name.trim() && KINDS.has(d.kind)) : [];
  if (!devices.length) { return { line: 'No devices can sign Copilot in here yet.', note: 'Adding a device comes in a later update.', all: null }; }
  const names = devices.map((d) => (d.thisDevice ? `${d.name.trim()} (this device)` : d.name.trim()));
  const listed = names.length > SHOWN ? `${names.slice(0, 2).join(', ')} and ${names.length - 2} more` : names.join(', ');
  return { line: `Can sign Copilot in: ${listed}`, note: 'Removing a single device comes later.', all: names.length > SHOWN ? names.join(', ') : null };
}