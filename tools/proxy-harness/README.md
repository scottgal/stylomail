# Proxy E2E harness

`run.sh` proves a loopback proxy shape through two client-to-backend protocol legs:

- IMAP authentication and a held client session;
- SMTP `EHLO`, `STARTTLS`, post-TLS authentication, submission, and a held session;
- management-session readback while each client is live; and
- Host process teardown after the proof.

The tracked `driver/` MailKit console project is part of the proof. It authenticates and lists IMAP, then performs SMTP STARTTLS, authenticates, and submits one message. It holds each authenticated client connection long enough for the harness to obtain management-session readback and only emits redacted success or exception-type output.

This is not a production TLS, external-network, semantic-assessment, or byte-counter test. The SMTP listener receives a throwaway self-signed certificate plus private key in the artifact directory. It is harness-only and never deployment material. The harness also generates a one-run CA and an SMTP-backend certificate with `localhost` and `127.0.0.1` SANs, passes the CA using `StyloMail:Proxy:Providers:harness:CaCertificatePath`, and exports the backend certificate/key paths for the local backend fixture. The configured backend host must match one of those SANs. This proves an opt-in custom-root trust path while retaining hostname validation.

No secret is committed. Unless already supplied, the script defaults tenant `harness`, principal `harness-admin`, sender `harness@example.test`, actor `harness`, client login `harness-client@example.test`, and generates fresh client/backend credentials, Host HMAC material, and exact-32-byte credential-protection material per run. Supplied environment values are honoured unchanged. It starts Dovecot and GreenMail Docker backends on assigned loopback ports, builds the tracked MailKit driver, mints the management API key into a mode-0600 temporary curl config, then unsets it. The enrolment body and its temporary directory are mode 0600/0700 and are removed at exit. Credentials are never intentionally written to artifacts, and the script rejects an artifact containing any supplied or generated credential value.

With Docker available, the default proof needs no configuration:

```sh
zsh tools/proxy-harness/run.sh
```

Expected wall time is approximately 30 to 90 seconds on a cold Docker image or package cache, then 10 to 20 seconds warm. It writes `.styloagent/artifacts/proxy-e2e-YYYYMMDDTHHMMSSZ/`, containing `host.log`, `driver-build.log`, `client-transcript.txt`, `sessions-imap-live.json`, `sessions-smtp-live.json`, the disposable listener certificate/key, and the disposable backend CA/certificate/key. The temporary secret directory and Docker containers are deleted at exit. If Docker is unavailable, it fails with that one sentence.

For manual capture, run `PROXY_E2E_KEEP_ALIVE_SECONDS=300 zsh tools/proxy-harness/run.sh`.
Point the console at the printed base URL and use the harness admin key from the printed temporary config location.
Capture the screen, then interrupt the harness or let its bounded timer tear everything down.
