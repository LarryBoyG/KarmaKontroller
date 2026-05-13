using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class SetupBootstrap
{
    [STAThread]
    private static int Main()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "KarmaKontrollerSetup-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            string scriptPath = Path.Combine(tempDir, "KarmaKontroller-Install.ps1");
            string payloadPath = Path.Combine(tempDir, "payload.zip");
            ExtractResource("KarmaKontroller-Install.ps1", scriptPath);
            ExtractResource("payload.zip", payloadPath);

            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(powershell))
            {
                powershell = "powershell.exe";
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath),
                WorkingDirectory = tempDir,
                UseShellExecute = true
            };

            Process process = Process.Start(startInfo);
            if (process != null)
            {
                process.WaitForExit();
                return process.ExitCode;
            }
            return 1;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "KarmaKontroller Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
            }
        }
    }

    private static void ExtractResource(string resourceName, string path)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using (Stream input = assembly.GetManifestResourceStream(resourceName))
        {
            if (input == null)
            {
                throw new InvalidOperationException("Missing setup resource: " + resourceName);
            }
            using (FileStream output = File.Create(path))
            {
                input.CopyTo(output);
            }
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
