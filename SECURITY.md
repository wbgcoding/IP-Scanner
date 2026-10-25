# Security policy

## Supported versions

Only the latest release receives security fixes.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately through GitHub
instead: open the **Security** tab of this repository and choose **Report a vulnerability**.

Helpful details:

- the IP-Scanner version and your Windows version
- what an attacker could do and the steps to reproduce it
- whether the problem involves a scanned network, the local database or a settings file

You get an answer within a few days. Once a fix is released, the report is credited in the
changelog unless you prefer to stay anonymous.

## Scope

IP-Scanner pings the networks you configure and resolves hostnames and MAC addresses on them. It
keeps its settings and its known-devices database next to the exe by default. The update check and
the internet latency ping are the only other network traffic, and both are asked about on first
start. Reports about how it handles scan results, the database, the settings file, network
requests or the update check are all in scope.
