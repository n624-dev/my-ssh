#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 3 ]]; then
  echo 'Usage: package-linux-release.sh <publish-directory> <release-directory> <archive-name>' >&2
  exit 2
fi
publish="$(realpath -- "$1")"
mkdir -p -- "$2"
release="$(realpath -- "$2")"
archive="$3"
if [[ ! "$archive" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*\.tar\.gz$ ]]; then
  echo 'Archive name must be a plain .tar.gz filename.' >&2
  exit 2
fi

tar -C "$publish" -czf "$release/$archive" -- my-ssh myssh LICENSE Terminal.Gui.LICENSE
# Users download both assets into one directory. Store a basename rather than
# the CI working directory so sha256sum -c works after moving the assets.
(
  cd -- "$release"
  sha256sum -- "$archive" > "$archive.sha256"
)
