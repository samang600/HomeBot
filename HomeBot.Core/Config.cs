using System.Text.Json;
using System.Text.Json.Serialization;
namespace HomeBot.Core;

/// <summary>config.json 전체. 토큰은 파일에 쓰지 말고 환경 변수(DISCORD_TOKEN)로.</summary>
public sealed class BotConfig
{
    public DiscordCfg Discord { get; set; } = new();
    public SwitchCfg Switch { get; set; } = new();
    public HomeCfg Home { get; set; } = new();
    public PhoneCfg Phone { get; set; } = new();
    public LimitCfg Limits { get; set; } = new();
    public RotationCfg Rotation { get; set; } = new();
    public VoteCfg Vote { get; set; } = new();
    public HybridCfg Hybrid { get; set; } = new();
    /// <summary>기본 전달 경로: switch_link / home_gts / home_friend</summary>
    public string DefaultRoute { get; set; } = "home_gts";
    public string DataDir { get; set; } = "data";
    public string DbPath => Path.Combine(DataDir, "homebot.db");
    public string ShotDir => Path.Combine(DataDir, "shots");

    public static BotConfig Load(string path)
    {
        var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var c = JsonSerializer.Deserialize<BotConfig>(File.ReadAllText(path), o) ?? new BotConfig();
        // 상대 경로는 config.json이 있는 폴더 기준 (dotnet run --project 는 작업 폴더를 프로젝트 폴더로 바꿈)
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string Abs(string p) => string.IsNullOrEmpty(p) || Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));
        c.DataDir = Abs(c.DataDir); c.Switch.MacroDir = Abs(c.Switch.MacroDir); c.Switch.SavePath = Abs(c.Switch.SavePath);
        Directory.CreateDirectory(c.DataDir); Directory.CreateDirectory(c.ShotDir);
        return c;
    }
    public static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = JsonIgnoreCondition.Never };
}

public sealed class DiscordCfg
{
    public string TokenEnv { get; set; } = "DISCORD_TOKEN";
    public ulong GuildId { get; set; }
    public List<ulong> AdminIds { get; set; } = [];
    /// <summary>스칼렛·바이올렛 신청 채널 (0이면 봇이 '포켓몬 교환' 분류를 만들고 그 안에 게임마다 신청 채널을 만듦). 다른 게임 채널은 이 채널 옆에 만들어 기억</summary>
    public ulong ChannelId { get; set; }
    /// <summary>혼자 시험해 보는 테스트 서버 (0이면 안 씀). 같은 봇이 두 서버에서 함께 받음. 게임 채널은 TestChannelId 옆에 만들어 기억</summary>
    public ulong TestGuildId { get; set; }
    public ulong TestChannelId { get; set; }
    /// <summary>true면 본 서버 신청은 받지 않고 테스트 서버에서만 받음 (점검·시험용)</summary>
    public bool TestOnly { get; set; }
    /// <summary>켜짐/꺼짐을 이름으로 보여 줄 채널 (0이면 봇이 음성 채널을 하나 만들어 씀)</summary>
    public ulong StatusChannelId { get; set; }
}

public sealed class SwitchCfg
{
    public string IP { get; set; } = "192.168.0.10";
    public int Port { get; set; } = 6000;
    /// <summary>봇이 도는 동안 스위치 화면을 꺼 둠 (봇이 꺼질 때 다시 켬)</summary>
    public bool ScreenOff { get; set; }
    /// <summary>시작 게임: VL(스칼렛·바이올렛) / SH(소드·실드) / BD(BDSP) / GP(레츠고) / PLA / ZA. 한 번 바꾼 뒤엔 봇이 기억한 게임</summary>
    public string Game { get; set; } = "VL";
    /// <summary>다루는 게임 (비우면 6개 전부). 스위치 세이브 주인에게 그 게임 세이브가 있어야 함 — 설치 마법사가 FTP로 확인해 넣음</summary>
    public string[] Games { get; set; } = [];
    /// <summary>봇 세이브 파일 경로 (없으면 빈 세이브 + 아래 트레이너)</summary>
    public string SavePath { get; set; } = "";
    public TrainerCfg Trainer { get; set; } = new();
    /// <summary>세이브에 넣고 HOME으로 옮기는 박스 (1부터, 모든 게임 공통 — 비워 둘 것). 레츠고는 맨 윗줄(파티)을 건너뜀</summary>
    public int TransferBox { get; set; } = 1;
    public string MacroDir { get; set; } = "macros";
    /// <summary>'누가 플레이하겠습니까?' 화면에서 HOME을 켤 사용자 / 게임 세이브 주인의 순서 (0부터, 왼쪽부터). Home.Accounts가 있으면 첫 계정 값이 HomeUserIndex</summary>
    public int HomeUserIndex { get; set; } = 0;
    public int GameUserIndex { get; set; } = 0;
    /// <summary>ftpsrv 포트·계정 (계정을 비우면 익명, ftpsrv config.ini의 anon=1). 포트가 안 되면 21·5000도 시도</summary>
    public int FtpPort { get; set; } = 21;
    public string FtpUser { get; set; } = "";
    public string FtpPass { get; set; } = "";
    /// <summary>게임 세이브 주인의 스위치 사용자 이름 (비우면 GameUserIndex 순서)</summary>
    public string SaveUser { get; set; } = "";
}

