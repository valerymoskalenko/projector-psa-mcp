using System.Reflection;
using System.Text.RegularExpressions;

namespace Projector.Mcp.Server.Prompts;

/// <summary>
/// The long prompts kept as Markdown files (prompts/*.md, embedded in the assembly): one text for the MCP prompt and
/// for people who paste it into a chat. Arguments fill the labelled input lines ("- City: &lt;city&gt;").
/// </summary>
internal static partial class PromptFiles
{
    /// <summary>Written for a line whose argument was not given; the prompt text says what that means.</summary>
    internal const string NotGiven = "(not given)";

    internal static string DraftDayTimecards => Texts.Value["projector_draft_day_timecards.md"];

    internal static string DraftTripExpenses => Texts.Value["projector_draft_trip_expenses.md"];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Texts = new(Load);

    private static Dictionary<string, string> Load()
    {
        var assembly = typeof(PromptFiles).Assembly;
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "projector_draft_day_timecards.md", "projector_draft_trip_expenses.md" })
        {
            using var stream = assembly.GetManifestResourceStream("prompts." + name)
                ?? throw new InvalidOperationException($"Prompt file {name} is not embedded in the server.");
            using var reader = new StreamReader(stream);
            texts[name] = LeadingComment().Replace(reader.ReadToEnd(), string.Empty).Trim();
        }

        return texts;
    }

    /// <summary>
    /// Replaces the placeholder of each labelled input line ("- Label: &lt;...&gt;" up to the end of the line) with the
    /// value, or with <see cref="NotGiven"/>. A label missing from the file is an error, so a renamed line fails loudly.
    /// </summary>
    internal static string Fill(string text, params (string Label, string? Value)[] lines)
    {
        foreach (var (label, value) in lines)
        {
            var pattern = new Regex("^- " + Regex.Escape(label) + ": <[^\\n]*$", RegexOptions.Multiline);
            if (!pattern.IsMatch(text))
            {
                throw new InvalidOperationException($"The prompt has no input line '- {label}: <...>'.");
            }

            var filled = string.IsNullOrWhiteSpace(value) ? NotGiven : value.Trim().ReplaceLineEndings(" ");
            text = pattern.Replace(text, _ => $"- {label}: {filled}", 1);
        }

        return text;
    }

    [GeneratedRegex("^\\s*<!--.*?-->\\s*", RegexOptions.Singleline)]
    private static partial Regex LeadingComment();
}
