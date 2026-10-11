# How to Connect Your Real SharePoint File & Dynamo Scripts

When your teammate sends you the SharePoint link or file path, you only need to change **2 lines** in the config file.

---

## 📍 Where is the Config File?

You can edit either:
- **User Override File (Preserved permanently):**  
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

### Scenario A: Your Teammate Syncs the SharePoint Folder via OneDrive (Most Popular)
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
If your teammate built a cloud API / Power Automate flow that returns JSON:

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
