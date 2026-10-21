# -*- coding: utf-8 -*-
"""
BIM Command Center - Built-in pyRevit Compatibility Shim
Provides standard pyRevit APIs so corporate pyRevit pushbuttons and scripts run on any workstation
without requiring pyRevit to be installed on every machine.
"""
import sys
import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')
import Autodesk.Revit.DB as DB
import Autodesk.Revit.UI as UI

class PyRevitException(Exception):
    pass

class PyRevitIOError(PyRevitException):
    pass

class _HostApplication(object):
    @property
    def uiapp(self):
        try:
            return __uiapp__
        except:
            try:
                return __revit__
            except:
                return None

    @property
    def app(self):
        uia = self.uiapp
        return uia.Application if uia else None

    @property
    def uidoc(self):
        try:
            return __uidoc__
        except:
            uia = self.uiapp
            return uia.ActiveUIDocument if uia else None

    @property
    def doc(self):
        try:
            return __doc__
        except:
            udoc = self.uidoc
            return udoc.Document if udoc else None

    @property
    def version(self):
        a = self.app
        return a.VersionNumber if a else "2026"

    def is_newer_than(self, ver):
        try:
            return int(self.version) > int(ver)
        except:
            return True

    def post_command(self, cmd_id):
        try:
            c = UI.RevitCommandId.LookupCommandId(cmd_id)
            if c and self.uiapp and self.uiapp.CanPostCommand(c):
                self.uiapp.PostCommand(c)
        except:
            pass

class _ExecutorParams(object):
    @property
    def engine_ver(self):
        return "IPY342"

    @property
    def script_runtime(self):
        return None

HOST_APP = _HostApplication()
EXEC_PARAMS = _ExecutorParams()

from . import revit
from . import script
from . import forms
