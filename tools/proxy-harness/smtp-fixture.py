import base64
import os
import ssl
import socketserver

EXPECTED_USER = os.environ["SMTP_BACKEND_USER"]
EXPECTED_SECRET = os.environ["SMTP_BACKEND_SECRET"]


class Handler(socketserver.StreamRequestHandler):
    def send(self, line):
        self.wfile.write((line + "\r\n").encode("ascii"))
        self.wfile.flush()

    def handle(self):
        self.send("220 harness SMTP TLS backend")
        authenticated = False
        data_mode = False
        for raw in self.rfile:
            line = raw.decode("utf-8", "replace").rstrip("\r\n")
            if data_mode:
                if line == ".":
                    data_mode = False
                    self.send("250 queued")
                continue
            verb, _, args = line.partition(" ")
            verb = verb.upper()
            if verb == "EHLO":
                self.send("250-harness")
                self.send("250-AUTH PLAIN LOGIN")
                self.send("250 SIZE 1048576")
            elif verb == "AUTH" and args.upper().startswith("PLAIN "):
                try:
                    _, user, secret = base64.b64decode(args.split(" ", 1)[1]).decode("utf-8").split("\0")
                    authenticated = user == EXPECTED_USER and secret == EXPECTED_SECRET
                except Exception:
                    authenticated = False
                self.send("235 Authentication successful" if authenticated else "535 Authentication failed")
            elif verb == "AUTH" and args.upper() == "LOGIN":
                self.send("334 VXNlcm5hbWU6")
                user = base64.b64decode(self.rfile.readline().strip()).decode("utf-8")
                self.send("334 UGFzc3dvcmQ6")
                secret = base64.b64decode(self.rfile.readline().strip()).decode("utf-8")
                authenticated = user == EXPECTED_USER and secret == EXPECTED_SECRET
                self.send("235 Authentication successful" if authenticated else "535 Authentication failed")
            elif verb in ("MAIL", "RCPT") and authenticated:
                self.send("250 OK")
            elif verb == "DATA" and authenticated:
                data_mode = True
                self.send("354 End data with <CR><LF>.<CR><LF>")
            elif verb == "QUIT":
                self.send("221 Bye")
                return
            else:
                self.send("503 Bad sequence of commands")


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True


context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain("/fixture/cert.pem", "/fixture/key.pem")
with Server(("0.0.0.0", 8465), Handler) as server:
    server.socket = context.wrap_socket(server.socket, server_side=True)
    server.serve_forever()
