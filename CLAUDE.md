# proj_1: Dynamic Island for Windows

A C#/WPF (.NET 8) Windows app styled after Apple's Dynamic Island. Its code is in `DynamicIsland/`.

## Before working on it

Read **`DynamicIsland/PROJECT.md`** first. It has the architecture, the purpose of each file, key constants,
widget behaviour, design rules, and the build/install commands, so you don't need to re-explore the codebase.
Open only the source files the task needs.

## Keep the docs in sync (required)

In the same change that touches the app, update `DynamicIsland/PROJECT.md`:
- the file table, if you add, remove, or rename files;
- the affected section, if behaviour, constants, settings, or build/install steps change;
- one line under **Change log**.

For user-visible changes, also update the root `README.md`, and re-render `docs/screenshots/` if the visuals
changed (command in PROJECT.md). Update this file only if the high-level instructions here change.

## Workflow

- The user gives features one step at a time. Build only what's asked; ask before changing scope.
- After each change: `dotnet build`, then reinstall with `dotnet publish` into `%LOCALAPPDATA%\Programs\DynamicIsland`
  and relaunch (exact commands in PROJECT.md).
- Motion and look follow the project skills in `.claude/skills` (apple-design, animate, emil-design-eng, …),
  translated from web to WPF. Avoid wobble or shake on open/close; the user dislikes it.
