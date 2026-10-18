"""Configuration for the local embedding service.

Every value here comes from the environment so the service has one source of truth that the
launcher, the provider and this file all name the same way. Nothing here is inferred from a
default that would silently differ from what the provider expects: the provider is configured
with the same tag, dimension and prefix version, and refuses to use a vector produced under a
different one.

The two constraints that are not policy choices:

* The bind address must be loopback. The service refuses to start on any other address,
  because the whole point of running the model locally is that message segments never leave
  the machine, and a bind that reaches a LAN interface contradicts that by configuration alone.
* The token comes from a file whose permissions do not let another user read it. A token in
  an environment variable ends up in a process listing; a file with 0644 is readable by every
  user on the box.
"""

from __future__ import annotations

import os
import pathlib
import stat
from dataclasses import dataclass
from typing import Mapping

# Matryoshka checkpoints this model was trained for. A value outside this set is not a
# smaller vector, it is a different (and unmeasured) truncation.
SUPPORTED_DIMENSIONS = (768, 512, 256, 128)

LOOPBACK_HOSTS = frozenset({"127.0.0.1", "::1", "localhost"})

# The one prefix this service applies, per version. It is applied server side to every
# segment so a caller cannot mix prefixes, and it is reported in the metadata so the provider
# can refuse a service whose prefix moved.
DEFAULT_PREFIX = "task: classification | query: "
DEFAULT_PREFIX_VERSION = "classification/1"

# The prototype artefact the classify endpoint reads, beside the code that uses it. It lives in the
# service directory so the deployed layout and the repository layout are the same thing: the first
# version resolved one directory too high on the host and asked for /home/scott/artefacts, which is
# a path that only looked right in the checkout.
DEFAULT_ARTEFACT_PATH = pathlib.Path(__file__).resolve().parent / "artefacts" / "elements-teacher-urgency-1.json"


class ConfigError(RuntimeError):
    """The service will not start, and the message names which setting is wrong."""


@dataclass(frozen=True)
class Config:
    host: str
    port: int
    token_path: pathlib.Path
    model: str
    dimension: int
    prefix: str
    prefix_version: str
    max_segments: int
    max_segment_chars: int
    max_total_chars: int
    max_tokens: int
    batch_size: int
    native_dimension: int = 768
    artefact_path: pathlib.Path = DEFAULT_ARTEFACT_PATH

    @staticmethod
    def from_env(env: Mapping[str, str] | None = None) -> "Config":
        source = os.environ if env is None else env

        host = source.get("EMB_HOST", "127.0.0.1").strip()
        if host not in LOOPBACK_HOSTS:
            raise ConfigError(
                f"EMB_HOST={host!r} is not a loopback address; the embedding service binds "
                "loopback only so message segments never reach a network interface"
            )

        dimension = _int(source, "EMB_DIM", 256)
        if dimension not in SUPPORTED_DIMENSIONS:
            raise ConfigError(
                f"EMB_DIM={dimension} is not one of {SUPPORTED_DIMENSIONS}; the model's "
                "Matryoshka truncations are exactly those widths"
            )

        token_path = pathlib.Path(
            source.get("EMB_TOKEN_FILE", str(pathlib.Path.home() / ".config/stylomail/embedding-token"))
        ).expanduser()

        prefix_version = source.get("EMB_PREFIX_VERSION", DEFAULT_PREFIX_VERSION).strip()
        if not prefix_version:
            raise ConfigError("EMB_PREFIX_VERSION is empty; every vector must name its prefix version")

        return Config(
            host=host,
            port=_int(source, "EMB_PORT", 11440),
            token_path=token_path,
            model=source.get("EMB_MODEL", "google/embeddinggemma-2").strip(),
            dimension=dimension,
            prefix=source.get("EMB_PREFIX", DEFAULT_PREFIX),
            prefix_version=prefix_version,
            max_segments=_int(source, "EMB_MAX_SEGMENTS", 32),
            max_segment_chars=_int(source, "EMB_MAX_SEGMENT_CHARS", 6000),
            max_total_chars=_int(source, "EMB_MAX_TOTAL_CHARS", 32000),
            max_tokens=_int(source, "EMB_MAX_TOKENS", 8192),
            batch_size=_int(source, "EMB_BATCH_SIZE", 8),
            artefact_path=pathlib.Path(
                source.get("EMB_ARTEFACT", str(DEFAULT_ARTEFACT_PATH))
            ).expanduser(),
        )

    def read_token(self) -> bytes:
        """Read the bearer token, refusing a file another user could read.

        Re-read on each call on purpose: rotation is a file write, not a restart. The cost is
        one small read on a loopback service, and the alternative is a token that outlives the
        decision to replace it.
        """
        try:
            info = self.token_path.stat()
        except FileNotFoundError as exc:
            raise ConfigError(f"token file {self.token_path} does not exist") from exc

        if not stat.S_ISREG(info.st_mode):
            raise ConfigError(f"token file {self.token_path} is not a regular file")
        if info.st_mode & (stat.S_IRWXG | stat.S_IRWXO):
            raise ConfigError(
                f"token file {self.token_path} is accessible to group or other "
                f"(mode {stat.S_IMODE(info.st_mode):04o}); chmod 600 it"
            )

        token = self.token_path.read_bytes().strip()
        if len(token) < 16:
            raise ConfigError(f"token file {self.token_path} holds {len(token)} bytes; it is not a token")
        return token


def _int(source: Mapping[str, str], name: str, default: int) -> int:
    raw = source.get(name)
    if raw is None or raw.strip() == "":
        return default
    try:
        return int(raw)
    except ValueError as exc:
        raise ConfigError(f"{name}={raw!r} is not an integer") from exc
