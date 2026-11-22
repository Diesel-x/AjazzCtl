using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace AjazzCtl;

public static class UninstallHelper
{
    public static bool IsSafeToDeleteDirectory(string? dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(dirPath).TrimEnd('\\', '/');
        }
        catch
        {
            return false;
        }

        string? root = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return false;

        string folderName = Path.GetFileName(full);
        if (!folderName.Contains("ajazz", StringComparison.OrdinalIgnoreCase))
            return false;

        Environment.SpecialFolder[] protectedFolders =
        [
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.Personal,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86
        ];

        foreach (var sf in protectedFolders)
        {
            string p = Environment.GetFolderPath(sf).TrimEnd('\\', '/');
            if (!string.IsNullOrEmpty(p) && string.Equals(full, p, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            string downloads = Path.Combine(userProfile, "Downloads").TrimEnd('\\', '/');
            if (string.Equals(full, downloads, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (Directory.Exists(full))
        {
            if (Directory.Exists(Path.Combine(full, ".git")))
                return false;
            if (Directory.EnumerateFiles(full, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                return false;
            if (Directory.EnumerateFiles(full, "*.cs", SearchOption.TopDirectoryOnly).Any())
                return false;
        }

        return true;
    }

    public static void PerformFullUninstall(string? processPath = null)
    {
        // 1. Remove Windows startup value if registered
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            runKey?.DeleteValue("AjazzCtl", throwOnMissingValue: false);
        }
        catch
        {
            // Ignore registry permission errors
        }

        // 2. Remove %LocalAppData%\AjazzCtl and %AppData%\AjazzCtl
        foreach (var sf in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            try
            {
                string baseDir = Environment.GetFolderPath(sf);
                if (!string.IsNullOrWhiteSpace(baseDir))
                {
                    string appDataDir = Path.Combine(baseDir, "AjazzCtl");
                    if (Directory.Exists(appDataDir))
                        Directory.Delete(appDataDir, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup failure
            }
        }

        // 3. Schedule detached deletion of executable (or portable folder) after exit
        string? exePath = processPath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return;

        string? appDir = Path.GetDirectoryName(exePath);
        string cmdArgs;
        if (IsSafeToDeleteDirectory(appDir))
        {
            cmdArgs = $"/C ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{appDir}\"";
        }
        else
        {
            var targets = new List<string> { exePath };
            if (!string.IsNullOrEmpty(appDir))
            {
                string[] companions = ["AjazzCtl.dll", "AjazzCtl.deps.json", "AjazzCtl.runtimeconfig.json", "AjazzCtl.pdb"];
                foreach (string name in companions)
                {
                    string p = Path.Combine(appDir, name);
                    if (File.Exists(p))
                        targets.Add(p);
                }
            }
            string deleteCommands = string.Join(" & ", targets.Select(t => $"del /f /q \"{t}\""));
            cmdArgs = $"/C ping 127.0.0.1 -n 3 > nul & {deleteCommands}";
        }

        Process.Start(new ProcessStartInfo("cmd.exe", cmdArgs)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
