#!/usr/bin/env python3
"""A stand-in for Ollama, so the live measurement path can be exercised without the model.

The live facts in `tests/StyloMail.Nimble.Tests/NimbleLiveMeasurementTests.cs` are the only code that
drives the shipping adapter end to end, and until now the only way to run them was to take the single
local model. That is fine for a measurement and wrong for a check: a defect in the harness itself
(a wrong artifact shape, a lost run, an assertion that throws away the evidence) would only surface
after a forty-call monopoly of a contended resource, and it would surface as a missing artifact rather
than as an error.

So this answers `/api/version` and `/v1/systemone` locally and the harness runs against it in under a
second. It is a stub, not a model: it does not read the prompt. What it does reproduce is the wire
contract, taken from the request the adapter actually sends rather than from documentation. The answer
object is built from the request's own `questions` keys, so a change to the question set shows up here
as a different number of answered keys rather than as a silently smaller answer.

    python3 tools/nimble-survey/fake_ollama.py --port 11599
    NIMBLE_LIVE_ENDPOINT=http://127.0.0.1:11599/v1/systemone \
    NIMBLE_CONTINUITY_RUNS=2 NIMBLE_CONTINUITY_OUT=/tmp/continuity-stub.json \
      dotnet test tests/StyloMail.Nimble.Tests --filter Records_whether_the_continuity

Two routes, because the adapter uses two: the liveness probe on the server root, and the decision
route. `/api/version` is the probe and is deliberately NOT under `/v1/`; it is what the live fact's
skip gate asks for, and a stub that moved it would make the gate the thing under test.

`--fail-at N` makes the Nth decision call return 500, which the adapter turns into an Unavailable row
without retrying (503 is the only retried status). That exercises the failure path deliberately rather
than waiting for it: the measurement must still write its artifact, and must still fail the test.

Nothing here reads, prints or stores a credential. It listens on loopback only.
"""

from __future__ import annotations

import argparse
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CALLS = {"n": 0}

DECISION_PATH = "/v1/systemone"


def build_handler(args):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, fmt, *values):  # noqa: A002 - signature fixed by BaseHTTPRequestHandler
            # Quieter than the default: one line per call, and the call log below is the useful one.
            sys.stderr.write(f"  {self.address_string()} {fmt % values}\n")

        def _read_body(self) -> bytes:
            """Read the request body, chunked or not.

            `JsonContent.Create` sends `Transfer-Encoding: chunked` because it does not know the
            serialised length up front, and a handler that only honours `Content-Length` reads zero
            bytes, answers 400, and then desynchronises the connection so the *next* request line is
            the leftover chunk header. That was this stub's first bug, found by running it rather than
            by reading it.
            """
            if "chunked" not in (self.headers.get("Transfer-Encoding") or "").lower():
                length = int(self.headers.get("Content-Length") or 0)
                return self.rfile.read(length)

            body = bytearray()
            while True:
                size_line = self.rfile.readline().strip()
                if not size_line:
                    continue
                chunk_size = int(size_line.split(b";")[0], 16)
                if chunk_size == 0:
                    self.rfile.readline()  # the trailing CRLF after the final chunk
                    return bytes(body)
                body += self.rfile.read(chunk_size)
                self.rfile.readline()  # CRLF after each chunk

        def _send(self, status: int, payload: dict) -> None:
            body = json.dumps(payload).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):  # noqa: N802 - name fixed by BaseHTTPRequestHandler
            if self.path.split("?")[0] == "/api/version":
                self._send(200, {"version": "0.0.0-fake-ollama"})
            else:
                self._send(404, {"error": f"no route {self.path}"})

        def do_POST(self):  # noqa: N802 - name fixed by BaseHTTPRequestHandler
            if self.path.split("?")[0] != DECISION_PATH:
                self._send(404, {"error": f"no route {self.path}"})
                return

            raw = self._read_body()
            try:
                request = json.loads(raw)
            except json.JSONDecodeError as error:
                self._send(400, {"error": f"unparseable body: {error}"})
                return

            CALLS["n"] += 1
            call = CALLS["n"]
            questions = list((request.get("questions") or {}))
            state = request.get("state") or ""
            # The member names rather than a count of the ones this stub happens to know: the old
            # shape's log line printed `options.num_ctx`, which no longer travels at all, and a line
            # that keeps printing a value the request does not carry is worse than no line.
            sys.stderr.write(
                f"  call {call}: {len(questions)} question(s), state {len(state.encode('utf-8'))} "
                f"bytes, members={sorted(request)}\n"
            )

            if args.fail_at and call == args.fail_at:
                sys.stderr.write(f"  call {call}: refusing with 500 as asked (--fail-at)\n")
                self._send(500, {"error": "stub refused this call"})
                return

            if not questions:
                # A silent empty answer is exactly what this stub exists to make loud.
                self._send(400, {"error": "the request carried no questions to answer"})
                return

            self._send(
                200,
                {
                    "model": request.get("model") or "unknown",
                    "answers": {
                        name: {"type": "noul", "noul": args.noul} for name in questions
                    },
                    "usage": {"input_tokens": args.prompt_tokens, "output_tokens": len(questions)},
                },
            )

    return Handler


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=11599)
    parser.add_argument(
        "--noul", type=float, default=1.0,
        help="the probability every question is answered with; the point is the count, not the value",
    )
    parser.add_argument(
        "--fail-at", type=int, default=0,
        help="return 500 on this 1-based call and no other, to exercise the unavailable path",
    )
    parser.add_argument("--prompt-tokens", type=int, default=100)
    args = parser.parse_args()

    server = ThreadingHTTPServer(("127.0.0.1", args.port), build_handler(args))
    print(f"fake ollama on http://127.0.0.1:{args.port}{DECISION_PATH}  noul={args.noul} "
          f"fail_at={args.fail_at or 'never'}", file=sys.stderr)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        print(f"total decision calls: {CALLS['n']}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
