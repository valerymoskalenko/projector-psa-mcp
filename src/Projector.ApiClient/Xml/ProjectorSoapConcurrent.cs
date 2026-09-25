using System.Xml.Linq;
using Projector.Domain.Exceptions;

namespace Projector.ApiClient.Xml;

/// <summary>
/// Bounded concurrent SOAP posts. Projector rejects larger bursts with TooManyRequests;
/// keep max concurrency at 3.
/// </summary>
public static class ProjectorSoapConcurrent
{
    public const int MaxAllowedConcurrency = 3;

    public static async Task<IReadOnlyList<SoapResponseItem>> PostAsync(
        HttpClient http,
        IReadOnlyList<SoapRequestItem> requests,
        int maxConcurrency = MaxAllowedConcurrency,
        CancellationToken cancellationToken = default)
    {
        if (maxConcurrency is < 1 or > MaxAllowedConcurrency)
        {
            throw new ProjectorApiException(
                $"MaxConcurrency must be between 1 and {MaxAllowedConcurrency}.",
                "TooManyRequests");
        }

        if (requests.Count == 0)
        {
            return [];
        }

        var results = new List<SoapResponseItem>(requests.Count);
        for (var offset = 0; offset < requests.Count; offset += maxConcurrency)
        {
            var batch = requests.Skip(offset).Take(maxConcurrency).ToList();
            var tasks = batch.Select(async req =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, req.Url);
                message.Headers.TryAddWithoutValidation("SOAPAction", req.SoapAction);
                var payload = req.Envelope.ToString(SaveOptions.DisableFormatting);
                message.Content = new StringContent(payload, System.Text.Encoding.UTF8, "text/xml");

                using var response = await http.SendAsync(message, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                XDocument doc;
                try
                {
                    doc = XDocument.Parse(body);
                }
                catch (Exception ex)
                {
                    throw new ProjectorApiException(
                        $"Projector SOAP HTTP {(int)response.StatusCode} returned non-XML content: {body}",
                        "InvalidSoapResponse",
                        ex);
                }

                return new SoapResponseItem(req.Key, doc);
            });

            results.AddRange(await Task.WhenAll(tasks));
        }

        return results;
    }
}

public sealed record SoapResponseItem(string Key, XDocument Response);
