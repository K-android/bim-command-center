# -*- coding: utf-8 -*-
"""
pyrevit.forms compatibility module for BIM Command Center
"""
import sys
import clr
clr.AddReference('RevitAPIUI')
clr.AddReference('System.Windows.Forms')
clr.AddReference('Microsoft.VisualBasic')
from Autodesk.Revit.UI import TaskDialog, TaskDialogCommonButtons

def alert(msg, title="BIM Command Center", ok=True, warn_icon=False, exitscript=False):
    td = TaskDialog(title)
    td.MainInstruction = str(msg)
    td.CommonButtons = TaskDialogCommonButtons.Ok
    td.Show()
    if exitscript:
        sys.exit(0)

def ask_for_string(prompt="Enter value:", default="", title="BIM Command Center"):
    try:
        import Microsoft.VisualBasic.Interaction as VB
        res = VB.InputBox(prompt, title, default, -1, -1)
        return res if res else default
    except:
        return default

def ask_for_one_item(items, default=None, prompt="Select one item:", title="BIM Command Center"):
    if not items:
        return default
    return items[0]

class SelectFromList(object):
    @classmethod
    def show(cls, items, multiselect=False, title="Select", button_name="Select"):
        if not items:
            return [] if multiselect else None
        return items if multiselect else items[0]

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

class ProgressBar(object):
    def __init__(self, title="Processing...", cancellable=False, step=1):
        self.title = title
        self.step = step
        self.cancelled = False

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        pass

    def update_progress(self, current, total):
        pass

def toast(message, title="pyRevit"):
    alert(message, title=title)
