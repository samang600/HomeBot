using PKHeX.Core;
using PKHeX.Core.AutoMod;
using PKHeXKR;
namespace HomeBot.Core;

/// <summary>주문 문장 → 합법 개체. 앱의 빠른 생성 엔진(PKHeXKR.Gen)을 봇 세이브 기준으로 돌림.</summary>
public sealed class GenService
{
    public SaveFile Sav { get; private set; }
    public GameVersion Game { get; private set; }
    private BotConfig cfg;
    // 엔진은 전역 조건(EncCond·트레이너 등록·ParseSettings)을 쓰므로 모든 게임을 통틀어 한 번에 하나.
    // 게임이 여럿이면(동시 운영) 작업 직전에 이 게임의 전역 설정을 다시 걸어 둠 (Activate)
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static GenService active;
    private TrainerPref pref;
    public Dictionary<string, string> UserDict { get; } = new();

    public GenService(BotConfig c, string game = null)
    {
        gate.Wait();   // 다른 게임 엔진이 생성 중이면 끝날 때까지 (전역 설정을 바꾸므로)
        try { Init(c, game); } finally { gate.Release(); }
        var dict = Path.Combine(c.DataDir, "dictionary.txt");   // "별칭=정식이름" 한 줄씩
        if (File.Exists(dict)) foreach (var l in File.ReadAllLines(dict)) { var i = l.IndexOf('='); if (i > 0) UserDict[l[..i].Trim()] = l[(i + 1)..].Trim(); }
    }
    private void Init(BotConfig c, string game)
    {
        cfg = c;
        GameInfo.CurrentLanguage = "ko";
        GameStrings ko = null; GenHost.StringsSource = () => ko ??= GameInfo.GetStrings("ko");
        GenHost.ExcludeUntradable = true;   // 교환·HOME 맡기기가 안 되는 개체(라이드 코라이돈·미라이돈 등)는 만들지 않음
        APILegality.AllowTrainerOverride = true; APILegality.SetMatchingBalls = true; APILegality.ForceSpecifiedBall = true;
        APILegality.SetAllLegalRibbons = false; APILegality.Timeout = 45; Legalizer.EnableEasterEggs = false;
        // (트래커 검사는 PKHeX 기본값 그대로)
        // 짝 게임(스칼렛·바이올렛 등)은 설정에 짝 코드만 있음 → 세이브 폴더로 알아낸 실제 버전이 있으면 그걸로 (어버이 기록·조우가 그 버전 기준)
        var ver = VersionOf?.Invoke(game ?? c.Switch.Game);
        Game = Enum.TryParse<GameVersion>(GameCodeToVersion(string.IsNullOrEmpty(ver) ? game ?? c.Switch.Game : ver), true, out var g) ? g : GameVersion.SL;
        Sav = LoadSave();
        ParseSettings.InitFromSaveFileData(Sav);
        // 자동 합법화(ALM)도 봇 트레이너(언어 포함)로 만들게 등록 — 안 하면 ALM 기본값(영어, 어버이 'ALM')이 쓰임
        var t = c.Switch.Trainer;
        pref = new TrainerPref(t.OT, (byte)t.Gender, t.TID, t.SID, Sav.Language > 0 ? Sav.Language : (int)LanguageID.Korean);
        GenTrainers.Register(Sav, _ => null, _ => pref, GenTrainers.SafeLang);
        active = this;
    }

    /// <summary>봇 게임 코드(VL·SH·PLA…) → PKHeX 게임 버전 이름 (설정에 SL·VL처럼 적혀 있으면 그대로)</summary>
    /// <summary>짝 게임 코드 → 실제 버전 코드 (Program이 세이브 폴더 확인 결과로 채움). 없으면 짝의 기본 버전</summary>
    public static Func<string, string> VersionOf { get; set; }
    private static string GameCodeToVersion(string g) => (g ?? "").Trim().ToUpperInvariant() switch { "PLA" => "PLA", "ZA" => "ZA", "SH" => "SH", "SW" => "SW", "GP" => "GP", "GE" => "GE", "BD" => "BD", "SP" => "SP", "SL" => "SL", _ => "VL" };

    /// <summary>이 게임의 전역 설정을 걸어 둠 (gate를 잡은 상태에서만 부름)</summary>
    private void Activate()
    {
        if (active == this) return;
        ParseSettings.InitFromSaveFileData(Sav);
        GenTrainers.Register(Sav, _ => null, _ => pref, GenTrainers.SafeLang);
        active = this;
    }

