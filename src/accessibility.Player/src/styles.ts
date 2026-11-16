export const playerStyles = `
:host {
  --al-accent: #8f1d1d;
  --al-surface: #ffffff;
  --al-surface-muted: #f4f4f2;
  --al-text: #171717;
  --al-border: #676767;
  --al-focus: #005fcc;
  --al-caption-size: 1;
  --al-caption-color: #ffffff;
  --al-caption-background: 0, 0, 0;
  --al-caption-opacity: 0.82;
  display: block;
  color: var(--al-text);
  font-family: Inter, "Noto Sans Ethiopic", "Segoe UI", Arial, sans-serif;
  line-height: 1.45;
  color-scheme: light;
}
*, *::before, *::after { box-sizing: border-box; }
button, input, select { font: inherit; }
button, select, input[type="range"], input[type="search"] { min-height: 2.75rem; }
button, select, a.control {
  border: 1px solid var(--al-border);
  border-radius: 0.4rem;
  background: var(--al-surface);
  color: var(--al-text);
}
button, a.control { cursor: pointer; padding: 0.45rem 0.7rem; text-decoration: none; display: inline-flex; align-items: center; }
button[hidden], [hidden] { display: none !important; }
button:disabled { cursor: not-allowed; opacity: 0.55; }
:focus-visible { outline: 3px solid var(--al-focus); outline-offset: 3px; }
.player { border: 1px solid var(--al-border); border-radius: 0.75rem; overflow: clip; background: var(--al-surface); }
.layout { display: grid; grid-template-columns: minmax(0, 1fr); }
.media-column { min-width: 0; }
.stage { position: relative; aspect-ratio: 16 / 9; background: #090909; overflow: hidden; }
.stage.audio-only { aspect-ratio: auto; min-height: 10rem; display: grid; place-items: center; }
.stage.audio-only::before { content: "♪"; color: #fff; font-size: 4rem; }
.primary-media { width: 100%; height: 100%; display: block; object-fit: contain; }
.caption-overlay {
  position: absolute;
  z-index: 5;
  left: 7%; right: 7%; bottom: 7%;
  text-align: center;
  pointer-events: none;
}
.caption-overlay span {
  display: inline;
  padding: 0.15em 0.35em;
  color: var(--al-caption-color);
  background: rgba(var(--al-caption-background), var(--al-caption-opacity));
  font-size: calc(clamp(1rem, 2.4vw, 1.75rem) * var(--al-caption-size));
  line-height: 1.45;
  box-decoration-break: clone;
  -webkit-box-decoration-break: clone;
}
.sign-window {
  position: absolute;
  z-index: 6;
  width: clamp(9rem, 25%, 18rem);
  aspect-ratio: 4 / 3;
  border: 3px solid #fff;
  border-radius: 0.5rem;
  background: #111;
  box-shadow: 0 0.25rem 1rem #0009;
  touch-action: none;
}
.sign-window[data-position="top-left"] { top: 1rem; left: 1rem; }
.sign-window[data-position="top-right"] { top: 1rem; right: 1rem; }
.sign-window[data-position="bottom-left"] { bottom: 1rem; left: 1rem; }
.sign-window[data-position="bottom-right"] { bottom: 1rem; right: 1rem; }
.sign-window[data-position="custom"] { left: var(--sign-left, 1rem); top: var(--sign-top, 1rem); }
.sign-window[data-size="small"] { width: clamp(7rem, 18%, 12rem); }
.sign-window[data-size="medium"] { width: clamp(9rem, 25%, 18rem); }
.sign-window[data-size="large"] { width: clamp(12rem, 35%, 24rem); }
.sign-window[data-size="custom"] { width: min(var(--sign-width, 18rem), 100%); }
.sign-window[data-maximized="true"] { inset: 0 !important; width: 100% !important; height: 100% !important; aspect-ratio: auto; border-radius: 0; }
.sign-window[data-minimized="true"] { width: auto; height: auto; aspect-ratio: auto; }
.sign-window[data-minimized="true"] .sign-toolbar { position: static; }
.sign-window[data-minimized="true"] .sign-resize { display: none; }
.sign-video { width: 100%; height: 100%; object-fit: contain; display: block; }
.sign-toolbar { position: absolute; inset: auto 0 0; display: flex; align-items: center; gap: 0.25rem; padding: 0.25rem; background: #000d; }
.sign-toolbar span { min-width: 0; flex: 1 1 auto; overflow: hidden; color: #fff; font-size: 0.72rem; text-overflow: ellipsis; white-space: nowrap; }
.sign-toolbar button { min-height: 2rem; flex: 0 1 auto; padding: 0.2rem 0.35rem; color: #fff; background: #222; border-color: #aaa; font-size: 0.75rem; }
.sign-resize { position: absolute; z-index: 2; left: 0; top: 0; min-width: 2.25rem; min-height: 2.25rem; padding: 0.1rem; color: #fff; background: #000d; cursor: nwse-resize; }
.controls { display: flex; align-items: center; flex-wrap: wrap; gap: 0.5rem; padding: 0.65rem; background: var(--al-surface-muted); border-top: 1px solid var(--al-border); }
.controls .seek { flex: 1 1 15rem; }
.controls .volume { width: 6rem; }
.offline-download { display: inline-flex; min-height: 2.75rem; align-items: center; padding: 0.45rem 0.7rem; border: 1px solid var(--al-border); border-radius: 0.4rem; background: var(--al-surface); color: var(--al-text); font-weight: 700; text-decoration: none; }
.time { min-width: 8.5rem; font-variant-numeric: tabular-nums; }
.status { min-height: 1.5rem; padding: 0.35rem 0.75rem; }
.status.error { color: #8b0000; font-weight: 700; }
.loading { display: grid; place-items: center; min-height: 16rem; padding: 2rem; text-align: center; }
.transcript { border-top: 1px solid var(--al-border); padding: 0.8rem; min-width: 0; max-height: 38rem; overflow: auto; }
.transcript header { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; }
.transcript h3 { margin: 0; font-size: 1.15rem; }
.transcript-language { display: grid; gap: 0.3rem; margin-top: 0.65rem; font-weight: 700; }
.transcript-status { margin: 0.5rem 0 0; color: #4a4a4a; font-size: 0.9rem; }
.transcript input { width: 100%; padding: 0.45rem; margin: 0.65rem 0; }
.segments { list-style: none; margin: 0; padding: 0; }
.segment { width: 100%; text-align: left; border: 0; border-bottom: 1px solid #ddd; border-radius: 0; padding: 0.65rem; background: transparent; }
.segment[aria-current="true"] { border-left: 0.35rem solid var(--al-accent); background: #fff2b8; }
.segment-time { display: block; color: #4a4a4a; font-size: 0.8rem; font-variant-numeric: tabular-nums; }
.dialog { width: min(34rem, calc(100% - 2rem)); border: 2px solid var(--al-border); border-radius: 0.65rem; padding: 1rem; }
.dialog::backdrop { background: #0009; }
.settings-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0.8rem; }
.settings-grid label { display: grid; gap: 0.3rem; }
.dialog-actions { display: flex; justify-content: flex-end; margin-top: 1rem; }
.warnings { margin: 0; padding: 0.5rem 2rem; background: #fff2b8; }
.visually-hidden { position: absolute !important; width: 1px !important; height: 1px !important; padding: 0 !important; margin: -1px !important; overflow: hidden !important; clip: rect(0, 0, 0, 0) !important; white-space: nowrap !important; border: 0 !important; }
@media (min-width: 62rem) {
  .layout.has-transcript { grid-template-columns: minmax(0, 2fr) minmax(18rem, 1fr); }
  .layout.has-transcript .transcript { border-top: 0; border-left: 1px solid var(--al-border); }
}
@media (max-width: 38rem) {
  .controls { align-items: stretch; }
  .controls > * { flex: 1 1 auto; }
  .controls .seek { flex-basis: 100%; order: -1; }
  .settings-grid { grid-template-columns: 1fr; }
  .sign-window[data-size="large"] { width: 55%; }
}
@media (forced-colors: active) {
  .player, button, select, .sign-window { border-color: CanvasText; }
  .segment[aria-current="true"] { outline: 3px solid Highlight; }
}
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { scroll-behavior: auto !important; transition: none !important; }
}
`;
