import test from 'node:test';
import assert from 'node:assert/strict';
import { parseQuery, validateIdentityValue, splitArchiveObject, DOMAINS } from '../lib/identity.mjs';

test('typed identity queries parse into their domain with the value kept as a string', () => {
  assert.deepEqual(parseQuery('prefab:-1227952025'), { kind: 'identity', domain: 'prefab-guid', value: '-1227952025' });
  assert.deepEqual(parseQuery('sequence:-40367522'), { kind: 'identity', domain: 'sequence-guid', value: '-40367522' });
  assert.deepEqual(parseQuery('OBJECT:80339BB4CB0AF4F5585B6812D20AB056/9142015999717103345'),
    { kind: 'identity', domain: 'archive-object', value: '80339bb4cb0af4f5585b6812d20ab056/9142015999717103345' });
  assert.deepEqual(parseQuery('name:Part_Charm_Loop_01'), { kind: 'identity', domain: 'unity-name', value: 'Part_Charm_Loop_01' });
});

test('a bare signed number is a number query, free text is text, empty is invalid', () => {
  assert.deepEqual(parseQuery(' -1227952025 '), { kind: 'number', value: '-1227952025' });
  assert.deepEqual(parseQuery('Wendigo'), { kind: 'text', value: 'Wendigo' });
  assert.equal(parseQuery('').kind, 'invalid');
  assert.equal(parseQuery('   ').code, 'invalid-query');
  assert.equal(parseQuery('x'.repeat(300)).code, 'invalid-query');
  assert.equal(parseQuery(42).code, 'invalid-query');
});

test('unknown prefixes and malformed identity values are structured errors', () => {
  const bad = parseQuery('bogus:1');
  assert.equal(bad.kind, 'invalid');
  assert.equal(bad.code, 'invalid-identity-domain');
  assert.equal(parseQuery('prefab:99999999999').code, 'invalid-identity-value');
  assert.equal(parseQuery('prefab:abc').code, 'invalid-identity-value');
  assert.equal(parseQuery('object:not-hex/1').code, 'invalid-identity-value');
  assert.equal(parseQuery('archive:80339bb4').code, 'invalid-identity-value');
  // A colon inside ordinary prose is still text.
  assert.equal(parseQuery('nova: what it does').kind, 'text');
});

test('int32 domains reject out-of-range and non-integer values, accept the extremes', () => {
  for (const d of ['prefab-guid', 'sequence-guid']) {
    assert.equal(validateIdentityValue(d, '-2147483648'), null);
    assert.equal(validateIdentityValue(d, '2147483647'), null);
    assert.notEqual(validateIdentityValue(d, '2147483648'), null);
    assert.notEqual(validateIdentityValue(d, '-2147483649'), null);
    assert.notEqual(validateIdentityValue(d, '1.0'), null);
    assert.notEqual(validateIdentityValue(d, '01'), null);
    assert.notEqual(validateIdentityValue(d, -1), null);
  }
  assert.notEqual(validateIdentityValue('entity', '1:2'), null);
  assert.equal(DOMAINS.includes('entity'), false);
});

test('64-bit pathIDs survive parse, split and JSON without precision loss', () => {
  const big = '9223372036854775807';
  const beyondDouble = '9007199254740993'; // 2^53 + 1, not representable as a JS number
  for (const id of [big, beyondDouble, '-9223372036854775808', '9142015999717103345']) {
    const value = `80339bb4cb0af4f5585b6812d20ab056/${id}`;
    assert.equal(validateIdentityValue('archive-object', value), null, id);
    const parsed = parseQuery(`object:${value}`);
    assert.equal(parsed.value, value);
    assert.equal(splitArchiveObject(parsed.value).pathID, id);
    assert.equal(JSON.parse(JSON.stringify(parsed)).value, value);
  }
  assert.notEqual(validateIdentityValue('archive-object', '80339bb4cb0af4f5585b6812d20ab056/9223372036854775808'), null);
  assert.equal(String(Number(beyondDouble)) === beyondDouble, false, 'the test would be vacuous if a double could hold this value');
});
