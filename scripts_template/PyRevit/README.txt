BIM COMMAND CENTER - CENTRAL PYREVIT SCRIPTS & PACKAGES GUIDE
==============================================================

BIM Command Center automatically discovers and runs pyRevit tools directly from this
central folder or from your loaded pyRevit ribbon extensions.

HOW TO ORGANIZE PYREVIT SCRIPTS WITH 'LIB' FILES:
------------------------------------------------

OPTION A: Single Standalone Script (.py)
  Drop standalone `.py` scripts directly into this folder or a subfolder:
  Example:
    PyRevit\
      Audit_Unplaced_Rooms.py
      Purge_CAD_Imports.py

OPTION B: Multi-file Script with 'lib' Dependencies
  Place helper modules in a `lib` or `packages` subfolder next to your main script:
  Example:
    PyRevit\
      RenumberDoors\
        RenumberDoors.py          <- Main entry script
        lib\
          excel_reader.py         <- Helper module (automatically hidden from catalog)
          room_geometry.py        <- Helper module (automatically hidden from catalog)
          __init__.py             <- Package marker (automatically hidden from catalog)
  
  In your main script, you can import helpers directly:
    import sys, os
    sys.path.append(os.path.join(os.path.dirname(__file__), 'lib'))
    import excel_reader

OPTION C: Standard pyRevit PushButton Bundle (.pushbutton)
  Drop standard pyRevit button folders directly here:
  Example:
    PyRevit\
      DoorRenumber.pushbutton\
        script.py                 <- Main script (catalog automatically names tool "Door Renumber")
        bundle.yaml               <- Metadata: title, tooltip, author
        icon.png                  <- Button icon
        lib\                      <- Private helper modules (automatically hidden from catalog)
          renumber_core.py

HOW RESOLUTION WORKS:
---------------------
1. Clean Catalog Filtering: Any `.py` files inside `lib\`, `libs\`, `packages\`, `deps\`,
   or folders starting with `_` (and any `__init__.py` files) are automatically hidden
   from the catalog. Only the actual runnable tool is presented.
2. Pushbutton Title Derivation: If a script is named `script.py`, BIM Command Center derives
   the tool title from the folder name (e.g. `DoorRenumber.pushbutton` -> `Door Renumber`),
   and parses `bundle.yaml` for exact title and tooltip descriptions.
3. Seamless Execution: If pyRevit is installed on the user's workstation, BIM Command Center
   executes the button directly in the active Revit session.
