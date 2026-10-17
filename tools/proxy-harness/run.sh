#!/bin/zsh
# Runs a loopback-only IMAP and SMTP proxy proof. It never prints credential values or API keys.
set -euo pipefail

if [[ -z "${STYLOMAIL_PROXY_LOGIN_KEY:-}" ]]; then
  export STYLOMAIL_PROXY_LOGIN_KEY="$(openssl rand -hex 32)"
fi
if [[ -z "${STYLOMAIL_PROXY_PROTECTION_KEY:-}" ]]; then
  # Hex encoding makes this exactly 32 UTF-8 bytes, suitable for AES-256 key material.
  export STYLOMAIL_PROXY_PROTECTION_KEY="$(openssl rand -hex 16)"
fi
: "${PROXY_E2E_TENANT:=harness}"
: "${PROXY_E2E_PRINCIPAL:=harness-admin}"
: "${PROXY_E2E_SENDER:=harness@example.test}"
: "${PROXY_E2E_ACTOR:=harness}"
: "${PROXY_E2E_HOST_URL:=http://127.0.0.1:5087}"
: "${PROXY_E2E_KEEP_ALIVE_SECONDS:=0}"
: "${PROXY_E2E_NO_TRAFFIC:=0}"
: "${PROXY_E2E_LOGIN:=harness-client@example.test}"
: "${PROXY_E2E_BACKEND_USERNAME:=backend@example.test}"
: "${PROXY_E2E_EMBEDDING_ENDPOINT:=http://127.0.0.1:11440}"
: "${PROXY_E2E_EMBEDDING_TOKEN_FILE:=}"
if [[ "$PROXY_E2E_NO_TRAFFIC" != 1 ]]; then
  [[ -n "$PROXY_E2E_EMBEDDING_TOKEN_FILE" ]] || { print -u2 -- 'PROXY_E2E_EMBEDDING_TOKEN_FILE is required for retrieval element proof.'; exit 1; }
fi
if [[ -z "${PROXY_E2E_CLIENT_PASSWORD:-}" ]]; then
  export PROXY_E2E_CLIENT_PASSWORD="$(openssl rand -hex 16)"
fi
if [[ -z "${PROXY_E2E_BACKEND_SECRET:-}" ]]; then
  export PROXY_E2E_BACKEND_SECRET="$(openssl rand -hex 16)"
fi
export PROXY_E2E_TENANT PROXY_E2E_PRINCIPAL PROXY_E2E_SENDER PROXY_E2E_ACTOR PROXY_E2E_HOST_URL
export PROXY_E2E_LOGIN PROXY_E2E_BACKEND_USERNAME

run_dir="${PROXY_E2E_RUN_DIR:-.styloagent/artifacts/proxy-e2e-$(date -u +%Y%m%dT%H%M%SZ)}"
mkdir -p "$run_dir"
run_dir="$(cd "$run_dir" && pwd)"
find "$PWD/src/StyloMail.Host/bin/Debug/net10.0" -type f -name '*.dll' -print0 | sort -z | \
  xargs -0 shasum -a 256 | shasum -a 256 | awk '{print substr($1,1,12)}' >"$run_dir/host-build.id"
secret_dir=$(mktemp -d "$run_dir/secrets.XXXXXX")
chmod 700 "$secret_dir"
host_log="$run_dir/host.log"
imap_sessions="$run_dir/sessions-imap-live.json"
smtp_sessions="$run_dir/sessions-smtp-live.json"
idle_sessions="$run_dir/sessions-idle.json"
retrieval_analysis="$run_dir/retrieval-analysis.json"
transcript="$run_dir/client-transcript.txt"
certificate="$run_dir/harness-smtp-cert.pem"
certificate_key="$run_dir/harness-smtp-key.pem"
backend_ca="$run_dir/harness-backend-ca.pem"
backend_ca_key="$run_dir/harness-backend-ca-key.pem"
backend_csr="$run_dir/harness-backend-smtp.csr"
backend_certificate="$run_dir/harness-backend-smtp-cert.pem"
backend_key="$run_dir/harness-backend-smtp-key.pem"
backend_extensions="$secret_dir/backend-extensions.cnf"
backend_p12="$run_dir/harness-backend-smtp.p12"
export backend_p12_password="$(openssl rand -hex 16)"
dovecot_container=''
smtp_fixture_container=''

