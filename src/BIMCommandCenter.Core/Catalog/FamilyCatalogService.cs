using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Catalog
{
    internal static class ShellThumbnailExtractor
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [In] string pszPath,
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

        public static string GetThumbnailBase64(string filePath, int size = 128)
        {
            try
            {
                if (!File.Exists(filePath)) return null;

                string cacheDir = Path.Combine(AppConfig.LocalDataDir, "thumbnails");
                Directory.CreateDirectory(cacheDir);

                string hash = Math.Abs((filePath + File.GetLastWriteTimeUtc(filePath).Ticks).GetHashCode()).ToString("X8");
                string cachedPath = Path.Combine(cacheDir, hash + ".png");
                if (File.Exists(cachedPath))
                {
                    byte[] bytes = File.ReadAllBytes(cachedPath);
                    return "data:image/png;base64," + Convert.ToBase64String(bytes);
                }

                SHCreateItemFromParsingName(filePath, IntPtr.Zero, ImageFactoryGuid, out IShellItemImageFactory factory);
                if (factory == null) return null;

                // 0x2 = SIIGBF_BIGGERSIZEOK, 0x0 = SIIGBF_RESIZETOFIT
                int hr = factory.GetImage(new SIZE(size, size), 0x2, out IntPtr hBitmap);
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
                            byte[] pngBytes = ms.ToArray();
                            File.WriteAllBytes(cachedPath, pngBytes);
                            return "data:image/png;base64," + Convert.ToBase64String(pngBytes);
                        }
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Thumbnail extraction skipped for " + Path.GetFileName(filePath) + ": " + ex.Message);
            }
            return null;
        }
    }

    public class FamilyItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Format { get; set; }
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
                        ["format"] = i.Format,
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

                            newItems.Add(new FamilyItem
                            {
                                Id = Math.Abs((rel + fi.Length).GetHashCode()).ToString("X8"),
                                Name = name,
                                Category = category,
                                Format = format,
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
