# -*- coding: utf-8 -*-
"""
pyrevit.forms compatibility module
"""
import clr
clr.AddReference('RevitAPIUI')
from Autodesk.Revit.UI import TaskDialog, TaskDialogCommonButtons

def alert(msg, title="BIM Command Center", ok=True, warn_icon=False, exitscript=False):
    td = TaskDialog(title)
    td.MainInstruction = str(msg)
    td.CommonButtons = TaskDialogCommonButtons.Ok
    td.Show()
    if exitscript:
        import sys
        sys.exit(0)

def check_selection(message="Please select elements in the active view before running this script."):
    from . import revit
    uidoc = revit.uidoc
    if not uidoc:
        alert("No active document found.", title="BIM Command Center")
        return False
    sel = uidoc.Selection.GetElementIds()
    if not sel or sel.Count == 0:
        alert(message, title="No Selection")
        return False
    return True
