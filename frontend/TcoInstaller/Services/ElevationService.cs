using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using TcoInstaller.Models;

namespace TcoInstaller.Services;

public static class ElevationService
{
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static (bool Started, string? Error) RelaunchElevated(
        string packageRoot,
        InstallerRequest request)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Elevation is only available on Windows.");

        try
        {
            var envelope = new ElevationEnvelope(packageRoot, request);
            var requestPayload = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)));

            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                WorkingDirectory = packageRoot,
                UseShellExecute = true,
                Verb = "runas"
            };

            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var assemblyPath = Assembly.GetEntryAssembly()?.Location
                    ?? throw new InvalidOperationException("The application assembly path is unavailable.");
                startInfo.ArgumentList.Add(assemblyPath);
            }

            startInfo.ArgumentList.Add("--request");
            startInfo.ArgumentList.Add(requestPayload);
            Process.Start(startInfo);
            return (true, null);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return (false, "Administrator approval was cancelled.");
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    public static ElevationEnvelope? ReadRequest(string? requestPayload)
    {
        if (string.IsNullOrWhiteSpace(requestPayload))
            return null;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(requestPayload));
            return JsonSerializer.Deserialize<ElevationEnvelope>(json);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }
}
