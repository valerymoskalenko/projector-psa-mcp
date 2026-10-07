using System.ComponentModel;
using System.Text.Json.Serialization;
using Projector.Application.Tools;

namespace Projector.Mcp.Server.Tools;

/// <summary>One card in a save_timecard call, as the agent sends it.</summary>
public sealed record SaveTimecardCard(
    [property: JsonPropertyName("work_date"), Description("Work date (yyyy-MM-dd). On update, the card's current work date.")]
    string WorkDate,
    [property: JsonPropertyName("hours"), Description("Hours worked, e.g. 1.5. More than 0, at most 24, in the account's time increment.")]
    double Hours,
    [property: JsonPropertyName("project_code"), Description("Project code, e.g. P001234-001")]
    string ProjectCode,
    [property: JsonPropertyName("task"), Description("WBS code (preferred), task UID, task_path or unique task name (get_timecard_options)")]
    string Task,
    [property: JsonPropertyName("role"), Description("Role UID or exact role name (get_timecard_options)")]
    string Role,
    [property: JsonPropertyName("narrative"), Description("What was done; required, at most 1000 characters. On a Billable task the customer reads it on the invoice: state the result in the customer's terms; never the word 'internal', your own staff's names or internal tools")]
    string Narrative,
    [property: JsonPropertyName("timecard_uid"), Description("Only to update: the card's timecardUid from list_timecards. Omit to create a new Draft card.")]
    string? TimecardUid = null,
    [property: JsonPropertyName("location"), Description("Location name; only when get_timecard_options says location_required")]
    string? Location = null,
    [property: JsonPropertyName("udf1"), Description("Text for UDF 1; only when get_timecard_options lists rules.udf1")]
    string? Udf1 = null,
    [property: JsonPropertyName("udf2"), Description("Text for UDF 2; only when get_timecard_options lists rules.udf2")]
    string? Udf2 = null)
{
    public SaveTimecardInput ToInput() =>
        new(WorkDate, Hours, ProjectCode, Task, Role, Narrative, TimecardUid, Location, Udf1, Udf2);
}
