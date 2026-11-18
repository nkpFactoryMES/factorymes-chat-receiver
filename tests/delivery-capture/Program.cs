using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FactoryMES.ChatReceiver;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;

// 실제 수신기 소스를 링크한 loopback wire·journal 검사다.
// 임의 외부 서버·SMTP·Oracle·업무 앱·유료 서비스에는 접근하지 않는다.
var checks = new List<object>();
var failures = new List<string>();
var testRoot = Path.Combine(
    Path.GetTempPath(),
    "factorymes-delivery-capture-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(testRoot);
var nextId = 2100000L;
var hash = new string('a', 64);
var text = HookEndpoints.TextPrefix + "정상 점검 안내 &amp; &lt;현장&gt;\n담당자가 확인하세요.";

void Check(bool valid, string label)
{
    checks.Add(new
    {
        label,
        passed = valid
    });
    if (!valid)
    {
        failures.Add(label);
        Console.WriteLine("FAIL " + label);
    }
}

static string Body(string text) => JsonSerializer.Serialize(new
{
    text,
    mrkdwn = false,
    unfurl_links = false,
    unfurl_media = false
});

async Task<HttpResponseMessage> Send(
    HttpClient client,
    string? body = null,
    string? deliveryId = null,
    string? messageHash = null,
    string? scenario = null,
    string? mediaType = "application/json",
    string? omitHeader = null,
    bool chunked = false,
    bool duplicateHeader = false
)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, HookEndpoints.HookPath);
    var bytes = Encoding.UTF8.GetBytes(body ?? Body(text));
    request.Content = chunked
        ? new StreamContent(new NonSeekableBody(bytes))
        : new ByteArrayContent(bytes);
    if (mediaType is not null)
    {
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
    }
    var headers = new Dictionary<string, string>
    {
        ["X-FactoryMES-Delivery-Id"] = deliveryId ?? (++nextId).ToString(),
        ["X-FactoryMES-Message-Hash"] = messageHash ?? hash,
        ["X-FactoryMES-Scenario"] = scenario ?? "SUCCESS"
    };
    foreach (var header in headers)
    {
        if (header.Key != omitHeader)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
    if (duplicateHeader)
    {
        request.Headers.TryAddWithoutValidation("X-FactoryMES-Delivery-Id", "9999");
    }
    return await client.SendAsync(request);
}

async Task Expect(
    HttpResponseMessage response,
    int expected,
    string label
)
{
    using (response)
    {
        Check((int)response.StatusCode == expected, label);
        await response.Content.ReadAsByteArrayAsync();
    }
}

