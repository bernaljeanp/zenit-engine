# Security policy

Zenit Engine runs a component with administrator rights (the Engine, started at sign-in) and talks to laptop
firmware, so security reports get priority.

## Reporting a vulnerability

Report it privately through this repository's **Security** tab, **Report a vulnerability**. Please don't open a
public issue for security problems.

Include what you found, how to reproduce it, and the Windows version and laptop model you tested on.
You'll get an answer within 7 days. Once a fix is released you will be credited, unless you'd rather not be.

## Scope

In scope: the Engine and its named pipe (`ZenitEngine`), the installer and the scheduled task it creates,
and anything that lets another user or a non-elevated process change hardware state or run code elevated.

Out of scope: vulnerabilities in laptop firmware or vendor drivers themselves; report those to the vendor.

## Supported versions

Only the latest commit on `main` is supported while the project is in early development.
