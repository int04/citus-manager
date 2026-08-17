using System.Diagnostics;
using System.Globalization;
using System.Text;
using CitusManager.Domain;
using CitusManager.Security;

namespace CitusManager.Services;

public sealed record PostgresToolResult(long Bytes, string Diagnostic, TimeSpan Duration);
public sealed record PostgresToolchainInfo(int Major, string PgDumpPath, string PgRestorePath,
    string PgDumpVersion, string PgRestoreVersion);
public sealed class PostgresToolException(string tool, int exitCode, string diagnostic)
    : InvalidOperationException($"{tool} failed with exit code {exitCode}: {diagnostic}")
{
    public string Tool { get; } = tool;
    public int ExitCode { get; } = exitCode;
    public string Diagnostic { get; } = diagnostic;
}

public interface IPostgresToolRunner
{
    Task<string> ReadVersionAsync(string tool, CancellationToken cancellationToken);
    Task<PostgresToolchainInfo> ResolveToolchainAsync(int postgresMajor, CancellationToken cancellationToken);
    Task<PostgresToolResult> DumpAsync(
        ClusterProfile source, int postgresMajor, Stream destination, Func<long, ValueTask>? progress,
        CancellationToken cancellationToken);
    Task<PostgresToolResult> RestoreFileAsync(
        ClusterProfile target, int postgresMajor, string archivePath, string section, bool clean, int jobs,
        Func<long, ValueTask>? progress, CancellationToken cancellationToken);
    Task<PostgresToolResult> RestoreStreamAsync(
        ClusterProfile target, int postgresMajor, Stream archive, string section, bool clean,
        Func<long, ValueTask>? progress, CancellationToken cancellationToken);
    Task<PostgresToolResult> ListAsync(int postgresMajor, string archivePath, CancellationToken cancellationToken);
    Task<PostgresToolResult> ListStreamAsync(int postgresMajor, Stream archive, CancellationToken cancellationToken);
}

public sealed class PostgresToolchainOptions
{
    public string PgDumpPath { get; set; } = "pg_dump";
    public string PgRestorePath { get; set; } = "pg_restore";
}

public sealed class PostgresToolOptions
{
    public const string SectionName = "Backup:PostgresTools";
    public string PgDumpPath { get; set; } = "pg_dump";
    public string PgRestorePath { get; set; } = "pg_restore";
    public Dictionary<int, PostgresToolchainOptions> Versions { get; set; } = [];
    public string Compression { get; set; } = "gzip:5";
    public int StallMinutes { get; set; } = 30;
    public int DiagnosticLimitCharacters { get; set; } = 32_768;
}

