#!/usr/bin/env bash
set -euo pipefail

: "${TALER_MERCHANT_PORT:=9966}"
: "${TALER_MERCHANT_DB_HOST:=taler-merchant-db}"
: "${TALER_MERCHANT_DB_PORT:=5432}"
: "${TALER_MERCHANT_DB_USER:=taler}"
: "${TALER_MERCHANT_DB_PASSWORD:=taler}"
: "${TALER_MERCHANT_DB_NAME:=taler-merchant}"
: "${TALER_MERCHANT_BASE_URL:=http://localhost:${TALER_MERCHANT_PORT}/}"

export TALER_MERCHANT_PORT
export TALER_MERCHANT_DB_HOST
export TALER_MERCHANT_DB_PORT
export TALER_MERCHANT_DB_USER
export TALER_MERCHANT_DB_PASSWORD
export TALER_MERCHANT_DB_NAME
export TALER_MERCHANT_BASE_URL

envsubst < /etc/taler-merchant/merchant.conf.template > /etc/taler-merchant/merchant.conf

until pg_isready -h "$TALER_MERCHANT_DB_HOST" -p "$TALER_MERCHANT_DB_PORT" -U "$TALER_MERCHANT_DB_USER"; do
  echo "Waiting for Taler merchant database..."
  sleep 2
done

taler-merchant-dbinit -c /etc/taler-merchant/merchant.conf || true

declare -a pids=()
declare -a daemons=(
  "taler-merchant-httpd"
  "taler-merchant-webhook"
  "taler-merchant-kyccheck"
  "taler-merchant-wirewatch"
  "taler-merchant-depositcheck"
  "taler-merchant-exchangekeyupdate"
  "taler-merchant-reconciliation"
)

# Optional helper available in some builds.
if command -v taler-merchant-donaukeyupdate >/dev/null 2>&1; then
  daemons+=("taler-merchant-donaukeyupdate")
fi

shutdown() {
  for pid in "${pids[@]:-}"; do
    if kill -0 "$pid" >/dev/null 2>&1; then
      kill "$pid" >/dev/null 2>&1 || true
    fi
  done
  wait || true
}

trap shutdown SIGTERM SIGINT

for daemon in "${daemons[@]}"; do
  if ! command -v "$daemon" >/dev/null 2>&1; then
    echo "Skipping unavailable daemon: $daemon"
    continue
  fi
  echo "Starting $daemon..."
  "$daemon" -c /etc/taler-merchant/merchant.conf &
  pids+=("$!")
done

if [ "${#pids[@]}" -eq 0 ]; then
  echo "No merchant daemons started; exiting."
  exit 1
fi

# Exit if any daemon exits unexpectedly.
wait -n "${pids[@]}"
exit $?
