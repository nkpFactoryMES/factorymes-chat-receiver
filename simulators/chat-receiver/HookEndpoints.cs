using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FactoryMES.ChatReceiver;

// 로컬 채팅 Webhook 수신기. Slack Incoming Webhook 모양의 JSON을 받아 기록만 한다(실제 채팅 없음).
// POST /hooks/factorymes
//   필수 헤더: X-FactoryMES-Delivery-Id(양의 정수), X-FactoryMES-Message-Hash(소문자 hex 64자),
//             X-FactoryMES-Scenario(SUCCESS·CHAT_TRANSIENT_ONCE·CHAT_ALWAYS_FAIL
//             ·CHAT_PERMANENT_FAIL)
//   본문: {"text": "...", "mrkdwn": false, "unfurl_links": false, "unfurl_media": false}
//         정확히 네 필드. text는 "[FactoryMES 로컬 검증] "으로 시작하고 1~6000자,
//         <·>는 없어야 하며 &는 &amp;·&lt;·&gt;만 쓴다.
//   처리 순서: 헤더 → Content-Type → 본문 크기·형식 → 이미 받은 건(재전송) → 시험 시나리오 → 기록.
//   응답: 성공·같은 요청 재전송 200 "ok", 같은 배송 ID에 다른 내용 409,
//         CHAT_TRANSIENT_ONCE 첫 요청 429(Retry-After 2), CHAT_ALWAYS_FAIL 503,
//         CHAT_PERMANENT_FAIL 400,
//         잘못된 헤더·본문 400, JSON 아님 415, 본문 초과 413, 기록 5000건 초과 507.
// GET /captures/{deliveryId}: 기록 조회(없으면 404).
// 로그: Debug 배송 ID·분기, Information 수신 기록, Warning 비정상. 본문·text·주소는 남기지 않는다.
public static class HookEndpoints
{
    public const string HookPath = "/hooks/factorymes";
    public const string TextPrefix = "[FactoryMES 로컬 검증] ";
    private const int MaxTextLength = 6000;

    // text 6000자가 모두 4바이트 문자이고 escape로 늘어난 경우까지 넉넉히 받는다.
    private const int MaxBodyBytes = 32 * 1024;

    private static readonly Regex DeliveryIdPattern = new("^[1-9][0-9]{0,18}$");
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$");
    private static readonly string[] Fields = ["text", "mrkdwn", "unfurl_links", "unfurl_media"];

    // 시험 시나리오 실패 횟수(배송 ID별, 메모리). 재시작하면 처음부터 센다.
    private static readonly ConcurrentDictionary<string, long> Attempts = new();

    public static void MapHooks(this WebApplication app)
    {
        app.MapPost(HookPath, ReceiveAsync);
        app.MapGet("/captures/{deliveryId}", CaptureAsync);
    }

    private static async Task<IResult> ReceiveAsync(
        HttpRequest request,
        CaptureJournal journal,
        ILoggerFactory logs,
        CancellationToken ct
    )
    {
        var log = logs.CreateLogger("FactoryMES.ChatReceiver.Hook");

        // 1. 헤더
        var deliveryId = request.Headers["X-FactoryMES-Delivery-Id"].ToString();
        var messageHash = request.Headers["X-FactoryMES-Message-Hash"].ToString();
        var scenario = request.Headers["X-FactoryMES-Scenario"].ToString();
        if (!DeliveryIdPattern.IsMatch(deliveryId)
            || !HashPattern.IsMatch(messageHash)
            || scenario is not ("SUCCESS" or "CHAT_TRANSIENT_ONCE" or "CHAT_ALWAYS_FAIL"
                or "CHAT_PERMANENT_FAIL"))
        {
            log.LogWarning("Chat hook rejected: invalid headers");
            return Results.Text("INVALID_HEADERS", statusCode: 400);
        }

        // 2. Content-Type
        var mediaType = request.ContentType?.Split(';')[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Chat hook rejected: content type Delivery={DeliveryId}", deliveryId);
            return Results.Text("UNSUPPORTED_MEDIA_TYPE", statusCode: 415);
        }

        // 3. 본문 크기·형식
        var body = await ReadBoundedAsync(request.Body, ct);
        if (body is null)
        {
            log.LogWarning("Chat hook rejected: body too large Delivery={DeliveryId}", deliveryId);
            return Results.Text("BODY_TOO_LARGE", statusCode: 413);
        }

        var text = ParseText(body);
        if (text is null)
        {
            log.LogWarning("Chat hook rejected: invalid body Delivery={DeliveryId}", deliveryId);
            return Results.Text("INVALID_BODY", statusCode: 400);
        }

        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        log.LogDebug(
            "Chat hook validated Delivery={DeliveryId} Scenario={Scenario}",
            deliveryId,
            scenario
        );

        // 4~6. 재전송 판정·시나리오·기록은 한 잠금 안에서 처리한다.
        return await journal.LockedAsync(
            async () =>
            {
                var attempt = Attempts.AddOrUpdate(deliveryId, 1, (_, count) => count + 1);
                var existing = journal.Find(deliveryId);
                if (existing is not null)
                {
                    if (existing.MessageHash != messageHash || existing.BodyHash != bodyHash)
                    {
                        log.LogWarning(
                            "Chat hook conflict: different content for Delivery={DeliveryId}",
                            deliveryId
                        );
                        return Results.Text("DELIVERY_CONFLICT", statusCode: 409);
                    }

                    // 같은 요청 재전송: 기록은 한 건 그대로, 받은 횟수만 늘린다.
                    await journal.SaveAsync(
                        existing with
                        {
                            Attempts = existing.Attempts + 1
                        },
                        ct
                    );
                    log.LogDebug("Chat hook replay acknowledged Delivery={DeliveryId}", deliveryId);
                    return Results.Text("ok", "text/plain", statusCode: 200);
                }

                var failure = Failure(scenario, attempt);
                if (failure is not null)
                {
                    log.LogWarning(
                        "Chat hook scenario failure Delivery={DeliveryId} Scenario={Scenario}"
                            + " Status={Status} Attempt={Attempt}",
                        deliveryId,
                        scenario,
                        failure.Value.Status,
                        attempt
                    );
                    return failure.Value.Result;
                }

                if (journal.Count >= CaptureJournal.MaxCaptures)
                {
                    log.LogWarning(
                        "Chat hook journal full Limit={Limit}",
                        CaptureJournal.MaxCaptures
                    );
                    return Results.Text("JOURNAL_FULL", statusCode: 507);
                }

                await journal.SaveAsync(
                    new Capture(
                        deliveryId,
                        messageHash,
                        bodyHash,
                        text,
                        DateTime.UtcNow,
                        attempt
                    ),
                    ct
                );
                log.LogInformation(
                    "Chat hook captured Delivery={DeliveryId} Attempt={Attempt}",
                    deliveryId,
                    attempt
                );
                return Results.Text("ok", "text/plain", statusCode: 200);
            },
            ct
        );
    }

