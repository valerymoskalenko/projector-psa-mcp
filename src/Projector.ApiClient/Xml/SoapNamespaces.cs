using System.Xml.Linq;

namespace Projector.ApiClient.Xml;

public static class SoapNamespaces
{
    public static readonly XNamespace SoapEnv = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Pws = "http://projectorpsa.com/PwsProjectorServices/";
    public static readonly XNamespace Req = "http://projectorpsa.com/DataContracts/Requests/";
    public static readonly XNamespace Com = "http://projectorpsa.com/DataContracts/Shared/Common/";
    public static readonly XNamespace Tim = "http://projectorpsa.com/DataContracts/Shared/TimeAndCost/";
    public static readonly XNamespace Sch = "http://projectorpsa.com/DataContracts/Shared/Scheduling/";
    public static readonly XNamespace Data = "http://www.opsplanning.com/webservices/public/data";

    public const string WcfSoapActionPrefix = "http://projectorpsa.com/PwsProjectorServices/IPwsProjectorServices/";
    public const string AsmxExportResourcesAction = "http://www.opsplanning.com/webservices/public/data/ExportResources";
    public const string AsmxExportScheduledTimeoffAction = "http://www.opsplanning.com/webservices/public/data/ExportScheduledTimeoff";
}
