using System.Text.Json;

namespace FactoryMES.ChatReceiver;

// 수신 기록 한 건. BodyHash는 받은 요청 본문 바이트 전체의 SHA-256이다(같은 요청 재전송 판별용).
public sealed record Capture(
    string DeliveryId,
    string MessageHash,
    string BodyHash,
    string Text,
    DateTime AcceptedAt,
    long Attempts
);

// 수신 성공 기록을 작은 JSON 파일 하나에 보관한다(SQLite 없음).
// 입력 설정: ChatReceiver:JournalPath(기본 /data/chat-receiver/journal.json).
// 처리: 프로세스 안 SemaphoreSlim 하나로 읽기·쓰기를 직렬화하고, 같은 폴더의 임시 파일에 쓴 뒤
//       File.Move(overwrite)로 바꿔 끼워 중간에 멈춰도 반쯤 쓴 파일이 남지 않게 한다.
//       재시작하면 파일을 다시 읽어 이어 간다. 최대 5000건이며 넘으면 새 수신을 거절한다.
//       파일이 손상됐으면 기동하지 않는다(조용히 비우지 않는다).
public sealed class CaptureJournal
{
    public const int MaxCaptures = 5000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Capture> captures;

    public CaptureJournal(IConfiguration configuration, ILogger<CaptureJournal> logger)
    {
        path = configuration.GetValue(
            "ChatReceiver:JournalPath",
            "/data/chat-receiver/journal.json"
        )!;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var loaded = File.Exists(path)
            ? JsonSerializer.Deserialize<Capture[]>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Chat receiver journal is empty or invalid.")
            : [];
        captures = loaded.ToDictionary(x => x.DeliveryId, StringComparer.Ordinal);
        logger.LogInformation("Chat receiver journal loaded Captures={Count}", captures.Count);
    }

    public int Count => captures.Count;

    // 한 요청의 판정(조회 → 기록)을 다른 요청과 섞이지 않게 잠근다.
    public async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    // 잠금 안에서만 부른다.
    public Capture? Find(string deliveryId) =>
        captures.GetValueOrDefault(deliveryId);

    // 잠금 안에서만 부른다. 기록을 바꾼 뒤 파일 전체를 원자적으로 다시 쓴다.
    public async Task SaveAsync(Capture capture, CancellationToken ct)
    {
        // 디스크 기록이 실패하면 메모리에도 성공을 남기지 않는다. 재전송의 거짓 ACK를 막는다.
        var next = captures.Values
            .Where(x => x.DeliveryId != capture.DeliveryId)
            .Append(capture)
            .ToArray();
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(
            temp,
            JsonSerializer.Serialize(next, Json),
            ct
        );
        File.Move(
            temp,
            path,
            overwrite: true
        );
        captures[capture.DeliveryId] = capture;
    }

    // 조회용. 잠금을 직접 잡고 읽기만 한다(기록은 바꾸지 않는다).
    public async Task<Capture?> ReadAsync(string deliveryId, CancellationToken ct) =>
        await LockedAsync(() => Task.FromResult(Find(deliveryId)), ct);
}


