#!/usr/bin/env python3
"""Small CxShell login-script smoke test."""

import platform
import sys


print("LOGIN_SCRIPT_OK", flush=True)
print(f"PYTHON_VERSION={platform.python_version()}", flush=True)
print(f"ARG_COUNT={len(sys.argv) - 1}", flush=True)

for index, value in enumerate(sys.argv[1:]):
    print(f"ARG_{index}={value}", flush=True)
