# -*- coding: utf-8 -*-
"""
BIM Command Center - Central Library Script
Audit and report unplaced or unclosed rooms in the active Revit project.
"""
from Autodesk.Revit.DB import FilteredElementCollector, BuiltInCategory, BuiltInParameter, Room

doc = __revit__.ActiveUIDocument.Document

rooms = FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().ToElements()

unplaced = []
unclosed = []
placed = []

for r in rooms:
    if isinstance(r, Room):
        if r.Location is None:
            unplaced.append(r)
        elif r.Area == 0:
            unclosed.append(r)
        else:
            placed.append(r)

print("=" * 60)
print("BIM COMMAND CENTER - CENTRAL LIBRARY ROOM AUDIT")
print("=" * 60)
print("Active Document:       {}".format(doc.Title))
print("Total Rooms:           {}".format(len(rooms)))
print("Placed & Enclosed:     {}".format(len(placed)))
print("Unplaced Rooms:        {}".format(len(unplaced)))
print("Unenclosed/Redundant:  {}".format(len(unclosed)))
print("=" * 60)

if unplaced:
    print("\n[!] Unplaced Rooms Detail:")
    for ur in unplaced[:25]:
        p = ur.get_Parameter(BuiltInParameter.ROOM_NAME)
        name = p.AsString() if p else "Unnamed"
        print("   - Room ID {}: \"{}\"".format(ur.Id.IntegerValue, name))
else:
    print("\n[OK] Model Hygiene: No unplaced rooms detected in this model.")
