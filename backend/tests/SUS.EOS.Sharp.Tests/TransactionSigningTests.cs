using System.Text;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using SUS.EOS.Sharp.Cryptography;
using SUS.EOS.Sharp.Models;
using SUS.EOS.Sharp.Serialization;
using SUS.EOS.Sharp.Services;
using Xunit;

namespace SUS.EOS.Sharp.Tests;

public sealed class TransactionSigningTests
{
    private const string PrivateKeyOne = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string ExpectedPackedTransaction =
        "bcd8db7040e244332211000000000100a6823403ea3055000000572d3ccdcd0100000080aa09ce35" +
        "00000000a8ed32323800000080aa09ce3500c0a5281a500dc70065cd1d000000000857415800000000" +
        "176e656f2d6f6e2d656467652d636f6e666f726d616e636500";

    [Fact]
    public async Task Transfer_fixture_is_packed_and_signed_as_antelope_transaction()
    {
        var service = new AntelopeTransactionService();
        var chainInfo = CreateChainInfo();

        var signed = await service.BuildAndSignWithAbiAsync(
            CreateTransferAbi(),
            chainInfo,
            "arb.neo",
            PrivateKeyOne,
            "eosio.token",
            "transfer",
            new
            {
                from = "arb.neo",
                to = "swap.alcor",
                quantity = "5.00000000 WAX",
                memo = "neo-on-edge-conformance"
            },
            TimeSpan.FromSeconds(60));

        Assert.Equal(ExpectedPackedTransaction, signed.PackedTransaction);
        var signature = Assert.Single(signed.Signatures);
        Assert.StartsWith("SIG_K1_", signature, StringComparison.Ordinal);
        VerifySignature(signature, chainInfo.ChainId, signed.PackedTransaction);
    }

    [Fact]
    public void Private_key_formats_use_valid_antelope_public_key_checksums()
    {
        var key = EosioKey.FromPrivateKey(PrivateKeyOne);

        Assert.Equal(
            "0279BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798",
            Convert.ToHexString(key.GetPublicKeyBytes()));
        AssertLegacyPublicKeyChecksum(key.PublicKey);
        AssertModernChecksum(key.PublicKeyK1, "PUB_K1_", "K1", 33);
    }

    private static void VerifySignature(string encoded, string chainId, string packedTransaction)
    {
        var bytes = DecodeBase58(encoded["SIG_K1_".Length..]);
        Assert.Equal(69, bytes.Length);
        var compact = bytes[..65];
        Assert.InRange(compact[0], (byte)31, (byte)34);
        Assert.Equal(CalculateRipemdChecksum(compact, "K1"), bytes[65..]);

        var digest = EosioSerializer.Sha256(EosioSerializer.CreateSigningData(
            chainId,
            Convert.FromHexString(packedTransaction)));
        var curve = SecNamedCurves.GetByName("secp256k1");
        var domain = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
        var publicPoint = curve.Curve.DecodePoint(EosioKey.FromPrivateKey(PrivateKeyOne).GetPublicKeyBytes());
        var verifier = new ECDsaSigner();
        verifier.Init(false, new ECPublicKeyParameters(publicPoint, domain));

        Assert.True(verifier.VerifySignature(
            digest,
            new BigInteger(1, compact[1..33]),
            new BigInteger(1, compact[33..65])));
    }

    private static void AssertLegacyPublicKeyChecksum(string publicKey)
    {
        Assert.StartsWith("EOS", publicKey, StringComparison.Ordinal);
        var bytes = DecodeBase58(publicKey[3..]);
        Assert.Equal(37, bytes.Length);
        Assert.Equal(CalculateRipemdChecksum(bytes[..33], null), bytes[33..]);
    }

    private static void AssertModernChecksum(
        string encoded,
        string prefix,
        string suffix,
        int payloadLength)
    {
        Assert.StartsWith(prefix, encoded, StringComparison.Ordinal);
        var bytes = DecodeBase58(encoded[prefix.Length..]);
        Assert.Equal(payloadLength + 4, bytes.Length);
        Assert.Equal(CalculateRipemdChecksum(bytes[..payloadLength], suffix), bytes[payloadLength..]);
    }

    private static byte[] CalculateRipemdChecksum(byte[] payload, string? suffix)
    {
        var digest = new Org.BouncyCastle.Crypto.Digests.RipeMD160Digest();
        digest.BlockUpdate(payload, 0, payload.Length);
        if (suffix is not null)
        {
            var suffixBytes = Encoding.ASCII.GetBytes(suffix);
            digest.BlockUpdate(suffixBytes, 0, suffixBytes.Length);
        }

        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return hash[..4];
    }

    private static byte[] DecodeBase58(string encoded)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var value = BigInteger.Zero;
        foreach (var character in encoded)
        {
            var digit = alphabet.IndexOf(character);
            Assert.True(digit >= 0, $"Invalid base58 character '{character}'.");
            value = value.Multiply(BigInteger.ValueOf(58)).Add(BigInteger.ValueOf(digit));
        }

        var payload = value.ToByteArrayUnsigned();
        var leadingZeros = encoded.TakeWhile(character => character == '1').Count();
        if (leadingZeros == 0)
            return payload;

        var decoded = new byte[leadingZeros + payload.Length];
        payload.CopyTo(decoded, leadingZeros);
        return decoded;
    }

    private static ChainInfo CreateChainInfo() => new()
    {
        ServerVersion = "test",
        ChainId = new string('0', 64),
        HeadBlockNum = 123_500,
        LastIrreversibleBlockNum = 123_456,
        LastIrreversibleBlockId = new string('0', 64),
        HeadBlockId = new string('0', 64),
        HeadBlockTime = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        HeadBlockProducer = "test",
        VirtualBlockCpuLimit = 0,
        VirtualBlockNetLimit = 0,
        BlockCpuLimit = 0,
        BlockNetLimit = 0,
        RefBlockPrefix = 0x11223344
    };

    private static AbiDefinition CreateTransferAbi() => new()
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
        Actions = [new AbiAction { Name = "transfer", Type = "transfer" }]
    };
}
