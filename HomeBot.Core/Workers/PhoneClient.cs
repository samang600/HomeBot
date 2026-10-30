using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace HomeBot.Core.Workers;

/// <summary>
/// 파이썬 워커 서비스(phone_worker/server.py)의 HTTP 클라이언트.
///  POST /job   {"type":"gts_deposit","args":{...}} → {"ok":true,"msg":"","vars":{...},"shot":"path"}
///  POST /match  multipart: image, template, threshold → {"found":true,"score":0.93,"x":..,"y":..}
///  POST /ocr    multipart: image, region → {"text":"..."}
///  GET  /ping
/// </summary>
public sealed class PhoneClient : IPhoneWorker, IVision
{
    private readonly HttpClient http; private readonly PhoneCfg cfg;
    public PhoneClient(PhoneCfg c) { cfg = c; http = new HttpClient { BaseAddress = new Uri(c.Url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(Math.Max(30, c.TimeoutSec)) }; http.DefaultRequestHeaders.ConnectionClose = true; }   // 파이썬 서버는 응답마다 연결을 닫음 → 닫힌 연결을 다시 쓰다 실패하지 않게

    private sealed class JobReply { [JsonPropertyName("ok")] public bool Ok { get; set; } [JsonPropertyName("msg")] public string Msg { get; set; } = ""; [JsonPropertyName("vars")] public Dictionary<string, string> Vars { get; set; } = new(); [JsonPropertyName("shot")] public string Shot { get; set; } = ""; }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try { using var r = await http.GetAsync("ping", ct); return r.IsSuccessStatusCode; } catch { return false; }
    }

    private async Task<StepResult> Job(string type, object args, CancellationToken ct)
    {
        try
        {
            // 파이썬 서버는 Content-Length가 있어야 본문을 읽음 → 문자열로 만들어 보냄 (PostAsJsonAsync는 chunked로 보냄)
            var json = System.Text.Json.JsonSerializer.Serialize(new { type, args });
            using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var r = await http.PostAsync("job", body, ct);
            var j = await r.Content.ReadFromJsonAsync<JobReply>(cancellationToken: ct) ?? new JobReply { Msg = "응답 없음" };
            return new StepResult { Ok = j.Ok, Message = j.Msg ?? "", Vars = j.Vars ?? new(), Screenshot = j.Shot ?? "" };
        }
        catch (Exception ex) { return StepResult.Fail("폰 워커 호출 실패: " + ex.Message); }
    }

    public Task<StepResult> GtsDepositAsync(int box, int slot, GtsWant want, CancellationToken ct) =>
        Job("gts_deposit", new { box, slot, species = want.Species, species_name = want.SpeciesName, gender = want.Gender, level_min = want.LevelMin, level_max = want.LevelMax }, ct);
    public Task<StepResult> GtsCollectAsync(CancellationToken ct) => Job("gts_collect", new { }, ct);
    public Task<StepResult> GtsWithdrawAsync(CancellationToken ct) => Job("gts_withdraw", new { }, ct);
    public Task<StepResult> FriendTradeAsync(int box, int slot, string friendName, CancellationToken ct) => Job("friend_trade", new { box, slot, friend = friendName }, ct);
    public Task<StepResult> ReleaseAsync(IReadOnlyList<(int box, int slot)> slots, CancellationToken ct) => Job("release", new { slots = slots.Select(s => new { s.box, s.slot }).ToList() }, ct);

