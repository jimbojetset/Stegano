using System;
using System.ComponentModel;
using System.Diagnostics;

namespace Stegano
{
    internal static class FileDialogs
    {
        public static string? OpenFile(string title = "Open file") =>
            Show(DialogType.Open, title, "");

        public static string? SaveFile(string title = "Save file", string defaultFileName = "") =>
            Show(DialogType.Save, title, defaultFileName);

        public static string? SelectFolder(string title = "Select folder") =>
            Show(DialogType.Folder, title, "");

        private enum DialogType { Open, Save, Folder }

        // A null result means cancellation. Missing desktop tools and dialog errors remain errors.
        private static string? Show(DialogType type, string title, string defaultFileName)
        {
            ArgumentNullException.ThrowIfNull(title);
            ArgumentNullException.ThrowIfNull(defaultFileName);
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                string dialog = type switch
                {
                    DialogType.Open => """
                        $dialog = New-Object System.Windows.Forms.OpenFileDialog
                        $dialog.Title = $env:STEGANO_DIALOG_TITLE
                        $dialog.CheckFileExists = $true
                        """,
                    DialogType.Save => """
                        $dialog = New-Object System.Windows.Forms.SaveFileDialog
                        $dialog.Title = $env:STEGANO_DIALOG_TITLE
                        $dialog.FileName = $env:STEGANO_DIALOG_NAME
                        $dialog.OverwritePrompt = $true
                        """,
                    _ => """
                        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
                        $dialog.Description = $env:STEGANO_DIALOG_TITLE
                        """
                };
                string selectedPath = type == DialogType.Folder ? "$dialog.SelectedPath" : "$dialog.FileName";
                start = new ProcessStartInfo("powershell.exe");
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-STA");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add($$"""
                    $ErrorActionPreference = 'Stop'
                    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding
                    Add-Type -AssemblyName System.Windows.Forms
                    {{dialog}}
                    try {
                        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
                            [Console]::WriteLine({{selectedPath}})
                        }
                    } finally {
                        $dialog.Dispose()
                    }
                    """);
                // Environment variables keep quotes in titles and filenames out of the script.
                start.Environment["STEGANO_DIALOG_TITLE"] = title;
                start.Environment["STEGANO_DIALOG_NAME"] = defaultFileName;
            }
            else if (OperatingSystem.IsMacOS())
            {
                string choose = type switch
                {
                    DialogType.Open => "choose file with prompt (item 1 of argv)",
                    DialogType.Save => "choose file name with prompt (item 1 of argv) default name (item 2 of argv)",
                    _ => "choose folder with prompt (item 1 of argv)"
                };
                start = new ProcessStartInfo("/usr/bin/osascript");
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add($"""
                    on run argv
                        try
                            return POSIX path of ({choose})
                        on error messageText number errorNumber
                            if errorNumber is -128 then return ""
                            error messageText number errorNumber
                        end try
                    end run
                    """);
                start.ArgumentList.Add("--");
                start.ArgumentList.Add(title);
                start.ArgumentList.Add(defaultFileName);
            }
            else if (OperatingSystem.IsLinux())
            {
                start = new ProcessStartInfo("zenity");
                start.ArgumentList.Add("--file-selection");
                start.ArgumentList.Add("--title=" + title);
                if (type == DialogType.Save)
                {
                    start.ArgumentList.Add("--save");
                    start.ArgumentList.Add("--confirm-overwrite");
                    start.ArgumentList.Add("--filename=" + defaultFileName);
                }
                else if (type == DialogType.Folder)
                    start.ArgumentList.Add("--directory");
            }
            else
                throw new PlatformNotSupportedException("File dialogs are supported on Windows, macOS, and Linux.");

            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = System.Text.Encoding.UTF8;
            start.StandardErrorEncoding = System.Text.Encoding.UTF8;
            using var process = new Process { StartInfo = start };
            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException($"Could not open the file dialog using {start.FileName}. "
                    + "A desktop session is required; on Linux, install zenity.", ex);
            }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            if (OperatingSystem.IsLinux() && process.ExitCode == 1 && string.IsNullOrWhiteSpace(error))
                return null;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"File dialog failed ({process.ExitCode}): {error.Trim()}");

            // Remove only the tool's line ending, preserving spaces in the selected path.
            if (output.EndsWith("\r\n", StringComparison.Ordinal))
                output = output[..^2];
            else if (output.EndsWith('\n'))
                output = output[..^1];
            return output.Length == 0 ? null : output;
        }
    }
}
