using System.Text.Json;
using System.Text.Json.Serialization;
namespace HomeBot.Core.Workers;

/// <summary>
/// 버튼 매크로 (macros/*.json). 단계 종류:
///  {"click":"A","n":2,"wait":600}      버튼 n번, 각 wait ms 뒤
///  {"hold":"DDOWN","ms":1500}          누르고 있기
///  {"stick":"LEFT","x":0,"y":-30000,"ms":500}
///  {"sleep":1000}
///  {"expect":"sv_portal","timeout":8000,"fail":"abort|continue|goto:라벨"}  화면 템플릿 확인
///  {"label":"다시"}  {"goto":"다시"}
///  {"ocr":[x,y,w,h],"into":"trainer"}  화면 글자 읽어 변수에
///  {"code":"${code}","keys":{"0":["DDOWN","DDOWN","DDOWN","A"],...}}  숫자 코드 입력
///  {"shot":"이름"}  캡처 저장
///  {"repeat":3,"steps":[...]}
/// 라벨·템플릿 이름은 문자열, ${var}는 vars로 치환.
/// </summary>
public sealed class MacroStep
{
    [JsonPropertyName("click")] public string Click { get; set; }
    [JsonPropertyName("n")] public int N { get; set; } = 1;
    /// <summary>n 대신 변수로 횟수 ("${down}")</summary>
    [JsonPropertyName("times")] public string Times { get; set; }
    [JsonPropertyName("wait")] public int Wait { get; set; } = 500;
    [JsonPropertyName("hold")] public string Hold { get; set; }
    [JsonPropertyName("ms")] public int Ms { get; set; }
    [JsonPropertyName("stick")] public string Stick { get; set; }
    [JsonPropertyName("x")] public short X { get; set; }
    [JsonPropertyName("y")] public short Y { get; set; }
    [JsonPropertyName("sleep")] public int Sleep { get; set; }
    [JsonPropertyName("expect")] public string Expect { get; set; }
    [JsonPropertyName("timeout")] public int Timeout { get; set; } = 8000;
    [JsonPropertyName("threshold")] public double Threshold { get; set; } = 0.85;
    [JsonPropertyName("fail")] public string Fail { get; set; } = "abort";
    /// <summary>true면 못 봤을 때 로그·실패 화면을 남기지 않음 (반복 확인용)</summary>
    [JsonPropertyName("quiet")] public bool Quiet { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; }
    [JsonPropertyName("goto")] public string Goto { get; set; }
    [JsonPropertyName("ocr")] public int[] Ocr { get; set; }
    [JsonPropertyName("into")] public string Into { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; }
    [JsonPropertyName("keys")] public Dictionary<string, string[]> Keys { get; set; }
    [JsonPropertyName("shot")] public string Shot { get; set; }
    [JsonPropertyName("repeat")] public int Repeat { get; set; }
    [JsonPropertyName("steps")] public List<MacroStep> Steps { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; }
}

public sealed class Macro
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("steps")] public List<MacroStep> Steps { get; set; } = [];

    private static readonly JsonSerializerOptions Opts = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    public static Macro Load(string path) => JsonSerializer.Deserialize<Macro>(File.ReadAllText(path), Opts) ?? throw new InvalidDataException(path);
    public static string Sub(string s, IDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(s) || vars == null) return s ?? "";
        foreach (var (k, v) in vars) s = s.Replace("${" + k + "}", v);
        return s;
    }
}

/// <summary>매크로를 실제로 돌리는 쪽이 구현. 스위치든 폰이든 같은 단계 집합을 씀.</summary>
public interface IMacroDevice
{
    Task ClickAsync(string button, CancellationToken ct);
    Task HoldAsync(string button, int ms, CancellationToken ct);
    Task StickAsync(string stick, short x, short y, int ms, CancellationToken ct);
    Task<byte[]> ScreenshotAsync(CancellationToken ct);
}

public sealed class MacroRunner(IMacroDevice dev, IVision vision, string shotDir, Action<string> log)
{
    /// <summary>이 라벨에 닿으면 거기서 성공으로 끝냄 (긴 매크로의 앞부분만 쓸 때: 게임 로고 자동 등록이 HOME 게임 선택 화면까지만 감)</summary>
    public string StopAt { get; set; }
    public async Task<StepResult> RunAsync(Macro m, IDictionary<string, string> vars, CancellationToken ct)
    {
        var v = new Dictionary<string, string>(vars ?? new Dictionary<string, string>());
        var r = await RunSteps(m.Steps, v, m.Name, ct);
        r.Vars = v; return r;
    }

