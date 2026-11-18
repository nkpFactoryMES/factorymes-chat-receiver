# 로컬 채팅 수신기 wire·journal 검증

운영 `HookEndpoints.cs`와 `CaptureJournal.cs`를 직접 링크해 별도 출력 폴더에서 빌드한다.
Kestrel을 `127.0.0.1:0`에 띄우고 실제 HTTP 요청을 보낸다.
SMTP·Oracle·업무 앱·외부 채팅 서버·유료 API는 실행하거나 호출하지 않는다.

```powershell
dotnet run --project tests/delivery-capture/DeliveryCaptureTests.csproj -c Release -- verification-results/delivery-capture-contracts.json
```

현재74개 단언이 통과한다.

- 필수 헤더·canonical ID·hash·시나리오·Content-Type·정확한 JSON 필드/자료형 검사
- UTF8/escape·markdown·미리보기 비활성·6000자·32KiB known/chunked 상한
- 실제429/Retry-After2초·503·400 응답과 실패 수신 기록 없음
- 동일 전송 재수신·내용 충돌409·20개 동시 요청에서 수신 기록1개
- 같은 파일을 사용하는 새 journal/HTTP host에서 원래 내용과 수신 시각 보존
- atomic file move 실패 시HTTP500·메모리0·조회404: 거짓 수신 ACK 없음
- 5000개 journal 한계507·기존 이력 보존·손상 파일 조용한 초기화 거절

기록은 OS 임시 폴더의 시험 전용 고유 경로에 남긴다. 사용자 데이터나 기존 수신함을 삭제하지 않는다.
새 host와 journal의 재생성은 같은 프로세스 안에서 수행한다.
실제 Docker 컨테이너 재시작·Oracle 재시도·SMTP 캡처는 별도 통합 검증 범위다.
한 번의 HTTP 수신이 실제 Slack 발송·확인·호환 인증을 뜻하지 않는다.
