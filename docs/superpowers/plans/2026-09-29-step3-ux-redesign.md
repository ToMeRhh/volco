# Step 3 — UX Redesign: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A critical, expert redesign of Volco's settings UI and OSD, delivered as a user-approved UX spec and then implemented, changing **only** `src/Volco.App/` and `tests/Volco.App.Tests/`.

**Architecture:** Step 2 left a 1:1 Avalonia port that is a view over `VolcoRuntime`. This step changes the view only. Task 1 is a design task with a hard approval gate; Task 2 turns the approved design into an ordinary implementation plan; Task 3 executes it.

**Tech Stack:** Avalonia 12.1.2, CommunityToolkit.Mvvm, Avalonia.Headless.XUnit.

**Spec:** `docs/superpowers/specs/2026-09-29-volco-avalonia-technical-design.md` §6 "Step 3". Behavioural authority for the OSD: `docs/superpowers/specs/2026-06-07-volume-osd-design.md`.

**Prerequisite:** Step 2 merged. Hand-off surface per the end of `docs/superpowers/plans/2026-09-29-step2-avalonia-cutover.md`.

## Known constraints from Step 2's final review

**Out of Step 3's App-only scope, because they need Core or Windows changes.** List these as "needs engine work" in the UX spec; do not build them in Step 3:
- Following the cursor's monitor for the OSD (needs a new platform seam).
- Any new persisted UX preference (OSD on/off or position, theme choice): `AppConfig` lives in Core and has no UI section. An alternative is an App-owned `ui.json`.
- Native-only UX such as Mica or start-with-Windows.

**The updater is Windows-only.** `UpdateInstaller` writes a `.bat` and the release asset is `Volco.exe`. A future OS would need an `IUpdateApplier` seam. Not for Step 3.

**First-commit candidates for Step 3** (all App or tests only):
- A `CompositionRoot` test that the OSD is subscribed before `runtime.Start()`.
- Replace `PressActionRow`'s fragile `$parent[Border]` cast binding.
- De-static `DeviceRefLookup`, then re-enable test parallelism in `Volco.App.Tests`.
- A test that the `SettingsCard` HeaderIcon is visible.
- The HighContrast palette.
- Distinct `Icon.Volume` and `Icon.Speaker` glyphs.

## Global Constraints

- **The PR may modify only `src/Volco.App/**` and `tests/Volco.App.Tests/**`** (plus the UX spec, plan, checklist and screenshots under `docs/`). This is the test of the decoupling. If a change seems to require touching `Volco.Core` or `Volco.Windows`, **stop and report it to the user** with the reason. That report is a finding, not a failure to hide.
- All Step 2 Global Constraints still apply: no P/Invoke in `Volco.App`, `x:DataType` everywhere, icons from `Icons.axaml`, CI-only verification, `/usr/bin/git`.
- Not in scope: new features that need new runtime or engine capabilities (e.g. per-app level meters, start-with-Windows, cursor-monitor OSD). Task 1 may *propose* them, but lists them separately as "needs engine work" for a future spec.

## Review Focus

1. **A redesigned page drops a setting that existed before,** so a user loses the ability to configure something → Task 3: `SettingsCoverageTests` asserts every `AppConfig` property the Step 2 pages edited is still editable (list derived in Task 1's inventory).
2. **A redesign changes copy or layout so that an existing `config.json` displays differently from what it does** (e.g. a friendly-name-only device no longer shows as selected) → Step 2's `ButtonsPage_FriendlyNameOnlyDevice_IsSelected` and siblings must keep passing unmodified, or be modified only with the user's approval noted in the PR.
3. **Accessibility regressions** (keyboard-only navigation, focus visibility, contrast) → Task 3: tab-order test per page, plus contrast checked against the token table in the UX spec.
4. **OSD redesign steals focus or blocks clicks** → Step 2's `OsdWindow_*` tests keep passing unmodified.
5. **The redesign reaches outside `Volco.App`** → Task 3 final step: `git diff --name-only main... | grep -v '^src/Volco.App/\|^tests/Volco.App.Tests/\|^docs/'` must print nothing.

---

### Task 1: UX audit and redesign proposal (no code)

**Who:** a fresh agent acting as an expert desktop UX reviewer, using the `superpowers:brainstorming` skill with the user. That skill's full process applies: questions, approaches, sectioned approval, written spec.

**Inputs:** the five Step 2 pages and the OSD (read the `.axaml` and ViewModels; the user supplies screenshots of the running app, because the agent cannot run it), `AppConfig.cs`, the three specs listed above, and `firmware/` + `tools/simulator/index.html` for how the hardware is actually used.

**Output:** `docs/superpowers/specs/<date>-volco-ux-redesign.md` containing:
1. **Inventory:** every setting on every page, mapped to the `AppConfig` property it edits. This list feeds Review Focus #1.
2. **Critique:** problems with the current design, each with reasoning grounded in how the device is used. Examples of what to question: is a five-page nav right for about 20 settings; is the slider-mapping flow discoverable; does the OSD carry the right information.
3. **Proposal:** page structure, per-page layout, interaction rules, copy changes, and a token table (colours with contrast ratios, spacing, type). Include annotated mockups; use the brainstorming visual companion if the user accepts it.
4. **"Needs engine work":** proposals that cannot be built in `Volco.App` alone, kept separate and **not** implemented in this step.

- [ ] **Step 1: Run the brainstorming process with the user** to an approved, committed UX spec. **Gate: no code until the user approves the written spec.**

### Task 2: Implementation plan for the approved design

- [ ] **Step 1: Use `superpowers:writing-plans`** on the approved UX spec, producing `docs/superpowers/plans/<date>-volco-ux-redesign-impl.md`. It must include this plan's Global Constraints and Review Focus verbatim, and one task per page plus one for the OSD, each with headless tests. **Gate: the user approves the plan.**

### Task 3: Implement

- [ ] **Step 1: Execute the Task 2 plan.** It must include `SettingsCoverageTests` (Review Focus #1) and per-page tab-order tests (Review Focus #3).
- [ ] **Step 2: Run the scope check:** `git diff --name-only main... | grep -v '^src/Volco.App/\|^tests/Volco.App.Tests/\|^docs/'` prints nothing (Review Focus #5).
- [ ] **Step 3: Windows build** (`gh workflow run build.yml --ref <branch> --field run_build=true`); write `docs/superpowers/checklists/<date>-step3-manual-checklist.md` (every inventory item still configurable and persisted; OSD parity items from Step 2's checklist; keyboard-only walk-through); open the PR. The PR body states the scope-check result explicitly. **Do not merge.**

## Done when

The UX spec and implementation plan are approved; CI is green; the scope check is clean (or the user has explicitly accepted a reported exception); the user has run the checklist and approved the PR.