    // 시험 시나리오별 실패 응답. 성공으로 진행하면 null.
    private static (int Status, IResult Result)? Failure(string scenario, long attempt) =>
        scenario switch
        {
            "CHAT_TRANSIENT_ONCE" when attempt == 1 => (429, TooManyRequests()),
            "CHAT_ALWAYS_FAIL" => (503, Results.Text("SCENARIO_UNAVAILABLE", statusCode: 503)),
            "CHAT_PERMANENT_FAIL" => (400, Results.Text("SCENARIO_REJECTED", statusCode: 400)),
            _ => null
        };

    private static IResult TooManyRequests() => new RetryAfterResult();

    // 429 + Retry-After: 2
    private sealed class RetryAfterResult : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = "2";
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("SCENARIO_RETRY_LATER");
        }
    }

    private static async Task<IResult> CaptureAsync(
        string deliveryId,
        CaptureJournal journal,
        CancellationToken ct
    )
    {
        if (!DeliveryIdPattern.IsMatch(deliveryId))
        {
            return Results.Text("INVALID_DELIVERY_ID", statusCode: 400);
        }

        var capture = await journal.ReadAsync(deliveryId, ct);
        return capture is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                deliveryId = capture.DeliveryId,
                messageHash = capture.MessageHash,
                text = capture.Text,
                acceptedAt = capture.AcceptedAt,
                attempts = capture.Attempts
            });
    }

    // 본문을 상한까지만 읽는다. 넘으면 null.
    private static async Task<byte[]?> ReadBoundedAsync(Stream source, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        while (true)
        {
            var count = await source.ReadAsync(block, ct);
            if (count == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + count > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(block, 0, count);
        }
    }

    // 본문 형식을 확인하고 text를 돌려준다. 형식이 틀리면 null.
    private static string? ParseText(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // 정확히 네 필드(중복·알 수 없는 필드 없음).
            var names = root.EnumerateObject().Select(x => x.Name).ToArray();
            if (names.Length != Fields.Length || !Fields.All(names.Contains))
            {
                return null;
            }

            if (root.GetProperty("mrkdwn").ValueKind != JsonValueKind.False
                || root.GetProperty("unfurl_links").ValueKind != JsonValueKind.False
                || root.GetProperty("unfurl_media").ValueKind != JsonValueKind.False)
            {
                return null;
            }

            var field = root.GetProperty("text");
            if (field.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = field.GetString()!;
            return text.Length is > 0 and <= MaxTextLength
                && text.StartsWith(TextPrefix, StringComparison.Ordinal)
                && Escaped(text)
                    ? text
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 보내는 쪽이 <, >, &를 escape했는지 확인한다. 남은 &는 &amp; &lt; &gt; 셋 중 하나여야 한다.
    private static bool Escaped(string text)
    {
        if (text.Contains('<') || text.Contains('>'))
        {
            return false;
        }

        var span = text.AsSpan();
        for (var i = span.IndexOf('&'); i >= 0; i = Next(span, i))
        {
            var rest = span[i..];
            if (!rest.StartsWith("&amp;") && !rest.StartsWith("&lt;") && !rest.StartsWith("&gt;"))
            {
                return false;
            }
        }

        return true;
    }

    private static int Next(ReadOnlySpan<char> text, int from)
    {
        var index = text[(from + 1)..].IndexOf('&');
        return index < 0 ? -1 : from + 1 + index;
    }
}
