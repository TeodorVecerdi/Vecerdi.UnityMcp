using System.Text;
using System.Text.RegularExpressions;

namespace UnityMcp;

/// <summary>
/// A single compiler diagnostic parsed from a Unity console log line. Unity emits compiler
/// messages in the canonical MSBuild shape
/// <c>Assets/Scripts/Foo.cs(12,20): error CS0103: The name 'x' does not exist ...</c>,
/// which this type decomposes into structured <see cref="File"/>/<see cref="Line"/>/<see cref="Code"/>
/// fields so callers no longer have to scrape raw strings.
/// </summary>
public sealed record CompilerDiagnostic {
    /// <summary>Source file path as Unity reported it (usually project-relative, e.g. <c>Assets/...</c>).</summary>
    public string? File { get; init; }

    /// <summary>1-based line number, or <c>null</c> when the message carried no location.</summary>
    public int? Line { get; init; }

    /// <summary>1-based column number, or <c>null</c> when the message carried no location.</summary>
    public int? Column { get; init; }

    /// <summary>Diagnostic code such as <c>CS0103</c>, or <c>null</c> when none was present.</summary>
    public string? Code { get; init; }

    /// <summary><c>error</c> or <c>warning</c>.</summary>
    public string Severity { get; init; } = "error";

    /// <summary>Human-readable diagnostic message, trailing assembly annotation stripped.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The originating assembly (from a trailing <c>[...csproj]</c> annotation), when present.</summary>
    public string? Assembly { get; init; }

    /// <summary>The unmodified log message this diagnostic was parsed from.</summary>
    public string Raw { get; init; } = string.Empty;

    /// <summary>True when a file/line location was recovered.</summary>
    public bool HasLocation => File is not null && Line is not null;

    // Matches: <file>(<line>,<col>): <severity> <code>: <message>
    // The file group is non-greedy so the first "(line,col):" wins even when the path contains parentheses.
    private static readonly Regex s_LocatedPattern = new(
        @"^\s*(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Matches location-free diagnostics, e.g. "error CS0006: Metadata file '...' could not be found".
    private static readonly Regex s_BarePattern = new(
        @"^\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Trailing " [C:\path\Thing.csproj]" annotation that MSBuild appends to messages.
    private static readonly Regex s_AssemblySuffix = new(
        @"\s*\[(?<assembly>[^\]]+\.csproj)\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempt to parse a single console log message into a structured diagnostic. Only the first line
    /// of a multi-line message is inspected (Unity puts the diagnostic on the first line and appends a
    /// stack trace / caret afterwards).
    /// </summary>
    public static bool TryParse(string? logMessage, out CompilerDiagnostic diagnostic) {
        diagnostic = null!;
        if (string.IsNullOrWhiteSpace(logMessage)) return false;

        var firstLine = FirstLine(logMessage);

        var located = s_LocatedPattern.Match(firstLine);
        if (located.Success) {
            var (message, assembly) = SplitAssembly(located.Groups["message"].Value);
            diagnostic = new CompilerDiagnostic {
                File = located.Groups["file"].Value.Trim(),
                Line = ParseInt(located.Groups["line"].Value),
                Column = ParseInt(located.Groups["col"].Value),
                Severity = located.Groups["severity"].Value,
                Code = located.Groups["code"].Value,
                Message = message,
                Assembly = assembly,
                Raw = logMessage,
            };
            return true;
        }

        var bare = s_BarePattern.Match(firstLine);
        if (bare.Success) {
            var (message, assembly) = SplitAssembly(bare.Groups["message"].Value);
            diagnostic = new CompilerDiagnostic {
                Severity = bare.Groups["severity"].Value,
                Code = bare.Groups["code"].Value,
                Message = message,
                Assembly = assembly,
                Raw = logMessage,
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Render a compact, agent-friendly single line, e.g.
    /// <c>Assets/Scripts/Foo.cs(12,20): error CS0103: The name 'x' does not exist</c>.
    /// </summary>
    public string ToDisplayLine() {
        var sb = new StringBuilder();
        if (HasLocation) {
            sb.Append(File).Append('(').Append(Line);
            if (Column is { } col) sb.Append(',').Append(col);
            sb.Append("): ");
        }

        sb.Append(Severity);
        if (Code is not null) sb.Append(' ').Append(Code);
        sb.Append(": ").Append(Message);
        return sb.ToString();
    }

    private static (string Message, string? Assembly) SplitAssembly(string message) {
        var match = s_AssemblySuffix.Match(message);
        if (!match.Success) return (message.Trim(), null);
        var trimmed = message[..match.Index].Trim();
        return (trimmed, match.Groups["assembly"].Value.Trim());
    }

    private static string FirstLine(string text) {
        var newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline];
    }

    private static int? ParseInt(string value) => int.TryParse(value, out var parsed) ? parsed : null;
}
