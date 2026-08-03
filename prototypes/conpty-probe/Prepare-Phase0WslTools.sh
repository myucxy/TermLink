#!/usr/bin/env bash
set -euo pipefail

root="${1:-/tmp/termlink-phase0-tools}"
download="$root/download"
mkdir -p "$download" "$root/fzf" "$root/htop" "$root/lib"

fetch() {
    local name="$1"
    local url="$2"
    local sha256="$3"
    local size="$4"
    local destination="$download/$name"

    if [[ -f "$destination" ]]; then
        echo "$sha256  $destination" | sha256sum --check --status || {
            echo "Existing download has the wrong SHA-256: $destination" >&2
            return 1
        }
    else
        curl --fail --location --retry 3 --output "$destination.partial" "$url"
        echo "$sha256  $destination.partial" | sha256sum --check --status || {
            echo "Downloaded file has the wrong SHA-256: $name" >&2
            return 1
        }
        mv "$destination.partial" "$destination"
    fi

    local actual_size
    actual_size="$(stat --format='%s' "$destination")"
    [[ "$actual_size" == "$size" ]] || {
        echo "Unexpected size for $name: $actual_size, expected $size" >&2
        return 1
    }
}

fetch \
    'fzf_0.44.1-1ubuntu0.3_amd64.deb' \
    'http://archive.ubuntu.com/ubuntu/pool/universe/f/fzf/fzf_0.44.1-1ubuntu0.3_amd64.deb' \
    '345ab0b82c0a2dfc364d1da4404b61c438413fc6881daf8acecd9e3821578258' \
    '1333828'
fetch \
    'htop_3.3.0-4build1_amd64.deb' \
    'http://archive.ubuntu.com/ubuntu/pool/main/h/htop/htop_3.3.0-4build1_amd64.deb' \
    'ee0e9cffc789788164214bac9b6e285a5127c07be1815129875c6c538ba849c6' \
    '170528'
fetch \
    'libnl-3-200_3.7.0-0.3build1.1_amd64.deb' \
    'http://archive.ubuntu.com/ubuntu/pool/main/libn/libnl3/libnl-3-200_3.7.0-0.3build1.1_amd64.deb' \
    '15b3f61db753dc3db6f62f4259f8b822299343e500886776486612a13a2ed247' \
    '55706'
fetch \
    'libnl-genl-3-200_3.7.0-0.3build1.1_amd64.deb' \
    'http://archive.ubuntu.com/ubuntu/pool/main/libn/libnl3/libnl-genl-3-200_3.7.0-0.3build1.1_amd64.deb' \
    '9ca0cb63f295d00e6346b9924278c07cfc61311cbd288b805b06e25aa08c9fd8' \
    '12232'

dpkg-deb --extract "$download/fzf_0.44.1-1ubuntu0.3_amd64.deb" "$root/fzf"
dpkg-deb --extract "$download/htop_3.3.0-4build1_amd64.deb" "$root/htop"
dpkg-deb --extract "$download/libnl-3-200_3.7.0-0.3build1.1_amd64.deb" "$root/lib"
dpkg-deb --extract "$download/libnl-genl-3-200_3.7.0-0.3build1.1_amd64.deb" "$root/lib"

fzf_version="$($root/fzf/usr/bin/fzf --version)"
htop_version="$(LD_LIBRARY_PATH="$root/lib/usr/lib/x86_64-linux-gnu" "$root/htop/usr/bin/htop" --version | head -n 1)"
[[ "$fzf_version" == '0.44.1 (debian)' ]] || { echo "Unexpected fzf version: $fzf_version" >&2; exit 1; }
[[ "$htop_version" == 'htop 3.3.0' ]] || { echo "Unexpected htop version: $htop_version" >&2; exit 1; }

printf 'Prepared phase 0 WSL tools in %s\nfzf: %s\nhtop: %s\n' "$root" "$fzf_version" "$htop_version"
