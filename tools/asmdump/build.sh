#!/usr/bin/env bash
# Builds AsmDump with the Roslyn compiler that ships inside the Unity 2022.3 editor (no .NET SDK needed).
set -euo pipefail
UNITY_DATA="${UNITY_DATA:-/g/Apps/UnityHub/2022.3.22f1/Editor/Data}"
GAME="${GAME:-/g/Games/CloudpunkVR}"
OUT="${1:?output dir}"
NETCORE="$UNITY_DATA/NetCoreRuntime/shared/Microsoft.NETCore.App/6.0.21"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"
refs=()
for r in System.Private.CoreLib System.Runtime System.Console System.Collections System.Linq System.Text.RegularExpressions netstandard; do
  refs+=("-r:$(cygpath -w "$NETCORE/$r.dll")")
done
"$UNITY_DATA/NetCoreRuntime/dotnet.exe" "$(cygpath -w "$UNITY_DATA/DotNetSdkRoslyn/csc.dll")" -nologo -nostdlib -target:exe \
  -out:"$(cygpath -w "$OUT/AsmDump.dll")" "${refs[@]}" -r:"$(cygpath -w "$GAME/BepInEx/core/Mono.Cecil.dll")" \
  "$(cygpath -w "$HERE/AsmDump.cs")"
cp "$GAME/BepInEx/core/Mono.Cecil.dll" "$OUT/"
cat > "$OUT/AsmDump.runtimeconfig.json" <<'JSON'
{"runtimeOptions":{"tfm":"net6.0","framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}
JSON
echo "built $OUT/AsmDump.dll"
