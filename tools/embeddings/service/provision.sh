#!/usr/bin/env bash
# Provision the embedding environment on the inference host.
#
# Design rules this script follows, each from a measured failure:
#
# 1. **One writer.** An exclusive lock on the host covers the whole run. Two provisioning processes
#    on one venv produced a torch whose files and CUDA libraries disagreed, and the failure was an
#    import error rather than a version mismatch anyone could see.
# 2. **Content, not a marker.** Readiness is decided by importing torch and comparing its version,
#    not by the existence of a file this script once wrote. A marker cannot know about an
#    environment someone else built.
# 3. **Destruction is a flag.** Removing the venv happens only with --reset, never as the else
#    branch of a check.
#
#   bash provision.sh            # install into an empty venv, or verify an existing one
#   bash provision.sh --reset    # remove the venv first, then install
set -uo pipefail

HOST="${EMB_DEPLOY_HOST:-scott@192.168.0.15}"
TORCH_VERSION="${EMB_TORCH_VERSION:-2.6.0}"
RESET=0
[ "${1:-}" = "--reset" ] && RESET=1

ssh -o BatchMode=yes -o ConnectTimeout=10 "$HOST" "wsl -d Ubuntu -- bash -s -- $RESET $TORCH_VERSION" <<'REMOTE'
set -uo pipefail
RESET="$1"
TORCH="${2:-2.6.0}"
VENV="$HOME/emb2"
PY="$VENV/bin/python"
LOCK="$HOME/.emb2-provision.lock"
export PIP_DISABLE_PIP_VERSION_CHECK=1

exec 9>"$LOCK"
if ! flock -n 9; then
  echo "REFUSING: another provisioning process holds $LOCK" >&2
  exit 1
fi

echo "== current state =="
if [ -x "$PY" ]; then
  "$PY" - <<'PY' || true
try:
    import torch
    print("torch_importable", torch.__version__)
except Exception as exc:
    print("torch_importable", False, type(exc).__name__, exc)
PY
else
  echo "no python at $VENV/bin/python"
fi

if [ "$RESET" = "1" ]; then
  echo "== --reset: removing $VENV =="
  rm -rf "$VENV"
fi

if [ ! -x "$PY" ]; then
  echo "== creating venv without pip, then bootstrapping pip (no root needed) =="
  mkdir -p "$VENV"
  python3 -m venv --without-pip "$VENV" || { echo "REFUSING: venv creation failed"; exit 1; }
  curl -fsSL --max-time 120 https://bootstrap.pypa.io/get-pip.py -o /tmp/get-pip.py \
    || { echo "REFUSING: get-pip download failed"; exit 1; }
  "$PY" /tmp/get-pip.py || { echo "REFUSING: pip bootstrap failed"; exit 1; }
fi

if "$PY" - "$TORCH" <<'PY'
import sys
import torch
want = sys.argv[1]
print("torch", torch.__version__)
sys.exit(0 if torch.__version__.split("+")[0] == want else 1)
PY
then
  echo "torch $TORCH already present, leaving the install alone"
else
  echo "== installing torch $TORCH from the cu124 index =="
  "$PY" -m pip install --force-reinstall "torch==$TORCH" \
    --index-url https://download.pytorch.org/whl/cu124 \
    || { echo "REFUSING: torch install failed"; exit 1; }
fi

# The loader constructs the model's vision and audio processors before it applies the config that
# disables those encoders, and those processors import PIL and torchvision. Measured as hard
# ImportErrors on the host. torchvision and torchaudio are pinned to the pair that matches torch
# 2.6.0 from the same index, because a newer pair drags torch forward with it.
echo "== modality processor dependencies =="
"$PY" -m pip install --no-cache-dir "torchvision==0.21.0" "torchaudio==2.6.0" \
  --index-url https://download.pytorch.org/whl/cu124 \
  || { echo "REFUSING: torchvision/torchaudio install failed"; exit 1; }

echo "== model libraries =="
"$PY" -m pip install "sentence-transformers==6.1.0" "transformers==5.20.1" fastapi uvicorn pillow \
  || { echo "REFUSING: library install failed"; exit 1; }

echo "== verification =="
"$PY" - <<'PY' || { echo "REFUSING: verification failed"; exit 1; }
import torch, sentence_transformers, transformers, fastapi, uvicorn
print("torch", torch.__version__)
print("cuda_available", torch.cuda.is_available())
print("device", torch.cuda.get_device_name(0) if torch.cuda.is_available() else "cpu")
print("bf16_supported", torch.cuda.is_bf16_supported() if torch.cuda.is_available() else False)
print("sentence_transformers", sentence_transformers.__version__)
print("transformers", transformers.__version__)
print("fastapi", fastapi.__version__)
print("uvicorn", uvicorn.__version__)
assert torch.cuda.is_available(), "CUDA is not available to torch"
assert torch.cuda.is_bf16_supported(), "this GPU does not report bf16 support"
PY

echo "== provisioned =="
REMOTE