stop_host() {
  if [[ -n "${host_pid:-}" ]] && kill -0 "$host_pid" 2>/dev/null; then
    # The Host is its own session/process group so a dotnet child cannot outlive this harness.
    kill -TERM -- "-$host_pid" 2>/dev/null || true
    wait "$host_pid" 2>/dev/null || true
  fi
  if [[ -n "${host_pid:-}" ]] && { kill -0 -- "-$host_pid" 2>/dev/null || ps -p "$host_pid" -o pid= | grep -q '[0-9]'; }; then
    print -u2 -- "Host process did not terminate cleanly."
    return 1
  fi
  host_pid=''
}

cleanup() {
  local exit_code=$?
  [[ -n "${client_pid:-}" ]] && kill -TERM "$client_pid" 2>/dev/null || true
  [[ -n "${smtp_client_pid:-}" ]] && kill -TERM "$smtp_client_pid" 2>/dev/null || true
  stop_host || exit_code=1
  [[ -n "$dovecot_container" ]] && docker rm -f "$dovecot_container" >/dev/null 2>&1 || true
  [[ -n "$smtp_fixture_container" ]] && docker rm -f "$smtp_fixture_container" >/dev/null 2>&1 || true
  rm -rf "$secret_dir"
  trap - EXIT
  exit "$exit_code"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# These certificates exist only for this disposable loopback run. They are never deployment material.
# The listener PEM includes its PKCS#8 private key because Host loads both from this one path.
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=localhost' \
  -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' \
  -keyout "$certificate_key" -out "$certificate" >/dev/null 2>&1
cat "$certificate_key" >>"$certificate"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=StyloMail Proxy Harness CA' \
  -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' \
  -keyout "$backend_ca_key" -out "$backend_ca" >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -subj '/CN=localhost' \
  -keyout "$backend_key" -out "$backend_csr" >/dev/null 2>&1
print -r -- 'subjectAltName=DNS:localhost,IP:127.0.0.1' >"$backend_extensions"
print -r -- 'extendedKeyUsage=serverAuth' >>"$backend_extensions"
openssl x509 -req -in "$backend_csr" -CA "$backend_ca" -CAkey "$backend_ca_key" -CAcreateserial \
  -days 1 -sha256 -extfile "$backend_extensions" -out "$backend_certificate" >/dev/null 2>&1
rm -f "$backend_csr" "$run_dir/harness-backend-ca.srl"
chmod 600 "$certificate_key" "$certificate" "$backend_ca_key" "$backend_ca" "$backend_certificate" "$backend_key"
openssl pkcs12 -export -out "$backend_p12" -inkey "$backend_key" -in "$backend_certificate" \
  -passout env:backend_p12_password >/dev/null 2>&1
chmod 600 "$backend_p12"

if ! docker info >/dev/null 2>&1; then
  print -u2 -- 'Docker is unavailable.'
  exit 1
fi

# Dovecot's tested rootless image reads USER_PASSWORD from its environment and listens on 31143.
dovecot_config="$secret_dir/99-harness.conf"
cp tests/StyloMail.Integration.Tests/Dovecot/99-harness.conf "$dovecot_config"
export USER_PASSWORD="$PROXY_E2E_BACKEND_SECRET"
dovecot_container=$(docker run -d -e USER_PASSWORD -v "$dovecot_config:/etc/dovecot/conf.d/99-harness.conf:ro" \
  -p 127.0.0.1::31143 dovecot/dovecot:latest)
for attempt in {1..30}; do
  PROXY_E2E_BACKEND_IMAP_PORT=$(docker port "$dovecot_container" 31143/tcp 2>/dev/null | sed -nE 's/.*:([0-9]+)$/\1/p')
  [[ -n "$PROXY_E2E_BACKEND_IMAP_PORT" ]] && break
  sleep 1
done
[[ -n "${PROXY_E2E_BACKEND_IMAP_PORT:-}" ]] || { print -u2 -- 'Dovecot did not publish an IMAP port.'; exit 1; }

# GreenMail ignored its mounted PKCS#12 and presented its default certificate. This minimal fixture
# uses the generated CA-signed PEM directly, while preserving the same implicit-TLS/AUTH/DATA contract.
export SMTP_BACKEND_USER="$PROXY_E2E_BACKEND_USERNAME"
export SMTP_BACKEND_SECRET="$PROXY_E2E_BACKEND_SECRET"
smtp_fixture_container=$(docker run -d -e SMTP_BACKEND_USER -e SMTP_BACKEND_SECRET \
  -v "$backend_certificate:/fixture/cert.pem:ro" -v "$backend_key:/fixture/key.pem:ro" \
  -v "$PWD/tools/proxy-harness/smtp-fixture.py:/fixture/smtp-fixture.py:ro" \
  -p 127.0.0.1::8465 python:3.12-alpine python /fixture/smtp-fixture.py)
sleep 1
docker logs "$smtp_fixture_container" >"$run_dir/smtp-fixture.log" 2>&1 || true
for attempt in {1..30}; do
  PROXY_E2E_BACKEND_SMTP_PORT=$(docker port "$smtp_fixture_container" 8465/tcp 2>/dev/null | sed -nE 's/.*:([0-9]+)$/\1/p')
  [[ -n "$PROXY_E2E_BACKEND_SMTP_PORT" ]] && break
  sleep 1
done
[[ -n "${PROXY_E2E_BACKEND_SMTP_PORT:-}" ]] || { print -u2 -- 'SMTP TLS fixture did not publish a port.'; exit 1; }
openssl s_client -connect "127.0.0.1:$PROXY_E2E_BACKEND_SMTP_PORT" -CAfile "$backend_ca" \
  -verify_return_error -verify_ip 127.0.0.1 -showcerts </dev/null >"$run_dir/backend-certificate.txt" 2>&1
openssl s_client -connect "127.0.0.1:$PROXY_E2E_BACKEND_SMTP_PORT" -CAfile "$backend_ca" \
  -verify_return_error -verify_ip 127.0.0.1 -showcerts </dev/null 2>/dev/null \
  | openssl x509 -noout -issuer -ext subjectAltName >>"$run_dir/backend-certificate.txt"
/usr/bin/grep -q 'Verify return code: 0 (ok)' "$run_dir/backend-certificate.txt" \
  || { print -u2 -- 'SMTP fixture certificate did not validate against the harness CA.'; exit 1; }
/usr/bin/grep -q 'IP Address:127.0.0.1' "$run_dir/backend-certificate.txt" \
  || { print -u2 -- 'SMTP fixture certificate has no loopback IP SAN.'; exit 1; }
/usr/bin/grep -q 'issuer=CN=StyloMail Proxy Harness CA' "$run_dir/backend-certificate.txt" \
  || { print -u2 -- 'SMTP fixture certificate issuer is not the harness CA.'; exit 1; }
export PROXY_E2E_BACKEND_IMAP_HOST=127.0.0.1 PROXY_E2E_BACKEND_IMAP_PORT
export PROXY_E2E_BACKEND_SMTP_HOST=127.0.0.1 PROXY_E2E_BACKEND_SMTP_PORT
export StyloMail__Storage__DatabasePath="$run_dir/host.db"
export StyloMail__Storage__SpoolRoot="$run_dir/spool"
export Logging__LogLevel__Default=Warning

# The key's stdout is captured into a mode-0600 curl config, then removed from the shell variable.
api_key=$(dotnet run --project src/StyloMail.Host --no-build -- key create --principal "$PROXY_E2E_PRINCIPAL" --tenant "$PROXY_E2E_TENANT" \
  --privileges Review,Administer --sender "$PROXY_E2E_SENDER" --by "$PROXY_E2E_ACTOR" | /usr/bin/grep -E '^smk_[0-9a-f]+_' | head -n 1)
print -r -- "header = \"X-StyloMail-Key: $api_key\"" >"$secret_dir/curl.conf"
chmod 600 "$secret_dir/curl.conf"
unset api_key

export StyloMail__Proxy__Enabled=true
export StyloMail__Proxy__Providers__harness__ImapHost="$PROXY_E2E_BACKEND_IMAP_HOST"
export StyloMail__Proxy__Providers__harness__ImapPort="$PROXY_E2E_BACKEND_IMAP_PORT"
export StyloMail__Proxy__Providers__harness__ImapTls=false
export StyloMail__Proxy__Providers__harness__SmtpHost="$PROXY_E2E_BACKEND_SMTP_HOST"
export StyloMail__Proxy__Providers__harness__SmtpPort="$PROXY_E2E_BACKEND_SMTP_PORT"
export StyloMail__Proxy__Providers__harness__SmtpTls=true
export StyloMail__Proxy__Providers__harness__CaCertificatePath="$backend_ca"
export StyloMail__Embeddings__Enabled=true
export StyloMail__Embeddings__Endpoint="$PROXY_E2E_EMBEDDING_ENDPOINT"
export StyloMail__Embeddings__TokenFilePath="$PROXY_E2E_EMBEDDING_TOKEN_FILE"
export PROXY_E2E_BACKEND_SMTP_CERTIFICATE="$backend_certificate"
export PROXY_E2E_BACKEND_SMTP_PRIVATE_KEY="$backend_key"
export StyloMail__Proxy__Listeners__0__ListenerId=imap-1
export StyloMail__Proxy__Listeners__0__TenantId="$PROXY_E2E_TENANT"
export StyloMail__Proxy__Listeners__0__Protocol=Imap
export StyloMail__Proxy__Listeners__0__BindAddress=127.0.0.1
export StyloMail__Proxy__Listeners__0__Port=0
export StyloMail__Proxy__Listeners__0__TlsPolicy=LoopbackHarnessPlaintext
export StyloMail__Proxy__Listeners__0__Enabled=false
export StyloMail__Proxy__Listeners__1__ListenerId=smtp-1
export StyloMail__Proxy__Listeners__1__TenantId="$PROXY_E2E_TENANT"
export StyloMail__Proxy__Listeners__1__Protocol=Smtp
export StyloMail__Proxy__Listeners__1__BindAddress=127.0.0.1
export StyloMail__Proxy__Listeners__1__Port=0
export StyloMail__Proxy__Listeners__1__TlsPolicy=Required
export StyloMail__Proxy__Listeners__1__CertificateReference="$certificate"
export StyloMail__Proxy__Listeners__1__Enabled=false
export StyloMail__Storage__DatabasePath="$run_dir/host.db"
export StyloMail__Storage__SpoolRoot="$run_dir/spool"
export ASPNETCORE_URLS="$PROXY_E2E_HOST_URL"

dotnet build tools/proxy-harness/driver/StyloMail.ProxyHarness.Driver.csproj --nologo >"$run_dir/driver-build.log" 2>&1
# Start the compiled Host directly in a dedicated session. `dotnet run` can leave its child alive
# when the harness shell is interrupted, which strands the listener ports.
python3 -c 'import os, sys; os.setsid(); os.execvp(sys.argv[1], sys.argv[1:])' \
  dotnet "$PWD/src/StyloMail.Host/bin/Debug/net10.0/StyloMail.Host.dll" >"$host_log" 2>&1 &
host_pid=$!
print -r -- "$host_pid" >"$run_dir/host.pid"

# Readiness is the management route, not /health/ready. Bounded retries leave host diagnostics in host.log.
for attempt in {1..30}; do
  if curl -fsS --config "$secret_dir/curl.conf" "$PROXY_E2E_HOST_URL/v1/proxy/listeners" >/dev/null; then
    break
  fi
  [[ "$attempt" == 30 ]] && { print -u2 -- "Host did not become ready; see $host_log"; exit 1; }
  sleep 1
done

# Tenant comes from the API key. The body intentionally carries neither tenantId nor accountId.
python3 - "$secret_dir/enrol.json" <<'PY'
import json, os, sys
with open(sys.argv[1], "w", encoding="utf-8") as stream:
    json.dump({
        "login": os.environ["PROXY_E2E_LOGIN"],
        "clientPassword": os.environ["PROXY_E2E_CLIENT_PASSWORD"],
        "backendAccountId": "harness-backend",
        "providerId": "harness",
        "credentialDiscriminator": "app-password",
        "backendUsername": os.environ["PROXY_E2E_BACKEND_USERNAME"],
        "backendSecret": os.environ["PROXY_E2E_BACKEND_SECRET"]}, stream)
PY
chmod 600 "$secret_dir/enrol.json"
account_status=$(curl -sS --config "$secret_dir/curl.conf" -o "$run_dir/account-response.json" -w '%{http_code}' \
  -X POST -H 'Content-Type: application/json' --data-binary "@$secret_dir/enrol.json" \
  "$PROXY_E2E_HOST_URL/v1/proxy/accounts")
[[ "$account_status" == 201 ]] || { print -u2 -- "Account enrolment returned HTTP $account_status."; exit 1; }

# The retrieval proof needs one real backend message. This stays in the disposable backend and is
# never copied to the artifact directory or transcript.
print -r -- 'From: sender@example.test' >"$secret_dir/retrieval.eml"
print -r -- 'To: backend@example.test' >>"$secret_dir/retrieval.eml"
print -r -- 'Message-ID: <harness-retrieval@example.test>' >>"$secret_dir/retrieval.eml"
print -r -- 'Subject: Retrieval proof' >>"$secret_dir/retrieval.eml"
print -r -- '' >>"$secret_dir/retrieval.eml"
print -r -- 'Please verify your account immediately.' >>"$secret_dir/retrieval.eml"
docker exec -i "$dovecot_container" doveadm save -u "$PROXY_E2E_BACKEND_USERNAME" INBOX <"$secret_dir/retrieval.eml"

listener_json=$(curl -fsS --config "$secret_dir/curl.conf" -X POST "$PROXY_E2E_HOST_URL/v1/proxy/listeners/imap-1/enable")
imap_port=$(print -r -- "$listener_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["bindPort"])')
unset listener_json
curl -fsS --config "$secret_dir/curl.conf" "$PROXY_E2E_HOST_URL/v1/proxy/sessions?scope=live" >"$idle_sessions"
python3 - "$idle_sessions" <<'PY'
import json, sys
if json.load(open(sys.argv[1], encoding="utf-8"))["sessions"]:
    raise SystemExit("Idle listener unexpectedly published a live authenticated session.")
PY
if [[ "$PROXY_E2E_NO_TRAFFIC" != 1 ]]; then
  PROXY_E2E_DRIVER_PROTOCOL=imap PROXY_E2E_DRIVER_HOST=127.0.0.1 PROXY_E2E_DRIVER_PORT="$imap_port" \
    dotnet run --project tools/proxy-harness/driver/StyloMail.ProxyHarness.Driver.csproj --no-build -- --hold >"$transcript" 2>&1 &
  client_pid=$!; sleep 1
  curl -fsS --config "$secret_dir/curl.conf" "$PROXY_E2E_HOST_URL/v1/proxy/sessions?scope=live" >"$imap_sessions"
  # The FETCH worker is intentionally off relay. Poll its durable, content-free rows rather than
  # assuming the driver completion means the background classification has persisted already.
  for attempt in {1..30}; do
    if python3 - "$run_dir/host.db" "$PROXY_E2E_TENANT" "$retrieval_analysis" <<'PY'
import json, sqlite3, sys
database, tenant, artifact = sys.argv[1:]
with sqlite3.connect(database) as connection:
    segments = connection.execute(
        "SELECT availability, availability_reason, segment_count FROM proxy_segment_outcome "
        "WHERE tenant_id=? AND direction='Retrieval' ORDER BY observed_at", (tenant,)).fetchall()
    points = connection.execute(
        "SELECT element, scorer, scorer_version FROM proxy_element_point "
        "WHERE tenant_id=? AND direction='Retrieval' ORDER BY observed_at", (tenant,)).fetchall()
with open(artifact, 'w', encoding='utf-8') as stream:
    json.dump({'direction': 'Retrieval', 'segmentation': [dict(availability=row[0], availabilityReason=row[1], segmentCount=row[2]) for row in segments],
               'elementPointCount': len(points), 'elements': [dict(element=row[0], scorer=row[1], scorerVersion=row[2]) for row in points]}, stream)
if not segments or not points:
    raise SystemExit(1)
PY
    then
      break
    fi
    [[ "$attempt" == 30 ]] && { print -u2 -- 'Retrieval analysis did not persist a Retrieval segmentation outcome and element point.'; exit 1; }
    sleep 1
  done
  if (( PROXY_E2E_KEEP_ALIVE_SECONDS == 0 )); then wait "$client_pid"; fi
fi

smtp_listener_json=$(curl -fsS --config "$secret_dir/curl.conf" -X POST "$PROXY_E2E_HOST_URL/v1/proxy/listeners/smtp-1/enable")
smtp_port=$(print -r -- "$smtp_listener_json" | python3 -c 'import json,sys; print(json.load(sys.stdin)["bindPort"])')
unset smtp_listener_json
if [[ "$PROXY_E2E_NO_TRAFFIC" != 1 ]]; then
  PROXY_E2E_DRIVER_PROTOCOL=smtp PROXY_E2E_DRIVER_HOST=127.0.0.1 PROXY_E2E_DRIVER_PORT="$smtp_port" \
    PROXY_E2E_DRIVER_HARNESS_CERT="$certificate" dotnet run --project tools/proxy-harness/driver/StyloMail.ProxyHarness.Driver.csproj --no-build -- --hold >>"$transcript" 2>&1 &
  smtp_client_pid=$!; sleep 1
  curl -fsS --config "$secret_dir/curl.conf" "$PROXY_E2E_HOST_URL/v1/proxy/sessions?scope=live" >"$smtp_sessions"
  if (( PROXY_E2E_KEEP_ALIVE_SECONDS == 0 )); then wait "$smtp_client_pid"; fi
fi

# Fail closed if any run artifact emitted a supplied or generated secret. Python receives values only via env.
python3 - "$run_dir" "$secret_dir" <<'PY'
import os, sys
secrets = [
    os.environ["STYLOMAIL_PROXY_LOGIN_KEY"],
    os.environ["STYLOMAIL_PROXY_PROTECTION_KEY"],
    os.environ["PROXY_E2E_CLIENT_PASSWORD"],
    os.environ["PROXY_E2E_BACKEND_SECRET"]]
for root, dirs, files in os.walk(sys.argv[1]):
    dirs[:] = [directory for directory in dirs
               if os.path.join(root, directory) != sys.argv[2]]
    for name in files:
        path = os.path.join(root, name)
        text = open(path, encoding="utf-8", errors="replace").read()
        if any(value and value in text for value in secrets):
            raise SystemExit("A harness artifact contained a credential value.")
PY
print -r -- "HARNESS CERTIFICATE ONLY; NOT DEPLOYMENT MATERIAL" >>"$transcript"
print -r -- "IMAP+SMTP PASS artifacts=$run_dir" >>"$transcript"
if [[ "$PROXY_E2E_KEEP_ALIVE_SECONDS" != <-> ]]; then
  print -u2 -- 'PROXY_E2E_KEEP_ALIVE_SECONDS must be a non-negative integer.'
  exit 1
fi
if (( PROXY_E2E_KEEP_ALIVE_SECONDS > 0 )); then
  curl -fsS --config "$secret_dir/curl.conf" "$PROXY_E2E_HOST_URL/v1/proxy/sessions?scope=live" >"$run_dir/sessions-keepalive-live.json"
if [[ "$PROXY_E2E_NO_TRAFFIC" != 1 ]]; then
  python3 - "$run_dir/sessions-keepalive-live.json" <<'PY'
import json, sys
if len(json.load(open(sys.argv[1], encoding='utf-8'))['sessions']) < 2: raise SystemExit('Keep-alive did not retain both live sessions.')
PY
fi
  print -r -- "KEEP-ALIVE base-url=$PROXY_E2E_HOST_URL api-key-config=$secret_dir/curl.conf imap-port=$imap_port smtp-port=$smtp_port seconds=$PROXY_E2E_KEEP_ALIVE_SECONDS"
  # Waiting on a child lets INT/TERM reach this trapping shell immediately; foreground sleep does not.
  sleep "$PROXY_E2E_KEEP_ALIVE_SECONDS" &
  keepalive_timer=$!
  wait "$keepalive_timer"
fi
stop_host
