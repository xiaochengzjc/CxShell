#!/usr/bin/env sh

printf 'LOGIN_SCRIPT_OK\n'
printf 'SHELL_NAME=%s\n' "${SHELL:-sh}"
printf 'ARG_COUNT=%s\n' "$#"

index=0
for value in "$@"; do
    printf 'ARG_%s=%s\n' "$index" "$value"
    index=$((index + 1))
done
