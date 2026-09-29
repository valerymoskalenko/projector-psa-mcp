using System.Diagnostics;
using Projector.ApiClient.Xml;

namespace Projector.Mcp.Server.Hosting;

/// <summary>
/// App Insights shaping. Every Projector call goes to the same URL (<c>PwsProjectorServices.svc</c>), so a dependency
/// is named after its SOAP action instead; query <c>customDimensions["projector.action"]</c>.
/// </summary>
internal static class ProjectorTelemetry
{
    public const string ActionTag = "projector.action";

    public static void EnrichProjectorCall(Activity activity, HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("SOAPAction", out var values)
            || values.FirstOrDefault() is not { Length: > 0 } header)
        {
            return;
        }

        var action = ProjectorSoapHttp.SoapActionName(header);
        activity.DisplayName = $"PWS {action}";
        activity.SetTag(ActionTag, action);
    }
}
