# Security policy

## Reporting a vulnerability

**Please do not open a public issue for a security problem.** A public issue tells everyone at the
same time it tells me, and EVE Console installs update themselves — so there is a window where the
report is public and the fix is not out yet.

Use GitHub's private reporting instead:

**[Report a vulnerability](https://github.com/kernoeve/EveConsole/security/advisories/new)** — or
the *Report a vulnerability* button on the repository's Security tab.

That opens a private advisory only you and I can see. If you would rather not use GitHub, a direct
message to `Kerno` on [Discord](https://discord.gg/H6NaAjJMar) is fine to make first contact — but
please do not put the details in a public channel.

I am one person on a side project, so I cannot promise a response time. I will acknowledge what I
can, and I would rather hear about something small than not hear about it.

## What is in scope

The desktop application and this repository. Most usefully:

- Anything that lets one person's install read or alter another person's data on a shared
  PostgreSQL database
- Credential handling — ESI tokens, the database password, the Slack and Discord integrations
- The update mechanism, since it runs on every installed copy
- Code execution from data the app parses but does not control: the SDE, Hoboleaks, EVE Ref
  archives, killmails, game and chat logs

## What is not

- **CCP's ESI, EVE Online itself, or the EVE SSO.** Report those to
  [CCP Games](https://www.eveonline.com/support), not here.
- **Third-party dependencies**, unless EVE Console's particular use of one is what creates the
  problem. Dependabot watches the dependency list; a known advisory in a package is already visible
  to me.
- **Anything requiring an attacker to already have your machine or your database credentials.** The
  app is a local client and makes no attempt to defend against someone who is already there.

## Supported versions

The latest release only. EVE Console auto-updates by default, and fixes go out in the next release
rather than being backported.
