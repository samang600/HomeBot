using PKHeX.Core;
namespace HomeBot.Core.Workers;

public sealed class StepResult
{
    public bool Ok; public string Message = ""; public string Screenshot = ""; public Dictionary<string, string> Vars = new();
    public static StepResult Fail(string m, string shot = "") => new() { Ok = false, Message = m, Screenshot = shot };
    public static StepResult Pass(string m = "") => new() { Ok = true, Message = m };
}

/// <summary>HOME 박스 한 칸 (0부터).</summary>
public sealed record HomeSlot(int Box, int Slot, PKM Pk, ulong Tracker);

/// <summary>봇 스위치 (sys-botbase). 게임이 켜진 상태와 HOME 앱이 켜진 상태를 오가며 씀.</summary>
public interface ISwitchWorker
{
    bool Connected { get; }
    int? HomeUserOverride { get; set; }
    HashSet<int> KeepHomeCells { get; set; }
    List<int> LastHomeSlots { get; }
    List<PKM> KeepPks { get; set; }
    Dictionary<int, int> KeepFound { get; }
    /// <summary>남은 개체 칸을 HOME 데이터로 확인 못 해 찬 칸을 하나도 놓아주지 않았음 (이번 이동)</summary>
    bool KeepUnverified { get; }
    bool KeepAmbiguous { get; }
    List<HomeSlot> LastAltRead { get; }
    Task ConnectAsync(CancellationToken ct);
    void Disconnect();
    /// <summary>게임 박스 하나를 통째로 씀 (0부터). slots: 30개, null = 빈 칸.</summary>
    Task<StepResult> InjectBoxAsync(int box, IReadOnlyList<PKM> slots, CancellationToken ct);
    Task<List<PKM>> ReadBoxAsync(int box, CancellationToken ct);
    Task<byte[]> ScreenshotAsync(CancellationToken ct);
    /// <summary>매크로 파일(macros/<name>.json) 실행. vars는 매크로 안 ${name} 치환.</summary>
    Task<StepResult> RunMacroAsync(string name, IDictionary<string, string> vars, CancellationToken ct);
    /// <summary>게임 박스 → HOME 박스 (스위치 HOME 앱 매크로). 둘 다 1부터.</summary>
    Task<StepResult> MoveBoxToHomeAsync(int gameBox, int homeBox, CancellationToken ct, IReadOnlyList<int> knownSlots = null);
    /// <summary>스위치 HOME 앱이 켜진 상태에서 HOME 박스 읽기 (HOME Live). 1부터.</summary>
    Task<List<HomeSlot>> ReadHomeBoxesAsync(int fromBox, int toBox, CancellationToken ct);
    /// <summary>스위치 배터리 % (sys-botbase 'charge'). 모르면 null.</summary>
    Task<int?> BatteryAsync(CancellationToken ct) => Task.FromResult<int?>(null);
}

/// <summary>HOME 폰 (ADB). 파이썬 워커 서비스에 HTTP로 작업을 맡김.</summary>
public interface IPhoneWorker
{
    Task<bool> PingAsync(CancellationToken ct);
    /// <summary>역 GTS: 사용자가 맡긴 포켓몬을 검색해 HOME 박스 slot 칸과 교환. Vars: found, traded, shot.</summary>
    /// <summary>nick·nickLang: 내보낼 봇 포켓몬의 이름과 언어. 워커에 이름 인식이 있으면 칸 위치 대신 이름으로 골라냄.</summary>
    Task<StepResult> GtsFindTradeAsync(string nameEn, int gender, int level, string lang, int slot, bool fresh, CancellationToken ct, string nick = "", string nickLang = "", int nth = 0, int total = 1, string fp = "", int acct = 1);
    /// <summary>워커가 포켓몬 이름을 읽을 수 있는지 (여러 마리 묶음은 이게 있어야 안전)</summary>
    Task<bool> NameOcrAsync(CancellationToken ct);
    /// <summary>최근 minutes분 동안 워커가 끝낸 교환 중 이 조건(맡긴 포켓몬 이름·레벨·언어·성별, 봇 포켓몬 이름)과 맞는 것이 있는지</summary>
    Task<bool> TradedRecentlyAsync(string nameEn, int level, string lang, int gender, string nick, int minutes, CancellationToken ct, int acct = 1);
    /// <summary>휴대폰 HOME 앱 종료.</summary>
    Task<StepResult> HomeCloseAsync(CancellationToken ct, int acct = 1);
    /// <summary>휴대폰 HOME을 새로 켜 두기만 함 (스위치 HOME 저장 직후 미리 준비)</summary>
    Task<StepResult> HomeLaunchAsync(CancellationToken ct, int acct = 1);
}

/// <summary>화면 인식 (파이썬 서비스). 스위치 캡처에도 같은 것을 씀.</summary>
public interface IVision
{
    /// <summary>템플릿이 화면에 있는지. 반환: 신뢰도 (0~1), 위치.</summary>
    Task<(bool found, double score, int x, int y)> MatchAsync(byte[] image, string template, double threshold, CancellationToken ct);
    Task<string> OcrAsync(byte[] image, (int x, int y, int w, int h)? region, CancellationToken ct);
    /// <summary>제목 글 읽기 (한·일·영, 밝은 글자도): HOME 게임 선택 화면의 게임 이름 칸</summary>
    Task<string> OcrTitleAsync(byte[] image, (int x, int y, int w, int h) region, CancellationToken ct) => OcrAsync(image, region, ct);
    /// <summary>화면 인식 서비스에 그 이름의 템플릿 그림이 있는지</summary>
    Task<bool> TemplateExistsAsync(string name, CancellationToken ct) => Task.FromResult(true);
    /// <summary>화면의 네모 안에서 배경과 다른 부분(로고)을 잘라 템플릿으로 저장. 성공하면 저장된 파일 경로</summary>
    Task<string> SaveLogoAsync(byte[] image, string name, (int x, int y, int w, int h) region, CancellationToken ct) => Task.FromResult<string>(null);
}