    private int budget = 2000;   // goto 무한 반복 방지
    private async Task<StepResult> RunSteps(List<MacroStep> steps, Dictionary<string, string> v, string name, CancellationToken ct)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (--budget < 0) return StepResult.Fail("매크로 단계가 너무 많음 (반복이 끝나지 않음)");
            var s = steps[i];
            if (s.Label != null) { if (StopAt != null && s.Label == StopAt) return StepResult.Pass("stop:" + StopAt); continue; }
            if (s.Goto != null) { if (StopAt != null && s.Goto == StopAt) return StepResult.Pass("stop:" + StopAt); i = steps.FindIndex(x => x.Label == s.Goto) ; if (i < 0) return StepResult.Fail($"라벨 없음: {s.Goto}"); continue; }
            if (s.Repeat > 0 && s.Steps != null) { for (int k = 0; k < s.Repeat; k++) { var rr = await RunSteps(s.Steps, v, name, ct); if (!rr.Ok) return rr; } continue; }
            if (s.Click != null) { int times = s.Times != null && int.TryParse(Macro.Sub(s.Times, v), out var tv) ? tv : s.N; for (int k = 0; k < times; k++) { await dev.ClickAsync(Macro.Sub(s.Click, v), ct); await Task.Delay(s.Wait, ct); } continue; }
            if (s.Hold != null) { await dev.HoldAsync(Macro.Sub(s.Hold, v), s.Ms, ct); continue; }
            if (s.Stick != null) { await dev.StickAsync(s.Stick, s.X, s.Y, s.Ms, ct); continue; }
            if (s.Sleep > 0) { await Task.Delay(s.Sleep, ct); continue; }
            if (s.Shot != null) { var img = await dev.ScreenshotAsync(ct); Save(img, name, Macro.Sub(s.Shot, v)); continue; }
            if (s.Expect != null)
            {
                var tpl = Macro.Sub(s.Expect, v); var sw = System.Diagnostics.Stopwatch.StartNew(); bool ok = false; byte[] last = null;
                while (sw.ElapsedMilliseconds < s.Timeout)
                {
                    last = await dev.ScreenshotAsync(ct);
                    bool found = false;
                    if (last.Length > 0) { try { (found, _, _, _) = await vision.MatchAsync(last, tpl, s.Threshold, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { log($"[{name}] 화면 인식 오류: {ex.Message}"); } }
                    if (found) { ok = true; break; }
                    await Task.Delay(100, ct);
                }
                if (ok) continue;
                var shot = !s.Quiet && last is { Length: > 0 } ? Save(last, name, "fail_" + tpl) : "";
                if (!s.Quiet) log($"[{name}] 기대 화면 아님: {tpl}");
                if (s.Fail == "continue") continue;
                if (s.Fail.StartsWith("goto:")) { var l = s.Fail[5..]; if (StopAt != null && l == StopAt) return StepResult.Pass("stop:" + StopAt); i = steps.FindIndex(x => x.Label == l); if (i < 0) return StepResult.Fail($"라벨 없음: {l}", shot); continue; }
                return StepResult.Fail($"화면이 '{tpl}'이 아님", shot);
            }
            if (s.Ocr != null && s.Into != null)
            {
                var img = await dev.ScreenshotAsync(ct);
                var text = await vision.OcrAsync(img, s.Ocr.Length == 4 ? (s.Ocr[0], s.Ocr[1], s.Ocr[2], s.Ocr[3]) : null, ct);
                v[s.Into] = (text ?? "").Trim(); log($"[{name}] OCR {s.Into} = '{v[s.Into]}'"); continue;
            }
            if (s.Code != null && s.Keys != null)
            {
                foreach (var ch in Macro.Sub(s.Code, v))
                {
                    if (!s.Keys.TryGetValue(ch.ToString(), out var seq)) return StepResult.Fail($"코드 글자 '{ch}'의 키 순서가 없음");
                    foreach (var b in seq) { await dev.ClickAsync(b, ct); await Task.Delay(s.Wait, ct); }
                }
                continue;
            }
        }
        return StepResult.Pass();
    }

    private string Save(byte[] img, string macro, string tag)
    {
        try
        {
            Directory.CreateDirectory(shotDir);
            var ext = img.Length > 4 && img[0] == 0x89 ? "png" : "jpg";
            var p = Path.Combine(shotDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{macro}_{Sanitize(tag)}.{ext}");
            File.WriteAllBytes(p, img); return p;
        }
        catch { return ""; }
    }
    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
