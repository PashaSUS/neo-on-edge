using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NeoOnEdge.Api.Hubs;
using NeoOnEdge.Api.Services;
using NeoOnEdge.Core.Configuration;
using NeoOnEdge.Core.Data;
using NeoOnEdge.Core.Services;
using NeoOnEdge.Indexer;
using NeoOnEdge.Workers;
using StackExchange.Redis;
using System.Net.Http.Headers;
using Serilog;

Serilog.Debugging.SelfLog.Enable(message => Console.Error.WriteLine($"[Serilog self-log] {message}"));

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog with optional Seq sink (configured via App:SeqUrl / App:SeqApiKey)
builder.Host.UseSerilog((context, services, loggerConfig) =>
{
    loggerConfig
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext()
        // Silence noisy framework namespaces
        .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
        .WriteTo.Console();

    var configuredSeqUrl = context.Configuration["App:SeqUrl"];
    var seqUrl = NormalizeSeqUrlForContainer(configuredSeqUrl);
    var seqEnabledSetting = context.Configuration["App:SeqEnabled"];
    var seqEnabled = bool.TryParse(seqEnabledSetting, out var parsedSeqEnabled)
        ? parsedSeqEnabled
        : !string.IsNullOrWhiteSpace(seqUrl);

    if (seqEnabled && !string.IsNullOrWhiteSpace(seqUrl))
    {
        var apiKey = context.Configuration["App:SeqApiKey"];
        if (!string.Equals(configuredSeqUrl, seqUrl, StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"[Serilog] Rewriting container-local SeqUrl '{configuredSeqUrl}' to '{seqUrl}'.");
        loggerConfig.WriteTo.Seq(seqUrl, apiKey: string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
        Console.WriteLine($"[Serilog] Seq sink enabled -> {seqUrl}");
    }
    else
    {
        Console.WriteLine($"[Serilog] Seq sink disabled (SeqEnabled={seqEnabled}, SeqUrl='{seqUrl}')");
    }
});

static string? NormalizeSeqUrlForContainer(string? seqUrl)
{
    if (!string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        return seqUrl;

    if (!Uri.TryCreate(seqUrl, UriKind.Absolute, out var uri))
        return seqUrl;

    if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1")
        return "http://seq";

    return seqUrl;
}

// ── Configuration ──────────────────────────────────────────────────────────
var appSettings = builder.Configuration.GetSection("App").Get<AppSettings>()
    ?? throw new InvalidOperationException("App settings section is missing");
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("App"));

// ── PostgreSQL ─────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(appSettings.PostgresConnectionString));

// ── Redis ──────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(appSettings.RedisConnectionString));

// ── SignalR ────────────────────────────────────────────────────────────────
builder.Services.AddSignalR();
builder.Services.AddHostedService<RedisSignalRBridge>();

// ── CORS ───────────────────────────────────────────────────────────────────
builder.Services.AddCors(opt => opt.AddDefaultPolicy(p =>
{
    var origins = appSettings.AllowedOrigins.Split(',', StringSplitOptions.TrimEntries);
    if (origins is ["*"]) p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    else p.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
}));

// ── Compression ────────────────────────────────────────────────────────────
builder.Services.AddResponseCompression(opt =>
{
    opt.EnableForHttps = true;
    opt.Providers.Add<BrotliCompressionProvider>();
    opt.Providers.Add<GzipCompressionProvider>();
});

// ── Controllers ────────────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── Background services ────────────────────────────────────────────────────
builder.Services.AddSingleton<TokenRegistry>();
builder.Services.AddHostedService<MemoryMonitorService>();
builder.Services.AddHostedService<WaxShipReader>();
builder.Services.AddHostedService<KlinesWorker>();
builder.Services.AddHostedService<StatsWorker>();
builder.Services.AddHostedService<RetentionCleanupWorker>();
builder.Services.AddScoped<TokenCatalogService>();
builder.Services.AddScoped<NeoRouteQuoteService>();
builder.Services.AddSingleton<ArbitrageExecutionService>();
builder.Services.AddHostedService<ArbitrageScannerWorker>();

// ── Alcor router service ───────────────────────────────────────────────────
builder.Services.AddHttpClient("alcor", (sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    client.BaseAddress = new Uri(settings.AlcorEndpoint);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHttpClient("wax-rpc", (sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    client.BaseAddress = new Uri(settings.WaxRpcEndpoint);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHttpClient("arbitrage-executor", (sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<AppSettings>>().Value;
    client.BaseAddress = new Uri(settings.ArbitrageExecutorUrl);
    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<AlcorRouterService>();

var app = builder.Build();

// ── Create DB schema on startup ─────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS route_excluded_tokens (
            contract varchar(13) NOT NULL,
            symbol varchar(12) NOT NULL,
            reason text NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT pk_route_excluded_tokens PRIMARY KEY (contract, symbol)
        );

        INSERT INTO route_excluded_tokens (contract, symbol, reason)
        VALUES
            ('chadtoken.gm', 'CHAD', 'Token charges transfer fees to the receiving exchange account.'),
            ('chadtoken.gm', 'BBCHAD', 'Token charges transfer fees to the receiving exchange account.'),
            ('waxpepetoken', 'KEK', 'Token charges transfer fees to the receiving exchange account.'),
            ('buzzingarden', 'BUZZ', 'Token charges transfer fees to the receiving exchange account.'),
            ('buzzingarden', 'LADYZ', 'Token charges transfer fees to the receiving exchange account.'),
            ('buzzingarden', 'CATERZ', 'Token charges transfer fees to the receiving exchange account.'),
            ('buzzingarden', 'DRAGONZ', 'Token charges transfer fees to the receiving exchange account.'),
            ('buzzingarden', 'HOPPERZ', 'Token charges transfer fees to the receiving exchange account.'),
            ('buzzingarden', 'BUTTERZ', 'Token charges transfer fees to the receiving exchange account.'),
            ('token.wienr', 'WIENR', 'Token charges transfer fees to the receiving exchange account.')
        ON CONFLICT (contract, symbol) DO NOTHING;
        """);
}

app.UseCors();
app.UseResponseCompression();
app.MapControllers();
app.MapHub<NeoEdgeHub>("/hub");

app.Run();

