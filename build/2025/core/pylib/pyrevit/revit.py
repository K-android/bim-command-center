# -*- coding: utf-8 -*-
"""
pyrevit.revit compatibility module for BIM Command Center
"""
import sys
import types
import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')
import Autodesk.Revit.DB as DB
import Autodesk.Revit.UI as UI

def get_doc():
    try:
        return __doc__
    except:
        pass
    try:
        return __revit__.ActiveUIDocument.Document
    except:
        return None

def get_uidoc():
    try:
        return __uidoc__
    except:
        pass
    try:
        return __revit__.ActiveUIDocument
    except:
        return None

def get_app():
    try:
        return __revit__.Application
    except:
        return None

def get_uiapp():
    try:
        return __revit__
    except:
        return None

def get_selection():
    ud = get_uidoc()
    d = get_doc()
    if ud and d:
        return [d.GetElement(id) for id in ud.Selection.GetElementIds()]
    return []

class _TransactionContext(object):
    def __init__(self, doc_or_name=None, name=None):
        if isinstance(doc_or_name, str):
            self.name = doc_or_name
            self.doc = get_doc()
        elif doc_or_name is not None and not isinstance(doc_or_name, str):
            self.doc = doc_or_name
            self.name = name or "BIM Command Center Transaction"
        else:
            self.doc = get_doc()
            self.name = name or "BIM Command Center Transaction"
        self.t = None

    def Start(self):
        if self.doc and (self.t is None or not self.t.HasStarted()):
            self.t = DB.Transaction(self.doc, self.name)
            self.t.Start()
        return self.t

    def Commit(self):
        if self.t and self.t.HasStarted() and not self.t.HasEnded():
            res = self.t.Commit()
            return res
        return None

    def RollBack(self):
        if self.t and self.t.HasStarted() and not self.t.HasEnded():
            res = self.t.RollBack()
            return res
        return None

    def __enter__(self):
        self.Start()
        return self.t

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.t and self.t.HasStarted() and not self.t.HasEnded():
            if exc_type is not None:
                self.t.RollBack()
            else:
                self.t.Commit()

def Transaction(doc_or_name=None, name=None):
    return _TransactionContext(doc_or_name, name)

class _TransactionGroupContext(object):
    def __init__(self, doc_or_name=None, name=None):
        if isinstance(doc_or_name, str):
            self.name = doc_or_name
            self.doc = get_doc()
        elif doc_or_name is not None and not isinstance(doc_or_name, str):
            self.doc = doc_or_name
            self.name = name or "BIM Command Center Group"
        else:
            self.doc = get_doc()
            self.name = name or "BIM Command Center Group"
        self.tg = None

    def Start(self):
        if self.doc and (self.tg is None or not self.tg.HasStarted()):
            self.tg = DB.TransactionGroup(self.doc, self.name)
            self.tg.Start()
        return self.tg

    def Assimilate(self):
        if self.tg and self.tg.HasStarted() and not self.tg.HasEnded():
            return self.tg.Assimilate()
        return None

    def RollBack(self):
        if self.tg and self.tg.HasStarted() and not self.tg.HasEnded():
            return self.tg.RollBack()
        return None

    def __enter__(self):
        self.Start()
        return self.tg

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.tg and self.tg.HasStarted() and not self.tg.HasEnded():
            if exc_type is not None:
                self.tg.RollBack()
            else:
                self.tg.Assimilate()

def TransactionGroup(doc_or_name=None, name=None):
    return _TransactionGroupContext(doc_or_name, name)

class ErrorSwallower(object):
    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        return True

class _RevitModule(types.ModuleType):
    @property
    def doc(self):
        return get_doc()

    @property
    def uidoc(self):
        return get_uidoc()

    @property
    def app(self):
        return get_app()

    @property
    def uiapp(self):
        return get_uiapp()

    @property
    def ACTIVE_REVIT_VERSION(self):
        a = get_app()
        try:
            return int(a.VersionNumber) if a else 2026
        except:
            return 2026

# Replace sys.modules entry with dynamic ModuleType
_current_mod = sys.modules[__name__]
_dyn_mod = _RevitModule(__name__)
_dyn_mod.__dict__.update(_current_mod.__dict__)
_dyn_mod.doc = property(lambda self: get_doc())
_dyn_mod.uidoc = property(lambda self: get_uidoc())
_dyn_mod.app = property(lambda self: get_app())
_dyn_mod.uiapp = property(lambda self: get_uiapp())
_dyn_mod.Transaction = Transaction
_dyn_mod.TransactionGroup = TransactionGroup
_dyn_mod.ErrorSwallower = ErrorSwallower
_dyn_mod.get_selection = get_selection
_dyn_mod.DB = DB
_dyn_mod.UI = UI

sys.modules[__name__] = _dyn_mod
