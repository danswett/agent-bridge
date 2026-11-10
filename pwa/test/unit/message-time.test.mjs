import { test } from 'node:test';
import assert from 'node:assert/strict';
import { messageTime, exactTime, nextChange } from '../../js/ui/message-time.js';
import { NO_TIME } from '../../js/history.js';

const LA = { locale: 'en-US', timeZone: 'America/Los_Angeles' };
// Local Pacific wall time to an ISO instant, so each case reads as the viewer sees it.
const NOW = Date.parse('2026-11-09T20:00:00-08:00');
const ago = (ms) => new Date(NOW - ms).toISOString();
const show = (sent, now = NOW, o = LA) => messageTime(sent, now, o);

test('a message under 10 seconds old reads just now', () => {
  assert.equal(show(ago(0)), 'just now');
  assert.equal(show(ago(9000)), 'just now');
});

test('10 to 59 seconds read as seconds ago, and a minute as 1 min ago', () => {
  assert.equal(show(ago(10000)), '10s ago');
  assert.equal(show(ago(59000)), '59s ago');
  assert.equal(show(ago(60000)), '1 min ago');
});

test('minutes round down and a message turns to a clock time at 60 minutes', () => {
  assert.equal(show(ago(3599000)), '59 min ago');
  assert.equal(show(ago(3600000)), '7:00 PM');
});

test('an earlier time the same day is a clock time, another day adds the date', () => {
  assert.equal(show('2026-11-09T09:05:00-08:00'), '9:05 AM');
  assert.equal(show('2026-11-08T19:41:00-08:00'), 'Nov 8, 7:41 PM');
});

test('the relative window wins over the calendar day across midnight', () => {
  const sent = '2026-11-08T23:50:00-08:00';
  assert.equal(show(sent, Date.parse('2026-11-09T00:10:00-08:00')), '20 min ago');
  assert.equal(show(sent, Date.parse('2026-11-09T00:50:00-08:00')), 'Nov 8, 11:50 PM');
});

test('another year adds the year, including Dec 31 seen on Jan 1', () => {
  assert.equal(show('2026-12-31T23:30:00-08:00', Date.parse('2027-01-01T08:00:00-08:00')), 'Dec 31, 2026, 11:30 PM');
  assert.equal(show('2025-11-09T19:41:00-08:00'), 'Nov 9, 2025, 7:41 PM');
});

test('the day is the viewer\u2019s on the DST change days', () => {
  assert.equal(show('2026-03-08T01:30:00-08:00', Date.parse('2026-03-08T12:00:00-07:00')), '1:30 AM');
  assert.equal(show('2026-11-01T01:30:00-07:00', Date.parse('2026-11-01T12:00:00-08:00')), '1:30 AM');
  assert.equal(show('2026-03-07T23:30:00-08:00', Date.parse('2026-03-08T12:00:00-07:00')), 'Mar 7, 11:30 PM');
});

test('a time in the future reads just now, never negative', () => {
  assert.equal(show(ago(-5000)), 'just now');
  assert.equal(show(ago(-30 * 86400000)), 'just now');
});

test('a missing, unreadable or no-time stand-in gives no text', () => {
  for (const v of [undefined, null, '', 'not a time', NO_TIME, 5]) {
    assert.equal(show(v), '');
    assert.equal(exactTime(v, LA), '');
    assert.equal(nextChange(v, NOW), null);
  }
});

test('a 24-hour locale draws the clock time without AM or PM', () => {
  assert.equal(show('2026-11-09T15:05:00-08:00', NOW, { locale: 'de-DE', timeZone: 'America/Los_Angeles' }), '15:05');
});

test('the exact time carries seconds and the zone and does not change with now', () => {
  assert.equal(exactTime('2026-11-09T19:41:07-08:00', LA), 'Mon, Nov 9, 2026, 7:41:07 PM PST');
});

test('the next change is each second under a minute, then the next whole minute, then none', () => {
  assert.equal(nextChange(ago(30000), NOW), 1000);
  assert.equal(nextChange(ago(90000), NOW), 30000);
  assert.equal(nextChange(ago(3600000), NOW), null);
});
