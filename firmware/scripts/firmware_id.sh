#!/usr/bin/env bash
# The firmware build id (spec §9.2): a hash of exactly the sources that make up the firmware, so
# a release whose firmware didn't change gets the same id and Volco offers no update.
# Usage: firmware_id.sh          prints 12 hex digits
#        firmware_id.sh --list   prints the hashed paths
# check_firmware_id_paths.py (run in CI) fails if platformio.ini builds from a path not listed here.
set -euo pipefail
PATHS=(
  firmware/src
  firmware/lib
  firmware/platformio.ini
  firmware/scripts
  proto/volco_config.proto
  proto/volco_config.options
)
if [[ "${1:-}" == "--list" ]]; then
  printf '%s\n' "${PATHS[@]}"
  exit 0
fi
cd "$(git rev-parse --show-toplevel)"
for p in "${PATHS[@]}"; do git rev-parse "HEAD:$p"; done | sha256sum | cut -c1-12
