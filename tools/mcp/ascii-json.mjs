// JSON text with every non-ASCII character escaped as \uXXXX, the same contract the bridge keeps on
// its wire. A script's stdout is decoded by whoever captures it: Windows PowerShell 5.1 decodes a
// native command's output with the console code page, so raw UTF-8 "·" (C2 B7) reads back as "Â·".
// Escaped JSON is still valid JSON and parses to the same strings under any decoder.

/** JSON.stringify(value, null, indent), then every UTF-16 unit from U+0080 up written as \uXXXX. */
export function asciiJson(value, indent) {
  const text = JSON.stringify(value, null, indent);
  if (text === undefined) return text;
  return text.replace(/[\u0080-￿]/g, (c) => "\\u" + c.charCodeAt(0).toString(16).padStart(4, "0"));
}