var journalPath = Path.Combine(
    testRoot,
    "normal",
    "journal.json"
);
await using (var server = await Receiver.StartAsync(journalPath))
{
    var client = server.Client;
    var firstId = (++nextId).ToString();
    using (var response = await Send(client, deliveryId: firstId))
    {
        Check(response.StatusCode == HttpStatusCode.OK
            && await response.Content.ReadAsStringAsync() == "ok",
            "Actual local HTTP returns exact ok only after journal save");
        Check(response.Content.Headers.ContentType?.MediaType == "text/plain",
            "Accepted webhook ACK has plain-text media type");
    }
    var captured = await client.GetFromJsonAsync<JsonElement>("/captures/" + firstId);
    Check(captured.GetProperty("text").GetString() == text
        && captured.GetProperty("messageHash").GetString() == hash,
        "Actual receiver preserves escaped UTF8 text and binding hash");
    Check(captured.GetProperty("attempts").GetInt64() == 1 && server.Journal.Count == 1,
        "First acceptance creates exactly one capture and one observed attempt");
    var acceptedAt = captured.GetProperty("acceptedAt").GetDateTime();
    Check(acceptedAt.Kind == DateTimeKind.Utc, "Stored acceptance timestamp is UTC");

    await Expect(
        await Send(client, deliveryId: firstId),
        200,
        "Byte-identical delivery replay is accepted"
    );
    var duplicate = await client.GetFromJsonAsync<JsonElement>("/captures/" + firstId);
    Check(server.Journal.Count == 1 && duplicate.GetProperty("attempts").GetInt64() == 2
        && duplicate.GetProperty("acceptedAt").GetDateTime() == acceptedAt,
        "Replay retains original identity and increments attempts without a second capture");
    await Expect(
        await Send(
            client,
            deliveryId: firstId,
            messageHash: new string('b', 64)
        ),
        409,
        "Same delivery with changed binding hash conflicts"
    );
    await Expect(
        await Send(
            client,
            Body(text + " changed"),
            firstId
        ),
        409,
        "Same delivery with changed text conflicts"
    );
    await Expect(
        await Send(
            client,
            " " + Body(text),
            firstId
        ),
        409,
        "Semantic JSON replay with changed raw bytes conflicts deliberately"
    );
    await Expect(
        await Send(
            client,
            deliveryId: firstId,
            scenario: "CHAT_ALWAYS_FAIL"
        ),
        200,
        "Previously accepted replay precedes later failure scenario"
    );
    var stable = await client.GetFromJsonAsync<JsonElement>("/captures/" + firstId);
    Check(stable.GetProperty("text").GetString() == text
        && stable.GetProperty("messageHash").GetString() == hash,
        "Conflicts cannot overwrite accepted capture content");

    foreach (var header in new[]
    {
        "X-FactoryMES-Delivery-Id",
        "X-FactoryMES-Message-Hash",
        "X-FactoryMES-Scenario"
    })
    {
        await Expect(
            await Send(client, omitHeader: header),
            400,
            "Missing required header rejected: " + header
        );
    }
    foreach (var id in new[] {
        "0",
        "01",
        "-1",
        "abc",
        "1/2",
        "99999999999999999999"
    })
    {
        await Expect(
            await Send(client, deliveryId: id),
            400,
            "Noncanonical delivery id rejected: " + id
        );
    }
    foreach (var invalidHash in new[] {
        "",
        new string('A', 64),
        new string('a', 63),
        "not-a-hash"
    })
    {
        await Expect(
            await Send(client, messageHash: invalidHash),
            400,
            "Invalid binding hash rejected length " + invalidHash.Length
        );
    }
    await Expect(
        await Send(client, scenario: "EXTERNAL_WEBHOOK"),
        400,
        "Unknown or external provider scenario rejected"
    );
    await Expect(
        await Send(client, duplicateHeader: true),
        400,
        "Multiple delivery id header values are rejected"
    );
    await Expect(
        await Send(client, mediaType: null),
        415,
        "Absent content type is rejected"
    );
    await Expect(
        await Send(client, mediaType: "text/plain"),
        415,
        "Non-JSON content type is rejected"
    );
    await Expect(
        await Send(client, mediaType: "APPLICATION/JSON; charset=utf-8"),
        200,
        "JSON media type case and charset are accepted"
    );

    var countBeforeBadBodies = server.Journal.Count;
    var malformed = new Dictionary<string, string>
    {
        ["invalid JSON"] = "{",
        ["array root"] = "[]",
        ["null root"] = "null",
        ["numeric text"] = "{\"text\":7,\"mrkdwn\":false,\"unfurl_links\":false,\"unfurl_media\":false}",
        ["missing flag"] = JsonSerializer.Serialize(new {
            text,
            mrkdwn = false,
            unfurl_links = false
        }),
        ["unknown field"] = JsonSerializer.Serialize(new
        {
            text,
            mrkdwn = false,
            unfurl_links = false,
            unfurl_media = false,
            token = "not-a-real-secret"
        }),
        ["duplicate field"] = "{\"text\":\"x\",\"text\":\"x\",\"mrkdwn\":false,"
            + "\"unfurl_links\":false,\"unfurl_media\":false}",
        ["markdown enabled"] = JsonSerializer.Serialize(new
        {
            text,
            mrkdwn = true,
            unfurl_links = false,
            unfurl_media = false
        }),
        ["link preview enabled"] = JsonSerializer.Serialize(new
        {
            text,
            mrkdwn = false,
            unfurl_links = true,
            unfurl_media = false
        }),
        ["media preview enabled"] = JsonSerializer.Serialize(new
        {
            text,
            mrkdwn = false,
            unfurl_links = false,
            unfurl_media = true
        }),
        ["false string"] = JsonSerializer.Serialize(new
        {
            text,
            mrkdwn = "false",
            unfurl_links = false,
            unfurl_media = false
        }),
        ["missing local prefix"] = Body("ordinary text"),
        ["raw angle brackets"] = Body(HookEndpoints.TextPrefix + "<!channel>"),
        ["raw ampersand"] = Body(HookEndpoints.TextPrefix + "A & B"),
        ["unsupported entity"] = Body(HookEndpoints.TextPrefix + "&quot;"),
        ["empty text"] = Body("")
    };
    foreach (var item in malformed)
    {
        await Expect(
            await Send(client, item.Value),
            400,
            "Malformed payload rejected: " + item.Key
        );
    }
    Check(server.Journal.Count == countBeforeBadBodies,
        "Rejected payloads do not enter the durable journal");
    var prefix = HookEndpoints.TextPrefix;
    await Expect(
        await Send(client, Body(prefix + new string('x', 6000 - prefix.Length))),
        200,
        "Text exactly 6000 characters is accepted within byte limit"
    );
    await Expect(
        await Send(client, Body(prefix + new string('x', 6001 - prefix.Length))),
        400,
        "Text over 6000 characters is rejected"
    );
    await Expect(
        await Send(client, new string('x', 32769)),
        413,
        "Known-length body over 32KiB is bounded"
    );
    await Expect(
        await Send(
            client,
            new string('x', 32769),
            chunked: true
        ),
        413,
        "Chunked unknown-length body over 32KiB is bounded"
    );
    await Expect(
        await client.GetAsync("/captures/0"),
        400,
        "Capture lookup validates delivery id"
    );
    await Expect(
        await client.GetAsync("/captures/999999991"),
        404,
        "Missing capture is not fabricated"
    );

    var transientId = (++nextId).ToString();
    using (var first = await Send(
        client,
        deliveryId: transientId,
        scenario: "CHAT_TRANSIENT_ONCE"
    ))
    {
        Check((int)first.StatusCode == 429 && first.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(2),
            "First transient request returns HTTP429 and Retry-After2seconds");
    }
    await Expect(
        await client.GetAsync("/captures/" + transientId),
        404,
        "Transient failure has no accepted capture"
    );
    await Expect(
        await Send(
            client,
            deliveryId: transientId,
            scenario: "CHAT_TRANSIENT_ONCE"
        ),
        200,
        "Second transient attempt is accepted"
    );
    var recovered = await client.GetFromJsonAsync<JsonElement>("/captures/" + transientId);
    Check(recovered.GetProperty("attempts").GetInt64() == 2,
        "Successful capture records both transient and accepted attempts");
    var unavailableId = (++nextId).ToString();
    await Expect(
        await Send(
            client,
            deliveryId: unavailableId,
            scenario: "CHAT_ALWAYS_FAIL"
        ),
        503,
        "Unavailable scenario returns HTTP503"
    );
    await Expect(
        await client.GetAsync("/captures/" + unavailableId),
        404,
        "HTTP503 creates no successful capture"
    );
    var permanentId = (++nextId).ToString();
    await Expect(
        await Send(
            client,
            deliveryId: permanentId,
            scenario: "CHAT_PERMANENT_FAIL"
        ),
        400,
        "Permanent rejection scenario returns HTTP400"
    );
    await Expect(
        await client.GetAsync("/captures/" + permanentId),
        404,
        "Permanent failure creates no successful capture"
    );

    var parallelId = (++nextId).ToString();
    var beforeParallel = server.Journal.Count;
    var parallel = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
        Send(client, deliveryId: parallelId)));
    Check(parallel.All(x => x.StatusCode == HttpStatusCode.OK),
        "Twenty concurrent identical requests all receive accepted ACK");
    foreach (var response in parallel)
    {
        response.Dispose();
    }
    var parallelCapture = await client.GetFromJsonAsync<JsonElement>("/captures/" + parallelId);
    Check(server.Journal.Count == beforeParallel + 1
        && parallelCapture.GetProperty("attempts").GetInt64() == 20,
        "Concurrent duplicate requests persist one capture with twenty attempts");
    var persisted = new CaptureJournal(Configuration(journalPath), NullLogger<CaptureJournal>.Instance);
    Check(persisted.Count == server.Journal.Count
        && (await persisted.ReadAsync(parallelId, CancellationToken.None))?.Attempts == 20,
        "New journal instance reloads complete concurrent capture from disk");
}