    private SaveFile LoadSave()
    {
        if (!string.IsNullOrEmpty(cfg.Switch.SavePath) && File.Exists(cfg.Switch.SavePath))
        {
            var s = SaveUtil.GetSaveFile(cfg.Switch.SavePath);
            // 봇 세이브 파일은 그 게임 형식일 때만 (게임 동시 운영: 다른 게임 엔진은 빈 세이브)
            if (s != null && s.GetType() == BlankSaveFile.Get(Game, "x", LanguageID.Korean).GetType()) return s;
        }
        var t = cfg.Switch.Trainer;
        // 한·중·일 언어는 어버이 이름 최대 6글자 (넘으면 모든 조우가 불법 → 엉뚱한 배포 개체로 대체됨)
        int maxOt = t.Language is "ko" or "ja" or "zh" ? 6 : 12;
        if (string.IsNullOrEmpty(t.OT)) t.OT = "홈봇";
        if (t.OT.Length > maxOt) { Console.WriteLine($"[설정] 어버이 이름 '{t.OT}'은 {maxOt}글자를 넘어 '{t.OT[..maxOt]}'로 줄여 씁니다"); t.OT = t.OT[..maxOt]; }
        var lang = t.Language switch { "ja" => LanguageID.Japanese, "en" => LanguageID.English, _ => LanguageID.Korean };
        var sav = BlankSaveFile.Get(Game, t.OT, lang);
        try { sav.OT = t.OT; sav.Gender = (byte)t.Gender; sav.Language = (int)lang; } catch { }
        try { if (sav.Generation >= 7) sav.ID32 = (uint)(t.SID * 1_000_000L + t.TID); else { sav.TID16 = (ushort)Math.Min(t.TID, 65535); sav.SID16 = (ushort)Math.Min(t.SID, 65535); } } catch { }
        return sav;
    }

    public string GameName => GenHost.GameName(Game);
    public GameStrings Strings => GenHost.Strings;

    /// <summary>문장 해석 (범위 주문 전개 포함). 여러 줄이면 여러 마리.</summary>
    public List<OrderSpec> Parse(string text)
    {
        var lines = QuickParse.ExpandRanges(text.Split('\n'), Sav);
        return QuickGen.Parse(string.Join("\n", lines), Sav, UserDict);
    }

    public bool InGame(OrderSpec o) => QuickGen.InGame(o, Sav);
    public string Describe(OrderSpec o) => o.Describe(Strings);

    /// <summary>한 마리 생성. 실패하면 (null, 이유).</summary>
    public async Task<(PKM pk, string msg)> MakeAsync(OrderSpec o, CancellationToken tok)
    {
        await gate.WaitAsync(tok);
        try
        {
            Activate();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(tok); cts.CancelAfter(TimeSpan.FromSeconds(cfg.Limits.GenSeconds));
            return await Task.Run(() =>
            {
                var (p, msg) = QuickGen.MakeOne(o, Sav, cts.Token);
                if (p == null) return (null, msg);
                p = QuickGen.Finish(o, p, Sav);
                if (!GenHost.IsLegal(p)) return (null, "만들었지만 합법 검사를 통과하지 못했습니다: " + Report(p));
                if (Untradable(p) is { } why) return (null, why);
                return (p, msg);
            }, cts.Token);
        }
        finally { gate.Release(); }
    }

    /// <summary>교환·HOME으로 못 보내는 개체면 이유, 아니면 null.</summary>
    public string Untradable(PKM p)
    {
        try
        {
            uint fa = p is IFormArgument f ? f.FormArgument : 0;
            if (TradeRestrictions.IsUntradable(p.Species, p.Form, fa, p.Format)) return "교환이 안 되는 개체입니다 (라이드 폼·배틀 폼 등)";
            if (TradeRestrictions.IsUntradableHeld(p.Context, p.HeldItem)) return "교환이 안 되는 도구를 지니고 있습니다";
            var la = new LegalityAnalysis(p);
            if (la.EncounterMatch is IEncounterTemplate enc && TradeRestrictions.IsUntradableEncounter(enc)) return "교환이 안 되는 조우(최초 라이드 전설 등)입니다";
            if (p.IsEgg) return "알은 HOME에 맡길 수 없습니다";
            if (SpeciesCategory.IsMythical(p.Species)) return "환상의 포켓몬은 GTS에서 교환할 수 없습니다";
            if (GtsBlockedRibbon(p) is { } rb) return $"배포 리본({rb})이 붙은 포켓몬은 GTS에서 교환할 수 없습니다";
        }
        catch { }
        return null;
    }

