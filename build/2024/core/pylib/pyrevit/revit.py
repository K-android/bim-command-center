# -*- coding: utf-8 -*-
"""
pyrevit.revit compatibility module
"""
import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitAPIUI')
from Autodesk.Revit.DB import Transaction, TransactionGroup
import Autodesk.Revit.DB as DB
import Autodesk.Revit.UI as UI

def get_revit_instance():
    try:
        import __main__
        return getattr(__main__, '__revit__', None)
    except:
        return None

class _RevitWrapper(object):
    @property
    def app(self):
        r = get_revit_instance()
        return r.Application if r else None

    @property
    def uiapp(self):
        return get_revit_instance()

    @property
    def doc(self):
        r = get_revit_instance()
        return r.ActiveUIDocument.Document if r and r.ActiveUIDocument else None

    @property
    def uidoc(self):
        r = get_revit_instance()
        return r.ActiveUIDocument if r else None

    @property
    def ACTIVE_REVIT_VERSION(self):
        app = self.app
        try:
            return int(app.VersionNumber) if app else 2026
        except:
            return 2026

    def Transaction(self, doc=None, name="BIM Command Center Script"):
        if doc is None:
            doc = self.doc
        return _TransactionContext(doc, name)

    def TransactionGroup(self, doc=None, name="BIM Command Center Group"):
        if doc is None:
            doc = self.doc
        return _TransactionGroupContext(doc, name)

class _TransactionContext(object):
    def __init__(self, doc, name):
        self.doc = doc
        self.name = name
        self.t = None

    def __enter__(self):
        if self.doc:
            self.t = Transaction(self.doc, self.name)
            self.t.Start()
        return self.t

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.t:
            if exc_type is not None:
                self.t.RollBack()
            else:
                self.t.Commit()

class _TransactionGroupContext(object):
    def __init__(self, doc, name):
        self.doc = doc
        self.name = name
        self.tg = None

    def __enter__(self):
        if self.doc:
            self.tg = TransactionGroup(self.doc, self.name)
            self.tg.Start()
        return self.tg

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.tg:
            if exc_type is not None:
                self.tg.RollBack()
            else:
                self.tg.Assimilate()

_wrapper = _RevitWrapper()
doc = _wrapper.doc
uidoc = _wrapper.uidoc
app = _wrapper.app
uiapp = _wrapper.uiapp
ACTIVE_REVIT_VERSION = _wrapper.ACTIVE_REVIT_VERSION
Transaction = _wrapper.Transaction
TransactionGroup = _wrapper.TransactionGroup
