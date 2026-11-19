# Security Policy

## Supported versions

Only the current release line receives security fixes.

| Version | Supported |
| ------- | --------- |
| 1.3.x   | Yes       |
| < 1.3   | No        |

## Reporting a vulnerability

Please report vulnerabilities privately through GitHub: open the repository's **Security** tab and choose **Report a vulnerability**. Do not open a public issue, pull request, or discussion for a suspected vulnerability.

Include the plugin version, Jellyfin version, affected endpoint or file, and steps to reproduce.

## What to expect

- Acknowledgement within 7 days.
- An initial assessment within 14 days.
- Confirmed issues are fixed in a patch release, followed by a published advisory. Reporters are credited unless they prefer otherwise.

## Scope

In scope:

- The Media Remover plugin: its API endpoints, voting and removal logic, dashboard page, and the voting client injected into Jellyfin Web.
- Its integrations with Radarr, Sonarr, and Jellyseerr / Seerr, including how provider URLs and API keys are stored, used, and exposed.

Out of scope: vulnerabilities in Jellyfin, Radarr, Sonarr, or Seerr themselves. Report those to their own projects.
