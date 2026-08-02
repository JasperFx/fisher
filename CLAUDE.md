# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

This repository is a green field. As of the initial commit it contains only `README.md`, `LICENSE`,
and a Visual Studio `.gitignore` — there is no source code, build script, or test suite yet.

**When real code lands, replace this file** with the actual build/test commands and an architecture
overview. Until then, do not assume any of the structure below exists — verify before acting.

## What is known

- **Fisher** is a SQLite-backed document and event store within the Critter Stack
  (the Marten / Wolverine / Weasel family of tools from JasperFx).
- The `.gitignore` is the standard Visual Studio one, so this is expected to be a .NET codebase.
- MIT licensed, copyright "The Critter Stack" and JasperFx Tools.

## Related repositories

Sibling Critter Stack repositories live under `/Users/jeremymiller/code` (`marten`, `wolverine`,
`weasel`, `jasperfx`, and their worktree directories). They are the reference for prior art on
document/event store design and for the conventions this project is likely to follow — consult them
directly rather than guessing.