    /// <summary>GTS·미라클 박스에서 교환이 막히는 배포·대회 리본. 있으면 리본 이름.</summary>
    public static string GtsBlockedRibbon(PKM p)
    {
        if (p is IRibbonSetEvent4 e4)
        {
            if (e4.RibbonClassic) return "클래식리본"; if (e4.RibbonPremier) return "프리미어리본"; if (e4.RibbonEvent) return "이벤트리본";
            if (e4.RibbonBirthday) return "버스데이리본"; if (e4.RibbonSpecial) return "스페셜리본"; if (e4.RibbonSouvenir) return "기념리본";
            if (e4.RibbonWishing) return "소원리본"; if (e4.RibbonChampionWorld) return "월드챔피언리본";
        }
        if (p is IRibbonSetEvent3 e3)
        {
            if (e3.RibbonChampionBattle) return "배틀챔피언리본"; if (e3.RibbonChampionRegional) return "지방챔피언리본"; if (e3.RibbonChampionNational) return "전국챔피언리본";
            if (e3.RibbonEarth) return "어스리본"; if (e3.RibbonNational) return "내셔널리본"; if (e3.RibbonCountry) return "컨트리리본"; if (e3.RibbonWorld) return "월드리본";
        }
        if (p is IRibbonSetCommon9 c9 && c9.RibbonPartner) return "파트너리본";
        return null;
    }

    public static string Report(PKM p) { try { var la = new LegalityAnalysis(p); return string.Join(" / ", la.Results.Where(r => r.Judgement == Severity.Invalid).Select(r => r.Result.ToString())); } catch { return "?"; } }

    // ---- 바이트 보관 (풀 DB)
    public static byte[] Bytes(PKM p) { var b = new byte[p.SIZE_STORED]; p.WriteDecryptedDataStored(b); return b; }
    public PKM FromBytes(byte[] b, string format)
    {
        // 풀에 저장한 형식(pa9·pk9·pk8…)으로 읽음 → 어느 게임 엔진으로 읽어도 그 개체 형식 그대로 (게임 동시 운영)
        PKM pk = null;
        if (!string.IsNullOrEmpty(format)) try { pk = EntityFormat.GetFromBytes(b, EntityFileExtension.GetContextFromExtension(format.TrimStart('.').ToLowerInvariant(), Sav.Context)); } catch { }
        pk ??= EntityFormat.GetFromBytes(b, Sav.Context);
        return pk ?? Sav.GetDecryptedPKM(b);
    }
    // ================= 쇼다운 세트 · 파일 · 상세 정보 =================
    private GameStrings en;
    public GameStrings En => en ??= GameInfo.GetStrings("en");

    /// <summary>쇼다운 세트처럼 보이는지 (여러 줄 + Ability:/EVs:/IVs:/Nature/- 기술/@ 도구 등)</summary>
    public static bool LooksShowdown(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;
        var lines = t.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return false;
        int hits = lines.Count(l => l.StartsWith("- ") || l.StartsWith("Ability:") || l.StartsWith("EVs:") || l.StartsWith("IVs:") || l.EndsWith(" Nature") || l.StartsWith("Tera Type:") || l.StartsWith("Level:") || l.StartsWith("Shiny:")
                                    || l.StartsWith("특성:") || l.StartsWith("노력치:") || l.StartsWith("개체값:") || l.StartsWith("테라스탈타입:") || l.StartsWith("레벨:") || l.StartsWith("이로치:") || l.Contains(" @ "));
        return hits >= 1;
    }

