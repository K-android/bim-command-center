BIM COMMAND CENTER - CENTRAL C# ADD-INS & EXTENSIONS GUIDE
============================================================

BIM Command Center supports true "Zero-Install" C# add-in execution.
Add-in DLLs placed in this central folder are instantly accessible to your whole
team without needing local workstation installation, MSI setups, or Revit restarts!

HOW TO ORGANIZE YOUR ADD-INS:
-----------------------------

OPTION A: Single Standalone Add-in (.dll)
  Drop your compiled tool DLL directly here or in a subfolder:
  Example:
    Custom Addins\
      AutoTagRooms.dll
      AlignViews.dll

OPTION B: Multi-DLL Add-in (With NuGet / Sibling Dependencies)
  Create a dedicated subfolder for the add-in. Keep the main command DLL
  at the top of the folder, and place dependency DLLs (e.g. Newtonsoft.Json.dll,
  RestSharp.dll, etc.) in the same folder or in a 'lib' / 'packages' subfolder:
  Example:
    Custom Addins\
      RoomFinishes\
        RoomFinishes.dll          <- Main Command DLL
        Newtonsoft.Json.dll       <- Dependency (automatically resolved, hidden from catalog)
        RestSharp.dll             <- Dependency (automatically resolved, hidden from catalog)

OPTION C: Standard .addin Manifest Package
  Drop your add-in's .addin manifest and its DLLs together:
  Example:
    Custom Addins\
      BatchExporter\
        BatchExporter.addin       <- Manifest defining <AddIn Type="Command">
        BatchExporter.dll         <- Entry Assembly
        lib\
          HelperLibrary.dll       <- Secondary DLL
  BIM Command Center will read the .addin file, parse each <AddIn Type="Command">,
  and present each command with its exact name and description!

HOW RESOLUTION WORKS AT RUNTIME:
--------------------------------
1. Dynamic Assembly Resolution: When an add-in command executes, BIM Command Center
   automatically resolves sibling dependencies from the add-in's directory, 'lib',
   'packages', 'deps', and 'runtimes' folders on-the-fly.
2. Filtered Catalog: Only the actual commands are listed in the user's tool menu;
   support and runtime DLLs are automatically kept hidden.
3. Live Revit Execution: Commands execute directly against the active Revit document
   and UI application just like native ribbon buttons.