await using (var restarted = await Receiver.StartAsync(journalPath))
{
    Check(
        restarted.Journal.Count > 0,
        "New receiver host reloads existing journal without clearing it"
    );
    var json = JsonSerializer.Deserialize<Capture[]>(File.ReadAllText(journalPath),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    var saved = json[0];
    var restored = await restarted.Client.GetFromJsonAsync<JsonElement>("/captures/" + saved.DeliveryId);
    Check(restored.GetProperty("acceptedAt").GetDateTime() == saved.AcceptedAt
        && restored.GetProperty("text").GetString() == saved.Text,
        "New HTTP host serves persisted original content and identity");
}

// 파일 자리에 디렉터리를 두어 atomic move를 실패시킨다. 테스트 전용 경로만 사용한다.
var failingPath = Path.Combine(
    testRoot,
    "disk-failure",
    "journal.json"
);
Directory.CreateDirectory(failingPath);
await using (var failure = await Receiver.StartAsync(failingPath))
{
    var id = (++nextId).ToString();
    await Expect(
        await Send(failure.Client, deliveryId: id),
        500,
        "Atomic journal move failure returns failure instead of a false ACK"
    );
    Check(failure.Journal.Count == 0 && failure.Journal.Find(id) is null,
        "Disk persistence failure leaves no false in-memory acceptance");
    await Expect(
        await failure.Client.GetAsync("/captures/" + id),
        404,
        "Failed disk write is absent from capture query"
    );
    await Expect(
        await Send(failure.Client, deliveryId: id),
        500,
        "Repeated delivery after disk failure still cannot falsely ACK"
    );
}

var fullPath = Path.Combine(
    testRoot,
    "capacity",
    "journal.json"
);
Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
var fullCaptures = Enumerable.Range(0, CaptureJournal.MaxCaptures).Select(x => new Capture(
    (6000000 + x).ToString(),
    hash,
    new string('b', 64),
    text,
    DateTime.UtcNow,
    1
)).ToArray();
await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(fullCaptures,
    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
await using (var full = await Receiver.StartAsync(fullPath))
{
    Check(
        full.Journal.Count == CaptureJournal.MaxCaptures,
        "Full journal fixture loads 5000 records"
    );
    await Expect(
        await Send(full.Client),
        507,
        "Full journal rejects a new capture with HTTP507"
    );
    Check(full.Journal.Count == CaptureJournal.MaxCaptures,
        "Journal capacity failure preserves existing records without eviction");
}

var corruptPath = Path.Combine(testRoot, "corrupt.json");
await File.WriteAllTextAsync(corruptPath, "[");
var corruptRejected = false;
try
{
    _ = new CaptureJournal(Configuration(corruptPath), NullLogger<CaptureJournal>.Instance);
}
catch (JsonException)
{
    corruptRejected = true;
}
Check(corruptRejected, "Corrupt journal is rejected instead of silently cleared");

var output = args.Length > 0 ? Path.GetFullPath(args[0])
    : Path.Combine(
        Directory.GetCurrentDirectory(),
        "verification-results",
        "delivery-capture-contracts.json"
    );
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
{
    suite = "Actual chat receiver loopback wire and durable journal",
    passed = checks.Count - failures.Count,
    failed = failures.Count,
    checks,
    failures,
    externalNetworkCalls = 0,
    realLoopbackHttp = true,
    actualSourceLinked = true,
    note = "No SMTP, Oracle, external chat or paid API. New receiver hosts exercise journal reload "
        + "within one process; production-container restart is a separate integration check."
}, new JsonSerializerOptions
{
    WriteIndented = true
}));
Console.WriteLine(
    $"Capture checks: {checks.Count - failures.Count} PASS / {failures.Count} FAIL; {output}"
);
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

static IConfiguration Configuration(string path) => new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ChatReceiver:JournalPath"] = path
    })
    .Build();

sealed class Receiver(
    WebApplication app,
    HttpClient client,
    CaptureJournal journal
) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public CaptureJournal Journal { get; } = journal;

    public static async Task<Receiver> StartAsync(string journalPath)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChatReceiver:JournalPath"] = journalPath
        });
        builder.Services.AddSingleton<CaptureJournal>();
        var app = builder.Build();
        app.MapHooks();
        var journal = app.Services.GetRequiredService<CaptureJournal>();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        })
        {
            BaseAddress = new Uri(address),
            Timeout = TimeSpan.FromSeconds(20)
        };
        return new Receiver(
            app,
            client,
            journal
        );
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

sealed class NonSeekableBody(byte[] bytes) : Stream
{
    private readonly MemoryStream source = new(bytes);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override int Read(
        byte[] buffer,
        int offset,
        int count
    ) => source.Read(
        buffer,
        offset,
        count
    );
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        source.ReadAsync(buffer, cancellationToken);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(
        byte[] buffer,
        int offset,
        int count
    ) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.Dispose();
        }
        base.Dispose(disposing);
    }
}

