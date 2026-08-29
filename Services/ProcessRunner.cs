using System.Diagnostics;
using System.ComponentModel;

namespace SpeakerHeadphoneSwitch.Services;

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new AudioServiceException($"無法啟動 {fileName}。");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return new ProcessResult(
                process.ExitCode,
                await outputTask,
                await errorTask);
        }
        catch (Win32Exception exception)
        {
            throw new AudioServiceException($"找不到音訊工具 {fileName}。", exception);
        }
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public void ThrowIfFailed(string operation)
    {
        if (ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(StandardError)
                ? StandardOutput.Trim()
                : StandardError.Trim();

            throw new AudioServiceException(
                string.IsNullOrWhiteSpace(detail)
                    ? operation
                    : $"{operation}：{detail}");
        }
    }
}
