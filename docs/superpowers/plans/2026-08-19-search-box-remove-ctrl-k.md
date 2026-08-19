# 话术库搜索框移除 Ctrl+K Implementation Plan

> **For agentic workers:** Execute the steps in order. This change is intentionally limited to the formal WPF search box and its regression contract.

**Goal:** Remove the library search box's `Ctrl+K` shortcut and visual badge without changing search behavior or other shortcuts.

**Architecture:** Keep the existing WPF `SearchBoxStyle` and `LibraryView` binding/search flow. Remove only the `KeyBinding` and the template badge, and reduce the right padding that existed for the badge. Add a source-level architecture regression test because the behavior is declared in XAML and there is no existing WPF visual test harness for this control.

**Tech Stack:** .NET 10, pure WPF/XAML, xUnit architecture tests.

---

### Task 1: Add the failing regression contract

**Files:**
- Modify: `tests/QuickPhrase.Architecture.Tests/ArchitectureTests.cs`

- [ ] Add a test that reads `desktop/QuickPhrase.Desktop/Views/LibraryView.xaml` and `desktop/QuickPhrase.Desktop/Themes/Controls.xaml` and asserts neither contains a `Ctrl+K`/`Ctrl K` search shortcut declaration or badge text.
- [ ] Run the focused architecture test and confirm it fails against the current XAML because the existing `KeyBinding` and badge are still present.

### Task 2: Remove the shortcut and badge

**Files:**
- Modify: `desktop/QuickPhrase.Desktop/Views/LibraryView.xaml:295-311`
- Modify: `desktop/QuickPhrase.Desktop/Themes/Controls.xaml:171-205`
- Modify: `desktop/QuickPhrase.Desktop/Views/LibraryView.xaml.cs:11-15`

- [ ] Update comments to describe the search footer without `Ctrl+K`.
- [ ] Remove the `TextBox.InputBindings` block containing the `Ctrl+K` binding.
- [ ] Change `SearchBoxStyle` padding from `28,0,72,0` to `28,0,16,0`.
- [ ] Remove the right-side `Ctrl K` border and text block from the control template.
- [ ] Keep the search `Text` binding, `SearchBox_KeyDown`, search command, and other list keyboard behavior unchanged.

### Task 3: Verify

- [ ] Run the focused regression test and confirm it passes.
- [ ] Run the full architecture test project.
- [ ] Run `dotnet build QuickPhrase.sln --no-restore`.
- [ ] Review the diff and confirm only the approved files changed, apart from the design/plan records.
