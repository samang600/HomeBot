using System.Net.Http.Json;
using Discord;
using Discord.WebSocket;
using HomeBot.Core;
using HomeBot.Core.Workers;

// 사용법:
//   HomeBot.Discord [config.json]           봇 실행
//   HomeBot.Discord record-switch [config]  스위치 화면을 1초마다 저장 (템플릿 수집용, Ctrl+C로 끝)
//   HomeBot.Discord macro <이름> [config]   매크로 하나만 돌려 보기
//   HomeBot.Discord gen "<신청 문장>" [config]  생성 엔진만 시험 (디스코드·스위치 없이)
Console.OutputEncoding = System.Text.Encoding.UTF8;
var args0 = args.ToList();
string cmd = args0.Count > 0 && !args0[0].EndsWith(".json") ? args0[0] : "run";
string cfgPath = args0.LastOrDefault(a => a.EndsWith(".json")) ?? "config.json";
if (!File.Exists(cfgPath)) { Console.WriteLine($"설정 파일이 없습니다: {cfgPath} (config.sample.json을 복사해 쓰세요)"); return 1; }
var cfg = BotConfig.Load(cfgPath);
var logPath = Path.Combine(cfg.DataDir, "bot.log");
void Log(string s) { var line = $"[{DateTime.Now:MM-dd HH:mm:ss}] {s}"; Console.WriteLine(line); try { File.AppendAllText(logPath, line + "\n"); } catch { } }

var db = new Db(cfg.DbPath);
// 게임 교대: 마지막으로 바꾼 게임으로 시작. 처음이면 설정의 게임
{
    // 관리 쪽에서 시작 게임을 바꿀 때: data/set_game.txt (VL / SH / BD / GP / PLA / ZA) 가 있으면 그 게임으로 기억을 바꾸고 파일을 지움
    var setFile = Path.Combine(cfg.DataDir, "set_game.txt");
    if (File.Exists(setFile))
    {
        var want = Orchestrator.Norm(File.ReadAllText(setFile).Trim());
        if (db.Get("game") != want) { db.Set("game", want); db.Set("game_since", DateTime.UtcNow.ToString("o")); }
        try { File.Delete(setFile); } catch { }
        Log($"시작 게임을 {Orchestrator.GameKo(want)}(으)로 바꿈 (set_game.txt)");
    }
    var g = db.Get("game");
    if (g.Length > 0) cfg.Switch.Game = g;
    // 다른 게임 박스에 있던 개체를 HOME에 있다고 잘못 기록했을 때 되돌림: data/restore_switch.txt (신청 번호를 쉼표로) → 그 신청의 개체를 게임 이동 박스로, 상태는 '스위치에 있음'
    var restFile = Path.Combine(cfg.DataDir, "restore_switch.txt");
    if (File.Exists(restFile))
    {
        foreach (var t in File.ReadAllText(restFile).Split(new[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!long.TryParse(t.TrimStart('#'), out var oid)) continue;
            var o = db.GetOrder(oid); if (o == null || o.PoolId == null) continue;
            var item = db.GetPool(o.PoolId.Value); if (item == null) continue;
            db.SetLocation(item.Id, Location.Switch, cfg.Switch.TransferBox - 1, item.Slot is >= 0 and < 30 ? item.Slot : 0);
            db.SetStatus(oid, OrderStatus.OnSwitch, note: "다른 게임 박스에 있음 (되돌림)");
            Log($"#{oid} 기록 되돌림 → 게임 박스 {cfg.Switch.TransferBox}-{(item.Slot is >= 0 and < 30 ? item.Slot : 0) + 1} · 스위치에 있음 ({Orchestrator.GameKo(o.Game)} 신청)");
        }
        try { File.Delete(restFile); } catch { }
    }
    cfg.Switch.Game = Orchestrator.Norm(cfg.Switch.Game);
    if (!Orchestrator.GamesOf(cfg).Contains(cfg.Switch.Game)) { Log($"기억한 게임 {Orchestrator.GameKo(cfg.Switch.Game)}은(는) 다루는 게임 목록에 없어 {Orchestrator.GameKo(Orchestrator.GamesOf(cfg)[0])}(으)로 시작"); cfg.Switch.Game = Orchestrator.GamesOf(cfg)[0]; db.Set("game", cfg.Switch.Game); }
    if (cfg.Switch.TransferBox is < 1 or > 32) cfg.Switch.TransferBox = 1;
    if (cfg.Home.Accounts.Count > 0) cfg.Switch.HomeUserIndex = cfg.Home.Accounts[0].SwitchUser;   // 첫 HOME 계정의 스위치 사용자
    Log($"게임: {Orchestrator.GameKo(cfg.Switch.Game)} · 박스 {cfg.Switch.TransferBox}" + (cfg.Switch.Game == "GP" ? " (2번째 줄부터)" : "") + $" · HOME 계정 {Math.Max(1, cfg.Home.Accounts.Count)}개");
    if (db.Get("game_since").Length == 0) db.Set("game_since", DateTime.UtcNow.ToString("o"));
}
// 짝 게임(스칼렛·바이올렛 등)의 실제 버전: 스위치 세이브 폴더(타이틀 ID)로 알아내 기억 (생성 엔진·HOME 로고가 그 버전 기준)
GenService.VersionOf = g => db.Get("game_ver:" + Orchestrator.Norm(g));
if (cmd != "gen")
{
    try
    {
        var ftp0 = new SaveFtp(cfg.Switch.IP, cfg.Switch.FtpPort, cfg.Switch.FtpUser, cfg.Switch.FtpPass);
        var vers = new List<string>();
        foreach (var g in Orchestrator.GamesOf(cfg))
        {
            try { var v = ftp0.DetectVersion(g, cfg.Switch.SaveUser, cfg.Switch.GameUserIndex); if (v != null) { db.Set("game_ver:" + g, v); vers.Add(Orchestrator.VersionKo(v)); } }
            catch (Exception ex)
            {
                Log($"{Orchestrator.GameKo(g)} 세이브 확인 실패: {ex.Message}" + (db.Get("game_ver:" + g).Length > 0 ? $" (기억한 버전 {Orchestrator.VersionKo(db.Get("game_ver:" + g))} 사용)" : ""));
                if (ex.Message.Contains("연결하지 못했습니다") || ex.Message.Contains("시간 초과")) { Log("ftpsrv에 연결되지 않아 나머지 게임 확인은 건너뜁니다 (기억한 버전으로 시작)"); break; }
            }
        }
        if (vers.Count > 0) Log("스위치 세이브 확인: " + string.Join(" · ", vers));
    }
    catch (Exception ex) { Log("ftpsrv 연결 실패 (기억한 게임 버전으로 시작): " + ex.Message); }
}
var gen = new GenService(cfg);
Log($"생성 엔진 준비: {gen.GameName} · 어버이 {gen.Sav.OT} {gen.Sav.DisplayTID}");

if (cmd == "gen")
{
    var text = args0.Count > 1 ? args0[1] : "피카츄";
    foreach (var o in gen.Parse(text))
    {
        Console.WriteLine("해석: " + gen.Describe(o) + " key=" + OrderKey.Of(o));
        var (pk, msg) = await gen.MakeAsync(o, CancellationToken.None);
        Console.WriteLine(pk == null ? "실패: " + msg : $"성공: {msg} · 합법 {new PKHeX.Core.LegalityAnalysis(pk).Valid}");
    }
    return 0;
}

// 봇 토큰이 없으면 워커를 기다리기 전에 바로 알림 (설치 마법사에서 등록)
if (cmd is not ("macro" or "record-switch") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(cfg.Discord.TokenEnv)))
{ Console.WriteLine($"봇 토큰이 없습니다: 관리 창 [설정] → [🧭 설치 마법사]에서 토큰을 넣으세요 (환경 변수 {cfg.Discord.TokenEnv})"); return 1; }

// 화면 인식: 파이썬 서비스(phone_worker/server.py)가 떠 있으면 그것, 아니면 확인 없이 진행(위험)
IVision vision;
{
    var pc = new PhoneClient(cfg.Phone);
    // 워커가 막 다시 켜지는 중일 수 있으니 1분까지 기다림 (화면 확인 없이 스위치 매크로를 돌리면 위험)
    bool up = false;
    int tries = cmd == "record-switch" ? 1 : 30;   // 스위치 기록은 화면 저장만 하므로 기다리지 않음
    for (int i = 0; i < tries && !(up = await pc.PingAsync(CancellationToken.None)); i++) { if (i == 0) Log("화면 인식 서비스 기다리는 중…"); await Task.Delay(2000); }
    if (cmd == "record-switch" && !up) { vision = new NoVision(); Log("화면 인식 서비스 없이 스위치 화면만 저장합니다"); }
    else
    {
        // 화면 확인 없이 스위치를 조작하면 엉뚱한 메뉴를 누름(10/29 22:50 HOME '지금까지의 기록' 화면에서 놓아주기 시도) → 서비스가 없으면 그 단계를 실패로 보고 일시정지
        vision = new GuardedVision(pc);
        Log(up ? "화면 인식 서비스 연결됨" : "[주의] 화면 인식 서비스가 아직 꺼져 있습니다. 켜질 때까지 스위치 조작은 멈춰 있어요 (관리 창에서 워커를 먼저 켜세요)");
    }
}
var phone = cfg.Phone.Enabled ? new PhoneClient(cfg.Phone) : null;
var sw = new SwitchWorker(cfg, gen.Sav, vision, Log) { VersionOf = GenService.VersionOf };

