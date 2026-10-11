# BIM Command Center & Computational Dispatcher

[![Revit](https://img.shields.io/badge/Revit-2023%20|%202024%20|%202025%20|%202026-blue.svg)](https://autodesk.com/revit)
[![Rhino](https://img.shields.io/badge/Rhino-7%20|%208%20(Grasshopper)-purple.svg)](https://rhino3d.com)
[![Architecture](https://img.shields.io/badge/Architecture-4--Tier%20Decoupled-emerald.svg)](#architecture)
[![Installer](https://img.shields.io/badge/Installer-2.7%20MB%20Zero--Admin-amber.svg)](dist/BIMCommandCenter_Setup_v1.0.0.exe)

Enterprise-grade, scalable universal tool dispatcher and computational catalog for Autodesk Revit (and McNeel Rhino / Grasshopper). Features a modern Edge WebView2 user interface, cloud-governed SharePoint catalog schema, and an interactive multimodal AI Copilot.

---

## 🌟 Key Features

1. **"What are we solving today?" Intent Search:**
   - Instant search across firm automations (Dynamo graphs, pyRevit scripts, native add-ins, Revit postable commands).
2. **Interactive BIM Copilot:**
   - Detects user intent conversationally, highlights prerequisites, and asks: *"Should I execute this script for you?"* with 1-click execution.
   - Captures active Revit viewports (`Document.ExportImage`) and ingests PDF specifications for multimodal context.
3. **Decoupled 4-Tier Architecture:**
   - Thin Native Loader (`%LocalAppData%`) + Staged Core Runner + Embedded WebView2 Frontend + Central SharePoint Brain.
   - **Zero file-locking on network shares:** Releases update without requiring users to close Revit.
4. **Zero-Privilege Single-File Installer:**
   - `dist/BIMCommandCenter_Setup_v1.0.0.exe` (2.7 MB) installs for any user without IT administrator rights.
5. **Cross-Platform Extensibility:**
   - Architected to share the exact same UI and SharePoint catalog with McNeel Rhino 7 & 8 (Grasshopper).

---

## 📁 Repository Structure

```text
├── src/
│   ├── Directory.Build.props                  # Multi-target build props (net48 / net8.0-windows)
│   ├── BIMCommandCenter.Loader/              # Immutable entry point (Tier 1)
│   │   ├── BIMCommandCenter.Loader.csproj
│   │   └── LoaderApp.cs                      # Staged auto-pull from central update share
│   └── BIMCommandCenter.Core/                # Dynamic runner & dockable pane (Tier 2)
│       ├── BIMCommandCenter.Core.csproj
│       ├── CoreApp.cs                        # Ribbon creation & dockable pane registration
│       ├── Catalog/                          # XlsxReader & CatalogService (Graph/Excel)
│       ├── Ideas/                            # IdeaService (SharePoint Backlog)
│       ├── Infrastructure/                   # AppConfig, Json helper, Log
│       ├── Revit/                            # RevitDispatcher, ToolRunner, RevitContext
│       └── UI/                               # CommandCenterPane (WebView2) & Bridge
├── frontend/                                 # Embedded Web UI (Tier 3)
│   ├── index.html                            # Standalone & WebView2 application
│   └── vendor/                               # Offline assets (tailwind.js, lucide.min.js)
├── installer/
│   └── BIMCommandCenterSetup.iss             # Inno Setup 6 compilation script
├── dist/                                     # Production distribution artifacts
│   ├── BIMCommandCenter_Setup_v1.0.0.exe     # Standalone installer (2.7 MB)
│   └── BIM_Command_Center_Scalability_and_Rhino_GH_Architecture.pdf
├── bridge/                                   # pyRevit & C# reference bridges
│   ├── pyrevit_script.py
│   └── RevitBridgeHandler.cs
├── config/
│   └── config.json                           # Central runtime configuration
├── dummy_catalog.json                        # Blueprint catalog dataset for SharePoint
├── dummy_ideas.json                          # Blueprint backlog dataset for SharePoint
├── BIM_Command_Center_Architecture_Specification.pdf
└── README_INSTALLER.md                       # Deployment guide for users & IT
```

---

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
