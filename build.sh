#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$ROOT/KmTronicRelay.csproj"
BUILD_DIR="$ROOT/build"
WIN_PUBLISH="$BUILD_DIR/bin/KmTronicRelay/Release/net8.0/win-x64/publish/KmTronicRelay.exe"
WIN_OUTPUT="$BUILD_DIR/KmTronicRelay.exe"

stop_running_executable() {
    local taskkill=""
    for candidate in taskkill.exe /mnt/c/Windows/System32/taskkill.exe; do
        if command -v "$candidate" >/dev/null 2>&1; then
            taskkill="$candidate"
            break
        fi
        if [[ -x "$candidate" ]]; then
            taskkill="$candidate"
            break
        fi
    done

    if [[ -n "$taskkill" ]]; then
        if [[ "$taskkill" == "taskkill.exe" ]]; then
            "$taskkill" //IM KmTronicRelay.exe //F >/dev/null 2>&1 || true
        else
            "$taskkill" /IM KmTronicRelay.exe /F >/dev/null 2>&1 || true
        fi
    fi

    local powershell=""
    for candidate in powershell.exe /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe; do
        if command -v "$candidate" >/dev/null 2>&1; then
            powershell="$candidate"
            break
        fi
        if [[ -x "$candidate" ]]; then
            powershell="$candidate"
            break
        fi
    done
    if [[ -n "$powershell" ]]; then
        "$powershell" -NoProfile -Command \
            "Stop-Process -Name KmTronicRelay -Force -ErrorAction SilentlyContinue" >/dev/null 2>&1 || true
    fi

    if command -v pkill >/dev/null 2>&1; then
        pkill -x KmTronicRelay >/dev/null 2>&1 || true
    fi
    sleep 1
}

copy_release_binary() {
    local attempt
    for attempt in 1 2 3; do
        stop_running_executable
        rm -f "$WIN_OUTPUT" 2>/dev/null || true
        if cp "$WIN_PUBLISH" "$WIN_OUTPUT" 2>/dev/null; then
            return 0
        fi
        echo "Output file locked — stopping KmTronicRelay.exe (attempt ${attempt}/3)..."
    done
    echo "ERROR: Could not copy $WIN_PUBLISH to $WIN_OUTPUT" >&2
    return 1
}

publish() {
    local runtime="$1"
    echo "Publishing self-contained binary for ${runtime}..."
    dotnet publish "$PROJECT" \
        -c Release \
        -r "$runtime" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true
}

if ! command -v dotnet >/dev/null 2>&1; then
    cat <<'EOF' >&2
ERROR: .NET SDK not found in WSL.

Install inside Ubuntu (no Windows admin needed):
  sudo apt update
  sudo apt install -y dotnet-sdk-8.0

Then run:
  ./build.sh
EOF
    exit 1
fi

mkdir -p "$BUILD_DIR"

echo "Clearing build intermediates..."
rm -rf "$BUILD_DIR/obj" "$BUILD_DIR/bin"

echo "Restoring packages..."
dotnet restore "$PROJECT"

echo "Checking for vulnerable packages..."
if dotnet list "$PROJECT" package --vulnerable --include-transitive | grep -q "has the following vulnerable"; then
    echo "ERROR: Vulnerable packages detected. Update packages before publishing." >&2
    dotnet list "$PROJECT" package --vulnerable --include-transitive >&2
    exit 1
fi

publish win-x64

echo "Running unit tests..."
dotnet test "$ROOT/tests/KmTronicRelay.Tests/KmTronicRelay.Tests.csproj" -c Release --verbosity minimal

echo "Copying release binary to build/..."
copy_release_binary

echo ""
echo "Build succeeded."
echo ""
echo "Windows (copy to a Windows PC and run — no .NET install needed):"
echo "  $WIN_OUTPUT"
echo ""
echo "Intermediate artifacts: build/bin/ and build/obj/"
