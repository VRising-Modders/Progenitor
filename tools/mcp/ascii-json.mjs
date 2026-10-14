// JSON text with every non-ASCII character escaped as \uXXXX, the same contract the bridge keeps on
// its wire. A script's stdout is decoded by whoever captures it: Windows PowerShell 5.1 decodes a
// native command's output with the console code page, so a raw UTF-8 middle dot (C2 B7) reads back as
// two characters (U+00C2 U+00B7). Escaped JSON is still valid JSON and parses to the same strings under any decoder.

const NON_ASCII = /[\u0080-\uffff]/g;
const escapeUnit = (c) => "\\u" + c.charCodeAt(0).toString(16).padStart(4, "0");

/** JSON.stringify(value, null, indent), then every UTF-16 unit from U+0080 up written as \uXXXX. */
export function asciiJson(value, indent) {
  const text = JSON.stringify(value, null, indent);
  if (text === undefined) return text;
  return text.replace(NON_ASCII, escapeUnit);
}

/** A plain-text result with every UTF-16 unit from U+0080 up written as \uXXXX, for the same reason. */
export function asciiText(text) {
  return String(text).replace(NON_ASCII, escapeUnit);
}
