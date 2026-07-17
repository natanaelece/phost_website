using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PremierAPI.Services;

public sealed class AdminMaintenanceService
{
    private const string StateDirectory = "/run/premierapi-maintenance";
    private static readonly Regex JobIdPattern = new("^[a-f0-9]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> TerminalPhases = new(StringComparer.Ordinal)
    {
        "success", "warning", "failed"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AdminMaintenanceService> _logger;

    public AdminMaintenanceService(IWebHostEnvironment environment, ILogger<AdminMaintenanceService> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public async Task<AdminMaintenanceStartResult> StartAsync(string operation)
    {
        operation = operation.Trim().ToLowerInvariant();
        if (operation is not ("publish" or "restart"))
            return new AdminMaintenanceStartResult(false, "Operação de manutenção inválida.", null);

        Directory.CreateDirectory(StateDirectory);
        CleanupOldStateFiles();
        var active = FindActiveJob();
        if (active != null)
            return new AdminMaintenanceStartResult(false, "Já existe uma manutenção em andamento.", active);

        string jobId = Guid.NewGuid().ToString("N");
        var queued = new AdminMaintenanceStatus(
            jobId, operation, "queued", "Preparando manutenção...", 0, DateTimeOffset.UtcNow, null);
        await WriteStatusAsync(queued);

        string scriptPath = Path.Combine(_environment.ContentRootPath, "scripts", "admin-maintenance.sh");
        if (!File.Exists(scriptPath))
        {
            var failed = queued with { Phase = "failed", Message = "Script de manutenção não encontrado.", UpdatedAt = DateTimeOffset.UtcNow };
            await WriteStatusAsync(failed);
            return new AdminMaintenanceStartResult(false, failed.Message, failed);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/systemd-run",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--quiet");
        startInfo.ArgumentList.Add($"--unit=premierapi-maintenance-{jobId}");
        startInfo.ArgumentList.Add("--description=PremierAPI admin maintenance");
        startInfo.ArgumentList.Add("--property=Type=exec");
        startInfo.ArgumentList.Add("--property=TimeoutStartSec=10min");
        startInfo.ArgumentList.Add("/bin/bash");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add(jobId);

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) throw new InvalidOperationException("Não foi possível iniciar systemd-run.");
            string standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                _logger.LogError("[ADMIN][MANUTENCAO] Falha ao iniciar job {JobId}: {Error}", jobId, standardError.Trim());
                var failed = queued with { Phase = "failed", Message = "Não foi possível iniciar a manutenção.", UpdatedAt = DateTimeOffset.UtcNow };
                await WriteStatusAsync(failed);
                return new AdminMaintenanceStartResult(false, failed.Message, failed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ADMIN][MANUTENCAO] Falha ao iniciar job {JobId}.", jobId);
            var failed = queued with { Phase = "failed", Message = "Não foi possível iniciar a manutenção.", UpdatedAt = DateTimeOffset.UtcNow };
            await WriteStatusAsync(failed);
            return new AdminMaintenanceStartResult(false, failed.Message, failed);
        }

        _logger.LogWarning("[ADMIN][MANUTENCAO] Job {JobId} iniciado com operação {Operation}.", jobId, operation);
        return new AdminMaintenanceStartResult(true, "Manutenção iniciada.", queued);
    }

    public async Task<AdminMaintenanceStatus?> GetStatusAsync(string jobId)
    {
        if (!JobIdPattern.IsMatch(jobId)) return null;
        string statusPath = GetStatusPath(jobId);
        if (!File.Exists(statusPath)) return null;

        try
        {
            string json = await File.ReadAllTextAsync(statusPath);
            var status = JsonSerializer.Deserialize<AdminMaintenanceStatus>(json, JsonOptions);
            if (status == null) return null;
            if (status.Phase is "warning" or "failed")
                status = status with { LogTail = await ReadLogTailAsync(jobId) };
            return status;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ADMIN][MANUTENCAO] Não foi possível ler o status do job {JobId}.", jobId);
            return null;
        }
    }

    private AdminMaintenanceStatus? FindActiveJob()
    {
        foreach (string file in Directory.EnumerateFiles(StateDirectory, "*.json"))
        {
            try
            {
                var status = JsonSerializer.Deserialize<AdminMaintenanceStatus>(File.ReadAllText(file), JsonOptions);
                if (status != null && !TerminalPhases.Contains(status.Phase) && status.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-15))
                    return status;
            }
            catch { }
        }
        return null;
    }

    private void CleanupOldStateFiles()
    {
        foreach (string file in Directory.EnumerateFiles(StateDirectory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-2)) File.Delete(file);
            }
            catch { }
        }
    }

    private static async Task WriteStatusAsync(AdminMaintenanceStatus status)
    {
        string json = JsonSerializer.Serialize(status, JsonOptions);
        await File.WriteAllTextAsync(GetStatusPath(status.JobId), json);
    }

    private static async Task<string?> ReadLogTailAsync(string jobId)
    {
        string path = Path.Combine(StateDirectory, $"{jobId}.log");
        if (!File.Exists(path)) return null;
        string[] lines = await File.ReadAllLinesAsync(path);
        string tail = string.Join('\n', lines.TakeLast(35));
        return tail.Length <= 6000 ? tail : tail[^6000..];
    }

    private static string GetStatusPath(string jobId) => Path.Combine(StateDirectory, $"{jobId}.json");
}

public sealed record AdminMaintenanceStatus(
    string JobId,
    string Operation,
    string Phase,
    string Message,
    int Warnings,
    DateTimeOffset UpdatedAt,
    string? LogTail);

public sealed record AdminMaintenanceStartResult(bool Success, string Message, AdminMaintenanceStatus? Status);
