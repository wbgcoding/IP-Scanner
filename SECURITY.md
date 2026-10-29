# Security policy

## Supported versions

Only the latest release receives security fixes.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately through GitHub
instead: open the **Security** tab of this repository and choose **Report a vulnerability**, or go
straight to https://github.com/wbgcoding/IP-Scanner/security/advisories/new.

Helpful details:

- the IP-Scanner version and your Windows version
- what an attacker could do and the steps to reproduce it
- whether the problem needs a device on the scanned network, a local user or a crafted file

You get an answer within a few days. Once a fix is released, the report is credited in the
changelog unless you prefer to stay anonymous.

## Scope

IP-Scanner sends pings, name lookups and port probes only to the targets you enter, checks
GitHub for a newer version and keeps its data next to the exe (`scanner.db`, `.conf`, exports,
`error.log`). Reports about how it parses network answers or imported files, stores or encrypts
its data, or verifies and installs an update are all in scope.