    public Task<StepResult> GtsFindTradeAsync(string nameEn, int gender, int level, string lang, int slot, bool fresh, CancellationToken ct, string nick = "", string nickLang = "", int nth = 0, int total = 1, string fp = "", int acct = 1) =>
        Job("gts_find_trade", new { species_name = nameEn, gender, level, lang, slot, fresh = fresh ? "1" : "0", nick, nick_lang = nickLang, nth, total, fp, acct }, ct);
    public async Task<bool> TradedRecentlyAsync(string nameEn, int level, string lang, int gender, string nick, int minutes, CancellationToken ct, int acct = 1)
    {
        var r = await Job("recent_trades", new { minutes }, ct);
        if (!r.Ok || !r.Vars.TryGetValue("list", out var js)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(js);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string S(string k) => e.TryGetProperty(k, out var v) ? v.ToString() : "";
                if (string.Equals(S("species_name"), nameEn, StringComparison.OrdinalIgnoreCase) && S("level") == level.ToString() && string.Equals(S("lang"), lang, StringComparison.OrdinalIgnoreCase)
                    && S("gender") == gender.ToString() && S("nick") == nick && (S("acct") == "" ? 1 : int.TryParse(S("acct"), out var ac) ? ac : 1) == acct) return true;
            }
        }
        catch { }
        return false;
    }
    public async Task<bool> NameOcrAsync(CancellationToken ct)
    {
        try { using var r = await http.GetAsync("ping", ct); var t = await r.Content.ReadAsStringAsync(ct); return t.Replace(" ", "").Contains("\"name_ocr\":true"); } catch { return false; }
    }
    public Task<StepResult> HomeCloseAsync(CancellationToken ct, int acct = 1) => Job("home_close", new { acct }, ct);
    public Task<StepResult> ExitAsync(CancellationToken ct) => Job("exit", new { }, ct);
    public Task<StepResult> HomeLaunchAsync(CancellationToken ct, int acct = 1) => Job("home_launch", new { acct }, ct);

    public async Task<(bool found, double score, int x, int y)> MatchAsync(byte[] image, string template, double threshold, CancellationToken ct)
    {
        JsonElement j = default;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var form = new MultipartFormDataContent { { new ByteArrayContent(image), "image", "shot.jpg" }, { new StringContent(template), "template" }, { new StringContent(threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)), "threshold" } };
                using var r = await http.PostAsync("match", form, ct);
                j = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                break;
            }
            catch (HttpRequestException) when (attempt < 3) { await Task.Delay(200 * (attempt + 1), ct); }   // 순간적인 연결 끊김은 다시 시도
        }
        return (j.TryGetProperty("found", out var f) && f.GetBoolean(), j.TryGetProperty("score", out var s) ? s.GetDouble() : 0, j.TryGetProperty("x", out var x) ? x.GetInt32() : 0, j.TryGetProperty("y", out var y) ? y.GetInt32() : 0);
    }
    public async Task<string> OcrAsync(byte[] image, (int x, int y, int w, int h)? region, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(image), "image", "shot.jpg" } };
        if (region is { } rg) form.Add(new StringContent($"{rg.x},{rg.y},{rg.w},{rg.h}"), "region");
        using var r = await http.PostAsync("ocr", form, ct);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return j.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }
    public async Task<string> OcrTitleAsync(byte[] image, (int x, int y, int w, int h) region, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(image), "image", "shot.jpg" }, { new StringContent($"{region.x},{region.y},{region.w},{region.h}"), "region" }, { new StringContent("title"), "mode" } };
        using var r = await http.PostAsync("ocr", form, ct);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return j.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }
    public async Task<bool> TemplateExistsAsync(string name, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent { { new StringContent(name), "name" }, { new StringContent("1"), "check" } };
        using var r = await http.PostAsync("logo", form, ct);
        if (!r.IsSuccessStatusCode) return true;   // 옛 워커(/logo 없음)면 '있다'고 보고 자동 등록을 건너뜀
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!j.TryGetProperty("exists", out var e)) return true;
        return e.GetBoolean();
    }
    public async Task<string> SaveLogoAsync(byte[] image, string name, (int x, int y, int w, int h) region, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(image), "image", "shot.jpg" }, { new StringContent(name), "name" }, { new StringContent($"{region.x},{region.y},{region.w},{region.h}"), "region" } };
        using var r = await http.PostAsync("logo", form, ct);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (j.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return j.TryGetProperty("path", out var p) ? p.GetString() : "";
        throw new IOException(j.TryGetProperty("msg", out var m) ? m.GetString() : "로고 저장 실패");
    }
}

/// <summary>화면 인식 서비스가 꺼져 있으면 '못 찾음'으로 끝나게 (확인 없이 진행하지 않음) + 알림. 서비스가 다시 켜지면 그대로 이어서 씀</summary>
public sealed class GuardedVision : IVision
{
    private readonly IVision inner; public Action<string> OnDown { get; set; }
    public GuardedVision(IVision inner) => this.inner = inner;
    private Exception Down(Exception ex) { try { OnDown?.Invoke(ex.Message); } catch { } return new IOException("화면 인식 서비스에 연결되지 않음 (" + ex.Message + ")", ex); }
    public async Task<(bool found, double score, int x, int y)> MatchAsync(byte[] image, string template, double threshold, CancellationToken ct)
    {
        try { return await inner.MatchAsync(image, template, threshold, ct); }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested) { throw Down(ex); }
    }
    public async Task<string> OcrAsync(byte[] image, (int x, int y, int w, int h)? region, CancellationToken ct)
    {
        try { return await inner.OcrAsync(image, region, ct); }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested) { throw Down(ex); }
    }
    public async Task<string> OcrTitleAsync(byte[] image, (int x, int y, int w, int h) region, CancellationToken ct)
    {
        try { return await inner.OcrTitleAsync(image, region, ct); }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested) { throw Down(ex); }
    }
    public async Task<bool> TemplateExistsAsync(string name, CancellationToken ct)
    {
        try { return await inner.TemplateExistsAsync(name, ct); }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested) { throw Down(ex); }
    }
    public async Task<string> SaveLogoAsync(byte[] image, string name, (int x, int y, int w, int h) region, CancellationToken ct)
    {
        try { return await inner.SaveLogoAsync(image, name, region, ct); }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested) { throw Down(ex); }
    }
}

/// <summary>폰 워커가 꺼져 있을 때: 화면 확인 없이 매크로만 돌림 (expect 단계는 항상 통과). 개발·1단계용.</summary>
public sealed class NoVision : IVision
{
    public Task<(bool found, double score, int x, int y)> MatchAsync(byte[] image, string template, double threshold, CancellationToken ct) => Task.FromResult((true, 1.0, 0, 0));
    public Task<string> OcrAsync(byte[] image, (int x, int y, int w, int h)? region, CancellationToken ct) => Task.FromResult("");
}
