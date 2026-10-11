# Security policy

Relay starts AI coding agents on your machine, so security reports are taken seriously.
[docs/threat-model.md](docs/threat-model.md) describes what Relay trusts, how each boundary is
enforced, and which risks are known and accepted.

## Reporting a vulnerability

Report it privately. Do not open a public issue or pull request for a suspected vulnerability.

Use GitHub's private reporting form:
[Report a vulnerability](https://github.com/RadicalCaitlin/relay/security/advisories/new)
(the **Security** tab of the repository, then **Report a vulnerability**).

Please include:

- what an attacker can do, and what they need in order to do it (a hostile repository, a hostile
  reply from an agent, a file on your machine, and so on)
- steps to reproduce, with the smallest example you can make
- your operating system, the output of `relay --version` and of `relay agents`
- whether the folder was trusted in Relay, and in the agent's own configuration

Do not include real credentials or private code. Build a harmless example instead: a command that
writes a marker file shows that a command ran just as well as a damaging one.

## What to expect

Relay is maintained by one person in their spare time. You will get an acknowledgement, an
assessment, and a fix or an explanation, as quickly as that allows. There is no bug bounty.

Once a fix is released the report is published as a security advisory, crediting you unless you
would rather not be named.

## Supported versions

Relay is pre-release. Only the latest commit on `main` is supported; fixes are not backported.

## In scope

- Relay running a command, or letting an agent run one, that the user did not ask for and that
  the threat model says cannot happen
- an untrusted folder configuring Claude through Relay
- agent output forging the user's words, Relay's own notices, or another participant's messages
- agent output starting an agent's turn outside an exchange the user opened
- anything that makes Relay print, store or send credentials

## Out of scope

- behaviour the threat model lists under "Remaining risks" as by design, accepted or inherent
- vulnerabilities in Claude Code or Codex themselves; report those to their vendors
- what an agent does within the permissions your own agent configuration grants it
- attacks that need a machine or user account that is already compromised
