using Rag.Companion.LibreOffice;

namespace Rag.Companion.Tests;

public sealed class LibreOfficeRunnerTests : IDisposable
{
    // Common shell preamble: derive the --outdir value and the source path (last argument),
    // then compute the expected output filename (source basename minus extension, plus .txt).
    private const string ParsePreamble = """
out=""
prev=""
for a in "$@"; do
  if [ "$prev" = "--outdir" ]; then out="$a"; fi
  prev="$a"
done
src="$prev"
name="$(basename "${src%.*}")"

""";

    private readonly string _base;

    public LibreOfficeRunnerTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "lo-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose() => TryDelete(_base);

    [LinuxFact]
    public async Task ConvertAsync_returns_single_utf8_output_on_success()
    {
        var (script, profile, output, source) = Setup(ParsePreamble + "printf 'converted text\\n' > \"$out/$name.txt\"\n");

        var text = await new LibreOfficeRunner(script).ConvertAsync(source, profile, output);

        Assert.Equal("converted text\n", text);
    }

    [LinuxFact]
    public async Task ConvertAsync_passes_metachar_filename_as_a_single_argument()
    {
        var source = Path.Combine(_base, "doc $HOME & 'quoted' (1);x.doc");
        var argvFile = Path.Combine(_base, "argv.txt");
        var (script, profile, output, _) = Setup(
            ParsePreamble + $"printf '%s\\n' \"$@\" > \"{argvFile}\"\nprintf 'ok\\n' > \"$out/$name.txt\"\n",
            source);

        await new LibreOfficeRunner(script).ConvertAsync(source, profile, output);

        var lines = await File.ReadAllLinesAsync(argvFile);
        Assert.Contains(source, lines);
    }

    [LinuxFact]
    public async Task ConvertAsync_fails_when_binary_does_not_exist()
    {
        var source = Path.Combine(_base, "input.doc");
        var profile = Path.Combine(_base, "profile");
        var output = Path.Combine(_base, "out");

        var ex = await Assert.ThrowsAsync<LibreOfficeException>(() =>
            new LibreOfficeRunner(Path.Combine(_base, "missing-soffice")).ConvertAsync(source, profile, output));

        Assert.NotNull(ex);
    }

    [LinuxFact]
    public async Task ConvertAsync_fails_on_missing_output()
    {
        var (script, profile, output, source) = Setup(ParsePreamble); // exits 0, produces nothing

        await Assert.ThrowsAsync<LibreOfficeException>(() =>
            new LibreOfficeRunner(script).ConvertAsync(source, profile, output));
    }

    [LinuxFact]
    public async Task ConvertAsync_fails_on_extra_output()
    {
        var (script, profile, output, source) = Setup(ParsePreamble + """
printf 'one\n' > "$out/$name.txt"
printf 'two\n' > "$out/other.txt"
""");

        await Assert.ThrowsAsync<LibreOfficeException>(() =>
            new LibreOfficeRunner(script).ConvertAsync(source, profile, output));
    }

    [LinuxFact]
    public async Task ConvertAsync_fails_on_reparse_output()
    {
        var (script, profile, output, source) = Setup(ParsePreamble + """
printf 'real\n' > "$out/real.txt"
ln -s real.txt "$out/$name.txt"
""");

        await Assert.ThrowsAsync<LibreOfficeException>(() =>
            new LibreOfficeRunner(script).ConvertAsync(source, profile, output));
    }

    [LinuxFact]
    public async Task ConvertAsync_fails_on_nonzero_exit()
    {
        var (script, profile, output, source) = Setup(ParsePreamble + "exit 3\n");

        var ex = await Assert.ThrowsAsync<LibreOfficeException>(() =>
            new LibreOfficeRunner(script).ConvertAsync(source, profile, output));

        Assert.Contains("code 3", ex.Message);
    }

    [LinuxFact]
    public async Task ConvertAsync_times_out_and_kills_the_process_tree()
    {
        var childPidFile = Path.Combine(_base, "child.pid");
        var (script, profile, output, source) = Setup(ParsePreamble + $"""
sleep 30 &
echo $! > "{childPidFile}"
wait
""");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<LibreOfficeException>(() =>
            new LibreOfficeRunner(script, TimeSpan.FromMilliseconds(500)).ConvertAsync(source, profile, output));
        sw.Stop();

        Assert.Contains("timeout", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Expected a prompt timeout, took {sw.Elapsed}.");

        var childPid = int.Parse(await File.ReadAllTextAsync(childPidFile));
        Assert.True(await WaitUntilDeadAsync(childPid, TimeSpan.FromSeconds(3)), $"Child process {childPid} was not killed.");
    }

    private (string Script, string Profile, string Output, string Source) Setup(string body, string? source = null)
    {
        var script = Path.Combine(_base, "soffice");
        File.WriteAllText(script, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var profile = Path.Combine(_base, "profile");
        var output = Path.Combine(_base, "out");
        var src = source ?? Path.Combine(_base, "input.doc");
        return (script, profile, output, src);
    }

    private static async Task<bool> WaitUntilDeadAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!Directory.Exists($"/proc/{pid}"))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return !Directory.Exists($"/proc/{pid}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
