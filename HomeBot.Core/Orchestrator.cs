using HomeBot.Core.Workers;
using PKHeX.Core;
using PKHeXKR;
namespace HomeBot.Core;

/// <summary>
/// 신청 처리 (깡계 HOME + 역 GTS, 한 번에 한 마리).
///
///  스위치 줄:  대기 → 생성 → 게임 세이브 박스에 씀(ftpsrv) → (휴대폰 HOME이 쉬는 동안) 스위치 HOME 열어 게임 박스 → HOME 박스 → HOME 닫기
///              → 다음 신청을 미리 생성·주입해 두고 기다림
///  휴대폰 줄: 스위치 HOME이 닫히면 휴대폰 HOME 켬 → GTS에서 사용자가 맡긴 포켓몬 검색 → 교환 → 휴대폰 HOME 종료
///
/// 같은 HOME 계정은 두 기기에서 동시에 못 쓰므로 homeLock으로 막음. HOME 1-1 칸이 하나뿐이라
/// 이전 신청의 교환이 끝나야(또는 포기해야) 다음 신청을 HOME으로 올림.
/// </summary>
public sealed class Orchestrator
{
    private readonly BotConfig cfg; private readonly Db db; private readonly ISwitchWorker sw; private readonly IPhoneWorker phone;
    // ---- 게임별 생성 엔진 (동시 운영). 기본(투표) 게임 엔진은 시작할 때 받은 것
    // 만드는 동안(다른 게임 생성이 끝날 때까지 기다릴 수 있음) 다른 게임 엔진을 쓰는 쪽을 막지 않게 게임마다 Lazy
    private readonly Dictionary<string, Lazy<GenService>> gens = new(); private readonly object gensLock = new(); private readonly HashSet<string> gensFailed = new();
    public GenService GenOf(string g)
    {
        g = Norm(g);
        Lazy<GenService> lz;
        lock (gensLock)
        {
            // 만들다 실패한 엔진(Lazy는 예외를 기억함)은 버리고 다음에 다시 시도
            if (gensFailed.Remove(g)) gens.Remove(g);
            if (!gens.TryGetValue(g, out lz))
                gens[g] = lz = new Lazy<GenService>(() =>
                {
                    log($"생성 엔진 준비: {GameKo(g)}");
                    try { return new GenService(cfg, g); }
                    catch { lock (gensLock) gensFailed.Add(g); throw; }
                }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
        return lz.Value;
    }
    /// <summary>기본 게임 엔진 (게임과 상관없는 일: 이름 해석·바이트 읽기 등에도 씀)</summary>
    private GenService gen => GenOf(Game);
    private readonly Action<string> log; private readonly Func<ulong, string, Task> notify;
    /// <summary>생성이 끝나 교환 카드를 띄울 때 (디스코드 임베드)</summary>
    public Func<Order, PKM, Task> OnCard { get; set; } = (_, _) => Task.CompletedTask;
    /// <summary>GTS 검색 결과가 없을 때 신청자에게 보낼 안내 (디스코드 쪽이 [수정][취소] 버튼을 붙여 DM). null이면 글만 보냄</summary>
    public Func<Order, string, Task> OnNoResult { get; set; }
    private readonly SemaphoreSlim homeLock = new(1, 1);   // 게임 전환용 (신청이 하나도 없을 때만)
    // ---- HOME 계정 여러 개(최대 5) 번갈아 쓰기: 계정마다 스위치 사용자 + 휴대폰 하나. 계정마다 10마리씩 차례로 채우고, 같은 계정은 스위치·휴대폰이 동시에 쓰지 않음
    public const int MaxAccounts = HomeAccountCfg.Max;
    private readonly SemaphoreSlim[] acctLock = Enumerable.Range(0, MaxAccounts + 1).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> acctHoldUntil = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> acctHoldCount = new();
    // n번째 계정(2~5)의 스위치 사용자 순서 (없으면 -1). 설정 Home.Accounts가 있으면 그 값, 없으면 관리 명령 home_user<n>으로 정한 값
    public int HomeUserOf(int n) => n <= 1 ? cfg.Switch.HomeUserIndex
        : cfg.Home.Accounts.Count >= n ? cfg.Home.Accounts[n - 1].SwitchUser
        : cfg.Home.Accounts.Count > 0 ? -1
        : int.TryParse(db.Get($"home_user{n}"), out var v) ? v : -1;
    public int HomeUser2 => HomeUserOf(2);
    public int HomeUser3 => HomeUserOf(3);
    /// <summary>쓰는 HOME 계정 수 (1~5): 2번부터 차례로, 비어 있는 번호에서 끊음</summary>
    public int Accounts { get { int n = 1; while (n < MaxAccounts && HomeUserOf(n + 1) >= 0) n++; return n; } }
    private int? UserOf(int acct) => acct >= 2 ? HomeUserOf(acct) : null;
    private int AcctOf(long orderId) => int.TryParse(db.Get($"acct:{orderId}"), out var a) && a >= 1 && a <= MaxAccounts ? a : 1;
    private int PoolAcct(long poolId) => int.TryParse(db.Get($"pacct:{poolId}"), out var a) && a >= 1 && a <= MaxAccounts ? a : 1;
    private List<Order> HomeOf(int a)
    {
        var l = db.Orders(OrderStatus.InHome, OrderStatus.Trading);
        if (l.Count == 0) return l;
        var kv = db.GetMany(l.Select(o => $"acct:{o.Id}"));   // 신청마다 따로 읽지 않고 한 번에 (휴대폰 줄이 자주 부름)
        return l.Where(o => (kv.TryGetValue($"acct:{o.Id}", out var v) && int.TryParse(v, out var x) && x >= 1 && x <= MaxAccounts ? x : 1) == a).ToList();
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> freshOf = new(Enumerable.Range(1, MaxAccounts).ToDictionary(k => k, _ => true));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> prelaunchOf = new(Enumerable.Range(1, MaxAccounts).ToDictionary(k => k, _ => DateTime.MinValue));
    /// <summary>두 계정 휴대폰 교환을 동시에 (MuMu에서 두 앱 창이 따로 떠 있어야 함). 관리 창 명령 phone_parallel:on/off</summary>
    public bool PhoneParallel => Accounts > 1 && db.Get("phone_parallel") == "1";
    public string SetPhoneParallel(bool on) { db.Set("phone_parallel", on ? "1" : "0"); return on ? "휴대폰 계정들 동시 교환 켬" : "휴대폰 계정들 번갈아 교환"; }
    /// <summary>운영자: 스위치 버튼을 차례로 누름 (엉뚱한 화면에 갇혔을 때). 예: B,B,HOME</summary>
    public async Task<string> SwitchKeysAsync(string keys)
    {
        if (sw is not HomeBot.Core.Workers.SwitchWorker w) return "스위치 워커 없음";
        foreach (var k in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (k.StartsWith("wait")) { await Task.Delay(int.TryParse(k[4..], out var ms) ? ms : 1000); continue; }
            if (k.StartsWith('!')) { await w.RawCommandAsync(k[1..], CancellationToken.None); await Task.Delay(600); continue; }   // !detachController 같은 sys-botbase 원시 명령
            await w.ClickAsync(k.ToUpperInvariant(), CancellationToken.None); await Task.Delay(600);
        }
        log("운영자: 스위치 버튼 " + keys);
        return "스위치 버튼 눌림: " + keys;
    }

    /// <summary>운영자: 지금 스위치 화면을 찍어 data/shots/switch_now.jpg 로 저장 (화면 확인용)</summary>
    public async Task<string> SwitchShotAsync()
    {
        var t = System.Diagnostics.Stopwatch.StartNew();
        byte[] img;
        try { img = await sw.ScreenshotAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token); }
        catch (Exception ex) { return $"스위치 화면 찍기 실패 ({t.Elapsed.TotalSeconds:F1}초): {ex.Message}"; }
        if (img == null || img.Length == 0) return $"스위치 화면이 비어 있음 ({t.Elapsed.TotalSeconds:F1}초)";
        Directory.CreateDirectory(cfg.ShotDir); var p = Path.Combine(cfg.ShotDir, "switch_now.jpg"); File.WriteAllBytes(p, img);
        log($"운영자: 스위치 화면 저장 {img.Length}바이트 ({t.Elapsed.TotalSeconds:F1}초)");
        return "스위치 화면 저장: " + p;
    }

    /// <summary>관리 명령 home_user&lt;k&gt;:&lt;n|off&gt; — 설정 Home.Accounts가 비었을 때만 쓰임 (예전 방식)</summary>
    public string SetHomeUser(int k, string v)
    {
        if (k < 2 || k > MaxAccounts) return $"home_user{k}: 계정 번호는 2~{MaxAccounts}";
        if (cfg.Home.Accounts.Count > 0) return "설정 Home.Accounts가 있어 이 명령은 쓰이지 않습니다 (설치 마법사에서 계정을 고치세요)";
        if (v is "off" or "-1") { db.Set($"home_user{k}", "-1"); return $"{k}번째 HOME 계정 끔"; }
        if (!int.TryParse(v, out var n) || n < 0 || n > 7) return $"home_user{k}: 0~7 또는 off";
        db.Set($"home_user{k}", n.ToString()); return $"{k}번째 HOME 계정: 스위치 사용자 {n}번째(0부터)";
    }
    private bool paused; private DateTime? pausedAt;
    /// <summary>일시정지. 풀릴 때 HOME 차례 신청들의 시간 제한을 멈춰 있던 만큼 늘려 줌 (봇 쪽 고장으로 사용자 시간이 깎이지 않게)</summary>
    public bool Paused
    {
        get => paused;
        set
        {
            if (value && !paused) pausedAt = DateTime.UtcNow;
            else if (!value && paused && pausedAt != null)
            {
                var d = DateTime.UtcNow - pausedAt.Value; pausedAt = null;
                try { foreach (var o in db.Orders(OrderStatus.InHome)) if (o.ClaimedAt != null) db.SetClaimed(o.Id, o.ClaimedAt.Value + d); } catch { }
                if (d > TimeSpan.FromMinutes(1)) log($"일시정지 {Math.Round(d.TotalMinutes)}분 → HOME 차례 신청 시간 제한을 그만큼 늘림");
            }
            paused = value;
        }
    }
    public string LastError { get; private set; } = "";
    public DateTime? LastTick { get; private set; }
    public string SwitchState { get; private set; } = "대기";
    public string PhoneState { get; private set; } = "대기";

    public Orchestrator(BotConfig c, Db d, GenService g, ISwitchWorker s, IPhoneWorker p, Action<string> logger, Func<ulong, string, Task> dm)
    {
        cfg = c; db = d; sw = s; phone = p; log = logger; notify = dm ?? ((_, _) => Task.CompletedTask);
        gens[Norm(c.Switch.Game)] = new Lazy<GenService>(g);
        if (sw is HomeBot.Core.Workers.SwitchWorker w) w.SavOf = x => GenOf(x).Sav;
    }

    private static readonly OrderStatus[] Active = [OrderStatus.Queued, OrderStatus.Generated, OrderStatus.OnSwitch, OrderStatus.InHome, OrderStatus.Ready, OrderStatus.Trading];

    // =============== 게임 교대 (한 번에 한 게임, 투표·시간·관리 창으로 바꿈) ===============
    /// <summary>게임 이름은 짝 게임까지 (봇 세이브는 한쪽이어도 다른 쪽 출신 개체도 만들 수 있음)</summary>
    public static string GameKo(string g) => Norm(g) switch { "ZA" => "레전즈 Z-A", "PLA" => "레전즈 아르세우스", "SH" => "소드·실드", "GP" => "레츠고 피카츄·이브이", "BD" => "브릴리언트 다이아몬드·샤이닝 펄", _ => "스칼렛·바이올렛" };
    /// <summary>채널 이름용 짧은 이름</summary>
    /// <summary>버전 코드(SL·SW·SP·GE 포함) → 짝 게임 코드</summary>
    public static string PairOf(string ver) => Norm(ver);
    /// <summary>버전 코드의 한국어 이름 (짝 중 어느 쪽인지)</summary>
    public static string VersionKo(string ver) => (ver ?? "").Trim().ToUpperInvariant() switch
    {
        "SL" => "스칼렛", "VL" => "바이올렛", "SW" => "소드", "SH" => "실드", "BD" => "브릴리언트 다이아몬드", "SP" => "샤이닝 펄",
        "GP" => "레츠고 피카츄", "GE" => "레츠고 이브이", "PLA" => "레전즈 아르세우스", "ZA" => "레전즈 Z-A", _ => GameKo(ver),
    };
    public static string GameCode(string g) => Norm(g) switch { "ZA" => "za", "PLA" => "pla", "SH" => "swsh", "GP" => "lgpe", "BD" => "bdsp", _ => "sv" };
    public static string Norm(string g) => (g ?? "").Trim().ToUpperInvariant() switch
    {
        "ZA" => "ZA", "PLA" or "LA" => "PLA", "SH" or "SW" or "SWSH" => "SH", "GP" or "GE" or "LGP" or "LGPE" => "GP", "BD" or "SP" or "BDSP" => "BD", _ => "VL",
    };
    /// <summary>다룰 수 있는 게임 — 스위치에 저장된 세이브를 ftpsrv로 고쳐 씀 (칩을 갈지 않음)</summary>
    public static readonly string[] SaveGames = ["VL", "PLA", "ZA", "SH", "GP", "BD"];
    /// <summary>이 봇이 다루는 게임 (설정 Switch.Games, 비우면 6개 전부). 설치 마법사가 스위치에 세이브가 있는 게임만 넣음</summary>
    public static string[] GamesOf(BotConfig c) => c.Switch.Games is { Length: > 0 } g ? g.Select(Norm).Distinct().ToArray() : SaveGames;
    public string[] Games => GamesOf(cfg);
    /// <summary>테스트 전용: 쿨타임 없이 1명만 눌러도 바뀜</summary>
    private bool FreeSwitch => cfg.Discord.TestOnly;
    /// <summary>세이브 모드 투표로 정해진 다음 게임</summary>
    private string VoteTarget => db.Get("vote_target") is { Length: > 0 } t && Norm(t) != Game ? Norm(t) : null;
    /// <summary>지금 스위치에서 교환 중인 게임</summary>
    public string Game => Norm(cfg.Switch.Game);
    /// <summary>다음 게임: 투표·관리 창으로 정한 게임, 없으면 목록에서 다음 게임 (시간 교대)</summary>
    public string OtherGame => VoteTarget ?? (Games.Length > 1 ? Games[(Array.IndexOf(Games, Game) + 1) % Games.Length] : Game);
    /// <summary>게임 전환 뒤 봇이 다시 켜지는 동안 (신청을 받지 않음)</summary>
    private bool restarting;
    private readonly List<DateTime> saveErrAt = new();
    /// <summary>지금 게임 신청을 받는 중인지 (마감·전환 요청·전환 중이면 false)</summary>
    public bool AcceptingOrders => !restarting && !IntakeClosed && !switchRequested;
    /// <summary>게임 전환 뒤 봇을 다시 켜야 할 때 (디스코드 쪽이 프로세스를 끝내면 관리 창이 다시 켬)</summary>
    public Func<Task> OnRestart { get; set; } = () => Task.CompletedTask;
    private bool switchRequested { get => db.Get("switch_req") == "1"; set => db.Set("switch_req", value ? "1" : "0"); }
    private TimeSpan Period => TimeSpan.FromHours(Math.Clamp(cfg.Rotation.Hours, 0.25, 48));
    public DateTime GameSince
    {
        get { var v = db.Get("game_since"); return DateTimeOffset.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t.UtcDateTime : DateTime.UtcNow; }
    }
    /// <summary>지금 게임이 끝나는(다음 게임으로 바뀌는) 시각. 자동 교대가 꺼져 있으면 null</summary>
    public DateTime? SwitchAt => Timed ? GameSince + Period : null;
    public bool SwitchPending => switchRequested || (SwitchAt is { } t && DateTime.UtcNow >= t);
    // =============== 게임 전환 투표 ===============
    /// <summary>투표로 전환 (관리 명령 vote:on/off가 설정보다 우선)</summary>
    public bool VoteMode => db.Get("vote_mode") switch { "1" => true, "0" => false, _ => cfg.Vote.Enabled };
    public string SetVoteMode(bool on) { db.Set("vote_mode", on ? "1" : "0"); if (on) ResetVotes(); log(on ? "게임 전환: 투표 방식" : "게임 전환: 투표 끔"); return on ? "투표로 게임 전환 켬 (시간 교대는 쉼)" : "투표 끔" + (cfg.Rotation.Enabled ? " (시간 교대로 돌아감)" : ""); }
    /// <summary>시간마다 자동 교대 (투표 방식이면 쉼)</summary>
    private bool Timed => cfg.Rotation.Enabled && !VoteMode;
    public int VoteNeeded => FreeSwitch ? 1 : Math.Max(1, cfg.Vote.Needed);
    /// <summary>그 게임으로 바꾸는 데 필요한 표: 기본 + 바로 전 게임이면 ExtraForPrevious, 그 전 게임이면 ExtraForRecent (줄지는 않음)</summary>
    public int VoteNeededFor(string target)
    {
        if (FreeSwitch) return 1;
        int n = Math.Max(1, cfg.Vote.Needed);
        if (string.IsNullOrEmpty(target)) return n;
        target = Norm(target);
        var h = GameHistory();
        var prev = h.Select(x => x.game).Where(g => g != Game).Distinct().Take(2).ToList();   // 바로 전 게임, 그 전 게임
        if (prev.Count > 0 && prev[0] == target) return n + Math.Max(0, cfg.Vote.ExtraForPrevious);
        if (prev.Count > 1 && prev[1] == target) return n + Math.Max(0, cfg.Vote.ExtraForRecent);
        return n;
    }
    /// <summary>지난 게임들 (최근 것부터): 게임이 바뀔 때마다 kv game_hist에 '게임|끝난 시각' 기록 (관리 창에서 바꾼 것도 다음 확인 때 잡힘)</summary>
    public List<(string game, DateTime ended)> GameHistory()
    {
        lock (voteLock)
        {
            var cur = Game;
            if (db.Get("game_hist_v") != "2") { db.Set("game_hist", ""); db.Set("game_hist_cur", ""); db.Set("game_hist_v", "2"); }   // 처음 넣은 기록(관리 창 전환을 빠뜨림)은 다시 만듦
            var seen = db.Get("game_hist_cur");
            var list = db.Get("game_hist").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('|'))
                .Where(p => p.Length == 2 && DateTimeOffset.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out _))
                .Select(p => (game: p[0], ended: DateTimeOffset.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal).UtcDateTime)).ToList();
            if (seen.Length == 0)
            {
                // 처음: 전환 투표 기록(data/vote_log.txt '[MM-dd HH:mm] 전환 투표 A → B')으로 지난 게임을 채움
                if (list.Count == 0)
                {
                    try
                    {
                        var byKo = SaveGames.ToDictionary(GameKo, g => g);
                        var rx = new System.Text.RegularExpressions.Regex(@"^\[(\d\d)-(\d\d) (\d\d):(\d\d)\] 전환 투표 (.+?) → (.+?)(?: \(|$)");
                        var f = Path.Combine(cfg.DataDir, "vote_log.txt");
                        var evs = new List<(string from, DateTime at)>(); string lastTo = null;
                        foreach (var line in File.Exists(f) ? File.ReadAllLines(f) : [])
                        {
                            var m = rx.Match(line.Trim()); if (!m.Success || !byKo.TryGetValue(m.Groups[5].Value.Trim(), out var from)) continue;
                            var at = new DateTime(DateTime.Now.Year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0, DateTimeKind.Local);
                            if (at > DateTime.Now.AddDays(1)) at = at.AddYears(-1);
                            evs.Add((from, at.ToUniversalTime()));
                            lastTo = byKo.TryGetValue(m.Groups[6].Value.Trim(), out var to) ? to : null;
                        }
                        // 마지막 투표 뒤 관리 창에서 바꾼 경우: 그 투표로 정해진 게임이 바로 전 게임
                        if (lastTo != null && lastTo != cur) evs.Add((lastTo, GameSince.ToLocalTime().ToUniversalTime()));
                        // 투표가 정해진 뒤 남은 신청을 끝내고 바뀌므로 끝난 시각은 대략 그 무렵 (마지막 것은 지금 게임 시작 시각)
                        for (int k = evs.Count - 1; k >= 0 && list.Count < 10; k--) list.Add((evs[k].from, k == evs.Count - 1 ? GameSince : evs[k].at));
                    }
                    catch { }
                    if (list.Count == 0 && LastVote is { } lv && Norm(lv.to) == cur && Norm(lv.from) != cur) list.Insert(0, (Norm(lv.from), GameSince));
                }
            }
            else if (seen != cur) list.Insert(0, (seen, GameSince));   // 게임이 바뀜: 그 게임은 지금 게임이 시작될 때 끝남
            if (seen != cur || db.Get("game_hist").Length == 0 && list.Count > 0)
            {
                list = list.Take(10).ToList();
                db.Set("game_hist", string.Join(",", list.Select(x => $"{x.game}|{x.ended:o}")));
                db.Set("game_hist_cur", cur);
            }
            return list;
        }
    }
    /// <summary>다음 전환 투표가 열리는 시각 (마지막으로 게임을 바꾼 때 + 쿨타임)</summary>
    public DateTime VoteOpenAt => GameSince + TimeSpan.FromHours(FreeSwitch ? 0 : Math.Max(0, cfg.Vote.CooldownHours));
    public bool SwitchRequested => switchRequested;
    private readonly object voteLock = new();
    /// <summary>지금 게임에서 다른 게임으로 바꾸자고 누른 사람들 (게임이 바뀌면 자동으로 비움)</summary>
    public List<ulong> Votes()
    {
        lock (voteLock)
        {
            if (db.Get("vote_game") != Game) return new();
            return VoteList().Select(x => x.uid).Distinct().ToList();
        }
    }
    /// <summary>표 목록: (누른 사람, 바꾸자는 게임 — 옛 방식 표는 OtherGame)</summary>
    private List<(ulong uid, string game)> VoteList() => db.Get("vote_users").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => { var p = x.Split(':'); return (uid: ulong.TryParse(p[0], out var v) ? v : 0, game: p.Length > 1 ? Norm(p[1]) : OtherGame); }).Where(x => x.uid != 0).ToList();
    /// <summary>세이브 모드: 게임별 표 (지금 게임은 빼고)</summary>
    public Dictionary<string, List<ulong>> VotesByGame()
    {
        lock (voteLock)
        {
            var l = db.Get("vote_game") == Game ? VoteList() : new();
            return Games.Where(g => g != Game).ToDictionary(g => g, g => l.Where(x => x.game == g).Select(x => x.uid).Distinct().ToList());
        }
    }
    public void ResetVotes() { lock (voteLock) { db.Set("vote_users", ""); db.Set("vote_game", Game); } }
    /// <summary>투표하기/취소 (같은 사람이 다시 누르면 취소). Triggered: 이번 표로 인원이 차서 전환 요청을 했음</summary>
    public (string Msg, bool Triggered) ToggleVote(ulong uid, string target = null)
        => ToggleVoteFor(uid, target);
    /// <summary>세이브 모드 투표: 바꿀 게임을 골라 누름. 같은 게임을 다시 누르면 취소, 다른 게임을 누르면 표를 옮김</summary>
    private (string Msg, bool Triggered) ToggleVoteFor(ulong uid, string target)
    {
        lock (voteLock)
        {
            if (!VoteMode) return ("지금은 투표로 게임을 바꾸지 않아요.", false);
            if (string.IsNullOrEmpty(target)) return ("게임전환-투표 채널에서 바꿀 게임 버튼을 눌러 주세요.", false);
            target = Norm(target);
            if (target == Game) return ($"지금 이미 **{GameKo(Game)}** 교환 중이에요.", false);
            if (switchRequested) return ($"이미 **{GameKo(OtherGame)}**로 바꾸기로 했어요. 남은 신청을 끝내면 바뀝니다.", false);
            if (restarting) return ("게임을 바꾸는 중이에요.", false);
            if (DateTime.UtcNow < VoteOpenAt) return ($"게임을 바꾼 지 얼마 안 돼서 **{Kst(VoteOpenAt)}**부터 투표할 수 있어요.", false);
            var l = db.Get("vote_game") == Game ? VoteList() : new();
            bool had = l.Any(x => x.uid == uid && x.game == target);
            l.RemoveAll(x => x.uid == uid && x.game == target);   // 한 사람이 게임마다 한 표씩 (같은 게임을 다시 누르면 그 표만 취소)
            if (!had) l.Add((uid, target));
            db.Set("vote_game", Game); db.Set("vote_users", string.Join(",", l.Select(x => $"{x.uid}:{x.game}")));
            int n = l.Count(x => x.game == target);
            int need = VoteNeededFor(target);
            if (had) return ($"**{GameKo(target)}** 투표를 취소했어요 ({n}/{need})", false);
            if (n < need) return ($"**{GameKo(target)}**로 바꾸기에 투표했어요 ({n}/{need}). 다시 누르면 취소돼요." + (need > VoteNeeded ? $" (최근에 했던 게임이라 {need}명이 필요해요)" : ""), false);
            db.Set("vote_target", target);
            log($"전환 투표 {n}명 달성: {GameKo(Game)} → {GameKo(target)}");
            RecordVote(Game, target, l);
            RequestSwitch();
            var left = LeftBeforeSwitch;
            return ($"{n}명이 모여 **{GameKo(target)}**로 바꾸기로 했어요! " + (left > 0 ? $"{GameKo(Game)} 신청은 마감하고, 남은 {left}건을 끝낸 뒤 바뀝니다." : "곧 바뀝니다."), true);
        }
    }
    /// <summary>전환 투표 기록: 전환이 정해질 때마다 누가 어느 게임에 투표했는지 data/vote_log.txt에 남기고 운영자에게 DM (투표 채널에는 올리지 않음)</summary>
    private void RecordVote(string from, string to, List<(ulong uid, string game)> votes)
    {
        try
        {
            string Who(ulong u)
            {
                var name = db.Get($"name:{u}"); if (string.IsNullOrEmpty(name)) name = u.ToString();
                var made = DateTimeOffset.FromUnixTimeMilliseconds((long)(u >> 22) + 1420070400000L).ToLocalTime();
                var os = db.OrdersOf(u, false);
                return $"{name} · 계정 {made:yyyy-MM-dd} · 교환 완료 {os.Count(o => o.Status == OrderStatus.Done)}/신청 {os.Count}";
            }
            var lines = votes.OrderBy(x => x.game == to ? 0 : 1).Select(x => $"  {(x.game == to ? "✔" : "·")} {GameKo(x.game)} ← {Who(x.uid)} ({x.uid})").ToList();
            var text = $"[{DateTime.Now:MM-dd HH:mm}] 전환 투표 {GameKo(from)} → {GameKo(to)}\n" + string.Join("\n", lines);
            File.AppendAllText(Path.Combine(cfg.DataDir, "vote_log.txt"), text + "\n\n");
            db.Set("last_vote", $"{from}|{to}|{DateTime.UtcNow:O}|" + string.Join(",", votes.Where(x => x.game == to).Select(x => x.uid)));   // 투표판에 다음 전환까지 보여 줌
            log($"전환 투표 기록: " + string.Join(", ", votes.Where(x => x.game == to).Select(x => db.Get($"name:{x.uid}") is { Length: > 0 } n ? n : x.uid.ToString())));
            _ = Task.Run(() => AdminAlert("🗳️ " + text));
        }
        catch (Exception ex) { log("투표 기록 실패: " + ex.Message); }
    }
    /// <summary>지난 전환 투표 (투표판 표시용): 어디서 어디로, 언제(UTC), 그 게임에 투표한 사람</summary>
    public (string from, string to, DateTime at, List<ulong> voters)? LastVote
    {
        get
        {
            var p = db.Get("last_vote").Split('|');
            if (p.Length < 4 && db.Get("vote_game") is { Length: > 0 } vg && Norm(vg) != Game)
            {
                // 이 기능 전에 정해진 전환: 남아 있는 표 중 지금 게임에 투표한 사람 (전환 시각 = 지금 게임 시작)
                var won = VoteList().Where(x => x.game == Game).Select(x => x.uid).ToList();
                if (won.Count > 0) return (Norm(vg), Game, GameSince, won);
            }
            if (p.Length < 4 || !DateTime.TryParse(p[2], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) return null;
            return (p[0], p[1], at, p[3].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => ulong.TryParse(x, out var v) ? v : 0).Where(v => v != 0).ToList());
        }
    }
    public int ActiveCount => OrdG(Game, Active).Count;
    /// <summary>게임이 바뀌기 전에 끝내야 하는 지금 게임 신청 수 (세이브 모드는 HOME에 올라간 교환은 그대로 이어지므로 게임 쪽 것만)</summary>
    public int LeftBeforeSwitch => OrdG(Game, NotHome).Count;

    // =============== 게임 동시 운영 (세이브 모드) ===============
    // 기본 게임(투표로 정함)에 HOME 계정이 다 필요하지 않으면 남는 계정을 다른 게임에 돌려 그 게임 신청도 같이 받음.
    // 신청이 다시 많아지면 같이 연 게임을 마감하고(받은 신청은 끝까지 처리) 계정을 기본 게임으로 모음.
    public bool HybridOn => Accounts > 1 && Games.Length > 1 && (db.Get("hybrid") switch { "1" => true, "0" => false, _ => cfg.Hybrid.Enabled });
    public string SetHybrid(bool on) { db.Set("hybrid", on ? "1" : "0"); log(on ? "게임 동시 운영 켬" : "게임 동시 운영 끔 (같이 연 게임은 받은 신청을 끝내고 닫음)"); return on ? "게임 동시 운영 켬" : "게임 동시 운영 끔"; }
    private static string NormOrNull(string g) => string.IsNullOrWhiteSpace(g) ? null : Norm(g);
    /// <summary>기본 게임과 같이 받는 게임들 (연 순서)</summary>
    public List<string> SideGames => db.Get("side_games").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Norm).Where(g => g != Game).Distinct().ToList();
    private void SetSides(IEnumerable<string> l) => db.Set("side_games", string.Join(",", l.Select(Norm).Distinct()));
    /// <summary>지금 처리하는 게임 전부 (기본 게임 + 같이 받는 게임)</summary>
    public List<string> Served { get { var l = new List<string> { Game }; l.AddRange(SideGames); return l; } }
    /// <summary>같이 받다가 마감하는 중 (받은 신청을 끝내면 계정이 기본 게임으로 돌아감)</summary>
    public bool SideClosing(string g) => db.Get($"side_closing:{Norm(g)}") == "1";
    /// <summary>그 게임 신청을 지금 받는지</summary>
    public bool IsOpen(string g)
    {
        g = Norm(g);
        if (g == Game) return AcceptingOrders;
        return SideGames.Contains(g) && !SideClosing(g) && !IntakeClosed && !restarting && AcctsOf(g).Count > 0;
    }
    /// <summary>HOME 계정이 맡은 게임 (같이 받는 게임에 돌린 계정이 아니면 기본 게임)</summary>
    public string GameOfAcct(int a) => NormOrNull(db.Get($"acct_game:{a}")) is { } g && SideGames.Contains(g) ? g : Game;
    private List<int> AcctsOf(string g) => Enumerable.Range(1, Accounts).Where(a => GameOfAcct(a) == g).ToList();
    /// <summary>게임 채널에 알림 (디스코드 쪽이 채움): (게임, 글)</summary>
    public Func<string, string, Task> OnGameNotice { get; set; } = (_, _) => Task.CompletedTask;
    private async Task Notice(string g, string text) { try { await OnGameNotice(Norm(g), text); } catch (Exception ex) { log("게임 알림 실패: " + ex.Message); } }
    public string HybridLine()
    {
        var sides = SideGames;
        var plan = db.Get("side_plan").Split('|');
        string p = plan.Length >= 2 && DateTime.TryParse(plan[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? $" · {Kst(at)}부터 {GameKo(plan[0])} 같이 받음 예정" : "";
        if (sides.Count == 0) return HybridOn ? $"동시 운영 대기 (계정 {Accounts}개 모두 {GameKo(Game)})" + p : "";
        return $"동시 운영: {GameKo(Game)} 계정 {string.Join(",", AcctsOf(Game))} · " + string.Join(" · ", sides.Select(g => $"{GameKo(g)} 계정 {string.Join(",", AcctsOf(g))}" + (SideClosing(g) ? " (마감 중)" : ""))) + p;
    }

    private DateTime planAt; private DateTime? spareSince, shortSince;
    private readonly Dictionary<string, DateTime> noSave = new();   // 세이브를 읽지 못해 같이 열 수 없는 게임 → 30분 뒤 다시 확인 (FTP가 잠깐 끊긴 것일 수 있음)
    /// <summary>기본 게임에 계정이 몇 개 필요한지: 최근 30분 신청 속도 + 밀린 신청을 30분 안에 비울 만큼</summary>
    private (int need, double perHour, int backlog) MainNeed()
    {
        var since = DateTime.UtcNow.AddMinutes(-30);
        int arrivals = db.GamesCreatedSince(since).Count(g => (NormOrNull(g) ?? Game) == Game);
        int backlog = OrdG(Game, NotHome).Count;
        double perHour = arrivals * 2.0;
        double cap = Math.Max(5, cfg.Hybrid.PerAccountPerHour);
        int need = (int)Math.Ceiling((perHour + backlog * 2.0) / cap);
        return (Math.Clamp(need, 1, Accounts), perHour, backlog);
    }

    /// <summary>1분마다: 기본 게임 계정이 남으면 다른 게임을 같이 열고, 모자라면 같이 연 게임을 마감</summary>
    private async Task PlanTick(CancellationToken ct)
    {
        if (DateTime.UtcNow - planAt < TimeSpan.FromSeconds(60)) return;
        planAt = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        // 예약해 둔 열기
        var plan = db.Get("side_plan").Split('|');
        if (plan.Length >= 3 && DateTime.TryParse(plan[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var openAt) && int.TryParse(plan[2], out var pa))
        {
            var pg = Norm(plan[0]);
            bool still = HybridOn && !IntakeClosed && !switchRequested && !restarting && pg != Game && !SideGames.Contains(pg) && pa >= 1 && pa <= Accounts && GameOfAcct(pa) == Game && AcctsOf(Game).Count > 1
                         && MainNeed().need < AcctsOf(Game).Count;   // 알림 뒤에 기본 게임 신청이 다시 많아졌으면 취소
            if (!still) { db.Set("side_plan", ""); log($"동시 운영: {GameKo(pg)} 열기 취소 (상황이 바뀜)"); await Notice(pg, $"⚪ **{GameKo(pg)}** 같이 받기를 취소했어요 ({GameKo(Game)} 신청이 다시 많아졌거나 운영 상태가 바뀜)."); return; }
            if (now < openAt) return;
            db.Set("side_plan", "");
            var l = SideGames; l.Add(pg); SetSides(l);
            foreach (var a in Enumerable.Range(1, MaxAccounts)) if (a != pa && NormOrNull(db.Get($"acct_game:{a}")) == pg) db.Set($"acct_game:{a}", "");   // 예전 기록이 남아 다른 계정까지 끌려가지 않게
            db.Set($"acct_game:{pa}", pg); db.Set($"side_since:{pg}", now.ToString("o")); db.Set($"side_closing:{pg}", "");
            log($"동시 운영: {GameKo(pg)} 신청 받기 시작 (계정 {pa} · {GameKo(Game)} 계정 {string.Join(",", AcctsOf(Game))})");
            return;
        }
        if (!HybridOn)
        {
            foreach (var g in SideGames.Where(g => !SideClosing(g))) await CloseSide(g, "동시 운영을 껐어요");
            return;
        }
        foreach (var g in SideGames.Where(g => !SideClosing(g) && AcctsOf(g).Count == 0).ToList()) await CloseSide(g, "맡은 HOME 계정이 없어져서");   // 계정 설정이 바뀐 경우: 남은 신청은 기본 게임 계정을 빌려 끝냄
        if (Paused || SwitchHold || switchRequested || restarting || IntakeClosed) { spareSince = shortSince = null; return; }
        var (need, perHour, backlog) = MainNeed();
        // 마감 중인 게임의 계정은 곧 기본 게임으로 돌아오므로 기본 게임 쪽으로 셈
        int mainN = AcctsOf(Game).Count + SideGames.Where(SideClosing).Sum(g => AcctsOf(g).Count);
        if (need > mainN)
        {
            spareSince = null; shortSince ??= now;
            if (now - shortSince.Value < TimeSpan.FromMinutes(cfg.Hybrid.CloseAfterMinutes)) return;
            // 우선순위가 가장 낮은(나중에 연) 게임부터 마감. 연 지 얼마 안 된 게임은 밀린 신청이 많을 때만
            var open = SideGames.Where(g => !SideClosing(g)).ToList();
            var pri = cfg.Hybrid.Priority.Select(Norm).Concat(Games).Distinct().ToArray();   // 우선순위에 없는 게임은 맨 뒤 (먼저 마감)
            var victim = open.OrderByDescending(g => Array.IndexOf(pri, g)).FirstOrDefault(g =>
                now - (DateTime.TryParse(db.Get($"side_since:{g}"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.MinValue) >= TimeSpan.FromMinutes(cfg.Hybrid.MinOpenMinutes) || backlog >= 20);
            if (victim != null) { await CloseSide(victim, $"{GameKo(Game)} 신청이 많아져서"); shortSince = now; }
        }
        else if (need < AcctsOf(Game).Count)   // 마감 중인 게임의 계정은 아직 돌아오지 않았으니 셈에서 뺌 (예약 확인과 같은 기준)
        {
            shortSince = null; spareSince ??= now;
            if (now - spareSince.Value < TimeSpan.FromMinutes(cfg.Hybrid.OpenAfterMinutes) || AcctsOf(Game).Count <= 1) return;
            string next = null;
            // 우선순위에 적힌 게임 중 다루는 게임(설정 Games)만. 우선순위에 없는 다루는 게임은 그 뒤에
            var order = cfg.Hybrid.Priority.Select(Norm).Concat(Games).Distinct().Where(Games.Contains);
            foreach (var cand in order.Where(g => g != Game && !SideGames.Contains(g) && !(noSave.TryGetValue(g, out var until) && DateTime.UtcNow < until)))
            {
                // 그 게임 세이브가 스위치에 있는지 먼저 확인 (없으면 그 게임은 건너뜀)
                if (sw is HomeBot.Core.Workers.SwitchWorker w) w.GameOverride = cand;
                try { await sw.ReadBoxAsync(cfg.Switch.TransferBox - 1, ct); next = cand; break; }
                catch (Exception ex) when (ex is not OperationCanceledException) { noSave[cand] = DateTime.UtcNow.AddMinutes(30); log($"동시 운영: {GameKo(cand)} 세이브를 읽지 못해 건너뜀 · 30분 뒤 다시 확인 ({ex.Message})"); }
                finally { if (sw is HomeBot.Core.Workers.SwitchWorker w2) w2.GameOverride = null; }
            }
            if (next == null) return;
            var warm = next; _ = Task.Run(() => { try { GenOf(warm); } catch (Exception ex) { log("생성 엔진 준비 실패: " + ex.Message); } });   // 여는 동안 미리 준비
            // 기본 게임 계정 중 HOME에 남은 교환이 가장 적은 계정을 돌림 (남은 교환은 그대로 끝냄)
            int acct = AcctsOf(Game).OrderBy(a => HomeOf(a).Count).ThenByDescending(a => a).First();
            var at = now.AddMinutes(Math.Max(0, cfg.Hybrid.NoticeMinutes));
            db.Set("side_plan", $"{next}|{at:O}|{acct}");
            spareSince = null;
            log($"동시 운영: {GameKo(Game)} 신청 시간당 {perHour:0}건·밀린 {backlog}건 → 계정 {need}개면 충분 → {Kst(at)}부터 {GameKo(next)}도 같이 받음 (계정 {acct})");
            await Notice(Game, $"🔀 지금은 {GameKo(Game)} 신청을 HOME 계정 {AcctsOf(Game).Count - 1}개로도 충분히 처리할 수 있어서, **{Kst(at)}부터 {GameKo(next)} 신청도 같이 받아요**. {GameKo(Game)} 신청은 그대로 계속 받아요.");
            await Notice(next, $"🟡 **{Kst(at)}부터 이 채널에서 {GameKo(next)} 신청을 받아요!** ({GameKo(Game)}와 같이 운영 · 신청이 많아지면 다시 마감할 수 있어요)");
        }
        else spareSince = shortSince = null;
    }

    /// <summary>같이 받던 게임 마감: 새 신청만 막고, 받은 신청은 그 계정으로 끝까지 처리한 뒤 계정을 기본 게임으로 돌림</summary>
    private async Task CloseSide(string g, string why)
    {
        g = Norm(g);
        db.Set($"side_closing:{g}", "1");
        log($"동시 운영: {GameKo(g)} 신청 마감 ({why}) · 남은 {OrdG(g, NotHome).Count}건을 끝내면 계정 {string.Join(",", AcctsOf(g))}을 {GameKo(Game)}로");
        await Notice(Game, $"🔀 {why} **{GameKo(g)}** 같이 받기를 마치고 HOME 계정을 {GameKo(Game)}로 모아요.");
        await Notice(g, $"🔴 {why} **{GameKo(g)}** 신청을 마감했어요. 이미 받은 신청은 그대로 처리해요.");
    }

    /// <summary>마감 중인 게임의 남은 신청이 다 HOME으로 올라갔으면 계정을 기본 게임으로 돌림</summary>
    private void FinishClosedSides()
    {
        foreach (var g in SideGames.Where(SideClosing).ToList())
        {
            if (OrdG(g, NotHome).Count > 0)
            {
                // 맡은 계정이 없어졌으면(계정 설정 변경 등) 기본 게임 계정 하나를 빌려 남은 신청을 끝냄
                if (AcctsOf(g).Count == 0 && AcctsOf(Game).Count > 1)
                {
                    int a = AcctsOf(Game).OrderBy(x => HomeOf(x).Count).First();
                    db.Set($"acct_game:{a}", g); log($"동시 운영: {GameKo(g)} 남은 신청을 계정 {a}로 마저 처리");
                }
                continue;
            }
            var accts = AcctsOf(g);
            foreach (var a in accts) db.Set($"acct_game:{a}", "");
            SetSides(SideGames.Where(x => x != g));
            db.Set($"side_closing:{g}", "");
            log($"동시 운영: {GameKo(g)} 마침 → 계정 {string.Join(",", accts)} {GameKo(Game)}로 (HOME에 남은 교환은 그대로 진행)");
        }
    }
    /// <summary>지금 게임에서 처리할 신청만. 다른 게임으로 받은 신청 중 아직 게임 쪽(대기·생성·게임 박스)에 있는 것은 그 게임으로 돌아올 때까지 보류.
    /// HOME에 올라간 신청(GTS 검색·교환)은 게임과 상관없이 진행</summary>
    private List<Order> Ord(params OrderStatus[] st) => db.Orders(st).Where(Mine).ToList();
    private bool Mine(Order o) => o.Status is OrderStatus.InHome or OrderStatus.Trading || string.IsNullOrEmpty(o.Game) || Served.Contains(Norm(o.Game));
    /// <summary>그 게임 신청만 (게임 칸이 비어 있는 옛 신청은 기본 게임)</summary>
    private bool IsOf(Order o, string g) => (string.IsNullOrEmpty(o.Game) ? Game : Norm(o.Game)) == g;
    private List<Order> OrdG(string g, params OrderStatus[] st) => db.Orders(st).Where(o => IsOf(o, g)).ToList();
    private static readonly OrderStatus[] NotHome = [OrderStatus.Queued, OrderStatus.Generated, OrderStatus.OnSwitch];
    /// <summary>다른 게임 신청이라 보류 중인 수</summary>
    public int HeldCount => db.Orders(Active).Count(o => !Mine(o));

    /// <summary>관리 창: 세이브 모드에서 바꿀 게임을 정해 전환 요청 (남은 신청을 끝낸 뒤 바뀜)</summary>
    public string RequestSwitchTo(string g)
    {
        var t = Norm(g);
        if (!Games.Contains(t)) return $"{GameKo(t)}은(는) 이 봇이 다루는 게임이 아닙니다 (설정 Switch.Games)";
        if (t == Game) return $"이미 {GameKo(t)}입니다";
        db.Set("vote_target", t); RequestSwitch(); return $"{GameKo(t)}로 전환 요청 (남은 신청 {LeftBeforeSwitch}건을 끝낸 뒤 바뀜)";
    }
    public void RequestSwitch() { switchRequested = true; log($"게임 전환 요청: {GameKo(Game)} → {GameKo(OtherGame)} (받은 신청이 끝나면 바꿈)"); }

    /// <summary>그 게임의 신청을 다시 받기 시작하는 시각 (지금 게임이면 null)</summary>
    public DateTime? NextStartOf(string game)
    {
        game = Norm(game);
        if (game == Game) return null;
        var at = SwitchAt ?? DateTime.UtcNow;
        var busy = LeftBeforeSwitch;   // 남은 신청을 끝낸 뒤 바뀜
        var (prep, trade, _) = EtaStats();
        var drain = TimeSpan.FromSeconds(busy * pace.TotalSeconds);
        var t = DateTime.UtcNow + drain > at ? DateTime.UtcNow + drain : at;
        return t + TimeSpan.FromMinutes(2);   // 게임 바꾸는 시간
    }
    public static string Kst(DateTime utc) => utc.ToLocalTime().ToString("HH:mm");

    /// <summary>대기열 맨 끝에 새로 들어온다면 교환 차례까지 걸릴 시간</summary>
    public TimeSpan EtaNew()
    {
        EtaStats();
        return TimeSpan.FromSeconds((OrdG(Game, Active).Count + 1) * pace.TotalSeconds);   // 내 교환 끝날 때까지
    }

    /// <summary>이 게임으로 지금 신청을 받을 수 있는지. 안 되면 신청자에게만 보일 안내</summary>
    /// <summary>새 신청 마감 (이미 받은 대기열만 처리). 관리 창 명령 intake:close / intake:open</summary>
    public bool IntakeClosed => db.Get("intake_closed") == "1";
    /// <summary>스위치 쉬기: 스위치 줄만 쉼 (휴대폰 GTS 교환·생성은 계속). 하던 스위치 작업은 끝까지 마친 뒤 멈춤 — 스위치를 직접 쓸 때</summary>
    public bool SwitchHold
    {
        get => db.Get("switch_hold") == "1";
        set { db.Set("switch_hold", value ? "1" : "0"); log(value ? "스위치 쉬기: 지금 스위치 작업을 마치면 스위치를 쉼 (휴대폰 교환은 계속)" : "스위치 쉬기 끝"); }
    }
    public string SetIntake(bool open) { db.Set("intake_closed", open ? "0" : "1"); log(open ? "새 신청 다시 받기" : "새 신청 마감 (지금 대기열까지만 처리)"); return open ? "새 신청을 다시 받습니다" : "새 신청 마감: 지금 대기열까지만 처리합니다"; }

    /// <summary>미리 신청을 받는 게임: 세이브 모드에서 전환이 정해졌고 신청 마감이 아닐 때 그 바뀔 게임</summary>
    public string PreOrderGame => switchRequested && !restarting && !IntakeClosed && !IsOpen(OtherGame) ? OtherGame : null;

    public string GameGate(string game)
    {
        game = Norm(game);
        if (restarting) return "봇이 게임을 바꾸고 다시 켜지는 중이에요. 1~2분 뒤에 다시 신청해 주세요.";
        if (IntakeClosed) return "지금은 새 신청을 받지 않고 있어요. 이미 받은 신청만 처리하는 중이니, 다시 열리면 신청해 주세요.";
        if (game != Game && IsOpen(game)) return null;   // 같이 운영 중인 게임
        if (game != Game && PreOrderGame == game) return null;   // 곧 바뀔 게임: 미리 신청 받음 (바뀐 뒤 순서대로 처리)
        if (game != Game && SideGames.Contains(game) && SideClosing(game)) return $"**{GameKo(Game)}** 신청이 많아져서 **{GameKo(game)}** 같이 받기는 마감했어요. 다시 열리면 이 채널에 알려 드릴게요.";
        if (game != Game)
        {
            if (!Timed && !switchRequested)
                return $"지금은 **{GameKo(Game)}**만 교환하고 있어요. **{GameKo(game)}** 교환은 잠시 쉬고 있으니, 다시 열리면 신청해 주세요." + (VoteMode ? $" (게임전환-투표 채널에서 {cfg.Vote.Needed}명이 모이면 바뀌어요)" : "");
            var at = NextStartOf(game);
            return $"지금은 **{GameKo(Game)}** 교환 시간이에요. **{GameKo(game)}** 신청은 {(at is { } t ? $"**{Kst(t)}**쯤부터" : "다음 교대 때부터")} 받아요.";
        }
        if (switchRequested) return $"곧 **{GameKo(OtherGame)}** 교환으로 바뀌어서 지금은 **{GameKo(Game)}** 신청을 더 받지 않아요. 다음 {GameKo(Game)} 교환 시간에 다시 신청해 주세요.";
        if (SwitchAt is { } end && OrdG(Game, Active).Count > 0)   // 대기열이 비어 있으면 늘 받음
        {
            var eta = EtaNew(); if (eta > Period / 2) eta = Period / 2;   // 추정치가 커도 주기 절반까지만
            var done = DateTime.UtcNow + eta;
            if (done > end)
            {
                var back = end + TimeSpan.FromMinutes(2) + Period;
                return $"지금 대기열로는 이번 **{GameKo(Game)}** 교환 시간(~{Kst(end)}) 안에 차례가 오지 않아요. 다음 {GameKo(Game)} 교환이 시작되는 **{Kst(back)}**쯤 다시 신청해 주세요.";
            }
        }
        return null;
    }

    /// <summary>받은 신청이 다 끝났고 교대 시각이 됐으면 스위치 게임을 바꾸고 봇을 다시 켬</summary>
    private async Task<bool> MaybeSwitchGame(CancellationToken ct)
    {
        if (!SwitchPending) return false;
        if (OrdG(Game, NotHome).Count > 0) { if (SwitchState.StartsWith("대기") || SwitchState.StartsWith("다음")) SwitchState = $"{GameKo(OtherGame)}로 바꾸기 전, 남은 신청 처리 중"; return false; }
        var target = OtherGame;
        if (!await homeLock.WaitAsync(0, ct)) return false;
        try
        {
            SwitchState = $"{GameKo(target)}로 게임 바꾸는 중";
            log($"게임 전환 시작: {GameKo(Game)} → {GameKo(target)}");
            // 세이브 모드: 봇을 다시 켜지 않고 바로 바꿈 (게임마다 생성 엔진이 따로 있음).
            // HOME에 올라간 이전 게임 신청과 같이 받는 게임의 교환은 그대로 계속됨
            try { GenOf(target); }   // 엔진을 먼저 준비 — 실패하면 기록을 바꾸지 않고 멈춤 (봇 전체가 멈추지 않게)
            catch (Exception ex) { LastError = $"{GameKo(target)} 생성 엔진 준비 실패: {ex.Message}"; SwitchState = "전환 실패 (엔진 준비)"; await AdminAlert(LastError + " → 게임을 바꾸지 않았습니다. 관리 창에서 다른 게임을 고르거나 봇을 다시 켜 주세요"); switchRequested = false; db.Set("vote_target", ""); return false; }
            var sides = SideGames.Where(x => x != target).ToList();
            foreach (var a in Enumerable.Range(1, Accounts)) if (NormOrNull(db.Get($"acct_game:{a}")) == target) db.Set($"acct_game:{a}", "");
            db.Set("side_plan", ""); noSave.Clear();
            db.Set($"side_closing:{target}", "");
            SetSides(sides);
            db.Set("game", target); db.Set("game_since", DateTime.UtcNow.ToString("o")); db.Set("vote_target", "");
            cfg.Switch.Game = target;
            switchRequested = false;
            spareSince = shortSince = null;
            log($"게임 전환 완료 (세이브 모드) → {GameKo(target)} · 다시 켜지 않음" + (sides.Count > 0 ? $" · 같이 받는 게임 {string.Join(",", sides.Select(GameKo))}" : ""));
            SwitchState = "대기 (세이브 모드)";
            return true;
        }
        finally { homeLock.Release(); }
    }

    // =============== 디스코드가 부르는 쪽 ===============
    public (List<OrderSpec> specs, string preview, string problem) Preview(string text) => Preview(text, gen);
    public (List<OrderSpec> specs, string preview, string problem) PreviewFor(string text, string game) => Preview(text, GenOf(game ?? Game));
    private (List<OrderSpec> specs, string preview, string problem) Preview(string text, GenService gen)
    {
        if (GenService.LooksShowdown(text))
        {
            var set = gen.ParseShowdown(text, out var prob);
            if (set == null) return (null, "", prob);
            var bad = set.InvalidLines.Count > 0 ? "  (읽지 못한 줄: " + string.Join(", ", set.InvalidLines.Take(3).Select(x => x.Value)) + ")" : "";
            return ([], "• 쇼다운 세트: " + gen.DescribeSet(set) + bad, null);
        }
        List<OrderSpec> specs;
        try { specs = gen.Parse(text); } catch (Exception ex) { return (null, "", "해석 실패: " + ex.Message); }
        if (specs.Count == 0) return (null, "", "포켓몬을 알아보지 못했습니다");
        if (specs.Any(o => o.Species == 0)) return (null, "", "포켓몬 이름을 찾지 못했습니다" + (specs[0].Unknown.Count > 0 ? " (모르는 말: " + string.Join(", ", specs.SelectMany(o => o.Unknown).Distinct()) + ")" : ""));
        var lines = new List<string>();
        foreach (var o in specs)
        {
            var extra = new List<string>();
            if (o.Unknown.Count > 0) extra.Add("모르는 말: " + string.Join(", ", o.Unknown));
            if (!gen.InGame(o)) extra.Add($"{gen.GameName}에는 없는 포켓몬");
            lines.Add("• " + gen.Describe(o) + (extra.Count > 0 ? "  (" + string.Join(" · ", extra) + ")" : ""));
        }
        return (specs, string.Join("\n", lines), null);
    }

    private string CheckUser(ulong user)
    {
        var u = db.EnsureUser(user);
        if (u.Blocked) return "차단된 사용자입니다";
        if (db.OrdersOf(user, true).Count >= cfg.Limits.PerUserActive) return "이미 진행 중인 신청이 있습니다 (/대기열 · /취소)";
        if (db.OrdersToday(user) >= cfg.Limits.PerUserDaily) return $"오늘 신청 한도({cfg.Limits.PerUserDaily}번)를 다 썼습니다";
        return null;
    }

    /// <summary>문장 또는 쇼다운 세트 신청.</summary>
    public async Task<(bool ok, string msg, long id)> SubmitAsync(ulong user, string text, KeyPoke key, ulong channel, string game = null)
    {
        if (GameGate(game ?? Game) is { } closed) return (false, closed, 0);
        if (CheckUser(user) is { } bad) return (false, bad, 0);
        if (key == null || !key.Valid) return (false, "GTS에 올릴 포켓몬 정보가 필요합니다", 0);
        text = (text ?? "").Trim();
        var gg = Norm(game ?? Game);
        var pre = gg != Game && !IsOpen(gg) ? gg : null;   // 같이 열린 게임이면 바로 받고, 아니면(전환 예정 게임) 미리 신청
        if (pre != null)
        {
            // 미리 신청: 지금 엔진은 다른 게임이라 이름만 확인하고, 실제 생성·게임에 있는지 확인은 바뀐 뒤 그 게임으로
            string what, kind = "text", okey;
            if (GenService.LooksShowdown(text))
            {
                var set = gen.ParseShowdown(text, out var prob);
                if (set == null) return (false, prob, 0);
                what = gen.DescribeSet(set); kind = "showdown"; okey = $"sd:{set.Species}";
            }
            else
            {
                var (sp, _, pb) = Preview(text);
                if (pb != null) return (false, pb, 0);
                if (sp.Count > 1) return (false, "한 번에 한 마리만 신청할 수 있습니다", 0);
                what = gen.Describe(sp[0]); okey = OrderKey.Of(sp[0]);
            }
            var pid0 = db.AddOrder(user, text, what, okey, Route.HomeGts, kind, channel, key, game: pre);
            log($"미리 신청 #{pid0} {user} ({GameKo(pre)}): {what}");
            return (true, Accepted(pid0, what, key, pre), pid0);
        }
        var ge = GenOf(gg);
        if (GenService.LooksShowdown(text))
        {
            var set = ge.ParseShowdown(text, out var prob);
            if (set == null) return (false, prob, 0);
            if (!ge.InGame(set.Species, set.Form)) return (false, $"{ge.GameName}에는 없는 포켓몬이라 만들 수 없습니다", 0);
            var id0 = db.AddOrder(user, text, ge.DescribeSet(set), $"sd:{set.Species}", Route.HomeGts, "showdown", channel, key, game: gg);
            log($"신청 #{id0} {user}" + (gg != Game ? $" ({GameKo(gg)})" : "") + $": 쇼다운 {ge.DescribeSet(set)}");
            return (true, Accepted(id0, ge.DescribeSet(set), key, game: gg), id0);
        }
        var (specs, _, problem) = Preview(text, ge);
        if (problem != null) return (false, problem, 0);
        if (specs.Count > 1) return (false, "한 번에 한 마리만 신청할 수 있습니다", 0);
        var o = specs[0];
        if (!ge.InGame(o)) return (false, $"{ge.GameName}에는 없는 포켓몬이라 만들 수 없습니다", 0);
        var id = db.AddOrder(user, text, ge.Describe(o), OrderKey.Of(o), Route.HomeGts, "text", channel, key, game: gg);
        log($"신청 #{id} {user}" + (gg != Game ? $" ({GameKo(gg)})" : "") + $": {ge.Describe(o)}");
        return (true, Accepted(id, ge.Describe(o), key, game: gg), id);
    }

    /// <summary>파일 신청: 읽어서 바로 풀에 넣음 (생성 단계 없음).</summary>
    public async Task<(bool ok, string msg, long id)> SubmitFileAsync(ulong user, byte[] data, string fileName, KeyPoke key, ulong channel, string game = null)
    {
        if (GameGate(game ?? Game) is { } closed) return (false, closed, 0);
        if (CheckUser(user) is { } bad) return (false, bad, 0);
        if (key == null || !key.Valid) return (false, "GTS에 올릴 포켓몬 정보가 필요합니다", 0);
        var fg = Norm(game ?? Game);
        if (fg != Game && !IsOpen(fg) && fg is var pg)
        {
            // 미리 신청한 파일: 받은 그대로 두었다가 게임이 바뀐 뒤 그 게임 형식으로 바꾸고 합법 확인 (GenerateNext)
            PKM raw = null;
            try { raw = EntityFormat.GetFromBytes(data, EntityFileExtension.GetContextFromExtension(Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant(), EntityContext.None)); } catch { }
            try { raw ??= EntityFormat.GetFromBytes(data, EntityContext.None); } catch { }
            if (raw == null || raw.Species == 0) return (false, "포켓몬 파일로 읽지 못했습니다 (.pk9 · .pb8 · .pk8 · .pa8 등)", 0);
            var d0 = (raw.IsShiny ? "이로치 " : "") + gen.Strings.Species[raw.Species] + $" Lv.{raw.CurrentLevel} (파일)";
            var fid = db.AddOrder(user, fileName, d0, $"file:{raw.Species}", Route.HomeGts, "file", channel, key, game: pg);
            var rp = db.AddPool(data, Path.GetExtension(fileName ?? "").TrimStart('.'), raw.Species, raw.Form, raw.IsShiny, $"fileraw:{fid}");
            db.Reserve(rp, fid); db.SetOrderPool(fid, rp);
            log($"미리 신청 #{fid} {user} ({GameKo(pg)}): 파일 {fileName} → {d0}");
            return (true, Accepted(fid, d0, key, pg), fid);
        }
        var (pk, msg) = GenOf(fg).FromFile(data, fileName);
        if (pk == null) return (false, msg, 0);
        if (BdspBlock(fg, pk, 0) is { } bb) return (false, bb, 0);
        var d = (pk.IsShiny ? "이로치 " : "") + gen.Strings.Species[pk.Species] + $" Lv.{pk.CurrentLevel} (파일)";
        var id = db.AddOrder(user, fileName, d, $"file:{pk.Species}", Route.HomeGts, "file", channel, key, OrderStatus.Generated, game: fg);
        var pid = db.AddPool(GenService.Bytes(pk), pk.Extension, pk.Species, pk.Form, pk.IsShiny, $"file:{id}");
        db.Reserve(pid, id); db.SetOrderPool(id, pid);
        log($"신청 #{id} {user}" + (fg != Game ? $" ({GameKo(fg)})" : "") + $": 파일 {fileName} → {d}");
        // 공개 카드는 차례가 와서 게임 세이브에 넣을 때 올림 (문장 신청과 같은 시점)
        return (true, Accepted(id, d, key, game: fg), id);
    }

    private string Accepted(long id, string what, KeyPoke key, string pre = null, string game = null)
    {
        db.SetOrderGame(id, pre ?? game ?? Game);
        if (pre != null)
        {
            int ahead = db.Orders(Active).Count(x => x.Id < id && IsOf(x, pre));
            var po = db.GetOrder(id);
            if (po != null) _ = Task.Run(() => notify(po.DiscordId, $"📝 #{id} **{what}** 미리 신청 접수 ({GameKo(pre)}) · 지금 {GameKo(Game)} 신청이 끝나고 {GameKo(pre)}로 바뀌면 순서대로 처리해요 (미리 신청 {ahead + 1}번째). 차례가 가까워지면 다시 알려 드릴게요."));
            return $"신청 #{id} 미리 접수 · **{what}** ({GameKo(pre)})\n지금 {GameKo(Game)} 신청을 마저 끝내고 **{GameKo(pre)}**로 바뀌면 순서대로 처리해요 · 미리 신청 {ahead + 1}번째\n"
                + GtsGuide(what, key) + "\n봇 차례가 되면 DM으로 알려 드립니다.";
        }
        var og = Norm(game ?? Game);
        int pos = OrdG(og, Active).Count(x => x.Id < id) + 1; var eta = EtaText(id);
        var o = db.GetOrder(id);
        // 앞에 기다리는 사람이 있으면 DM으로도 남겨 둠 (접수 메시지는 본인에게만 잠깐 보이므로)
        if (pos > 1 && o != null)
            _ = Task.Run(() => notify(o.DiscordId, $"📝 #{id} **{what}** 접수됐어요 · 대기 {pos}번 · {eta}\n차례가 가까워지면 다시 알려 드릴게요. /대기열 로 언제든 남은 시간을 볼 수 있어요."));
        return $"신청 #{id} 접수 · **{what}** · 대기 {pos}번 · {eta}\n" + GtsGuide(what, key) + "\n봇 차례가 되면 DM으로 알려 드립니다.";
    }

    /// <summary>사용자가 GTS에 맡기는 방법 안내.</summary>
    public string GtsGuide(string botMon, KeyPoke k, PKM pk = null) =>
        "**GTS에 맡기는 방법** (지금 미리 맡겨 둬도 됩니다)\n" +
        $"> 포켓몬 HOME → 교환 → GTS → 포켓몬을 맡긴다 → **{k.Ko()}** 선택\n" +
        (pk != null
            ? $"> 원하는 포켓몬: **{SpeciesOf(botMon)}** · 성별 **{GenderKo(pk)}** · 레벨 **{LevelBand(pk)}**\n" +
              "> 원하는 포켓몬 조건을 위처럼 봇 포켓몬에 꼭 맞게 설정해 주세요. '상관없음'으로 두면 다른 사람이 먼저 가져가 버릴 수 있어요.\n"
            : $"> 원하는 포켓몬: **{SpeciesOf(botMon)}** · 성별·레벨은 봇이 포켓몬을 만든 뒤 차례 알림 DM에서 알려 드려요 (그 조건에 맞게 설정해 주세요)\n") +
        "> 맡긴 포켓몬의 레벨·성별·언어가 위와 다르면 봇이 찾지 못합니다.";
    public string GtsGuide(Order o) => GtsGuide(o.Describe, o.Key2, BotPk(o));
    private PKM BotPk(Order o) { try { var it = o.PoolId != null ? db.GetPool(o.PoolId.Value) : null; return it != null ? gen.FromBytes(it.PkBytes, it.Format) : null; } catch { return null; } }
    private static string GenderKo(PKM pk) => pk.Gender switch { 0 => "수컷", 1 => "암컷", _ => "상관없음(성별 없음)" };
    private static string LevelBand(PKM pk) { int lv = Math.Clamp((int)pk.CurrentLevel, 1, 100); int lo = (lv - 1) / 10 * 10 + 1; return $"{lo}~{lo + 9}"; }
    private static string SpeciesOf(string describe) { var w = describe.Replace("이로치 ", "").Split(' ', StringSplitOptions.RemoveEmptyEntries); return w.Length > 0 ? w[0] : describe; }

    public string Status(ulong user)
    {
        var l = db.OrdersOf(user, false).Take(5).ToList();
        if (l.Count == 0) return "신청이 없습니다";
        return string.Join("\n", l.Select(o => $"#{o.Id} {o.Describe} · {o.Status.Ko()}" + (Active.Contains(o.Status) && o.Status is not (OrderStatus.Trading or OrderStatus.InHome) ? $" · 순서 {db.QueuePosition(o.Id)} · {EtaText(o.Id)}" : "") + (o.Key2.Valid ? $" · 맡길 포켓몬 {o.Key2.Ko()}" : "") + (o.Error.Length > 0 ? " · " + o.Error : "")));
    }

    /// <summary>신청자가 GTS에 맡길(맡긴) 포켓몬 정보를 고침. 교환이 끝나기 전까지 가능. HOME에 올라가 찾는 중이면 남은 시간도 새로 줌</summary>
    public string EditKey(ulong user, long? id, KeyPoke kp)
    {
        var o = id != null ? db.GetOrder(id.Value) : db.OrdersOf(user, true).FirstOrDefault();
        if (o == null || o.DiscordId != user) return "고칠 신청이 없어요";
        if (!Active.Contains(o.Status) || o.Status == OrderStatus.Trading) return "이미 끝났거나 교환 중인 신청이라 고칠 수 없어요";
        if (kp == null || !kp.Valid) return "맡길 포켓몬 정보가 올바르지 않아요";
        db.SetKey(o.Id, kp);
        ph.TryRemove(o.Id, out _);                                                        // '못 찾음' 안내를 다시 할 수 있게
        noneSinceDm.TryRemove(o.Id, out _); lock (toldRetry) toldRetry.Remove(o.Id);
        noneWait.TryRemove(o.Id, out _);   // 고쳤으면 바로 다음 차례에 찾음
        if (o.Status == OrderStatus.InHome) db.SetClaimed(o.Id, DateTime.UtcNow);   // 고친 뒤 다시 맡길 시간을 줌
        log($"#{o.Id} 맡길 포켓몬 수정 → {kp.Ko()}");
        return $"#{o.Id} 맡길 포켓몬을 **{kp.Ko()}**(으)로 고쳤어요." + (o.Status == OrderStatus.InHome ? $" 지금부터 {cfg.Limits.ClaimTimeoutSec / 60}분 동안 다시 찾아요." : "") + "\n" + GtsGuide(o.Describe, kp, BotPk(o));
    }

    public string Cancel(ulong user, long? id)
    {
        var o = id != null ? db.GetOrder(id.Value) : db.OrdersOf(user, true).FirstOrDefault();
        if (o == null || o.DiscordId != user) return "취소할 신청이 없습니다";
        if (o.Status is OrderStatus.Done or OrderStatus.Failed or OrderStatus.Cancelled) return "이미 끝난 신청입니다";
        if (o.PoolId != null) db.Reserve(o.PoolId.Value, null);
        db.SetStatus(o.Id, OrderStatus.Cancelled);
        return $"#{o.Id} 취소했습니다" + (o.Status == OrderStatus.Trading ? " (지금 검색 중인 것이 끝나면 멈춥니다)" : "");
    }

    /// <summary>예전 명령 호환: 역 GTS에서는 봇이 알아서 찾으므로 안내만.</summary>
    public string Claim(ulong user)
    {
        var o = db.OrdersOf(user, true).FirstOrDefault();
        if (o == null) return "진행 중인 신청이 없습니다";
        return $"#{o.Id} {o.Status.Ko()} · 봇이 차례가 되면 GTS에서 맡긴 포켓몬을 찾아 교환합니다\n" + GtsGuide(o);
    }

    /// <summary>관리 창이 읽는 상태 (data/state.json)</summary>
    public object State(bool discord)
    {
        var w = sw as HomeBot.Core.Workers.SwitchWorker;
        string VerOf(string g) => w?.VersionNow(g) ?? g;
        return new
        {
            at = DateTime.Now.ToString("HH:mm:ss"), discord,
            game = Game, gameKo = GameKo(Game), ver = VerOf(Game), verKo = VersionKo(VerOf(Game)), gameSince = Kst(GameSince),
            sides = SideGames.Select(g => new { game = g, ko = GameKo(g), ver = VerOf(g), closing = SideClosing(g), accts = AcctsOf(g) }).ToList(),
            mainAccts = AcctsOf(Game), accounts = Accounts,
            hybrid = HybridOn, hybridLine = HybridLine(),
            paused = Paused, intakeClosed = IntakeClosed, switchHold = SwitchHold, switchRequested, switchTo = switchRequested ? OtherGame : null, switchToKo = switchRequested ? GameKo(OtherGame) : null,
            preOrder = PreOrderGame, voteMode = VoteMode, votes = VoteMode ? Votes().Count : 0, voteNeeded = VoteNeeded, voteOpenAt = DateTime.UtcNow < VoteOpenAt ? Kst(VoteOpenAt) : null,
            switchConnected = sw.Connected, switchState = SwitchState, phoneState = PhoneState, phone = phone != null,
            active = db.Orders(Active).Count, heldOther = HeldCount, tradesHour = TradesLastHour(), pace = PaceLine(), lastError = LastError,
            games = Games.Select(g => new { game = g, ko = GameKo(g), ver = VerOf(g), verKo = VersionKo(VerOf(g)), open = IsOpen(g), main = g == Game }).ToList(),
        };
    }
    /// <summary>관리 창: 로고 자동 등록을 다시 돌림 (없는 로고만)</summary>
    private volatile bool logoRescan;
    public string RequestLogoScan() { logoRescan = true; return "게임 로고 확인: 없는 로고가 있으면 곧 HOME에서 등록합니다 (봇 로그 확인)"; }

    public string AdminStatus()
    {
        var all = db.Orders(Active);
        var rot = $"교환 게임: {GameKo(Game)}" + (SwitchAt is { } sa ? $" · {Kst(sa)}에 {GameKo(OtherGame)}로 교대" : VoteMode ? "" : " · 자동 교대 꺼짐")
            + (VoteMode && !switchRequested ? (DateTime.UtcNow < VoteOpenAt ? $" · 전환 투표 {Kst(VoteOpenAt)}부터" : $" · 전환 투표 {Votes().Count}/{VoteNeeded}") : "") + (switchRequested ? " · 전환 요청됨" : "") + "\n" + (HybridLine() is { Length: > 0 } hl ? hl + "\n" : "");
        return rot + $"스위치 {(sw.Connected ? "연결" : "끊김")}{(Battery is { } bt ? $" · 🔋 {bt}%" : "")} · {SwitchState}\n휴대폰 {(phone != null ? "켬" : "끔")} · {PhoneState}\n{(Paused ? "⏸ 일시정지" : "▶ 가동")}{(IntakeClosed ? " · 🚫 신청 마감" : "")} · 진행 중 {all.Count}건 · 최근 1시간 교환 {db.TradesLastHour()}" +
               $"\n" + string.Join("\n", all.Take(8).Select(o => $"#{o.Id} {o.Describe} · {o.Status.Ko()}")) + (LastError.Length > 0 ? "\n마지막 오류: " + LastError : "");
    }

    // ---- 임베드용 자료 (봇 쪽에서 디스코드 임베드로 꾸밈)
    public List<Order> ActiveOrders() => db.Orders(Active);
    public List<Order> UserOrders(ulong user) => db.OrdersOf(user, false);
    public int Position(long id) => db.QueuePosition(id);
    public bool IsActive(Order o) => Active.Contains(o.Status);
    public string PaceLine() { EtaStats(); return $"한 건 약 {pace.TotalMinutes:0.0}분 · 최근 {paceN + 1}건 기준"; }
    public int TradesLastHour() => db.TradesLastHour();
    public string RotationLine() => $"{GameKo(Game)}" + (VoteMode && !switchRequested ? (DateTime.UtcNow < VoteOpenAt ? $" · 투표 {Kst(VoteOpenAt)}부터" : $" · 전환 투표 {Votes().Count}/{VoteNeeded}") : "") + (SwitchAt is { } sa ? $" · {Kst(sa)}에 {GameKo(OtherGame)}로 교대" : "") + (switchRequested ? " · 전환 요청됨" : "");
    public bool SwitchConnected => sw.Connected;

    public string QueueText()
    {
        var all = db.Orders(Active);
        if (all.Count == 0) return "대기 중인 신청이 없습니다";
        var (prep, trade, n) = EtaStats();
        return string.Join("\n", all.Take(15).Select((o, i) => $"{i + 1}. #{o.Id} {o.Describe} · {o.Status.Ko()}" + (o.Status is OrderStatus.InHome or OrderStatus.Trading ? "" : " · " + EtaText(o.Id)))) +
               $"\n(처리 속도: 한 건 약 {pace.TotalMinutes:0.0}분 · 최근 {paceN + 1}건 기준)";
    }

    // =============== 예상 시간 ===============
    // 지금까지 끝난 신청 기록으로 한 건에 걸리는 시간을 잼:
    //   준비 = 앞 신청이 끝난(또는 이 신청이 들어온) 때 → HOME 도착 (생성·주입·스위치 HOME 이동)
    //   교환 = HOME 도착 → 교환 끝 (휴대폰 GTS 검색, 사용자가 언제 맡기느냐에 따라 달라짐)
    // 가끔 생기는 시간 초과(10분)나 재시작에 휘둘리지 않게 평균 대신 중앙값을 씀.
    private (TimeSpan prep, TimeSpan trade, int n, DateTime at) etaCache;
    private TimeSpan pace = TimeSpan.FromSeconds(150); private int paceN;   // 한 건이 끝나는 평균 간격 (EtaStats가 계산)

    public (TimeSpan prep, TimeSpan trade, int samples) EtaStats()
    {
        if (etaCache.at > DateTime.UtcNow.AddSeconds(-30)) return (etaCache.prep, etaCache.trade, etaCache.n);
        var runs = db.RecentRuns(21);
        var preps = new List<double>(); var trades = new List<double>();
        for (int i = 0; i < runs.Count; i++)
        {
            var (created, claimed, finished) = runs[i];
            var start = i > 0 && runs[i - 1].finished > created ? runs[i - 1].finished : created;
            if (i > 0 || runs.Count == 1)
            {
                var p = (claimed - start).TotalSeconds;
                if (p is > 15 and < 1200) preps.Add(p);
            }
            var t = (finished - claimed).TotalSeconds;
            if (t is > 5 and < 3600) trades.Add(t);
        }
        static double Med(List<double> l, double def) { if (l.Count == 0) return def; l.Sort(); return l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2; }
        var prep = TimeSpan.FromSeconds(Med(preps, 100)); var trade = TimeSpan.FromSeconds(Med(trades, 130));
        // 실제 처리 속도: 신청이 끝나는 간격 (묶음 이동·검색·교환이 다 들어 있음). 봇이 꺼져 있던 긴 공백은 뺌
        var fin = runs.Select(r => r.finished).OrderBy(t => t).ToList(); var gaps = new List<double>();
        for (int i = 1; i < fin.Count; i++) { var g = (fin[i] - fin[i - 1]).TotalSeconds; if (g >= 0 && g <= 900) gaps.Add(g); }
        paceN = gaps.Count;
        pace = gaps.Count >= 4 ? TimeSpan.FromSeconds(Math.Clamp(gaps.Average(), 45, 600)) : prep / Math.Max(1, BatchMax) + TimeSpan.FromSeconds(90);
        etaCache = (prep, trade, Math.Min(preps.Count, trades.Count), DateTime.UtcNow);
        return (prep, trade, etaCache.n);
    }

    /// <summary>이 신청이 교환 차례(HOME 도착)가 되기까지 남은 시간. 이미 차례면 0, 끝난 신청이면 null.</summary>
    public TimeSpan? EtaTurn(long id)
    {
        var all = db.Orders(Active);
        var me = all.FirstOrDefault(o => o.Id == id);
        if (me == null) return null;
        if (me.Status is OrderStatus.InHome or OrderStatus.Trading or OrderStatus.Ready) return TimeSpan.Zero;
        var (prep, trade, _) = EtaStats();
        var now = DateTime.UtcNow;
        // 동시 운영: 같은 게임 신청만 앞에 있는 것으로 셈 (HOME에 있는 것은 그 게임 계정 몫). 속도는 그 게임을 맡은 계정 수만큼
        var g = string.IsNullOrEmpty(me.Game) ? Game : Norm(me.Game);
        var accts = AcctsOf(g);
        all = all.Where(o => o.Status is OrderStatus.InHome or OrderStatus.Trading or OrderStatus.Ready ? accts.Contains(AcctOf(o.Id)) : IsOf(o, g)).ToList();
        // 실제 처리 순서: HOME에 있는 것 → 게임 박스에 들어간 것 → 생성된 것 → 대기 (같은 단계 안에서는 대기열 순서)
        static int Stage(OrderStatus st) => st switch { OrderStatus.InHome or OrderStatus.Trading or OrderStatus.Ready => 0, OrderStatus.OnSwitch => 1, _ => 2 };
        var order = all.Select((o, i) => (o, i)).OrderBy(x => Stage(x.o.Status)).ThenBy(x => x.i).Select(x => x.o).ToList();
        // 앞에 있는 신청 수 × 실제 처리 속도 (묶음으로 옮기므로 한 건씩 더하는 것보다 이게 실제와 맞음)
        int ahead = order.TakeWhile(o => o.Id != id).Count();
        double sec = ahead * pace.TotalSeconds * Accounts / Math.Max(1, accts.Count);
        return TimeSpan.FromSeconds(sec);
    }

    public static string Dur(TimeSpan t)
    {
        int m = Math.Max(1, (int)Math.Ceiling(t.TotalMinutes));
        return m < 60 ? $"약 {m}분" : $"약 {m / 60}시간" + (m % 60 > 0 ? $" {m % 60}분" : "");
    }

    /// <summary>"예상 대기 약 7분" 같은 짧은 글. 이미 차례거나 끝났으면 빈 글.</summary>
    public string EtaText(long id)
    {
        if (db.GetOrder(id) is { } ho && !Mine(ho)) return $"{GameKo(ho.Game)}로 바뀐 뒤 처리";   // 미리 신청·다른 게임 신청
        var t = EtaTurn(id);
        if (t == null) return "";
        if (t.Value == TimeSpan.Zero) return "지금 교환 차례";
        return $"예상 대기 {Dur(t.Value)}" + (Paused ? " (봇 일시정지 중이라 더 걸릴 수 있어요)" : "");
    }

    /// <summary>이미 "곧 차례" DM을 보낸 신청</summary>
    private readonly HashSet<long> toldNext = new();

    /// <summary>HOME에 올라간 신청 바로 다음 사람에게 곧 차례라고 미리 알림 (GTS에 미리 맡겨 두라고)</summary>
    private async Task NotifyNextAsync(string g)
    {
        try
        {
            var next = OrdG(g, OrderStatus.OnSwitch, OrderStatus.Generated, OrderStatus.Queued)
                .Select((o, i) => (o, i)).OrderBy(x => x.o.Status == OrderStatus.OnSwitch ? 0 : 1).ThenBy(x => x.i).Select(x => x.o).FirstOrDefault();
            bool firstTold; lock (toldNext) firstTold = next != null && toldNext.Add(next.Id);
            if (!firstTold) return;
            var eta = EtaTurn(next.Id) ?? TimeSpan.Zero;
            await notify(next.DiscordId, $"⏰ #{next.Id} **{next.Describe}** 곧 차례예요! {Dur(eta)} 뒤 교환을 시작합니다. 아직 GTS에 맡기지 않았다면 지금 맡겨 주세요.\n" + GtsGuide(next));
        }
        catch (Exception ex) { log("다음 차례 알림 실패: " + ex.Message); }
    }

    // =============== 루프 ===============
    public async Task RunAsync(CancellationToken ct)
    {
        // 켜질 때 교대 시각이 한참 지나 있으면(봇을 오래 꺼 뒀던 경우) 지금부터 새 주기로 — 켜자마자 게임을 바꾸지 않게
        if (Timed && DateTime.UtcNow - GameSince > Period) { db.Set("game_since", DateTime.UtcNow.ToString("o")); log("교대 주기를 지금부터 새로 셉니다"); }
        // 동시 운영 기록 정리: 같이 받는 게임이 아닌데 남은 계정 배정은 지움 (나중에 그 게임을 다시 열 때 엉뚱한 계정이 끌려가지 않게)
        foreach (var an in Enumerable.Range(1, MaxAccounts)) if (NormOrNull(db.Get($"acct_game:{an}")) is { } ag && !SideGames.Contains(ag)) db.Set($"acct_game:{an}", "");
        if (SwitchHold) log("스위치 쉬기 중이라 시작 시 스위치 확인을 건너뜀");
        else try { await StartupSwitchAsync(ct); } catch (Exception ex) { log("시작 시 스위치 확인 오류: " + ex.Message); }
        try { await RecoverAsync(ct); } catch (Exception ex) { log("중단 복구 중 오류: " + ex.Message); }
        foreach (var sg in SideGames) try { GenOf(sg); } catch (Exception ex) { log($"{GameKo(sg)} 생성 엔진 준비 실패: {ex.Message}"); }   // 같이 받던 게임 엔진을 미리 준비 (스위치 줄 안에서 만들지 않게)
        var a = Loop("스위치", SwitchTick, ct);
        var g = Loop("생성", GenerateNext, ct);
        var lanes = Enumerable.Range(1, MaxAccounts).Select(k => Loop(k == 1 ? "휴대폰" : $"휴대폰{k}", c => PhoneTick(k, c), ct)).ToList();   // 동시 모드면 계정마다 한 줄
        var bat = BatteryLoop(ct);
        await Task.WhenAll(lanes.Append(a).Append(g).Append(bat));
    }

    public int? Battery { get; private set; }
    private bool batteryWarned;
    /// <summary>2분마다 스위치 배터리를 읽어 상태에 띄움. 20% 이하로 떨어지면 운영자에게 한 번 알림 (30% 넘게 다시 차면 초기화)</summary>
    private async Task BatteryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (sw.Connected && !SwitchHold)
                {
                    var v = await sw.BatteryAsync(ct);
                    if (v is { } b)
                    {
                        Battery = b;
                        if (b <= 20 && !batteryWarned) { batteryWarned = true; await AdminAlert($"🔋 스위치 배터리가 {b}%예요. 충전기를 확인해 주세요"); }
                        else if (b > 30) batteryWarned = false;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { }
            try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { break; }
        }
    }

    private async Task Loop(string name, Func<CancellationToken, Task> tick, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (!Paused) await tick(ct); LastTick = DateTime.Now; await Watch("loop:" + name, true); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { LastError = $"[{name}] {ex.Message}"; log($"{name} 줄 오류: {ex}"); await Watch("loop:" + name, false, $"{name} 줄에서 같은 오류가 계속 나고 있어요: {ex.Message}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch { break; }
        }
    }

    // ---- 조용한 실패 알림: 일시정지는 안 되지만 처리가 멈춘 것과 같은 실패가 일정 시간 넘게 이어지면 운영자에게 한 번 알림 (성공하면 초기화)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime since, bool told)> failWatch = new();
    private async Task Watch(string key, bool ok, string msg = "", int minutes = 5)
    {
        if (ok) { failWatch.TryRemove(key, out _); return; }
        var w = failWatch.GetOrAdd(key, _ => (DateTime.UtcNow, false));
        if (w.told || DateTime.UtcNow - w.since < TimeSpan.FromMinutes(minutes)) return;
        failWatch[key] = (w.since, true);
        try { await AdminAlert($"{msg} ({minutes}분 넘게 이어짐)"); } catch { }
    }

    // ---- 스위치 쪽 한 번짜리 실패(화면 한 번 잘못 읽음 등): 저장 전이면 HOME을 저장 없이 닫고 다음 차례에 다시. 10분 안에 3번이면 그때 일시정지
    private readonly List<DateTime> softFails = new();
    private async Task SoftFail(string what)
    {
        LastError = what;
        lock (softFails) { softFails.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(10)); softFails.Add(DateTime.UtcNow); }
        int n; lock (softFails) n = softFails.Count;
        if (n >= 3) { Paused = true; log($"{what} → 10분 안에 {n}번 → 일시정지"); await AdminAlert(what + $" (10분 안에 {n}번이라 일시정지)"); lock (softFails) softFails.Clear(); }
        else log($"{what} → 저장 없이 닫고 다음 차례에 다시 ({n}/3)");
    }
    private async Task DiscardHome(CancellationToken ct)
    {
        try { var d = await sw.RunMacroAsync("home_discard", new Dictionary<string, string>(), ct); if (!d.Ok) log("HOME 닫기 확인 실패: " + d.Message); } catch (Exception dex) when (dex is not OperationCanceledException) { log("HOME 닫기 오류: " + dex.Message); }
    }

    /// <summary>켜질 때 스위치가 타이틀·로딩·HOME 앱 등 어디에 있든 봇 게임의 필드까지 데려다 놓음 (게임 전환 직후 재시작 포함)</summary>
    private async Task StartupSwitchAsync(CancellationToken ct)
    {
        SwitchState = "시작: 스위치 상태 확인";
        if (!await EnsureSwitch(ct)) { log("스위치에 연결되지 않아 시작 확인을 건너뜁니다 (연결되면 그때 확인)"); return; }   // HOME 앱이 켜져 있으면 여기서 게임으로 돌아옴
        sw.Disconnect();
        // 게임은 켜지 않음. HOME 이동 도중 꺼졌으면 HOME 앱이 켜져 있을 수 있음 → 저장하지 않고 닫음 (세이브를 다시 쓰려면 닫혀 있어야 함)
        try { var d = await sw.RunMacroAsync("home_discard", new Dictionary<string, string>(), ct); if (!d.Ok) log("시작: HOME 닫기 확인 실패: " + d.Message); } catch (Exception ex) when (ex is not OperationCanceledException) { log("시작: HOME 닫기 오류: " + ex.Message); }
        try { await sw.ConnectAsync(ct); } catch (Exception ex) { log("시작: 재연결 실패: " + ex.Message); }
        SwitchState = "대기";
    }

    // ---------- 게임 로고 자동 등록: HOME 게임 선택 화면에서 봇이 쓰는 게임을 로고로 찾는데, 그 그림이 없으면(스칼렛·소드·펄·이브이, 다른 언어판) 처음 한 번 HOME에 들어가 목록을 돌며 잘라 둠
    private readonly HashSet<string> logoTried = new(), logoOk = new();
    private async Task EnsureLogosAsync(CancellationToken ct)
    {
        if (sw is not HomeBot.Core.Workers.SwitchWorker w || Paused || SwitchHold) return;
        if (logoRescan) { logoRescan = false; logoTried.Clear(); logoOk.Clear(); }
        var need = new List<string>();
        foreach (var g in Served)
        {
            var ver = w.VersionNow(g);
            if (logoTried.Contains(ver) || logoOk.Contains(ver)) continue;
            try { if (await w.LogoExistsAsync(ver, ct)) { logoOk.Add(ver); continue; } }
            catch (Exception) { return; }   // 화면 인식 서비스가 꺼짐 → GuardedVision이 일시정지시킴
            need.Add(ver);
        }
        if (need.Count == 0) return;
        if (!await homeLock.WaitAsync(0, ct)) return;
        if (!await acctLock[1].WaitAsync(0, ct)) { homeLock.Release(); return; }
        foreach (var v in need) logoTried.Add(v);   // 실패해도 켜 있는 동안 다시 돌지 않음 (관리 창에서 수동 등록 가능)
        try
        {
            SwitchState = "게임 로고 등록 중";
            log($"게임 로고 등록: {string.Join("·", need.Select(GameKoV))} 로고 그림이 없어 HOME 게임 선택 화면에서 잘라 둡니다 (처음 한 번)");
            // HOME 계정 1로 들어감 → 같은 계정 휴대폰이 HOME을 쓰고 있으면 먼저 닫음
            if (phone != null) { try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(150)); await phone.HomeCloseAsync(cts.Token, 1); } catch (Exception ex) { log("휴대폰 HOME 닫기 확인 못 함: " + ex.Message); } }
            sw.HomeUserOverride = null;
            if (!await EnsureSwitch(ct)) return;
            var r = await w.RunMacroAsync("home_open", new Dictionary<string, string>(), ct, "pick_game_1");
            if (!r.Ok) { LastError = "게임 로고 등록: HOME 게임 선택 화면까지 가지 못함: " + r.Message; await AdminAlert(LastError + " → 관리 창 [게임 로고 등록]으로 직접 등록해 주세요"); return; }
            var (saved, seen) = await w.ScanLogosAsync(need, ct);
            var missing = need.Where(v => !saved.Contains(v)).ToList();
            var msg = (saved.Count > 0 ? $"게임 로고 등록 완료: {string.Join("·", saved.Select(GameKoV))}" : "게임 로고를 찾지 못함")
                + (missing.Count > 0 ? $" · 목록에 없음: {string.Join("·", missing.Select(GameKoV))} (본 게임: {string.Join("·", seen.Select(GameKoV))}) → 관리 창 [게임 로고 등록]으로 직접 등록하거나, 그 게임이 세이브 주인 사용자의 것인지 확인" : "");
            log(msg); await AdminAlert(msg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { LastError = "게임 로고 등록 오류: " + ex.Message; log(LastError); }
        finally
        {
            try { var d = await sw.RunMacroAsync("home_discard", new Dictionary<string, string>(), ct); if (!d.Ok) log("로고 등록 뒤 HOME 닫기 실패: " + d.Message); } catch (Exception ex) { log("로고 등록 뒤 HOME 닫기 오류: " + ex.Message); }
            sw.HomeUserOverride = null;
            acctLock[1].Release(); homeLock.Release();
            SwitchState = "대기";
        }
    }
    private static string GameKoV(string ver) => VersionKo(ver);

    // ---------- 봇이 중간에 꺼졌다 켜졌을 때: DB에 남은 상태를 실제 상태에 맞춰 이어서 진행
    private async Task RecoverAsync(CancellationToken ct)
    {
        // 봇이 꺼져 있을 때 관리 창에서 취소한 신청: 그때 못 보낸 DM을 지금 보냄
        foreach (var (k, v) in db.KvPrefix("dm_pending:"))
        {
            var p = v.Split('\t', 2);
            if (p.Length == 2 && ulong.TryParse(p[0], out var uid)) { try { await notify(uid, p[1]); } catch { } }
            db.KvDelete(k);
        }
        var active = Ord(Active);
        if (active.Count == 0) return;
        log($"중단 복구: 진행 중 신청 {active.Count}건 확인" + (HeldCount > 0 ? $" (다른 게임 신청 {HeldCount}건은 그 게임으로 돌아올 때까지 보류)" : ""));
        if (db.Get("turn_dm_mode") != "1")
        {
            // '교환 차례' DM을 첫 검색 때 보내도록 바뀐 첫 실행: 이미 HOME에 있는 신청은 옛 방식으로 DM을 받았으니 다시 보내지 않음
            foreach (var o in active.Where(o => o.Status is OrderStatus.InHome or OrderStatus.Trading)) db.Set($"turn_dm:{o.Id}", "1");
            db.Set("turn_dm_mode", "1");
        }
        // 예전 방식(접수 즉시 카드)으로 이미 카드가 올라간 신청은 다시 올리지 않게 표시
        foreach (var o in active.Where(o => o.Status is not (OrderStatus.Queued or OrderStatus.Generated))) if (db.Get($"card:{o.Id}") != "1") db.Set($"card:{o.Id}", "1");
        var boxNowOf = new Dictionary<string, List<PKM>>();   // 게임마다 박스는 한 번만 읽음 (신청마다 읽으면 한 건에 10초 넘게 걸림)
        foreach (var o0 in active)
        {
            var o = db.GetOrder(o0.Id);
            if (o == null || o.Status != o0.Status) continue;   // 복구 도중 관리 창에서 상태가 바뀐 신청은 건드리지 않음
            switch (o.Status)
            {
                case OrderStatus.Trading:
                    // 휴대폰 GTS 검색 도중 꺼짐 → HOME 1-1에 그대로 있으니 검색을 처음부터 다시 (제한 시간도 새로)
                    db.SetStatus(o.Id, OrderStatus.InHome, note: "중단 뒤 재개"); db.SetClaimed(o.Id, DateTime.UtcNow);
                    log($"#{o.Id} 교환 중이었음 → GTS 검색을 다시 시작");
                    break;
                case OrderStatus.InHome:
                    db.SetClaimed(o.Id, DateTime.UtcNow);   // 남은 시간을 새로 줌
                    log($"#{o.Id} HOME에 있음 → GTS 검색 이어서");
                    break;
                case OrderStatus.OnSwitch:
                {
                    if (SwitchHold) { log($"#{o.Id} 게임 박스에 있음 (스위치 쉬기 중이라 확인은 나중에)"); break; }
                    // 게임 박스에 아직 있는지 확인. 없으면 이미 HOME으로 옮겨진 뒤 꺼진 것 → HOME에 있다고 보고 진행
                    var item = o.PoolId != null ? db.GetPool(o.PoolId.Value) : null;
                    if (item == null) { Fail(o, "배정된 개체가 없습니다"); break; }
                    if (!await EnsureSwitch(ct)) { log($"#{o.Id} 스위치에 연결되지 않아 확인 보류 (연결되면 그대로 이어서)"); break; }
                    try
                    {
                        var og = string.IsNullOrEmpty(o.Game) ? Game : Norm(o.Game);
                        if (!boxNowOf.TryGetValue(og, out var box))
                        {
                            if (sw is HomeBot.Core.Workers.SwitchWorker w) w.GameOverride = og;
                            try { box = await sw.ReadBoxAsync(cfg.Switch.TransferBox - 1, ct); }
                            finally { if (sw is HomeBot.Core.Workers.SwitchWorker w2) w2.GameOverride = null; }
                            boxNowOf[og] = box;
                        }
                        var pk = gen.FromBytes(item.PkBytes, item.Format);
                        int sl = item.Slot is >= 0 and < 30 ? item.Slot : 0;
                        bool here = box.Count > sl && box[sl] != null && Same(box[sl], pk);
                        if (!here) { int at = box.FindIndex(b => b != null && Same(b, pk)); if (at >= 0) { here = true; sl = at; db.SetLocation(item.Id, Location.Switch, cfg.Switch.TransferBox - 1, at); } }
                        if (here) log($"#{o.Id} 게임 박스 1-{sl + 1}에 있음 → HOME 이동부터 이어서");
                        else
                        {
                            db.SetLocation(item.Id, Location.Home, 1, item.Slot is >= 0 and < 30 ? item.Slot : 0, verified: false);
                            db.SetStatus(o.Id, OrderStatus.InHome, note: "중단 뒤 재개"); db.SetClaimed(o.Id, DateTime.UtcNow);
                            log($"#{o.Id} 게임 박스에 없음 → HOME에 올라간 것으로 보고 GTS 검색부터");
                        }
                    }
                    catch (Exception ex) { log($"#{o.Id} 박스 확인 실패: {ex.Message} (HOME 이동부터 이어서)"); }
                    break;
                }
                case OrderStatus.Generated:
                    log($"#{o.Id} 생성만 된 상태 → {GameKo(Game)}에 넣기부터 이어서");
                    break;
                case OrderStatus.Queued:
                    log($"#{o.Id} 대기 중 → 생성부터");
                    break;
            }
        }
    }

    // ---------- 스위치 줄
    private async Task SwitchTick(CancellationToken ct)
    {
        if (SwitchHold) { SwitchState = "⏸ 스위치 쉬기 · 스위치 쉬는 중 (지금 스위치를 직접 써도 됨)"; return; }
        await RefreshNameOcr(ct);
        if (await MaybeSwitchGame(ct)) return;
        await EnsureLogosAsync(ct);
        await PlanTick(ct);
        // 게임마다 차례로: 그 게임 세이브에 넣고, 그 게임을 맡은 HOME 계정으로 옮김 (가장 오래 기다린 신청이 있는 게임부터)
        var games = Served.Select(g => (g, first: OrdG(g, NotHome).Select(o => o.Id).DefaultIfEmpty(long.MaxValue).Min())).OrderBy(x => x.first).Select(x => x.g).ToList();
        foreach (var g in games)
        {
            if (ct.IsCancellationRequested) break;
            if (AcctsOf(g).Count == 0 && !(SideClosing(g) && OrdG(g, NotHome).Count > 0)) continue;   // 마감 중인데 맡은 계정이 없으면 기본 게임 계정으로 마저 처리
            if (sw is HomeBot.Core.Workers.SwitchWorker w) w.GameOverride = g;
            try
            {
                await InjectNext(g, ct);   // 생성은 따로 도는 '생성' 줄이 미리 해 둠 (스위치가 HOME에 가 있는 동안에도)
                await MoveToHome(g, ct);
            }
            finally { if (sw is HomeBot.Core.Workers.SwitchWorker w2) w2.GameOverride = null; }
        }
        FinishClosedSides();
    }

    /// <summary>가장 오래된 대기 신청 하나 생성 (미리 만들어 두는 건 한 마리까지)</summary>
    // 여러 마리 묶음은 휴대폰 워커가 포켓몬 이름을 읽을 수 있을 때만 (GTS 박스 화면 순서가 HOME 칸 순서와 달라 이름으로 골라야 함)
    private int BatchMax => nameOcr ? Math.Clamp(cfg.Limits.MoveBatchMax, 1, 30) : 1;
    private bool nameOcr; private DateTime nameOcrAt;
    private async Task RefreshNameOcr(CancellationToken ct)
    {
        if (phone == null || DateTime.UtcNow - nameOcrAt < TimeSpan.FromMinutes(1)) return;
        nameOcrAt = DateTime.UtcNow;
        bool v = await phone.NameOcrAsync(ct);
        if (v != nameOcr) log(v ? $"휴대폰 이름 인식 사용 가능 → 묶음 최대 {Math.Clamp(cfg.Limits.MoveBatchMax, 1, 30)}마리" : "휴대폰 이름 인식 없음 → 한 마리씩");
        nameOcr = v;
    }
    private static string LangCode(int l) => l switch { 1 => "JPN", 2 => "ENG", 3 => "FRE", 4 => "ITA", 5 => "GER", 7 => "SPA", 8 => "KOR", 9 => "CHS", 10 => "CHT", _ => "ENG" };

    /// <summary>게임 박스에 들어갈 다음 묶음: 대기·생성됨 신청을 대기열 순서로, 게임 박스 빈자리만큼</summary>
    private List<Order> NextWindow(string g)
    {
        int room = BatchMax - OrdG(g, OrderStatus.OnSwitch).Count;
        return room <= 0 ? [] : OrdG(g, OrderStatus.Queued, OrderStatus.Generated).Take(room).ToList();
    }

    /// <summary>디스코드 공개 카드 (신청마다 한 번만, 기다리지 않음)</summary>
    private void PostCard(Order o, PKM pk)
    {
        if (o == null || pk == null || db.Get($"card:{o.Id}") == "1") return;
        db.Set($"card:{o.Id}", "1");
        _ = Task.Run(async () => { try { await OnCard(o, pk); } catch (Exception ex) { log($"#{o.Id} 카드 실패: {ex.Message}"); } });
    }

    private async Task GenerateNext(CancellationToken ct)
    {
        // 대기열 순서대로 다음 묶음을 정함 (파일 신청은 생성이 필요 없을 뿐, 순서는 똑같이 기다림)
        // 묶음 안에서 아직 안 만든 것 중 맨 앞 하나를 생성
        // 다음 두 묶음까지 미리 만듦 (스위치 줄과 따로 돌아서, 스위치가 HOME 이동 중이어도 생성이 멈추지 않음)
        // 게임마다 다음 두 묶음 안에서 아직 안 만든 맨 앞 신청 → 그중 가장 먼저 들어온 것
        var o = Served.Select(g => OrdG(g, OrderStatus.Queued, OrderStatus.Generated).Take(BatchMax * 2).FirstOrDefault(x => x.Status == OrderStatus.Queued))
            .Where(x => x != null).OrderBy(x => x.Id).FirstOrDefault();
        if (o == null) { await Task.Delay(1000, ct); return; }
        var og = string.IsNullOrEmpty(o.Game) ? Game : Norm(o.Game);
        GenService gen;
        try { gen = GenOf(og); }
        catch (Exception ex) { if (!Fail(o, "생성 엔진 준비 실패")) return; log($"#{o.Id} {GameKo(og)} 생성 엔진 준비 실패: {ex.Message}"); await notify(o.DiscordId, $"#{o.Id} {o.Describe}를 만들지 못했습니다: 봇의 {GameKo(og)} 준비에 문제가 있어요. 운영자에게 알려 주세요."); return; }
        PKM pk; string msg;
        if (o.Kind == "file")
        {
            // 미리 신청한 파일: 받은 그대로 둔 것을 지금 게임 형식으로 바꿔 확인
            var raw = o.PoolId is { } rid ? db.GetPool(rid) : null;
            try { (pk, msg) = raw == null ? (null, "올린 파일을 찾지 못했습니다") : gen.FromFile(raw.PkBytes, o.Text); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { (pk, msg) = (null, "변환 오류: " + ex.Message); }
            if (pk != null && BdspBlock(og, pk, o.Id) is { } bb1) { pk = null; msg = bb1; }
            if (pk == null) { if (Fail(o, msg)) await notify(o.DiscordId, $"#{o.Id} {o.Describe}를 {GameKo(og)}에 넣을 수 없습니다: {msg}"); return; }
            if (raw != null) db.Reserve(raw.Id, null);
            var fp = db.AddPool(GenService.Bytes(pk), pk.Extension, pk.Species, pk.Form, pk.IsShiny, $"file:{o.Id}");
            db.Reserve(fp, o.Id); db.SetOrderPool(o.Id, fp);
            if (!db.SetStatusIf(o.Id, OrderStatus.Queued, OrderStatus.Generated, note: msg)) { db.Reserve(fp, null); db.SetLocation(fp, Location.Gone, -1, -1); log($"#{o.Id} 바꾸는 사이 취소됨"); return; }
            log($"#{o.Id} 미리 신청 파일 → {GameKo(og)} 형식: {o.Describe}");
            return;
        }
        // 생성 엔진이 예외를 내도(해석 오류·시간 초과 등) 이 신청만 실패로 끝냄 — 안 잡으면 같은 신청을 1초마다 다시 집어 모든 생성이 멈춤
        try
        {
            if (o.Kind == "showdown")
            {
                var set = gen.ParseShowdown(o.Text, out var prob);
                (pk, msg) = set == null ? (null, prob) : await gen.MakeFromSetAsync(set, ct, o.Text);
            }
            else
            {
                var specs = gen.Parse(o.Text);
                (pk, msg) = specs.Count == 0 ? (null, "해석 실패") : await gen.MakeAsync(specs[0], ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            (pk, msg) = (null, ex is OperationCanceledException ? "생성 시간이 너무 오래 걸림" : "생성 오류: " + ex.Message);
            log($"#{o.Id} 생성 중 예외: {ex.GetType().Name}: {ex.Message}");
        }
        if (db.GetOrder(o.Id).Status == OrderStatus.Cancelled) return;
        if (pk != null && BdspBlock(og, pk, o.Id) is { } bb2) { pk = null; msg = bb2; }
        if (pk == null) { if (Fail(o, msg)) await notify(o.DiscordId, $"#{o.Id} {o.Describe}를 만들지 못했습니다: {msg}"); return; }
        var pid = db.AddPool(GenService.Bytes(pk), pk.Extension, pk.Species, pk.Form, pk.IsShiny, o.Key);
        db.Reserve(pid, o.Id); db.SetOrderPool(o.Id, pid);
        if (!db.SetStatusIf(o.Id, OrderStatus.Queued, OrderStatus.Generated, note: msg)) { db.Reserve(pid, null); db.SetLocation(pid, Location.Gone, -1, -1); log($"#{o.Id} 만드는 사이 취소됨"); return; }
        log($"#{o.Id} 생성" + (og != Game ? $"({GameKo(og)})" : "") + $": {o.Describe} ({msg})");
        PostCard(db.GetOrder(o.Id), pk);
    }

    /// <summary>휴대폰 HOME 요약(Summary) 화면과 맞춰 볼 개체 정보 (레벨·성별·능력치·기술·어버이·ID)</summary>
    private string Fingerprint(PKM pk)
    {
        try
        {
            var c = pk.Clone(); c.ResetPartyStats();
            var stats = new[] { c.Stat_HPMax, c.Stat_ATK, c.Stat_DEF, c.Stat_SPA, c.Stat_SPD, c.Stat_SPE }.Select(v => (int)v).ToArray();
            var ml = gen.En.Move; int mc = ml.Count();
            var moves = new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 }.Where(m => m > 0 && m < mc).Select(m => ml[m]).ToArray();
            return System.Text.Json.JsonSerializer.Serialize(new { lv = (int)pk.CurrentLevel, gender = (int)pk.Gender, stats, moves, ot = pk.OriginalTrainerName, tid = pk.DisplayTID.ToString("D6") });
        }
        catch (Exception ex) { log("요약 비교 정보 만들기 실패: " + ex.Message); return ""; }
    }

    /// <summary>같은 묶음에 넣으면 휴대폰에서 이름으로 구분이 안 되는 둘인지 (같은 종·폼, 또는 보이는 이름이 같거나 아주 비슷)</summary>
    private bool SameLook(PKM a, PKM b)
    {
        if (a.Species == b.Species && a.Form == b.Form) return true;
        string na = ShownName(a), nb = ShownName(b);
        if (na == nb) return true;
        int d = Lev(na, nb); return d * 4 <= Math.Max(na.Length, nb.Length);   // 25% 이내 차이면 비슷한 이름
    }
    /// <summary>휴대폰 HOME 화면에 보이는 이름 (닉네임이면 닉네임, 아니면 그 언어의 종 이름)</summary>
    private static string ShownName(PKM p) => (p.Nickname ?? "").Trim();
    private static int Lev(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i; for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++) for (int j = 1; j <= b.Length; j++) d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>게임 이동용 박스 1번 칸이 비면(이전 신청이 HOME으로 올라갔으면) 다음 개체를 넣음</summary>
    private async Task InjectNext(string g, CancellationToken ct)
    {
        // 대기열 맨 앞부터 이어진 '생성 끝난' 신청만 넣음 (앞 신청이 아직 생성 전이면 뒤 파일 신청이 새치기하지 않게)
        var gens = NextWindow(g).TakeWhile(o => o.Status == OrderStatus.Generated).Where(o => o.PoolId != null).ToList();
        if (gens.Count == 0) return;
        var onSwitch = OrdG(g, OrderStatus.OnSwitch).Where(o => o.PoolId != null).Select(o => db.GetPool(o.PoolId.Value)).Where(p => p != null).ToList();
        if (onSwitch.Count >= BatchMax) return;
        if (!await EnsureSwitch(ct)) return;
        int box = cfg.Switch.TransferBox - 1;
        // 지금 게임 박스에 있는 봇 개체는 그대로 두고, 빈 칸에 새 개체를 채움
        var slots = new PKM[30]; var used = new HashSet<int>();
        foreach (var p in onSwitch) if (p.Box == box && p.Slot is >= 0 and < 30) { slots[p.Slot] = gen.FromBytes(p.PkBytes, p.Format); used.Add(p.Slot); }
        var placed = new List<(Order o, PoolItem item, int slot)>();
        foreach (var o in gens)
        {
            if (used.Count + placed.Count >= BatchMax) break;
            var item = db.GetPool(o.PoolId.Value);
            if (item == null) { Fail(o, "배정된 개체가 없습니다"); continue; }
            // 휴대폰에서 이름으로 골라내므로, 같은 종(이름이 같거나 비슷한)은 한 묶음에 함께 넣지 않음 → 이번 묶음은 여기서 마감
            var mk = gen.FromBytes(item.PkBytes, item.Format);
            // 브다샤펄 전설·환상: 생성한 뒤에 같은 조합이 HOME으로 올라갔을 수 있음 (11-01 18:01 #8787 이로치 칠색조 — 앞 신청 #8783이 옮기는 사이 취소돼 대기열 확인을 빠져나감)
            // → HOME이 이 개체를 막아 범위 이동 전체가 실패하므로 넣기 전에 기록을 다시 확인
            if (BdspSpecialKey(g, mk) is { } bkey && BdspSent().Contains(bkey))
            {
                db.Reserve(item.Id, null); db.SetLocation(item.Id, Location.Gone, -1, -1);
                var why = BdspBlock(g, mk, o.Id) ?? "HOME이 받지 않는 개체예요";
                if (Fail(o, why)) await notify(o.DiscordId, $"#{o.Id} {o.Describe}를 {GameKo(g)}에 넣을 수 없습니다: {why}");
                continue;
            }
            // 같은 종이 겹쳐도 묶음에 함께 넣음: 대기열 순서 = 칸 순서이고, 휴대폰에서 같은 이름 중 몇 번째인지로 고름 (SearchOnce)
            int first = HomeBot.Core.Workers.SwitchWorker.FirstSlotOf(cfg, g);   // 레츠고: 맨 윗줄(파티)은 건너뜀
            int slot = Enumerable.Range(first, 30 - first).FirstOrDefault(i => !used.Contains(i) && placed.All(x => x.slot != i), -1);
            if (slot < 0) break;
            slots[slot] = gen.FromBytes(item.PkBytes, item.Format); placed.Add((o, item, slot));
        }
        if (placed.Count == 0) return;
        SwitchState = $"{GameKo(g)}에 {placed.Count}마리 넣는 중";
        var r = await sw.InjectBoxAsync(box, slots, ct);
        // 같은 게임에 넣기가 계속 실패하면(세이브 깨짐·FTP 끊김·세이브 잠김 등) 일시정지는 안 되지만 처리가 멈춘 것과 같음 → 5분 넘으면 운영자에게 한 번 알림 (11-01 01:42: 23분 동안 아무 알림 없이 실패만 반복)
        await Watch("inject:" + g, r.Ok, $"{GameKo(g)}에 포켓몬 넣기가 계속 실패하고 있어요 (스위치 세이브·FTP 확인 필요): {r.Message}");
        if (!r.Ok)
        {
            LastError = "주입 실패: " + r.Message; log(LastError);
            // 세이브 모드: 세이브가 잠겨 있음(busy) = 스위치에 HOME이나 게임이 켜져 있음 → 저장 없이 닫음 (10-29 02:14~07:25 사례: HOME이 열린 채 5시간 멈춤)
            if (r.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - lastBusyClose > TimeSpan.FromMinutes(1))
            {
                lastBusyClose = DateTime.UtcNow;
                log("세이브가 잠겨 있음 (스위치에 HOME 또는 게임이 켜져 있음) → 저장하지 않고 닫음");
                try { var d = await sw.RunMacroAsync("home_discard", new Dictionary<string, string>(), ct); if (!d.Ok) log("닫기 확인 실패: " + d.Message); } catch (Exception dex) when (dex is not OperationCanceledException) { log("닫기 오류: " + dex.Message); }
            }
            // 연결이 끊긴 경우(일시정지 등으로 오래 쉬면 스위치가 끊음): 연결을 버려 다음 차례에 다시 연결. 1초마다 같은 실패를 되풀이하지 않게 잠깐 쉼
            sw.Disconnect();
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
            return;
        }
        // 전에 이 박스에 있던 다른 봇 개체(지난 교환에서 내려온 것 등)는 덮어써졌으므로 정리
        var keep = onSwitch.Select(p => p.Id).Concat(placed.Select(x => x.item.Id)).ToHashSet();
        foreach (var p in db.Pool(Location.Switch).Where(p => p.Box == box && !keep.Contains(p.Id)))
        {
            // 다른 게임 세이브에 있는 개체(동시 운영)는 건드리지 않음
            if (p.ReservedBy is { } rb && db.GetOrder(rb) is { } ro && !IsOf(ro, g)) continue;
            db.SetLocation(p.Id, Location.Gone, -1, -1);
        }
        foreach (var (o, item, slot) in placed)
        {
            db.SetLocation(item.Id, Location.Switch, box, slot);
            if (!db.SetStatusIf(o.Id, OrderStatus.Generated, OrderStatus.OnSwitch)) { log($"#{o.Id} 넣는 사이 취소됨 → 그대로 둠 (다음 주입 때 덮어씀)"); db.SetLocation(item.Id, Location.Gone, -1, -1); continue; }
            log($"#{o.Id} {GameKo(g)} 박스 {box + 1}-{slot + 1}에 넣음");
            if (o.Kind == "file") PostCard(db.GetOrder(o.Id), gen.FromBytes(item.PkBytes, item.Format));
        }
        SwitchState = $"{GameKo(g)}에 {used.Count + placed.Count}마리 · HOME 차례 기다림";
    }

    /// <summary>앞 묶음의 GTS 교환이 모두 끝났으면, 게임 박스에 넣어 둔 것들을 한꺼번에 스위치 HOME으로 올림</summary>
    private async Task MoveToHome(string g, CancellationToken ct)
    {
        var batch = OrdG(g, OrderStatus.OnSwitch).Where(o => o.PoolId != null).ToList();
        if (batch.Count == 0) return;
        // 채울 계정: 이 게임을 맡은 계정 중 HOME 칸이 빈(교환 중인 신청이 없는) 계정. 여럿이면 지난번 다음 계정
        var mine = AcctsOf(g);
        if (mine.Count == 0 && SideClosing(g)) mine = AcctsOf(Game);   // 마감 중인 게임의 남은 신청은 기본 게임 계정으로
        if (mine.Count == 0) return;
        int last = int.TryParse(db.Get("last_acct"), out var la) ? la : 2;
        var free = mine.Where(a => HomeOf(a).Count == 0).ToList();
        int acct;
        if (free.Count > 0) acct = free.Contains(last % Accounts + 1) ? last % Accounts + 1 : free[0];
        else
        {
            // 두 계정 모두 교환 중: 한쪽에 몇 마리(3마리 이하)만 남아 있으면 그 계정에 바로 다음 묶음을 채움 — 남은 개체는 그대로 두고 빈 줄에 놓음
            // (늦게 맡기는 몇 명 때문에 휴대폰 한 줄이 통째로 쉬지 않게)
            var cand = mine.Select(a => (a, n: HomeOf(a).Count)).Where(x => x.n <= 3 && (DateTime.UtcNow >= acctHoldUntil.GetValueOrDefault(x.a) || x.n < acctHoldCount.GetValueOrDefault(x.a, 99))).OrderBy(x => x.n).ToList();
            if (cand.Count == 0) return;
            acct = cand[0].a;
        }
        // 더 올 신청이 남아 있고 묶음이 아직 안 찼으면 조금 기다림 (다 채워서 한 번에 가는 게 빠름). 기다려도 3초마다 다시 봄
        bool moreComing = OrdG(g, OrderStatus.Queued, OrderStatus.Generated).Count > 0 && batch.Count < BatchMax;
        if (moreComing) { SwitchState = $"{GameKo(g)}에 {batch.Count}마리 · 다음 신청 생성 기다림"; return; }
        if (!await acctLock[acct].WaitAsync(0, ct)) return;
        bool launched = false;
        try
        {
            if (!await EnsureSwitch(ct)) return;
            // 옮기기 직전 확인: 게임 메모리의 박스에 넣어 둔 개체가 그대로 있는지 (일시정지·게임 재시작 등으로 사라지면 HOME에 빈 박스만 열림, 00:34 사례)
            try
            {
                int tb = cfg.Switch.TransferBox - 1;
                var nowBox = await sw.ReadBoxAsync(tb, ct);
                var want = new PKM[30]; int lost = 0, stray = 0; var batchSlots = new HashSet<int>();
                for (int i = 0; i < 30; i++) want[i] = i < nowBox.Count ? nowBox[i] : null;
                foreach (var o in batch)
                {
                    var it = db.GetPool(o.PoolId.Value);
                    if (it == null || it.Box != tb || it.Slot is < 0 or >= 30) continue;
                    batchSlots.Add(it.Slot);
                    var pk = gen.FromBytes(it.PkBytes, it.Format);
                    if (want[it.Slot] == null || !Same(want[it.Slot], pk)) { want[it.Slot] = pk; lost++; }
                }
                // 이번에 옮길 개체가 아닌데 박스에 남은 것(취소된 신청 등)은 비움: 범위 이동 네모 안에 들어가 같이 HOME으로 올라가면
                // HOME이 거부하는 개체(가짜 트래커 등)가 묶음 전체 저장을 막음 (10-31 17:07·17:29·17:33 #6910)
                // 봇이 이 박스에 넣어 둔 기록이 있는 칸(이번 묶음에 안 든 개체 포함)은 건드리지 않음 → 취소 등으로 기록이 없어진 칸만 비움
                {
                    foreach (var p in db.Pool(Location.Switch))
                        if (p.Box == tb && p.Slot is >= 0 and < 30 && p.ReservedBy is { } rb && db.GetOrder(rb) is { } ro && Active.Contains(ro.Status)) batchSlots.Add(p.Slot);
                    for (int i = 0; i < 30; i++) if (!batchSlots.Contains(i) && want[i] is { Species: > 0 }) { want[i] = null; stray++; }
                }
                if (lost > 0 || stray > 0)
                {
                    log($"게임 박스 맞추기: 사라진 {lost}마리 다시 넣고 · 옮기지 않을 {stray}마리 비움 → 옮김");
                    var ri = await sw.InjectBoxAsync(tb, want, ct);
                    if (!ri.Ok)
                    {
                        LastError = "다시 넣기 실패: " + ri.Message; log(LastError);
                        await Watch("premove:" + g, false, $"{GameKo(g)} HOME으로 옮기기 전 게임 박스 맞추기가 계속 실패하고 있어요: {ri.Message}");
                        sw.Disconnect(); try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
                        return;
                    }
                }
                await Watch("premove:" + g, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 박스를 못 읽었는데 그대로 옮기면 남은 개체(가짜 트래커 등) 정리가 빠져 HOME 저장이 거부될 수 있음 (#6910) → 다음 차례에 다시 읽음
                LastError = "옮기기 전 게임 박스 확인 실패: " + ex.Message; log(LastError + " → 다음 차례에 다시");
                await Watch("premove:" + g, false, $"{GameKo(g)} HOME으로 옮기기 전 게임 박스를 계속 읽지 못하고 있어요: {ex.Message}");
                sw.Disconnect(); try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { }
                return;
            }
            SwitchState = $"{batch.Count}마리 스위치 HOME으로 옮기는 중" + (Accounts > 1 ? $" (계정 {acct})" : "");
            // 같은 계정이 휴대폰에서 켜져 있으면 안 됨 → 닫기 (기다리지 않음: 게임 저장하는 몇 초 사이에 끝남)
            if (phone != null)
            {
                // 같은 계정을 휴대폰이 쓰고 있으면 스위치 HOME이 막힘 → 닫힐 때까지 기다림 (다른 계정 교환 중이면 그게 끝난 뒤 닫힘)
                try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(150)); var c = await phone.HomeCloseAsync(cts.Token, acct); if (!c.Ok) log("휴대폰 HOME 닫기: " + c.Message); }
                catch (Exception ex) { log("휴대폰 HOME 닫기 확인 못 함: " + ex.Message); }
                freshOf[acct] = true;
            }
            sw.HomeUserOverride = UserOf(acct);
            // 이 계정에 아직 교환 안 끝난 봇 개체 칸 → 놓아주지 않고 피해서 옮김
            var keepItems = HomeOf(acct).Where(o => o.PoolId != null).Select(o => db.GetPool(o.PoolId.Value)).Where(p => p != null && p.Location == Location.Home).ToList();
            sw.KeepHomeCells = keepItems.Select(p => p.Slot).Where(x => x is >= 0 and < 30).ToHashSet();
            sw.KeepPks = keepItems.Select(p => gen.FromBytes(p.PkBytes, p.Format)).ToList();
            if (keepItems.Count > 0) log($"계정 {acct}: 남은 {keepItems.Count}마리({string.Join(",", sw.KeepHomeCells.Select(x => x + 1))}번 칸)는 두고 빈 줄에 채움");
            var items = batch.Select(o => (o, item: db.GetPool(o.PoolId.Value))).Where(x => x.item != null && x.item.Box == cfg.Switch.TransferBox - 1 && x.item.Slot >= 0).OrderBy(x => x.item.Slot).ToList();
            if (items.Count == 0) { foreach (var o in batch) Fail(o, "게임 칸 정보가 없습니다"); return; }
            var r = await sw.MoveBoxToHomeAsync(cfg.Switch.TransferBox, 1, ct, items.Select(x => x.item.Slot).ToList());
            if (!r.Ok && r.Message.Contains("옮길 자리가 없음"))
            {
                // 남은 개체가 흩어져 있어 10마리 네모를 놓을 빈 줄이 없음 → 이 계정은 남은 교환이 끝날 때까지 잠시 미룸 (멈추지 않음)
                log($"계정 {acct}: 남은 개체 때문에 빈 줄이 없어 이번엔 건너뜀 (남은 교환이 끝나면 채움)");
                acctHoldUntil[acct] = DateTime.MaxValue; acctHoldCount[acct] = HomeOf(acct).Count;   // 남은 수가 줄 때까지 다시 열지 않음 (열 때마다 휴대폰 같은 계정이 끊김)
                // 다시 연결해도 HOME이 닫히지 않음 → 직접 저장 없이 닫음 (안 닫으면 세이브가 잠기고 같은 계정 휴대폰 GTS도 막힘)
                try { var d = await sw.RunMacroAsync("home_discard", new Dictionary<string, string>(), ct); if (!d.Ok) log("HOME 닫기 확인 실패: " + d.Message); } catch (Exception dex) when (dex is not OperationCanceledException) { log("HOME 닫기 실패: " + dex.Message); }
                return;
            }
            if (!r.Ok && r.Message.StartsWith("[열기]"))
            {
                // HOME을 여는 단계(로그인·연동)에서 실패: 아직 아무것도 안 옮겼으니 그 게임으로 돌아가 한 번 더
                log($"HOME 열기 실패({r.Message}) → {GameKo(g)}(으)로 돌아가 한 번 더 시도");
                SwitchState = "HOME 열기 실패 · 다시 시도";
                { try { await sw.RunMacroAsync("home_discard", new Dictionary<string, string>(), ct); } catch (Exception dex) when (dex is not OperationCanceledException) { log("HOME 닫기 오류: " + dex.Message); } }
                sw.Disconnect();
                if (!await EnsureSwitch(ct)) return;   // HOME이 켜져 있으면 저장 없이 닫고 게임 필드까지 복귀
                r = await sw.MoveBoxToHomeAsync(cfg.Switch.TransferBox, 1, ct, items.Select(x => x.item.Slot).ToList());
            }
            if (!r.Ok)
            {
                // HOME 저장 전 실패(화면 한 번 잘못 읽음 등): 저장 없이 닫으면 HOME·게임 모두 옮기기 전 그대로(놓아준 것도 되돌아감) → 다음 차례에 다시
                await DiscardHome(ct);
                SwitchState = "HOME 이동 실패 · 다시 시도";
                await SoftFail("HOME 이동 실패: " + r.Message);
                return;
            }
            List<HomeSlot> read;
            try { read = await sw.ReadHomeBoxesAsync(1, 1, ct); } catch (Exception ex) { log("HOME 박스 읽기 실패: " + ex.Message); read = []; }
            log($"HOME 박스 1 읽기: {read.Count}마리" + string.Concat(read.Select(h => $" [{h.Slot + 1}:{h.Pk.Species}]")));
            // HOME 저장 → HOME 버튼으로 스위치 메뉴 → (여기서 휴대폰 HOME을 미리 켬) → HOME 앱 닫기 (다음 세이브 쓰기 준비)
            var saved = await sw.RunMacroAsync("home_save", new Dictionary<string, string>(), ct);
            if (!saved.Ok)
            {
                // 저장 중 HOME 서버 오류(에러 코드 2-ALZTA-0005 등 · '타이틀 화면으로 돌아갑니다'): 저장이 안 됐으니 HOME·게임 모두 옮기기 전 그대로
                // (17:07 확인: 옮기던 10마리가 게임 박스에 그대로 있었음) → OK 누르고 HOME을 닫은 뒤 다음 차례에 같은 개체를 다시 옮김.
                // 30분 안에 3번 넘게 나면 서버 쪽 문제가 이어지는 것이라 일시정지
                bool homeErr = false;
                try { homeErr = (await sw.RunMacroAsync("home_error_ok", new Dictionary<string, string>(), ct)).Ok; } catch (Exception ex) when (ex is not OperationCanceledException) { log("HOME 오류 창 확인 실패: " + ex.Message); }
                saveErrAt.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(30));
                // 같은 묶음이 연달아 서버 오류(에러 10015 = HOME 트래커가 유효하지 않음)면 HOME 트래커가 있는 파일 개체가 원인일 가능성이 큼
                // → 그 개체(최대 3마리)만 빼고 나머지를 다시 옮김 (11-01 14:20~14:31: 누리레느 파일의 가짜 트래커 하나 때문에 12마리가 4번 실패)
                if (homeErr && saveErrAt.Count >= 1)
                {
                    var bad = items.Where(x => gen.FromBytes(x.item.PkBytes, x.item.Format) is IHomeTrack { Tracker: not 0 }).ToList();
                    if (bad.Count > 0 && bad.Count <= 3 && bad.Count < items.Count)
                    {
                        foreach (var (bo, bi) in bad)
                        {
                            db.Reserve(bi.Id, null);
                            if (!Fail(bo, "HOME이 트래커를 받아들이지 않음 (에러 10015)")) continue;
                            await notify(bo.DiscordId, $"❌ #{bo.Id} {bo.Describe} · 올려 주신 파일의 HOME 트래커를 Pokémon HOME 서버가 받아들이지 않아(에러 10015) 교환할 수 없어 신청을 취소했습니다. HOME에 한 번도 들어간 적 없는 포켓몬이라면 트래커를 지운 파일로 다시 신청해 주세요.");
                        }
                        log($"HOME 저장 서버 오류가 연달아 남 → HOME 트래커가 있는 {bad.Count}마리({string.Join(",", bad.Select(x => "#" + x.o.Id))})를 빼고 다시 옮김");
                        await AdminAlert($"HOME 저장 오류(10015 추정)로 트래커가 있는 파일 신청 {string.Join(",", bad.Select(x => "#" + x.o.Id))}를 실패 처리하고 나머지를 다시 옮겨요");
                        saveErrAt.Clear();
                    }
                }
                if (homeErr && saveErrAt.Count < 3)
                {
                    saveErrAt.Add(DateTime.UtcNow);
                    log($"HOME 저장 중 서버 오류 창 → OK · 저장되지 않았으니 HOME을 닫고 다음 차례에 다시 옮김 ({saveErrAt.Count}/3, 30분 기준)");
                    SwitchState = "HOME 서버 오류 · 다시 시도";
                    await DiscardHome(ct);   // HOME을 닫은 상태로 다음 차례 (위 '옮길 자리가 없음'과 같게)
                    return;
                }
                LastError = "HOME 저장 실패: " + saved.Message + (homeErr ? " (HOME 서버 오류가 30분 안에 여러 번)" : ""); Paused = true; await AdminAlert(LastError); return;
            }
            // HOME 저장이 끝난 순간 개체는 HOME에 있음 → 기록을 바로 고침. 게임 실행(아래)이 실패해 멈췄다 재개해도 '게임 박스에 없음'으로 보고
            // 같은 개체를 다시 넣어 HOME에 두 번 올리는 일이 없게 (전에는 게임 실행이 끝난 뒤에야 기록을 고쳤음)
            var keepIds = keepItems.Select(p => p.Id).ToHashSet();
            // 놓아준 개체는 기록에서 지움. 단, 남은 칸을 확인 못 해 찬 칸을 하나도 놓아주지 않았으면(KeepUnverified) 그대로 HOME에 있음 → 지우지 않음
            //  (지우면 같은 이름 개체가 HOME에 둘인데 하나로 알고 '어느 것인지 알 수 없음'이 됨: #763 사례) — 다음 이동 때 확인되면 놓아줌
            if (!sw.KeepUnverified)
                foreach (var p in db.Pool(Location.Home).Where(p => PoolAcct(p.Id) == acct && !keepIds.Contains(p.Id) && items.All(x => x.item.Id != p.Id))) db.SetLocation(p.Id, Location.Gone, -1, -1);
            var placedSlots = sw.LastHomeSlots;
            // 남은 개체의 실제 칸을 HOME 데이터로 찾았으면 기록을 고침 (다음 묶음·같은 이름 순서에 씀)
            if (!sw.KeepAmbiguous) foreach (var (i, cell) in sw.KeepFound) if (i < keepItems.Count && keepItems[i].Slot != cell) { log($"남은 개체 칸 고침: 풀 {keepItems[i].Id} {keepItems[i].Slot + 1} → {cell + 1}"); db.SetLocation(keepItems[i].Id, Location.Home, 1, cell); }
            // 게임 k번 칸 → HOME k번째 빈 칸(0부터 차례) 로 옮겼음
            for (int k = 0; k < items.Count; k++)
            {
                var (o, item) = items[k];
                var pk = gen.FromBytes(item.PkBytes, item.Format);
                var hit = read.FirstOrDefault(h => Same(h.Pk, pk)) ?? sw.LastAltRead.FirstOrDefault(h => Same(h.Pk, pk));
                int slot = hit?.Slot ?? (k < placedSlots.Count ? placedSlots[k] : k);
                db.SetLocation(item.Id, Location.Home, 1, slot, verified: hit != null);
                if (BdspSpecialKey(g, pk) is { } bk) { BdspMarkSent(bk); log($"#{o.Id} 브다샤펄 전설·환상 기록: {gen.Strings.Species[pk.Species]}{(pk.IsShiny ? " 이로치" : "")} (이 세이브에서 다시 보낼 수 없음)"); }
                db.Set($"pacct:{item.Id}", acct.ToString()); db.Set($"acct:{o.Id}", acct.ToString());
                if (!db.SetStatusIf(o.Id, OrderStatus.OnSwitch, OrderStatus.InHome)) { log($"#{o.Id} 옮기는 사이 취소됨 → HOME에 둠 (다음 이동 때 놓아줌)"); continue; }
                db.SetClaimed(o.Id, DateTime.UtcNow);
                log($"#{o.Id} HOME 1-{slot + 1}에 올라감");
                // '교환 차례' DM은 휴대폰이 실제로 이 신청을 처음 검색할 때 보냄 (SearchOnce) — 묶음 뒤쪽 사람이 DM 받고 한참 기다리지 않게
            }
            db.Set("batch_size", items.Count.ToString()); db.Set($"batch_size:{acct}", items.Count.ToString()); db.Set("last_acct", acct.ToString());
            freshOf[acct] = true;
            log($"{items.Count}마리 HOME 이동 완료" + (Accounts > 1 ? $" (계정 {acct} · 휴대폰 #N{acct})" : "") + " → 휴대폰 차례");
            _ = Task.Run(() => NotifyNextAsync(g));
            if (phone != null)
            {
                // 저장이 끝났으니 휴대폰 HOME을 지금 켜 둠 (그동안 휴대폰도 로그인·준비) — 기다리지 않음
                _ = Task.Run(async () => { try { var l = await phone.HomeLaunchAsync(ct, acct); if (l.Ok) prelaunchOf[acct] = DateTime.UtcNow; else log("휴대폰 HOME 미리 켜기: " + l.Message); } catch { } });
            }
            var back = await sw.RunMacroAsync("home_close", new Dictionary<string, string>(), ct);
            if (!back.Ok)
            {
                // 이미 저장·기록이 끝났으니 다시 옮길 것은 없음 → 연결을 버리고 다음 차례에 다시 (HOME이 남아 있으면 세이브 잠김으로 보고 저장 없이 닫음)
                sw.Disconnect();
                await SoftFail($"{GameKo(g)} 복귀(HOME 닫기) 실패: " + back.Message);
                return;
            }
            launched = true;
        }
        finally { sw.HomeUserOverride = null; sw.KeepHomeCells = new(); sw.KeepPks = new(); acctLock[acct].Release(); }
        if (launched)
        {
            try { await sw.ConnectAsync(ct); } catch (Exception ex) { LastError = "HOME을 닫은 뒤 재연결 실패: " + ex.Message; }
            SwitchState = "다음 신청 준비";
        }
    }

    private DateTime lastBusyClose = DateTime.MinValue;
    private async Task<bool> EnsureSwitch(CancellationToken ct)
    {
        if (sw.Connected) return true;
        try { await sw.ConnectAsync(ct); await Watch("switch-connect", true); return true; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = "스위치 연결 실패: " + ex.Message; SwitchState = "스위치 연결 안 됨";
            await Watch("switch-connect", false, "스위치에 연결하지 못하고 있어요 (와이파이·sys-botbase 확인 필요): " + ex.Message);
            return false;
        }
    }

    // ---------- 휴대폰 줄: HOME에 올라간 신청들을 돌아가며 한 번씩 검색 (먼저 맡긴 사람부터 교환됨)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (bool toldNone, bool toldMany, int errors, string shot)> ph = new();
    /// <summary>못 찾음 안내 DM 뒤 연속으로 못 찾은 횟수 / 다시 맡기기·취소 안내를 보낸 신청</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> noneSinceDm = new();
    private readonly HashSet<long> toldRetry = new();
    private bool IsToldRetry(long id) { lock (toldRetry) return toldRetry.Contains(id); }
    // 아직 안 맡긴(검색 결과 없음) 신청은 다음 검색까지 점점 길게 쉼: 20초 → 40 → 80 → 120 → 180초. 그동안 맡긴 사람을 먼저 교환
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (int n, DateTime next)> noneWait = new();
    private static readonly int[] NoneBackoff = [20, 40, 80, 120, 180];

    private async Task PhoneTick(int lane, CancellationToken ct)
    {
        if (phone == null) return;
        // 동시 모드: 줄 1 = 계정 1, 줄 2 = 계정 2. 아니면 줄 1이 두 계정을 번갈아(맡긴 순서대로) 처리하고 줄 2는 쉼
        bool par = PhoneParallel;
        if ((lane > 1 && !par) || lane > Accounts) { await Task.Delay(1000, ct); return; }
        var list = db.Orders(OrderStatus.InHome).Where(o => o.PoolId != null && (!par || AcctOf(o.Id) == lane)).OrderBy(o => o.ClaimedAt).ToList();
        if (!par && Accounts > 1)
        {
            // 번갈아 모드: 두 계정 신청을 한 건씩 엇갈려 (한 계정이 다 끝난 뒤 다른 계정이 시작되지 않게)
            var per = Enumerable.Range(1, Accounts).Select(a => list.Where(o => AcctOf(o.Id) == a).ToList()).ToList();
            var mix = new List<Order>();
            for (int i = 0; i < per.Max(l => l.Count); i++) foreach (var l in per) if (i < l.Count) mix.Add(l[i]);
            list = mix;
        }
        if (list.Count == 0) { PhoneState = "대기"; return; }
        // 차례 알림을 미리: 이 줄에서 '차례' DM을 받은 신청이 늘 3건이 되게 다음 사람들에게 먼저 보냄 (HOME에 올라간 순서).
        // DM 뒤 첫 검색은 잠깐 기다림 → 신청자가 GTS에 맡길 시간 (전에는 첫 검색과 동시에 DM이 가서 찾는 사이 시간이 흐름)
        int told = list.Count(o => db.Get($"turn_dm:{o.Id}") == "1");
        foreach (var o in list.Where(o => db.Get($"turn_dm:{o.Id}") != "1").Take(Math.Max(0, 3 - told)).ToList()) TurnNotify(o);
        list = list.Where(o => db.Get($"turn_dm:{o.Id}") == "1").ToList();
        if (list.Count == 0) { PhoneState = "대기"; return; }
        // 두 계정의 신청을 함께 돌아가며 검색 (먼저 맡긴 사람부터). 계정마다 잠금: 스위치가 그 계정을 채우는 중이면 그 계정 신청만 건너뜀
        // 쉴 차례가 아닌 신청만 (시간이 다 된 것은 끝내야 하니 포함). 전부 쉬는 중이면 가장 먼저 깰 하나만 검색 (줄이 놀지 않게)
        var now0 = DateTime.UtcNow;
        var due = list.Where(o => !noneWait.TryGetValue(o.Id, out var w) || w.next <= now0 || Expired(o)).ToList();
        if (due.Count == 0)
        {
            var soon = list.OrderBy(o => noneWait.TryGetValue(o.Id, out var w) ? w.next : now0).First();
            var wait = (noneWait.TryGetValue(soon.Id, out var ws) ? ws.next : now0) - now0;
            // 차례 DM 직후의 첫 대기(n==0)는 맡길 시간을 주려는 것뿐 → 다른 할 일이 없으면 기다리지 않고 바로 찾음 (줄이 놀지 않게)
            bool firstWait = noneWait.TryGetValue(soon.Id, out var fw) && fw.n == 0;
            if (!firstWait && wait > TimeSpan.FromSeconds(1)) { await Task.Delay(wait < TimeSpan.FromSeconds(10) ? wait : TimeSpan.FromSeconds(10), ct); return; }
            due = [soon];
        }
        list = due;
        foreach (var o in list)
        {
            ct.ThrowIfCancellationRequested();
            var cur = db.GetOrder(o.Id);
            if (cur == null || cur.Status != OrderStatus.InHome) continue;
            if (Paused) break;   // 앞 신청에서 고장으로 멈췄으면 나머지도 바로 멈춤 (같은 오류로 시간만 씀)
            int a = AcctOf(o.Id);
            if (!await acctLock[a].WaitAsync(0, ct)) continue;
            try { await SearchOnce(cur, ct); }
            finally { acctLock[a].Release(); }
            // 이 계정 신청이 다 끝났으면 그 계정 휴대폰 HOME 닫기 (스위치가 그 계정을 채울 수 있게)
            if (HomeOf(a).Count == 0) { try { await phone.HomeCloseAsync(ct, a); } catch { } freshOf[a] = true; }
        }
        if (list.Count == 0 || db.Orders(OrderStatus.InHome).Count == 0) PhoneState = "대기";
        else await Task.Delay(TimeSpan.FromSeconds(3), ct);
    }

    private int DeadlineSec(Order o)
    {
        int a0 = AcctOf(o.Id);
        int together = Math.Max(Math.Max(1, HomeOf(a0).Count), int.TryParse(db.Get($"batch_size:{a0}", db.Get("batch_size")), out var bs) ? bs : 1);
        return Math.Max(120, cfg.Limits.ClaimTimeoutSec) + Math.Min(90 * (together - 1), 1800);
    }

    /// <summary>'교환 차례' DM + 시간 제한 시작. 첫 검색은 40초 뒤 (맡길 시간)</summary>
    private void TurnNotify(Order o)
    {
        db.Set($"turn_dm:{o.Id}", "1");
        db.SetClaimed(o.Id, DateTime.UtcNow);
        noneWait[o.Id] = (0, DateTime.UtcNow.AddSeconds(40));
        var oo = db.GetOrder(o.Id) ?? o; int mins = Math.Max(1, DeadlineSec(oo) / 60);
        _ = Task.Run(() => notify(oo.DiscordId, $"🔔 #{oo.Id} **{oo.Describe}** 교환 차례예요! 지금 GTS에 맡겨 주세요. 잠시 뒤부터 봇이 찾기 시작해요 (최대 {mins}분 동안).\n" + GtsGuide(oo)));
        log($"#{o.Id} 교환 차례 알림");
    }

    private DateTime Deadline(Order o) => (o.ClaimedAt ?? DateTime.UtcNow).AddSeconds(DeadlineSec(o));
    private bool Expired(Order o) => DateTime.UtcNow >= Deadline(o);

    private async Task SearchOnce(Order o, CancellationToken ct)
    {
        var item = db.GetPool(o.PoolId.Value);
        var k = o.Key2;
        if (db.Get($"turn_dm:{o.Id}") != "1")
        {
            // 휴대폰이 이 사람 것을 처음 찾기 시작하는 순간: '교환 차례' DM + 시간 제한도 지금부터 셈
            db.Set($"turn_dm:{o.Id}", "1");
            db.SetClaimed(o.Id, DateTime.UtcNow); o = db.GetOrder(o.Id) ?? o;
            var oo = o;
            _ = Task.Run(() => notify(oo.DiscordId, $"🔔 #{oo.Id} **{oo.Describe}** 교환 차례예요! 지금 봇이 GTS에서 맡긴 포켓몬을 찾고 있어요 (최대 {Math.Max(1, DeadlineSec(oo) / 60)}분 동안).\n" + GtsGuide(oo)));
        }
        var st = ph.GetValueOrDefault(o.Id);
        // 묶음이면 돌아가며 찾으므로 그만큼 시간을 더 줌. 묶음 크기는 HOME에 올릴 때 정해 둔 값 (교환이 끝날수록 줄어드는 지금 수를 쓰면 남은 사람 시간이 깎임)
        int acct = AcctOf(o.Id);
        var deadline = Deadline(o);
        if (DateTime.UtcNow >= deadline)
        {
            if (!db.SetStatusIf(o.Id, OrderStatus.InHome, OrderStatus.Failed, error: "시간 안에 GTS에서 찾지 못함")) { ph.TryRemove(o.Id, out _); return; }   // 그 사이 취소됨
            db.EndTrade(db.AddTrade(o.Id, item?.Id, o.Route), "시간 초과", st.shot ?? "");
            if (item != null) db.Reserve(item.Id, null);
            log($"#{o.Id} 시간 초과 (GTS에서 못 찾음)");
            await notify(o.DiscordId, $"⌛ #{o.Id} 시간 안에 GTS에서 맡긴 포켓몬(**{k.Ko()}**)을 찾지 못해 신청을 끝냈습니다. 맡긴 포켓몬은 GTS에서 회수하고 다시 신청해 주세요.");
            ph.TryRemove(o.Id, out _); return;
        }
        PhoneState = $"#{o.Id} GTS에서 {k.Ko()} 찾는 중 (" + (Accounts > 1 ? $"#N{acct} " : "") + $"HOME 1-{item?.Slot + 1})";
        var mine = item != null ? gen.FromBytes(item.PkBytes, item.Format) : null;
        // 휴대폰 화면의 이름: 닉네임(그 언어) — HOME이 종 이름을 앱 언어(영어)로 바꿔 보여 줄 때를 대비해 영어 종 이름도 함께 보냄
        var nickArg = mine == null ? "" : ShownName(mine) + (mine.IsNicknamed ? "" : "|" + gen.En.Species[mine.Species]);
        if (freshOf[acct] && DateTime.UtcNow - prelaunchOf[acct] < TimeSpan.FromMinutes(5)) { freshOf[acct] = false; prelaunchOf[acct] = DateTime.MinValue; }   // 미리 켜 둔 HOME을 그대로 씀
        // 같은 종·같은 이름의 봇 개체가 HOME에 여럿이면: 휴대폰 GTS 박스도 HOME 칸 순서대로 보이므로, 이 신청 개체가 그중 몇 번째인지 함께 보냄
        int nth = 0, total = 1;
        if (mine != null && item != null)
        {
            var sibs = db.Pool(Location.Home).Where(p => PoolAcct(p.Id) == acct).Select(p => (p, pk: gen.FromBytes(p.PkBytes, p.Format)))
                .Where(x => x.pk.Species == mine.Species && ShownName(x.pk) == ShownName(mine)).OrderBy(x => x.p.Slot).ToList();
            if (sibs.Count > 1) { total = sibs.Count; nth = Math.Max(0, sibs.FindIndex(x => x.p.Id == item.Id)); log($"#{o.Id} 같은 이름 {total}마리 중 {nth + 1}번째 (HOME 칸 순서) → 요약 화면으로 확인"); }
        }
        string fp = mine != null ? Fingerprint(mine) : "";   // 같은 이름이 화면에 여럿 보이면(기록에 없는 남은 개체 등) 요약으로 확인하는 데 씀
        var r = await phone.GtsFindTradeAsync(k.NameEn, k.SearchGender, k.Level, k.Lang, item?.Slot ?? 0, freshOf[acct], ct, nickArg, mine != null ? LangCode(mine.Language) : "", nth, total, fp, acct);
        freshOf[acct] = false;
        if (r.Vars.TryGetValue("shot", out var sh) && sh.Length > 0) st.shot = sh;
        if (!r.Ok)
        {
            if (r.Message.Contains("검게만 보임"))
            {
                // MuMu 화면이 안 그려짐 (PC 화면 꺼짐·잠금) → 신청자 탓이 아니니 실패로 세지 않고, 봇을 멈추고 운영자에게 알림
                log($"#{o.Id} {r.Message} → 일시정지"); freshOf[acct] = true; ph[o.Id] = st;
                if (!Paused) { Paused = true; LastError = "휴대폰 화면이 검게만 보여 일시정지 (PC 화면 꺼짐·잠금 확인 후 재개)"; await AdminAlert(LastError); }
                return;
            }
            if (r.Message.Contains("통신 오류"))
            {
                // HOME 서버 통신 오류 창 → 워커가 OK를 눌러 닫음. 신청자 탓이 아니니 실패로 세지 않고 다음 차례에 다시
                log($"#{o.Id} {r.Message}"); ph[o.Id] = st; return;
            }
            if (r.Message.Contains("WANT_MISMATCH"))
            {
                // 찾은 GTS 글이 원하는 포켓몬이 봇 포켓몬과 다름 → 봇 오류가 아님. 신청자에게 한 번 알리고, 실패로 세지 않고 쉬었다 다시 찾음 (시간 제한 안에서)
                log($"#{o.Id} 찾은 글의 '원하는 포켓몬'이 봇 포켓몬과 다름 (신청자 설정 확인 필요)");
                noneWait[o.Id] = (Math.Min(noneWait.GetValueOrDefault(o.Id).n + 1, NoneBackoff.Length - 1), DateTime.UtcNow.AddSeconds(60));
                if (!st.toldMany)
                {
                    st.toldMany = true;
                    var mine2 = item != null ? gen.FromBytes(item.PkBytes, item.Format) : null;
                    var want = mine2 != null ? $"**{SpeciesOf(o.Describe)}** · 성별 **{GenderKo(mine2)}** · 레벨 **{LevelBand(mine2)}**" : $"**{SpeciesOf(o.Describe)}**";
                    _ = Task.Run(() => notify(o.DiscordId, $"⚠️ #{o.Id} GTS에서 맡긴 포켓몬(**{k.Ko()}**)을 찾았는데, 그 글의 '원하는 포켓몬'이 봇 포켓몬과 맞지 않아 교환할 수 없었어요.\n> 원하는 포켓몬을 {want} 로 설정해 다시 맡겨 주세요. (같은 조건의 다른 사람 글이 먼저 보였을 수도 있어요)\n맞게 맡기셨다면 그대로 두시면 계속 찾아요."));
                }
                ph[o.Id] = st; return;
            }
            if (r.Message.StartsWith("폰 워커 호출 실패"))
            {
                // 워커가 다시 켜지는 중이거나 연결이 잠깐 끊김 → 신청자 탓이 아니니 실패 횟수에 넣지 않고 조금 기다림
                log($"#{o.Id} 워커 연결 안 됨 (다시 시도): {r.Message}"); freshOf[acct] = true; ph[o.Id] = st;
                await Task.Delay(5000, ct); return;
            }
            st.errors++; log($"#{o.Id} GTS 검색 오류: {r.Message}"); LastError = "GTS: " + r.Message; freshOf[acct] = true;
            if (st.errors >= 3)
            {
                if (!db.SetStatusIf(o.Id, OrderStatus.InHome, OrderStatus.Failed, error: "휴대폰 HOME 조작 오류")) { ph.TryRemove(o.Id, out _); return; }   // 그 사이 취소됨
                db.EndTrade(db.AddTrade(o.Id, item?.Id, o.Route), "휴대폰 오류", st.shot ?? "");
                if (item != null) db.Reserve(item.Id, null);
                log($"[넘김] #{o.Id} 봇 쪽 오류 3번 → 넘김: {r.Message}");
                await notify(o.DiscordId, $"❌ #{o.Id} {o.Describe} · 봇 쪽 문제로 교환하지 못해 신청을 넘겼습니다. 운영자가 문제를 파악해 고치고 있으니, GTS에 맡긴 포켓몬은 회수하시고 나중에 다시 신청해 주세요. 불편을 드려 죄송합니다.");
                await AdminAlert($"#{o.Id} 휴대폰 HOME 오류: {LastError}");
                ph.TryRemove(o.Id, out _); return;
            }
            ph[o.Id] = st; return;
        }
        st.errors = 0;
        bool tradedEarlier = false;
        if (r.Vars.GetValueOrDefault("traded") != "1" && (int.TryParse(r.Vars.GetValueOrDefault("found"), out var f0) ? f0 : 0) == 0)
        {
            // 검색 결과가 없음: 봇이 교환 도중 꺼졌다 켜져서 교환은 끝났는데 결과를 못 받은 경우일 수 있음 → 워커 기록 확인
            try { tradedEarlier = await phone.TradedRecentlyAsync(k.NameEn, k.Level, k.Lang, k.SearchGender, nickArg, 40, ct, acct); } catch { }
            if (tradedEarlier) log($"#{o.Id} 검색 결과는 없지만 워커 기록에 교환 완료가 있음 → 완료 처리");
        }
        if (r.Vars.GetValueOrDefault("traded") == "1" || tradedEarlier)
        {
            db.EndTrade(db.AddTrade(o.Id, item?.Id, o.Route), "ok", st.shot ?? "");
            if (item != null) db.SetLocation(item.Id, Location.Gone, -1, -1);
            db.SetStatus(o.Id, OrderStatus.Done);
            await notify(o.DiscordId, $"✅ #{o.Id} **{o.Describe}** 교환 완료! HOME 박스를 확인해 주세요. 이용해 주셔서 감사합니다");
            log($"#{o.Id} 교환 완료"); ph.TryRemove(o.Id, out _); return;
        }
        int found = int.TryParse(r.Vars.GetValueOrDefault("found"), out var f) ? f : 0;
        // 결과가 없으면(아직 안 맡김) 다음 1바퀴는 건너뜀, 두 번 넘게 없으면 2바퀴 — 맡긴 사람들을 먼저 처리
        if (found == 0)
        {
            var w = noneWait.GetValueOrDefault(o.Id);
            int n = Math.Min(w.n, NoneBackoff.Length - 1);
            noneWait[o.Id] = (w.n + 1, DateTime.UtcNow.AddSeconds(NoneBackoff[n]));
        }
        else noneWait.TryRemove(o.Id, out _);
        if (found == 0 && !st.toldNone && DateTime.UtcNow - (o.ClaimedAt ?? DateTime.UtcNow) >= TimeSpan.FromSeconds(150))
        {
            st.toldNone = true;
            var txt = $"🔍 #{o.Id} GTS에서 맡긴 포켓몬을 찾지 못했어요. 계속 찾는 중이에요 (남은 시간 약 {(int)(deadline - DateTime.UtcNow).TotalMinutes}분)\n" +
                "확인해 주세요:\n" +
                $"> • 맡긴 포켓몬: **{k.Ko()}** 이 맞나요? (이름·레벨·성별·언어가 하나라도 다르면 못 찾아요)\n" +
                (mine != null ? $"> • 원하는 포켓몬을 **{SpeciesOf(o.Describe)}** · 성별 **{GenderKo(mine)}** · 레벨 **{LevelBand(mine)}** 로 설정했나요?\n" : $"> • 원하는 포켓몬을 **{SpeciesOf(o.Describe)}**로 설정했나요?\n") +
                "> • GTS에 맡긴 지 1~2분 지나야 검색에 나올 수 있어요\n" +
                "맡긴 포켓몬 정보가 틀렸다면 아래 **[맡긴 포켓몬 수정]**으로 고치거나, **[신청 취소]**를 눌러 주세요. 맞다면 그대로 두시면 계속 찾아요.";
            if (OnNoResult != null) await OnNoResult(o, txt); else await notify(o.DiscordId, txt);
        }
        else if (found == 0 && st.toldNone && !IsToldRetry(o.Id) && noneSinceDm.AddOrUpdate(o.Id, 1, (_, v) => v + 1) >= 3)
        {
            // 안내 DM 뒤에도 3번 더 못 찾음: 정보가 맞아도 HOME GTS 검색에 늦게 반영되는 경우가 있음 → 다시 맡기기 또는 취소 안내 (한 번만)
            lock (toldRetry) toldRetry.Add(o.Id);
            var txt = $"🔁 #{o.Id} 3번 더 찾아봤지만 GTS 검색에 **{k.Ko()}**이(가) 나오지 않아요.\n" +
                "맡긴 정보가 맞는데도 안 나온다면 HOME GTS 쪽에서 검색에 늦게 반영되는 경우일 수 있어요.\n" +
                "> • GTS에서 맡긴 포켓몬을 **회수한 뒤 다시 맡겨** 보시거나\n" +
                "> • 아래 **[신청 취소]**를 누르고 맡긴 포켓몬을 회수한 뒤, 잠시 후 다시 신청해 주세요.\n" +
                "그대로 두시면 남은 시간 동안 계속 찾아요.";
            log($"#{o.Id} 안내 뒤 3번 더 못 찾음 → 다시 맡기기·취소 안내");
            if (OnNoResult != null) await OnNoResult(o, txt); else await notify(o.DiscordId, txt);
        }
        else if (found > 1 && !st.toldMany)
        {
            st.toldMany = true;
            await notify(o.DiscordId, $"⚠️ #{o.Id} 같은 조건(**{k.Ko()}**)의 글이 {found}개라 어느 것이 당신 것인지 알 수 없습니다. 맡긴 것을 회수하고 **레벨이 다른 포켓몬**으로 다시 맡긴 뒤 /취소 → 다시 신청해 주세요.");
        }
        ph[o.Id] = st;
    }

    // ---- 브다샤펄 전설·환상 제한: HOME은 브다샤펄 세이브 하나당, 트래커가 없는(브다샤펄에서 태어난) 전설·준전설·환상을
    //      종마다 일반색·이로치 각각 1마리만 받음. 세이브를 되돌려도 HOME 서버가 기억하므로 봇이 보낸 것을 기록해 두고 같은 조합은 받지 않음.
    public static string BdspSpecialKey(string game, PKM pk)
    {
        if (pk is not PB8 pb || Norm(game) != "BD") return null;
        if (pb.Tracker != 0) return null;                                          // 다른 게임에서 HOME을 거쳐 온 개체는 제한 없음
        if (!(SpeciesCategory.IsLegendary(pk.Species) || SpeciesCategory.IsSubLegendary(pk.Species) || SpeciesCategory.IsMythical(pk.Species))) return null;
        return $"{pk.Species}:{(pk.IsShiny ? 1 : 0)}";
    }
    private HashSet<string> BdspSent() => db.Get("bdsp_special_sent").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    private void BdspMarkSent(string key) { var s = BdspSent(); if (s.Add(key)) db.Set("bdsp_special_sent", string.Join(",", s)); }
    /// <summary>이 개체를 받으면 안 되는 이유 (없으면 null): 이미 HOME으로 보냈거나, 같은 조합이 대기열에 이미 있음</summary>
    private string BdspBlock(string game, PKM pk, long selfId)
    {
        if (BdspSpecialKey(game, pk) is not { } key) return null;
        var name = gen.Strings.Species[pk.Species] + (pk.IsShiny ? " 이로치" : "");
        string Why(string w) => $"브릴리언트 다이아몬드·샤이닝 펄의 {name}은(는) {w}. HOME은 브다샤펄 세이브 하나에서 전설·환상을 종마다 일반색·이로치 각각 한 번만 받아요. 다른 게임으로 신청해 주세요";
        if (BdspSent().Contains(key)) return Why("이 봇 세이브에서 이미 HOME으로 보냈어요");
        // 진행 중인 신청 + 옮기는 사이 취소돼 개체가 아직 스위치·HOME에 남은 신청 (그대로 HOME에 올라가 기록됨)
        foreach (var other in db.Orders(Active).Concat(db.Orders(OrderStatus.Cancelled).TakeLast(50)))
        {
            if (other.Id == selfId || other.PoolId == null || (NormOrNull(other.Game) ?? Game) != "BD") continue;
            var it = db.GetPool(other.PoolId.Value);
            if (it == null || it.Species != pk.Species || it.Shiny != pk.IsShiny) continue;
            if (other.Status == OrderStatus.Cancelled && it.Location is not (Location.Switch or Location.Home)) continue;
            try { if (BdspSpecialKey("BD", GenOf("BD").FromBytes(it.PkBytes, it.Format)) == key) return Why($"이미 다른 신청(#{other.Id})으로 보내는 중이에요"); } catch { }
        }
        return null;
    }

    /// <summary>운영자: 브다샤펄로 이미 보낸 전설·환상 기록 보기·추가·빼기 (봇 밖에서 보낸 것도 적어 둘 수 있음). 인자: "" 보기 · "종이름[:이로치]" 추가 · "-종이름[:이로치]" 빼기 · "clear"</summary>
    public string AdminBdspSent(string arg)
    {
        arg = (arg ?? "").Trim();
        string Show() { var s = BdspSent(); return s.Count == 0 ? "브다샤펄 전설·환상 기록 없음" : "브다샤펄로 보낸 전설·환상: " + string.Join(", ", s.Select(k => { var p = k.Split(':'); return gen.Strings.Species[int.Parse(p[0])] + (p[1] == "1" ? " 이로치" : ""); })); }
        if (arg == "") return Show();
        if (arg == "clear") { db.Set("bdsp_special_sent", ""); log("운영자: 브다샤펄 전설·환상 기록 비움"); return "기록을 비웠습니다"; }
        bool remove = arg.StartsWith('-'); if (remove) arg = arg[1..];
        var parts = arg.Split(':'); var name = parts[0].Trim(); bool shiny = parts.Length > 1 && parts[1].Trim() is "이로치" or "1" or "shiny";
        int sp = Array.FindIndex(gen.Strings.Species.ToArray(), x => x == name);
        if (sp <= 0) sp = Array.FindIndex(GameInfo.GetStrings("en").Species.ToArray(), x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (sp <= 0) return $"'{name}' 포켓몬을 찾지 못했습니다";
        if (!(SpeciesCategory.IsLegendary((ushort)sp) || SpeciesCategory.IsSubLegendary((ushort)sp) || SpeciesCategory.IsMythical((ushort)sp))) return $"{gen.Strings.Species[sp]}은(는) 전설·환상이 아니라 제한 대상이 아닙니다";
        var key = $"{sp}:{(shiny ? 1 : 0)}"; var set = BdspSent();
        if (remove) set.Remove(key); else set.Add(key);
        db.Set("bdsp_special_sent", string.Join(",", set)); log($"운영자: 브다샤펄 기록 {(remove ? "빼기" : "추가")} {gen.Strings.Species[sp]}{(shiny ? " 이로치" : "")}");
        return Show();
    }

    /// <summary>실패로 끝냄. 그 사이 상태가 바뀌었으면(취소 등) 건드리지 않고 false</summary>
    private bool Fail(Order o, string why)
    {
        if (!db.SetStatusIf(o.Id, o.Status, OrderStatus.Failed, error: why)) { log($"#{o.Id} 실패 처리 건너뜀 (그 사이 상태가 바뀜): {why}"); return false; }
        log($"#{o.Id} 실패: {why}"); return true;
    }
    /// <summary>밖에서 멈춰야 할 일이 생겼을 때 (이미 멈춰 있으면 알리지 않음)</summary>
    public void PauseFor(string why)
    {
        if (Paused) return;
        Paused = true; LastError = why; log(why);
        _ = Task.Run(async () => { try { await AdminAlert(why); } catch { } });
    }
    private async Task AdminAlert(string msg) { foreach (var a in cfg.Discord.AdminIds) await notify(a, "🛠 [HomeBot] " + msg); }
    private static bool Same(PKM a, PKM b) => a.Species == b.Species && a.PID == b.PID && a.EncryptionConstant == b.EncryptionConstant;

    // =============== 관리 창: 대기열 편집 ===============
    /// <summary>순서를 바꿀 수 있는 신청 (아직 스위치에 넣기 전)</summary>
    private static readonly OrderStatus[] Movable = [OrderStatus.Queued, OrderStatus.Generated];

    public string MoveOrder(long id, string dir)
    {
        var list = db.Orders(Movable);
        int i = list.FindIndex(o => o.Id == id);
        if (i < 0) return $"#{id}는 순서를 바꿀 수 없는 상태입니다 (스위치에 들어간 뒤에는 고정)";
        int j = dir switch { "up" => i - 1, "down" => i + 1, "top" => 0, "bottom" => list.Count - 1, _ => i };
        if (j < 0 || j >= list.Count || j == i) return "더 옮길 수 없습니다";
        var item = list[i]; list.RemoveAt(i); list.Insert(j, item);
        // 앞부분 기준값부터 차례로 다시 매김
        double baseP = list.Select(o => db.PriorityOf(o.Id)).Min();
        for (int k = 0; k < list.Count; k++) db.SetPriority(list[k].Id, baseP + k * 0.001);
        // 이미 생성된 개체가 순서 뒤로 밀렸으면 그대로 둠 (생성은 한 마리만 미리 하므로 큰 차이 없음)
        return $"#{id} 순서 변경";
    }

    /// <summary>운영자: 실제로는 교환이 끝났는데 봇이 모르고 있는 신청을 완료로 처리</summary>
    public async Task<string> AdminDoneAsync(long id)
    {
        var o = db.GetOrder(id);
        if (o == null || !Active.Contains(o.Status)) return $"#{id}는 진행 중이 아닙니다";
        if (o.PoolId != null) db.SetLocation(o.PoolId.Value, Location.Gone, -1, -1);
        db.SetStatus(o.Id, OrderStatus.Done, note: "운영자가 완료 처리");
        ph.TryRemove(o.Id, out _);
        log($"#{id} 운영자 완료 처리");
        await notify(o.DiscordId, $"✅ #{o.Id} **{o.Describe}** 교환 완료! HOME 박스를 확인해 주세요. 이용해 주셔서 감사합니다");
        return $"#{id} 완료 처리";
    }

    /// <summary>운영자: 스위치·HOME에 올렸던 개체가 사라졌을 때(수동 조작 등) 같은 개체를 다시 게임 박스에 넣는 단계로 되돌림</summary>
    public Task<string> AdminRequeueAsync(long id)
    {
        var o = db.GetOrder(id);
        if (o == null || o.PoolId == null || o.Status is not (OrderStatus.OnSwitch or OrderStatus.InHome or OrderStatus.Trading or OrderStatus.Failed))
            return Task.FromResult($"#{id}는 스위치·HOME 단계가 아니라 다시 넣을 수 없습니다");
        var pi = db.GetPool(o.PoolId.Value);
        if (pi == null || (pi.ReservedBy != null && pi.ReservedBy != o.Id)) return Task.FromResult($"#{id}의 개체가 없거나 다른 신청이 쓰는 중이라 다시 넣을 수 없습니다");
        db.Reserve(o.PoolId.Value, o.Id);   // 실패로 끝난 신청은 개체 예약이 풀려 있음 → 다시 잡음
        db.SetLocation(o.PoolId.Value, Location.Pc, -1, -1);
        db.SetClaimed(o.Id, null);
        db.SetStatus(o.Id, OrderStatus.Generated, note: "운영자가 다시 넣기");
        ph.TryRemove(o.Id, out _);
        log($"#{id} 운영자: 다시 넣기 (생성된 개체를 게임 박스에 다시 넣음)");
        return Task.FromResult($"#{id} 다시 넣기");
    }

    /// <summary>운영자: 신청자에게 DM만 보냄 (상태는 그대로)</summary>
    public async Task<string> AdminDmAsync(long id, string text)
    {
        var o = db.GetOrder(id);
        if (o == null) return $"#{id} 신청이 없습니다";
        await notify(o.DiscordId, $"📩 #{o.Id} {o.Describe} · {text}");
        log($"#{id} 운영자 DM 보냄");
        return $"#{id} DM 보냄";
    }

    public async Task<string> AdminCancelAsync(long id, string why = "운영자가 신청을 취소했습니다")
    {
        var o = db.GetOrder(id);
        if (o == null || !Active.Contains(o.Status)) return $"#{id}는 진행 중이 아닙니다";
        if (o.PoolId != null) db.Reserve(o.PoolId.Value, null);
        db.SetStatus(o.Id, OrderStatus.Cancelled, error: why);
        log($"#{id} 관리자 취소");
        await notify(o.DiscordId, $"🚫 #{o.Id} {o.Describe} · {why}" + (o.Status is OrderStatus.InHome or OrderStatus.Trading ? ". GTS에 맡긴 포켓몬이 있다면 회수해 주세요." : ""));
        return $"#{id} 취소";
    }

    public async Task<string> ClearQueueAsync(bool includeTrading)
    {
        int n = 0;
        foreach (var o in db.Orders(Active))
        {
            if (!includeTrading && o.Status is OrderStatus.InHome or OrderStatus.Trading) continue;
            await AdminCancelAsync(o.Id, "운영자가 대기열을 초기화해 신청이 취소됐습니다"); n++;
        }
        return $"{n}건 취소";
    }

    /// <summary>관리 창에 보여 줄 대기열 (탭 구분 한 줄씩: id, 상태, 설명, 사용자, 맡길 포켓몬, 순서변경가능)</summary>
    public List<ulong> ActiveUserIds() => db.Orders(Active).Select(o => o.DiscordId).Distinct().ToList();

    public string QueueTsv()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var o in db.Orders(Active))
            sb.Append(o.Id).Append('\t').Append(o.Status.Ko()).Append('\t').Append(o.Describe.Replace('\t', ' ')).Append('\t').Append(o.DiscordId).Append('\t')
              .Append(o.Key2.Valid ? o.Key2.Ko() : "").Append('\t').Append(Movable.Contains(o.Status) ? "1" : "0").Append('\t')
              .Append(db.Get($"name:{o.DiscordId}").Replace('\t', ' ')).Append('\t').Append(GameKo(string.IsNullOrEmpty(o.Game) ? Game : o.Game)).Append('\n');
        return sb.ToString();
    }
}
