using FactoryMES.ChatReceiver;
using FactoryMES.Foundation;

// 로컬 채팅 수신기: Backend의 LOCAL_WEBHOOK 공급자가 보내는 Slack Incoming Webhook 모양 JSON을 받아
// 기록만 한다. 실제 채팅 서비스·외부 통신·자격 증명이 없다. 내부 망에서만 쓴다.
// - POST /hooks/factorymes: 수신(형식·재전송·시험 시나리오 규칙은 HookEndpoints 참고)
// - GET /captures/{deliveryId}: 기록 조회(검증 도구·화면 확인용)
// - GET /health/live, /health/ready: 기록 파일을 읽어 기동했을 때만 응답한다.
// 기록은 ChatReceiver:JournalPath의 JSON 파일에 남아 재시작 뒤에도 이어진다(최대 5000건).
if (await HealthProbe.TryRunAsync(args)) return;

var builder = WebApplication.CreateBuilder(args);
CostPolicy.EnsureLocalOnly(builder.Configuration);
builder.Services.AddSingleton<CaptureJournal>();

var app = builder.Build();

// 기동할 때 기록 파일을 읽는다. 손상됐으면 여기서 멈춘다(빈 기록으로 조용히 시작하지 않는다).
var journal = app.Services.GetRequiredService<CaptureJournal>();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet(
    "/health/ready",
    () => Results.Ok(new
    {
        status = "ready",
        captures = journal.Count
    })
);
app.MapHooks();

app.Run();