    /// <summary>"어버이 · TID · SID" 칸 해석. 예: "새아 468686 1234", "새아", "468686". 반환 (이름, TID, SID, 문제)</summary>
    public (string ot, int tid, int sid, string problem) ParseTrainer(string raw)
    {
        string ot = null; int tid = -1, sid = -1;
        foreach (var t in (raw ?? "").Replace("/", " ").Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var u = t.ToUpperInvariant();
            if (u is "TID" or "SID" or "ID" or "어버이") continue;
            if (t.All(char.IsDigit)) { if (tid < 0) tid = int.Parse(t); else if (sid < 0) sid = int.Parse(t); else return (null, -1, -1, "숫자는 TID·SID 두 개까지만 적어 주세요"); }
            else if (ot == null) ot = t;
            else return (null, -1, -1, "어버이 이름에는 띄어쓰기를 쓸 수 없어요");
        }
        int maxOt = Sav.Language is (int)LanguageID.Korean or (int)LanguageID.Japanese or (int)LanguageID.ChineseS or (int)LanguageID.ChineseT ? 6 : 12;
        if (ot != null && ot.Length > maxOt) return (null, -1, -1, $"어버이 이름은 {maxOt}글자까지예요");
        if (tid > 999999) return (null, -1, -1, "TID는 0~999999 (6자리)예요");
        if (sid > 4294) return (null, -1, -1, "SID는 0~4294 (4자리)예요");
        return (ot, tid, sid, null);
    }

    /// <summary>신청 문장/세트에 어버이 정보를 붙임 (문장은 "어버이 이름 TID SID", 세트는 OT:/TID:/SID: 줄)</summary>
    public static string WithTrainer(string text, bool showdown, string ot, int tid, int sid)
    {
        if (ot == null && tid < 0) return text;
        if (showdown)
            return text.TrimEnd() + (ot != null ? $"\nOT: {ot}" : "") + (tid >= 0 ? $"\nTID: {tid}" : "") + (sid >= 0 ? $"\nSID: {sid}" : "");
        var lines = text.Replace("\r", "").Split('\n').ToList();
        lines[0] = lines[0].TrimEnd() + " 어버이" + (ot != null ? " " + ot : "") + (tid >= 0 ? " " + tid.ToString("D6") : "") + (tid >= 0 && sid >= 0 ? " " + sid.ToString("D4") : "");
        return string.Join("\n", lines);
    }

    public ShowdownSet ParseShowdown(string text, out string problem)
    {
        problem = null;
        text = text.Replace("\r", "").Trim();
        foreach (var lang in new[] { "en", "ko" })
        {
            ShowdownSet set;
            try { set = new ShowdownSet(text, BattleTemplateLocalization.GetLocalization(lang)); } catch { continue; }
            if (set.Species != 0) return set;
        }
        problem = "쇼다운 세트에서 포켓몬 이름을 찾지 못했습니다 (첫 줄: 이름 @ 도구)";
        return null;
    }

    public string DescribeSet(ShowdownSet set) => (set.Shiny ? "이로치 " : "") + Strings.Species[set.Species] + (set.Level is > 0 and < 100 ? $" Lv.{set.Level}" : "");

    public bool InGame(ushort species, byte form) { try { return Sav.Personal.IsPresentInGame(species, form); } catch { return true; } }

