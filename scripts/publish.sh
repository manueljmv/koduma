#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Koduma.Cli/Koduma.Cli.csproj"
LINUX_OUT="$HOME/Programs/koduma"

dotnet publish "$PROJECT" -c Release -r linux-x64 --self-contained -o "$LINUX_OUT"

echo "Published linux-x64 to $LINUX_OUT"
echo
echo "win-x64 was not published: Native AOT (PublishAot=true) does not cross-compile" \
     "between OSes. Run this on a Windows machine with the .NET SDK and the" \
     "'Desktop development with C++' workload installed:"
echo "  dotnet publish \"$PROJECT\" -c Release -r win-x64 --self-contained -o \"%USERPROFILE%\\Programs\\win-x64\\koduma\""
