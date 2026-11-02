# Kerio Cert Manager

**SSL certificates from ManageEngine Key Manager Plus, installed on Kerio Connect — automatically.**
A certificate is renewed in Key Manager Plus, and minutes later it is installed on every Kerio Connect server,
verified over TLS on every port, rolled back if it does not come up, and the administrators get a report by e-mail.

## Tags

| Tag | What it is |
|---|---|
| `latest` | The newest release |
| `1.0` | The newest 1.0.x |
| `1.0.9` | A fixed version — recommended for servers where updates are planned |

## What you need

- Kerio Connect 9.0.2+ (tested on 10.0.6) and a dedicated account with full administration rights.
- Key Manager Plus 7.1.1+ with its REST API key; PostgreSQL 16 (part of the stack); a mailbox with SMTP access.
- Cloudflare Tunnel or your own reverse proxy with TLS — the service publishes no ports to the outside.

## Inside the image

ASP.NET Core 10 API with the React web panel on Ubuntu 24.04 chiseled: no shell and no package manager, runs as a
non-root user, read-only file system, no Linux capabilities. Web panel in English, Ukrainian and Russian; sign-in
with a password and an authenticator app. Every release is tested and scanned for vulnerabilities before
publishing and ships with an SBOM and build provenance.

## License

Proprietary software, all rights reserved. The image is public, but using it requires the owner's permission, and
reverse engineering it is not allowed. The source code is not public. Third-party open-source components inside the
image remain under their own licenses.
