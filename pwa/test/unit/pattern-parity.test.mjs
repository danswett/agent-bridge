// A form field's pattern is judged by C# in the protocol vectors, but the host reads it as Copilot
// CLI does, with new RegExp(pattern) and no flags (#889). This runs every pattern through Node and
// fails on a verdict that differs from the vector's: a vector refused with "does not match the
// form the question asks for" must fail the match, and any other AnswerRequest vector must pass it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const file = path.join(pwa, '..', 'docs', 'protocol', 'vectors', 'sessions.json');
const REFUSED_FOR_PATTERN = 'does not match the form the question asks for';

// Every field of every context's question that carries constraints.pattern.
function patternFields(contexts) {
  const found = [];
  for (const [context, body] of Object.entries(contexts)) {
    for (const field of body?.question?.fields ?? []) {
      if (typeof field?.constraints?.pattern === 'string') {
        found.push({ context, name: field.name, pattern: field.constraints.pattern });
      }
    }
  }
  return found;
}

// The disagreements between the vectors and new RegExp(pattern).test(value), as readable lines.
function disagreements(vectors, fields) {
  const out = [];
  for (const vector of vectors) {
    for (const field of fields.filter((f) => f.context === vector.context)) {
      const value = vector.json?.fields?.[field.name];
      if (typeof value !== 'string') continue;
      const matches = new RegExp(field.pattern).test(value);
      const reason = typeof vector.reason === 'string' ? vector.reason : '';
      const refusedForIt = reason.startsWith(`"${field.name}": `) && reason.endsWith(REFUSED_FOR_PATTERN);
      // A value refused for another reason on this same field (empty, too long) is stopped by that
      // earlier check, so the pattern was never asked and its mismatch says nothing.
      if (!refusedForIt && reason.startsWith(`"${field.name}"`)) continue;
      if (matches === refusedForIt) {
        out.push(`${vector.name}: ${refusedForIt ? 'refused for the pattern but' : 'not refused for the pattern but'} `
          + `JavaScript ${matches ? 'matches' : 'does not match'}; pattern ${field.pattern}, value ${JSON.stringify(value)}`);
      }
    }
  }
  return out;
}

const vectors = JSON.parse(readFileSync(file, 'utf8'));
const answers = vectors.records.AnswerRequest;
const all = [...answers.valid, ...answers.refused];
const fields = patternFields(vectors.contexts);

test('the vectors have pattern fields and answers to check them with', () => {
  assert.ok(fields.length > 0, 'no field in the contexts has constraints.pattern');
  assert.ok(all.some((v) => fields.some((f) => f.context === v.context && typeof v.json?.fields?.[f.name] === 'string')));
});

test('every pattern in the vectors compiles under new RegExp(pattern) with no flags', () => {
  const walk = (node, where) => {
    if (Array.isArray(node)) node.forEach((n, i) => walk(n, `${where}[${i}]`));
    else if (node && typeof node === 'object') {
      for (const [key, value] of Object.entries(node)) {
        if (key === 'pattern' && typeof value === 'string') {
          assert.doesNotThrow(() => new RegExp(value), `${where}.pattern ${JSON.stringify(value)} does not compile`);
        } else walk(value, `${where}.${key}`);
      }
    }
  };
  walk(vectors, 'sessions.json');
});

test('JavaScript judges each AnswerRequest vector against a field pattern as the vector does', () => {
  assert.deepEqual(disagreements(all, fields), []);
});

test('a disagreement is reported with the vector name, the pattern and the value', () => {
  const field = [{ context: 'c', name: 'ticket', pattern: '^[A-Z]+-[0-9]+$' }];
  const vector = (name, value, reason) => ({ name, context: 'c', json: { fields: { ticket: value } }, reason });
  assert.deepEqual(disagreements([
    vector('ok-valid', 'ABC-12'),
    vector('ok-refused', 'ABC-12x', `"ticket": ${REFUSED_FOR_PATTERN}`),
    vector('ok-other-reason', 'ABC-12', '"mail": must be an email address'),
    vector('stopped-earlier-on-the-same-field', '', '"ticket" needs an answer'),
  ], field), []);
  const wrong = disagreements([
    vector('valid-but-off', 'ABC-12x'),
    vector('refused-but-matches', 'ABC-12', `"ticket": ${REFUSED_FOR_PATTERN}`),
  ], field);
  assert.equal(wrong.length, 2);
  assert.match(wrong[0], /valid-but-off.*\^\[A-Z\]\+-\[0-9\]\+\$.*"ABC-12x"/);
  assert.match(wrong[1], /refused-but-matches.*"ABC-12"/);
});
