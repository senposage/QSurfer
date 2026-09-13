#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
output="$root/dist/QSurfer-linux-x64"
staging="$output.staging"

rm -rf "$staging"
dotnet publish "$root/src/QSurfer.Avalonia/QSurfer.Avalonia.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$staging"

mkdir -p "$staging/config"
cp "$root/config.template.json" "$staging/config/config.json"
chmod +x "$staging/QSurfer"
rm -rf "$output"
mv "$staging" "$output"
printf 'Linux package ready: %s\n' "$output"
