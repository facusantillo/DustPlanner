using System.CommandLine;

namespace DustPlanner.Cli;

/// <summary>Wraps command actions so user-facing failures print one line and exit with code 2 (System.CommandLine would otherwise print a stack trace and return 1).</summary>
internal static class CliAction
{
    public const int UserErrorExitCode = 2;

    public static Func<ParseResult, CancellationToken, Task<int>> Guarded(Func<ParseResult, CancellationToken, Task<int>> body) =>
        async (result, ct) =>
        {
            try
            {
                return await body(result, ct).ConfigureAwait(false);
            }
            catch (CliException ex)
            {
                await Console.Error.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
                return UserErrorExitCode;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is HttpRequestException || ex is InvalidOperationException)
            {
                await Console.Error.WriteLineAsync("error: " + ex.Message).ConfigureAwait(false);
                return UserErrorExitCode;
            }
        };
}
