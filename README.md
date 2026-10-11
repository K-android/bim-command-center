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
2. **Multi-Engine BIM AI Copilot (⚙️ Settings UI):**
   - **Google Gemini**: Multimodal viewport inspection (`Document.ExportImage`), PDF ingestion, and reasoning.
   - **Groq**: Ultra-fast responses powered by `llama-3.3-70b-versatile`.
   - **Local AI / Ollama**: Air-gapped, zero-cost local LLM inference via `http://localhost:11434/v1`.
   - **Interactive Execution**: Copilot recommends tools conversationally with 1-click `[▶ Yes, execute now]` buttons.
3. **Unified Central Scripts Library (`%AppData%\BIMCommandCenter\scripts`):**
   - Auto-discovers and categorizes `.dyn`, `.py`, `.dll`, and Windows `.lnk` shortcuts dynamically.
   - Pre-organized subfolders for `Dynamo\`, `PyRevit\`, and `Custom Addins\`.
   - "Open Folder in Explorer" button right from the in-Revit Settings modal.
4. **Pre-Integrated Native Add-ins & Zero-Install Deployment:**
   - Bundles **BIM Health Check** across Revit 2023–2026.
   - 3-tier lookup hierarchy (Central Shared Folder $\rightarrow$ Local User Addins $\rightarrow$ Bundled fallback) enables deploying new firm add-ins with zero user reinstallation.
5. **Decoupled 4-Tier Architecture:**
   - Thin Native Loader (`%LocalAppData%`) + Staged Core Runner + Embedded WebView2 Frontend + Central SharePoint Brain.
   - **Zero file-locking on network shares:** Releases update without requiring users to close Revit.
6. **Zero-Privilege Single-File Installer:**
   - `dist/BIMCommandCenter_Setup_v1.0.0.exe` (2.7 MB) installs for any user without IT administrator rights.
7. **Cross-Platform Extensibility:**
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
│   ├── index.html                            # Standalone & WebView2 application with Settings UI
│   └── vendor/                               # Offline assets (tailwind.js, lucide.min.js)
├── tools/                                    # Pre-bundled native add-ins
│   ├── 2023/                                 # BIMHealthCheck.dll (.NET Framework 4.8)
│   ├── 2024/                                 # BIMHealthCheck.dll (.NET Framework 4.8)
│   ├── 2025/                                 # BIMHealthCheck.dll (.NET 8.0-windows)
│   └── 2026/                                 # BIMHealthCheck.dll (.NET 8.0-windows)
├── scripts_template/                         # Initial central library structure
│   ├── Dynamo/                               # Directory for .dyn graphs
│   ├── PyRevit/                              # Directory for .py scripts
│   └── Custom Addins/                        # Directory for .dll / .lnk shortcuts
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
├── README_INSTALLER.md                       # Deployment guide for users & IT
└── SETTINGS_GUIDE.md                         # Guide for AI Keys, Central Folders & SharePoint
```

---

## ⚙️ AI Engine & Central Scripts Configuration

Click the **⚙️ Settings** icon in the top right of the BIM Command Center panel in Revit:

### 1. Select Your AI Engine
- **Google Gemini**: Enter your free API key from [Google AI Studio](https://aistudio.google.com/app/apikey). Supports multimodal analysis of active Revit viewports.
- **Groq**: Enter your key from [Groq Console](https://console.groq.com/keys) for ultra-low latency LLaMA inference.
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
