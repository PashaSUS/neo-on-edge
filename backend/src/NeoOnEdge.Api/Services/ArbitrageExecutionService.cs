using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NeoOnEdge.Core.Configuration;

namespace NeoOnEdge.Api.Services;

public sealed class ArbitrageExecutionService(
    IHttpClientFactory httpFactory,
    IOptions<AppSettings> settings,
    ILogger<ArbitrageExecutionService> log)
{
    public bool IsConfigured =>
        settings.Value.ArbitrageExecutionEnabled &&
        !string.IsNullOrWhiteSpace(settings.Value.ArbitrageAccount) &&
        !string.IsNullOrWhiteSpace(settings.Value.ArbitrageExecutorSecret);

    public async Task<decimal> GetBalanceAsync(TokenCatalogItem token, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient("wax-rpc");
        using var response = await http.PostAsJsonAsync(
            "/v1/chain/get_currency_balance",
            new
            {
                code = token.Contract,
                account = settings.Value.ArbitrageAccount,
                symbol = token.Symbol
            },
            ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            log.LogError(
                "WAX RPC balance request failed: endpoint={Endpoint}, account={Account}, token={TokenId}, HTTP {StatusCode}, body={Body}",
                settings.Value.WaxRpcEndpoint,
                settings.Value.ArbitrageAccount,
                $"{token.Symbol.ToLowerInvariant()}-{token.Contract}",
                (int)response.StatusCode,
                Truncate(body));
            throw new InvalidOperationException(
                $"WAX RPC balance check failed for {token.Symbol}-{token.Contract}: HTTP {(int)response.StatusCode} from {settings.Value.WaxRpcEndpoint}.");
        }

        var balances = await response.Content.ReadFromJsonAsync<string[]>(cancellationToken: ct) ?? [];
        if (balances.Length == 0)
            return 0;

        var amount = balances[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    public async Task<string> TransferAsync(
        TokenCatalogItem token,
        long rawAmount,
        string to,
        string memo,
        CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Automatic arbitrage execution is not fully configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/transfer");
        request.Headers.Add("X-Arbitrage-Secret", settings.Value.ArbitrageExecutorSecret);
        request.Content = JsonContent.Create(new
        {
            tokenContract = token.Contract,
            to,
            quantity = $"{FormatRawAmount(rawAmount, token.Precision)} {token.Symbol}",
            memo
        });

        using var http = httpFactory.CreateClient("arbitrage-executor");
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var result = TryReadTransferResponse(body);
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(result?.TransactionId))
        {
            log.LogError(
                "Arbitrage executor rejected transfer: endpoint={Endpoint}, token={TokenId}, recipient={Recipient}, quantity={Quantity}, HTTP {StatusCode}, error={Error}, body={Body}",
                settings.Value.ArbitrageExecutorUrl,
                $"{token.Symbol.ToLowerInvariant()}-{token.Contract}",
                to,
                $"{FormatRawAmount(rawAmount, token.Precision)} {token.Symbol}",
                (int)response.StatusCode,
                result?.Error,
                Truncate(body));
            throw new InvalidOperationException(
                result?.Error ??
                $"Arbitrage executor returned HTTP {(int)response.StatusCode} from {settings.Value.ArbitrageExecutorUrl}/transfer.");
        }

        return result.TransactionId;
    }

    private static string FormatRawAmount(long rawAmount, int precision)
    {
        precision = Math.Clamp(precision, 0, 18);
        var scale = Pow10(precision);
        return (rawAmount / scale).ToString($"F{precision}", CultureInfo.InvariantCulture);
    }

    private static decimal Pow10(int precision)
    {
        decimal value = 1m;
        for (int i = 0; i < precision; i++)
            value *= 10m;
        return value;
    }

    private static TransferResponse? TryReadTransferResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<TransferResponse>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value.Length <= 500 ? value : value[..500];
    }

    private sealed record TransferResponse(
        [property: JsonPropertyName("transactionId")] string? TransactionId,
        [property: JsonPropertyName("error")] string? Error);
}
