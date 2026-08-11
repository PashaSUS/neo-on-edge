using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using SUS.EOS.Sharp.Cryptography;
using SUS.EOS.Sharp.Models;
using SUS.EOS.Sharp.Services;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8787");

var rpcEndpoints = GetRpcEndpoints();
var rpcEndpointSummary = string.Join(", ", rpcEndpoints);
var transferAbi = CreateTransferAbi();
var account = GetRequiredSetting("ARBITRAGE_ACCOUNT", required: false) ?? "";
var permission = GetRequiredSetting("ARBITRAGE_PERMISSION", required: false) ?? "active";
var sharedSecret = GetRequiredSetting("ARBITRAGE_EXECUTOR_SECRET", required: false) ?? "";
var privateKey = ReadPrivateKey();
var allowedRecipients = (GetRequiredSetting("ARBITRAGE_ALLOWED_RECIPIENTS", required: false) ?? "swap.neo,swap.alcor")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.Ordinal);

var configured =
    IsAccountName(account) &&
    IsAccountName(permission) &&
    !string.IsNullOrWhiteSpace(sharedSecret) &&
    EosioKey.IsValidPrivateKey(privateKey);

builder.Services.AddSingleton<IAntelopeBlockchainClient>(_ => new AntelopeHttpClient(rpcEndpoints));
builder.Services.AddSingleton<IAntelopeTransactionService, AntelopeTransactionService>();

var app = builder.Build();

app.MapGet("/health", () => configured
    ? Results.Ok(new { configured = true, account })
    : Results.Json(new { configured = false, account = string.IsNullOrWhiteSpace(account) ? null : account }, statusCode: 503));

app.MapPost("/transfer", async (
    HttpRequest request,
    TransferRequest transfer,
    IAntelopeBlockchainClient client,
    IAntelopeTransactionService transactions,
    CancellationToken ct) =>
{
    if (!configured)
        return Results.Json(new TransferResponse(null, "Arbitrage executor is not configured."), statusCode: 503);

    if (!SecretMatches(request.Headers["X-Arbitrage-Secret"].FirstOrDefault(), sharedSecret))
        return Results.Json(new TransferResponse(null, "Unauthorized."), statusCode: 401);

    if (!IsAccountName(transfer.TokenContract) || !IsAccountName(transfer.To))
        return Results.BadRequest(new TransferResponse(null, "Invalid token contract or recipient."));

    if (!allowedRecipients.Contains(transfer.To))
        return Results.BadRequest(new TransferResponse(null, "Recipient is not allow-listed."));

    if (!IsPositiveQuantity(transfer.Quantity) || transfer.Memo is null || transfer.Memo.Length > 256)
        return Results.BadRequest(new TransferResponse(null, "Invalid quantity or memo."));

    try
    {
        var actionData = new
        {
            from = account,
            to = transfer.To,
            quantity = transfer.Quantity,
            memo = transfer.Memo
        };
        var chainInfo = await GetChainInfoAsync(client, rpcEndpointSummary, ct);
        var signed = await BuildTransferAsync(
            transactions,
            chainInfo,
            account,
            privateKey,
            transfer.TokenContract,
            transferAbi,
            actionData,
            TimeSpan.FromSeconds(60),
            permission);
        var result = await PushTransferAsync(client, signed, rpcEndpointSummary, ct);

        return Results.Ok(new TransferResponse(result.TransactionId, null));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Arbitrage transfer failed for {Quantity} from {Account} via {TokenContract} to {Recipient}",
            transfer.Quantity,
            account,
            transfer.TokenContract,
            transfer.To);
        return Results.Json(
            new TransferResponse(
                null,
                $"Executor transfer failed for {transfer.Quantity} from {account} via {transfer.TokenContract} to {transfer.To}: {ex.Message}"),
            statusCode: 500);
    }
});

app.Run();

