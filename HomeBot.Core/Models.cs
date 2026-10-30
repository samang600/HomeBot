namespace HomeBot.Core;

public enum OrderStatus { Queued, Generated, OnSwitch, InHome, Ready, Trading, Cleanup, Done, Failed, Cancelled }
public enum Location { Pc, Switch, Home, Gone }
public enum Route { SwitchLink, HomeGts, HomeFriend }

public static class Enums
{
    public static string Db(this OrderStatus s) => s switch
    {
        OrderStatus.Queued => "queued", OrderStatus.Generated => "generated", OrderStatus.OnSwitch => "on_switch", OrderStatus.InHome => "in_home",
        OrderStatus.Ready => "ready", OrderStatus.Trading => "trading", OrderStatus.Cleanup => "cleanup", OrderStatus.Done => "done", OrderStatus.Failed => "failed", _ => "cancelled",
    };
    public static OrderStatus ParseStatus(string s) => s switch
    {
        "queued" => OrderStatus.Queued, "generated" => OrderStatus.Generated, "on_switch" => OrderStatus.OnSwitch, "in_home" => OrderStatus.InHome,
        "ready" => OrderStatus.Ready, "trading" => OrderStatus.Trading, "cleanup" => OrderStatus.Cleanup, "done" => OrderStatus.Done, "failed" => OrderStatus.Failed, _ => OrderStatus.Cancelled,
    };
    public static string Ko(this OrderStatus s) => s switch
    {
        OrderStatus.Queued => "대기", OrderStatus.Generated => "생성됨", OrderStatus.OnSwitch => "스위치에 있음", OrderStatus.InHome => "HOME에 있음",
        OrderStatus.Ready => "준비됨 (/받기)", OrderStatus.Trading => "교환 중", OrderStatus.Cleanup => "정리 중", OrderStatus.Done => "완료", OrderStatus.Failed => "실패", _ => "취소",
    };
    public static string Db(this Location l) => l switch { Location.Pc => "pc", Location.Switch => "switch", Location.Home => "home", _ => "gone" };
    public static Location ParseLocation(string s) => s switch { "pc" => Location.Pc, "switch" => Location.Switch, "home" => Location.Home, _ => Location.Gone };
    public static string Db(this Route r) => r switch { Route.SwitchLink => "switch_link", Route.HomeGts => "home_gts", _ => "home_friend" };
    public static Route ParseRoute(string s) => s switch { "home_gts" => Route.HomeGts, "home_friend" => Route.HomeFriend, _ => Route.SwitchLink };
    public static string Ko(this Route r) => r switch { Route.SwitchLink => "스위치 링크 교환", Route.HomeGts => "HOME GTS", _ => "HOME 친구 교환" };
}

public sealed class User
{
    public ulong DiscordId; public string HomeFriend = ""; public bool FriendOk; public bool Blocked; public int Misses; public string TrainerName = ""; public DateTime CreatedAt;
}

public sealed class Order
{
    public long Id; public ulong DiscordId; public string Text = ""; public string Describe = ""; public string Key = "";
    public OrderStatus Status; public Route Route; public long? PoolId; public string Error = ""; public string Note = "";
    public DateTime CreatedAt, UpdatedAt; public DateTime? ClaimedAt;
    /// <summary>text = 빠른 생성 문장 / showdown = 쇼다운 세트 / file = 올린 파일(풀에 바로 들어감)</summary>
    public string Kind = "text"; public ulong ChannelId;
    /// <summary>사용자가 GTS에 맡길 열쇠 포켓몬 (역 GTS)</summary>
    public KeyPoke Key2 = new();
    /// <summary>신청한 게임 (VL 바이올렛 / ZA 레전즈 Z-A)</summary>
    public string Game = "";
}

/// <summary>사용자가 GTS에 맡기는 포켓몬. 봇이 이 조건으로 GTS를 검색함.</summary>
public sealed class KeyPoke
{
    public ushort Species { get; set; }
    public string NameEn { get; set; } = "";
    public string NameKo { get; set; } = "";
    public int Level { get; set; }
    /// <summary>0 수컷 / 1 암컷 / -1 성별 없음·상관없음</summary>
    public int Gender { get; set; } = -1;
    /// <summary>HOME 표기: ENG KOR JPN FRA DEU ITA ES-ES ES-LA CHS CHT</summary>
    public string Lang { get; set; } = "";
    /// <summary>성별이 하나로 정해진 종(라티오스·성별 없음 등): HOME 검색 창의 Gender 칸이 잠겨 있어 고르면 안 됨</summary>
    public bool GenderFixed { get; set; }
    public int SearchGender => GenderFixed ? -1 : Gender;
    public bool Valid => Species > 0 && NameEn.Length > 0 && Level is >= 1 and <= 100;
    public string Ko() => $"{NameKo} Lv.{Level} {(Gender == 0 ? "♂" : Gender == 1 ? "♀" : "성별없음")} {Lang}";
}

public sealed class PoolItem
{
    public long Id; public byte[] PkBytes = []; public string Format = ""; public ushort Species; public byte Form; public bool Shiny; public string Key = "";
    public Location Location; public int Box = -1, Slot = -1;   // 0부터
    public long? ReservedBy; public DateTime CreatedAt; public DateTime? VerifiedAt;
}

public sealed class Trade
{
    public long Id; public long OrderId; public long? PoolId; public Route Route; public DateTime StartedAt; public DateTime? FinishedAt; public string Result = ""; public string Screenshot = "";
}
