/* NNA1618 — focus timer v3: pure phase state machine (WORK -> CHILL -> WORK -> ... -> DONE).
 * No DOM, no timers, no localStorage — widget.js owns the clock and persistence; this module only
 * knows how to move from one phase to the next and when the session is finished. UMD so it loads
 * both as a plain <script> in the wallpaper page and via `require`/`import` from the Node test. */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.NNAFocusModel = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  var DEFAULTS = { work: 25, chill: 5, cycles: 4 };

  function clampInt(v, min, max, dflt) {
    v = Number(v);
    if (!isFinite(v)) v = dflt;
    v = Math.round(v);
    return Math.max(min, Math.min(max, v));
  }

  /** Normalizes a partial config against the WORK/CHILL/CYCLES bounds (1-180 / 1-60 / 1-12). */
  function normalizeCfg(cfg) {
    cfg = cfg || {};
    return {
      work: clampInt(cfg.work, 1, 180, DEFAULTS.work),
      chill: clampInt(cfg.chill, 1, 60, DEFAULTS.chill),
      cycles: clampInt(cfg.cycles, 1, 12, DEFAULTS.cycles),
    };
  }

  /** Fresh state: phase 'work', zero work phases completed. */
  function initial(cfg) {
    return { phase: 'work', completedWork: 0, cfg: normalizeCfg(cfg) };
  }

  /** Minutes the current phase should run for ('done' has no duration). */
  function phaseMinutes(state) {
    if (state.phase === 'work') return state.cfg.work;
    if (state.phase === 'chill') return state.cfg.chill;
    return 0;
  }

  /**
   * Moves to the next phase once the current one's timer has run out.
   * work -> chill -> work -> ... until `cycles` work phases have completed, then 'done' (terminal:
   * advancing from 'done' returns the same state unchanged).
   */
  function advance(state) {
    if (state.phase === 'done') return state;
    if (state.phase === 'work') {
      var completedWork = state.completedWork + 1;
      var phase = completedWork >= state.cfg.cycles ? 'done' : 'chill';
      return { phase: phase, completedWork: completedWork, cfg: state.cfg };
    }
    // chill -> work
    return { phase: 'work', completedWork: state.completedWork, cfg: state.cfg };
  }

  /** Applies a new config: keeps completedWork/phase (so a WORK/CHILL edit mid-session doesn't
   * reset progress), but a preset switch should call initial(cfg) instead to start over. */
  function withConfig(state, cfg) {
    return { phase: state.phase, completedWork: state.completedWork, cfg: normalizeCfg(cfg) };
  }

  function isDone(state) { return state.phase === 'done'; }

  return {
    defaults: function () { return { work: DEFAULTS.work, chill: DEFAULTS.chill, cycles: DEFAULTS.cycles }; },
    normalizeCfg: normalizeCfg,
    initial: initial,
    phaseMinutes: phaseMinutes,
    advance: advance,
    withConfig: withConfig,
    isDone: isDone,
  };
});
