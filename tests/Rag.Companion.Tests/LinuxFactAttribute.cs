namespace Rag.Companion.Tests;

/// <summary>
/// A fact that only runs where a POSIX shell, <c>/proc</c> and <c>ln -s</c> exist.
/// </summary>
/// <remarks>
/// The companion cases that use this drive a fake POSIX <c>soffice</c>: a <c>#!/bin/sh</c> script with no
/// extension, a child process tree observed through <c>/proc</c>, and a symlinked output file. The class used to
/// call a <c>SkipOnWindows()</c> helper whose body only returned, so on Windows those cases ran anyway — four
/// failed because Windows cannot execute an extension-less shell script, and four more asserted a thrown
/// <c>LibreOfficeException</c> and passed for the wrong reason, because the process failed to start rather than
/// doing what the case set up. Declaring the platform requirement here turns that silent gap into a reported
/// skip, and the reason names what would close it.
/// </remarks>
public sealed class LinuxFactAttribute : FactAttribute
{
    /// <summary>Marks the case skipped on any platform without a POSIX shell, <c>/proc</c> and <c>ln -s</c>.</summary>
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Drives a fake POSIX soffice (sh script, /proc, ln -s); this project has no Windows equivalent.";
        }
    }
}
