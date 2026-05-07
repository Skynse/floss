#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"

app_exec="${1:-$repo_root/src/Floss.App/bin/Debug/net10.0/Floss.App}"
desktop_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mime_dir="${XDG_DATA_HOME:-$HOME/.local/share}/mime"
thumbnailer_dir="${XDG_DATA_HOME:-$HOME/.local/share}/thumbnailers"
bin_dir="${XDG_BIN_HOME:-$HOME/.local/bin}"
desktop_file="$desktop_dir/floss.desktop"

if [[ ! -x "$app_exec" ]]; then
  echo "Floss executable not found or not executable: $app_exec" >&2
  echo "Build first with: dotnet build" >&2
  echo "Or pass the executable path: $0 /path/to/Floss.App" >&2
  exit 1
fi

mkdir -p "$desktop_dir" "$mime_dir/packages" "$thumbnailer_dir" "$bin_dir"

install -m 0644 "$script_dir/application-x-floss.xml" "$mime_dir/packages/application-x-floss.xml"

install -m 0755 "$script_dir/floss-thumbnailer" "$bin_dir/floss-thumbnailer"
thumbnailer_exec="$bin_dir/floss-thumbnailer"
sed "s|@THUMBNAILER_EXEC@|$thumbnailer_exec|g" "$script_dir/floss.thumbnailer" > "$thumbnailer_dir/floss.thumbnailer"
chmod 0644 "$thumbnailer_dir/floss.thumbnailer"

escaped_exec="${app_exec//\\/\\\\}"
escaped_exec="${escaped_exec//\"/\\\"}"
sed "s|@EXEC@|\"$escaped_exec\"|g" "$script_dir/floss.desktop.in" > "$desktop_file"
chmod 0644 "$desktop_file"

if command -v update-mime-database >/dev/null 2>&1; then
  update-mime-database "$mime_dir"
fi

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$desktop_dir" >/dev/null 2>&1 || true
fi

if command -v xdg-mime >/dev/null 2>&1; then
  xdg-mime default floss.desktop application/x-floss
  xdg-mime default floss.desktop application/x-kra
fi

echo "Registered .floss as application/x-floss"
echo "Registered .kra as application/x-kra"
echo "Desktop file: $desktop_file"
echo "Executable: $app_exec"
