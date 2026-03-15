#!/usr/bin/env bash
set -euo pipefail

PLUGIN_NAME="BTCPayServer.Plugins.Taler"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PLUGIN_DIR="$ROOT_DIR/$PLUGIN_NAME"
BTCPAY_SRC_DIR="$ROOT_DIR/submodules/btcpayserver"
BUILD_DIR="$PLUGIN_DIR/bin/Release/net10.0"
OUT_DIR="$ROOT_DIR/artifacts"

mkdir -p "$OUT_DIR"

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$ROOT_DIR/.dotnet-home}"
mkdir -p "$DOTNET_CLI_HOME"

if [ ! -f "$BTCPAY_SRC_DIR/BTCPayServer/BTCPayServer.csproj" ]; then
  echo "btcpayserver submodule not initialised. Run: git submodule update --init"
  exit 1
fi

dotnet build "$PLUGIN_DIR/$PLUGIN_NAME.csproj" -c Release

dotnet run --project "$BTCPAY_SRC_DIR/BTCPayServer.PluginPacker" -- \
  "$BUILD_DIR" "$PLUGIN_NAME" "$OUT_DIR"
