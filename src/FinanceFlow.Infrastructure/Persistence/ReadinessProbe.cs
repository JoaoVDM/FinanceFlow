using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace FinanceFlow.Infrastructure.Persistence;

public static class ReadinessProbe
{
    public static async Task<int> RunAsync(string address, string certificatePath)
    {
        try
        {
            var addressUri = new Uri(address);
            if (addressUri.Scheme != "https" || !addressUri.IsLoopback) return 1;
            using var trusted = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(certificatePath));
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, presented, _, errors) => presented is not null
                    && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                    && DateTime.UtcNow >= presented.NotBefore.ToUniversalTime() && DateTime.UtcNow <= presented.NotAfter.ToUniversalTime()
                    && CryptographicOperations.FixedTimeEquals(presented.RawData, trusted.RawData)
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(addressUri);
            if (!response.IsSuccessStatusCode) return 1;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("status").GetString() == "ready" ? 0 : 1;
        }
        catch { return 1; }
    }
}
