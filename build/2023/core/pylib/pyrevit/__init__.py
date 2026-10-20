# -*- coding: utf-8 -*-
"""
BIM Command Center - Built-in pyRevit Compatibility Shim
Provides standard pyRevit APIs so corporate pyRevit scripts run on any workstation
without requiring pyRevit to be installed on every machine.
"""
import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')
import Autodesk.Revit.DB as DB
import Autodesk.Revit.UI as UI
import sys

from . import revit
from . import script
from . import forms
