# -*- coding: utf-8 -*-
"""
BIM Command Center - Central Library Script
Audit and report imported DWG / CAD instances in the active Revit project.
"""
from Autodesk.Revit.DB import FilteredElementCollector, ImportInstance

doc = __revit__.ActiveUIDocument.Document

cad_imports = FilteredElementCollector(doc).OfClass(ImportInstance).ToElements()

print("=" * 60)
print("BIM COMMAND CENTER - CAD IMPORT HYGIENE AUDIT")
print("=" * 60)
print("Active Document:       {}".format(doc.Title))
print("Total CAD Imports:     {}".format(len(cad_imports)))
print("=" * 60)

unpinned_count = 0
for ci in cad_imports:
    pinned = "Pinned" if ci.Pinned else "UNPINNED [WARNING]"
    if not ci.Pinned:
        unpinned_count += 1
    view_specific = "View-Specific" if ci.ViewSpecific else "Model-Wide 3D"
    type_elem = doc.GetElement(ci.GetTypeId())
    name = type_elem.Name if type_elem else "Imported CAD"
    print("   - ID {:<9} | {:<22} | {:<16} | {}".format(ci.Id.IntegerValue, pinned, view_specific, name))

if unpinned_count > 0:
    print("\n[!] Attention: Found {} unpinned CAD import(s). Ensure CAD links are pinned to prevent coordinate shifting.".format(unpinned_count))
elif len(cad_imports) == 0:
    print("\n[OK] Clean Model: Zero imported CAD instances found in this document.")
