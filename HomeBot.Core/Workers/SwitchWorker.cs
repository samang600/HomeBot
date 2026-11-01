using System.Net.Sockets;
using System.Text;
using HomeLive.Core;
using HomeLive.DeviceExecutor;
using PKHeX.Core;
using PKHeX.Core.Injection;
using SysBot.Base;
namespace HomeBot.Core.Workers;

/// <summary>
/// 봇 스위치. 게임은 켜지 않음: 박스는 ftpsrv로 세이브 파일을 고쳐 쓰고(SaveFtp),
/// 버튼·화면 캡처는 sys-botbase 소켓에 직접 명령, HOME 박스 읽기는 HOME Live의 DeviceExecutor.
/// 한 번에 한 작업만 (오케스트레이터가 직렬로 부름).
/// </summary>
public sealed class SwitchWorker : ISwitchWorker, IMacroDevice
{
    private readonly BotConfig cfg; private readonly SaveFile sav; private readonly IVision vision; private readonly Action<string> log;
    private ICommunicatorNX com; private readonly object sync = new();
    /// <summary>세이브 파일 읽기·쓰기·되돌리기를 한 번에 하나씩 (운영자 '세이브 확인'이 넣기 도중의 반쯤 쓴 세이브를 보고 되돌리지 않게). 버튼(sync)과 따로라 FTP 재시도가 버튼을 막지 않음</summary>
    private readonly object saveLock = new();
    private ICommunicatorNX NewCom() => new SysBotMini { IP = cfg.Switch.IP, Port = cfg.Switch.Port };
    /// <summary>게임 동시 운영: 오케스트레이터가 이번 작업(넣기·HOME 이동)의 게임을 정해 둠. 비면 기본 게임(설정)</summary>
    public string GameOverride { get; set; }
    private string G => GameOverride ?? cfg.Switch.Game;
    /// <summary>게임별 봇 세이브 (형식 확인용). 없으면 시작할 때 받은 기본 게임 세이브</summary>
    public Func<string, SaveFile> SavOf { get; set; }
    private SaveFile SavG => SavOf?.Invoke(Orchestrator.Norm(G)) ?? sav;
    public bool Connected => com is { Connected: true };
    private SaveFtp ftp;
    private SaveFtp Ftp => ftp ??= new SaveFtp(cfg.Switch.IP, cfg.Switch.FtpPort, cfg.Switch.FtpUser, cfg.Switch.FtpPass);
    /// <summary>이 게임에서 봇이 쓰기 시작하는 칸 (레츠고: 박스 맨 윗줄은 파티라 건너뜀)</summary>
    public static int FirstSlotOf(BotConfig c, string game = null) => Orchestrator.Norm(game ?? c.Switch.Game) == "GP" ? 6 : 0;
    public string FtpProbe() => Ftp.Probe();
    /// <summary>짝 게임의 실제 버전 (세이브 폴더로 알아냄). FTP가 안 되면 기억해 둔 값(VersionOf), 그것도 없으면 짝의 기본 버전</summary>
    public Func<string, string> VersionOf { get; set; }
    public string VersionNow(string game)
    {
        game = Orchestrator.Norm(game);
        try { if (Ftp.VersionOf(game) is { } v) return v; } catch { }
        try { if (VersionOf?.Invoke(game) is { Length: > 0 } k) return k; } catch { }
        return game;
    }
    // ---------- 게임 로고 자동 등록
    /// <summary>HOME 게임 선택 화면 왼쪽 칸: 게임 이름 띠 / 로고가 있는 범위 (1280x720 기준)</summary>
    private static readonly (int x, int y, int w, int h) TitleBand = (50, 172, 580, 44), LogoBox = (50, 170, 580, 260);
    public Task<bool> LogoExistsAsync(string ver, CancellationToken ct) => vision.TemplateExistsAsync(LogoTemplate(ver), ct);
    /// <summary>게임 이름 글에서 버전 알아내기 (한·영·일 이름 일부로)</summary>
    public static string VersionFromTitle(string text)
    {
        var t = new string((text ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (t.Length == 0) return null;
        (string ver, string[] keys)[] table =
        [
            ("SL", ["스칼렛", "scarlet", "スカーレット", "écarlate", "ecarlate", "karmesin", "escarlata", "scarlatto", "朱"]), ("VL", ["바이올렛", "violet", "バイオレット", "purpur", "púrpura", "purpura", "violetto", "紫"]),
            ("SW", ["소드", "sword", "ソード", "épée", "epee", "schwert", "espada", "spada", "剑", "劍"]), ("SH", ["실드", "shield", "シールド", "bouclier", "schild", "escudo", "scudo", "盾"]),
            ("BD", ["다이아몬드", "diamond", "ダイヤモンド", "diamant", "diamante", "钻石", "鑽石"]), ("SP", ["샤이닝펄", "shiningpearl", "シャイニングパール", "pearl", "perle", "perla", "珍珠"]),
            ("GP", ["피카츄", "pikachu", "ピカチュウ", "皮卡丘"]), ("GE", ["이브이", "eevee", "イーブイ", "évoli", "evoli", "伊布"]),
            ("PLA", ["아르세우스", "arceus", "アルセウス", "阿尔宙斯", "阿爾宙斯"]), ("ZA", ["legendsza", "레전즈za", "레전드za", "legendza"]),
        ];
        foreach (var (ver, keys) in table) if (keys.Any(k => t.Contains(new string(k.Where(char.IsLetterOrDigit).ToArray())))) return ver;
        if ((t.Contains("legends") || t.Contains("레전드") || t.Contains("레전즈")) && t.Contains("za") && !t.Contains("arceus") && !t.Contains("아르세우스")) return "ZA";
        return null;
    }
    /// <summary>HOME 게임 선택 화면(세이브 주인 목록, 커서가 첫 게임)에서 목록을 돌며 이름을 읽고, 아직 로고 그림이 없는 버전의 로고를 잘라 저장.
    /// 반환: (저장한 버전들, 목록에서 본 버전들)</summary>
    public async Task<(List<string> saved, List<string> seen)> ScanLogosAsync(IReadOnlyCollection<string> wanted, CancellationToken ct)
    {
        var saved = new List<string>(); var seen = new List<string>();
        string[] moves = [null, "DRIGHT", "DRIGHT", "DDOWN", "DLEFT", "DLEFT", "DDOWN", "DRIGHT", "DRIGHT", "DDOWN", "DLEFT", "DLEFT"];   // home_open 매크로와 같은 순서 (3칸씩 한 줄, 12칸)
        string lastText = null; int same = 0;
        foreach (var mv in moves)
        {
            ct.ThrowIfCancellationRequested();
            if (mv != null) { await ClickAsync(mv, ct); await Task.Delay(600, ct); }
            var img = await ScreenshotAsync(ct);
            if (img.Length == 0) continue;
            var text = await vision.OcrTitleAsync(img, TitleBand, ct);
            var ver = VersionFromTitle(text);
            log($"로고 등록: 게임 선택 칸 '{text.Replace(" | ", " / ").Trim()}' → {(ver == null ? "모르는 게임" : Orchestrator.VersionKo(ver))}");
            if (text == lastText) { if (++same >= 2) break; } else same = 0;   // 목록 끝이라 커서가 안 움직이면 그만
            lastText = text;
            if (ver == null) continue;
            if (!seen.Contains(ver)) seen.Add(ver);
            if (!wanted.Contains(ver) || saved.Contains(ver)) continue;
            try { var p = await vision.SaveLogoAsync(img, LogoTemplate(ver), LogoBox, ct); saved.Add(ver); log($"로고 등록: {Orchestrator.VersionKo(ver)} → {p}"); }
            catch (Exception ex) { log($"로고 등록 실패 ({Orchestrator.VersionKo(ver)}): {ex.Message}"); }
            if (wanted.All(saved.Contains)) break;
        }
        return (saved, seen);
    }
    /// <summary>HOME 게임 선택 화면 왼쪽 로고 템플릿 이름 (버전마다 하나)</summary>
    public static string LogoTemplate(string ver) => (ver ?? "").Trim().ToUpperInvariant() switch
    {
        "ZA" => "home_gs_za", "PLA" => "home_gs_la", "SH" => "home_gs_sh", "SW" => "home_gs_sw", "GP" => "home_gs_gp", "GE" => "home_gs_ge",
        "BD" => "home_gs_bd", "SP" => "home_gs_sp", "SL" => "home_gs_scarlet", _ => "home_gs_violet",
    };

    public SwitchWorker(BotConfig c, SaveFile s, IVision v, Action<string> logger) { cfg = c; sav = s; vision = v; log = logger; }

    // ---------- 연결 (버튼·화면용. 게임을 켜지 않으므로 게임 확인 없음)
    public async Task ConnectAsync(CancellationToken ct)
    {
        Disconnect();
        await Task.Run(() => { var c = NewCom(); c.Connect(); com = c; log($"스위치 연결: {cfg.Switch.IP} · {Orchestrator.GameKo(cfg.Switch.Game)}"); Dim(); }, ct);
    }
    public void Disconnect() { try { com?.Disconnect(); } catch { } com = null; }

    /// <summary>버튼·화면만 쓰는 연결 (HOME 앱이 켜져 있을 때 등 게임 확인 없이).</summary>
    public void EnsureRaw()
    {
        if (com is { Connected: true }) return;
        var c = NewCom(); c.Connect(); com = c; Dim();
    }

    /// <summary>설정 Switch.ScreenOff면 스위치 화면만 끔 (게임·조작·화면 캡처는 그대로, 배터리·발열이 줄어듦)</summary>
    private void Dim() { if (cfg.Switch.ScreenOff) try { Raw("screenOff", false); } catch { } }
    public void ScreenOff() { try { Raw("screenOff", false); } catch (Exception ex) { log("화면 끄기 실패: " + ex.Message); } }
    public void ScreenOn() { try { if (com is { Connected: true }) Raw("screenOn", false); } catch { } }

    // ---------- 박스 (세이브 파일)
    public async Task<StepResult> InjectBoxAsync(int box, IReadOnlyList<PKM> slots, CancellationToken ct)
        => await Task.Run(() => SaveInject(box, slots), ct);

    public async Task<List<PKM>> ReadBoxAsync(int box, CancellationToken ct)
        => await Task.Run(() => { SaveFile s; lock (saveLock) (s, _, _) = LoadSave(); return Enumerable.Range(0, 30).Select(i => Slot(s, box, i) is { } q ? s.GetBoxSlotAtIndex(q) : null).ToList(); }, ct);

    // ---------- 세이브 모드 (ftpsrv)
    /// <summary>박스 칸 → 세이브 안 절대 칸 번호 (레츠고는 박스가 25칸짜리라 30칸 단위로 셈). 없는 칸이면 null</summary>
    private static int? Slot(SaveFile s, int box, int i)
    {
        int idx = s is SAV7b ? box * 30 + i : box * s.BoxSlotCount + i;
        if (s is not SAV7b && i >= s.BoxSlotCount) return null;
        return idx < s.SlotCount ? idx : null;
    }
    /// <summary>운영자 확인용: 그 게임 세이브의 박스를 읽음 (스위치 줄이 지금 다른 게임을 다루고 있어도 섞이지 않게 게임을 직접 받음)</summary>
    public Task<List<PKM>> ReadBoxOfAsync(string game, int box, CancellationToken ct) =>
        Task.Run(() => { SaveFile s; lock (saveLock) (s, _, _) = LoadSave(game, allowRestore: false); return Enumerable.Range(0, 30).Select(i => Slot(s, box, i) is { } q ? s.GetBoxSlotAtIndex(q) : null).ToList(); }, ct);
    /// <summary>allowRestore: 읽기만 하는 확인(운영자)이면 false — 깨진 세이브를 봐도 되돌리지 않고 오류만</summary>
    private (SaveFile s, string path, byte[] raw) LoadSave(string game = null, bool allowRestore = true)
    {
        game = Orchestrator.Norm(game ?? G);
        var path = Ftp.SavePath(game, cfg.Switch.SaveUser, cfg.Switch.GameUserIndex);
        var raw = Ftp.Retr(path);
        if (!SaveUtil.TryGetSaveFile(raw.ToArray(), out var s, path))
        {
            // 쓰는 도중 연결이 끊겨 세이브가 잘린 경우(11-01 01:42: 4435304 → 4260880바이트): 이 게임의 가장 최근 정상 백업(쓰기 직전 사본)으로 되돌림
            var restored = allowRestore ? RestoreLatestBackup(game, path, raw.Length) : null;
            if (restored == null || !SaveUtil.TryGetSaveFile(restored.ToArray(), out s, path)) throw new InvalidDataException($"세이브를 읽지 못했습니다 ({raw.Length}바이트): {path}");
            raw = restored;
        }
        var want = SavOf?.Invoke(game) ?? sav;
        if (s.GetType() != want.GetType()) throw new InvalidDataException($"세이브 게임이 다릅니다: {s.GetType().Name} (봇 게임 {want.GetType().Name})");
        return (s, path, raw);
    }
    /// <summary>스위치 세이브가 깨져 읽히지 않을 때: 그 게임의 가장 최근 백업 중 정상으로 읽히는 것을 다시 올리고 확인. 성공하면 그 내용, 아니면 null</summary>
    private byte[] RestoreLatestBackup(string game, string path, int badLen)
    {
        try
        {
            var dir = Path.Combine(cfg.DataDir, "save_backup");
            if (!Directory.Exists(dir)) return null;
            foreach (var f in Directory.GetFiles(dir, game + "_*_before.bin").OrderByDescending(x => x).Take(5))
            {
                var b = File.ReadAllBytes(f);
                // 잘린 경우(지금 세이브가 백업보다 짧음)만 되돌림: 게임 업데이트로 세이브 형식이 바뀌어 못 읽는 것이면 옛 백업으로 덮으면 안 됨
                if (b.Length <= badLen) { log($"세이브를 못 읽지만 잘린 것이 아님 ({badLen}바이트 ≥ 백업 {b.Length}바이트) → 되돌리지 않음 (게임 업데이트 등 확인 필요)"); return null; }
                // 주의: TryGetSaveFile은 넘긴 배열을 그 자리에서 복호화함 → 반드시 사본으로 확인 (원본을 올려야 함. 02:01 사례: 복호화된 채 올라감)
                if (!SaveUtil.TryGetSaveFile(b.ToArray(), out _, path)) continue;
                log($"⚠ 스위치 {game} 세이브가 깨져 있음 ({badLen}바이트, 읽기 실패) → 최근 백업 {Path.GetFileName(f)} ({b.Length}바이트)로 되돌림");
                Backup(Ftp.Retr(path), "broken", game);
                Ftp.Stor(path, b);
                var back = Ftp.Retr(path);
                if (back.AsSpan().SequenceEqual(b)) { log("세이브 되돌리기 확인 완료"); return b; }
                log($"세이브 되돌리기 확인 실패 ({back.Length}/{b.Length}바이트)");
                return null;
            }
            log("세이브가 깨져 있지만 되돌릴 정상 백업이 없음");
        }
        catch (Exception ex) { ftp = null; log("세이브 되돌리기 실패: " + ex.Message); }
        return null;
    }

    /// <summary>세이브 사본을 data/save_backup에 남김. 실패하면 false (디스크 꽉 참 등)</summary>
    private bool Backup(byte[] raw, string tag, string game = null)
    {
        try
        {
            var dir = Path.Combine(cfg.DataDir, "save_backup"); Directory.CreateDirectory(dir);
            var g = Orchestrator.Norm(game ?? G);
            File.WriteAllBytes(Path.Combine(dir, $"{g}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{tag}.bin"), raw);
            foreach (var old in Directory.GetFiles(dir, g + "_*.bin").OrderByDescending(x => x).Skip(40)) File.Delete(old);   // 게임마다 최근 40개만
            return true;
        }
        catch (Exception ex) { log("세이브 백업 실패: " + ex.Message); return false; }
    }
    private StepResult SaveInject(int box, IReadOnlyList<PKM> slots)
    {
        try
        {
            lock (saveLock)
            {
                var (s, path, raw) = LoadSave();
                // 백업이 없으면 올리다 끊겼을 때 되돌릴 곳이 없음 → 쓰지 않음 (아직 아무것도 안 바꿈)
                if (!Backup(raw, "before")) return StepResult.Fail("세이브 쓰기 실패: 쓰기 전 백업을 남기지 못해 쓰지 않음 (PC 디스크 공간 확인)");
                int first = FirstSlotOf(cfg, G), put = 0;
                for (int i = first; i < 30; i++)
                {
                    if (Slot(s, box, i) is not { } idx) break;
                    var want = i < slots.Count ? slots[i] : null;
                    if (s.IsBoxSlotOverwriteProtected(idx)) { if (want is { Species: > 0 }) throw new InvalidOperationException($"{box + 1}-{i + 1}번 칸은 잠긴 칸(파티 등)이라 쓸 수 없습니다"); continue; }
                    PKM pk;
                    if (want is not { Species: > 0 }) pk = s.BlankPKM;
                    else
                    {
                        pk = want.Clone();
                        if (pk.GetType() != s.PKMType) pk = EntityConverter.ConvertToType(pk, s.PKMType, out var res) ?? throw new InvalidOperationException($"{box + 1}-{i + 1}: 세이브 형식으로 바꾸지 못함 ({res})");
                        pk.RefreshChecksum(); put++;
                    }
                    s.SetBoxSlotAtIndex(pk, idx, EntityImportSettings.None);   // PID·EC·어버이 등은 그대로 (세이브 트레이너로 고치지 않음)
                }
                if (s is SAV7b b7) b7.FixStoragePreWrite();
                try { s.CurrentBox = box; } catch { }   // HOME이 이 박스를 먼저 보여 주게
                var outb = s.Write().ToArray();
                try
                {
                    // 올리는 중 끊기면 스위치 세이브가 잘린 채 남음 → 다시 연결해 같은 내용을 두 번까지 더 올림
                    for (int attempt = 0; ; attempt++)
                    {
                        try { Ftp.Stor(path, outb); break; }
                        catch (Exception sx) when (attempt < 2) { ftp = null; log($"세이브 올리기 중 끊김 ({sx.Message}) → 다시 올림 ({attempt + 1}/2)"); Thread.Sleep(1500); }
                    }
                    var back = Ftp.Retr(path);
                    if (!back.AsSpan().SequenceEqual(outb)) { Backup(back, "mismatch"); throw new IOException($"올린 세이브를 다시 받아 보니 다릅니다 ({back.Length}/{outb.Length}바이트)"); }
                }
                catch
                {
                    // 끝내 실패: 스위치 세이브가 잘렸거나 다른 내용일 수 있음 → 받아 둔 원래 세이브를 바로 다시 올림 (디스크 백업에 기대지 않음)
                    ftp = null;
                    try { Ftp.Stor(path, raw); var chk = Ftp.Retr(path); log(chk.AsSpan().SequenceEqual(raw) ? "세이브 쓰기 실패 → 원래 세이브로 되돌려 올림" : $"세이브 쓰기 실패 → 원래 세이브로 되돌리기 확인 실패 ({chk.Length}/{raw.Length}바이트)"); }
                    catch (Exception rx) { ftp = null; log("세이브 쓰기 실패 → 원래 세이브 되돌리기도 실패: " + rx.Message); }
                    throw;
                }
                return StepResult.Pass($"세이브 박스 {box + 1}에 {put}마리 씀 ({Path.GetFileName(path)})");
            }
        }
        catch (Exception ex) { ftp = null; return StepResult.Fail("세이브 쓰기 실패: " + ex.Message); }
    }

    // ---------- 버튼·화면 (sys-botbase 원시 명령)
    private string Raw(string cmd, bool expectReply, int timeoutMs = 15000)
    {
        EnsureRaw();
        lock (sync)
        {
            var s = ((SysBotMini)com).Connection;
            s.Send(Encoding.ASCII.GetBytes(cmd + "\r\n"));
            if (!expectReply) return "";
            s.ReceiveTimeout = timeoutMs;
            var sb = new StringBuilder(); var buf = new byte[65536];
            while (true)
            {
                int n = s.Receive(buf);
                if (n <= 0) break;
                int nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                if (nl >= 0) { sb.Append(Encoding.ASCII.GetString(buf, 0, nl)); break; }
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
            return sb.ToString().Trim();
        }
    }
    private bool batteryUnsupported;
    /// <summary>배터리 %: sys-botbase 'charge' 명령 (지원하지 않는 버전이면 답이 없어 한 번 실패 뒤로는 묻지 않음)</summary>
    public Task<int?> BatteryAsync(CancellationToken ct) => Task.Run<int?>(() =>
    {
        if (batteryUnsupported || !Connected) return null;
        try
        {
            var r = Raw("charge", true, 5000);
            if (int.TryParse(r.Trim(), out var v) && v is >= 0 and <= 100) return v;
            batteryUnsupported = true; log($"스위치 배터리를 읽지 못함 (답: '{r}') → 배터리 표시 끔"); return null;
        }
        catch (Exception ex) { batteryUnsupported = true; log("스위치 배터리를 읽지 못함 (sys-botbase가 'charge'를 지원하지 않는 듯): " + ex.Message); return null; }
    }, ct);
    public Task ClickAsync(string button, CancellationToken ct) => Task.Run(() => Raw($"click {button}", false), ct);
    public Task TouchAsync(int x, int y, CancellationToken ct) => Task.Run(() => Raw($"touch {x} {y}", false), ct);
    /// <summary>운영자용: sys-botbase 원시 명령 (detachController 등)</summary>
    public Task RawCommandAsync(string cmd, CancellationToken ct) => Task.Run(() => Raw(cmd, false), ct);
    public async Task HoldAsync(string button, int ms, CancellationToken ct) { await Task.Run(() => Raw($"press {button}", false), ct); await Task.Delay(ms, ct); await Task.Run(() => Raw($"release {button}", false), ct); }
    public async Task StickAsync(string stick, short x, short y, int ms, CancellationToken ct) { await Task.Run(() => Raw($"setStick {stick} {x} {y}", false), ct); await Task.Delay(ms, ct); await Task.Run(() => Raw($"setStick {stick} 0 0", false), ct); }
    /// <summary>화면 캡처. 게임이 켜지는 중 등 캡처가 안 되는 순간에는 빈 배열 (= 화면 확인 실패로 처리하고 다시 시도).</summary>
    public Task<byte[]> ScreenshotAsync(CancellationToken ct) => Task.Run(() =>
    {
        try { var hex = Raw("pixelPeek", true, 30000); return hex.Length > 100 && hex.Length % 2 == 0 ? Convert.FromHexString(hex) : Array.Empty<byte>(); }
        catch (Exception ex) when (ex is FormatException or System.Net.Sockets.SocketException) { log("화면 캡처 실패: " + ex.Message); try { Disconnect(); } catch { } return Array.Empty<byte>(); }
    }, ct);

    // ---------- 매크로
    private Macro LoadMacro(string name)
    {
        var p = Path.Combine(cfg.Switch.MacroDir, name + ".json");
        if (!File.Exists(p)) throw new FileNotFoundException("매크로 파일이 없습니다: " + p);
        return Macro.Load(p);
    }
    /// <summary>두 번째 HOME 계정을 채울 때만: HOME을 켤 스위치 사용자 순서 (없으면 설정값)</summary>
    public int? HomeUserOverride { get; set; }
    /// <summary>HOME 박스에 아직 교환 안 끝난 봇 개체가 있는 칸 (놓아주지 않고 피해서 옮김). 비어 있으면 예전처럼 1번 칸부터</summary>
    public HashSet<int> KeepHomeCells { get; set; } = new();
    /// <summary>남은 봇 개체들 (HOME 데이터에서 실제 칸을 찾음). KeepFound: KeepPks 순번 → 찾은 칸</summary>
    public List<PKM> KeepPks { get; set; } = new();
    public Dictionary<int, int> KeepFound { get; private set; } = new();
    public bool KeepUnverified { get; private set; }
    /// <summary>기록상 칸과 찾은 칸이 달라 둘 다 지켰음 → 어느 쪽이 맞는지 모르니 기록을 고치지 않음</summary>
    public bool KeepAmbiguous { get; private set; }
    /// <summary>마지막 HOME 읽기에서 다른 위치(heap+0x715FC0)로 읽은 결과</summary>
    public List<HomeSlot> LastAltRead { get; private set; } = new();
    /// <summary>마지막 이동에서 게임 칸 순서대로 각 개체가 놓인 HOME 칸 (0부터)</summary>
    public List<int> LastHomeSlots { get; private set; } = new();

    public Task<StepResult> RunMacroAsync(string name, IDictionary<string, string> vars, CancellationToken ct) => RunMacroAsync(name, vars, ct, null);
    public async Task<StepResult> RunMacroAsync(string name, IDictionary<string, string> vars, CancellationToken ct, string stopAt)
    {
        await Task.Run(EnsureRaw, ct);
        var m = LoadMacro(name);
        var all = new Dictionary<string, string>
        {
            ["homeUser"] = (HomeUserOverride ?? cfg.Switch.HomeUserIndex).ToString(), ["gameUser"] = cfg.Switch.GameUserIndex.ToString(),
            ["userUp"] = Math.Max(0, (HomeUserOverride ?? cfg.Switch.HomeUserIndex) - cfg.Switch.GameUserIndex).ToString(),
            ["userDown"] = Math.Max(0, cfg.Switch.GameUserIndex - (HomeUserOverride ?? cfg.Switch.HomeUserIndex)).ToString(),
            // 게임 선택 화면이 이미 세이브 주인의 목록인지: 주인 이름을 글자로 확인 (이름을 모르면 예전 그림 템플릿)
            ["ownerTpl"] = "@text:740,140,430,65:" + (cfg.Switch.SaveUser ?? "").Trim(),   // 이름이 비면 늘 사용자 바꾸기 단계로
            ["gsTpl"] = LogoTemplate(VersionNow(G)),
        };
        foreach (var kv in vars ?? new Dictionary<string, string>()) all[kv.Key] = kv.Value;   // 부르는 쪽 값이 우선
        vars = all;
        var r = await new MacroRunner(this, vision, cfg.ShotDir, log) { StopAt = stopAt }.RunAsync(m, vars, ct);
        Dim();   // HOME 버튼·게임 실행으로 화면이 다시 켜질 수 있음
        log($"매크로 {name}: {(r.Ok ? "완료" : "실패 · " + r.Message)}");
        return r;
    }
    /// <summary>
    /// 게임 박스(1부터) → HOME 박스(1부터). 기록한 흐름 그대로: 게임 저장 → HOME 앱 → 게임 연동 → 박스 화면에서 한 마리씩 옮김.
    /// 끝나면 HOME 앱 박스 화면에 머묾 (저장 전) → home_save 매크로가 저장하고 HOME을 닫음 돌아감.
    /// </summary>
    public async Task<StepResult> MoveBoxToHomeAsync(int gameBox, int homeBox, CancellationToken ct, IReadOnlyList<int> knownSlots = null)
    {
        // 1) 옮길 칸: 방금 넣은 칸을 알면 그대로 (박스를 다시 읽지 않아 빨리 시작), 모르면 게임이 켜져 있을 때 박스를 읽음
        List<int> from;
        if (knownSlots != null && knownSlots.Count > 0) from = knownSlots.ToList();
        else
        {
            if (!Connected) await ConnectAsync(ct);
            var mons = await ReadBoxAsync(gameBox - 1, ct);
            from = Enumerable.Range(0, mons.Count).Where(i => mons[i] != null && mons[i].Species != 0).ToList();
        }
        if (from.Count == 0) return StepResult.Pass("옮길 포켓몬 없음");
        LastHomeSlots = new();
        // 2) 게임 저장 → HOME 앱 → 게임 연동 → 박스 화면
        var r = await RunMacroAsync("home_open", new Dictionary<string, string>(), ct);
        if (!r.Ok) { r.Message = "[열기] " + r.Message; return r; }   // 아직 아무것도 옮기기 전 → 다시 해도 안전
        // 3) 놓을 HOME 칸
        //  깡계(free): 항상 HOME 1번 칸부터 '바꾸기' — 그 칸에 있던 포켓몬(GTS로 받은 열쇠 포켓몬)은 게임 박스로 내려오고, 다음 주입 때 덮어써짐
        //  프리미엄: HOME Live로 빈 칸을 읽어 거기에
        List<int> free;
        if (cfg.Home.IsFree) free = null;   // 깡계는 BulkMoveAsync가 칸을 정함
        else
        {
            List<HomeSlot> home;
            try { home = await ReadHomeBoxesAsync(homeBox, homeBox, ct); }
            catch (Exception ex) { return StepResult.Fail("HOME 박스 읽기 실패: " + ex.Message); }
            var used = home.Select(h => h.Slot).ToHashSet();
            free = Enumerable.Range(0, 30).Where(i => !used.Contains(i)).ToList();
            if (free.Count < from.Count) return StepResult.Fail($"HOME 박스 {homeBox}에 빈 칸이 {free.Count}개뿐 ({from.Count}마리 필요)");
        }
        // 4) 한 마리씩: 커서는 HOME 박스 첫 칸에서 시작. 왼쪽 HOME 박스 열 0~5, 오른쪽 게임 박스 열 6~11 (오른쪽으로 계속 누르면 넘어감)
        int cr = 0, cc = 0;
        async Task Go(int row, int col)
        {
            for (; cc < col; cc++) { await ClickAsync("DRIGHT", ct); await Task.Delay(85, ct); }
            for (; cc > col; cc--) { await ClickAsync("DLEFT", ct); await Task.Delay(85, ct); }
            for (; cr < row; cr++) { await ClickAsync("DDOWN", ct); await Task.Delay(85, ct); }
            for (; cr > row; cr--) { await ClickAsync("DUP", ct); await Task.Delay(85, ct); }
        }
        // 4-0) 깡계 + 2마리 이상: 여러 마리 함께 놓아주기 → 범위 선택으로 한 번에 옮기기 (기록 20261024_181440)
        // 1마리여도 같은 방식: 받은 포켓몬은 '함께 놓아주기'로 한 번에, 옮기기는 범위 선택(1칸짜리 네모)으로 (한 마리씩 놓아주면 칸마다 8초)
        if (cfg.Home.IsFree && from.Count >= 1)
            return await BulkMoveAsync(from, gameBox, homeBox, ct);
        // 4-1) 프리미엄: 빈 칸에 한 마리씩 (깡계는 위에서 끝남)
        for (int k = 0; k < from.Count; k++)
        {
            int g = from[k], h = free[k];
            await Go(g / 6, 6 + g % 6);
            await ClickAsync("A", ct); await Task.Delay(500, ct);
            var mshot = await ScreenshotAsync(ct);
            var (menu, _, _, _) = await vision.MatchAsync(mshot, "home_move_menu", 0.8, ct);
            if (!menu) { try { if (mshot.Length > 0) { Directory.CreateDirectory(cfg.ShotDir); File.WriteAllBytes(Path.Combine(cfg.ShotDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_move_nomenu.jpg"), mshot); } } catch { } await ClickAsync("B", ct); return StepResult.Fail($"게임 박스 {g + 1}번 칸에서 메뉴가 열리지 않음 (빈 칸이거나 화면이 다름)"); }
            await ClickAsync("A", ct); await Task.Delay(400, ct);   // 이동한다
            await Go(h / 6, h % 6);
            await ClickAsync("A", ct); await Task.Delay(600, ct);   // 놓기
            var after = await ScreenshotAsync(ct);
            if (after.Length > 0 && (await vision.MatchAsync(after, "home_swap_blocked", 0.85, ct)).found)
            {
                await ClickAsync("B", ct); await Task.Delay(500, ct); await ClickAsync("B", ct);
                return StepResult.Fail("HOME 칸에 이 게임으로 못 가는 포켓몬이 있어 옮기지 못함");
            }
            log($"HOME 이동: 게임 {gameBox}-{g + 1} → HOME {homeBox}-{h + 1}");
            LastHomeSlots.Add(h);
        }
        return StepResult.Pass($"{from.Count}마리 옮김 (저장 전)");
    }

    /// <summary>HOME 박스 화면 칸 중심 (1280×720). 열 0~5 = 왼쪽 HOME 박스, 6~11 = 오른쪽 게임 박스</summary>
    private static (int x, int y) CellXY(int row, int col) => ((int)(95 + 92.2 * (col % 6)) + (col >= 6 ? 628 : 0), 163 + 75 * row);

    /// <summary>HOME 박스 찬 칸에 차례로 커서를 올려 화면 아래 정보 칸의 숫자(레벨·능력치)를 읽고, 남은 봇 개체(KeepPks)와 맞는 칸을 찾음.
    /// 레벨이 맞으면 2점, 능력치 하나 맞을 때마다 1점 → 7점 이상(8점 만점)이고 그 칸에 7점 넘는 다른 개체가 없을 때만 그 개체. 모두 찾으면 바로 멈춤.
    /// (5점이면 성격만 다른 같은 종 둘이 서로 바뀌어 기록될 수 있음 · 커서가 한 칸 밀리면 모든 칸이 어긋나므로 커서 위치를 화면으로 확인한 칸만 읽음)</summary>
    private async Task<(HashSet<int> keep, Dictionary<int, int> found, bool ambiguous)> ScanKeepByScreenAsync(bool[] busy, Func<int, int, Task> go, Func<Task<(bool found, int cell)>> cursorAt, CancellationToken ct)
    {
        var keep = new HashSet<int>(); var found = new Dictionary<int, int>(); bool ambiguous = false;
        var want = KeepPks.Select((pk, i) => (i, lv: (int)pk.CurrentLevel, st: pk.GetStats(pk.PersonalInfo).Select(x => (int)x).ToArray())).ToList();
        var sw0 = System.Diagnostics.Stopwatch.StartNew(); var seen = new List<string>();
        for (int c = 0; c < 30 && found.Count < KeepPks.Count; c++)
        {
            if (!busy[c]) continue;
            await go(c / 6, c % 6); await Task.Delay(350, ct);
            var (cf, at) = await cursorAt();
            if (cf && at != c) { seen.Add($"{c + 1}:커서{at + 1}"); continue; }   // 버튼이 씹혀 다른 칸을 보고 있음 (cursorAt이 위치를 다시 맞춰 둠)
            var img = await ScreenshotAsync(ct);
            if (img.Length == 0) continue;
            var text = await vision.OcrAsync(img, (10, 568, 1260, 122), ct);
            var nums = System.Text.RegularExpressions.Regex.Matches(text ?? "", @"\d+").Select(m => int.Parse(m.Value)).Where(n => n <= 999).ToList();
            seen.Add($"{c + 1}:[{string.Join(",", nums.Take(8))}]");
            int best = -1, bestSc = 0, hits = 0;
            foreach (var w in want.Where(w => !found.ContainsKey(w.i)))
            {
                var pool = new List<int>(nums); int sc = 0;
                if (pool.Remove(w.lv)) sc += 2;
                foreach (var s in w.st) if (pool.Remove(s)) sc++;
                if (sc >= 7) hits++;
                if (sc > bestSc) { bestSc = sc; best = w.i; }
            }
            if (hits > 1) { ambiguous = true; keep.Add(c); seen[^1] += "(둘 이상 맞음)"; continue; }   // 어느 개체인지 모름 → 칸은 지키되 기록은 고치지 않음
            if (best >= 0 && bestSc >= 7) { found[best] = c; keep.Add(c); }
        }
        log($"남은 봇 개체 화면 확인: {found.Count}/{KeepPks.Count} ({sw0.Elapsed.TotalSeconds:F0}초) " + string.Join(" ", seen.Take(30)));
        return (keep, found, ambiguous);
    }

    private async Task SaveShot(byte[] img, string tag)
    {
        try { if (img == null || img.Length == 0) img = await ScreenshotAsync(CancellationToken.None); if (img.Length > 0) { Directory.CreateDirectory(cfg.ShotDir); File.WriteAllBytes(Path.Combine(cfg.ShotDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{tag}.jpg"), img); } } catch { }
    }

    /// <summary>
    /// 깡계 여러 마리 이동. 커서는 HOME 박스 첫 칸(한 마리 선택 모드)에서 시작.
    /// ① HOME 칸에 남은 포켓몬(지난 GTS에서 받은 것)을 '함께 놓아준다'로 한꺼번에 놓아줌
    /// ② ZR로 범위 선택 → 게임 박스에서 네모로 골라 HOME 첫 칸에 한 번에 내려놓음 → ZL로 한 마리 선택 모드로 되돌림
    /// </summary>
    private async Task<StepResult> BulkMoveAsync(List<int> from, int gameBox, int homeBox, CancellationToken ct)
    {
        int cr = 0, cc = 0;
        async Task Go(int row, int col)
        {
            for (; cc < col; cc++) { await ClickAsync("DRIGHT", ct); await Task.Delay(85, ct); }
            for (; cc > col; cc--) { await ClickAsync("DLEFT", ct); await Task.Delay(85, ct); }
            for (; cr < row; cr++) { await ClickAsync("DDOWN", ct); await Task.Delay(85, ct); }
            for (; cr > row; cr--) { await ClickAsync("DUP", ct); await Task.Delay(85, ct); }
        }
        async Task<bool> See(string tpl, int ms, double thr = 0.85)
        {
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            while (sw0.ElapsedMilliseconds < ms)
            {
                var img = await ScreenshotAsync(ct);
                if (img.Length > 0 && (await vision.MatchAsync(img, tpl, thr, ct)).found) return true;
                await Task.Delay(150, ct);
            }
            return false;
        }
        async Task<bool[]> Busy(int colOffset)
        {
            // 커서가 HOME 첫 칸에 있을 때 찍어야 함 (커서 화살표가 그 위 칸을 가려 '찬 칸'으로 보일 수 있음)
            var res = new bool[30];
            byte[] img = Array.Empty<byte>();
            for (int t = 0; t < 3 && img.Length == 0; t++) { img = await ScreenshotAsync(ct); if (img.Length == 0) await Task.Delay(200, ct); }
            if (img.Length == 0) throw new InvalidOperationException("화면 캡처 실패");
            for (int i = 0; i < 30; i++)
            {
                var (x, y) = CellXY(i / 6, colOffset + i % 6);
                res[i] = (await vision.MatchAsync(img, $"@busy:{x - 30},{y - 28},60,56", 0.10, ct)).found;
            }
            return res;
        }
        async Task<bool[]> Checks()
        {
            var img = await ScreenshotAsync(ct);
            var res = new bool[30];
            if (img.Length == 0) return res;
            for (int i = 0; i < 30; i++)
            {
                var (x, y) = CellXY(i / 6, i % 6);
                res[i] = (await vision.MatchAsync(img, $"@check:{x + 18},{y - 23}", 0.3, ct)).found;
            }
            return res;
        }

        // 남은 봇 개체(교환 덜 끝난 것)가 있으면: 실제 칸을 HOME 데이터로 확인. 확인 못 하면 찬 칸은 하나도 놓아주지 않고 빈 줄에만 채움
        //  (기록된 칸이 틀려 받은 포켓몬을 남기고 봇 개체를 놓아주는 일이 없게, 02:30 사례)
        await Task.Delay(450, ct);
        bool[] busy;
        try { busy = await Busy(0); } catch (Exception ex) { return StepResult.Fail("[열기] HOME 박스 확인 실패: " + ex.Message); }   // 아직 아무것도 안 바꿈 → 게임으로 돌아가 다시 해도 안전
        KeepFound = new(); KeepUnverified = false; KeepAmbiguous = false;
        var dbKeep = new HashSet<int>(KeepHomeCells);   // 기록상 남은 봇 개체 칸 — 화면에 포켓몬이 있으면 무조건 지킴 (HOME 데이터가 예전 상태라 엉뚱한 칸을 가리켜도 놓아주지 않게)
        if (KeepPks.Count > 0)
        {
            List<HomeSlot> rd = new();
            for (int tr = 0; tr < 2 && rd.Count == 0; tr++) { try { rd = await ReadHomeBoxesAsync(homeBox, homeBox, ct); } catch { } if (rd.Count == 0) await Task.Delay(800, ct); }
            var keep = new HashSet<int>();
            for (int i = 0; i < KeepPks.Count; i++)
            {
                var pk = KeepPks[i];
                var h = rd.FirstOrDefault(x => x.Pk != null && x.Pk.Species == pk.Species && x.Pk.EncryptionConstant == pk.EncryptionConstant && !keep.Contains(x.Slot) && x.Slot is >= 0 and < 30);   // 칸 밝기(busy)는 보지 않음: 연한 포켓몬(마빌크 등)은 빈 칸처럼 보임
                if (h != null) { keep.Add(h.Slot); KeepFound[i] = h.Slot; }
            }
            if (KeepFound.Count < KeepPks.Count && LastAltRead.Count > 0)
            {
                var keep2 = new HashSet<int>(); var found2 = new Dictionary<int, int>();
                for (int i = 0; i < KeepPks.Count; i++)
                {
                    var pk = KeepPks[i];
                    var h = LastAltRead.FirstOrDefault(x => x.Pk != null && x.Pk.Species == pk.Species && x.Pk.EncryptionConstant == pk.EncryptionConstant && !keep2.Contains(x.Slot) && x.Slot is >= 0 and < 30);
                    if (h != null) { keep2.Add(h.Slot); found2[i] = h.Slot; }
                }
                log($"남은 봇 개체 찾기: 기본 위치 {KeepFound.Count}/{KeepPks.Count} · 다른 위치 {found2.Count}/{KeepPks.Count}");
                if (found2.Count > KeepFound.Count) { keep = keep2; KeepFound = found2; }
            }
            if (KeepFound.Count < KeepPks.Count)
            {
                // HOME 데이터가 예전 상태라 못 찾음 → 화면으로 확인: 찬 칸마다 커서를 올려 아래 정보 칸(레벨·능력치)을 읽고 남은 봇 개체와 맞춰 봄
                var (k2, f2, amb) = await ScanKeepByScreenAsync(busy, Go, async () => (await SyncCursor(), cc < 6 ? cr * 6 + cc : -1), ct);
                await Go(0, 0); await Task.Delay(200, ct);
                if (amb) KeepAmbiguous = true;
                if (f2.Count > KeepFound.Count || amb) { keep.UnionWith(k2); if (f2.Count > KeepFound.Count) KeepFound = f2; }
            }
            if (KeepFound.Count == KeepPks.Count)
            {
                // 기록상 칸은 화면에 비어 보여도 지킴 (연한 포켓몬은 빈 칸처럼 보임 · 정말 비었으면 그 칸 하나를 덜 쓸 뿐)
                var extra = dbKeep.Where(c => c is >= 0 and < 30 && !keep.Contains(c)).ToList();
                foreach (var c in extra) keep.Add(c);
                KeepAmbiguous |= extra.Any(c => busy[c]);
                KeepHomeCells = keep;
                log($"남은 봇 개체 칸 확인: {string.Join(",", keep.OrderBy(x => x).Select(x => x + 1))}" + (extra.Count > 0 ? $" (기록상 칸 {string.Join(",", extra.Select(x => x + 1))}도 포켓몬이 있어 함께 지킴)" : ""));
            }
            else
            {
                KeepHomeCells = Enumerable.Range(0, 30).Where(i => busy[i]).Concat(dbKeep.Where(c => c is >= 0 and < 30)).ToHashSet(); KeepUnverified = true;
                log($"남은 봇 개체 칸을 HOME 데이터로 확인 못 함 (읽음 {rd.Count}마리) → 찬 칸 {KeepHomeCells.Count}개는 그대로 두고 빈 줄에 채움");
            }
        }

        // 옮길 네모: 게임 박스에서 넣은 칸들을 감싸는 네모 → HOME 박스 첫 칸 기준 같은 모양
        int r0 = from.Min(g => g / 6), r1 = from.Max(g => g / 6), c0 = from.Min(g => g % 6), c1 = from.Max(g => g % 6);
        // 놓을 줄(R): 남은 봇 개체(KeepHomeCells)와 겹치지 않는 가장 위 줄 (같은 계정에 교환이 덜 끝난 개체가 있어도 그 아래에 채움)
        int R = -1;
        for (int rr = 0; rr + (r1 - r0) < 5 && R < 0; rr++)
        {
            bool ok = true;
            for (int r = r0; r <= r1 && ok; r++) for (int c = c0; c <= c1; c++) if (KeepHomeCells.Contains((r - r0 + rr) * 6 + (c - c0))) { ok = false; break; }
            if (ok) R = rr;
        }
        // 두 줄짜리 네모가 들어갈 빈 줄이 없으면: 게임 박스 줄마다 따로(한 줄짜리 네모) 빈 줄에 옮김
        bool split = R < 0;
        var cellOf = new Dictionary<int, int>();
        var groups = new List<(int gr, int gc0, int gc1, int tr)>();
        if (!split) foreach (var g in from) cellOf[g] = (g / 6 - r0 + R) * 6 + (g % 6 - c0);
        else
        {
            var taken = new HashSet<int>(KeepHomeCells);
            foreach (var grp in from.GroupBy(g => g / 6).OrderBy(x => x.Key))
            {
                int gc0 = grp.Min(g => g % 6), gc1 = grp.Max(g => g % 6), tr = -1;
                for (int rr = 0; rr < 5 && tr < 0; rr++)
                    if (Enumerable.Range(gc0, gc1 - gc0 + 1).All(c => !taken.Contains(rr * 6 + (c - gc0)))) tr = rr;
                if (tr < 0) return StepResult.Fail("[열기] HOME 박스에 옮길 자리가 없음 (남은 개체와 겹침)");
                foreach (var g in grp) { cellOf[g] = tr * 6 + (g % 6 - gc0); }
                for (int c = gc0; c <= gc1; c++) taken.Add(tr * 6 + (c - gc0));
                groups.Add((grp.Key, gc0, gc1, tr));
            }
            log($"남은 개체 때문에 줄마다 나눠 옮김: " + string.Join(", ", groups.Select(x => $"게임 {x.gr + 1}줄→HOME {x.tr + 1}줄")));
        }
        int Cell(int g) => cellOf[g];
        var homeCells = split ? groups.SelectMany(x => Enumerable.Range(x.tr * 6, x.gc1 - x.gc0 + 1)).ToList() : new List<int>();
        if (!split) for (int r = r0; r <= r1; r++) for (int c = c0; c <= c1; c++) homeCells.Add((r - r0 + R) * 6 + (c - c0));
        int clear = Math.Max(from.Count, Math.Clamp(cfg.Limits.MoveBatchMax, 1, 30));

        // ① 놓아주기
        var targets = Enumerable.Range(0, 30).Where(i => busy[i] && !KeepHomeCells.Contains(i) && (i < clear + Math.Max(R, 0) * 6 || homeCells.Contains(i) || KeepHomeCells.Count > 0)).ToList();
        if (targets.Count == 1)
        {
            int t = targets[0];
            await Go(t / 6, t % 6);
            var rr = await ReleaseHereAsync(t / 6, t % 6, ct);
            if (!rr.Ok) return rr;
            log($"HOME {homeBox}-{t + 1} 놓아줌");
        }
        else if (targets.Count > 1)
        {
            int f0 = targets[0];
            await Go(f0 / 6, f0 % 6); await Task.Delay(250, ct);
            // 메뉴 → 놓아준다 → '1마리만/함께' 창. 버튼이 가끔 씹혀(메뉴가 늦게 열리거나 아래 칸 이동이 빠짐) 창이 안 뜨면 물러났다가 천천히 다시 시도
            bool asked = false;
            for (int att = 0; att < 3 && !asked; att++)
            {
                await ClickAsync("A", ct);
                if (!await See("home_move_menu", 3000, 0.8)) { await SaveShot(null, "release_nomenu"); return StepResult.Fail("놓아주기: 포켓몬 메뉴가 열리지 않음 (화면 저장됨)"); }
                await Task.Delay(250 + att * 200, ct);
                for (int i = 0; i < 4; i++) { await ClickAsync("DDOWN", ct); await Task.Delay(150 + att * 150, ct); }
                await Task.Delay(att * 200, ct);
                await ClickAsync("A", ct);                                       // 놓아준다
                if (await See("home_release_ask", 3000 + att * 1000)) { asked = true; break; }
                await SaveShot(null, $"release_noask{att + 1}");
                log($"놓아주기: '함께 놓아준다' 창이 안 떠서 물러났다가 다시 시도 ({att + 1}/3)");
                // 열린 것(메뉴·상태 화면·마킹 등)을 닫고 박스 화면으로 돌아오기
                for (int b = 0; b < 4; b++)
                {
                    await ClickAsync("B", ct); await Task.Delay(500, ct);
                    var im = await ScreenshotAsync(ct);
                    if (im.Length > 0 && !(await vision.MatchAsync(im, "home_move_menu", 0.8, ct)).found && !(await vision.MatchAsync(im, "home_release_ask", 0.85, ct)).found
                        && (await vision.MatchAsync(im, "home_boxes", 0.85, ct)).found) break;
                }
                await Task.Delay(400, ct);
            }
            if (!asked) { await ClickAsync("B", ct); await ClickAsync("B", ct); return StepResult.Fail("놓아주기: '함께 놓아준다' 창이 세 번 다 안 뜸 (화면 저장됨)"); }
            await ClickAsync("DDOWN", ct); await Task.Delay(200, ct);
            await ClickAsync("A", ct);                                           // 함께 놓아준다
            if (!await See("home_multi_bar", 3000)) { await SaveShot(null, "multi_nobar"); await ClickAsync("B", ct); await ClickAsync("B", ct); return StepResult.Fail("놓아주기: 여러 마리 고르기 화면이 안 뜸"); }
            foreach (var t in targets.Skip(1)) { await Go(t / 6, t % 6); await Task.Delay(120, ct); await ClickAsync("A", ct); await Task.Delay(200, ct); }
            // 체크가 정확히 고른 칸에만 있는지 확인 (A가 두 번 먹혀 풀린 칸 등은 한 번 더 눌러 바로잡음)
            await Task.Delay(300, ct);
            bool ok = false;
            for (int pass = 0; pass < 2 && !ok; pass++)
            {
                var chk = await Checks();
                var wrong = Enumerable.Range(0, 30).Where(i => chk[i] != targets.Contains(i)).ToList();
                if (wrong.Count == 0) { ok = true; break; }
                log($"놓아주기 체크 바로잡기: {string.Join(",", wrong.Select(i => i + 1))}번 칸");
                foreach (var t in wrong) { await Go(t / 6, t % 6); await Task.Delay(120, ct); await ClickAsync("A", ct); await Task.Delay(250, ct); }
                await Task.Delay(300, ct);
            }
            if (!ok) { await SaveShot(null, "multi_check_bad"); await ClickAsync("B", ct); await Task.Delay(500, ct); await ClickAsync("B", ct); return StepResult.Fail("놓아주기: 체크 표시가 맞지 않아 취소함 (화면 저장됨)"); }
            await ClickAsync("PLUS", ct);                                        // 함께 놓아주기
            if (!await See("home_release_confirm", 3000)) { await ClickAsync("B", ct); await ClickAsync("B", ct); return StepResult.Fail($"놓아주기: '정말 이 {targets.Count}마리를' 확인 창이 안 뜸"); }
            if (!await See("home_release_yes_sel", 800))
            {
                await ClickAsync("DUP", ct); await Task.Delay(250, ct);
                if (!await See("home_release_yes_sel", 1500)) { await SaveShot(null, "multi_confirm"); await ClickAsync("B", ct); await ClickAsync("B", ct); return StepResult.Fail("놓아주기: '놓아준다' 버튼을 고르지 못함"); }
            }
            await ClickAsync("A", ct);                                           // 놓아준다
            if (!await See("home_text_next", 10000, 0.9)) return StepResult.Fail("놓아주기: '바이바이' 글이 나오지 않음");
            await ClickAsync("A", ct);                                           // 한 번만 (커서 칸이 비어 있어 더 누르면 '일람'으로 감)
            var sw1 = System.Diagnostics.Stopwatch.StartNew(); bool closed = false;
            while (sw1.ElapsedMilliseconds < 4000)
            {
                var im = await ScreenshotAsync(ct);
                if (im.Length > 0 && !(await vision.MatchAsync(im, "home_text_next", 0.9, ct)).found) { closed = true; break; }
                await Task.Delay(200, ct);
            }
            if (!closed) return StepResult.Fail("놓아주기: '바이바이' 글이 닫히지 않음");
            await Task.Delay(500, ct);
            log($"HOME {homeBox}: {targets.Count}마리 함께 놓아줌 ({string.Join(",", targets.Select(i => i + 1))}번 칸)");
        }
        // 놓아준 뒤 HOME 칸이 비었는지 확인 (커서는 커서 위치를 HOME 첫 칸으로 되돌린 뒤 찍음)
        await Go(0, 0); await Task.Delay(400, ct);
        try { busy = await Busy(0); } catch (Exception ex) { return StepResult.Fail("HOME 박스 확인 실패: " + ex.Message); }
        var still = homeCells.Where(i => busy[i]).ToList();
        if (still.Count > 0) { await SaveShot(null, "bulk_home_busy"); return StepResult.Fail($"HOME 칸이 비지 않아 옮길 수 없음 ({string.Join(",", still.Select(i => i + 1))}번 칸)"); }

        // ② 범위 선택으로 옮기기
        await ClickAsync("ZR", ct);
        if (!await See("home_mode_range", 2000))
        {
            await ClickAsync("ZR", ct);
            if (!await See("home_mode_range", 2000)) { await SaveShot(null, "range_mode"); return StepResult.Fail("범위 선택 모드로 바꾸지 못함"); }
        }
        async Task MoveRect(int ra, int rb, int ca, int cb, int toRow)
        {
            await Go(ra, 6 + ca); await Task.Delay(200, ct);
            await ClickAsync("A", ct); await Task.Delay(400, ct);                    // 네모 시작
            await Go(rb, 6 + cb); await Task.Delay(200, ct);
            await ClickAsync("A", ct); await Task.Delay(700, ct);                    // 네모 끝 → 집어 듦
            // 든 채로 HOME 쪽으로: 네모 왼쪽 위가 HOME (toRow, 1열)에 오도록 (커서가 네모 어느 모서리에 붙든 왼쪽 6+ca, 위 ra번 → 아래 toRow번)
            for (int i = 0; i < 6 + ca; i++) { await ClickAsync("DLEFT", ct); await Task.Delay(120, ct); }
            for (int i = 0; i < ra; i++) { await ClickAsync("DUP", ct); await Task.Delay(120, ct); }
            for (int i = 0; i < toRow; i++) { await ClickAsync("DDOWN", ct); await Task.Delay(120, ct); }
            await Task.Delay(300, ct);
            await ClickAsync("A", ct); await Task.Delay(900, ct);                    // 내려놓기
        }
        async Task<bool> SyncCursor()
        {
            // 내려놓은 뒤 커서(초록 화살표) 위치를 화면에서 찾아 cr/cc를 맞춤 (다음 네모를 집으러 갈 때 필요)
            for (int t = 0; t < 3; t++)
            {
                var img = await ScreenshotAsync(ct);
                if (img.Length > 0)
                {
                    var (f, _, x, y) = await vision.MatchAsync(img, "home_cursor", 0.75, ct);
                    // 이름표가 화살표 바로 위에 붙거나(맨 윗줄은 주황 머리띠 위) 배경이 달라 못 찾을 때 → 이름표 붙은 화살표 그림으로 한 번 더
                    // (20261028_210423 맨 윗줄 1.0 · 20261026_031929 가운데 줄 0.79, 커서 없는 화면은 0.69 이하)
                    if (!f) (f, _, x, y) = await vision.MatchAsync(img, "home_cursor_top", 0.75, ct);
                    if (f)
                    {
                        int bc = Enumerable.Range(0, 12).OrderBy(c => Math.Abs(CellXY(0, c).x - x)).First();
                        int br = Enumerable.Range(0, 5).OrderBy(r => Math.Abs(CellXY(r, 0).y - (y + 50))).First();
                        cr = br; cc = bc; return true;
                    }
                }
                await Task.Delay(300, ct);
            }
            return false;
        }
        if (!split) await MoveRect(r0, r1, c0, c1, R);
        else
        {
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var gp = groups[gi];
                if (gi > 0 && !await SyncCursor()) { await SaveShot(null, "split_cursor"); await ClickAsync("B", ct); return StepResult.Fail("줄 나눠 옮기기: 커서 위치를 찾지 못함 (화면 저장됨)"); }
                await MoveRect(gp.gr, gp.gr, gp.gc0, gp.gc1, gp.tr);
            }
        }
        // 확인: 게임 박스 칸은 비고, HOME 칸은 찼는지
        bool[] hb, gb;
        try { hb = await Busy(0); gb = await Busy(6); } catch (Exception ex) { return StepResult.Fail("이동 확인 실패: " + ex.Message); }
        var notMoved = from.Where(g => gb[g]).ToList();
        var missing = from.Where(g => !hb[Cell(g)]).ToList();
        // 연한 색 포켓몬(마빌크 등)은 칸 밝기만으로는 빈 칸처럼 보임 → 커서를 그 칸에 올려 아래 정보 칸(이름·레벨 줄)에 무언가 나오는지로 다시 확인
        // (20261031_032057: HOME 1번 칸 마빌크를 '없음'으로 봐 멈췄음. 정보 줄은 빈 칸이면 표준편차 0.3, 포켓몬이면 30)
        if (missing.Count > 0 && notMoved.Count == 0 && await SyncCursor())   // 내려놓은 뒤 커서 위치부터 맞춤
        {
            foreach (var g in missing.ToList())
            {
                int hc = Cell(g);
                await Go(hc / 6, hc % 6); await Task.Delay(450, ct);
                var im = await ScreenshotAsync(ct);
                if (im.Length > 0 && (await vision.MatchAsync(im, "@busy:20,575,1240,28", 0.10, ct)).found)
                { missing.Remove(g); log($"HOME {homeBox}-{hc + 1}: 칸이 흐려 보였지만 정보 칸으로 포켓몬 확인"); }
            }
        }
        if (notMoved.Count > 0 || missing.Count > 0)
        {
            await SaveShot(null, "bulk_move_bad");
            await ClickAsync("B", ct); await Task.Delay(400, ct);
            return StepResult.Fail($"범위 이동 확인 실패 (게임 박스에 남음: {string.Join(",", notMoved.Select(i => i + 1))} / HOME에 없음: {string.Join(",", missing.Select(i => i + 1))}) — 화면 저장됨");
        }
        await ClickAsync("ZL", ct);                                              // 한 마리 선택 모드로 되돌림 (다음 열기 때도 확인함)
        if (!await See("home_boxes", 2000)) log("한 마리 선택 모드로 되돌리기 확인 못 함 (다음 열기 때 다시 맞춤)");
        foreach (var g in from) { log($"HOME 이동: 게임 {gameBox}-{g + 1} → HOME {homeBox}-{Cell(g) + 1}"); LastHomeSlots.Add(Cell(g)); }
        return StepResult.Pass($"{from.Count}마리 범위 이동 (저장 전)");
    }

    /// <summary>커서가 놓인 HOME 칸의 포켓몬 놓아주기 (기록 20261024_025403): A → 놓아준다(아래 4칸) → 1마리만 → 정말? 놓아준다(위) → 바이바이</summary>
    private async Task<StepResult> ReleaseHereAsync(int row, int col, CancellationToken ct)
    {
        async Task<bool> See(string tpl, int ms, double thr = 0.85)
        {
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            while (sw0.ElapsedMilliseconds < ms)
            {
                var img = await ScreenshotAsync(ct);
                if (img.Length > 0 && (await vision.MatchAsync(img, tpl, thr, ct)).found) return true;
                await Task.Delay(150, ct);
            }
            return false;
        }
        // 빈 칸에서 A를 누르면 '일람'으로 넘어가 버림 → 누르기 전에 칸 위 이름표(예: Camerupt(Lv.33))가 떠 있는지로 포켓몬 유무를 판단
        await Task.Delay(450, ct);
        // 포켓몬이 있는지는 그 칸 자체를 보고 판단 (빈 칸은 매끈한 동그라미라 밝기 변화가 거의 없음, 포켓몬이 있으면 크게 변함)
        var (cx, cy) = CellXY(row, col);
        bool busy = false;
        for (int t = 0; t < 3 && !busy; t++)
        {
            var img = await ScreenshotAsync(ct);
            if (img.Length > 0) (busy, _, _, _) = await vision.MatchAsync(img, $"@busy:{cx - 30},{cy - 28},60,56", 0.15, ct);
            if (!busy) await Task.Delay(150, ct);
        }
        if (!busy) return StepResult.Pass("빈 칸");
        bool asked = false;
        for (int att = 0; att < 3 && !asked; att++)
        {
            await ClickAsync("A", ct);
            if (!await See("home_move_menu", 3000, 0.8))
            {
                await SaveShot(null, "release_nomenu");
                return StepResult.Fail("놓아주기: 포켓몬이 있는데 메뉴가 열리지 않음 (화면 저장됨)");
            }
            await Task.Delay(250 + att * 200, ct);
            for (int i = 0; i < 4; i++) { await ClickAsync("DDOWN", ct); await Task.Delay(150 + att * 150, ct); }
            await Task.Delay(att * 200, ct);
            await ClickAsync("A", ct);
            if (await See("home_release_ask", 3000 + att * 1000)) { asked = true; break; }
            await SaveShot(null, $"release1_noask{att + 1}");
            log($"놓아주기: '1마리만 놓아준다' 창이 안 떠서 물러났다가 다시 시도 ({att + 1}/3)");
            for (int b = 0; b < 4; b++)
            {
                await ClickAsync("B", ct); await Task.Delay(500, ct);
                var im = await ScreenshotAsync(ct);
                if (im.Length > 0 && !(await vision.MatchAsync(im, "home_move_menu", 0.8, ct)).found && !(await vision.MatchAsync(im, "home_release_ask", 0.85, ct)).found
                    && (await vision.MatchAsync(im, "home_boxes", 0.85, ct)).found) break;
            }
            await Task.Delay(400, ct);
        }
        if (!asked) { await ClickAsync("B", ct); await ClickAsync("B", ct); return StepResult.Fail("놓아주기: '1마리만 놓아준다' 창이 세 번 다 안 뜸 (화면 저장됨)"); }
        await ClickAsync("A", ct);                                   // 1마리만 놓아준다
        if (!await See("home_release_confirm", 3000)) { await ClickAsync("B", ct); await ClickAsync("B", ct); return StepResult.Fail("놓아주기: 확인 창이 안 뜸"); }
        await ClickAsync("DUP", ct); await Task.Delay(200, ct);     // 기본은 '취소' → 위의 '놓아준다'
        await ClickAsync("A", ct);                                   // 놓아준다
        // '…를 놓아주었다 / 바이바이' 글이 다 나올 때까지(아래 ▼ 표시) 기다렸다가 넘김. 글이 남아 있으면 다음 버튼이 먹히지 않음
        if (!await See("home_text_next", 8000, 0.9)) return StepResult.Fail("놓아주기: '바이바이' 글이 나오지 않음");
        await ClickAsync("A", ct);                                   // 바이바이 넘기기 — 딱 한 번만. (이 뒤 커서는 빈 칸 위라 A를 더 누르면 '일람'으로 넘어감)
        // 글이 닫힐 때까지 버튼 없이 기다리기만 함
        var sw1 = System.Diagnostics.Stopwatch.StartNew(); bool closed = false;
        while (sw1.ElapsedMilliseconds < 4000)
        {
            var im = await ScreenshotAsync(ct);
            if (im.Length > 0 && !(await vision.MatchAsync(im, "home_text_next", 0.9, ct)).found) { closed = true; break; }
            await Task.Delay(200, ct);
        }
        if (!closed) return StepResult.Fail("놓아주기: '바이바이' 글이 닫히지 않음");
        await Task.Delay(500, ct);
        return StepResult.Pass();
    }

    // ---------- HOME Live (스위치 HOME 앱이 켜진 상태)
    public async Task<List<HomeSlot>> ReadHomeBoxesAsync(int fromBox, int toBox, CancellationToken ct)
    {
        // HOME Live는 자기 소켓을 쓰므로 게임용 연결은 잠시 끊음
        Disconnect();
        var exec = new DeviceExecutor<DeviceState>(new DeviceState { Connection = new SwitchConnectionConfig { IP = cfg.Switch.IP, Port = cfg.Switch.Port, Protocol = SwitchProtocol.WiFi }, InitialRoutine = RoutineType.ReadWrite });
        var result = new List<HomeSlot>();
        try
        {
            await exec.RunAsync(ct); await exec.Connect(ct);
            async Task<List<HomeSlot>> ReadAll()
            {
                var res = new List<HomeSlot>();
                for (int b = fromBox; b <= toBox; b++)
                {
                    var data = await exec.ReadBoxData(b - 1, ct);
                    var list = PokeHandler.GenerateEntitiesFromBoxBin(data);
                    for (int i = 0; i < list.Count; i++)
                    {
                        var w = list[i]; if (w?.PKM == null || w.PKM.Species == 0) continue;
                        res.Add(new HomeSlot(b, i, w.PKM, w.Tracker));
                    }
                }
                return res;
            }
            result = await ReadAll();
            // HOME 4.1.0 박스 데이터 위치가 두 곳 알려져 있음 (우리 자동 찾기 heap+0x12F80, 원본 HOME Live heap+0x715FC0).
            // 한쪽이 예전 상태(열 때 불러온 사본 등)일 수 있어 다른 위치도 읽어 둠 → 남은 개체·옮긴 개체 확인에 둘 다 씀
            LastAltRead = new();
            try
            {
                DeviceExecutor<DeviceState>.UseOffset(0x715FC0);
                LastAltRead = await ReadAll();
            }
            catch (Exception ex) { log("HOME 다른 위치 읽기 실패: " + ex.Message); }
            finally { DeviceExecutor<DeviceState>.UseOffset(0); }
            log($"HOME 박스 읽기 비교: 기본 {result.Count}마리 · 다른 위치 {LastAltRead.Count}마리" + string.Concat(LastAltRead.Select(h => $" [{h.Slot + 1}:{h.Pk.Species}]")));
        }
        finally { try { exec.Disconnect(); } catch { } }
        try { EnsureRaw(); } catch { }   // 버튼용 연결 복구 (HOME 앱 상태)
        return result;
    }
}
