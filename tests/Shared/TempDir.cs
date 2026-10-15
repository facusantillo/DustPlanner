namespace DustPlanner.Tests;

/// <summary>
/// Deletes a temporary folder, retrying briefly: Windows keeps file handles alive for a moment after the owner is
/// disposed (child processes, SQLite), and a leftover temp folder must never fail a test on its own.
/// </summary>
internal static class TempDir
{
    public static void Delete(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(300);
            }
        }
    }
}
