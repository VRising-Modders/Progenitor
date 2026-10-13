// Shared printer for the catalog examples. Support lines match
// tools/catalog/recipe.mjs formatSupport / renderInspect (inspect --text).

export function formatSupport(op) {
  const s = op.support ?? {
    classes: [],
    strongest: 'none',
    missing: [],
    agentReportedOnly: false,
    runsNotComplete: [],
    runsComplete: [],
    runsOtherBuild: [],
  };
  if (op.status === 'untested' || op.status === 'not-applicable' || (s.strongest === 'none' && !(op.evidence ?? []).length)) {
    return s.runsNotComplete.length ? 'no evidence; untested; run not complete' : 'no evidence; untested';
  }
  const bits = [];
  if (s.agentReportedOnly) bits.push('agent-reported only');
  else {
    const shown = s.classes.filter((c) => c !== 'missing');
    if (shown.length) bits.push(shown.join(', '));
  }
  bits.push(s.classes.includes('media') ? 'media present' : 'media none');
  if (s.runsNotComplete.length) bits.push('run not complete');
  else if ((s.runsOtherBuild ?? []).length) bits.push('run other build');
  else if ((s.runsComplete ?? []).length) bits.push('run complete');
  else bits.push('run none');
  return bits.join('; ');
}

export function formatOperationLine(key, op) {
  const sides = (op.sides ?? ['server', 'client']).join(',');
  return `${key.padEnd(16)} ${op.status.padEnd(17)} [${formatSupport(op)}] sides=${sides} applicability=${op.applicability}`;
}

export function printRecipeAnswer(look, { supported } = {}) {
  const one = supported
    ?? look.implementation.map((i) => [i.path, i.symbol].filter(Boolean).join(' :: ')).join('; ');
  console.log(`recipe: ${look.recipeId}`);
  console.log(`kind: ${look.kind}`);
  console.log(`supported: ${one}`);
  console.log('initialization:');
  look.requirements.initialization.forEach((step, i) => console.log(`  ${i + 1}. ${step}`));
  console.log('limitations:');
  for (const line of look.limitations) console.log(`  - ${line}`);
  console.log('operations:');
  for (const [key, op] of Object.entries(look.operations)) {
    console.log(`  ${formatOperationLine(key, op)}`);
    if ((op.evidence ?? []).length > 0 && op.support?.agentReportedOnly === true) {
      console.log('    not independently recorded');
    }
  }
  console.log(`currentVerification.claimable=${look.currentVerification.claimable}`);
  console.log(`runtimeAvailability=${look.runtimeAvailability.state}`);
}
