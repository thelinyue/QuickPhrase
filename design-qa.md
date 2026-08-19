# QuickPhrase 视觉与交互 QA

## 证据

- Source visual truth: `C:\Users\林樾\.codex\generated_images\01a00867-8c81-7ec3-9533-b112468eaea5\exec-c3604c95-894a-441a-af5d-4ce8b8ab7474.png`
- Source pixels: 1488 × 1056 generated mock; normalized to the 1440 × 1024 desktop CSS target for comparison.
- Implementation screenshots: `qa-artifacts/library-1440.png`, `qa-artifacts/launcher-1440.png`, `qa-artifacts/launcher-multi-1440.png`, `qa-artifacts/launcher-unsupported-1440.png`, `qa-artifacts/onboarding-1440.png`, `qa-artifacts/editor-1440.png`, `qa-artifacts/settings-1440.png`, `qa-artifacts/settings-conflict-1440.png`, `qa-artifacts/library-1200.png`, `qa-artifacts/library-1024.png`
- Combined comparison: `qa-artifacts/launcher-comparison.png`
- Browser: Playwright Chromium, deviceScaleFactor 1, viewport 1440 × 1024; responsive checks at 1200 × 760 and 1024 × 768.
- Primary states: library default, standalone Launcher over enterprise-chat context with `hfcc`, multi-result `hf`, zero-result `abcxyz`, unsupported target fallback, editor form, settings conflict, first-run onboarding, and Launcher preview open.

## Comparison

Full-view comparison confirms the selected Fluent/Floating visual language is retained: pale blue wallpaper, translucent white surfaces, restrained lavender accent, compact Fluent iconography, and low-noise typography.

Focused Launcher comparison confirms the product-architecture correction from the previous concept board: Launcher is now a standalone floating scene over an enterprise chat window, includes the explicit `企业微信 · 张先生` insertion target, uses a 2px accent indicator instead of a heavy blue border, and keeps the result anatomy to title, preview, metadata, shortcut, and favorite.

The main window now uses category-first navigation, an approximately 1200 × 760 three-column layout, a real phrase list, and a read-only preview by default. The editor appears only as its own scene. `Alt + Space` opens the independent Launcher and `Ctrl + K` focuses the main-window search.

V1 product boundary is explicit: QuickPhrase stores fixed standard replies and information-collection prompts; it does not store customer data or expose template variables. Example prompts now cover serial number, fault time, software version, screenshots, logs, network state, and reproduction steps.

## Required fidelity surfaces

- Fonts and typography: Segoe UI Variable / Segoe UI fallback, readable 10–22px hierarchy, compact keyboard labels, no clipped Chinese copy in the tested viewports.
- Spacing and layout rhythm: 8px-oriented spacing, 8–15px radii, lightweight row separators, centered library window, independent Launcher proportions, and no horizontal overflow at 1200/1024 widths.
- Colors and visual tokens: pale blue-grey base, translucent white panels, lavender focus states, yellow favorites, green connection/status indicators, and restrained elevation.
- Image quality and asset fidelity: the generated Windows-style folded-ribbon wallpaper is used as a real raster asset at `public/assets/quickphrase-wallpaper.png`; icons use `@fluentui/react-icons` rather than handcrafted SVG or CSS drawings.
- Copy and content: real Chinese phrase titles, bodies, tags, categories, keyboard instructions, chat recipient, app adapter states, and settings labels are rendered.

## Interaction evidence

- `Alt + Space`: opens and closes the standalone Launcher; the shortcut can be paused from the tray menu.
- `Ctrl + K`: focuses only the main-window search and does not open the Launcher.
- Launcher `hfcc`: resolves to `恢复出厂设置`; Arrow keys change selection; Tab toggles preview; Enter inserts; Ctrl + Enter inserts and sends; Esc returns to the previous scene.
- Library: category-first sidebar, favorites, recent view, search, selected phrase preview, favorite toggle, new category form, and new phrase entry point.
- Editor: title/body/category/tags/optional shortcut mode (none, Alt+1–9, custom), duplicate-slot validation, send-mode guidance, save feedback, cancel, and delete confirmation.
- Launcher boundary states: `hf` renders six results with internal scrolling and dynamic height; `abcxyz` keeps the query and offers a new phrase entry; unsupported targets show clipboard fallback and `Ctrl + V` guidance.
- Conflict and onboarding: global `Alt + Space` conflict warning can be resolved; the first-run “试一下” action opens the sample Launcher directly.
- Settings: general, shortcut, send-behavior, and application-adapter states; toggles and completion feedback work.
- Tray: quick search, new phrase, pause/resume shortcuts, settings, and exit menu are clickable.
- Browser console: no errors or page exceptions were reported.

## Comparison history

1. Initial concept-board implementation showed the Launcher inside the main library and opened the editor by default.
2. Product feedback required a category-first library, read-only preview by default, an independent Launcher over a chat context, explicit insertion target, lighter selected state, and separate `Alt + Space` / `Ctrl + K` semantics.
3. V1 refinement added the larger management window, dynamic Launcher height/internal scrolling, unsupported-target fallback, hotkey conflict, first-run onboarding, and optional shortcut uniqueness rules.
4. Product boundary was simplified by removing template variables entirely; fixed information-collection prompts are now the canonical support workflow.
5. The current implementation was re-captured at matching desktop and responsive viewports; no actionable P0/P1/P2 visual findings remain.

## Verification commands

- `npm run build` — passed.
- `npm run test:sites` — 4 tests passed.
- `node scripts/qa.mjs` — passed; console errors 0.

final result: passed
