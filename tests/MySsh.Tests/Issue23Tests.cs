using System.Diagnostics;

internal sealed class Issue23Tests : IRegressionCase
{
    public string Name => "#23 downloaded Linux archives have relocatable SHA-256 manifests";

    public async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var root = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var publish = root.File("published files");
        var release = root.File("release files");
        var downloaded = root.File("downloaded elsewhere");
        Directory.CreateDirectory(publish);
        Directory.CreateDirectory(downloaded);
        string[] files = ["my-ssh", "myssh", "LICENSE", "Terminal.Gui.LICENSE"];
        foreach (var file in files) await File.WriteAllTextAsync(Path.Combine(publish, file), "packaging fixture", ct);
        var script = Path.Combine(AppContext.BaseDirectory, "package-linux-release.sh");
        const string archive = "my-ssh-v1.2.3-linux-x64.tar.gz";
        var package = await RunAsync("bash", root.Path, ct, script, publish, release, archive);
        RegressionCases.Check(package.Code == 0, "Packaging failed: " + package.Text);
        File.Copy(Path.Combine(release, archive), Path.Combine(downloaded, archive));
        File.Copy(Path.Combine(release, archive + ".sha256"), Path.Combine(downloaded, archive + ".sha256"));
        var checksum = await File.ReadAllTextAsync(Path.Combine(downloaded, archive + ".sha256"), ct);
        RegressionCases.Check(checksum.TrimEnd().EndsWith("  " + archive, StringComparison.Ordinal) && !checksum.Contains('/'),
            "The manifest includes a build-directory path.");
        var verified = await RunAsync("sha256sum", downloaded, ct, "-c", "--", archive + ".sha256");
        RegressionCases.Check(verified.Code == 0, "A relocated asset failed verification: " + verified.Text);
        var listing = await RunAsync("tar", downloaded, ct, "-tzf", archive);
        RegressionCases.Check(listing.Code == 0 && listing.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Order().SequenceEqual(files.Order()), "The release archive lost required files.");
        await File.AppendAllTextAsync(Path.Combine(downloaded, archive), "corruption", ct);
        var corrupted = await RunAsync("sha256sum", downloaded, ct, "-c", "--", archive + ".sha256");
        RegressionCases.Check(corrupted.Code != 0, "Checksum verification accepted changed data.");
    }

    private static async Task<(int Code, string Text)> RunAsync(string executable, string cwd, CancellationToken ct, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start packaging test process.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
