#!/usr/bin/env bash
# Builds SpoutTestSender with the Roslyn compiler inside the Unity 2022.3 editor (no .NET SDK needed); runs on its dotnet.
set -euo pipefail
UNITY_DATA="${UNITY_DATA:-/g/Apps/UnityHub/2022.3.22f1/Editor/Data}"
OUT="${1:?output dir}"
NETCORE="$UNITY_DATA/NetCoreRuntime/shared/Microsoft.NETCore.App/6.0.21"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"
refs=()
for r in System.Private.CoreLib System.Runtime System.Console System.Threading System.Threading.Thread System.Runtime.InteropServices System.Text.Encoding.Extensions netstandard; do
  refs+=("-r:$(cygpath -w "$NETCORE/$r.dll")")
done
"$UNITY_DATA/NetCoreRuntime/dotnet.exe" "$(cygpath -w "$UNITY_DATA/DotNetSdkRoslyn/csc.dll")" -nologo -nostdlib -target:exe \
  -out:"$(cygpath -w "$OUT/SpoutTestSender.dll")" "${refs[@]}" "$(cygpath -w "$HERE/SpoutTestSender.cs")"
cat > "$OUT/SpoutTestSender.runtimeconfig.json" <<'JSON'
{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}
JSON
echo "run: \"$UNITY_DATA/NetCoreRuntime/dotnet.exe\" $OUT/SpoutTestSender.dll [seconds] [name]"
