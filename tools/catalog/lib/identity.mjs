// identity.mjs - typed native identities for the offline recipe catalog.
//
// Every identity is carried as a string so that signed 32-bit GUID hashes and 64-bit Unity
// pathIDs survive JSON and JavaScript without precision loss. Nothing in this module reads
// the game, the inventory or the recipes; it only knows what a well-formed identity looks like
// and how a query may spell one.
//
// Domains (the schema in ../recipes/SCHEMA.md):
//   prefab-guid     signed int32 decimal, a PrefabGUID hash (gameplay prefab, buff, ability)
//   sequence-guid   signed int32 decimal, a SequenceGUID hash (same hash space as PrefabGUID;
//                   kept distinct because it is resolved through the client-only sequence map)
//   archive         32 lowercase hex characters, a content-archive id (== content-file id)
//   archive-object  <archive>/<pathID>, a SerializedFile object inside one archive
//   unity-name      a Unity object m_Name (a shader name, a particle prefab body name, ...);
//                   never unique on its own, so it is a name, not a key
//
// A runtime Entity index:version is deliberately not a domain: it is evidence from one
// session and is refused by the validator when a recipe tries to use it as an identity.

export const DOMAINS = Object.freeze(['prefab-guid', 'sequence-guid', 'archive', 'archive-object', 'unity-name']);

const INT32_MIN = -2147483648n;
const INT32_MAX = 2147483647n;
const RE_INT = /^-?(0|[1-9][0-9]*)$/;
const RE_HEX32 = /^[0-9a-f]{32}$/;
const RE_ARCHIVE_OBJECT = /^([0-9a-f]{32})\/(-?(?:0|[1-9][0-9]*))$/;

function isInt32String(s) {
  if (typeof s !== 'string' || !RE_INT.test(s)) return false;
  const v = BigInt(s);
  return v >= INT32_MIN && v <= INT32_MAX;
}

function isInt64String(s) {
  if (typeof s !== 'string' || !RE_INT.test(s)) return false;
  const v = BigInt(s);
  return v >= -(1n << 63n) && v <= (1n << 63n) - 1n;
}

/** Returns null when the value is well formed for the domain, else a short reason. */
export function validateIdentityValue(domain, value) {
  if (!DOMAINS.includes(domain)) return `unknown identity domain '${domain}'`;
  if (typeof value !== 'string' || value.length === 0) return 'identity value must be a non-empty string';
  switch (domain) {
    case 'prefab-guid':
    case 'sequence-guid':
      return isInt32String(value) ? null : `${domain} must be a signed 32-bit decimal integer written as a string`;
    case 'archive':
      return RE_HEX32.test(value) ? null : 'archive id must be 32 lowercase hex characters';
    case 'archive-object': {
      const m = RE_ARCHIVE_OBJECT.exec(value);
      if (!m) return 'archive-object must be <32 hex>/<pathID>';
      return isInt64String(m[2]) ? null : 'archive-object pathID must fit a signed 64-bit integer';
    }
    case 'unity-name':
      return value.length <= 512 ? null : 'unity-name is too long';
    default:
      return `unhandled domain '${domain}'`;
  }
}

/** Split an archive-object value into its two halves (both strings). */
export function splitArchiveObject(value) {
  const m = RE_ARCHIVE_OBJECT.exec(value);
  return m ? { archiveId: m[1], pathID: m[2] } : null;
}

/**
 * Parse a query string. Returns one of:
 *   { kind: 'identity', domain, value }         "prefab:-1227952025", "object:<hex>/<id>"
 *   { kind: 'number', value }                    "-1227952025" - any int32 domain may match
 *   { kind: 'text', value }                      anything else
 *   { kind: 'invalid', code, message }
 * Query prefixes: prefab:, sequence:, archive:, object:, name:  (case-insensitive).
 */
export function parseQuery(raw) {
  if (typeof raw !== 'string') return { kind: 'invalid', code: 'invalid-query', message: 'query must be a string' };
  const q = raw.trim();
  if (q.length === 0) return { kind: 'invalid', code: 'invalid-query', message: 'query is empty' };
  if (q.length > 256) return { kind: 'invalid', code: 'invalid-query', message: 'query is longer than 256 characters' };
  const colon = q.indexOf(':');
  if (colon > 0) {
    const prefix = q.slice(0, colon).toLowerCase();
    const value = q.slice(colon + 1).trim();
    const domain = { prefab: 'prefab-guid', sequence: 'sequence-guid', archive: 'archive', object: 'archive-object', name: 'unity-name' }[prefix];
    if (domain) {
      const normalized = domain === 'archive' || domain === 'archive-object' ? value.toLowerCase() : value;
      const problem = validateIdentityValue(domain, normalized);
      if (problem) return { kind: 'invalid', code: 'invalid-identity-value', message: `${prefix}: ${problem}`, domain, value };
      return { kind: 'identity', domain, value: normalized };
    }
    // Unknown "word:" prefixes fall through as text unless they look like an attempted domain.
    if (/^[a-z-]{1,16}$/.test(prefix) && !/\s/.test(prefix) && value.length > 0 && !value.includes(' ')) {
      return { kind: 'invalid', code: 'invalid-identity-domain', message: `unknown identity prefix '${prefix}:'; use prefab:, sequence:, archive:, object: or name:`, prefix };
    }
  }
  if (RE_INT.test(q)) {
    if (!isInt32String(q) && !isInt64String(q)) return { kind: 'invalid', code: 'invalid-identity-value', message: 'number does not fit a signed 64-bit integer' };
    return { kind: 'number', value: q };
  }
  return { kind: 'text', value: q };
}
