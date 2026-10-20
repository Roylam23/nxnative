#!/usr/bin/env python3
"""Compatibility entry point for the old name.

The packing script is now scripts/build_ipa.py: it accepts a game folder, a single .nsp or a
single .xci and detects the rest from the files. This wrapper forwards the command line so old
commands keep working; new commands should call build_ipa.py directly.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_ipa

if __name__ == '__main__':
    code = build_ipa.cli(sys.argv[1:])
    if not build_ipa.in_ipython(): sys.exit(code)
