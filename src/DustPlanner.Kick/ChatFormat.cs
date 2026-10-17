using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DustPlanner.Kick;

/// <summary>Number formatting and the length limits of a chat message. Culture-free: the host runs with invariant globalization.</summary>
public static class ChatFormat
{
    /// <summary>Kick counts user-perceived characters (grapheme clusters); confirmed against its OpenAPI in K0.</summary>
    public const int MaxGraphemes = 500;

    /// <summary>Kick also caps the UTF-8 size of a message; confirmed against its OpenAPI in K0.</summary>
    public const int MaxUtf8Bytes = 2048;

    private const string Ellipsis = "…";

    /// <summary>Thousands separator by language: <c>1.240</c> in Spanish, <c>1,240</c> in English.</summary>
    public static string Number(string language, long value)
    {
        var digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        var separator = language == ChatTexts.Spanish ? '.' : ',';
        var sb = new StringBuilder(digits.Length + digits.Length / 3 + 1);
        if (value < 0)
        {
            sb.Append('-');
        }

        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                sb.Append(separator);
            }

            sb.Append(digits[i]);
        }

        return sb.ToString();
    }

    /// <summary><c>70 %</c> in Spanish (space before the sign, as the RAE writes it), <c>70%</c> in English; <c>—</c> when unknown.</summary>
    public static string Percent(string language, double? ratio)
    {
        if (ratio is null)
        {
            return "—";
        }

        var value = (int)Math.Round(ratio.Value * 100, MidpointRounding.AwayFromZero);
        return language == ChatTexts.Spanish ? value + " %" : value + "%";
    }

    /// <summary>One line, trimmed, within both chat limits (an ellipsis marks a cut), never empty.</summary>
    public static string Fit(string text)
    {
        var line = (text ?? string.Empty).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        while (line.Contains("  ", StringComparison.Ordinal))
        {
            line = line.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (line.Length == 0)
        {
            return "—";
        }

        var info = new StringInfo(line);
        if (info.LengthInTextElements <= MaxGraphemes && Encoding.UTF8.GetByteCount(line) <= MaxUtf8Bytes)
        {
            return line;
        }

        var keep = Math.Min(info.LengthInTextElements, MaxGraphemes) - 1;
        var cut = info.SubstringByTextElements(0, keep);
        while (Encoding.UTF8.GetByteCount(cut) + Encoding.UTF8.GetByteCount(Ellipsis) > MaxUtf8Bytes && keep > 0)
        {
            keep--;
            cut = info.SubstringByTextElements(0, keep);
        }

        return cut.TrimEnd() + Ellipsis;
    }

    /// <summary>A reply must never start with the prefix, or a bot reading its own messages would trigger a command.</summary>
    public static string WithoutLeadingPrefix(string text, string prefix)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrEmpty(prefix) || !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return text;
        }

        return "· " + text;
    }

    /// <summary>
    /// The language the streamer's Windows shows its UI in, reduced to <c>es</c> or <c>en</c>. The host runs with invariant
    /// globalization, so <see cref="CultureInfo.CurrentUICulture"/> cannot tell; Windows is asked directly and other
    /// systems fall back to <c>LANG</c>.
    /// </summary>
    public static string SystemLanguage()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Primary language id 0x0A is Spanish in every regional variant.
                return (NativeMethods.GetUserDefaultUILanguage() & 0x3FF) == 0x0A ? ChatTexts.Spanish : ChatTexts.English;
            }

            var lang = Environment.GetEnvironmentVariable("LC_ALL") ?? Environment.GetEnvironmentVariable("LANG") ?? string.Empty;
            return lang.StartsWith("es", StringComparison.OrdinalIgnoreCase) ? ChatTexts.Spanish : ChatTexts.English;
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is System.Security.SecurityException)
        {
            return ChatTexts.English;
        }
    }

    private static class NativeMethods
    {
        // DllImport rather than LibraryImport: the generated marshalling needs unsafe code, which this assembly does not enable.
#pragma warning disable SYSLIB1054
        [DllImport("kernel32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern ushort GetUserDefaultUILanguage();
#pragma warning restore SYSLIB1054
    }
}
