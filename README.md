# BIM Command Center & Computational Dispatcher

[![Revit](https://img.shields.io/badge/Revit-2023%20|%202024%20|%202025%20|%202026-blue.svg)](https://autodesk.com/revit)
[![Rhino](https://img.shields.io/badge/Rhino-7%20|%208%20(Grasshopper)-purple.svg)](https://rhino3d.com)
[![Architecture](https://img.shields.io/badge/Architecture-4--Tier%20Decoupled-emerald.svg)](#architecture)
[![Installer](https://img.shields.io/badge/Installer-5.9%20MB%20Zero--Admin-amber.svg)](dist/BIMCommandCenter_Setup_v1.0.0.exe)

Enterprise-grade, scalable universal tool dispatcher and computational catalog for Autodesk Revit (and McNeel Rhino / Grasshopper). Features a modern Edge WebView2 user interface, cloud-governed SharePoint catalog schema, and an interactive multimodal AI Copilot.

---

## 🌟 Key Features

1. **"What are we solving today?" Intent Search:**
   - Instant search across firm automations (Dynamo graphs, pyRevit scripts, native add-ins, Revit postable commands).
2. **Multi-Engine BIM AI Copilot (⚙️ Settings UI):**
   - **Google Gemini**: Multimodal viewport inspection (`Document.ExportImage`), PDF ingestion, and reasoning across Gemini 2.5 Flash, 2.5 Pro, 3.8 Flash, and 3.6 Flash.
   - **Groq**: Ultra-fast responses powered by `llama-3.3-70b-versatile`.
   - **Local AI / Ollama**: Air-gapped, zero-cost local LLM inference via `http://localhost:11434/v1`.
   - **Interactive Execution**: Copilot recommends tools conversationally with 1-click `[▶ Yes, execute now]` buttons.
3. **Unified Central Scripts Library (`ScriptsRoot`):**
   - Dynamic discovery and automated categorization for `.dyn`, `.py`, `.dll`, `.addin`, `.gh`, and Windows `.lnk` shortcuts.
   - Zero-install execution: team members run shared scripts directly from a network UNC path or OneDrive folder without local setup.
4. **Zero-Install pyRevit & Python Engine:**
   - Bundles an embedded in-process IronPython 3.4.2 engine with pyRevit compatibility shims (`revit`, `script`, `forms`, `DB`, `UI`).
   - Supports pyRevit `.pushbutton` bundles, `bundle.yaml` metadata, and private `lib/` modules without requiring pyRevit installed on client PCs.
5. **Multi-DLL Native C# Add-in Dispatcher:**
   - Executes compiled `.dll` tools with full runtime dependency resolution (`lib/`, `packages/`, `deps/`).
   - Reads standard `.addin` manifests to discover commands while keeping secondary dependency DLLs hidden from the catalog.
6. **Deep Dynamo Inspection & Mini-Player:**
   - Automatically parses `.dyn` JSON trees to extract inputs (elements, sliders, dropdowns, strings, booleans) into the mini-player modal even if "Is Input" was never toggled in Dynamo.
7. **Role-Based Governance & Discipline Filtering:**
   - Central `governance.json` permission matrix enforced in real-time (Roles: Admin, Coordinator, SeniorModeler, JuniorModeler; Disciplines: Architecture, Structure, MEP, Computational, All).
   - Seamless offline caching (`governance_cache.json`) ensures scripts run when disconnected from the office network.
   - Multi-user concurrency-safe telemetry logging (`runs_{username}.jsonl`).
8. **Decoupled 4-Tier Architecture & Zero File-Locking:**
   - Thin Native Loader (`%LocalAppData%`) + Staged Core Runner + Embedded WebView2 Frontend + Central SharePoint/Network Brain.
   - Core updates deploy seamlessly without requiring Revit restarts or admin privileges.
9. **Zero-Privilege Single-File Installer:**
   - `dist/BIMCommandCenter_Setup_v1.0.0.exe` (~5.9 MB) installs for any user without IT administrator rights.

---

## 📂 Folder Structures (Central Library & Workstations)

BIM Command Center is engineered with a strict 3-dimension folder architecture:
1. **Central Scripts Library (`ScriptsRoot`)**: The shared central repository (UNC share or OneDrive sync) managed by BIM managers.
2. **Local Client Workstation**: The isolated, auto-updating local runner on every modeler's PC.
3. **Developer Source Repository**: The C# / .NET / WebView2 source codebase.

---

### 1. Central Scripts Library Structure (`ScriptsRoot`)

The central scripts folder (configured in Revit via **⚙️ Settings $\rightarrow$ Central Scripts Folder** or `%AppData%\BIMCommandCenter\config.json`) is scanned dynamically whenever Revit starts or when the user clicks **Refresh (↻)**.

```text
📁 \\server\BIM_Share\CommandCenter\scripts\   (or %AppData%\BIMCommandCenter\scripts)
│
├── 📄 governance.json                         # User authentication & role/discipline permissions matrix
├── 📄 governance.json.backup                  # Automated backup of governance rules
│
├── 📁 Dynamo\                                 # Dynamo Visual Programming Library
│   ├── 📄 Renumber_Doors.dyn                  # Graph file (auto-inspected for inputs & dependencies)
│   ├── 📄 Renumber_Doors.desc.txt             # (Optional) Sidecar plain-text tool description
│   ├── 📄 Wall_Finish_Creator.dyn
│   ├── 📄 Wall_Finish_Creator.json            # (Optional) Sidecar JSON with safetyLevel, author, packages
│   ├── 📄 Auto_Dimension_Grids.dyn
│   └── 📄 Deprecated_Script.dyn.disabled      # Appending '.disabled' instantly hides tool from all users
│
├── 📁 PyRevit\                                # pyRevit PushButtons & Standalone Python Scripts
│   ├── 📄 Audit_Unplaced_Rooms.py             # Standalone Python script (runs via embedded IronPython)
│   ├── 📄 Purge_CAD_Imports.py
│   │
│   ├── 📁 DoorRenumber.pushbutton\            # Standard pyRevit PushButton bundle
│   │   ├── 📄 script.py                       # Main execution entry point
│   │   ├── 📄 bundle.yaml                     # Metadata (title, tooltip/description, author)
│   │   ├── 🖼️ icon.png                        # Button icon displayed in Command Center UI
│   │   └── 📁 lib\                            # Private helper modules (automatically hidden from catalog)
│   │       ├── 📄 renumber_engine.py
│   │       └── 📄 __init__.py
│   │
│   ├── 📁 SheetAligner.pushbutton\            # Another pyRevit button bundle
│   │   ├── 📄 script.py
│   │   └── 📄 bundle.yaml
│   │
│   └── 📁 lib\                                # Shared Python modules accessible to ALL pyRevit scripts
│       ├── 📄 bim_utils.py                    # Imported via `import bim_utils` without extra path setup
│       └── 📄 excel_bridge.py
│
├── 📁 Custom Addins\                          # Compiled C# Revit Add-ins (.NET Assemblies)
│   ├── 📄 AlignViews.dll                      # Standalone single-assembly command
│   ├── 📄 QuickSection.lnk                    # Windows shortcut pointing to a network or local DLL
│   │
│   ├── 📁 RoomFinishes\                       # Multi-DLL add-in with third-party dependencies
│   │   ├── 📄 RoomFinishes.dll                # Entry command assembly (implements IExternalCommand)
│   │   ├── 📄 Newtonsoft.Json.dll             # Dependency (auto-resolved at runtime, hidden from UI)
│   │   ├── 📄 RestSharp.dll                   # Dependency (auto-resolved at runtime, hidden from UI)
│   │   └── 📁 lib\                            # Additional dependency assemblies
│   │       └── 📄 GeometryHelpers.dll
│   │
│   └── 📁 BatchSheetManager\                  # Manifest-driven add-in package
│       ├── 📄 BatchSheetManager.addin         # Standard Revit manifest defining commands & classes
│       ├── 📄 BatchSheetManager.dll           # Entry assembly referenced in manifest
│       └── 📄 SheetHelper.dll                 # Secondary assembly
│
├── 📁 Grasshopper\                            # Computational Geometry Definitions
│   ├── 📄 Facade_Panel_Optimization.gh        # Rhino.Inside.Revit Grasshopper definition
│   └── 📄 Canopy_Generative_Form.ghx          # XML Grasshopper definition
│
├── 📁 lib\                                    # Central shared Python/C# libraries (added to sys.path)
│   └── 📄 company_standards.py
│
└── 📁 telemetry\                              # Concurrency-Safe Execution Audit Logs
    ├── 📄 runs_rohan.verma.jsonl              # Modeler audit log (timestamp, toolId, status, duration)
    ├── 📄 runs_priya.sharma.jsonl
    └── 📄 runs_john.doe.jsonl
```

#### How the Central Library Works:

| Engine / File Type | Organization & File Naming | Metadata & Descriptions | Runtime Resolution Behavior |
| :--- | :--- | :--- | :--- |
| **Dynamo (`.dyn`)** | Drop `.dyn` files into `Dynamo\` or any category subfolder (e.g. `Architecture\`). | Deep inspection reads node JSON: extracts inputs (element pickers, sliders, dropdowns, booleans) + companion `{name}.desc.txt` or `{name}.json`. | Runs in Dynamo's automation pipeline; values entered in Mini-Player are injected directly into graph nodes. |
| **pyRevit (`.pushbutton`)** | Place `DoorRenumber.pushbutton` folders in `PyRevit\`. Contains `script.py`, `bundle.yaml`, and optional `lib\`. | Clean title extracted from folder name (`Door Renumber`) or `title:` in `bundle.yaml`. Docstrings and `tooltip:` become descriptions. | **Zero-install:** runs in-process via embedded IronPython 3.4.2 with built-in shims (`pyrevit`, `DB`, `UI`). Private `lib\` added to `sys.path`. |
| **Loose Python (`.py`)** | Place standalone `.py` scripts in `PyRevit\` or subfolders. | Parses docstrings (`"""..."""`) and `__doc__`. Helper files in `lib/` or starting with `_` are hidden. | Standalone execution with `__revit__`, `__doc__`, and stdout/stderr capture redirected to the UI. |
| **C# Add-ins (`.dll`)** | Place single `.dll` or multi-DLL folders in `Custom Addins\`. Supports `.addin` manifests. | Assembly version info + companion `.desc.txt` or manifest `<Description>`. | **Dynamic Assembly Resolution:** Sibling DLLs and DLLs inside `lib\`, `packages\`, `deps\`, and `runtimes\` are resolved automatically without GAC or installer. |
| **Disabling Tools** | Rename file or folder with `.disabled` (e.g., `OldScript.dyn.disabled`). | N/A | Excluded instantly from scanning; hidden across all workstations immediately upon refresh. |

---

### 2. Local Client Workstation Architecture

When the zero-admin installer (`BIMCommandCenter_Setup_v1.0.0.exe`) runs on a modeler's workstation, it deploys a lightweight, decoupled runner structure:

```text
📁 C:\Users\<Username>\
│
├── 📁 AppData\Roaming\
│   ├── 📁 Autodesk\Revit\Addins\<Year>\       # Standard Revit Add-in Discovery Folder
│   │   └── 📄 BIMCommandCenter.addin          # Thin manifest pointing to Tier 1 Loader
│   │
│   └── 📁 BIMCommandCenter\                   # User Configuration & Local Defaults
│       ├── 📄 config.json                     # Active settings (scriptsRoot, aiEngine, apiKey, etc.)
│       └── 📁 scripts\                        # Local fallback library (if central network share is unset)
│
└── 📁 AppData\Local\BIMCommandCenter\         # Runtime Engine & Isolated Cache
    │
    ├── 📄 session.json                        # Persisted user session (when "Remember me" is checked)
    ├── 📄 governance_cache.json               # Local offline cache of governance.json rules
    ├── 📄 catalog_cache.json                  # Local offline cache of available catalog tools
    ├── 📄 command_center.log                  # Rolling diagnostic execution log
    │
    └── 📁 Revit<Year>\                        # Version-isolated runner (e.g. Revit2026)
        ├── 📄 BIMCommandCenter.Loader.dll     # Tier 1 Loader (loaded once by Revit, never locks)
        │
        └── 📁 core\                           # Tier 2 Dynamic Runner (updated without restarting Revit)
            ├── 📄 BIMCommandCenter.Core.dll   # Main engine, dockable pane, catalog & dispatcher
            ├── 📄 IronPython.dll              # Embedded IronPython 3.4.2 runtime
            ├── 📄 IronPython.Modules.dll
            ├── 📄 Microsoft.Scripting.dll
            ├── 📄 Microsoft.Dynamic.dll
            │
            ├── 📁 pylib\                      # Zero-install pyRevit Compatibility Shims
            │   └── 📁 pyrevit\
            │       ├── 📄 __init__.py         # pyrevit package marker
            │       ├── 📄 revit.py            # pyrevit.revit (doc, uidoc, Transaction, DB, UI)
            │       ├── 📄 script.py           # pyrevit.script (get_output, get_logger, output window)
            │       └── 📄 forms.py            # pyrevit.forms (alert, ask_for_string, select_views)
            │
            ├── 📁 frontend\                   # Tier 3 Embedded WebView2 User Interface
            │   ├── 📄 index.html              # Single-page modern UI with Login Gate & AI Chat
            │   └── 📁 vendor\                 # Offline Tailwind CSS & Lucide icon assets
            │
            └── 📁 tools\                      # Pre-bundled native add-ins
                └── 📄 BIMHealthCheck.dll      # Built-in health check audit engine
```

#### Why this workstation separation matters:
1. **Zero Admin Privileges Required:** All files reside strictly within `%LocalAppData%` and `%AppData%`. No `C:\Program Files` access or Windows admin prompts required.
2. **Network Resilience & Offline Fallback:** If a modeler works offline or loses VPN access to the corporate server, `governance_cache.json` and `catalog_cache.json` allow them to keep working with full role-based security.
3. **No Revit File-Locking:** The Tier 1 loader loads `core/` dynamically into memory or isolated staging folders, allowing seamless core updates without forcing all users to exit Revit.

---

### 3. Developer Source Repository Structure

```text
📁 revit-command-center\
├── 📁 src/
│   ├── 📄 Directory.Build.props               # Multi-target build properties (net48 & net8.0-windows)
│   │
│   ├── 📁 BIMCommandCenter.Loader/            # Tier 1 Thin Entry Point
│   │   ├── 📄 BIMCommandCenter.Loader.csproj
│   │   └── 📄 LoaderApp.cs                   # Staged reflection loader & auto-updater
│   │
│   └── 📁 BIMCommandCenter.Core/              # Tier 2 Dynamic Runner & Dockable Pane
│       ├── 📄 BIMCommandCenter.Core.csproj    # References IronPython 3.4.2 & Revit API
│       ├── 📄 CoreApp.cs                     # Revit ribbon tab, button & dockable pane registration
│       │
│       ├── 📁 Catalog/                       # Catalog Services & Discovery
│       │   ├── 📄 CatalogService.cs          # Multi-source loader (scriptsRoot, SharePoint, Excel, JSON)
│       │   └── 📄 XlsxReader.cs              # Native zero-dependency Excel reader
│       │
│       ├── 📁 Security/                      # Governance & Authentication
│       │   └── 📄 GovernanceService.cs       # PIN authentication, role/discipline access & offline cache
│       │
│       ├── 📁 Revit/                         # Execution & Engine Dispatchers
│       │   ├── 📄 RevitDispatcher.cs         # ExternalEvent thread synchronizer
│       │   ├── 📄 ToolRunner.cs              # Universal runner (Dynamo, pyRevit, Add-ins, Postable)
│       │   ├── 📄 DynamoRunner.cs            # Dynamo API automation & input parameter injection
│       │   ├── 📄 DynamoPackageInspector.cs  # Deep JSON parser for Dynamo inputs & dependencies
│       │   ├── 📄 PythonRunner.cs            # Embedded IronPython runner & search path configurator
│       │   └── 📄 RevitContext.cs            # Thread-safe UI & DB context accessor
│       │
│       ├── 📁 UI/                            # Presentation & Edge WebView2 Host
│       │   ├── 📄 CommandCenterPane.cs       # Dockable pane host wrapping WebView2
│       │   └── 📄 Bridge.cs                  # Two-way async JSON bridge (Revit <-> JavaScript)
│       │
│       ├── 📁 Infrastructure/                # Logging, Config & Utilities
│       │   ├── 📄 AppConfig.cs               # Settings manager (%AppData%\BIMCommandCenter\config.json)
│       │   ├── 📄 Json.cs                    # High-performance internal JSON serializer
│       │   └── 📄 Log.cs                     # Rolling diagnostic file logger
│       │
│       └── 📁 pylib/pyrevit/                 # Bundled pyRevit compatibility shims
│           ├── 📄 __init__.py
│           ├── 📄 revit.py
│           ├── 📄 script.py
│           └── 📄 forms.py
│
├── 📁 frontend/                              # Tier 3 WebView2 Web Application
│   ├── 📄 index.html                         # Unified UI: Intent Search, Mini-Player, Login Gate, AI Copilot
│   └── 📁 vendor/                            # 100% offline styling and icon assets
│
├── 📁 scripts_template/                      # Starter template for Central Scripts Library (ScriptsRoot)
│   ├── 📄 governance.json                    # Blueprint role and permission database
│   ├── 📁 Dynamo/                            # Sample Dynamo graphs
│   ├── 📁 PyRevit/                           # Sample pyRevit scripts & pushbutton guides
│   ├── 📁 Custom Addins/                     # Sample add-in instructions & folder structures
│   └── 📁 Grasshopper/                       # Sample Grasshopper definitions
│
├── 📁 tools/                                 # Intrinsic pre-compiled add-in binaries
│   ├── 📁 2023/                              # BIMHealthCheck.dll (.NET Framework 4.8)
│   ├── 📁 2024/                              # BIMHealthCheck.dll (.NET Framework 4.8)
│   ├── 📁 2025/                              # BIMHealthCheck.dll (.NET 8.0-windows)
│   └── 📁 2026/                              # BIMHealthCheck.dll (.NET 8.0-windows)
│
├── 📁 installer/
│   └── 📄 BIMCommandCenterSetup.iss          # Inno Setup 6 packaging script
│
└── 📁 dist/                                  # Production distribution build artifacts
    ├── 📄 BIMCommandCenter_Setup_v1.0.0.exe  # Standalone installer (~5.9 MB)
    └── 📄 BIM_Command_Center_Scalability_and_Rhino_GH_Architecture.pdf

---

## ⚙️ AI Engine & Central Scripts Configuration

Click the **⚙️ Settings** icon in the top right of the BIM Command Center panel in Revit:

### 1. Select Your AI Engine
- **Google Gemini**: Enter your free API key from [Google AI Studio](https://aistudio.google.com/app/apikey). Supports multimodal analysis of active Revit viewports with Gemini 2.5 Flash, Gemini 2.5 Pro, Gemini 3.8 Flash, and Gemini 3.6 Flash.
- **Groq**: Enter your key from [Groq Console](https://console.groq.com/keys) for ultra-low latency LLaMA inference (`llama-3.3-70b-versatile`).
- **Local AI (Ollama)**: Connects to `http://localhost:11434/v1` for 100% offline, air-gapped model execution.
- Click **"Test Connection"** to verify API validity before saving.

### 2. Manage Your Central Library
- Click **"Open Folder in Explorer"** to immediately open `%AppData%\BIMCommandCenter\scripts`.
- Drop any `.dyn`, `.py`, `.dll`, or Windows shortcut (`.lnk`) into the `Dynamo\`, `PyRevit\`, or `Custom Addins\` folders.
- Hit the **Refresh (↻)** button in BIM Command Center, and your scripts appear instantly in the library!


## 🚀 Quick Start & Installation

### Option 1: Run the Standalone Installer
Run `dist/BIMCommandCenter_Setup_v1.0.0.exe`.  
Follow the wizard to select your installed Revit versions. No admin privileges required!

### Option 2: Enterprise IT Mass Push
```cmd
BIMCommandCenter_Setup_v1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

---

## 🛠️ Building from Source

Prerequisites: [.NET SDK (8.0+)](https://dotnet.microsoft.com/) and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
# Build for Revit 2026 (.NET 8.0)
dotnet build src/BIMCommandCenter.Loader/BIMCommandCenter.Loader.csproj -c Release -p:RevitVersion=2026
dotnet build src/BIMCommandCenter.Core/BIMCommandCenter.Core.csproj -c Release -p:RevitVersion=2026

# Compile the installer executable
& "ISCC.exe" installer/BIMCommandCenterSetup.iss
```

---

## 📄 Documentation & Whitepapers

- **[Architecture Specification PDF](BIM_Command_Center_Architecture_Specification.pdf)**: Comprehensive schemas, IPC protocols, and execution models.
- **[Scalability & Rhino/Grasshopper PDF](dist/BIM_Command_Center_Scalability_and_Rhino_GH_Architecture.pdf)**: Enterprise scalability proof and cross-platform architecture.
