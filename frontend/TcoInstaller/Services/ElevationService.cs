using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TcoInstaller.Contracts;

namespace TcoInstaller.Services;

/// <summary>Serializes typed requests and relaunches the same executable through Windows UAC.</summary>
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
        InstallerRequest request)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Elevation is only available on Windows.");

        try
        {
            var requestPayload = CreatePayload(request);

            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };

            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var assemblyPath = Environment.GetCommandLineArgs()
                    .FirstOrDefault(argument => argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
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
            var envelope = JsonSerializer.Deserialize<ElevationEnvelope>(json, JsonOptions);
            return envelope is { Schema: 1 } && Enum.IsDefined(envelope.Request.Action) ? envelope : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }

    public static string CreatePayload(InstallerRequest request)
    {
        var envelope = new ElevationEnvelope(1, request);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions)));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
