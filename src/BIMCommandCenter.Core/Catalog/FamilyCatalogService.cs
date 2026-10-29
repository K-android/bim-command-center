using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Catalog
{
    internal static class ShellThumbnailExtractor
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            [In] IntPtr pbc,
            [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] int flags, [Out] out IntPtr phbm);
        }

        [ComImport]
        [Guid("0000000b-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IStorage
        {
            [PreserveSig]
            int CreateStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, uint grfMode, uint reserved1, uint reserved2, out System.Runtime.InteropServices.ComTypes.IStream ppstm);
            [PreserveSig]
            int OpenStream([MarshalAs(UnmanagedType.LPWStr)] string pwcsName, IntPtr reserved1, uint grfMode, uint reserved2, out System.Runtime.InteropServices.ComTypes.IStream ppstm);
        }

        [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int StgOpenStorage(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcsName,
            IntPtr pstgPriority,
            uint grfMode,
            IntPtr snbExclude,
            uint reserved,
            out IStorage ppstgOpen);

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        private static readonly Guid ImageFactoryGuid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
        private static readonly byte[] PngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] PngIend = new byte[] { 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };
        private static readonly byte[] DwgSentinel = new byte[]
        {
            0x1F, 0x25, 0x6D, 0x07, 0xD4, 0x36, 0x28, 0x28,
            0x9D, 0x57, 0xCA, 0x3F, 0x9D, 0x44, 0x10, 0x2B
        };

        public static string GetThumbnailBase64(string filePath, int size = 192)
        {
            try
            {
                if (!File.Exists(filePath)) return null;

                // 1. Check for an explicit sidecar preview image (.png / .jpg) next to the family/block file
                string dir = Path.GetDirectoryName(filePath) ?? "";
                string baseName = Path.GetFileNameWithoutExtension(filePath);
                foreach (var imgExt in new[] { ".png", ".jpg", ".jpeg" })
                {
                    string sidecarImg = Path.Combine(dir, baseName + imgExt);
                    if (File.Exists(sidecarImg))
                    {
                        byte[] scBytes = File.ReadAllBytes(sidecarImg);
                        string mime = imgExt == ".png" ? "image/png" : "image/jpeg";
                        return $"data:{mime};base64," + Convert.ToBase64String(scBytes);
                    }
                }

                // 2. Check local thumbnail disk cache (v3 key so stale empty/icon caches are bypassed)
                string cacheDir = Path.Combine(AppConfig.LocalDataDir, "thumbnails");
                Directory.CreateDirectory(cacheDir);

                string hash = "v3_" + Math.Abs((filePath + "|" + File.GetLastWriteTimeUtc(filePath).Ticks).GetHashCode()).ToString("X8");
                string cachedPath = Path.Combine(cacheDir, hash + ".png");
                if (File.Exists(cachedPath))
                {
                    byte[] cachedBytes = File.ReadAllBytes(cachedPath);
                    if (cachedBytes.Length > 64)
                    {
                        return "data:image/png;base64," + Convert.ToBase64String(cachedBytes);
                    }
                }

                string ext = (Path.GetExtension(filePath) ?? "").ToLowerInvariant();
                byte[] extractedPng = null;

                // 3. Direct embedded binary extraction for .rfa, .dwg, .3dm, .skp
                if (ext == ".rfa")
                {
                    extractedPng = TryExtractRfaPreviewPng(filePath);
                }
                else if (ext == ".dwg")
                {
                    extractedPng = TryExtractDwgPreviewPng(filePath);
                }
                else if (ext == ".3dm" || ext == ".skp")
                {
                    extractedPng = TryExtractEmbeddedPngFromFile(filePath);
                }

                if (extractedPng != null && extractedPng.Length > 64)
                {
                    try { File.WriteAllBytes(cachedPath, extractedPng); } catch { }
                    return "data:image/png;base64," + Convert.ToBase64String(extractedPng);
                }

                // 4. Fallback to Windows Shell Thumbnail Factory (with SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK = 0x09, then SIIGBF_RESIZETOFIT = 0x00)
                byte[] shellPng = TryExtractShellThumbnailPng(filePath, size);
                if (shellPng != null && shellPng.Length > 64)
                {
                    try { File.WriteAllBytes(cachedPath, shellPng); } catch { }
                    return "data:image/png;base64," + Convert.ToBase64String(shellPng);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Thumbnail extraction skipped for " + Path.GetFileName(filePath) + ": " + ex.Message);
            }
            return null;
        }

        private static byte[] TryExtractRfaPreviewPng(string filePath)
        {
            // Method A: Read OLE Compound Document stream "RevitPreview4.0" via ole32.dll StgOpenStorage
            try
            {
                // STGM_READ (0x00000000) | STGM_SHARE_DENY_WRITE (0x00000020)
                int hr = StgOpenStorage(filePath, IntPtr.Zero, 0x00000020u, IntPtr.Zero, 0, out IStorage storage);
                if (hr == 0 && storage != null)
                {
                    try
                    {
                        // STGM_READ (0x00000000) | STGM_SHARE_EXCLUSIVE (0x00000010)
                        int sHr = storage.OpenStream("RevitPreview4.0", IntPtr.Zero, 0x00000010u, 0, out System.Runtime.InteropServices.ComTypes.IStream stream);
                        if (sHr == 0 && stream != null)
                        {
                            try
                            {
                                stream.Stat(out System.Runtime.InteropServices.ComTypes.STATSTG stat, 1);
                                int len = (int)Math.Min(stat.cbSize, 4 * 1024 * 1024);
                                if (len > 32)
                                {
                                    byte[] buf = new byte[len];
                                    IntPtr bytesReadPtr = Marshal.AllocCoTaskMem(sizeof(int));
                                    try
                                    {
                                        stream.Read(buf, len, bytesReadPtr);
                                        byte[] png = SliceValidPng(buf, 0, buf.Length);
                                        if (png != null) return png;
                                    }
                                    finally
                                    {
                                        Marshal.FreeCoTaskMem(bytesReadPtr);
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(stream);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(storage);
                    }
                }
            }
            catch { }

            // Method B: Direct scan of .rfa file bytes for embedded PNG
            return TryExtractEmbeddedPngFromFile(filePath);
        }

        private static byte[] TryExtractEmbeddedPngFromFile(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int maxBytes = (int)Math.Min(fs.Length, 16 * 1024 * 1024);
                    byte[] buffer = new byte[maxBytes];
                    int read = fs.Read(buffer, 0, maxBytes);
                    return SliceValidPng(buffer, 0, read);
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] SliceValidPng(byte[] buffer, int offset, int length)
        {
            int endLimit = Math.Min(buffer.Length, offset + length);
            int searchPos = offset;

            while (searchPos < endLimit - PngHeader.Length)
            {
                int startIdx = FindPattern(buffer, PngHeader, searchPos, endLimit);
                if (startIdx < 0) break;

                int iendIdx = FindPattern(buffer, PngIend, startIdx + PngHeader.Length, Math.Min(endLimit, startIdx + 2 * 1024 * 1024));
                if (iendIdx > startIdx)
                {
                    int pngLen = (iendIdx + PngIend.Length) - startIdx;
                    if (pngLen > 64 && pngLen < 2 * 1024 * 1024)
                    {
                        byte[] candidate = new byte[pngLen];
                        Buffer.BlockCopy(buffer, startIdx, candidate, 0, pngLen);
                        if (IsValidPngBytes(candidate))
                        {
                            return candidate;
                        }
                    }
                }

                searchPos = startIdx + PngHeader.Length;
            }

            return null;
        }

        private static bool IsValidPngBytes(byte[] pngBytes)
        {
            try
            {
                using (var ms = new MemoryStream(pngBytes))
                {
                    var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                    {
                        var frame = decoder.Frames[0];
                        return frame.PixelWidth >= 8 && frame.PixelHeight >= 8;
                    }
                }
            }
            catch { }
            return false;
        }

        private static byte[] TryExtractDwgPreviewPng(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 64) return null;

                    // DWG stores the thumbnail section address at offset 0x0D (13)
                    fs.Seek(0x0D, SeekOrigin.Begin);
                    int thumbPos = br.ReadInt32();
                    if (thumbPos > 0 && thumbPos + 32 < fs.Length)
                    {
                        fs.Seek(thumbPos, SeekOrigin.Begin);
                        byte[] sentinel = br.ReadBytes(16);
                        bool sentinelMatches = true;
                        for (int i = 0; i < 16; i++)
                        {
                            if (sentinel[i] != DwgSentinel[i]) { sentinelMatches = false; break; }
                        }

                        if (sentinelMatches)
                        {
                            int sectionSize = br.ReadInt32();
                            byte count = br.ReadByte();
                            for (int i = 0; i < count && i < 10; i++)
                            {
                                byte code = br.ReadByte();
                                int dataStart = br.ReadInt32();
                                int dataSize = br.ReadInt32();

                                if (dataStart > 0 && dataSize > 32 && dataStart + dataSize <= fs.Length)
                                {
                                    long savedPos = fs.Position;
                                    fs.Seek(dataStart, SeekOrigin.Begin);
                                    byte[] imgData = br.ReadBytes(dataSize);
                                    fs.Seek(savedPos, SeekOrigin.Begin);

                                    // Code 6 = PNG preview (AutoCAD 2013+)
                                    if (code == 6)
                                    {
                                        byte[] png = SliceValidPng(imgData, 0, imgData.Length);
                                        if (png != null) return png;
                                    }
                                    // Code 2 = DIB/BMP preview without 14-byte BITMAPFILEHEADER
                                    else if (code == 2)
                                    {
                                        byte[] converted = ConvertDibToPng(imgData);
                                        if (converted != null) return converted;
                                    }
                                }
                            }
                        }
                    }
                }

                // Fallback: scan file for embedded PNG
                return TryExtractEmbeddedPngFromFile(filePath);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] ConvertDibToPng(byte[] dibBytes)
        {
            try
            {
                if (dibBytes == null || dibBytes.Length < 40) return null;

                // Read BITMAPINFOHEADER fields to calculate offset to pixel data
                int headerSize = BitConverter.ToInt32(dibBytes, 0);
                short bitCount = BitConverter.ToInt16(dibBytes, 14);
                int clrUsed = BitConverter.ToInt32(dibBytes, 32);

                int paletteEntries = clrUsed;
                if (paletteEntries == 0 && bitCount <= 8)
                    paletteEntries = 1 << bitCount;

                int bfOffBits = 14 + headerSize + (paletteEntries * 4);
                int totalFileSize = 14 + dibBytes.Length;

                byte[] bmpBytes = new byte[totalFileSize];
                bmpBytes[0] = (byte)'B';
                bmpBytes[1] = (byte)'M';
                Buffer.BlockCopy(BitConverter.GetBytes(totalFileSize), 0, bmpBytes, 2, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(bfOffBits), 0, bmpBytes, 10, 4);
                Buffer.BlockCopy(dibBytes, 0, bmpBytes, 14, dibBytes.Length);

                using (var ms = new MemoryStream(bmpBytes))
                {
                    var decoder = new BmpBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    if (decoder.Frames.Count > 0)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
                        using (var outMs = new MemoryStream())
                        {
                            encoder.Save(outMs);
                            return outMs.ToArray();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static byte[] TryExtractShellThumbnailPng(string filePath, int size)
        {
            try
            {
                int createHr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ImageFactoryGuid, out IShellItemImageFactory factory);
                if (createHr != 0 || factory == null) return null;

                // First try: 0x08 (SIIGBF_THUMBNAILONLY) | 0x01 (SIIGBF_BIGGERSIZEOK) = 0x09
                // Second try: 0x00 (SIIGBF_RESIZETOFIT)
                int[] flagAttempts = new[] { 0x09, 0x08, 0x00 };
                foreach (int flags in flagAttempts)
                {
                    int hr = factory.GetImage(new SIZE(size, size), flags, out IntPtr hBitmap);
                    if (hr == 0 && hBitmap != IntPtr.Zero)
                    {
                        try
                        {
                            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                hBitmap,
                                IntPtr.Zero,
                                System.Windows.Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());

                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(src));
                            using (var ms = new MemoryStream())
                            {
                                encoder.Save(ms);
                                return ms.ToArray();
                            }
                        }
                        finally
                        {
                            DeleteObject(hBitmap);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static int FindPattern(byte[] buffer, byte[] pattern, int startIndex, int endLimit)
        {
            int max = Math.Min(buffer.Length, endLimit) - pattern.Length;
            byte first = pattern[0];
            for (int i = startIndex; i <= max; i++)
            {
                if (buffer[i] != first) continue;
                bool match = true;
                for (int j = 1; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }
    }

    internal static class FamilyMetadataExtractor
    {
        public class ExtractedMetadata
        {
            public string RevitCategory { get; set; }
            public string FileVersion { get; set; }
            public string HostType { get; set; }
            public List<string> FamilyTypes { get; set; } = new List<string>();
            public List<string> ParametersSummary { get; set; } = new List<string>();
            public string Description { get; set; }
        }

        public static ExtractedMetadata Extract(string filePath, string name, string folderCategory, string format)
        {
            var meta = new ExtractedMetadata();
            try
            {
                string dir = Path.GetDirectoryName(filePath) ?? "";
                string baseName = Path.GetFileNameWithoutExtension(filePath);
                string sidecarDescPath = Path.Combine(dir, baseName + ".desc.txt");
                string customDescription = null;
                if (File.Exists(sidecarDescPath))
                {
                    customDescription = (File.ReadAllText(sidecarDescPath) ?? "").Trim();
                }

                if (string.Equals(format, "RFA", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractFromRfaBinary(filePath, meta);
                }
                else if (string.Equals(format, "DWG", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractFromDwgBinary(filePath, meta);
                }
                else if (string.Equals(format, "3DM", StringComparison.OrdinalIgnoreCase))
                {
                    meta.FileVersion = "Rhino 3DM";
                    meta.RevitCategory = "3D Rhino Geometry";
                    meta.HostType = "3D DirectShape / Import";
                }
                else if (string.Equals(format, "SKP", StringComparison.OrdinalIgnoreCase))
                {
                    meta.FileVersion = "SketchUp SKP";
                    meta.RevitCategory = "SketchUp Component";
                    meta.HostType = "3D Component Import";
                }

                // Infer missing fields from filename and folder category
                EnrichFromNamingConventions(name, folderCategory, format, meta);

                if (!string.IsNullOrWhiteSpace(customDescription))
                {
                    meta.Description = customDescription;
                }
                else if (string.IsNullOrWhiteSpace(meta.Description))
                {
                    meta.Description = SynthesizeDescription(name, folderCategory, format, meta);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Metadata extraction note for " + name + ": " + ex.Message);
                EnrichFromNamingConventions(name, folderCategory, format, meta);
                if (string.IsNullOrWhiteSpace(meta.Description))
                {
                    meta.Description = SynthesizeDescription(name, folderCategory, format, meta);
                }
            }
            return meta;
        }

        private static void ExtractFromRfaBinary(string filePath, ExtractedMetadata meta)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int scanLen = (int)Math.Min(fs.Length, 512 * 1024);
                byte[] buf = new byte[scanLen];
                int read = fs.Read(buf, 0, scanLen);

                // 1. Scan for PartAtom XML (<entry ... </entry>) in ASCII/UTF-8
                string ascii = Encoding.UTF8.GetString(buf, 0, read);
                int entryStart = ascii.IndexOf("<entry", StringComparison.OrdinalIgnoreCase);
                int entryEnd = entryStart >= 0 ? ascii.IndexOf("</entry>", entryStart, StringComparison.OrdinalIgnoreCase) : -1;
                if (entryStart >= 0 && entryEnd > entryStart)
                {
                    string xml = ascii.Substring(entryStart, (entryEnd + 8) - entryStart);

                    // Extract Revit Category terms
                    var catMatches = Regex.Matches(xml, @"<category>\s*<term>([^<]+)</term>\s*<scheme>adsk:revit:grouping</scheme>", RegexOptions.IgnoreCase);
                    var terms = new List<string>();
                    foreach (Match m in catMatches)
                    {
                        string term = WebDecode(m.Groups[1].Value.Trim());
                        if (!string.IsNullOrWhiteSpace(term) && !terms.Contains(term, StringComparer.OrdinalIgnoreCase))
                            terms.Add(term);
                    }
                    if (terms.Count > 0)
                    {
                        meta.RevitCategory = terms.Last();
                    }

                    // Extract Family Types from <g:part> blocks
                    var partMatches = Regex.Matches(xml, @"<g:part>([\s\S]*?)</g:part>", RegexOptions.IgnoreCase);
                    var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var seenParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (Match pm in partMatches)
                    {
                        string partXml = pm.Groups[1].Value;
                        var titleMatch = Regex.Match(partXml, @"<title>([^<]+)</title>", RegexOptions.IgnoreCase);
                        if (titleMatch.Success)
                        {
                            string tName = WebDecode(titleMatch.Groups[1].Value.Trim());
                            if (!string.IsNullOrWhiteSpace(tName) && seenTypes.Add(tName) && meta.FamilyTypes.Count < 8)
                            {
                                meta.FamilyTypes.Add(tName);
                            }
                        }

                        // Check for embedded Description parameter
                        if (string.IsNullOrWhiteSpace(meta.Description))
                        {
                            var descMatch = Regex.Match(partXml, @"<Description[^>]*>([^<]+)</Description>", RegexOptions.IgnoreCase);
                            if (descMatch.Success)
                            {
                                string dVal = WebDecode(descMatch.Groups[1].Value.Trim());
                                if (!string.IsNullOrWhiteSpace(dVal) && dVal.Length > 3)
                                    meta.Description = dVal;
                            }
                        }

                        // Extract key dimension/specification parameters from first type
                        if (meta.ParametersSummary.Count < 5)
                        {
                            var paramMatches = Regex.Matches(partXml, @"<([A-Za-z0-9_.-]+)\s+[^>]*displayName=""([^""]+)""[^>]*>([^<]+)</\1>", RegexOptions.IgnoreCase);
                            foreach (Match prm in paramMatches)
                            {
                                string dispName = WebDecode(prm.Groups[2].Value.Trim());
                                string pVal = WebDecode(prm.Groups[3].Value.Trim());
                                if (string.IsNullOrWhiteSpace(dispName) || string.IsNullOrWhiteSpace(pVal)) continue;

                                if (Regex.IsMatch(dispName, @"^(Width|Height|Depth|Length|Radius|Diameter|Rough Width|Rough Height|Thickness|Material|Fire Rating|Manufacturer|Model|Voltage|Wattage|Flow|Elevation)$", RegexOptions.IgnoreCase))
                                {
                                    if (seenParams.Add(dispName) && meta.ParametersSummary.Count < 5)
                                    {
                                        meta.ParametersSummary.Add($"{dispName}: {pVal}");
                                    }
                                }
                            }
                        }
                    }
                }

                // 2. Scan UTF-16LE BasicFileInfo for saved Revit version (e.g. "Format: 2025" or "Autodesk Revit 2024")
                string unicodeText = Encoding.Unicode.GetString(buf, 0, read);
                var verMatch = Regex.Match(unicodeText, @"(?:Format\s*:\s*|Autodesk\s+Revit\s+)(202[0-9]|201[8-9])", RegexOptions.IgnoreCase);
                if (verMatch.Success)
                {
                    meta.FileVersion = "Revit " + verMatch.Groups[1].Value;
                }
            }
        }

        private static void ExtractFromDwgBinary(string filePath, ExtractedMetadata meta)
        {
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < 6) return;
                byte[] hdr = new byte[6];
                fs.Read(hdr, 0, 6);
                string verCode = Encoding.ASCII.GetString(hdr);
                switch (verCode)
                {
                    case "AC1032": meta.FileVersion = "AutoCAD 2018+"; break;
                    case "AC1027": meta.FileVersion = "AutoCAD 2013"; break;
                    case "AC1024": meta.FileVersion = "AutoCAD 2010"; break;
                    case "AC1021": meta.FileVersion = "AutoCAD 2007"; break;
                    case "AC1018": meta.FileVersion = "AutoCAD 2004"; break;
                    case "AC1015": meta.FileVersion = "AutoCAD 2000"; break;
                    default: meta.FileVersion = "CAD DWG"; break;
                }
            }
        }

        private static void EnrichFromNamingConventions(string name, string folderCategory, string format, ExtractedMetadata meta)
        {
            string combined = $"{name} {folderCategory}".Replace('_', ' ').Replace('-', ' ');

            // Infer dimensions if present in name (e.g. 900x2100, 1200x600mm, 36x84)
            var dimMatch = Regex.Match(name, @"\b(\d{2,4}\s*[xX]\s*\d{2,4}(?:\s*[xX]\s*\d{2,4})?\s*(?:mm|cm|m|in|ft)?)\b");
            if (dimMatch.Success && meta.FamilyTypes.Count == 0)
            {
                meta.FamilyTypes.Add(dimMatch.Groups[1].Value);
            }

            if (string.IsNullOrWhiteSpace(meta.RevitCategory))
            {
                if (Regex.IsMatch(combined, @"\b(door|doors|shutter|entrance|gate)\b", RegexOptions.IgnoreCase))
                    meta.RevitCategory = "Doors";
                else if (Regex.IsMatch(combined, @"\b(window|windows|glazing|curtain|skylight|louvre|louver)\b", RegexOptions.IgnoreCase))
                    meta.RevitCategory = "Windows";
                else if (Regex.IsMatch(combined, @"\b(chair|desk|table|sofa|couch|bed|cabinet|wardrobe|shelf|workstation|seating|bench|furniture)\b", RegexOptions.IgnoreCase))
                    meta.RevitCategory = "Furniture";
                else if (Regex.IsMatch(combined, @"\b(light|lighting|luminaire|lamp|sconce|downlight|pendant|troffer|electrical|panel|switch|receptacle)\b", RegexOptions.IgnoreCase))
                    meta.RevitCategory = "Lighting & Electrical";
                else if (Regex.IsMatch(combined, @"\b(hvac|diffuser|grille|damper|vav|ahu|fcu|chiller|boiler|pump|fan|duct|mechanical)\b", RegexOptions.IgnoreCase))
                    meta.RevitCategory = "Mechanical Equipment";
                else if (Regex.IsMatch(combined, @"\b(toilet|wc|urinal|sink|lavatory|basin|faucet|shower|bath|drain|valve|pipe|plumbing|sprinkler)\b", RegexOptions.IgnoreCase))
                    meta.RevitCategory = "Plumbing Fixtures";
                else if (Regex.IsMatch(combined, @"\b(column|beam|truss|footing|pile|foundation|brace|joist|rebar|structural|steel|concrete)\b", RegexOptions.IgnoreCase))
                    meta.RevitCategory = "Structural";
                else if (Regex.IsMatch(combined, @"\b(tag|symbol|north|titleblock|callout|section|elevation|grid|level|detail|annotation|breakline)\b", RegexOptions.IgnoreCase))
                    meta.RevitCategory = "Annotation & Detail";
                else if (string.Equals(format, "DWG", StringComparison.OrdinalIgnoreCase))
                    meta.RevitCategory = "CAD Block";
                else
                    meta.RevitCategory = !string.IsNullOrWhiteSpace(folderCategory) && folderCategory != "General" ? folderCategory : "Revit Family";
            }

            if (string.IsNullOrWhiteSpace(meta.HostType))
            {
                if (Regex.IsMatch(combined, @"\b(wall|door|window|sconce)\b", RegexOptions.IgnoreCase) || meta.RevitCategory == "Doors" || meta.RevitCategory == "Windows")
                    meta.HostType = "Wall-Based";
                else if (Regex.IsMatch(combined, @"\b(ceiling|downlight|troffer|pendant|diffuser|sprinkler)\b", RegexOptions.IgnoreCase))
                    meta.HostType = "Ceiling / Face-Based";
                else if (Regex.IsMatch(combined, @"\b(tag|symbol|titleblock|detail|annotation|2d)\b", RegexOptions.IgnoreCase) || meta.RevitCategory == "Annotation & Detail")
                    meta.HostType = "2D View-Specific";
                else if (string.Equals(format, "DWG", StringComparison.OrdinalIgnoreCase))
                    meta.HostType = "CAD Block / Import";
                else
                    meta.HostType = "Level / Workplane-Based";
            }

            if (string.IsNullOrWhiteSpace(meta.FileVersion))
            {
                meta.FileVersion = format == "RFA" ? "Revit RFA" : (format == "DWG" ? "CAD DWG" : format);
            }
        }

        private static string SynthesizeDescription(string name, string folderCategory, string format, ExtractedMetadata meta)
        {
            string cleanName = Regex.Replace(name.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
            var parts = new List<string>();

            if (format == "RFA")
            {
                parts.Add($"Standard {meta.RevitCategory ?? "Revit"} family ({meta.HostType ?? "Parametric"})");
            }
            else if (format == "DWG")
            {
                parts.Add($"Standard {meta.RevitCategory ?? "CAD"} DWG block for {folderCategory ?? "project"} detailing & layout");
            }
            else
            {
                parts.Add($"3D {format} component ({meta.RevitCategory ?? folderCategory})");
            }

            if (meta.FamilyTypes.Count > 1)
            {
                parts.Add($"{meta.FamilyTypes.Count} sizes/types ({string.Join(", ", meta.FamilyTypes.Take(3))}{(meta.FamilyTypes.Count > 3 ? "…" : "")})");
            }
            else if (meta.FamilyTypes.Count == 1)
            {
                parts.Add($"Type: {meta.FamilyTypes[0]}");
            }

            if (meta.ParametersSummary.Count > 0)
            {
                parts.Add(string.Join(", ", meta.ParametersSummary.Take(2)));
            }

            return string.Join(" • ", parts);
        }

        private static string WebDecode(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'");
        }
    }

    public class FamilyItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string RevitCategory { get; set; }
        public string Format { get; set; }
        public string FileVersion { get; set; }
        public string HostType { get; set; }
        public List<string> FamilyTypes { get; set; } = new List<string>();
        public List<string> ParametersSummary { get; set; } = new List<string>();
        public string Description { get; set; }
        public string FullPath { get; set; }
        public string RelativePath { get; set; }
        public long FileSizeBytes { get; set; }
        public string FileSize { get; set; }
        public string LastModified { get; set; }
        public string Thumbnail { get; set; }
    }

    public class FamilyCatalogService
    {
        private readonly AppConfig _config;
        private List<FamilyItem> _items = new List<FamilyItem>();
        private readonly object _lock = new object();
        private DateTime _lastScanTime = DateTime.MinValue;

        public FamilyCatalogService(AppConfig config)
        {
            _config = config;
        }

        public string FamiliesFolder => _config.FamiliesRoot;

        public Dictionary<string, object> GetCatalog(bool forceRefresh = false)
        {
            lock (_lock)
            {
                if (forceRefresh || _items.Count == 0 || (DateTime.UtcNow - _lastScanTime).TotalMinutes > 3)
                {
                    Scan();
                }

                var categories = _items.Select(i => i.Category).Distinct().OrderBy(c => c).ToList();

                return new Dictionary<string, object>
                {
                    ["families"] = _items.Select(i => new Dictionary<string, object>
                    {
                        ["id"] = i.Id,
                        ["name"] = i.Name,
                        ["category"] = i.Category,
                        ["revitCategory"] = i.RevitCategory,
                        ["format"] = i.Format,
                        ["fileVersion"] = i.FileVersion,
                        ["hostType"] = i.HostType,
                        ["familyTypes"] = i.FamilyTypes,
                        ["parametersSummary"] = i.ParametersSummary,
                        ["description"] = i.Description,
                        ["fullPath"] = i.FullPath,
                        ["relativePath"] = i.RelativePath,
                        ["fileSizeBytes"] = i.FileSizeBytes,
                        ["fileSize"] = i.FileSize,
                        ["lastModified"] = i.LastModified,
                        ["thumbnail"] = i.Thumbnail
                    }).ToList(),
                    ["categories"] = categories,
                    ["folder"] = FamiliesFolder,
                    ["count"] = _items.Count
                };
            }
        }

        public void Scan()
        {
            var newItems = new List<FamilyItem>();
            var roots = new List<string>();

            if (!string.IsNullOrWhiteSpace(_config.FamiliesRoot) && Directory.Exists(_config.FamiliesRoot))
                roots.Add(_config.FamiliesRoot);

            if (!string.IsNullOrWhiteSpace(_config.ScriptsRoot))
            {
                string alt1 = Path.Combine(_config.ScriptsRoot, "families");
                if (Directory.Exists(alt1) && !roots.Contains(alt1, StringComparer.OrdinalIgnoreCase))
                    roots.Add(alt1);

                string alt2 = Path.Combine(Path.GetDirectoryName(_config.ScriptsRoot) ?? "", "families");
                if (Directory.Exists(alt2) && !roots.Contains(alt2, StringComparer.OrdinalIgnoreCase))
                    roots.Add(alt2);
            }

            var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".rfa", ".dwg", ".3dm", ".skp"
            };

            foreach (var root in roots)
            {
                try
                {
                    var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        string ext = Path.GetExtension(file);
                        if (!supportedExtensions.Contains(ext)) continue;

                        try
                        {
                            var fi = new FileInfo(file);
                            string rel = file.Substring(root.Length).TrimStart('\\', '/');
                            string[] parts = rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                            string category = parts.Length > 1 ? parts[0] : "General";

                            string name = Path.GetFileNameWithoutExtension(file);
                            string format = ext.TrimStart('.').ToUpperInvariant();

                            string thumb = ShellThumbnailExtractor.GetThumbnailBase64(file);
                            var meta = FamilyMetadataExtractor.Extract(file, name, category, format);

                            newItems.Add(new FamilyItem
                            {
                                Id = Math.Abs((rel + fi.Length).GetHashCode()).ToString("X8"),
                                Name = name,
                                Category = category,
                                RevitCategory = meta.RevitCategory,
                                Format = format,
                                FileVersion = meta.FileVersion,
                                HostType = meta.HostType,
                                FamilyTypes = meta.FamilyTypes,
                                ParametersSummary = meta.ParametersSummary,
                                Description = meta.Description,
                                FullPath = file,
                                RelativePath = rel,
                                FileSizeBytes = fi.Length,
                                FileSize = FormatFileSize(fi.Length),
                                LastModified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                                Thumbnail = thumb
                            });
                        }
                        catch (Exception ex)
                        {
                            Log.Write("Error indexing family file " + file + ": " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("Error scanning root " + root + ": " + ex.Message);
                }
            }

            _items = newItems.OrderBy(i => i.Category).ThenBy(i => i.Name).ToList();
            _lastScanTime = DateTime.UtcNow;
            Log.Write($"FamilyCatalog scanned {_items.Count} components across {roots.Count} root directories.");
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }
    }
}
