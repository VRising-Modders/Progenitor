/** Wall clock used by poll loops and wait steps. Bridge TCP timeouts always use real timers. */

export function realClock() {
  return {
    now: () => Date.now(),
    sleep: (ms) => new Promise((resolve) => setTimeout(resolve, Math.max(0, Number(ms) || 0))),
  };
}

/** Deterministic clock: sleep advances `now` without waiting. For offline tests. */
export function fakeClock(start = 0) {
  let t = start;
  return {
    now: () => t,
    sleep: async (ms) => { t += Math.max(0, Number(ms) || 0); },
    set: (value) => { t = value; },
  };
}
