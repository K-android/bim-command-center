# -*- coding: utf-8 -*-
"""
rpw (revitpythonwrapper) compatibility shim for BIM Command Center
"""
import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')
import Autodesk.Revit.DB as DB
import Autodesk.Revit.UI as UI
from pyrevit import revit

db = DB
ui = UI
doc = revit.doc
uidoc = revit.uidoc
