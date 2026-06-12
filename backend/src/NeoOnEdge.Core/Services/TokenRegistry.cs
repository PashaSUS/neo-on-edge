namespace NeoOnEdge.Core.Services;

/// <summary>
/// In-memory map of WAX token contracts and symbols (populated by Indexer).
/// Thread-safe via ConcurrentDictionary.
/// </summary>
public class TokenRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TokenInfo> _tokens = new();

    public void Upsert(TokenInfo info) => _tokens[Key(info.Contract, info.Symbol)] = info;
    public TokenInfo? Get(string contract, string symbol) => _tokens.GetValueOrDefault(Key(contract, symbol));
    public IReadOnlyCollection<TokenInfo> All() => _tokens.Values.ToArray();

    private static string Key(string contract, string symbol) => $"{contract}::{symbol}";
}

public record TokenInfo(string Contract, string Symbol, int Precision, string? LogoUrl = null);
