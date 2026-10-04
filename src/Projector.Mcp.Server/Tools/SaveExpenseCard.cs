using System.ComponentModel;
using System.Text.Json.Serialization;
using Projector.Application.Tools;

namespace Projector.Mcp.Server.Tools;

/// <summary>A receipt for one card: a file sent as base64, or a receipt already in the user's receipt pool.</summary>
public sealed record SaveExpenseReceipt(
    [property: JsonPropertyName("file_name"), Description("File name with extension: .pdf, .png, .jpg, .jpeg or .gif")]
    string? FileName = null,
    [property: JsonPropertyName("content_base64"), Description("The file content, base64 encoded; at most 2 MB before encoding (Projector's limit)")]
    string? ContentBase64 = null,
    [property: JsonPropertyName("receipt_uid"), Description("Instead of a file: a receipt already in the pool (list_expenses options.receipt_pool)")]
    string? ReceiptUid = null);

/// <summary>One cost card in a save_expenses call, as the agent sends it.</summary>
public sealed record SaveExpenseCard(
    [property: JsonPropertyName("date"), Description("Date of the expense (yyyy-MM-dd), as on the receipt")]
    string Date,
    [property: JsonPropertyName("project_code"), Description("Project code, e.g. P001234-001 (list_expenses options.projects)")]
    string ProjectCode,
    [property: JsonPropertyName("expense_type"), Description("Expense type name allowed on the project, e.g. Travel (options.expense_types)")]
    string ExpenseType,
    [property: JsonPropertyName("description"), Description("What was paid for, e.g. Uber airport to hotel; required for most types, at most 255 characters")]
    string? Description,
    [property: JsonPropertyName("amount"), Description("The amount on the receipt, in its currency, e.g. 38.04")]
    double Amount,
    [property: JsonPropertyName("currency"), Description("The receipt's currency code, e.g. CAD. Omit for the report currency; another currency is converted with Projector's rate for the date")]
    string? Currency = null,
    [property: JsonPropertyName("location"), Description("Optional location name from options.locations")]
    string? Location = null,
    [property: JsonPropertyName("card_uid"), Description("Only to change a draft or rejected card: its card_uid from list_expenses report. Omit to add a new card")]
    string? CardUid = null,
    [property: JsonPropertyName("receipt"), Description("Optional receipt for this card")]
    SaveExpenseReceipt? Receipt = null)
{
    public SaveExpenseInput ToInput() =>
        new(Date, ProjectCode, ExpenseType, Description, Amount, Currency, Location, CardUid,
            Receipt?.FileName, Receipt?.ContentBase64, Receipt?.ReceiptUid);
}
