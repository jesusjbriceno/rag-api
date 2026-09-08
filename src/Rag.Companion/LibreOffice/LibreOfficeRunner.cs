using System.Diagnostics;
using System.Text;
using Rag.Companion.Snapshot;

namespace Rag.Companion.LibreOffice;

/// <summary>
/// Runs a pinned LibreOffice <c>soffice.com</c> (26.8 x64) to convert one DOC file to UTF-8 text.
/// The process is started without a shell (<see cref="ProcessStartInfo.UseShellExecute"/> is false)
/// and receives arguments only through <see cref="ProcessStartInfo.ArgumentList"/>, so metacharacter
/// filenames are never shell-interpreted. A dedicated user profile and output directory isolate the
/// run, stdin is closed immediately, and the whole process tree is killed after a 60-second timeout.
/// Output is validated to be exactly one non-reparse UTF-8 text file.
/// </summary>
public sealed class LibreOfficeRunner : ILibreOfficeRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly string _sofficePath;
    private readonly TimeSpan _timeout;

    public LibreOfficeRunner(string sofficePath, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sofficePath);
        _sofficePath = sofficePath;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<string> ConvertAsync(
        string sourceDocPath,
        string profileDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDocPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var sourceFullPath = Path.GetFullPath(sourceDocPath);
        var profileFullPath = Path.GetFullPath(profileDirectory);
        var outputFullPath = Path.GetFullPath(outputDirectory);

        Directory.CreateDirectory(profileFullPath);
        Directory.CreateDirectory(outputFullPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _sofficePath,
            UseShellExecute = false,
            // Standard output/error are inherited (not redirected) so LibreOffice can never deadlock
            // on a full pipe buffer; stdin is redirected and closed immediately (no stdin).
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        // Arguments are passed verbatim through ArgumentList (never a shell command string).
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add("txt:Text (encoded):UTF8");
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(outputFullPath);
        startInfo.ArgumentList.Add($"-env:UserInstallation={ToFileUrl(profileFullPath)}");
        startInfo.ArgumentList.Add(sourceFullPath);

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            try
            {
                if (!process.Start())
                {
                    throw new LibreOfficeException("LibreOffice process failed to start.");
                }
            }
            catch (Exception ex) when (ex is not LibreOfficeException)
            {
                throw new LibreOfficeException($"LibreOffice process failed to start: {ex.Message}", ex);
            }

            // No stdin: close it immediately so the child cannot block waiting for input.
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillTree(process);
                throw new LibreOfficeException($"LibreOffice exceeded the {_timeout.TotalSeconds:0}-second timeout.");
            }

            if (process.ExitCode != 0)
            {
                throw new LibreOfficeException($"LibreOffice exited with code {process.ExitCode}.");
            }

            return ReadSingleOutput(outputFullPath);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string ReadSingleOutput(string outputDirectory)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(outputDirectory);
        }
        catch (IOException ex)
        {
            throw new LibreOfficeException("Unable to enumerate the LibreOffice output directory.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LibreOfficeException("Access to the LibreOffice output directory was denied.", ex);
        }

        if (files.Length == 0)
        {
            throw new LibreOfficeException("LibreOffice produced no output file.");
        }

        if (files.Length > 1)
        {
            throw new LibreOfficeException($"LibreOffice produced {files.Length} output files; expected exactly one.");
        }

        var outputPath = files[0];
        if (!string.Equals(Path.GetExtension(outputPath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            throw new LibreOfficeException("LibreOffice output is not a .txt file.");
        }

        if (ConfinedSnapshotService.IsLink(new FileInfo(outputPath)))
        {
            throw new LibreOfficeException("LibreOffice output is a reparse point, symlink, or junction.");
        }

        try
        {
            return File.ReadAllText(outputPath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true));
        }
        catch (DecoderFallbackException ex)
        {
            throw new LibreOfficeException("LibreOffice output is not valid UTF-8.", ex);
        }
        catch (IOException ex)
        {
            throw new LibreOfficeException("Unable to read the LibreOffice output file.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LibreOfficeException("Access to the LibreOffice output file was denied.", ex);
        }
    }

    private static string ToFileUrl(string path) => new Uri(path).AbsoluteUri;

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best effort: the timeout exception is what the caller reports; a failed kill must not mask it.
        }
    }
}
