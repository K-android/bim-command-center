# BIM Command Center - Settings & Connection Guide

Configure your AI Engines, Central Scripts Library, and SharePoint data connection points.

---

## ⚙️ Option 1: Configure Directly Inside Revit (Recommended)

You don't need to touch JSON files manually! You can configure everything directly inside Revit:

1. Open Autodesk Revit and open the **BIM Command Center** panel.
2. Click the **⚙️ Settings** icon in the top right header.
3. Configure your preferences:
   - **AI Provider**: Choose between **Google Gemini**, **Groq**, or **Local AI (Ollama)**.
   - **API Key**: Enter your key and click **Test Connection** (turns green when verified).
   - **Central Scripts Directory**: Shows your current central library path.
   - **Open Folder in Explorer**: Click to immediately open your scripts folder on your desktop.
4. Click **Save Settings**. Your settings are automatically saved to `%AppData%\BIMCommandCenter\config.json` and persistent browser storage!

---

## 📁 Organizing Your Central Scripts Library

Your default central folder is located at:
```text
%AppData%\BIMCommandCenter\scripts\
```

It is pre-organized into three categorized subfolders:
- **`Dynamo\`**: Drop `.dyn` graphs here. Appears under category **"Dynamo"**.
- **`PyRevit\`**: Drop `.py` Python scripts here. Appears under category **"PyRevit"**.
- **`Custom Addins\`**: Drop compiled `.dll` assemblies or Windows `.lnk` shortcuts here. Appears under category **"Custom Addins"**.

> **Tip - Target Shortcut Links**: Instead of copying large projects or code repositories, simply right-click your compiled `.dll` $\rightarrow$ *Create shortcut* $\rightarrow$ drop the `.lnk` into `Custom Addins\`. BIM Command Center will automatically resolve the target and run it directly!

---

## 📍 Option 2: Editing the Config File Manually

If you prefer editing the JSON file directly:
- **User Override File (Preserved across all updates):**  
  `%AppData%\BIMCommandCenter\config.json`
- **Or Repository Config File:**  
  `config\config.json`

---

## ✏️ The 2 Main Connection Points to Change

Open `config.json` in Notepad or VS Code:

```json
{
  "catalogSource": "PASTE_YOUR_SHAREPOINT_OR_EXCEL_PATH_HERE",
  "catalogSheet": "Tools",
  "scriptsRoot": "PASTE_YOUR_DYNAMO_SCRIPTS_FOLDER_PATH_HERE",
  "ideasEndpoint": "",
  "updateSource": "",
  "cacheMinutes": 30
}
```

---

## 📋 Common Scenarios & Examples

### Scenario A: SharePoint Folder Synced via OneDrive (Most Popular)
If the SharePoint document library is synced to your computer (via the OneDrive *"Sync"* or *"Add shortcut to OneDrive"* button):

```json
{
  "catalogSource": "C:\\Users\\%USERNAME%\\CompanySharePoint\\BIM\\BIM_ToolCatalog.xlsx",
  "catalogSheet": "Tools",
  "scriptsRoot": "C:\\Users\\%USERNAME%\\CompanySharePoint\\BIM\\Scripts",
  "cacheMinutes": 30
}
```

### Scenario B: Shared Company Network Drive (SMB)
If your firm keeps tools on an internal server drive:

```json
{
  "catalogSource": "\\\\company-server\\BIM\\ToolCatalog.xlsx",
  "catalogSheet": "Tools",
  "scriptsRoot": "\\\\company-server\\BIM\\DynamoScripts",
  "cacheMinutes": 30
}
```

### Scenario C: Power Automate Webhook or Cloud API
If the organization provides a cloud API / Power Automate flow that returns JSON:

```json
{
  "catalogSource": "https://prod-xx.westus.logic.azure.com/workflows/your-endpoint",
  "scriptsRoot": "C:\\Users\\%USERNAME%\\CompanySharePoint\\BIM\\Scripts",
  "ideasEndpoint": "https://prod-xx.westus.logic.azure.com/workflows/ideas-endpoint",
  "cacheMinutes": 30
}
```

---

## 🔄 How Changes Take Effect
1. Save `config.json`.
2. Open Revit and click **Command Center** $\rightarrow$ click the **Refresh (↻)** button at the top right of the panel.
3. The catalog immediately reloads from your new source!
