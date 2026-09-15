using Godot;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public partial class GamePathManager : Node
{
    private const string ConfigPath = "user://settings.cfg";
    private const string DeadlockRelativePath = "steamapps/common/Deadlock/game/citadel/pak01_dir.vpk";
    
    public string CurrentGamePath { get; private set; }
    public string CurrentSavePath { get; private set; }
    public string CurrentCsdkPath { get; private set; }
    public string CurrentResourceCompilerPath { get; private set; }

    public override void _Ready()
    {
        LoadConfig();
    }

    public void LoadConfig()
    {
        var config = new ConfigFile();
        Error err = config.Load(ConfigPath);
        if (err == Error.Ok)
        {
            CurrentGamePath = (string)config.GetValue("Paths", "GamePath", "");
            CurrentSavePath = (string)config.GetValue("Paths", "SavePath", "");
            CurrentCsdkPath = (string)config.GetValue("Paths", "CsdkPath", "");
            CurrentResourceCompilerPath = (string)config.GetValue("Paths", "ResourceCompilerPath", "");
        }
    }

    public void SaveConfig(string gamePath, string savePath)
    {
        CurrentGamePath = gamePath;
        CurrentSavePath = savePath;
        
        var config = new ConfigFile();
        config.Load(ConfigPath); // Load existing to preserve other settings if any
        config.SetValue("Paths", "GamePath", gamePath);
        config.SetValue("Paths", "SavePath", savePath);
        config.Save(ConfigPath);
    }

    public void SaveCsdkPath(string csdkPath)
    {
        CurrentCsdkPath = csdkPath;
        var config = new ConfigFile();
        config.Load(ConfigPath);
        config.SetValue("Paths", "CsdkPath", csdkPath);
        config.Save(ConfigPath);
    }

    public void SaveResourceCompilerPath(string compilerPath)
    {
        CurrentResourceCompilerPath = compilerPath;
        var config = new ConfigFile();
        config.Load(ConfigPath);
        config.SetValue("Paths", "ResourceCompilerPath", compilerPath);
        config.Save(ConfigPath);
    }

    public string ResolveResourceCompilerExe()
    {
        // 0. Direct configured compiler executable path
        if (!string.IsNullOrEmpty(CurrentResourceCompilerPath) && File.Exists(CurrentResourceCompilerPath))
        {
            string norm = CurrentResourceCompilerPath.Replace('\\', '/');
            if (norm.Contains("/game/bin/win64/resourcecompiler.exe", StringComparison.OrdinalIgnoreCase))
            {
                string binCs2Sibling = norm.Replace("/game/bin/win64/resourcecompiler.exe", "/game/bin_cs2/win64/resourcecompiler.exe", StringComparison.OrdinalIgnoreCase);
                if (File.Exists(binCs2Sibling)) return binCs2Sibling;
            }
            return CurrentResourceCompilerPath;
        }

        // 1. Configured CSDK path (prioritize isolated bin_cs2 to prevent schema mismatch)
        if (!string.IsNullOrEmpty(CurrentCsdkPath))
        {
            string p1 = Path.Combine(CurrentCsdkPath, "game", "bin_cs2", "win64", "resourcecompiler.exe");
            if (File.Exists(p1)) return p1;

            string p2 = Path.Combine(CurrentCsdkPath, "Reduced_CSDK_12", "game", "bin_cs2", "win64", "resourcecompiler.exe");
            if (File.Exists(p2)) return p2;

            string p3 = Path.Combine(CurrentCsdkPath, "game", "bin", "win64", "resourcecompiler.exe");
            if (File.Exists(p3)) return p3;

            string p4 = Path.Combine(CurrentCsdkPath, "resourcecompiler.exe");
            if (File.Exists(p4)) return p4;
        }

        // 2. Deadlock game path siblings / Reduced_CSDK_12
        if (!string.IsNullOrEmpty(CurrentGamePath))
        {
            string[] relativeCandidates = {
                Path.GetFullPath(Path.Combine(CurrentGamePath, "..", "CSDK12", "game", "bin_cs2", "win64", "resourcecompiler.exe")),
                Path.GetFullPath(Path.Combine(CurrentGamePath, "..", "CSDK12", "Reduced_CSDK_12", "game", "bin_cs2", "win64", "resourcecompiler.exe")),
                Path.GetFullPath(Path.Combine(CurrentGamePath, "..", "Reduced_CSDK_12", "game", "bin_cs2", "win64", "resourcecompiler.exe")),
                Path.GetFullPath(Path.Combine(CurrentGamePath, "..", "CSDK12", "game", "bin", "win64", "resourcecompiler.exe")),
                Path.Combine(CurrentGamePath, "game", "bin", "win64", "resourcecompiler.exe")
            };
            foreach (var rc in relativeCandidates)
            {
                if (File.Exists(rc)) return rc;
            }
        }

        // 3. Common drive root locations
        string[] candidates = {
            @"C:\CSDK12\game\bin_cs2\win64\resourcecompiler.exe",
            @"C:\CSDK12\Reduced_CSDK_12\game\bin_cs2\win64\resourcecompiler.exe",
            @"D:\CSDK12\game\bin_cs2\win64\resourcecompiler.exe",
            @"D:\CSDK12\Reduced_CSDK_12\game\bin_cs2\win64\resourcecompiler.exe",
            @"E:\CSDK12\game\bin_cs2\win64\resourcecompiler.exe",
            @"E:\CSDK12\Reduced_CSDK_12\game\bin_cs2\win64\resourcecompiler.exe",
            @"E:\SteamLibrary\steamapps\common\CSDK12\game\bin_cs2\win64\resourcecompiler.exe",
            @"C:\CSDK12\game\bin\win64\resourcecompiler.exe",
            @"D:\CSDK12\game\bin\win64\resourcecompiler.exe",
            @"E:\CSDK12\game\bin\win64\resourcecompiler.exe"
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return null;
    }

    public async Task<string> AutoDetectDeadlockPathAsync()
    {
        return await Task.Run(() => 
        {
            try
            {
                // 1. Find Steam installation path via Registry (Windows specific)
                string steamPath = "";
                if (OperatingSystem.IsWindows())
                {
                    string[] defaultPaths = {
                        @"C:\Program Files (x86)\Steam",
                        @"C:\Program Files\Steam"
                    };

                    foreach (var path in defaultPaths)
                    {
                        if (Directory.Exists(path))
                        {
                            steamPath = path;
                            break;
                        }
                    }

                    #pragma warning disable CA1416 // Validate platform compatibility
                    try
                    {
                        using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ?? 
                                       Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam"))
                        {
                            if (key != null)
                            {
                                var val = key.GetValue("InstallPath") as string;
                                if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                                {
                                    steamPath = val;
                                }
                            }
                        }
                    }
                    catch { /* Ignore registry errors */ }
                    #pragma warning restore CA1416
                }
                else if (OperatingSystem.IsLinux())
                {
                    steamPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local/share/Steam");
                }
                
                if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
                    return null;

                // 2. Check main steam path
                if (ValidateDeadlockPath(steamPath))
                    return Path.Combine(steamPath, "steamapps", "common", "Deadlock");

                // 3. Parse libraryfolders.vdf
                string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    string content = File.ReadAllText(vdfPath);
                    // Match "path" "C:\\SteamLibrary"
                    var matches = Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\"");
                    foreach (Match m in matches)
                    {
                        if (m.Groups.Count > 1)
                        {
                            // VDF escapes backslashes
                            string libPath = m.Groups[1].Value.Replace("\\\\", "\\");
                            if (ValidateDeadlockPath(libPath))
                            {
                                return Path.Combine(libPath, "steamapps", "common", "Deadlock");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[GamePathManager] Auto-detect failed: {ex.Message}");
            }
            return null;
        });
    }

    public bool ValidateDeadlockPath(string baseLibraryPath)
    {
        // baseLibraryPath could be a steam library root OR the deadlock folder itself.
        string fullPath = Path.Combine(baseLibraryPath, DeadlockRelativePath);
        if (File.Exists(fullPath)) return true;

        string directPath = Path.Combine(baseLibraryPath, "game/citadel/pak01_dir.vpk");
        if (File.Exists(directPath)) return true;

        return false;
    }
}
