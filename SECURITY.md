# Security policy

## Reporting a vulnerability or a missed attack

- **Vulnerabilities in PasteGuard itself** (for example a way to leak the API key or make the agent run code): please report privately through GitHub's **Security → Report a vulnerability** on this repository. Don't open a public issue.
- **A ClickFix command that wasn't blocked:** open an issue with the command **defanged**. Replace real domains and IPs with `example.com` / `203.0.113.x` and don't include working payload URLs. Include the score shown in Settings → Try it.

## Design rules

- The agent never executes, opens, or downloads anything it inspects. It only reads clipboard text.
- Only text that passes the local command pre-filter is sent, and only to `api.typesafe.ai`.
- The API key is stored with Windows DPAPI (current user) and is never written to the log.
- Updates are only checked when the user clicks **Check for updates**. The new exe must come from this repository's GitHub release over HTTPS, match the release's SHA-256 file and size, and carry PasteGuard's name and the release's version, or it is deleted and not installed.
- Test and calibration samples are plain text, and every address in them is a reserved domain or IP (RFC 2606 / RFC 5737).
