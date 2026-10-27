#!/usr/bin/env sh
# Builds the plugin folder a Turbo server loads, dist/clienterrors/: manifest.json, the C# sources
# (server/**/*.cs) and the plugin's files. The server compiles the sources as it loads
# the plugin: the folder holds nothing built. The sources are compiled first as the server does
# (server/TurboClientErrors.csproj imports the server's plugin-sdk/Turbo.Plugin.props), so a folder that would
# not load is not made.
# TurboBin: the server's bin folder to build against (default: ../turbo-cloud's built Turbo.Main).
set -e
cd "$(dirname "$0")/.."
dotnet build server/TurboClientErrors.csproj -nologo -v q -clp:NoSummary
rm -rf dist/clienterrors
mkdir -p dist/clienterrors
cp manifest.json dist/clienterrors/
find server -name '*.cs' ! -path '*/bin/*' ! -path '*/obj/*' | tar -cf - -T - | tar -xf - -C dist/clienterrors
for folder in client/src; do
  if [ -d "$folder" ]; then mkdir -p "dist/clienterrors/$(dirname "$folder")" && cp -r "$folder" "dist/clienterrors/$folder"; fi
done
echo "dist/clienterrors: $(find dist/clienterrors -type f | wc -l) files"