static string[] GetRpcEndpoints()
{
    var configured = GetRequiredSetting("WAX_RPC_ENDPOINTS", required: false)
        ?? GetRequiredSetting("WAX_RPC_ENDPOINT", required: false)
        ?? "https://wax.greymass.com";

    return configured
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static string? GetRequiredSetting(string name, bool required)
{
    var value = Environment.GetEnvironmentVariable(name)?.Trim();
    if (required && string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"{name} is required.");
    return value;
}

static string ReadPrivateKey()
{
    var path = Environment.GetEnvironmentVariable("ARBITRAGE_PRIVATE_KEY_FILE")?.Trim();
    if (!string.IsNullOrWhiteSpace(path))
        return File.ReadAllText(path).Trim();

    return Environment.GetEnvironmentVariable("ARBITRAGE_PRIVATE_KEY")?.Trim() ?? "";
}

static bool SecretMatches(string? candidate, string expected)
{
    var actualBytes = Encoding.UTF8.GetBytes(candidate ?? "");
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return expectedBytes.Length > 0 &&
           actualBytes.Length == expectedBytes.Length &&
           CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
}

static bool IsAccountName(string? value)
    => !string.IsNullOrWhiteSpace(value) &&
       value.Length <= 12 &&
       Regex.IsMatch(value, "^[a-z1-5.]+$", RegexOptions.CultureInvariant);

static bool IsPositiveQuantity(string? value)
{
    if (string.IsNullOrWhiteSpace(value) ||
        !Regex.IsMatch(value, @"^(0|[1-9]\d*)(\.\d{1,18})? [A-Z1-5]{1,12}$", RegexOptions.CultureInvariant))
        return false;

    var amount = value.Split(' ', 2)[0];
    return decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) &&
           parsed > 0;
}

static async Task<ChainInfo> GetChainInfoAsync(
    IAntelopeBlockchainClient client,
    string rpcEndpointSummary,
    CancellationToken ct)
{
    try
    {
        return await client.GetInfoAsync(ct);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"WAX RPC get_info failed at {DescribeRpcEndpoint(client, rpcEndpointSummary)}: {ex.Message}",
            ex);
    }
}

static async Task<SignedTransactionResult> BuildTransferAsync(
    IAntelopeTransactionService transactions,
    ChainInfo chainInfo,
    string account,
    string privateKey,
    string tokenContract,
    AbiDefinition transferAbi,
    object actionData,
    TimeSpan expiration,
    string permission)
{
    try
    {
        return await transactions.BuildAndSignWithAbiAsync(
            transferAbi,
            chainInfo,
            account,
            privateKey,
            tokenContract,
            "transfer",
            actionData,
            expiration,
            permission);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Local transfer serialization failed for {tokenContract}::transfer: {ex.Message}",
            ex);
    }
}

static async Task<TransactionResult> PushTransferAsync(
    IAntelopeBlockchainClient client,
    SignedTransactionResult signed,
    string rpcEndpointSummary,
    CancellationToken ct)
{
    try
    {
        return await client.PushTransactionAsync(
            new
            {
                signatures = signed.Signatures,
                compression = 0,
                packed_context_free_data = "",
                packed_trx = signed.PackedTransaction
            },
            ct);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"WAX RPC push_transaction failed at {DescribeRpcEndpoint(client, rpcEndpointSummary)}: {ex.Message}",
            ex);
    }
}

static string DescribeRpcEndpoint(IAntelopeBlockchainClient client, string fallback)
    => client is AntelopeHttpClient httpClient ? httpClient.Endpoint : fallback;

static AbiDefinition CreateTransferAbi() => new()
{
    Structs =
    [
        new AbiStruct
        {
            Name = "transfer",
            Fields =
            [
                new AbiField { Name = "from", Type = "name" },
                new AbiField { Name = "to", Type = "name" },
                new AbiField { Name = "quantity", Type = "asset" },
                new AbiField { Name = "memo", Type = "string" }
            ]
        }
    ],
    Actions =
    [
        new AbiAction
        {
            Name = "transfer",
            Type = "transfer"
        }
    ]
};

internal sealed record TransferRequest(string TokenContract, string To, string Quantity, string? Memo);
internal sealed record TransferResponse(string? TransactionId, string? Error);
