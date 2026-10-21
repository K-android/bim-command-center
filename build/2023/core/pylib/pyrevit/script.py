# -*- coding: utf-8 -*-
"""
pyrevit.script compatibility module for BIM Command Center
"""
import sys

class _Output(object):
    def print_md(self, text):
        print(text)

    def print_html(self, html):
        print(html)

    def print_code(self, code):
        print(code)

    def print_table(self, table_data, title='', columns=None, formats=None):
        if title:
            print("=== {} ===".format(title))
        if columns:
            print(" | ".join(str(c) for c in columns))
            print("-" * 50)
        for row in table_data:
            if isinstance(row, (list, tuple)):
                print(" | ".join(str(item) for item in row))
            else:
                print(str(row))

    def insert_divider(self):
        print("-" * 60)

    def set_title(self, title):
        pass

    def freeze(self):
        pass

    def unfreeze(self):
        pass

    def close(self):
        pass

    def save_contents(self, file_path):
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

class _DummyConfig(object):
    def __getattr__(self, name):
        return None
    def get_option(self, name, default=None):
        return default
    def set_option(self, name, value):
        pass

_cfg = _DummyConfig()

def get_config(section_name=None):
    return _cfg

def get_universal_custom_config(section_name=None):
    return _cfg

_data_store = {}

def store_data(key, data):
    _data_store[key] = data

def load_data(key):
    return _data_store.get(key)

def exit():
    sys.exit(0)
