#!/usr/bin/env bash
# Compiles src/*.cs into CloudpunkVR.dll against the BepInEx-generated interop assemblies of the installed game (start
# the installed game once first: BepInEx writes BepInEx/interop on the first launch), then deploys it.
#   GAME=/d/Games/CloudpunkVR tools/build.sh     build + deploy to the game's BepInEx/plugins/CloudpunkVR
#   tools/build.sh --no-deploy
# Run it in Git Bash. The compiler is Roslyn's csc.dll, taken from (first that exists): $CSC_DLL, a Unity 2022.3 editor
# ($UNITY_DATA = its Editor/Data folder), or the newest installed .NET SDK (dotnet on PATH).
set -euo pipefail
UNITY_DATA="${UNITY_DATA:-/g/Apps/UnityHub/2022.3.22f1/Editor/Data}"
GAME="${GAME:-/g/Games/CloudpunkVR}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${OUT:-$ROOT/build}"
mkdir -p "$OUT"
[[ -d "$GAME/BepInEx/interop" ]] || { echo "no $GAME/BepInEx/interop: set GAME to the installed VR copy and start it once" >&2; exit 1; }

DOTNET="${DOTNET:-dotnet}"
if [[ -z "${CSC_DLL:-}" ]]; then
  if [[ -f "$UNITY_DATA/DotNetSdkRoslyn/csc.dll" ]]; then
    DOTNET="$UNITY_DATA/NetCoreRuntime/dotnet.exe"; CSC_DLL="$UNITY_DATA/DotNetSdkRoslyn/csc.dll"
  else
    SDK="$(dotnet --list-sdks | tail -n 1 | tr -d '\r')"   # "8.0.428 [C:\Program Files\dotnet\sdk]"
    SDK_DIR="${SDK#*[}"; SDK_DIR="${SDK_DIR%]}"
    CSC_DLL="$(cygpath -u "$SDK_DIR")/${SDK%% *}/Roslyn/bincore/csc.dll"
  fi
fi
[[ -f "$CSC_DLL" ]] || { echo "no C# compiler at $CSC_DLL: install a .NET SDK or set CSC_DLL / UNITY_DATA" >&2; exit 1; }
csc() { "$DOTNET" "$(win "$CSC_DLL")" "$@"; }

win() { cygpath -w "$1"; }
RSP="$OUT/refs.rsp"
: > "$RSP"
for f in "$GAME"/dotnet/System.*.dll "$GAME"/dotnet/netstandard.dll "$GAME"/dotnet/mscorlib.dll; do
  case "$(basename "$f")" in System.Private.*|*.Native.dll) ;; *) echo "-r:\"$(win "$f")\"" >> "$RSP" ;; esac
done
echo "-r:\"$(win "$GAME/dotnet/System.Private.CoreLib.dll")\"" >> "$RSP"
for f in BepInEx.Core BepInEx.Unity.IL2CPP BepInEx.Unity.Common Il2CppInterop.Runtime Il2CppInterop.Common 0Harmony; do
  echo "-r:\"$(win "$GAME/BepInEx/core/$f.dll")\"" >> "$RSP"
done
for f in "$GAME"/BepInEx/interop/*.dll; do echo "-r:\"$(win "$f")\"" >> "$RSP"; done

SRC=()
for f in "$ROOT"/src/*.cs "$ROOT"/src/Coo/*.cs; do SRC+=("$(win "$f")"); done

csc -nologo -nostdlib -noconfig \
  -target:library -langversion:latest -optimize+ -unsafe+ -debug:portable -warn:1 -nowarn:CS1701,CS1702 \
  -out:"$(win "$OUT/CloudpunkVR.dll")" @"$(win "$RSP")" "${SRC[@]}"
echo "built $OUT/CloudpunkVR.dll"

# Preloader patcher: runs inside il2cpp_init, before the engine starts its job system.
PRE=()
for f in "$ROOT"/preload/*.cs; do PRE+=("$(win "$f")"); done
csc -nologo -nostdlib -noconfig \
  -target:library -langversion:latest -optimize+ -unsafe+ -debug:portable -warn:1 -nowarn:CS1701,CS1702 \
  -out:"$(win "$OUT/CloudpunkVR.Preload.dll")" @"$(win "$RSP")" -r:"$(win "$GAME/BepInEx/core/BepInEx.Preloader.Core.dll")" "${PRE[@]}"
echo "built $OUT/CloudpunkVR.Preload.dll"

# Controller layout editor: a WinForms exe on .NET Framework 4.x (part of every Windows 10/11), sharing ControlMap.cs.
EDITOR="Cloudpunk VR 按键设置.exe"
FW="$(cygpath -u "${WINDIR:-C:\Windows}")/Microsoft.NET/Framework64/v4.0.30319"
csc -nologo -nostdlib -noconfig   -target:winexe -langversion:latest -optimize+ -warn:1   -r:"$(win "$FW/mscorlib.dll")" -r:"$(win "$FW/System.dll")" -r:"$(win "$FW/System.Core.dll")"   -r:"$(win "$FW/System.Drawing.dll")" -r:"$(win "$FW/System.Windows.Forms.dll")"   -out:"$(win "$OUT/$EDITOR")" "$(win "$ROOT/src/ControlMap.cs")" "$(win "$ROOT/tools/ControlsEditor/ControlsEditor.cs")"
echo "built $OUT/$EDITOR"

if [[ "${1:-}" != "--no-deploy" ]]; then
  DEST="$GAME/BepInEx/plugins/CloudpunkVR"
  mkdir -p "$DEST"
  cp "$OUT/CloudpunkVR.dll" "$OUT/CloudpunkVR.pdb" "$DEST/"
  mkdir -p "$GAME/BepInEx/patchers/CloudpunkVR"
  cp "$OUT/CloudpunkVR.Preload.dll" "$OUT/CloudpunkVR.Preload.pdb" "$GAME/BepInEx/patchers/CloudpunkVR/"
  # the passenger-seat doll (tools/fufu-export writes assets/fufu from art/CortiDoll)
  if compgen -G "$ROOT/assets/fufu/*" > /dev/null; then
    mkdir -p "$DEST/fufu" && cp "$ROOT"/assets/fufu/* "$DEST/fufu/"
  fi
  if compgen -G "$ROOT/bindings/*.json" > /dev/null; then
    mkdir -p "$DEST/bindings" && cp "$ROOT"/bindings/*.json "$DEST/bindings/"
  fi
  cp "$OUT/$EDITOR" "$ROOT/README-CloudpunkVR.md" "$GAME/"
  echo "deployed to $DEST"
fi
