// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Arcanum.Data;

/// <summary>
/// Replacing a file with a freshly written one, on every file system. Some can't rename a file onto an existing one (a
/// console's memory card): there the old file steps aside as <c>.bak</c> first and is removed once the new one is in
/// place; <see cref="Recover"/> puts it back when the device stopped in between.
/// </summary>
public static class SafeFile
{
    /// <summary>Moves <paramref name="temp"/> onto <paramref name="path"/>, replacing what is there.</summary>
    public static void Replace(string temp, string path)
    {
        try
        {
            File.Move(temp, path, overwrite: true);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(path) || !File.Exists(temp)) throw;
        }
        var backup = path + ".bak";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(path, backup);
        try
        {
            File.Move(temp, path);
        }
        catch
        {
            File.Move(backup, path); // the old file stays as it was
            throw;
        }
        File.Delete(backup);
    }

    /// <summary>Puts back a file a cut-off <see cref="Replace"/> left aside (call before reading <paramref name="path"/>).</summary>
    public static void Recover(string path)
    {
        var backup = path + ".bak";
        if (!File.Exists(path) && File.Exists(backup)) File.Move(backup, path);
    }
}