if (cmd == "record-switch")
{
    sw.EnsureRaw();   // 화면만 찍으므로 켜진 게임 확인 없이 연결 (HOME 앱이 켜져 있어도 됨)
    var dir = Path.Combine(cfg.DataDir, "record_switch", DateTime.Now.ToString("yyyyMMdd_HHmmss")); Directory.CreateDirectory(dir);   // 실행할 때마다 새 폴더
    Console.WriteLine($"1초마다 {dir}에 저장합니다. 컨트롤러로 HOME 이동을 한 번 끝까지 진행하세요. Ctrl+C로 종료");
    using var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    byte[] prev = null; int n = 0;
    while (!cts.IsCancellationRequested)
    {
        try
        {
            var img = await sw.ScreenshotAsync(cts.Token);
            if (prev == null || !img.AsSpan().SequenceEqual(prev)) { File.WriteAllBytes(Path.Combine(dir, $"{++n:0000}_{DateTime.Now:HHmmss}.jpg"), img); prev = img; Console.Write($"\r{n}장 "); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Console.WriteLine("캡처 실패: " + ex.Message); }
        try { await Task.Delay(1000, cts.Token); } catch { }
    }
    return 0;
}
if (cmd == "macro")
{
    await sw.ConnectAsync(CancellationToken.None);
    var vars = new Dictionary<string, string>();
    foreach (var a in args0.Skip(2)) { var i = a.IndexOf('='); if (i > 0) vars[a[..i]] = a[(i + 1)..]; }
    var r = await sw.RunMacroAsync(args0[1], vars, CancellationToken.None);
    Console.WriteLine(r.Ok ? "완료 " + string.Join(" ", r.Vars.Select(kv => kv.Key + "=" + kv.Value)) : "실패: " + r.Message + " " + r.Screenshot);
    return r.Ok ? 0 : 1;
}

// ---------------- 디스코드 봇
var token = Environment.GetEnvironmentVariable(cfg.Discord.TokenEnv);
if (string.IsNullOrEmpty(token)) { Console.WriteLine($"환경 변수 {cfg.Discord.TokenEnv}에 봇 토큰을 넣으세요"); return 1; }
ThreadPool.SetMinThreads(64, 64);   // 생성(합법성 검색)이 스레드를 오래 잡아도 디스코드 응답용 스레드가 바로 나오게
var client = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.DirectMessages, LogLevel = LogSeverity.Info, UseInteractionSnowflakeDate = false });
async Task Dm(ulong user, string msg) { try { var u = await client.GetUserAsync(user); if (u != null) await u.SendMessageAsync(msg); } catch (Exception ex) { Log($"DM 실패 {user}: {ex.Message}"); } }
var orch = new Orchestrator(cfg, db, gen, sw, phone, Log, Dm);
if (vision is GuardedVision gv) gv.OnDown = m => orch.PauseFor("화면 인식 서비스에 연결되지 않아 일시정지 (관리 창에서 워커를 켠 뒤 [▶ 재개]): " + m);
string voteSig = "";
var voteGate = new SemaphoreSlim(1, 1);
// ---- 게임별 신청 채널: 스칼렛·바이올렛 = Discord.ChannelId (0이면 봇이 만듦), 나머지는 봇이 만들어 kv main_chan_XX에 기억
// 기준 게임: 다루는 게임 중 첫 번째 (설정 ChannelId·TestChannelId가 가리키는 채널). 다른 게임 채널은 그 옆에 만들어 기록(main_chan_<게임>)
string BaseGame = Orchestrator.GamesOf(cfg)[0];
ulong MainChanOf(string g) => g == BaseGame && cfg.Discord.ChannelId != 0 ? cfg.Discord.ChannelId : ulong.TryParse(db.Get("main_chan_" + g), out var t) ? t : 0;
ulong[] OrderChannels() => orch.Games.Select(MainChanOf).Where(x => x != 0).Distinct().ToArray();
string GameOfChannel(ulong ch) =>
    ch == 0 ? orch.Game
    : Orchestrator.SaveGames.FirstOrDefault(g => TestChanOf(g) == ch || MainChanOf(g) == ch) ?? orch.Game;
// 테스트 서버 게임별 신청 채널 (설정에 있으면 그 값, 없으면 봇이 만들어 kv test_chan_XX에 기억)
ulong TestChanOf(string g) => g == BaseGame && cfg.Discord.TestChannelId != 0 ? cfg.Discord.TestChannelId : ulong.TryParse(db.Get("test_chan_" + g), out var t) ? t : 0;
bool TestChannel(ulong ch) => ch != 0 && Orchestrator.SaveGames.Any(g => TestChanOf(g) == ch);
bool MainChannel(ulong ch) => OrderChannels().Contains(ch);
bool OrderChannel(ulong ch) => TestChannel(ch) || (!cfg.Discord.TestOnly && MainChannel(ch));
if (phone != null && !await phone.PingAsync(CancellationToken.None)) Log("휴대폰 워커에 연결되지 않습니다 (관리 창에서 [워커 시작] 확인)");

