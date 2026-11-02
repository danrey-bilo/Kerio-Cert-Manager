# Kerio Cert Manager

**SSL certificates from ManageEngine Key Manager Plus, installed on Kerio Connect — automatically.**
A certificate is renewed in Key Manager Plus, and minutes later it is installed on every Kerio Connect server,
verified over TLS on every port, rolled back if it does not come up, and the administrators get a report by e-mail.

- Source, documentation and releases: [github.com/danrey-bilo/Kerio-Cert-Manager](https://github.com/danrey-bilo/Kerio-Cert-Manager)
- User guide (English and Russian): [docs](https://github.com/danrey-bilo/Kerio-Cert-Manager/tree/main/docs)
- Changelog: [CHANGELOG.md](https://github.com/danrey-bilo/Kerio-Cert-Manager/blob/main/CHANGELOG.md)

## Tags

| Tag | What it is |
|---|---|
| `latest` | The newest release |
| `1.0` | The newest 1.0.x |
| `1.0.7` | A fixed version — recommended for servers where updates are planned |

## Run with Portainer

1. Download `kerio-cert-manager-vX.Y.Z.zip` from [Releases](https://github.com/danrey-bilo/Kerio-Cert-Manager/releases/latest):
   `docker-compose.yml`, `.env.example` with hints and a step-by-step README.
2. Fill in `.env.example`: the master key, two database passwords, the Cloudflare Tunnel token and the first
   administrator (the commands to generate them are in the file).
3. **Stacks → Add stack → Upload** `docker-compose.yml` → **Load variables from .env file** → **Deploy the stack**.
   The image is pulled from here; the `migrate` service prepares the database before the service starts.
4. To update: set a new version in `KCM_IMAGE` → **Update the stack**.

Command-line installation with secrets in files:
[README](https://github.com/danrey-bilo/Kerio-Cert-Manager#installation-from-the-command-line).

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

Free to use. Copying, modifying and redistributing the code is not allowed —
[LICENSE](https://github.com/danrey-bilo/Kerio-Cert-Manager/blob/main/LICENSE).
