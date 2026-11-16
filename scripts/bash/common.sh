#!/usr/bin/env bash
# Shared console helpers. Source this file; do not execute it directly.

vibekit_require_commands() {
    local name missing=0
    for name in "$@"; do
        if ! command -v "$name" >/dev/null 2>&1; then
            printf 'Missing command: %s\n' "$name" >&2
            missing=1
        fi
    done
    return "$missing"
}

vibekit_choose() {
    local choice index=1 option
    [ "$#" -gt 0 ] || return 1
    for option in "$@"; do
        printf '%s) %s\n' "$index" "$option" >&2
        index=$((index + 1))
    done
    printf 'Select an option: ' >&2
    IFS= read -r choice || return 1
    case "$choice" in
        ''|*[!0-9]*) return 1 ;;
    esac
    # Strip leading zeroes before arithmetic to avoid octal interpretation.
    while [ "${choice#0}" != "$choice" ]; do choice=${choice#0}; done
    [ -n "$choice" ] || return 1
    [ "${#choice}" -le 6 ] || return 1
    [ "$choice" -ge 1 ] && [ "$choice" -le "$#" ] || return 1
    printf '%s\n' "$choice"
}
