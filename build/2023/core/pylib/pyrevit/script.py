# -*- coding: utf-8 -*-
"""
pyrevit.script compatibility module
"""
import sys

class _Output(object):
    def print_md(self, text):
        print(text)

    def print_html(self, html):
        print(html)

    def print_code(self, code):
        print(code)

    def set_title(self, title):
        pass

    def close(self):
        pass

_out = _Output()

def get_output():
    return _out

def get_logger():
    import logging
    return logging.getLogger("BIMCommandCenter")

def get_document():
    from . import revit
    return revit.doc

def exit():
    sys.exit(0)