public sealed class PostgresToolRunner(
    IClusterSecretProtector secrets,
    Microsoft.Extensions.Options.IOptions<PostgresToolOptions> configured,
    ILogger<PostgresToolRunner> logger) : IPostgresToolRunner
{
    private readonly PostgresToolOptions _options = configured.Value;

    public async Task<string> ReadVersionAsync(string tool, CancellationToken cancellationToken)
    {
        var executable = tool switch
        {
            "pg_dump" => _options.PgDumpPath,
            "pg_restore" => _options.PgRestorePath,
            _ => tool
        };
        var result = await RunAsync(executable, ["--version"], null, null, null, cancellationToken);
        return result.Diagnostic.Trim();
    }

    public async Task<PostgresToolchainInfo> ResolveToolchainAsync(int postgresMajor, CancellationToken cancellationToken)
    {
        var candidates = _options.Versions.TryGetValue(postgresMajor, out var versioned)
            ? [versioned]
            : DiscoverToolchains(postgresMajor).ToList();
        var detected = new List<string>();
        foreach (var selected in candidates)
        {
            try
            {
                var dump = (await RunAsync(selected.PgDumpPath, ["--version"], null, null, null, cancellationToken)).Diagnostic.Trim();
                var restore = (await RunAsync(selected.PgRestorePath, ["--version"], null, null, null, cancellationToken)).Diagnostic.Trim();
                var dumpMajor = ParseMajor(dump);
                var restoreMajor = ParseMajor(restore);
                detected.Add($"{selected.PgDumpPath}={dumpMajor}, {selected.PgRestorePath}={restoreMajor}");
                if (dumpMajor == postgresMajor && restoreMajor == postgresMajor)
                    return new(postgresMajor, selected.PgDumpPath, selected.PgRestorePath, dump, restore);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                detected.Add($"{selected.PgDumpPath}/{selected.PgRestorePath} unavailable ({exception.GetType().Name})");
            }
        }
        throw new InvalidOperationException(
            $"PostgreSQL {postgresMajor} backup toolchain is required. Checked: {string.Join("; ", detected)}. " +
            $"Install the PostgreSQL {postgresMajor} client or configure Backup:PostgresTools:Versions:{postgresMajor}.");
    }

    private IEnumerable<PostgresToolchainOptions> DiscoverToolchains(int major)
    {
        yield return new() { PgDumpPath = _options.PgDumpPath, PgRestorePath = _options.PgRestorePath };
        var roots = new[]
        {
            $"/opt/homebrew/opt/postgresql@{major}/bin",
            $"/usr/local/opt/postgresql@{major}/bin",
            $"/usr/lib/postgresql/{major}/bin"
        };
        foreach (var root in roots)
        {
            var dump = Path.Combine(root, "pg_dump");
            var restore = Path.Combine(root, "pg_restore");
            if (File.Exists(dump) && File.Exists(restore))
                yield return new() { PgDumpPath = dump, PgRestorePath = restore };
        }
    }

    internal static int ParseMajor(string version)
    {
        var match = System.Text.RegularExpressions.Regex.Match(version, @"(?<!\d)(\d+)(?:\.\d+)?");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major))
            throw new InvalidOperationException($"Cannot parse PostgreSQL tool version: {version}");
        return major;
    }

    public async Task<PostgresToolResult> DumpAsync(
        ClusterProfile source, int postgresMajor, Stream destination, Func<long, ValueTask>? progress,
        CancellationToken cancellationToken)
    {
        var toolchain = await ResolveToolchainAsync(postgresMajor, cancellationToken);
        var args = BuildDumpArguments(source, _options.Compression);
        return await WithCredentialsAsync(source, (environment, token) =>
            RunAsync(toolchain.PgDumpPath, args, environment, destination, progress, token), cancellationToken);
    }

    internal static List<string> BuildDumpArguments(ClusterProfile source, string? compression)
    {
        var selectedCompression = string.IsNullOrWhiteSpace(compression) ? "gzip:5" : compression.Trim();
        var args = new List<string>
        {
            "--format=custom", $"--compress={selectedCompression}", "--verbose", "--no-password",
            "--host", source.Host, "--port", source.Port.ToString(CultureInfo.InvariantCulture),
            "--dbname", source.Database
        };
        if (!string.IsNullOrWhiteSpace(source.Username)) args.AddRange(["--username", source.Username]);
        return args;
    }

    public async Task<PostgresToolResult> RestoreFileAsync(
        ClusterProfile target, int postgresMajor, string archivePath, string section, bool clean, int jobs,
        Func<long, ValueTask>? progress, CancellationToken cancellationToken)
    {
        if (section is not ("pre-data" or "data" or "post-data"))
            throw new ArgumentException("Restore section must be pre-data, data, or post-data.", nameof(section));
        var args = new List<string>
        {
            "--format=custom", "--exit-on-error", "--verbose", "--no-password",
            "--section", section, "--host", target.Host,
            "--port", target.Port.ToString(CultureInfo.InvariantCulture), "--dbname", target.Database,
            "--jobs", Math.Clamp(jobs, 1, 32).ToString(CultureInfo.InvariantCulture)
        };
        if (clean && section == "pre-data") args.AddRange(["--clean", "--if-exists"]);
        if (!string.IsNullOrWhiteSpace(target.Username)) args.AddRange(["--username", target.Username]);
        args.Add(archivePath);
        var toolchain = await ResolveToolchainAsync(postgresMajor, cancellationToken);
        return await WithCredentialsAsync(target, (environment, token) =>
            RunAsync(toolchain.PgRestorePath, args, environment, null, progress, token), cancellationToken);
    }

    public async Task<PostgresToolResult> ListAsync(int postgresMajor, string archivePath, CancellationToken cancellationToken)
    {
        var toolchain = await ResolveToolchainAsync(postgresMajor, cancellationToken);
        return await RunAsync(toolchain.PgRestorePath, ["--list", archivePath], null, null, null, cancellationToken);
    }

    public async Task<PostgresToolResult> ListStreamAsync(int postgresMajor, Stream archive, CancellationToken cancellationToken)
    {
        var toolchain = await ResolveToolchainAsync(postgresMajor, cancellationToken);
        return await RunAsync(toolchain.PgRestorePath, ["--list"], null, null, null, cancellationToken, archive,
            drainInputAfterConsumerCloses: true);
    }

    public Task<PostgresToolResult> RestoreStreamAsync(
        ClusterProfile target, int postgresMajor, Stream archive, string section, bool clean,
        Func<long, ValueTask>? progress, CancellationToken cancellationToken)
    {
        if (section is not ("pre-data" or "data" or "post-data"))
            throw new ArgumentException("Restore section must be pre-data, data, or post-data.", nameof(section));
        var args = new List<string>
        {
            "--format=custom", "--exit-on-error", "--verbose", "--no-password", "--jobs", "1",
            "--section", section, "--host", target.Host, "--port", target.Port.ToString(CultureInfo.InvariantCulture),
            "--dbname", target.Database
        };
        if (clean && section == "pre-data") args.AddRange(["--clean", "--if-exists"]);
        if (!string.IsNullOrWhiteSpace(target.Username)) args.AddRange(["--username", target.Username]);
        return RestoreStreamCoreAsync(target, postgresMajor, archive, args, progress, cancellationToken);
    }

    private async Task<PostgresToolResult> RestoreStreamCoreAsync(ClusterProfile target, int postgresMajor,
        Stream archive, IReadOnlyList<string> args, Func<long, ValueTask>? progress, CancellationToken cancellationToken)
    {
        var toolchain = await ResolveToolchainAsync(postgresMajor, cancellationToken);
        return await WithCredentialsAsync(target, (environment, token) =>
            RunAsync(toolchain.PgRestorePath, args, environment, null, progress, token, archive), cancellationToken);
    }

    private async Task<T> WithCredentialsAsync<T>(
        ClusterProfile profile,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "citus-manager-pg", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pgpass = Path.Combine(directory, ".pgpass");
        try
        {
            var password = string.IsNullOrWhiteSpace(profile.ProtectedPassword)
                ? string.Empty : secrets.Unprotect(profile.ProtectedPassword);
            var line = string.Join(':', Escape(profile.Host), profile.Port, Escape(profile.Database),
                Escape(profile.Username ?? "*"), Escape(password));
            await File.WriteAllTextAsync(pgpass, line + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(pgpass, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PGPASSFILE"] = pgpass,
                ["PGSSLMODE"] = profile.SslMode switch
                {
                    ClusterSslMode.Disable => "disable", ClusterSslMode.Prefer => "prefer",
                    ClusterSslMode.Require => "require", ClusterSslMode.VerifyCa => "verify-ca",
                    ClusterSslMode.VerifyFull => "verify-full", _ => "prefer"
                },
                ["PGCONNECT_TIMEOUT"] = "30", ["PGCLIENTENCODING"] = "UTF8"
            };
            return await action(environment, cancellationToken);
        }
        finally
        {
            try { if (File.Exists(pgpass)) File.Delete(pgpass); Directory.Delete(directory, true); }
            catch (Exception exception) { logger.LogWarning("Failed to remove temporary PostgreSQL credential file ({ErrorType}).", exception.GetType().Name); }
        }
    }

    private async Task<PostgresToolResult> RunAsync(
        string executable, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment,
        Stream? stdoutDestination, Func<long, ValueTask>? progress, CancellationToken cancellationToken)
        => await RunAsync(executable, args, environment, stdoutDestination, progress, cancellationToken, null);

    private async Task<PostgresToolResult> RunAsync(
        string executable, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment,
        Stream? stdoutDestination, Func<long, ValueTask>? progress, CancellationToken cancellationToken,
        Stream? stdinSource, bool drainInputAfterConsumerCloses = false)
    {
        var start = DateTimeOffset.UtcNow;
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdinSource is not null,
            CreateNoWindow = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        using var registration = cancellationToken.Register(() => Kill(process));

        long lastActivity = Stopwatch.GetTimestamp();
        void Activity() => Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
        var stalled = false;
        using var watchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchdog = WatchStallAsync(process, () => Interlocked.Read(ref lastActivity), () => stalled = true,
            TimeSpan.FromMinutes(Math.Max(1, _options.StallMinutes)), watchdogCancellation.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, _options.DiagnosticLimitCharacters, Activity, cancellationToken);
        Task? stdinTask = null;
        if (stdinSource is not null)
            stdinTask = CopyInputAsync(stdinSource, process.StandardInput.BaseStream, Activity,
                drainInputAfterConsumerCloses, cancellationToken);
        Task<string>? stdoutTextTask = null;
        Task<long>? stdoutCountTask = null;
        Task stdoutTask;
        if (stdoutDestination is null)
        {
            stdoutTextTask = ReadBoundedAsync(process.StandardOutput, _options.DiagnosticLimitCharacters, Activity, cancellationToken);
            stdoutTask = stdoutTextTask;
        }
        else
        {
            stdoutCountTask = CopyProgressAsync(process.StandardOutput.BaseStream, stdoutDestination, progress, Activity, cancellationToken);
            stdoutTask = stdoutCountTask;
        }
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            if (stdinTask is not null) await stdinTask;
            await stdoutTask;
        }
        catch { Kill(process); throw; }
        finally
        {
            watchdogCancellation.Cancel();
            try { await watchdog; } catch (OperationCanceledException) { }
        }
        if (stalled) throw new TimeoutException($"{Path.GetFileName(executable)} produced no bytes or diagnostic heartbeat for {_options.StallMinutes} minute(s).");
        var stderr = await stderrTask;
        var stdoutText = stdoutTextTask is null ? string.Empty : await stdoutTextTask;
        if (process.ExitCode != 0)
            throw new PostgresToolException(Path.GetFileName(executable), process.ExitCode, SafeDiagnostic(stderr));
        var bytes = stdoutCountTask is null ? 0L : await stdoutCountTask;
        return new PostgresToolResult(bytes, string.IsNullOrWhiteSpace(stdoutText) ? stderr : stdoutText,
            DateTimeOffset.UtcNow - start);
    }

    internal static async Task CopyInputAsync(
        Stream source, Stream destination, Action activity, bool drainAfterDestinationCloses, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];
        var destinationOpen = true;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (destinationOpen)
            {
                try { await destination.WriteAsync(buffer.AsMemory(0, read), ct); }
                catch (IOException) when (drainAfterDestinationCloses) { destinationOpen = false; }
            }
            activity();
        }
        if (destinationOpen) await destination.FlushAsync(ct);
        try { destination.Close(); }
        catch (IOException) when (drainAfterDestinationCloses) { }
    }

    private static async Task<long> CopyProgressAsync(
        Stream source, Stream destination, Func<long, ValueTask>? progress, Action activity, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            activity();
            total += read;
            if (progress is not null) await progress(total);
        }
        await destination.FlushAsync(cancellationToken);
        return total;
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, int limit, Action activity, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var builder = new StringBuilder(Math.Min(limit, 8192));
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            activity();
            if (builder.Length + read > limit) builder.Remove(0, Math.Min(builder.Length, builder.Length + read - limit));
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    private static async Task WatchStallAsync(Process process, Func<long> lastActivity, Action stalled,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (process.HasExited) return;
            if (Stopwatch.GetElapsedTime(lastActivity()) < timeout) continue;
            stalled();
            Kill(process);
            return;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);
    private static string SafeDiagnostic(string value)
    {
        var line = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 2000 ? line : line[^2000..];
    }
}
