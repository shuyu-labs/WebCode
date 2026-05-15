# AGENTS.md

This repository allows autonomous coding agents to make scoped code and documentation changes, but the following rules are hard constraints and must not be bypassed.

## Secret Safety

- Never commit API keys, access tokens, passwords, private keys, cookies, session secrets, provider auth files, or any other real credential material to this repository.
- Never push any commit containing credential material to any remote.
- Treat anything that looks like a live secret as compromised until proven otherwise.
- Stop and remove the secret from the index before any commit if a staged file contains suspected credential material.
- Test fixtures may use obvious dummy placeholders, but they must remain clearly fake and non-functional.

## Local Config Hygiene

- Repository-local agent or tool state under `/.codex/` must remain untracked.
- Do not use repository commits to persist machine-local, user-local, or provider-auth state.

## Engineering Notes

- Agent implementation notes live under `/docs/agent-notes/`.
- Add notable implementation findings to a daily Markdown file named `YYYY-MM-DD.md`.
- Update the current day's note when you solve a bug, confirm a non-obvious platform constraint, or discover a working reference implementation worth reusing.
- Before changing behavior for a bugfix, Feishu card feature, or other non-trivial implementation detail, search `/docs/agent-notes/` for relevant prior notes and reuse confirmed patterns where possible.