static string SizeName(int s) => s switch { < 0 => "", 0 => "XXXS", <= 30 => "XXS", <= 60 => "XS", <= 100 => "S", <= 160 => "M", <= 195 => "L", <= 241 => "XL", <= 254 => "XXL", _ => "XXXL" };
var http = new HttpClient();
// 폼(리전 폼·메가 등) 그림: PokeAPI의 '종 → 변종' 목록에서 폼 이름과 맞는 것을 찾아 HOME 그림 주소를 얻음 (한 번 찾으면 기억)
var spriteCache = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
async Task<string> FormSprite(PokeCard c)
{
    if (c.FormIndex == 0 || c.FormEn.Length == 0) return c.Sprite;
    var key = $"{c.SpeciesId}/{c.FormIndex}/{(c.Shiny ? 1 : 0)}";
    if (spriteCache.TryGetValue(key, out var hit)) return hit;
    string result = c.Sprite;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var sp = await http.GetFromJsonAsync<System.Text.Json.JsonElement>($"https://pokeapi.co/api/v2/pokemon-species/{c.SpeciesId}", cts.Token);
        // PKHeX 폼 이름 → PokeAPI 이름 조각
        var words = c.FormEn.ToLowerInvariant().Replace("alolan", "alola").Replace("galarian", "galar").Replace("hisuian", "hisui").Replace("paldean", "paldea").Replace("gigantamax", "gmax")
            .Split(new[] { ' ', '-', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 0 && w != "form" && w != "forme" && w != "style" && w != "mode" && w != "size").ToList();
        string best = null; int bestScore = 0;
        foreach (var v in sp.GetProperty("varieties").EnumerateArray())
        {
            var name = v.GetProperty("pokemon").GetProperty("name").GetString() ?? "";
            var rest = name.Length > c.SpeciesEn.Length ? name[(c.SpeciesEn.ToLowerInvariant().Replace(" ", "-").Length)..].Trim('-') : "";
            if (rest.Length == 0) continue;
            int score = words.Count(w => rest.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (score > bestScore || (score == bestScore && best != null && rest.Length < best.Length && score > 0)) { bestScore = score; best = name; }
        }
        if (best != null && bestScore > 0)
        {
            var pm = await http.GetFromJsonAsync<System.Text.Json.JsonElement>($"https://pokeapi.co/api/v2/pokemon/{best}", cts.Token);
            var home = pm.GetProperty("sprites").GetProperty("other").GetProperty("home");
            var url = home.GetProperty(c.Shiny ? "front_shiny" : "front_default");
            if (url.ValueKind == System.Text.Json.JsonValueKind.String) result = url.GetString();
            else { var any = pm.GetProperty("sprites").GetProperty(c.Shiny ? "front_shiny" : "front_default"); if (any.ValueKind == System.Text.Json.JsonValueKind.String) result = any.GetString(); }
        }
    }
    catch (Exception ex) { Log($"폼 그림 찾기 실패 ({c.SpeciesEn} {c.FormEn}): {ex.Message}"); }
    spriteCache[key] = result;
    return result;
}

async Task<Embed> CardEmbed(Order o, PKHeX.Core.PKM pk, IUser who)
{
    var c = gen.Card(pk);
    c.Sprite = await FormSprite(c);
    var lines = new List<string>();
    if (c.Tera.Length > 0) lines.Add($"**테라스탈 타입:** {c.Tera}");
    if (c.Alpha >= 0) lines.Add($"**우두머리:** {(c.Alpha == 1 ? "예" : "아니오")}");
    if (c.Scale >= 0) lines.Add($"**배율:** {c.Scale}");
    lines.Add($"**레벨:** {c.Level}");
    if (c.Ball.Length > 0) lines.Add($"**볼:** {c.Ball}");
    lines.Add($"**만난 레벨:** {c.MetLevel}");
    if (c.MetDate.Length > 0) lines.Add($"**만난 날짜:** {c.MetDate}");
    if (c.MetLocation.Length > 0) lines.Add($"**만난 장소:** {c.MetLocation}");
    lines.Add($"**특성:** {c.Ability}");
    lines.Add($"**성격:** {c.Nature}");
    if (c.Item.Length > 0) lines.Add($"**지닌 물건:** {c.Item}");
    lines.Add($"**언어:** {c.Language}");
    lines.Add($"**개체값:** {c.IVs}");
    if (c.EVs.Length > 0) lines.Add($"**노력치:** {c.EVs}");
    var e = new EmbedBuilder()
        .WithAuthor($"{who?.GlobalName ?? who?.Username ?? "사용자"}의 {c.Title}", who?.GetAvatarUrl() ?? who?.GetDefaultAvatarUrl())
        .WithColor(c.Shiny ? new Color(0xF1C40F) : new Color(0x3498DB))
        .WithThumbnailUrl(c.Sprite)
        .WithDescription($"신청 #{o.Id} · 사용자: <@{o.DiscordId}>\n\n" + string.Join("\n", lines))
        .AddField("기술", c.Moves.Count > 0 ? string.Join("\n", c.Moves.Select(m => "• " + m)) : "-", true)
        .AddField("리본 · 증표", (c.Ribbons.Count + c.Marks.Count) > 0 ? string.Join("\n", c.Marks.Select(m => "✦ " + m).Concat(c.Ribbons.Select(r => "• " + r))) : "없음", true)
        .WithFooter($"준비 중 · 대기 순서 {db.QueuePosition(o.Id)}번" + (orch.EtaText(o.Id) is { Length: > 0 } eta ? $" · {eta}" : "") + $" · 어버이: {c.OT} | TID: {c.TID:D6} | SID: {c.SID:D4} · HomeBot");
    return e.Build();
}
orch.OnCard = async (o, pk) =>
{
    try
    {
        var who = await client.GetUserAsync(o.DiscordId);
        var embed = await CardEmbed(o, pk, who);
        if (o.ChannelId != 0 && await client.GetChannelAsync(o.ChannelId) is IMessageChannel ch) await ch.SendMessageAsync(text: $"<@{o.DiscordId}> 포켓몬을 만들었어요! 지금 HOME으로 옮길 준비 중이에요" + (orch.EtaTurn(o.Id) is { } t && t > TimeSpan.Zero ? $" (교환 차례까지 {Orchestrator.Dur(t)})" : "") + ". 교환 차례가 되면 DM으로 알려 드릴게요 ⏳", embed: embed);
        else if (who != null) await who.SendMessageAsync(embed: embed);
    }
    catch (Exception ex) { Log("카드 보내기 실패: " + ex.Message); }
};

const string Help = """
**HomeBot 사용법** (포켓몬 HOME · GTS로 받기, 한 번에 1마리)
게임마다 **교환신청-sv**(스칼렛·바이올렛) · **-swsh**(소드·실드) · **-bdsp** · **-lgpe**(레츠고) · **-pla**(아르세우스) · **-za** 채널이 있어요. 한 번에 한 게임만 교환하고, 채널 이름의 🟢가 지금 받는 게임이에요. **게임전환-투표** 채널에서 바꾸고 싶은 게임 버튼을 정해진 인원이 누르면(또는 `/전환투표`) 지금 게임 신청을 마감하고, 남은 신청을 끝낸 뒤 그 게임으로 바뀝니다.
1. `/교환` 을 입력하면 신청 창이 뜹니다. 파일로 신청하려면 `/교환 파일:` 에 .pk9 · .pk8 · .pb8 · .pb7 · .pa8 · .pa9 등을 첨부하세요.
   • 포켓몬: 빠른 생성 문장(예: `피카츄 암컷 이로치 6V 몬스터볼`) 또는 쇼다운 세트를 붙여 넣기
   • GTS에 올릴 포켓몬: 내가 GTS에 맡길 아무 포켓몬의 이름·레벨·성별·언어
2. HOME → 교환 → GTS → **포켓몬을 맡긴다** 로 그 포켓몬을 맡기고, 원하는 포켓몬을 **신청한 포켓몬**으로 설정 (성별·레벨은 봇이 차례 알림 DM에서 알려 주는 대로 — '상관없음'으로 두면 다른 사람이 가로챌 수 있어요)
3. 차례가 되면 봇이 GTS에서 맡긴 포켓몬을 찾아 교환합니다. 진행 상황은 DM으로 알려 드려요.
`/해석` 문장이 어떻게 읽히는지 · `/대기열` 내 신청 · `/전체대기열` · `/취소`
""";

client.Log += m => { Log("[Discord] " + m); return Task.CompletedTask; };
client.Ready += async () =>
{
    var guild = client.GetGuild(cfg.Discord.GuildId);
    if (guild == null) { Log("GuildId가 잘못됐거나 봇이 서버에 없습니다"); return; }
    if (cfg.Discord.ChannelId != 0 && guild.GetTextChannel(cfg.Discord.ChannelId) == null) Log($"[설정] ChannelId {cfg.Discord.ChannelId} 채널을 찾지 못했습니다 (ID 확인 필요 · 0으로 두면 봇이 만듦)");
    var cmds = new List<ApplicationCommandProperties>
    {
        new SlashCommandBuilder().WithName("교환").WithDescription("포켓몬 신청 (문장·쇼다운 세트·파일) → HOME GTS로 받기")
            .AddOption("파일", ApplicationCommandOptionType.Attachment, "포켓몬 파일 (.pk9 .pb8 .pk8 등, 선택)", false).Build(),
        new SlashCommandBuilder().WithName("해석").WithDescription("신청 문장/세트가 어떻게 해석되는지만 보기").AddOption("신청", ApplicationCommandOptionType.String, "신청 문장", true).Build(),
        new SlashCommandBuilder().WithName("대기열").WithDescription("내 신청 상태").Build(),
        new SlashCommandBuilder().WithName("전체대기열").WithDescription("지금 대기 중인 신청 목록").Build(),
        new SlashCommandBuilder().WithName("받기").WithDescription("GTS에 맡기는 방법 다시 보기").Build(),
        new SlashCommandBuilder().WithName("수정").WithDescription("GTS에 맡길(맡긴) 내 포켓몬 정보 고치기 (교환 전까지)").AddOption("번호", ApplicationCommandOptionType.Integer, "신청 번호 (비우면 진행 중인 신청)", false).Build(),
        new SlashCommandBuilder().WithName("취소").WithDescription("내 신청 취소").AddOption("번호", ApplicationCommandOptionType.Integer, "신청 번호 (비우면 가장 최근)", false).Build(),
        new SlashCommandBuilder().WithName("전환투표").WithDescription("지금 교환 중인 게임을 다른 게임으로 바꾸자고 투표 (다시 쓰면 취소)").Build(),
        new SlashCommandBuilder().WithName("도움말").WithDescription("사용법").Build(),
        new SlashCommandBuilder().WithName("상태").WithDescription("운영자: 봇 상태").Build(),
        new SlashCommandBuilder().WithName("일시정지").WithDescription("운영자: 멈춤/재개").Build(),
        new SlashCommandBuilder().WithName("차단").WithDescription("운영자: 사용자 차단/해제").AddOption("사용자", ApplicationCommandOptionType.User, "사용자", true).Build(),
    };
    await guild.BulkOverwriteApplicationCommandAsync(cmds.ToArray());
    if (cfg.Discord.TestGuildId != 0 && client.GetGuild(cfg.Discord.TestGuildId) is { } tg)
    {
        try { await tg.BulkOverwriteApplicationCommandAsync(cmds.ToArray()); Log("테스트 서버에도 슬래시 명령 등록" + (cfg.Discord.TestOnly ? " (테스트 전용 모드: 본 서버 신청 막음)" : "")); }
        catch (Exception ex) { Log("테스트 서버 명령 등록 실패: " + ex.Message); }
    }
    else if (cfg.Discord.TestGuildId != 0) Log("테스트 서버에 봇이 없습니다 (초대 필요)");
    Log("슬래시 명령 등록 완료");
};

bool IsAdmin(ulong id) => cfg.Discord.AdminIds.Contains(id);
var pendingFiles = new System.Collections.Concurrent.ConcurrentDictionary<string, IAttachment>();

Modal KeyModal(string id)
{
    return new ModalBuilder().WithTitle("GTS에 맡길 포켓몬 고치기").WithCustomId(id)
        .AddTextInput("GTS에 올릴 내 포켓몬 이름", "kname", TextInputStyle.Short, "예) 구구 / 꼬렛", required: true, maxLength: 20)
        .AddTextInput("그 포켓몬의 레벨 · 성별 (수컷/암컷/없음)", "klevgen", TextInputStyle.Short, "예) 5 수컷", required: true, maxLength: 12)
        .AddTextInput("그 포켓몬의 언어 (KOR ENG JPN 등)", "klang", TextInputStyle.Short, "KOR", required: true, maxLength: 6, value: "KOR").Build();
}
(string level, string gender) SplitLevGen(string lg)
{
    var lv = new string(lg.Where(char.IsDigit).ToArray());
    var g = new string(lg.Where(ch => !char.IsDigit(ch)).ToArray()).Trim();
    return (lv, g.Length == 0 ? "없음" : g);
}
// GTS 검색 결과가 없을 때: 신청자에게 [맡긴 포켓몬 수정] [신청 취소] 버튼이 붙은 DM
orch.OnNoResult = async (o, text) =>
{
    try
    {
        var u = await client.GetUserAsync(o.DiscordId); if (u == null) return;
        var comps = new ComponentBuilder()
            .WithButton("맡긴 포켓몬 수정", $"keyedit_btn:{o.Id}", ButtonStyle.Primary, new Emoji("✏️"))
            .WithButton("신청 취소", $"cancel_btn:{o.Id}", ButtonStyle.Danger, new Emoji("🚫")).Build();
        await u.SendMessageAsync(text, components: comps);
    }
    catch (Exception ex) { Log($"DM 실패 {o.DiscordId}: {ex.Message}"); }
};
Func<SocketMessageComponent, Task> onButton = async b =>
{
    try
    {
        var id = b.Data.CustomId;
        if (id == "vote_switch" || id.StartsWith("vote_switch:"))
        {
            await b.DeferAsync(ephemeral: true);
            if (VoteOnTest() && b.GuildId != cfg.Discord.TestGuildId) { try { await b.FollowupAsync("🛠 지금은 봇 점검 중이라 투표를 잠시 쉬어요.", ephemeral: true); } catch { } return; }
            var (vm, vt) = orch.ToggleVote(b.User.Id, id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : null);
            try { await b.FollowupAsync(vm, ephemeral: true); } catch { }
            await AfterVote(vt);
            return;
        }
        var p = id.Split(':');
        if (p.Length != 2 || !long.TryParse(p[1], out var oid)) return;
        if (p[0] == "keyedit_btn") await b.RespondWithModalAsync(KeyModal($"keyedit:{oid}"));
        else if (p[0] == "cancel_btn") await b.RespondAsync(orch.Cancel(b.User.Id, oid));
    }
    catch (Exception ex) { Log("버튼 오류: " + ex.Message); }
};
// 디스코드 게이트웨이 스레드를 막지 않게 바로 돌려주고 따로 처리 (막히면 다른 사람의 /교환이 3초 안에 응답 못 함)
client.ButtonExecuted += b => { _ = Task.Run(async () => { try { await onButton(b); } catch (Exception ex) { Log("ButtonExecuted 처리 오류: " + ex.Message); } }); return Task.CompletedTask; };

Modal OrderModal(string id, bool hasFile)
{
    var mb = new ModalBuilder().WithTitle(hasFile ? "파일 신청 · GTS에 올릴 포켓몬" : "포켓몬 신청").WithCustomId(id);
    if (!hasFile)
    {
        // 디스코드 신청 창은 칸이 5개까지라 '레벨·성별'을 한 칸으로 묶음
        mb.AddTextInput("포켓몬 (빠른 생성 문장 또는 쇼다운 세트)", "set", TextInputStyle.Paragraph, "예) 피카츄 암컷 이로치 6V 몬스터볼\n또는 쇼다운 세트를 그대로 붙여 넣기", required: true, maxLength: 1000);
        mb.AddTextInput("어버이 이름 · TID · SID (선택, 비우면 봇 기본)", "trainer", TextInputStyle.Short, "예) 새아 468686 1234", required: false, maxLength: 30);
        mb.AddTextInput("GTS에 올릴 내 포켓몬 이름", "kname", TextInputStyle.Short, "예) 구구 / 꼬렛 (아무 포켓몬)", required: true, maxLength: 20);
        mb.AddTextInput("그 포켓몬의 레벨 · 성별 (수컷/암컷/없음)", "klevgen", TextInputStyle.Short, "예) 5 수컷", required: true, maxLength: 12);
        mb.AddTextInput("그 포켓몬의 언어 (KOR ENG JPN 등)", "klang", TextInputStyle.Short, "KOR", required: true, maxLength: 6, value: "KOR");
    }
    else
    {
        mb.AddTextInput("GTS에 올릴 내 포켓몬 이름", "kname", TextInputStyle.Short, "예) 구구 / 꼬렛 (아무 포켓몬)", required: true, maxLength: 20);
        mb.AddTextInput("그 포켓몬의 레벨", "klevel", TextInputStyle.Short, "예) 5", required: true, maxLength: 3);
        mb.AddTextInput("그 포켓몬의 성별 (수컷 / 암컷 / 없음)", "kgender", TextInputStyle.Short, "수컷", required: true, maxLength: 4);
        mb.AddTextInput("그 포켓몬의 언어 (KOR ENG JPN 등)", "klang", TextInputStyle.Short, "KOR", required: true, maxLength: 6, value: "KOR");
    }
    return mb.Build();
}

// ---- 대기열·상태 임베드 (읽기 쉽게)
static string StIcon(OrderStatus s) => s switch
{
    OrderStatus.Queued => "⏳", OrderStatus.Generated => "🧬", OrderStatus.OnSwitch => "🎮", OrderStatus.InHome => "🏠", OrderStatus.Ready => "📦",
    OrderStatus.Trading => "🔄", OrderStatus.Done => "✅", OrderStatus.Failed => "❌", OrderStatus.Cancelled => "🚫", _ => "•",
};
static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
Embed QueueEmbed()
{
    var all = orch.ActiveOrders();
    var eb = new EmbedBuilder().WithColor(new Color(0x3BA55D)).WithTitle($"📋 전체 대기열 · {all.Count}건");
    if (all.Count == 0) eb.WithDescription("대기 중인 신청이 없어요. `/교환`으로 신청해 주세요!");
    else
    {
        var lines = all.Take(20).Select((o, i) =>
        {
            var eta = o.Status is OrderStatus.InHome or OrderStatus.Trading ? "" : orch.EtaText(o.Id).Replace("예상 대기 ", "");
            return $"`{i + 1,2}` {StIcon(o.Status)} **#{o.Id}** {Cut(o.Describe, 46)}\n　　{o.Status.Ko()}" + (eta.Length > 0 ? $" · ⏱ {eta}" : "");
        });
        var d = string.Join("\n", lines); if (all.Count > 20) d += $"\n… 외 {all.Count - 20}건";
        eb.WithDescription(Cut(d, 4000));
    }
    eb.WithFooter($"처리 속도 {orch.PaceLine()}" + (orch.IntakeClosed ? " · 🚫 신청 마감" : "") + (orch.Paused ? " · ⏸ 일시정지" : ""));
    return eb.Build();
}
Embed MyEmbed(ulong uid)
{
    var mine = orch.UserOrders(uid);
    var act = mine.Where(orch.IsActive).Take(5).ToList();
    var done = mine.Where(o => !orch.IsActive(o)).Take(3).ToList();
    var eb = new EmbedBuilder().WithColor(new Color(0x5865F2)).WithTitle("🙋 내 신청");
    if (act.Count == 0) eb.WithDescription("진행 중인 신청이 없어요.");
    foreach (var o in act)
    {
        var v = $"{StIcon(o.Status)} {o.Status.Ko()}";
        if (o.Status is not (OrderStatus.InHome or OrderStatus.Trading)) v += $" · 순서 {orch.Position(o.Id)}번 · {orch.EtaText(o.Id)}";
        if (o.Key2.Valid) v += $"\n📤 맡길 포켓몬: **{o.Key2.Ko()}**";
        if (o.Error.Length > 0) v += $"\n⚠️ {o.Error}";
        eb.AddField($"#{o.Id} {Cut(o.Describe, 200)}", Cut(v, 1000));
    }
    if (done.Count > 0) eb.AddField("최근 끝난 신청", string.Join("\n", done.Select(o => $"{StIcon(o.Status)} #{o.Id} {Cut(o.Describe, 40)}" + (o.Status == OrderStatus.Failed && o.Error.Length > 0 ? $" — {Cut(o.Error, 40)}" : ""))));
    return eb.Build();
}
Embed AdminEmbed()
{
    var all = orch.ActiveOrders();
    var eb = new EmbedBuilder().WithColor(orch.Paused ? new Color(0xFAA61A) : new Color(0x3BA55D))
        .WithTitle((orch.Paused ? "⏸ 일시정지" : "▶ 가동 중") + (orch.IntakeClosed ? " · 🚫 신청 마감" : ""))
        .AddField("🎮 교환 게임", orch.RotationLine(), true)
        .AddField("📈 최근 1시간", $"{orch.TradesLastHour()}건 교환", true)
        .AddField("📋 진행 중", $"{all.Count}건", true)
        .AddField("🕹 스위치", (orch.SwitchConnected ? "연결 · " : "끊김 · ") + (orch.Battery is { } bt ? $"🔋 {bt}% · " : "") + Cut(orch.SwitchState, 200))
        .AddField("📱 휴대폰", Cut(orch.PhoneState, 200));
    if (all.Count > 0) eb.AddField("대기열 앞쪽", Cut(string.Join("\n", all.Take(8).Select(o => $"{StIcon(o.Status)} #{o.Id} {Cut(o.Describe, 40)}")), 1000));
    if (orch.LastError.Length > 0) eb.AddField("⚠️ 마지막 오류", Cut(orch.LastError, 1000));
    return eb.Build();
}
Func<SocketSlashCommand, Task> onSlash = async c =>
{
    if (cfg.Discord.TestOnly && MainChannel(c.ChannelId ?? 0) && !TestChannel(c.ChannelId ?? 0) && c.CommandName == "교환") { await R("🛠 지금은 봇 점검 중이라 신청을 잠시 받지 않아요. 조금 뒤에 다시 시도해 주세요."); return; }
    if (c.Channel is IDMChannel && c.CommandName == "교환") { await R("신청은 서버의 교환신청 채널에서 해 주세요"); return; }
    if (!OrderChannel(c.ChannelId ?? 0) && c.CommandName == "교환") { await R(string.Join(" · ", Orchestrator.SaveGames.Select(g => (MainChanOf(g), Orchestrator.GameKo(g))).Where(x => x.Item1 != 0).Select(x => $"<#{x.Item1}>({x.Item2})")) + " 채널에서 써 주세요"); return; }
    string Opt(string n) => c.Data.Options.FirstOrDefault(o => o.Name == n)?.Value?.ToString() ?? "";
    var uid = c.User.Id;
    RememberName(c.User);
    // 창(모달)을 띄우는 명령(교환·수정) 말고는 먼저 '생각 중' 응답을 보내 3초 제한을 피하고, 결과는 뒤에 보냄 (글이 길면 2000자에서 자름)
    if (c.CommandName is not ("교환" or "수정")) { try { await c.DeferAsync(ephemeral: true); } catch (Exception ex) { Log($"명령 응답 시작 실패 {c.CommandName}: {ex.Message}"); return; } }
    async Task R(string t)
    {
        t ??= ""; if (t.Length > 1990) t = t[..1990] + "…";
        if (c.HasResponded) await c.FollowupAsync(t, ephemeral: true); else await c.RespondAsync(t, ephemeral: true);
    }
    async Task RE(Embed e)
    {
        if (c.HasResponded) await c.FollowupAsync(embed: e, ephemeral: true); else await c.RespondAsync(embed: e, ephemeral: true);
    }
    try
    {
        switch (c.CommandName)
        {
            case "교환":
            {
                var att = c.Data.Options.FirstOrDefault(o => o.Name == "파일")?.Value as IAttachment;
                if (orch.GameGate(GameOfChannel(c.ChannelId ?? 0)) is { } closed) { await R("⏳ " + closed); break; }
                var id = "order:" + Guid.NewGuid().ToString("N")[..12];
                if (att != null) pendingFiles[id] = att;
                await c.RespondWithModalAsync(OrderModal(id, att != null)); break;
            }
            case "해석": { var (_, pv, pb) = orch.PreviewFor(Opt("신청"), GameOfChannel(c.ChannelId ?? 0)); await R(pb ?? pv); break; }
            case "대기열": await RE(MyEmbed(uid)); break;
            case "전체대기열": await RE(QueueEmbed()); break;
            case "받기": await R(orch.Claim(uid)); break;
            case "도움말": await R(Help); break;
            case "전환투표": { var (vm, vt) = orch.ToggleVote(uid); await R(vm); await AfterVote(vt); break; }
            case "수정":
            {
                long nid = long.TryParse(Opt("번호"), out var nn) ? nn : 0;
                await c.RespondWithModalAsync(KeyModal($"keyedit:{nid}")); break;
            }
            case "취소": await R(orch.Cancel(uid, long.TryParse(Opt("번호"), out var n) ? n : null)); break;
            case "상태": if (IsAdmin(uid)) await RE(AdminEmbed()); else await R("운영자만 쓸 수 있습니다"); break;
            case "일시정지": if (!IsAdmin(uid)) { await R("운영자만 쓸 수 있습니다"); break; } orch.Paused = !orch.Paused; await R(orch.Paused ? "⏸ 일시정지" : "▶ 재개"); break;
            case "차단":
            {
                if (!IsAdmin(uid)) { await R("운영자만 쓸 수 있습니다"); break; }
                var target = (IUser)c.Data.Options.First(o => o.Name == "사용자").Value; var u = db.EnsureUser(target.Id); u.Blocked = !u.Blocked; db.SaveUser(u);
                await R($"{target.Username} {(u.Blocked ? "차단" : "차단 해제")}"); break;
            }
        }
    }
    catch (Exception ex) { Log($"명령 오류 {c.CommandName}: {ex}"); try { if (!c.HasResponded) await R("오류: " + ex.Message); else await c.FollowupAsync("오류: " + ex.Message, ephemeral: true); } catch { } }
};
// 디스코드 게이트웨이 스레드를 막지 않게 바로 돌려주고 따로 처리 (막히면 다른 사람의 /교환이 3초 안에 응답 못 함)
client.SlashCommandExecuted += c => { _ = Task.Run(async () => { try { await onSlash(c); } catch (Exception ex) { Log("SlashCommandExecuted 처리 오류: " + ex.Message); } }); return Task.CompletedTask; };

Func<SocketModal, Task> onModal = async m =>
{
    if (m.Data.CustomId.StartsWith("keyedit:"))
    {
        string KV(string id) => m.Data.Components.FirstOrDefault(x => x.CustomId == id)?.Value ?? "";
        try
        {
            await m.DeferAsync(ephemeral: true);
            var (lv, g) = SplitLevGen(KV("klevgen"));
            var (kp, prob) = gen.ParseKey(KV("kname"), lv, g, KV("klang"));
            if (kp == null) { await m.FollowupAsync("❌ " + prob, ephemeral: true); return; }
            long eid = long.TryParse(m.Data.CustomId[8..], out var x) && x > 0 ? x : 0;
            await m.FollowupAsync("✏️ " + orch.EditKey(m.User.Id, eid > 0 ? eid : null, kp), ephemeral: true);
        }
        catch (Exception ex) { Log("수정 창 오류: " + ex); try { await m.FollowupAsync("오류: " + ex.Message, ephemeral: true); } catch { } }
        return;
    }
    if (!m.Data.CustomId.StartsWith("order:")) return;
    string V(string id) => m.Data.Components.FirstOrDefault(x => x.CustomId == id)?.Value ?? "";
    try
    {
        await m.DeferAsync(ephemeral: true);
        string klevel = V("klevel"), kgender = V("kgender");
        if (V("klevgen") is { Length: > 0 } lg)
        {
            // "5 수컷", "5수컷", "수컷 5" 모두 받음
            klevel = new string(lg.Where(char.IsDigit).ToArray());
            kgender = new string(lg.Where(ch => !char.IsDigit(ch)).ToArray()).Trim();
            if (kgender.Length == 0) kgender = "없음";
        }
        var (kp, kprob) = gen.ParseKey(V("kname"), klevel, kgender, V("klang"));
        if (kp == null) { await m.FollowupAsync("❌ " + kprob, ephemeral: true); pendingFiles.TryRemove(m.Data.CustomId, out _); return; }
        ulong ch = m.ChannelId ?? 0;
        (bool ok, string msg, long id) r;
        if (pendingFiles.TryRemove(m.Data.CustomId, out var att))
        {
            if (att.Size > 1024 * 64) { await m.FollowupAsync("❌ 파일이 너무 큽니다", ephemeral: true); return; }
            var bytes = await http.GetByteArrayAsync(att.Url);
            r = await orch.SubmitFileAsync(m.User.Id, bytes, att.Filename, kp, ch, GameOfChannel(ch));
        }
        else
        {
            var text = V("set");
            var (ot, tid, sid, tprob) = gen.ParseTrainer(V("trainer"));
            if (tprob != null) { await m.FollowupAsync("❌ " + tprob, ephemeral: true); return; }
            text = GenService.WithTrainer(text, GenService.LooksShowdown(text), ot, tid, sid);
            r = await orch.SubmitAsync(m.User.Id, text, kp, ch, GameOfChannel(ch));
        }
        await m.FollowupAsync((r.ok ? "✅ " : "❌ ") + r.msg, ephemeral: true);
    }
    catch (Exception ex) { Log("신청 창 오류: " + ex); try { await m.FollowupAsync("오류: " + ex.Message, ephemeral: true); } catch { } }
};
// 디스코드 게이트웨이 스레드를 막지 않게 바로 돌려주고 따로 처리 (막히면 다른 사람의 /교환이 3초 안에 응답 못 함)
client.ModalSubmitted += m => { _ = Task.Run(async () => { try { await onModal(m); } catch (Exception ex) { Log("ModalSubmitted 처리 오류: " + ex.Message); } }); return Task.CompletedTask; };

client.Ready += async () => { _ = Task.Run(async () => { try { await SetupGameChannels(); } catch (Exception ex) { Log("게임 채널 설정 오류: " + ex.Message); } }); await Task.CompletedTask; };
await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();
using var loopCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; loopCts.Cancel(); };
Log("봇 시작");
// ---- 신청자 이름 기억 (관리 창 대기열에 ID와 함께 표시 — 제재·확인용)
void RememberName(IUser u)
{
    try
    {
        var disp = (u as SocketGuildUser)?.DisplayName ?? u.GlobalName ?? u.Username;
        var v = disp == u.Username ? $"@{u.Username}" : $"{disp} (@{u.Username})";
        if (db.Get($"name:{u.Id}") != v) db.Set($"name:{u.Id}", v);
    }
    catch { }
}
DateTime nextNameFetch = DateTime.MinValue;
async Task FillNamesAsync()
{
    // 이름을 모르는 신청자는 디스코드에서 한 번에 한 명씩만 물어봄 (요청 수 제한 지키기)
    if (DateTime.UtcNow < nextNameFetch || client.ConnectionState != ConnectionState.Connected) return;
    var miss = orch.ActiveUserIds().FirstOrDefault(id => db.Get($"name:{id}").Length == 0);
    if (miss == 0) return;
    nextNameFetch = DateTime.UtcNow.AddSeconds(3);
    try
    {
        IUser u = (IUser)client.GetGuild(cfg.Discord.GuildId)?.GetUser(miss) ?? client.GetUser(miss);
        u ??= await client.Rest.GetUserAsync(miss);
        if (u != null) RememberName(u); else db.Set($"name:{miss}", "(알 수 없음)");
    }
    catch { db.Set($"name:{miss}", "(알 수 없음)"); }
}
// 관리 창(HomeBot관리)과 주고받기: data/status.txt 에 상태를 쓰고, data/cmd.txt 의 명령(pause/resume)을 실행
_ = Task.Run(async () =>
{
    var st = Path.Combine(cfg.DataDir, "status.txt"); var cmdf = Path.Combine(cfg.DataDir, "cmd.txt");
    while (!loopCts.IsCancellationRequested)
    {
        try
        {
            if (File.Exists(cmdf))
            {
                // 먼저 옮겨 두고 읽음: 읽고 지우는 사이에 관리 창이 덧붙인 명령이 사라지지 않게
                var runf = cmdf + ".run"; File.Delete(runf); File.Move(cmdf, runf);
                var lines = File.ReadAllLines(runf); File.Delete(runf);
                foreach (var raw in lines)
                { try {
                    var c = raw.Trim(); if (c.Length == 0) continue;
                    var p = c.Split(':'); string res;
                    if (c == "pause") { orch.Paused = true; res = "일시정지"; }
                    else if (c == "resume") { orch.Paused = false; res = "재개"; }
                    else if (p[0] == "move" && p.Length == 3 && long.TryParse(p[1], out var mid)) res = orch.MoveOrder(mid, p[2]);
                    else if (p[0] == "cancel" && p.Length == 2 && long.TryParse(p[1], out var cid)) res = await orch.AdminCancelAsync(cid);
                    else if (p[0] == "done" && p.Length == 2 && long.TryParse(p[1], out var did)) res = await orch.AdminDoneAsync(did);
                    else if (p[0] == "requeue" && p.Length == 2 && long.TryParse(p[1], out var rid)) res = await orch.AdminRequeueAsync(rid);
                    else if (p[0].StartsWith("home_user") && int.TryParse(p[0]["home_user".Length..], out var hk) && p.Length == 2) res = orch.SetHomeUser(hk, p[1]);
                    else if (c is "intake:close" or "intake_close") res = orch.SetIntake(false);
                    else if (c is "intake:open" or "intake_open") res = orch.SetIntake(true);
                    else if (c is "phone_parallel:on" or "phone_parallel:off") res = orch.SetPhoneParallel(c.EndsWith("on"));
                    else if (p[0] == "sw_keys" && p.Length == 2) res = await orch.SwitchKeysAsync(p[1]);
                    else if (c == "sw_shot") res = await orch.SwitchShotAsync();
                    else if (c == "gui_restart") res = GuiRestart(cfg.DataDir);
                    else if (c == "worker_restart") res = phone != null ? ((await phone.ExitAsync(CancellationToken.None)).Ok ? "워커 다시 켜기 (작업이 끝난 뒤)" : "워커 다시 켜기 실패") : "워커 없음";
                    else if (p[0] == "dm" && p.Length >= 3 && long.TryParse(p[1], out var dmid)) res = await orch.AdminDmAsync(dmid, c.Split(':', 3)[2]);
                    else if (p[0] == "cancelmsg" && p.Length >= 3 && long.TryParse(p[1], out var cmid)) res = await orch.AdminCancelAsync(cmid, c.Split(':', 3)[2]);
                    else if (c.StartsWith("bdsp_sent")) res = orch.AdminBdspSent(c.Length > 10 ? c[10..] : "");   // bdsp_sent · bdsp_sent:디아루가:이로치 · bdsp_sent:-디아루가 · bdsp_sent:clear
                    else if (c == "clear") res = await orch.ClearQueueAsync(false);
                    else if (c == "clear_all") res = await orch.ClearQueueAsync(true);
                    else if (c.StartsWith("announce:"))
                    {
                        // 신청 채널(모든 게임)에 공지 올리기: announce:<글> (\n은 줄바꿈)
                        var txt = c["announce:".Length..].Replace("\\n", "\n"); int sent = 0;
                        foreach (var chId in OrderChannels())
                            if (await client.GetChannelAsync(chId) is IMessageChannel ach) { try { await ach.SendMessageAsync(txt); sent++; } catch (Exception ex) { Log("공지 실패: " + ex.Message); } }
                        res = $"공지 올림 ({sent}개 채널)";
                    }
                    else if (c == "shutdown") { res = "종료 요청"; loopCts.Cancel(); }
                    else if (c is "vote:on" or "vote:off") { res = orch.SetVoteMode(c.EndsWith("on")); voteSig = ""; }
                    else if (c is "hybrid:on" or "hybrid:off") res = orch.SetHybrid(c.EndsWith("on"));
                    else if (c == "logo:scan") res = orch.RequestLogoScan();
                    else if (c == "vote:reset") { orch.ResetVotes(); foreach (var t in new[] { true, false }) if (db.Get(VK("vote_channel", t)) == "1") db.Set(VK("vote_channel", t), ""); voteSig = ""; res = "전환 투표 초기화"; }
                    else if (c == "switch:hold") { orch.SwitchHold = true; res = "스위치 쉬기 켬 (스위치 작업을 마치면 쉼)"; }
                    else if (c == "switch:resume") { orch.SwitchHold = false; res = "스위치 쉬기 끔"; }
                    else if (c == "game:switch") { orch.RequestSwitch(); res = "게임 전환 요청 (남은 신청이 끝나면 바꿈)"; }
                    else if (c.StartsWith("game:switch:")) { res = orch.RequestSwitchTo(c["game:switch:".Length..]); voteSig = ""; }
                    else if (c is "screen:off" or "screen:on")
                    {
                        // 관리 창 버튼: 스위치 화면(백라이트)만 끄고 켬
                        cfg.Switch.ScreenOff = c.EndsWith("off");
                        if (cfg.Switch.ScreenOff) sw.ScreenOff(); else sw.ScreenOn();
                        res = cfg.Switch.ScreenOff ? "스위치 화면 끔 (조작·화면 인식은 그대로)" : "스위치 화면 켬";
                    }
                    else if (c == "save:check")
                    {
                        // 세이브 모드 확인: 지금 게임 세이브를 받아 읽기만 함 (쓰지 않음)
                        try { var box = await sw.ReadBoxOfAsync(cfg.Switch.Game, cfg.Switch.TransferBox - 1, CancellationToken.None); var used = Enumerable.Range(0, box.Count).Where(i => box[i] is { Species: > 0 }).ToList(); res = $"세이브 읽기 성공: {Orchestrator.GameKo(cfg.Switch.Game)} 박스 {cfg.Switch.TransferBox} — {used.Count}마리" + (used.Count > 0 ? $" ({string.Join(",", used.Select(i => $"{i + 1}:{box[i].Species}"))})" : " (비어 있음)"); }
                        catch (Exception ex) { res = "세이브 읽기 실패: " + ex.Message; }
                    }
                    else if (c == "ftp:probe") { try { var t = await Task.Run(() => sw.FtpProbe()); File.WriteAllText(Path.Combine(cfg.DataDir, "ftp_probe.txt"), t); res = "FTP 목록을 data/ftp_probe.txt에 저장"; } catch (Exception ex) { res = "FTP 확인 실패: " + ex.Message; } }
                    else res = "모르는 명령: " + c;
                    Log("관리 창: " + res);
                } catch (Exception cex) { Log($"관리 창 명령 실패: {raw.Trim()} · {cex.Message}"); } }   // 한 명령이 실패해도 뒤 명령(일시정지 등)은 실행
            }
            await FillNamesAsync();
            File.WriteAllText(Path.Combine(cfg.DataDir, "queue.tsv"), orch.QueueTsv());
            File.WriteAllText(st, $"[{DateTime.Now:HH:mm:ss}] 디스코드 {client.ConnectionState}\n" + orch.AdminStatus() + "\n\n대기열\n" + orch.QueueText());
            // 관리 창용 기계 상태 (글 상태와 별개로 칸마다): 지금 게임·버전, 같이 받는 게임, 동시 운영, 신청 마감, 계정 배정 등
            try { var sp = Path.Combine(cfg.DataDir, "state.json"); File.WriteAllText(sp + ".tmp", System.Text.Json.JsonSerializer.Serialize(orch.State(client.ConnectionState == ConnectionState.Connected))); File.Move(sp + ".tmp", sp, true); } catch { }
        }
        catch { }
        try { await Task.Delay(1000, loopCts.Token); } catch { }
    }
});
// ---- 상태 채널: 이름으로 켜짐/일시정지/꺼짐 표시 (디스코드는 채널 이름 변경을 10분에 2번까지만 허용 → 바뀔 때만, 횟수 지켜서)
const string NameOn = "🟢 봇 작동 중", NamePause = "🟡 봇 일시정지", NameOff = "🔴 봇 꺼짐";
var renames = new Queue<DateTime>(); string appliedName = null;
async Task<IGuildChannel> StatusChannel()
{
    var guild = client.GetGuild(cfg.Discord.GuildId); if (guild == null) return null;
    ulong id = cfg.Discord.StatusChannelId != 0 ? cfg.Discord.StatusChannelId : (ulong.TryParse(db.Get("status_channel"), out var x) ? x : 0);
    if (id != 0 && guild.GetChannel(id) is IGuildChannel ch) return ch;
    if (id != 0 || loopCts.IsCancellationRequested) return null;   // 기억한 채널을 못 찾음(캐시·종료 중) → 새로 만들지 않음
    try
    {
        var vc = await guild.CreateVoiceChannelAsync(NameOn, p => p.Position = 0);
        try { await vc.AddPermissionOverwriteAsync(guild.EveryoneRole, new OverwritePermissions(connect: PermValue.Deny)); } catch { }
        db.Set("status_channel", vc.Id.ToString()); Log($"상태 채널을 만들었습니다 ({vc.Id})"); appliedName = NameOn;
        return vc;
    }
    catch (Exception ex) { Log("상태 채널을 만들지 못했습니다 (봇에 '채널 관리' 권한이 필요): " + ex.Message); return null; }
}
async Task<bool> SetStatusName(string name, bool force = false)
{
    if (name == appliedName) return true;
    while (renames.Count > 0 && DateTime.UtcNow - renames.Peek() > TimeSpan.FromMinutes(10)) renames.Dequeue();
    if (renames.Count >= 2 && !force) return false;
    var ch = await StatusChannel(); if (ch == null) return false;
    if (ch.Name == name) { appliedName = name; return true; }
    try
    {
        await ch.ModifyAsync(p => p.Name = name, new RequestOptions { Timeout = 8000, RetryMode = RetryMode.AlwaysFail });
        renames.Enqueue(DateTime.UtcNow); appliedName = name; Log("상태 채널: " + name); return true;
    }
    catch (Exception ex)
    {
        // 횟수 제한에 걸리면 10분 동안 다시 시도하지 않음 (로그도 한 번만)
        if (ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) { for (int i = renames.Count; i < 2; i++) renames.Enqueue(DateTime.UtcNow); Log("상태 채널: 디스코드 이름 변경 횟수 제한 → 10분 뒤 다시 시도"); }
        else Log("상태 채널 이름 바꾸기 실패: " + ex.Message);
        return false;
    }
}
_ = Task.Run(async () =>
{
    while (!loopCts.IsCancellationRequested)
    {
        try { if (client.ConnectionState == ConnectionState.Connected && client.GetGuild(cfg.Discord.GuildId) != null) await SetStatusName(orch.Paused ? NamePause : NameOn); } catch { }
        try { await Task.Delay(10000, loopCts.Token); } catch { }
    }
});
// ---- 신청 채널 이름에 지금 교환 중인 게임 표시 (🟢 받는 중 / 🔴 쉬는 중 / ⚫ 이 봇이 안 다루는 게임)
async Task SetupGameChannels()
{
    string On(string g) => orch.Game == g ? "🟢" : "🔴";
    async Task Name(ITextChannel ch, string want)
    {
        if (ch == null || ch.Name == want) return;
        try { await ch.ModifyAsync(p => p.Name = want, new RequestOptions { Timeout = 8000, RetryMode = RetryMode.AlwaysFail }); Log($"채널 이름: {want}"); }
        catch (Exception ex) { Log($"채널 이름 바꾸기 실패({want}): {ex.Message}"); }
    }
    // 서버 하나: 기준 게임(다루는 게임 중 첫 번째) 채널 옆에 다루는 게임마다 신청 채널 하나 (없으면 만들고 기준 채널 권한을 그대로 복사).
    // 본 서버에 기준 채널이 없으면(설정 ChannelId 0) '포켓몬 교환' 분류와 기준 채널부터 만듦. 지금 게임 채널만 🟢
    async Task Setup(SocketGuild guild, ulong baseId, string kv, bool test)
    {
        ITextChannel baseCh = baseId != 0 ? guild.GetTextChannel(baseId) : null;
        if (baseCh == null && !test)
        {
            try
            {
                var cat = await guild.CreateCategoryChannelAsync("포켓몬 교환");
                baseCh = await guild.CreateTextChannelAsync("교환신청-" + Orchestrator.GameCode(BaseGame), p => { p.CategoryId = cat.Id; p.Topic = $"{Orchestrator.GameKo(BaseGame)} 포켓몬 신청 (/교환)"; });
                db.Set(kv + BaseGame, baseCh.Id.ToString()); Log($"'포켓몬 교환' 분류와 신청 채널을 만들었습니다 ({baseCh.Id})");
            }
            catch (Exception ex) { Log("신청 채널을 만들지 못했습니다 (봇에 '채널 관리' 권한 필요): " + ex.Message); return; }
        }
        if (baseCh == null) return;
        int off = 0;
        foreach (var g in Orchestrator.SaveGames)
        {
            off++;
            ITextChannel ch = g == BaseGame ? baseCh : ulong.TryParse(db.Get(kv + g), out var id) && id != 0 ? guild.GetTextChannel(id) : null;
            if (ch == null && !orch.Games.Contains(g)) continue;   // 다루지 않는 게임은 채널을 만들지 않음 (예전에 만든 것만 ⚫)
            if (ch == null)
            {
                try
                {
                    ch = await guild.CreateTextChannelAsync("교환신청-" + Orchestrator.GameCode(g), p => { p.CategoryId = baseCh.CategoryId; p.Position = baseCh.Position + off; p.Topic = $"{Orchestrator.GameKo(g)} 포켓몬 신청 (/교환)" + (test ? " · 테스트" : ". 투표로 다른 게임과 번갈아 교환합니다."); });
                    foreach (var ow in baseCh.PermissionOverwrites)
                        try
                        {
                            if (ow.TargetType == PermissionTarget.Role && guild.GetRole(ow.TargetId) is { } role) await ch.AddPermissionOverwriteAsync(role, ow.Permissions);
                            else if (ow.TargetType == PermissionTarget.User && guild.GetUser(ow.TargetId) is { } gu) await ch.AddPermissionOverwriteAsync(gu, ow.Permissions);
                        }
                        catch { }
                    db.Set(kv + g, ch.Id.ToString()); Log($"{(test ? "테스트 " : "")}{Orchestrator.GameKo(g)} 신청 채널을 만들었습니다 ({ch.Id})");
                }
                catch (Exception ex) { Log($"{Orchestrator.GameKo(g)} 신청 채널을 만들지 못했습니다 (봇에 '채널 관리' 권한 필요): " + ex.Message); }
            }
            await Name(ch, (orch.Games.Contains(g) ? On(g) : "⚫") + "교환신청-" + Orchestrator.GameCode(g));
        }
    }
    if (client.GetGuild(cfg.Discord.GuildId) is { } mg) await Setup(mg, MainChanOf(BaseGame), "main_chan_", false);
    if (cfg.Discord.TestGuildId != 0 && client.GetGuild(cfg.Discord.TestGuildId) is { } tg) await Setup(tg, cfg.Discord.TestChannelId, "test_chan_", true);
}
// ---- 게임 전환 투표 채널: 봇 글 하나(임베드 + 버튼)를 계속 고쳐 씀. 사람들은 버튼이나 /전환투표로만 참여 (글은 못 씀 → 패널이 묻히지 않게)
// 테스트 전용: 투표 채널도 테스트 서버에 (본 서버 투표판은 같은 모양으로 보여 주되 잠금)
bool VoteOnTest() => cfg.Discord.TestOnly && cfg.Discord.TestGuildId != 0;
// 투표판 두 곳: test=true 테스트 서버, false 본 서버. 테스트 중에는 본 서버 투표판도 같은 모양으로 보여 주되 버튼은 잠금
string VK(string k, bool test) => test ? k + "_test" : k;
ulong VoteChannelId(bool test) => !test && cfg.Vote.ChannelId != 0 ? cfg.Vote.ChannelId : (ulong.TryParse(db.Get(VK("vote_channel", test)), out var v) ? v : 0);
// 투표판에 누가 눌렀는지 보여 줌 (멘션은 이름으로 보일 뿐 알림은 가지 않음)
string Who(IEnumerable<ulong> us) { var l = us.ToList(); return l.Count == 0 ? "" : " · " + string.Join(" ", l.Select(u => $"<@{u}>")); }
// 지난 전환 투표: 다음 전환이 정해질 때까지 투표판 아래에 남김
string LastVoteLine()
{
    if (orch.LastVote is not { } lv || orch.SwitchRequested) return "";
    return $"\n\n📜 지난 투표 ({lv.at.ToLocalTime():MM-dd HH:mm}) {Orchestrator.GameKo(lv.from)} → **{Orchestrator.GameKo(lv.to)}**:{Who(lv.voters)}";
}
(Embed, MessageComponent) VotePanel(bool preview = false)
{
    // 바꿀 게임을 골라 누름 (게임마다 따로 셈). 누가 눌렀는지는 보이지 않게 인원만
    var cur = Orchestrator.GameKo(orch.Game); var other = Orchestrator.GameKo(orch.OtherGame);
    // 본 서버 미리보기(테스트 중)는 테스트용 1명이 아니라 본 서버 정원(설정 Vote.Needed)으로 보여 줌
    int need = preview ? Math.Max(1, cfg.Vote.Needed) : orch.VoteNeeded;
    bool cool = DateTime.UtcNow < orch.VoteOpenAt;
    var byG = orch.VotesByGame();
    // 게임마다 필요한 표: 바로 전 게임·그 전 게임은 설정(Vote.ExtraForPrevious/ExtraForRecent)만큼 더 (미리보기는 본 서버 정원 기준으로 같은 만큼 더함)
    int NeedOf(string g) => need + (orch.VoteNeededFor(g) - orch.VoteNeeded);
    int exP = Math.Max(0, cfg.Vote.ExtraForPrevious), exR = Math.Max(0, cfg.Vote.ExtraForRecent);
    string extraTxt = exP > 0 || exR > 0 ? $"(" + string.Join(", ", new[] { exP > 0 ? $"바로 전에 했던 게임은 {need + exP}명" : null, exR > 0 ? $"그 전에 했던 게임은 {need + exR}명" : null }.Where(x => x != null)) + ")" : "";
    string body = $"지금 교환 중: **{cur}**\n\n바꾸고 싶은 게임 버튼을 누르세요. 한 게임이 **{need}명**{extraTxt}을 모으면 {cur} 신청을 마감하고, 이미 받은 신청을 모두 끝낸 뒤 그 게임으로 바꿔요.\n게임마다 한 표씩 넣을 수 있고, 같은 버튼을 다시 누르면 그 표만 취소돼요." + (cfg.Vote.CooldownHours > 0 && need > 1 ? $" 게임을 바꾼 뒤 **{cfg.Vote.CooldownHours:0.#}시간** 동안은 다시 바꿀 수 없어요." : "") + "\n\n";
    if (byG.Count == 0) body += "이 봇은 지금 한 게임만 다뤄요.";
    else if (!orch.VoteMode) body += "⏸ 지금은 투표를 쉬고 있어요.";
    else if (orch.SwitchRequested) body += $"✅ 투표 완료! **{other}**로 바꾸는 중이에요 (남은 신청 {orch.LeftBeforeSwitch}건 처리 후).";
    else if (cool) body += $"⏳ **{Orchestrator.Kst(orch.VoteOpenAt)}**부터 투표할 수 있어요.";
    else if (preview) body += "🛠 지금은 봇 점검(테스트) 중이라 투표를 잠시 쉬어요. 점검이 끝나면 여기서 바로 투표할 수 있어요.";
    else body += string.Join("\n", byG.Select(kv => { int nd = NeedOf(kv.Key); return $"**{Orchestrator.GameKo(kv.Key)}** " + string.Concat(Enumerable.Range(0, nd).Select(i => i < kv.Value.Count ? "🟦" : "⬜")) + $" {kv.Value.Count}/{nd}" + (nd > need ? " · 최근에 한 게임" : "") + Who(kv.Value); }));
    if (orch.SwitchRequested && byG.TryGetValue(orch.OtherGame, out var won)) body += $"\n투표한 사람:{Who(won)}";
    if (orch.SideGames.Count > 0) body += $"\n\n🔀 지금 같이 받는 게임: " + string.Join(" · ", orch.SideGames.Select(x => $"**{Orchestrator.GameKo(x)}**" + (orch.SideClosing(x) ? " (마감 중)" : ""))) + " — 신청이 많아지면 마감될 수 있어요.";
    body += LastVoteLine();
    var eb = new EmbedBuilder().WithTitle("🔁 게임 전환 투표").WithColor(orch.SwitchRequested ? new Color(0xFAA61A) : new Color(0x5865F2))
        .WithDescription(body).WithFooter($"{cur} 교환 시작 {Orchestrator.Kst(orch.GameSince)} · HomeBot");
    bool can = orch.VoteMode && !orch.SwitchRequested && !cool && !preview;
    var cb = new ComponentBuilder();
    foreach (var (g, l) in byG) cb.WithButton($"{Orchestrator.GameKo(g)} ({l.Count}/{NeedOf(g)})", "vote_switch:" + g, ButtonStyle.Primary, disabled: !can);
    return (eb.Build(), cb.Build());
}
async Task<ITextChannel> VoteChannel(bool tv)
{
    var guild = client.GetGuild(tv ? cfg.Discord.TestGuildId : cfg.Discord.GuildId); if (guild == null) return null;
    var id = VoteChannelId(tv);
    if (id != 0) return guild.GetTextChannel(id);
    var sv = (tv ? TestChanOf(BaseGame) : MainChanOf(BaseGame)) is var bid && bid != 0 ? guild.GetTextChannel(bid) : null;
    try
    {
        var made = await guild.CreateTextChannelAsync("게임전환-투표", p => { if (sv != null) { p.CategoryId = sv.CategoryId; p.Position = sv.Position + 2; } p.Topic = "버튼을 눌러 교환할 게임을 바꾸자고 투표해요 (/전환투표 도 가능)"; });
        if (sv != null)
            foreach (var ow in sv.PermissionOverwrites)
            {
                try
                {
                    if (ow.TargetType == PermissionTarget.Role && guild.GetRole(ow.TargetId) is { } role) await made.AddPermissionOverwriteAsync(role, ow.Permissions);
                    else if (ow.TargetType == PermissionTarget.User && guild.GetUser(ow.TargetId) is { } gu) await made.AddPermissionOverwriteAsync(gu, ow.Permissions);
                }
                catch { }
            }
        // 일반 사용자는 글 쓰기 막음 (봇은 허용)
        try
        {
            var ev = made.GetPermissionOverwrite(guild.EveryoneRole) ?? OverwritePermissions.InheritAll;
            await made.AddPermissionOverwriteAsync(guild.EveryoneRole, ev.Modify(sendMessages: PermValue.Deny, createPublicThreads: PermValue.Deny, addReactions: PermValue.Deny));
            await made.AddPermissionOverwriteAsync(guild.CurrentUser, new OverwritePermissions(viewChannel: PermValue.Allow, sendMessages: PermValue.Allow, embedLinks: PermValue.Allow, readMessageHistory: PermValue.Allow));
        }
        catch (Exception ex) { Log("투표 채널 권한 설정 실패: " + ex.Message); }
        db.Set(VK("vote_channel", tv), made.Id.ToString()); Log($"게임 전환 투표 채널을 만들었습니다 ({made.Id})");
        return made;
    }
    catch (Exception ex) { Log("투표 채널을 만들지 못했습니다 (봇에 '채널 관리' 권한 필요): " + ex.Message); db.Set(VK("vote_channel", tv), "1"); return null; }
}
async Task RefreshVotePanel(bool force = false)
{
    if (client.ConnectionState != ConnectionState.Connected) return;
    await voteGate.WaitAsync();
    try
    {
        var targets = VoteOnTest() ? new[] { (test: true, preview: false), (test: false, preview: true) } : new[] { (test: false, preview: false) };
        var sigAll = "";
        foreach (var (test, preview) in targets)
        {
            var (e, comps) = VotePanel(preview);
            sigAll += e.Description + e.Title + comps.Components.Count + orch.Game + "|";
        }
        if (!force && sigAll == voteSig) return;
        foreach (var (test, preview) in targets)
        {
            try
            {
                var (e, comps) = VotePanel(preview);
                var ch = await VoteChannel(test); if (ch == null) continue;
                IUserMessage msg = null;
                if (ulong.TryParse(db.Get(VK("vote_msg", test)), out var mid) && mid != 0) { try { msg = await ch.GetMessageAsync(mid) as IUserMessage; } catch { } }
                if (msg != null) await msg.ModifyAsync(m => { m.Embed = e; m.Components = comps; m.Content = ""; });
                else { msg = await ch.SendMessageAsync(embed: e, components: comps); db.Set(VK("vote_msg", test), msg.Id.ToString()); }
            }
            catch (Exception ex) { Log($"투표 패널 갱신 실패({(test ? "테스트" : "본")} 서버): " + ex.Message); }
        }
        voteSig = sigAll;
    }
    catch (Exception ex) { Log("투표 패널 갱신 실패: " + ex.Message); }
    finally { voteGate.Release(); }
}
async Task AfterVote(bool triggered)
{
    await RefreshVotePanel();
    if (!triggered) return;
    var other = Orchestrator.GameKo(orch.OtherGame); var cur = Orchestrator.GameKo(orch.Game); var left = orch.LeftBeforeSwitch;
    var txt = $"🔁 전환 투표 {orch.VoteNeededFor(orch.OtherGame)}명 달성! **{cur}** 신청은 여기서 마감하고, " + (left > 0 ? $"남은 {left}건을 끝낸 뒤 " : "") + $"**{other}** 교환으로 바꿉니다. 바뀌면 채널 이름의 🟢로 알려 드려요.";
    foreach (var chId in VoteOnTest() ? Orchestrator.SaveGames.Select(TestChanOf).Where(x => x != 0).ToArray() : OrderChannels())
        if (await client.GetChannelAsync(chId) is IMessageChannel ach) { try { await ach.SendMessageAsync(txt); } catch (Exception ex) { Log("전환 공지 실패: " + ex.Message); } }
    await AdminAlertDm($"전환 투표 달성: {cur} → {other} (남은 {left}건)");
}
async Task AdminAlertDm(string t) { foreach (var a in cfg.Discord.AdminIds) await Dm(a, "🔁 [HomeBot] " + t); }
client.Ready += async () => { _ = Task.Run(async () => { try { await Task.Delay(3000); await RefreshVotePanel(force: true); } catch { } }); await Task.CompletedTask; };
// 쿨타임이 끝나거나 게임이 바뀌면 패널이 저절로 바뀌게 30초마다 확인 (바뀐 게 있을 때만 수정)
_ = Task.Run(async () =>
{
    while (!loopCts.IsCancellationRequested)
    {
        try { await Task.Delay(30000, loopCts.Token); } catch { break; }
        try { await RefreshVotePanel(); } catch { }
    }
});
// 신청 열림/마감 알림: 지금 게임 채널에 한 번씩 (상태가 바뀔 때만, kv ann_state에 마지막 알림 상태를 기억해 재시작해도 겹치지 않게)
async Task AnnounceGameState()
{
    if (client.ConnectionState != ConnectionState.Connected) return;
    var g = orch.Game; bool open = orch.AcceptingOrders;
    var state = $"{g}:{(open ? "open" : "closed")}";
    var last = db.Get("ann_state");
    if (last == state) return;
    db.Set("ann_state", state);
    if (last.Length == 0 && !open) return;   // 처음 켰을 때 마감 상태면 알리지 않음 (열려 있으면 '받기 시작' 한 번)
    if (open && db.Get("ann_side:" + g) == "open") { db.Set("ann_side:" + g, "main"); return; }   // 같이 받던 게임이 메인이 된 것: 이미 '받기 시작'을 알렸으니 겹치지 않게
    string txt = open
        ? $"🟢 **{Orchestrator.GameKo(g)}** 신청을 받기 시작했어요! 이 채널에서 `/교환`으로 신청해 주세요."
        : $"🔴 **{Orchestrator.GameKo(g)}** 신청을 마감했어요." + (orch.SwitchRequested ? $" 이미 받은 신청을 모두 끝낸 뒤 **{Orchestrator.GameKo(orch.OtherGame)}** 교환으로 바뀝니다." + (orch.PreOrderGame != null ? $" {Orchestrator.GameKo(orch.OtherGame)} 신청은 그 게임 채널에서 지금 미리 받아요." : "") : " 이미 받은 신청은 그대로 처리해요.");
    var chans = new[] { cfg.Discord.TestOnly ? 0UL : MainChanOf(g), TestChanOf(g) }.Where(x => x != 0).Distinct();
    foreach (var id in chans)
        if (await client.GetChannelAsync(id) is IMessageChannel ch) { try { await ch.SendMessageAsync(txt); } catch (Exception ex) { Log("신청 알림 실패: " + ex.Message); } }
    Log($"신청 알림: {Orchestrator.GameKo(g)} {(open ? "열림" : "마감")}");
}
// 미리 신청 알림: 전환이 정해져 곧 바뀔 게임 채널에 "지금부터 미리 신청 가능" 한 번 (kv ann_pre)
async Task AnnouncePreOrder()
{
    if (client.ConnectionState != ConnectionState.Connected) return;
    var pg = orch.PreOrderGame ?? "";
    var key = pg.Length > 0 ? $"{orch.Game}>{pg}" : "";
    var last = db.Get("ann_pre");
    if (last == key) return;
    db.Set("ann_pre", key);
    if (pg.Length == 0) return;
    var txt = $"🟡 곧 **{Orchestrator.GameKo(pg)}** 교환으로 바뀌어요! 지금부터 이 채널에서 `/교환`으로 **미리 신청**할 수 있어요. {Orchestrator.GameKo(orch.Game)} 신청이 끝나고 바뀌면 신청한 순서대로 처리해요.";
    var chans = new[] { cfg.Discord.TestOnly ? 0UL : MainChanOf(pg), TestChanOf(pg) }.Where(x => x != 0).Distinct();
    foreach (var id in chans)
        if (await client.GetChannelAsync(id) is IMessageChannel ch) { try { await ch.SendMessageAsync(txt); } catch (Exception ex) { Log("미리 신청 알림 실패: " + ex.Message); } }
    Log($"미리 신청 알림: {Orchestrator.GameKo(pg)}");
}
// 전환 투표가 다시 가능해지면 투표 채널에 한 번 알림 (쿨다운이 끝난 시각을 kv에 기억해 겹치지 않게, 지난 알림은 지움)
async Task AnnounceVoteOpen()
{
    if (client.ConnectionState != ConnectionState.Connected) return;
    var at = orch.VoteOpenAt;
    if (!orch.VoteMode || orch.SwitchRequested || at <= orch.GameSince || DateTime.UtcNow < at) return;   // 쿨다운이 없거나 아직이면 알릴 것 없음
    var key = $"{orch.Game}:{at:O}";
    var last = db.Get("vote_open_ann");
    if (last == key) return;
    db.Set("vote_open_ann", key);
    if (DateTime.UtcNow - at > TimeSpan.FromMinutes(30)) return;   // 봇이 꺼져 있다 한참 뒤에 켜진 경우엔 늦은 알림을 보내지 않음
    var test = VoteOnTest();
    if (await client.GetChannelAsync(VoteChannelId(test)) is not IMessageChannel ch) return;
    try
    {
        if (ulong.TryParse(db.Get(VK("vote_open_msg", test)), out var old) && old != 0)
            try { await ch.DeleteMessageAsync(old); } catch { }
        var m = await ch.SendMessageAsync($"🗳️ 이제 게임 전환 투표를 할 수 있어요! 지금 교환 중: **{Orchestrator.GameKo(orch.Game)}** · 바꾸고 싶은 게임 버튼을 위 투표판에서 눌러 주세요 ({orch.VoteNeeded}명이 모이면 바뀌어요).");
        db.Set(VK("vote_open_msg", test), m.Id.ToString());
        Log("전환 투표 가능 알림");
    }
    catch (Exception ex) { Log("투표 가능 알림 실패: " + ex.Message); }
}
// 게임 동시 운영: 봇이 정한 알림을 그 게임 신청 채널에 (본 서버 + 테스트 서버)
async Task PostToGame(string g, string text)
{
    var chans = new[] { cfg.Discord.TestOnly ? 0UL : MainChanOf(g), TestChanOf(g) }.Where(x => x != 0).Distinct();
    foreach (var id in chans)
        if (await client.GetChannelAsync(id) is IMessageChannel ch) { try { await ch.SendMessageAsync(text); } catch (Exception ex) { Log("게임 알림 실패: " + ex.Message); } }
}
orch.OnGameNotice = PostToGame;
// 같이 받는 게임이 열리고 닫힐 때 그 게임 채널에 알림 (기본 게임은 AnnounceGameState가 맡음) + 채널 이름의 🟢/🔴를 맞춤
string sideSig = null;
async Task AnnounceSideState()
{
    if (client.ConnectionState != ConnectionState.Connected) return;
    foreach (var g in Orchestrator.SaveGames)
    {
        var state = g == orch.Game ? "main" : orch.IsOpen(g) ? "open" : "closed";
        var k = "ann_side:" + g; var last = db.Get(k);
        if (last == state) continue;
        db.Set(k, state);
        if (state == "main" || last == "main" || last.Length == 0 && state == "closed") continue;   // 기본 게임으로 바뀌거나 기본에서 내려온 경우는 AnnounceGameState가 알림
        if (state == "open") await PostToGame(g, $"🟢 **{Orchestrator.GameKo(g)}** 신청을 받기 시작했어요! ({Orchestrator.GameKo(orch.Game)}와 같이 운영) 이 채널에서 `/교환`으로 신청해 주세요.");
        else if (!orch.SideClosing(g)) await PostToGame(g, $"🔴 **{Orchestrator.GameKo(g)}** 신청을 마감했어요. 이미 받은 신청은 그대로 처리해요.");
        Log($"같이 받는 게임 알림: {Orchestrator.GameKo(g)} {(state == "open" ? "열림" : "마감")}");
    }
    var sig = orch.Game + "|" + string.Join(",", Orchestrator.SaveGames.Where(orch.IsOpen));
    if (sig != sideSig) { var first = sideSig == null; sideSig = sig; if (!first) await SetupGameChannels(); }
}
_ = Task.Run(async () =>
{
    while (!loopCts.IsCancellationRequested)
    {
        try { await Task.Delay(5000, loopCts.Token); } catch { break; }
        try { await AnnounceGameState(); } catch { }
        try { await AnnounceSideState(); } catch (Exception ex) { Log("동시 운영 알림 오류: " + ex.Message); }
        try { await AnnounceVoteOpen(); } catch { }
        try { await AnnouncePreOrder(); } catch { }
    }
});
// 게임을 바꾼 뒤: 봇 프로세스를 끝내면 관리 창이 새 게임으로 다시 켬 (생성 엔진·스위치 연결이 게임마다 달라서)
orch.OnRestart = async () => { Log("게임 전환으로 봇을 다시 켭니다"); loopCts.Cancel(); await Task.CompletedTask; };
_ = Task.Run(() =>
    {
        // 시작 확인: ftpsrv 목록을 파일로 (경로 문제를 바로 볼 수 있게)
        try { File.WriteAllText(Path.Combine(cfg.DataDir, "ftp_probe.txt"), sw.FtpProbe()); Log("FTP 확인: data/ftp_probe.txt"); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(cfg.DataDir, "ftp_probe.txt"), "실패: " + ex.Message); Log("FTP 확인 실패: " + ex.Message); }
    });
