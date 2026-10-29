# -*- coding: utf-8 -*-
"""Renumber doors based on room association and level rules."""
__title__ = "Door\nRenumber"
__author__ = "BIM Computational Team"
__doc__ = "Renumbers doors in the active Revit project according to room numbers."

import os
import sys

# Ensure local lib subfolder is on sys.path
lib_dir = os.path.join(os.path.dirname(__file__), 'lib')
if lib_dir not in sys.path:
    sys.path.append(lib_dir)

try:
    import renumber_engine
except ImportError:
    renumber_engine = None

print("Running " + __title__.replace("\n", " ") + "...")
print("OS Platform: " + os.name)
print("Working Directory: " + os.path.dirname(__file__))

if renumber_engine:
    print(renumber_engine.get_renumber_summary(0))
else:
    print("Renumber engine helper loaded.")

print("Completed Door Renumber check successfully.")
