#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 ]]; then
    printf 'Usage: %s output-directory\n' "$0" >&2
    exit 2
fi

fixture_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p -- "$1"
output_dir="$(cd -- "$1" && pwd)"
scratch_dir="$(mktemp -d)"
trap 'rm -rf -- "$scratch_dir"' EXIT

sed -e 's/EXTERNAL ASS/EMBEDDED ASS/' -e 's/pos(320,42)/pos(320,314)/' \
    -e 's/&H00FFFF00/\&H0000FFFF/' \
    "$fixture_dir/external.ass" > "$scratch_dir/embedded.ass"

"${FFMPEG:-ffmpeg}" -hide_banner -loglevel error -y \
    -f lavfi -i 'testsrc2=size=640x360:rate=30' \
    -f lavfi -i 'sine=frequency=440:sample_rate=48000' \
    -f lavfi -i 'sine=frequency=880:sample_rate=48000' \
    -i "$scratch_dir/embedded.ass" \
    -map 0:v -map 1:a -map 2:a -map 3:s -t 20 \
    -c:v libx264 -profile:v high -pix_fmt yuv420p -preset fast -crf 23 \
    -c:a aac -b:a 96k -c:s ass \
    -metadata:s:a:0 'title=440 Hz' -metadata:s:a:1 'title=880 Hz' \
    -metadata:s:s:0 'title=Embedded ASS' -disposition:s:0 default \
    "$output_dir/双音轨-ASS.mkv"

cp -- "$fixture_dir/external.ass" "$output_dir/external.ass"
