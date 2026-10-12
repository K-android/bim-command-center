# BIM Command Center — Installer & Deployment Guide

## 📦 What's Included

The standalone setup file **`BIMCommandCenter_Setup_v1.0.0.exe`** (2.7 MB) is ready to distribute to users across the company.

### Supported Revit Versions:
- **Autodesk Revit 2026** (.NET 8.0)
- **Autodesk Revit 2025** (.NET 8.0)
- **Autodesk Revit 2024** (.NET Framework 4.8)
- **Autodesk Revit 2023** (.NET Framework 4.8)

---

## 🚀 How to Install

### Method 1: Standard User Installation (Zero Admin Rights Needed)
1. Double-click **`BIMCommandCenter_Setup_v1.0.0.exe`**.
2. Select your installed Revit version(s) (or keep all checked).
3. Click **Next** $\rightarrow$ **Install**.
4. Launch Revit. You will see a new **`BIM Tools`** tab on the Revit Ribbon with a **`Command Center`** button!

> **Note:** The installer runs with `PrivilegesRequired=lowest`. It installs into `%LocalAppData%\BIMCommandCenter` and registers the `.addin` manifest in `%AppData%\Autodesk\Revit\Addins\<Year>`, so **users never need Windows Administrator privileges**.

### Method 2: Enterprise / IT Mass Push (Intune / SCCM / GPO)
IT administrators can deploy the installer silently in the background:
```cmd
BIMCommandCenter_Setup_v1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

To silently uninstall:
```cmd
"%LocalAppData%\BIMCommandCenter\uninstall\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES
```

---

## 🔗 Connecting the Central SharePoint / Network Catalog

To connect the company SharePoint Excel or List:

1. Open `%AppData%\BIMCommandCenter\config.json` (or edit `config.json` in the install directory).
2. Point `catalogSource` to the synced SharePoint path:
```json
{
  "catalogSource": "C:\\Users\\%USERNAME%\\CompanySharePoint\\BIM\\BIM_ToolCatalog.xlsx",
  "catalogSheet": "Tools",
  "scriptsRoot": "C:\\Users\\%USERNAME%\\CompanySharePoint\\BIM\\Scripts",
  "ideasEndpoint": "https://prod-xx.westus.logic.azure.com/workflows/...",
  "updateSource": "\\\\company-server\\BIMTools\\Updates",
  "cacheMinutes": 30
}
```

---

## 🔄 Zero-Reinstall Central Updates

Because of the **Loader + Core** architecture:
- The installer places a tiny `BIMCommandCenter.Loader.dll` once.
- When you build an update to `BIMCommandCenter.Core.dll` or the frontend, simply drop the new build folder into your central network share (specified in `updateSource`).
- When users launch Revit, the Loader checks the network share, copies the update locally in <3 seconds without file locking, and loads it. **Users never have to re-run an installer for feature or script updates!**