public sealed class TrainerCfg
{
    public string OT { get; set; } = "홈봇";
    public int TID { get; set; } = 123456;
    public int SID { get; set; } = 1234;
    public int Gender { get; set; } = 0;
    public string Language { get; set; } = "ko";
}

public sealed class HomeCfg
{
    /// <summary>premium: 풀을 HOME에 쌓음 / free: 박스 1개, 교환 직전에만 올림</summary>
    public string Mode { get; set; } = "free";
    public bool IsFree => string.Equals(Mode, "free", StringComparison.OrdinalIgnoreCase);
    /// <summary>HOME 계정 (1~5개). 계정마다 스위치 사용자(그 사용자의 HOME)와 같은 HOME 계정으로 로그인한 휴대폰 하나. 비우면 한 계정 (Switch.HomeUserIndex · 워커 기본 기기)</summary>
    public List<HomeAccountCfg> Accounts { get; set; } = [];
}

public sealed class HomeAccountCfg
{
    /// <summary>HOME 계정(휴대폰) 최대 수 — 휴대폰 워커(server.py MAX_ACCTS)와 같게</summary>
    public const int Max = 5;
    /// <summary>스위치 '누가 플레이하겠습니까?' 화면의 순서 (0부터)</summary>
    public int SwitchUser { get; set; }
    /// <summary>휴대폰 adb 주소: MuMu는 127.0.0.1:16384(첫 기기)·16416·16448…, USB 폰은 일련번호, 무선 디버깅은 IP:포트</summary>
    public string Phone { get; set; } = "";
    /// <summary>앱 복제본처럼 안드로이드 다른 사용자에 깔린 HOME이면 그 사용자 번호 (보통 비움 = 0)</summary>
    public int? AndroidUser { get; set; }
}


/// <summary>시간마다 다음 게임으로 교대 (투표를 끈 경우만)</summary>
public sealed class RotationCfg
{
    /// <summary>false면 관리 창의 [게임 전환]으로만 바꿈</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>한 게임을 교환하는 시간 (시간)</summary>
    public double Hours { get; set; } = 3;
}

/// <summary>투표 채널에서 N명이 누르면 지금 게임 신청을 마감하고, 남은 신청을 끝낸 뒤 다른 게임으로 바꿈</summary>
/// <summary>게임 동시 운영 (세이브 모드): 기본 게임(투표)에 HOME 계정이 다 필요하지 않으면 남는 계정으로 다른 게임도 같이 받음</summary>
public sealed class HybridCfg
{
    public bool Enabled { get; set; } = true;
    /// <summary>같이 열 게임 우선순위 (이미 열린 게임은 건너뜀)</summary>
    public string[] Priority { get; set; } = ["ZA", "VL", "SH", "PLA"];
    /// <summary>HOME 계정 하나가 한 시간에 끝내는 교환 수 (계정이 몇 개 필요한지 셈할 때)</summary>
    public double PerAccountPerHour { get; set; } = 28;
    /// <summary>계정이 남는 상태가 이만큼 이어져야 다른 게임을 엶 (분)</summary>
    public double OpenAfterMinutes { get; set; } = 15;
    /// <summary>계정이 모자란 상태가 이만큼 이어지면 같이 연 게임을 닫음 (분)</summary>
    public double CloseAfterMinutes { get; set; } = 3;
    /// <summary>다른 게임을 열기 전에 미리 알리는 시간 (분)</summary>
    public double NoticeMinutes { get; set; } = 3;
    /// <summary>같이 연 게임은 적어도 이만큼은 유지 (분, 깜빡임 방지)</summary>
    public double MinOpenMinutes { get; set; } = 30;
}

public sealed class VoteCfg
{
    /// <summary>켜면 시간마다 자동 교대(Rotation)는 쓰지 않음. 관리 명령 vote:on / vote:off 로도 바꿈</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>전환에 필요한 인원</summary>
    public int Needed { get; set; } = 5;
    /// <summary>게임을 바꾼 뒤 다음 전환 투표까지 최소 시간 (시간)</summary>
    public double CooldownHours { get; set; } = 2;
    /// <summary>투표 채널 (0이면 봇이 만들어 기억)</summary>
    public ulong ChannelId { get; set; }
}

public sealed class PhoneCfg
{
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = "http://127.0.0.1:8765";
    public int TimeoutSec { get; set; } = 300;
}

public sealed class LimitCfg
{
    public int PerUserDaily { get; set; } = 5;
    public int PerUserActive { get; set; } = 1;
    public int ClaimTimeoutSec { get; set; } = 600;
    /// <summary>한 번에 HOME으로 옮기는 최대 마리 수 (게임 박스 → HOME 박스 1). 12 = 두 줄</summary>
    public int MoveBatchMax { get; set; } = 12;
    public int GenSeconds { get; set; } = 90;
}