    public async Task<(PKM pk, string msg)> MakeFromSetAsync(ShowdownSet set, CancellationToken tok, string setText = null)
    {
        await gate.WaitAsync(tok);
        try
        {
            Activate();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(tok); cts.CancelAfter(TimeSpan.FromSeconds(cfg.Limits.GenSeconds));
            return await Task.Run(() =>
            {
                // 세트 안 "OT: 이름 / TID: 숫자 / SID: 숫자" 줄이 있으면 그 트레이너로 만듦
                ITrainerInfo tr = Sav;
                var lines = (setText ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
                string L(string k) => lines.FirstOrDefault(l => l.StartsWith(k + ":", StringComparison.OrdinalIgnoreCase))?[(k.Length + 1)..].Trim();
                var ot = L("OT"); int.TryParse(L("TID") ?? "", out var tid); int.TryParse(L("SID") ?? "", out var sid);
                if (!string.IsNullOrEmpty(ot) || L("TID") != null)
                {
                    uint id32 = L("TID") != null ? (uint)((L("SID") != null ? sid : Sav.DisplaySID) * 1_000_000L + tid) : Sav.ID32;
                    tr = new SimpleTrainerInfo(Sav.Version) { OT = string.IsNullOrEmpty(ot) ? Sav.OT : ot, ID32 = id32, Gender = Sav.Gender, Language = Sav.Language, Generation = Sav.Generation, Context = Sav.Context };
                }
                var r = tr.GetLegalFromSet(set);
                var p = r.Created;
                if (p == null) return (null, "만들지 못했습니다");
                if (!GenHost.IsLegal(p)) return (null, "이 세트는 합법으로 만들 수 없습니다: " + Report(p));
                if (Untradable(p) is { } why) return (null, why + " (코라이돈·미라이돈은 문장 신청으로 해 주세요)");
                return (p, r.Status.ToString());
            }, cts.Token);
        }
        finally { gate.Release(); }
    }

    /// <summary>올린 파일(.pk9 .pb8 .pk8 등) → 봇 게임 형식으로 바꾸고 합법 검사.</summary>
    public (PKM pk, string msg) FromFile(byte[] data, string fileName)
    {
        gate.Wait();
        try { Activate(); return FromFileCore(data, fileName); }
        finally { gate.Release(); }
    }
    private (PKM pk, string msg) FromFileCore(byte[] data, string fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        PKM pk = null;
        try { pk = EntityFormat.GetFromBytes(data, EntityFileExtension.GetContextFromExtension(ext, Sav.Context)); } catch { }
        pk ??= EntityFormat.GetFromBytes(data, Sav.Context);
        if (pk == null || pk.Species == 0) return (null, "포켓몬 파일로 읽지 못했습니다 (.pk9 · .pb8 · .pk8 · .pa8 등)");
        if (pk.GetType() != Sav.PKMType)
        {
            var c = EntityConverter.ConvertToType(pk, Sav.PKMType, out var res);
            if (c == null) return (null, $"{GameName} 형식으로 바꿀 수 없는 포켓몬입니다 ({res})");
            pk = c;
        }
        if (!InGame(pk.Species, pk.Form)) return (null, $"{GameName}에는 없는 포켓몬입니다");
        if (!GenHost.IsLegal(pk))
        {
            // 다른 세이브에서 만든 파일: 받은 트레이너(HT)가 그 사람(예: PKHeX 기본 'PKHeX')으로 돼 있으면 봇 세이브 기준으론 '받은 트레이너 이름 불일치'.
            // 봇 박스에 들어가면 어차피 봇 트레이너가 맡으므로, 봇 세이브에 맞춰(AdaptToSaveFile) 합법이면 그대로 받음
            var adapted = pk.Clone();
            try { Sav.AdaptToSaveFile(adapted); adapted.RefreshChecksum(); } catch { adapted = null; }
            if (adapted != null && GenHost.IsLegal(adapted)) pk = adapted;
        }
        if (!GenHost.IsLegal(pk) && !OnlyRibbonInvalid(pk))
        {
            var rep = Report(pk);
            if (rep.Contains("TransferTrackerMissing")) return (null, "HOME 트래커가 없는 개체입니다 (다른 게임에서 HOME을 거쳐 온 기록이 없음). 받을 수 없어요");
            return (null, "합법 검사를 통과하지 못한 파일입니다: " + rep);
        }
        if (Untradable(pk) is { } why) return (null, why);
        return (pk, "파일");
    }

    /// <summary>
    /// 불법 사유가 리본(증표 포함)뿐인지. 실제 HOME은 리본을 검사하지 않아 교환·이동에 문제가 없으므로 파일 신청에서는 허용 (PKHeX 모바일과 같은 정책).
    /// 기술·떠올리기 기술이 불법이면 허용하지 않음.
    /// </summary>
    public static bool OnlyRibbonInvalid(PKM pk)
    {
        try
        {
            var la = new LegalityAnalysis(pk);
            if (!MoveResult.AllValid(la.Info.Moves) || !MoveResult.AllValid(la.Info.Relearn)) return false;
            var bad = la.Results.Where(r => r.Judgement == Severity.Invalid).ToList();
            return bad.Count > 0 && bad.All(r => r.Identifier == CheckIdentifier.Ribbon || r.Identifier == CheckIdentifier.RibbonMark);
        }
        catch { return false; }
    }

    /// <summary>열쇠 포켓몬 이름(한글·영어) → 종 번호와 영어 이름. 영어 이름에 특수문자(é ♀ 등)가 있으면 HOME에 칠 수 없어 거절.</summary>
    public (KeyPoke kp, string problem) ParseKey(string name, string level, string gender, string lang)
    {
        var kp = new KeyPoke();
        name = (name ?? "").Trim();
        static int Find(IReadOnlyList<string> l, string n) { for (int i = 1; i < l.Count; i++) if (string.Equals(l[i], n, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        int sp = Find(Strings.Species, name);
        if (sp <= 0) sp = Find(En.Species, name);
        if (sp <= 0 && UserDict.TryGetValue(name, out var alias)) sp = Find(Strings.Species, alias);
        if (sp <= 0) return (null, $"올릴 포켓몬 '{name}'을 찾지 못했습니다");
        kp.Species = (ushort)sp; kp.NameKo = Strings.Species[sp]; kp.NameEn = En.Species[sp];
        if (kp.NameEn.Any(ch => ch > 127 || ch == ':')) return (null, $"'{kp.NameKo}'({kp.NameEn})는 HOME 검색창에 입력하기 어려운 이름이라 다른 포켓몬을 올려 주세요");
        var lv = new string((level ?? "").Where(char.IsDigit).ToArray());
        if (!int.TryParse(lv, out var l) || l is < 1 or > 100) return (null, "레벨은 1~100 숫자로 적어 주세요");
        kp.Level = l;
        var g = (gender ?? "").Trim().ToLowerInvariant();
        kp.Gender = g is "수컷" or "수" or "♂" or "m" or "male" or "남" ? 0 : g is "암컷" or "암" or "♀" or "f" or "female" or "여" ? 1 : -1;
        // 성별이 정해진 종은 HOME 검색 창에서 성별 칸이 잠김 → 검색 때 고르지 않음. 표시용 성별은 실제 값으로 맞춤
        try
        {
            var pi = PersonalTable.SV.GetFormEntry(kp.Species, 0) as IGenderDetail ?? (IGenderDetail)PersonalTable.SWSH.GetFormEntry(kp.Species, 0);
            if (pi.Genderless) { kp.GenderFixed = true; kp.Gender = -1; }
            else if (pi.OnlyMale) { kp.GenderFixed = true; kp.Gender = 0; }
            else if (pi.OnlyFemale) { kp.GenderFixed = true; kp.Gender = 1; }
        }
        catch { }
        var lg = (lang ?? "").Trim().ToUpperInvariant().Replace("_", "-");
        kp.Lang = lg switch
        {
            "KOR" or "KO" or "KR" or "한국어" or "한글" => "KOR", "ENG" or "EN" or "영어" => "ENG", "JPN" or "JA" or "JP" or "일본어" => "JPN",
            "FRA" or "FR" or "프랑스어" => "FRA", "DEU" or "DE" or "독일어" => "DEU", "ITA" or "IT" or "이탈리아어" => "ITA",
            "ES-ES" or "ES" or "SPA" or "스페인어" => "ES-ES", "ES-LA" or "스페인어(중남미)" => "ES-LA", "CHS" or "중국어간체" or "간체" => "CHS", "CHT" or "중국어번체" or "번체" => "CHT",
            "" => "KOR", _ => "",
        };
        if (kp.Lang.Length == 0) return (null, "언어는 KOR · ENG · JPN · FRA · DEU · ITA · ES-ES · ES-LA · CHS · CHT 중 하나로 적어 주세요");
        return (kp, null);
    }

    /// <summary>교환 카드에 쓸 상세 정보.</summary>
    public PokeCard Card(PKM pk)
    {
        var s = Strings;
        var c = new PokeCard { Species = s.Species[pk.Species], Shiny = pk.IsShiny, Level = pk.CurrentLevel };
        c.Form = pk.Form > 0 ? (FormConverter.GetFormList(pk.Species, s.types, s.forms, GameInfo.GenderSymbolASCII, pk.Context).ElementAtOrDefault(pk.Form) ?? "") : "";
        c.Gender = pk.Gender switch { 0 => "♂", 1 => "♀", _ => "" };
        if (pk is ITeraType t) c.Tera = s.types.ElementAtOrDefault((int)t.TeraType) ?? "";
        if (pk is IScaledSize ss)
        {
            c.HeightScalar = ss.HeightScalar; c.WeightScalar = ss.WeightScalar;
            try
            {
                var pi = pk.PersonalInfo as IPersonalMisc;
                if (pi != null) { c.HeightM = PB7.GetHeightAbsolute(pi, ss.HeightScalar) / 100f; c.WeightKg = PB7.GetWeightAbsolute(pi, ss.HeightScalar, ss.WeightScalar) / 10f; }
            }
            catch { }
        }
        if (pk is IScaledSize3 s3) c.Scale = s3.Scale;              // 바이올렛·Z-A 배율 (0~255)
        if (pk is IAlpha al) c.Alpha = al.IsAlpha ? 1 : 0;         // 우두머리 (Z-A·아르세우스)
        // 리본·증표 (한국어 이름)
        try
        {
            foreach (var r in RibbonInfo.GetRibbonInfo(pk))
            {
                if (!r.HasRibbon && r.RibbonCount == 0) continue;
                var rn = s.Ribbons.GetNameSafe(r.Name, out var kn) ? kn : r.Name.Replace("Ribbon", "");
                if (r.RibbonCount > 0) rn += $" ×{r.RibbonCount}";
                (r.Name.StartsWith("RibbonMark") ? c.Marks : c.Ribbons).Add(rn);
            }
        }
        catch { }
        c.Ball = s.balllist.ElementAtOrDefault(pk.Ball) ?? "";
        c.MetLevel = pk.MetLevel;
        try { c.MetDate = pk.MetDate?.ToString("yyyy-MM-dd") ?? ""; } catch { }
        try { c.MetLocation = s.GetLocationName(false, pk.MetLocation, pk.Format, pk.Generation, (GameVersion)pk.Version); } catch { }
        c.Ability = s.abilitylist.ElementAtOrDefault(pk.Ability) ?? "";
        c.Nature = s.natures.ElementAtOrDefault((int)pk.Nature) ?? "";
        c.Item = pk.HeldItem > 0 ? s.itemlist.ElementAtOrDefault(pk.HeldItem) ?? "" : "";
        c.Language = ((LanguageID)pk.Language) switch { LanguageID.Korean => "KOR (한국어)", LanguageID.English => "ENG (영어)", LanguageID.Japanese => "JPN (일본어)", LanguageID.French => "FRA", LanguageID.German => "DEU", LanguageID.Italian => "ITA", LanguageID.Spanish => "ES-ES", LanguageID.SpanishL => "ES-LA", LanguageID.ChineseS => "CHS", LanguageID.ChineseT => "CHT", _ => "?" };
        Span<int> iv = stackalloc int[6]; pk.GetIVs(iv);
        c.IVs = iv.ToArray().All(v => v == 31) ? "6V" : string.Join("/", new[] { iv[0], iv[1], iv[2], iv[4], iv[5], iv[3] });
        Span<int> ev = stackalloc int[6]; pk.GetEVs(ev);
        c.EVs = ev.ToArray().Any(v => v > 0) ? string.Join("/", new[] { ev[0], ev[1], ev[2], ev[4], ev[5], ev[3] }) : "";
        c.Moves = new[] { pk.Move1, pk.Move2, pk.Move3, pk.Move4 }.Where(m => m != 0).Select(m => s.movelist.ElementAtOrDefault(m) ?? "?").ToList();
        c.OT = pk.OriginalTrainerName; c.TID = pk.DisplayTID; c.SID = pk.DisplaySID;
        var name = (En.Species[pk.Species] ?? "").ToLowerInvariant();
        c.Sprite = $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/other/home/{(pk.IsShiny ? "shiny/" : "")}{pk.Species}.png";
        c.FormEn = pk.Form > 0 ? (FormConverter.GetFormList(pk.Species, En.types, En.forms, GameInfo.GenderSymbolASCII, pk.Context).ElementAtOrDefault(pk.Form) ?? "") : "";
        c.SpeciesEn = En.Species[pk.Species] ?? ""; c.FormIndex = pk.Form; c.SpeciesId = pk.Species;
        return c;
    }
}

public sealed class PokeCard
{
    public string Species = "", Form = "", Gender = "", Tera = "", Ball = "", MetDate = "", MetLocation = "", Ability = "", Nature = "", Item = "", Language = "", IVs = "", EVs = "", OT = "", Sprite = "", FormEn = "", SpeciesEn = "";
    public int FormIndex, SpeciesId;
    public bool Shiny; public int Alpha = -1; public int Level, MetLevel, Scale = -1, HeightScalar = -1, WeightScalar = -1; public float HeightM, WeightKg; public uint TID, SID; public List<string> Moves = [], Ribbons = [], Marks = [];
    public string Title => (Shiny ? "✨ " : "") + Species + (Form.Length > 0 ? $" ({Form})" : "") + (Gender.Length > 0 ? " " + Gender : "");
}
