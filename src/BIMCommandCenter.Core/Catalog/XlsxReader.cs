using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace BIMCommandCenter.Core.Catalog
{
    /// <summary>
    /// Reads the first row as headers and every following row as a record from an .xlsx file.
    /// Pure ZipArchive + XML so we don't ship an Excel library into Revit.
    /// Works with a SharePoint Excel file synced locally via OneDrive ("Add shortcut to My files" / "Sync").
    /// </summary>
    public static class XlsxReader
    {
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static List<Dictionary<string, string>> ReadRows(string path, string sheetName = null)
        {
            // Copy first: the file may be open in Excel / locked by OneDrive sync.
            string temp = Path.Combine(Path.GetTempPath(), "bimcc_catalog_" + Guid.NewGuid().ToString("N") + ".xlsx");
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = File.Create(temp))
            {
                src.CopyTo(dst);
            }

            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(temp))
                {
                    List<string> shared = ReadSharedStrings(zip);
                    string sheetPath = FindSheetPath(zip, sheetName);
                    XDocument sheet = Load(zip, sheetPath) ?? throw new InvalidDataException("Worksheet not found: " + sheetPath);

                    var rows = sheet.Descendants(S + "sheetData").Elements(S + "row").ToList();
                    if (rows.Count == 0) return new List<Dictionary<string, string>>();

                    Dictionary<int, string> headers = ReadRow(rows[0], shared)
                        .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                        .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());

                    var result = new List<Dictionary<string, string>>();
                    foreach (XElement row in rows.Skip(1))
                    {
                        Dictionary<int, string> cells = ReadRow(row, shared);
                        if (cells.Values.All(string.IsNullOrWhiteSpace)) continue;

                        var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var h in headers)
                            record[h.Value] = cells.TryGetValue(h.Key, out string v) ? v : "";
                        result.Add(record);
                    }
                    return result;
                }
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        private static XDocument Load(ZipArchive zip, string entryPath)
        {
            ZipArchiveEntry entry = zip.GetEntry(entryPath);
            if (entry == null) return null;
            using (Stream s = entry.Open()) return XDocument.Load(s);
        }

        private static List<string> ReadSharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            XDocument doc = Load(zip, "xl/sharedStrings.xml");
            if (doc == null) return list;
            foreach (XElement si in doc.Root.Elements(S + "si"))
                list.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));
            return list;
        }

        private static string FindSheetPath(ZipArchive zip, string sheetName)
        {
            XDocument wb = Load(zip, "xl/workbook.xml");
            XDocument rels = Load(zip, "xl/_rels/workbook.xml.rels");
            var sheets = wb?.Descendants(S + "sheet").ToList() ?? new List<XElement>();
            if (sheets.Count == 0) return "xl/worksheets/sheet1.xml";

            XElement chosen = null;
            if (!string.IsNullOrWhiteSpace(sheetName))
                chosen = sheets.FirstOrDefault(s => string.Equals((string)s.Attribute("name"), sheetName, StringComparison.OrdinalIgnoreCase));
            chosen = chosen ?? sheets[0];

            string relId = (string)chosen.Attribute(R + "id");
            string target = rels?.Root.Elements(PR + "Relationship")
                .FirstOrDefault(r => (string)r.Attribute("Id") == relId)?.Attribute("Target")?.Value;
            if (string.IsNullOrEmpty(target)) return "xl/worksheets/sheet1.xml";
            target = target.TrimStart('/');
            return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) ? target : "xl/" + target;
        }

        private static Dictionary<int, string> ReadRow(XElement row, List<string> shared)
        {
            var cells = new Dictionary<int, string>();
            int fallbackIndex = 0;
            foreach (XElement c in row.Elements(S + "c"))
            {
                string reference = (string)c.Attribute("r");
                int col = reference != null ? ColumnIndex(reference) : fallbackIndex;
                fallbackIndex = col + 1;

                string type = (string)c.Attribute("t");
                string raw = c.Element(S + "v")?.Value;
                string value;
                switch (type)
                {
                    case "s":
                        value = int.TryParse(raw, out int idx) && idx < shared.Count ? shared[idx] : "";
                        break;
                    case "inlineStr":
                        value = string.Concat(c.Descendants(S + "t").Select(t => t.Value));
                        break;
                    case "b":
                        value = raw == "1" ? "true" : "false";
                        break;
                    default:
                        value = raw ?? "";
                        break;
                }
                cells[col] = value;
            }
            return cells;
        }

        private static int ColumnIndex(string cellRef)
        {
            int index = 0;
            foreach (char ch in cellRef)
            {
                if (!char.IsLetter(ch)) break;
                index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return index - 1;
        }
    }
}
