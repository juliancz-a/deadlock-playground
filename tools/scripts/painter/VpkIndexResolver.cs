using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Godot;

namespace DeadlockPlayground.Painter
{
    /// <summary>
    /// Scans Deadlock's citadel/addons directory and resolves the next available VPK index
    /// adhering to Valve's pak##_dir.vpk naming convention, preventing destructive overwrites.
    /// </summary>
    public static class VpkIndexResolver
    {
        private static readonly Regex VpkPattern = new(
            @"^pak(\d{2})_dir\.vpk$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        /// <summary>
        /// Scans the given directory and returns a sorted list of all existing pak numbers.
        /// </summary>
        public static List<int> GetExistingIndices(string addonsDir)
        {
            var indices = new List<int>();
            if (string.IsNullOrEmpty(addonsDir) || !Directory.Exists(addonsDir))
            {
                return indices;
            }

            try
            {
                var files = Directory.GetFiles(addonsDir, "pak*_dir.vpk");
                foreach (var file in files)
                {
                    string fileName = Path.GetFileName(file);
                    var match = VpkPattern.Match(fileName);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int idx))
                    {
                        if (!indices.Contains(idx))
                        {
                            indices.Add(idx);
                        }
                    }
                }
                indices.Sort();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[VpkIndexResolver] Error scanning addons directory: {ex.Message}");
            }

            return indices;
        }

        /// <summary>
        /// Resolves the next available integer index for pak##_dir.vpk.
        /// If no matching pak files exist, checks for pak01_dir.vpk: returns 1 if absent, 2 if present.
        /// Otherwise returns max(existing) + 1.
        /// </summary>
        public static int ResolveNextIndex(string addonsDir)
        {
            var existing = GetExistingIndices(addonsDir);

            if (existing.Count == 0)
            {
                // If pak01_dir.vpk is present in addonsDir, default to 02, otherwise 01
                string pak01Path = string.IsNullOrEmpty(addonsDir) ? "" : Path.Combine(addonsDir, "pak01_dir.vpk");
                if (!string.IsNullOrEmpty(addonsDir) && File.Exists(pak01Path))
                {
                    return 2;
                }
                return 1;
            }

            int maxIndex = existing[^1];
            int next = maxIndex + 1;
            if (next > 99)
            {
                GD.PushWarning($"[VpkIndexResolver] VPK slot limit reached (index {next} > 99). Next mods might exceed standard two-digit range.");
            }
            return next;
        }

        /// <summary>
        /// Resolves the next available filename formatted as 'pak##_dir.vpk'.
        /// </summary>
        public static string ResolveNextFileName(string addonsDir)
        {
            int nextIndex = ResolveNextIndex(addonsDir);
            return $"pak{nextIndex:D2}_dir.vpk";
        }

        /// <summary>
        /// Resolves the full target destination path for the next VPK file.
        /// </summary>
        public static string ResolveNextFullPath(string addonsDir)
        {
            string fileName = ResolveNextFileName(addonsDir);
            return string.IsNullOrEmpty(addonsDir) ? fileName : Path.Combine(addonsDir, fileName);
        }
    }
}
