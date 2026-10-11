# Contributing to Relay

Thank you for helping. Relay is a small project with one maintainer, so the most useful
contributions are focused ones: a bug report that can be reproduced, or a change that does one thing.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you write code

- **Found a security problem?** Do not open an issue. Follow [SECURITY.md](SECURITY.md).
- **Found a bug?** Open an issue with the steps to reproduce it.
- **Want to add or change something?** Open an issue first and wait for a reply. Relay keeps a
  deliberately narrow scope (see the limitations and roadmap in [README.md](README.md)), and a
  short conversation saves you from writing something that cannot be merged.

## Setting up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Neither Claude Code nor Codex
has to be installed: the tests use fake agents.

```
dotnet build
dotnet test
```

To have Git refuse a commit that contains a secret, install
[gitleaks](https://github.com/gitleaks/gitleaks#installing) and enable the hook once per clone:

```
git config core.hooksPath .githooks
```

## Making a change

[AGENTS.md](AGENTS.md) holds the architecture, coding conventions, testing rules and the security
rules for running processes. It applies to people as much as to coding agents; read it first.

- Keep the change small and within the project boundaries described there.
- The build must have no warnings and the tests must pass.
- Add or update tests for what you changed. A security fix needs a test that fails without it.
- If you change anything covered by [docs/threat-model.md](docs/threat-model.md), update it in the
  same pull request.
- Update `README.md` and `AGENTS.md` when behaviour, commands or architecture change.
- Do not add a package dependency without discussing it in the issue first.

Using a coding agent to help is fine. You are responsible for what you submit: read it, run it,
and say what you verified and what you could not.

## Pull requests

- Branch from `main`, named `<type>/<issue number>-<short-description>`, for example
  `fix/25-atomic-trust-file`.
- Start commit and pull request titles with the kind of change: `feat:`, `fix:`, `docs:`, `test:`
  or `ci:`.
- Link the issue in the description, say what you tested, and say what you did not.
- CI builds and tests on Windows, macOS and Linux and scans the commits for secrets. All of it
  must pass.
- Pull requests are squash-merged, so the title becomes the commit message on `main`.

## Licence

Relay is released under the [MIT License](LICENSE). By contributing you agree that your
contribution is licensed under the same terms.
