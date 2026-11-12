using System.Diagnostics;

namespace Reaper.Core;

public sealed class ChildProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task output;
    private readonly Task error;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;

    public ChildProcess(string executable, IEnumerable<string> arguments, string workingDirectory, Action<string> log)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException($"Failed to start {executable}");
        process.StandardInput.Close();
        output = Pump(process.StandardOutput, log);
        error = Pump(process.StandardError, text => log("[stderr] " + text));
    }

    private static async Task Pump(StreamReader reader, Action<string> log)
    {
        while (await reader.ReadLineAsync() is { } line) log(line);
    }

    public async Task<int> WaitAsync(CancellationToken token)
    {
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { Kill(); throw; }
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    private void Kill()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        process.Dispose();
    }
}
