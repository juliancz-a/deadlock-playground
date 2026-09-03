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
