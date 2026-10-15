; Inno Setup Script for BIM Command Center
; Builds standalone executable installer: BIMCommandCenter_Setup_v1.0.0.exe

#define MyAppName "BIM Command Center"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "BIM Computational Team"
#define MyAppURL "https://company.sharepoint.com/sites/BIM"

[Setup]
AppId={{6F1B7C0E-3C1D-4E8B-9A57-2B3C4D5E6F70}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\BIMCommandCenter
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=BIMCommandCenter_Setup_v1.0.0
OutputDir=..\dist
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableDirPage=yes
UninstallFilesDir={localappdata}\BIMCommandCenter\uninstall

[Types]
Name: "full"; Description: "Install for all detected Revit versions"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "revit2026"; Description: "Autodesk Revit 2026"; Types: full custom
Name: "revit2025"; Description: "Autodesk Revit 2025"; Types: full custom
Name: "revit2024"; Description: "Autodesk Revit 2024"; Types: full custom
Name: "revit2023"; Description: "Autodesk Revit 2023"; Types: full custom

[Files]
; --- Revit 2026 Binaries ---
Source: "..\build\2026\*"; DestDir: "{app}\Revit2026"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2026

; --- Revit 2025 Binaries ---
Source: "..\build\2025\*"; DestDir: "{app}\Revit2025"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2025

; --- Revit 2024 Binaries ---
Source: "..\build\2024\*"; DestDir: "{app}\Revit2024"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2024

; --- Revit 2023 Binaries ---
Source: "..\build\2023\*"; DestDir: "{app}\Revit2023"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: revit2023

; --- Central Scripts Folders (Dynamo, PyRevit, Custom Addins) ---
Source: "..\scripts_template\*"; DestDir: "{userappdata}\BIMCommandCenter\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs onlyifdoesntexist

[Dirs]
Name: "{userappdata}\BIMCommandCenter\scripts\Dynamo"
Name: "{userappdata}\BIMCommandCenter\scripts\PyRevit"
Name: "{userappdata}\BIMCommandCenter\scripts\Custom Addins"
Name: "{userappdata}\BIMCommandCenter\scripts\Grasshopper"

[Code]
// Helper function to write Revit .addin manifest file
procedure CreateAddinManifest(Year: String; ComponentName: String);
var
  AddinDir: String;
  AddinFilePath: String;
  AssemblyPath: String;
  ManifestContent: String;
begin
  if WizardIsComponentSelected(ComponentName) then
  begin
    AddinDir := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Year);
    ForceDirectories(AddinDir);
    AddinFilePath := AddinDir + '\BIMCommandCenter.addin';
    AssemblyPath := ExpandConstant('{app}\Revit' + Year + '\BIMCommandCenter.Loader.dll');

    ManifestContent := 
      '<?xml version="1.0" encoding="utf-8"?>' + #13#10 +
      '<RevitAddIns>' + #13#10 +
      '  <AddIn Type="Application">' + #13#10 +
      '    <Name>BIM Command Center</Name>' + #13#10 +
      '    <Assembly>' + AssemblyPath + '</Assembly>' + #13#10 +
      '    <AddInId>6F1B7C0E-3C1D-4E8B-9A57-2B3C4D5E6F70</AddInId>' + #13#10 +
      '    <FullClassName>BIMCommandCenter.Loader.LoaderApp</FullClassName>' + #13#10 +
      '    <VendorId>BIMCC</VendorId>' + #13#10 +
      '    <VendorDescription>BIM Command Center</VendorDescription>' + #13#10 +
      '  </AddIn>' + #13#10 +
      '</RevitAddIns>';

    SaveStringToFile(AddinFilePath, ManifestContent, False);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    CreateAddinManifest('2026', 'revit2026');
    CreateAddinManifest('2025', 'revit2025');
    CreateAddinManifest('2024', 'revit2024');
    CreateAddinManifest('2023', 'revit2023');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Years: array[0..3] of String;
  I: Integer;
  AddinFilePath: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Years[0] := '2026';
    Years[1] := '2025';
    Years[2] := '2024';
    Years[3] := '2023';

    for I := 0 to 3 do
    begin
      AddinFilePath := ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Years[I] + '\BIMCommandCenter.addin');
      if FileExists(AddinFilePath) then
        DeleteFile(AddinFilePath);
    end;
  end;
end;
