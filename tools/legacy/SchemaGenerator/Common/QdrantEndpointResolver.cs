using Microsoft.Extensions.Configuration;

namespace SchemaGenerator.Common;

/// <summary>
/// Resolved Qdrant gRPC endpoint settings.
/// </summary>
public sealed record QdrantEndpointSettings(
    string Host,
    int Port,
    bool UseHttps,
    string? ApiKey);

/// <summary>
/// Resolves Qdrant connection settings from configuration, supporting both plain hostnames and full URLs.
/// </summary>
public static class QdrantEndpointResolver
{
    public const int DefaultGrpcPort = 6334;

    public static QdrantEndpointSettings Resolve(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var rawHost = config["Qdrant:Host"]?.Trim();
        if (string.IsNullOrWhiteSpace(rawHost))
        {
            throw new InvalidOperationException("Configuration value 'Qdrant:Host' is required.");
        }

        string host;
        int? uriPort = null;
        bool? uriHttps = null;

        if (rawHost.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(rawHost, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new InvalidOperationException($"Invalid 'Qdrant:Host' URL: '{rawHost}'.");
            }

            host = uri.Host;
            if (!uri.IsDefaultPort && uri.Port > 0)
            {
                uriPort = uri.Port;
            }

            if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                uriHttps = true;
            }
            else if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                uriHttps = false;
            }
        }
        else
        {
            var colonIdx = rawHost.LastIndexOf(':');
            if (colonIdx > 0 && rawHost.IndexOf(':') == colonIdx &&
                int.TryParse(rawHost[(colonIdx + 1)..], out var parsedInlinePort) && parsedInlinePort > 0)
            {
                host = rawHost[..colonIdx];
                uriPort = parsedInlinePort;
            }
            else
            {
                host = rawHost;
            }
        }

        int port = DefaultGrpcPort;
        var configuredPort = config["Qdrant:Port"];
        if (!string.IsNullOrWhiteSpace(configuredPort))
        {
            if (!int.TryParse(configuredPort, out port) || port <= 0)
            {
                throw new InvalidOperationException($"Invalid 'Qdrant:Port' value: '{configuredPort}'.");
            }
        }
        else if (uriPort.HasValue)
        {
            port = uriPort.Value;
        }

        bool useHttps;
        var configuredUseHttps = config["Qdrant:UseHttps"];
        if (!string.IsNullOrWhiteSpace(configuredUseHttps))
        {
            if (!bool.TryParse(configuredUseHttps, out useHttps))
            {
                throw new InvalidOperationException($"Invalid 'Qdrant:UseHttps' value: '{configuredUseHttps}'.");
            }
        }
        else if (uriHttps.HasValue)
        {
            useHttps = uriHttps.Value;
        }
        else
        {
            var isLocal = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                          || host == "127.0.0.1"
                          || host == "::1";
            useHttps = !isLocal;
        }

        var apiKey = string.IsNullOrWhiteSpace(config["Qdrant:ApiKey"])
            ? null
            : config["Qdrant:ApiKey"]!.Trim();

        return new QdrantEndpointSettings(host, port, useHttps, apiKey);
    }
}