try { await orch.RunAsync(loopCts.Token); } catch (OperationCanceledException) { }
if (cfg.Switch.ScreenOff) sw.ScreenOn();
try { await SetStatusName(NameOff, force: true); } catch { }
await client.StopAsync();
return 0;

// 관리 창 통째로 다시 켜기: 따로 떨어진 PowerShell이 관리 창(봇·워커 포함)을 끄고 같은 파이썬으로 다시 켠 뒤 [전체 시작]까지 누름 (--start)
static string GuiRestart(string dataDir)
{
    try
    {
        var ps = Path.GetFullPath(Path.Combine(dataDir, "gui_restart.ps1"));   // WMI로 띄운 프로세스는 작업 폴더가 System32라 전체 경로로
        File.WriteAllText(ps, """
Start-Transcript -Path (Join-Path $PSScriptRoot 'gui_restart.log') -Force | Out-Null
Start-Sleep -Seconds 2
$all = Get-CimInstance Win32_Process
$gui = $all | Where-Object { $_.CommandLine -like '*manager.pyw*' } | Select-Object -First 1
if (-not $gui) { "관리 창을 찾지 못함"; exit 1 }
$exe = $gui.ExecutablePath
$m = [regex]::Match($gui.CommandLine, '([A-Za-z]:\\[^"]*?manager\.pyw)')
$pyw = $m.Groups[1].Value
taskkill /PID $gui.ProcessId /T /F | Out-Null
Start-Sleep -Seconds 1
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*phone_worker*server.py*' -or $_.CommandLine -like '*HomeBot.Discord*' } | ForEach-Object { taskkill /PID $_.ProcessId /T /F | Out-Null }
Start-Sleep -Seconds 2
"다시 켜기: $exe $pyw"
Start-Process -FilePath $exe -ArgumentList @("`"$pyw`"", "--start") -WorkingDirectory (Split-Path $pyw)
Stop-Transcript | Out-Null
""", new System.Text.UTF8Encoding(true));
        // WMI로 띄워 봇·관리 창과 프로세스 관계가 없게 함 (관리 창을 끌 때 같이 죽지 않게: 10/29 두 번 실패)
        var inner = $"powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps}\"";
        var launch = "Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='" + inner.Replace("'", "''") + "'} | Out-Null";
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{launch.Replace("\"", "\\\"")}\"") { CreateNoWindow = true, UseShellExecute = false }))
            p?.WaitForExit(15000);
        return "관리 창 다시 켜기 시작 (몇 초 뒤 봇·워커가 꺼졌다 다시 켜짐)";
    }
    catch (Exception ex) { return "관리 창 다시 켜기 실패: " + ex.Message; }
}
