# Savanna Upstream Baseline

## Attribution And Distribution Restriction

Source: [alx-africa/Savanna-static-pages](https://github.com/alx-africa/Savanna-static-pages), pinned to [9e83ac77891708117728ad9fc05301e124cc8ec8](https://github.com/alx-africa/Savanna-static-pages/tree/9e83ac77891708117728ad9fc05301e124cc8ec8), commit message `cleanup`, dated 2026-11-05. The upstream Git commit API reports this commit as unsigned.

**Local import is authorized for this project. Publishing or redistributing these captures/assets is not authorized by this import.** No repository-wide license file was identified in the complete pinned Git tree. Obtain and record permission from the relevant rightsholders before redistribution, including review of captured content, branding, images and fonts. Licenses in individual libraries do not license the entire repository. No publishing operation was performed.

The [upstream README at this exact revision](https://github.com/alx-africa/Savanna-static-pages/blob/9e83ac77891708117728ad9fc05301e124cc8ec8/README.md) describes frozen captures from the local development application, not a live Savanna integration. Its Git blob SHA-1 is `d54db460c7eee644c291970334ffbf9b4f60fcff` (4,937 bytes). It lists the limited working controls and concludes: "Everything else is inert." Its claim that pages can be opened with `file://` is not proof of a self-contained offline PWA. This document retains the attribution and relevant behavior inventory; the README and upstream `.gitignore` are not placed in the public web root.

## Pinned Import

Run from the project root with Node.js 22 or newer; no npm package install, Git repository or archive executable is needed:

```powershell
node scripts/import-savanna.mjs --inspect
node scripts/import-savanna.mjs
node scripts/import-savanna.mjs --check
```

`--inspect` downloads and validates the bounded archive but writes nothing. The default imports only missing files into `Savanna-static-pages/index.html`, `pages/` and `assets/`. `--check` is read-only and fails for missing or changed upstream files. Every mode validates the archive SHA-256, tar header checksums, Git archive commit comment and reconstructed Git blob/tree hashes. It does not download arbitrary dependency versions or initialize Git.

An already downloaded archive can be reused without network access:

```powershell
node scripts/import-savanna.mjs --archive "C:\path\to\savanna-9e83ac77891708117728ad9fc05301e124cc8ec8.tar.gz"
node scripts/import-savanna.mjs --check --archive "C:\path\to\savanna-9e83ac77891708117728ad9fc05301e124cc8ec8.tar.gz"
```

The destination is resolved relative to the script, not the shell's working directory. Import preflights all supplied destinations and refuses different existing content, symlinks and non-regular destinations before adding files. New files use exclusive creation. There is no force/reset mode and no cleanup of unrelated content. The old `src/` and `public/` placeholders remain untouched. Future intentional edits such as the concept player mount will make strict `--check` fail; review their diff against this baseline rather than reimporting over them.

| Evidence | Pinned value |
|---|---|
| Archive URL | `https://codeload.github.com/alx-africa/Savanna-static-pages/tar.gz/9e83ac77891708117728ad9fc05301e124cc8ec8` |
| Archive SHA-256 | `c3152e3bb31b2e8cb6d3a64797782b0c1e305f911bb5122700f67ee38f58a2fd` |
| Archive bytes | 6,881,300 |
| Uncompressed tar bytes | 11,325,440 |
| Full upstream Git tree SHA-1 | `4e23b00d814a5ea565fab54facfc57bfc585c3d8` |
| Git archive PAX comment | `9e83ac77891708117728ad9fc05301e124cc8ec8` |
| Full upstream inventory | 167 regular files, 11,186,692 file-content bytes |
| Imported web inventory | 164 regular files, 11,175,571 file-content bytes |
| Imported breakdown | 1 landing page, 3 captures, 9 CSS files, 76 fonts, 72 images, 3 JavaScript files |

The full tree's total size and largest file (1,316,987 bytes) were inspected before fetching the archive on 2026-11-14. The archive endpoint did not advertise `Content-Length`; the download is bounded to 16 MiB while streaming and decompression is bounded to 20 MiB. The script fails closed if GitHub regenerates the archive with different compressed bytes, even if it would contain the same files. Reassess provenance before deliberately updating the pin. These hashes establish reproducibility against the observed source, not a signed publisher attestation or redistribution permission.

The source contains more images than the README's older count of 51. The table above comes from the verified archive, not that description. The importer deliberately excludes the irrelevant macOS `assets/.DS_Store` metadata file; no application asset is dropped or rewritten.

## Baseline Evidence

Run date: 2026-11-14; Windows, Node.js `v24.21.0`. Browser work used the already installed `Chrome/157.0.8103.78` in an isolated headless profile via Chrome DevTools Protocol (CDP). No browser or npm dependency was downloaded. A read-only scratch server served only `Savanna-static-pages` on `http://127.0.0.1:8134`. All off-origin browser requests were configured to fail; this was not an unrestricted connected-browser test. The tested initial views made no external requests, so this does not establish that every captured view works with third-party fonts blocked.

| Check | Actual result |
|---|---|
| Importer and `assets/js/static-page.js` syntax | `node --check` passed for both. |
| Initial bounded archive acquisition | Downloaded 6,881,300 bytes; SHA-256, PAX commit and full Git tree verified. |
| Initial import | 165 files created; no existing files replaced. |
| `--check --archive ...` | Passed: all 165 web files exactly equal the pinned archive. |
| Repeated import with cached archive | Passed: 0 created, 165 unchanged. |
| Safety tests in a separate scratch copy | Passed: unrelated file preservation, conflict preflight before any missing file is created, intentional edit preservation, missing/modified verification failure, invalid/conflicting CLI rejection, corrupt archive rejection, directory junction rejection, no Git initialization. |
| Network `--inspect` reruns | Two attempts reached the 120-second timeout. Later HEAD requests to GitHub API and codeload returned 200. No successful default-network importer rerun is claimed; the cached `--archive` path is verified. |
| Landing page at 1440 x 900 | Loaded, screenshot inspected, all three capture links present with original relative targets. |
| Paced homepage at 1440 x 900 | Loaded, screenshot inspected, original CSS/JS order and no broken loaded local images in that initial viewport. This is not an exhaustive audit of lazy-loaded assets. |
| Paced sidebar | CDP pointer click collapsed 250px to 70px, persisted `sidebar-collapsed=true`, survived reload, and expanded to 250px. |
| Paced courses dropdown | CDP pointer clicks opened and closed the Bootstrap menu with external Popper retained. |
| Paced week panels | DOM `.click()` checks swapped all 16 supplied panels (Course Overview plus Weeks 1-15); active `aria-current` and panel text matched each template. Not a keyboard or pointer-hit-target test of every week. |
| Paced score modal after a panel swap | DOM click opened the Week 10 score-details modal. Escape did not close it in the current harness; target/window focus fidelity was not validated before the run stopped, so this is unresolved, not a proven upstream Escape defect. |
| Initial network errors | The first browser attempt observed 404 for `/favicon.ico` and `/favicon_alx.ico`, neither supplied by this archive. No missing icons were invented or downloaded. |
| Remaining browser baseline | Incomplete. The run stopped before the self-paced page, concept page, mobile widths, subpath hosting, scroll/focus checks and full keyboard interaction checks. |

The courses dropdown also remained open after a CDP Escape attempt while `document.activeElement` was `BODY`. Its nonfocusable DIV trigger is source-confirmed, but Escape routing still needs a correctly focused manual/browser test. The harness failed its modal Escape assertion; this document does not relabel that failed run as a full browser pass. Work was preserved at the integration checkpoint without adding a player mount or patching host behavior.

Local, non-published scratch evidence is retained under `C:\Users\PC\AppData\Local\Temp\kilo\savanna-baseline-evidence\`: `baseline.json` (partial failed-run report), `index-desktop.png` and `homepage-v1-paced-desktop.png`. The scratch helpers are `check-savanna-baseline.mjs`, `serve-savanna-baseline.mjs`, `test-savanna-import.mjs` and `inspect-savanna.mjs` in the parent temp directory. They are not installed project build scripts or a completed test suite. The baseline server and isolated browser were stopped before handoff.

The verified cached archive for this machine is `C:\Users\PC\AppData\Local\Temp\kilo\savanna-9e83ac77891708117728ad9fc05301e124cc8ec8.tar.gz`. Use the documented `--archive` option to avoid redownloading it. A fresh environment still needs network access or that exact, hash-verified archive.

For subsequent baseline checks, serve `Savanna-static-pages` as the static document root, not the project root. If Python 3 is available, its standard-library server is one option (this command was not used in the recorded run):

```powershell
python -m http.server 8134 --bind 127.0.0.1 --directory Savanna-static-pages
```

Open `/index.html` first and follow the numbered judge route. The integrated concept defaults to a local rights-safe sample with reviewed captions and transcript JSON; `/pages/jobs.html` and `/pages/certificate.html?id=ALX-VA1-AMARA-2026` complete the route without requiring the AccessLearn API.

## Supplied Behavior

The following is the upstream README/source inventory, not a claim that every interaction was browser-tested:

| Area | Preserved source behavior |
|---|---|
| Sidebar | `static-page.js:20-62`: toggles body/sidebar classes and stores `sidebar-collapsed`; blocked storage is caught, and viewports below 768px initially collapse. `static-page.css` overrides captured inline label display. |
| Curriculum bar | `static-page.js:64-101`: measures `offsetHeight`, updates `#layout-bars` padding, observes resize, hides after downward window scrolling and shows on upward scrolling. |
| Week strip | `static-page.js:124-217`: preloaded `<template>` panels and matching score modals are cloned, selection state updated, and selected items scrolled into view. This does not fetch live learner data. |
| Self-paced modules | `static-page.js:219-243`: three preloaded activity panels swap and selection styling changes. |
| Programme modal | `static-page.js:103-122`: preloaded course/module bodies toggle `hidden` and `aria-expanded`. It does not call the live programme endpoint. |
| Bootstrap | Existing data-attribute dropdowns, modals and collapses use the supplied bundle; tooltips/popovers are initialized in `static-page.js:245-267`. |
| Other actions | The captured identity, scores, course/activity links and original completion form remain fixtures. The additive player progress, Career Gateway links and certificate lookup are implemented Track 2 behavior. |

The concept's "Mark as complete" is still a native `type="submit"` button inside `<form method="post" action="#">`. It is not an implemented completion API. Activating it can submit to the current static URL, which a normal static server cannot process; "inert" does not mean every control is prevented from causing a hash change or native submission. No real account flow was exercised.

## Additive Mount Contract

The implemented insertion point is inside `#curriculum_navigation_content .savanna-styles`, immediately after that container's first banner paragraph and before the authored `<figure>`. The labelled `.accesslearn-module` does not replace navigation, wrap the remaining lesson, or mount into a `data-react-class` placeholder. Player controls remain outside the later completion form.

`assets/js/accesslearn-savanna.js` attaches `db-player:progress` and `db-player:complete` listeners before loading the SDK, keeps demo progress in local storage, and links directly to `pages/jobs.html`. The fixture loads `demo-accessible-careers` from its same-origin static manifest by default. A deployed host can replace the lesson and API through the existing query/data configuration.

The concept capture preserves its original stylesheet and script order, then adds the scoped `accesslearn-integration.css` and host adapter. Bootstrap, Popper, and the 1.5 MB starter stylesheet are not modified or rebundled.

Despite its filename, the supplied Bootstrap 5.3.3 bundle requires the external `Popper` global: `assets/js/bootstrap.bundle.js:6-10` calls `factory(global.Popper)`. Its library MIT notice remains intact and does not grant rights to the other captured assets.

## Host CSS And Focus

These are evidence-backed requirements for later targeted integration, not changes applied to upstream:

- `application_alx.css:103` sets the root font size to 14px, also measured in the desktop browser. Shadow DOM does not isolate `rem` from the document root. Do not assume a 16px `rem` or change the host's root size. Use deliberate player sizing and test actual zoom/reflow.
- `application_alx.css:34436-34470` gives the navigation menu 350px desktop width, a 200% mobile width, and the flex-growing content column `width: 0%` on desktop / `width: 100%` below 768px. Add scoped `min-width: 0`, responsive/max-width constraints and wrapping for the new section/custom element; do not globally replace the host's flex widths. The existing 200% mobile navigation needs runtime reflow measurement before selecting any narrowly scoped fix.
- `static-page.js:68-100` owns window scrolling and dynamic bar measurement. Preserve its `ResizeObserver` padding rather than copying the captured `style="padding-top: 63px"`. In the paced 1440px baseline the header measured 72px and the bar 63px (bottom 135px), but those are not universal focus offsets.
- `application_alx.css:39827-39886` fixes the curriculum bar below a 72px header (56px top offset at widths <=320px), with sidebar offsets 70px/250px. Its hidden class only sets `transform: translateY(-100%)` and `pointer-events: none`; it does not remove descendants from the tab order or reveal itself on focus. Later integration must measure the visible header/bar, provide scoped scroll margins for new focus targets, and ensure bar focus is never obscured. Check `focusin`/`:focus-within` behavior before adding a targeted reveal treatment; no global keyboard interception or fixed scroll container.
- The concept hamburger and courses trigger are DIVs without a role or tabindex. Runtime inspection of the paced equivalents confirmed `tabIndex=-1`. The source deliberately preserves these defects. A later integrated keyboard journey may require narrowly scoped native-button/label/state fixes while preserving click behavior and appearance; none were applied in this Track 2 slice.
- Keep SDK styles inside Shadow DOM and use AccessLearn-specific light-DOM names. The host already defines `.offline:not(tr) { opacity: 0.5; }` at `application_alx.css:33627`, so a generic `.offline` wrapper would unintentionally dim new UI.

## Outstanding Checks

Still required: full keyboard paths through the preserved Bootstrap defects; NVDA and TalkBack checks; a live published-lesson run against the API; mobile navigation with real player media; real 400% zoom; subpath deployment; and production employer/credential services. Automated Track 2 checks now cover mount order, 200% reflow, speech controls, axe results, structured data and registry verification.

`typekit-xgz4ilr-css.css:21,27,33,39` still lists external `use.typekit.net` WOFF2/WOFF sources before local font fallbacks, and its header contains Adobe-specific terms. Do not equate the supplied local font collection with verified offline independence or unrestricted font redistribution. These captures have no imported service worker/manifest or functional AccessLearn offline package.

No NVDA, TalkBack, real Android, complete keyboard-only journey, 400% zoom, actual offline reload or whole-site accessibility conformance result is claimed. Automated axe checks cover only the new Career Gateway and certificate pages. Compatibility remains with these frozen files, not Savanna's live backend, identity, enrollment or completion APIs. Preserve these gaps in later release evidence.
