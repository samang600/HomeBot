using System.Net.Sockets;
using System.Text;
namespace HomeBot.Core.Workers;

/// <summary>
/// ftpsrv(스위치 sysmodule)로 게임 세이브를 내려받고 올림. 게임을 켜지 않아도 됨.
/// ftpsrv 경로: /save:/&lt;사용자 이름&gt; [UID]/files/&lt;게임 이름&gt; [타이틀ID]/&lt;파일&gt; — 괄호 안 값으로 찾음 (이름은 언어마다 다름).
/// 저장(커밋)은 ftpsrv가 파일을 닫을 때 함 (config.ini [Nx] save_writable=1 필요).
/// </summary>
public sealed class SaveFtp
{
    private readonly string host; private readonly int port; private readonly string user, pass;
    private readonly Dictionary<string, string> found = new();
    public SaveFtp(string host, int port, string user, string pass) { this.host = host; this.port = port; this.user = user; this.pass = pass; }

    /// <summary>짝 게임(소드·실드 등)의 타이틀 ID와 버전 코드. 설정에서는 짝으로 고르고, 실제 어느 쪽인지는 세이브 폴더로 알아냄</summary>
    public static (string title, string ver)[] TitlesOf(string game) => Orchestrator.Norm(game) switch
    {
        "ZA" => [("0100F43008C44000", "ZA")],
        "PLA" => [("01001F5010DFA000", "PLA")],
        "SH" => [("01008DB008C2C000", "SH"), ("0100ABF008968000", "SW")],
        "GP" => [("010003F003A34000", "GP"), ("0100187003A36000", "GE")],
        "BD" => [("0100000011D90000", "BD"), ("010018E011D92000", "SP")],
        _ => [("01008F6008C5E000", "VL"), ("0100A3D008C5C000", "SL")],
    };
    public static string TitleOf(string game) => TitlesOf(game)[0].title;
    private readonly Dictionary<string, string> versions = new();
    /// <summary>세이브 폴더로 알아낸 실제 버전 (SL·VL·SW·SH·BD·SP·GP·GE·PLA·ZA). 아직 모르면 null</summary>
    public string VersionOf(string game) { lock (found) return versions.TryGetValue(Orchestrator.Norm(game), out var v) ? v : null; }
    /// <summary>세이브 파일을 찾지 않고 버전만 알아냄 (시작할 때 생성 엔진·로고 고르기용)</summary>
    public string DetectVersion(string game, string saveUser, int userIndex) { SavePath(game, saveUser, userIndex); return VersionOf(game); }
    private static readonly string[] SaveNames = ["main", "savedata.bin", "SaveData.bin"];

    // ---------- 최소 FTP (PASV, 바이너리)
    private sealed class Session : IDisposable
    {
        private readonly TcpClient c; private readonly NetworkStream s; private readonly string host;
        public Session(string host, int port, string user, string pass)
        {
            this.host = host;
            c = new TcpClient { ReceiveTimeout = 20000, SendTimeout = 20000 };
            if (!c.ConnectAsync(host, port).Wait(8000)) throw new IOException($"FTP 연결 시간 초과 ({host}:{port}) — 스위치에 ftpsrv가 켜져 있는지 확인");
            s = c.GetStream();
            Expect(Reply(), 220);
            var (code, msg) = Cmd("USER " + (string.IsNullOrEmpty(user) ? "anonymous" : user));
            if (code == 331) (code, msg) = Cmd("PASS " + pass);
            if (code != 230) throw new IOException($"FTP 로그인 실패: {code} {msg} (ftpsrv config.ini의 user/pass 또는 anon=1 확인)");
            Cmd("OPTS UTF8 ON");
            Expect(Cmd("TYPE I"), 200);
        }
        private string Line()
        {
            var b = new List<byte>();
            while (true) { int x = s.ReadByte(); if (x < 0) throw new IOException("FTP 연결이 끊김"); if (x == '\n') break; if (x != '\r') b.Add((byte)x); }
            return Encoding.UTF8.GetString(b.ToArray());
        }
        public (int, string) Reply()
        {
            var l = Line();
            if (l.Length < 3 || !int.TryParse(l[..3], out var code)) throw new IOException("FTP 응답 이상: " + l);
            if (l.Length > 3 && l[3] == '-') { var end = l[..3] + " "; string m; do m = Line(); while (!m.StartsWith(end)); }
            return (code, l.Length > 4 ? l[4..] : "");
        }
        public (int, string) Cmd(string line) { var b = Encoding.UTF8.GetBytes(line + "\r\n"); s.Write(b); return Reply(); }
        public static void Expect((int code, string msg) r, int want) { if (r.code != want) throw new IOException($"FTP {r.code} {r.msg}"); }
        private TcpClient Pasv()
        {
            var (code, msg) = Cmd("PASV");
            if (code != 227) throw new IOException($"FTP PASV 실패: {code} {msg}");
            int a = msg.IndexOf('('), z = msg.IndexOf(')');
            var p = msg[(a + 1)..z].Split(',').Select(int.Parse).ToArray();
            var d = new TcpClient { ReceiveTimeout = 30000, SendTimeout = 30000 };
            if (!d.ConnectAsync(host, p[4] * 256 + p[5]).Wait(8000)) throw new IOException("FTP 데이터 연결 시간 초과");
            return d;
        }
        public byte[] Get(string cmd)
        {
            using var d = Pasv();
            var (code, msg) = Cmd(cmd);
            if (code is not (150 or 125)) throw new IOException($"{cmd.Split(' ')[0]} 실패: {code} {msg}");
            using var ms = new MemoryStream(); d.GetStream().CopyTo(ms); d.Close();
            var (c2, m2) = Reply(); if (c2 != 226) throw new IOException($"{cmd.Split(' ')[0]} 끝 응답 이상: {c2} {m2}");
            return ms.ToArray();
        }
        public void Put(string path, byte[] data)
        {
            using (var d = Pasv())
            {
                var (code, msg) = Cmd("STOR " + path);
                if (code is not (150 or 125)) throw new IOException($"STOR 실패: {code} {msg} (ftpsrv config.ini [Nx] save_writable=1 확인)");
                var ns = d.GetStream(); ns.Write(data); ns.Flush(); d.Client.Shutdown(SocketShutdown.Send); d.Close();
            }
            var (c2, m2) = Reply(); if (c2 != 226) throw new IOException($"STOR 끝 응답 이상: {c2} {m2}");
        }
        public List<string> List(string path) => Encoding.UTF8.GetString(Get("NLST " + path)).Split('\n').Select(x => x.Trim('\r', ' ', '/')).Where(x => x.Length > 0).Select(x => x.Contains('/') ? x[(x.LastIndexOf('/') + 1)..] : x).ToList();
        public void Dispose() { try { Cmd("QUIT"); } catch { } try { c.Close(); } catch { } }
    }
    private int okPort;
    /// <summary>설정 포트로 안 되면 ftpsrv 기본값(21)·흔한 값(5000)도 시도하고, 된 포트를 기억</summary>
    private Session Open()
    {
        if (okPort != 0) return new(host, okPort, user, pass);
        Exception first = null;
        foreach (var p in new[] { port, 21, 5000 }.Where(x => x > 0).Distinct())
        {
            try { var s = new Session(host, p, user, pass); okPort = p; return s; }
            catch (Exception ex) when (ex is SocketException or AggregateException || ex.Message.Contains("시간 초과")) { first ??= ex; }
        }
        throw new IOException($"ftpsrv에 연결하지 못했습니다 ({host}, 포트 {port}/21/5000): {first?.GetBaseException().Message} — 스위치에 ftpsrv가 켜져 있는지, config.ini의 port 확인");
    }
    public int Port => okPort;

    public byte[] Retr(string path) { using var f = Open(); return f.Get("RETR " + path); }
    public void Stor(string path, byte[] data) { using var f = Open(); f.Put(path, data); }

    /// <summary>세이브 주인(이름이 saveUser, 비우면 userIndex 번째)의 그 게임 세이브 파일 경로</summary>
    public string SavePath(string game, string saveUser, int userIndex)
    {
        var key = game + "|" + saveUser + "|" + userIndex;
        lock (found) if (found.TryGetValue(key, out var hit)) return hit;
        using var f = Open();
        var root = SaveRoot(f);
        var users = f.List(root).Where(x => x.Contains('[') && x.Length >= 34).ToList();
        if (users.Count == 0) throw new IOException($"FTP {root}에 사용자 세이브가 없습니다");
        string u = !string.IsNullOrWhiteSpace(saveUser)
            ? users.FirstOrDefault(x => x.StartsWith(saveUser.Trim() + " [", StringComparison.OrdinalIgnoreCase)) ?? throw new IOException($"사용자 '{saveUser}' 세이브 폴더 없음 (있는 사용자: {string.Join(", ", users)})")
            : userIndex >= 0 && userIndex < users.Count ? users[userIndex] : throw new IOException($"사용자 {userIndex + 1}번째 세이브 폴더 없음 ({users.Count}명)");
        var files = $"{root}/{u}/files";
        var apps = f.List(files);
        var (app, ver) = TitlesOf(game).Select(t => (apps.FirstOrDefault(x => x.Contains("[" + t.title + "]", StringComparison.OrdinalIgnoreCase)), t.ver)).FirstOrDefault(x => x.Item1 != null);
        if (app == null) throw new IOException($"{u}에 {Orchestrator.GameKo(game)}({string.Join("/", TitlesOf(game).Select(t => t.title))}) 세이브가 없습니다 (있는 것: {string.Join(", ", apps)})");
        lock (found) versions[Orchestrator.Norm(game)] = ver;
        var dir = $"{files}/{app}";
        var names = f.List(dir);
        var file = SaveNames.FirstOrDefault(n => names.Contains(n)) ?? throw new IOException($"{dir}에 세이브 파일(main 등)이 없습니다 (있는 것: {string.Join(", ", names)})");
        var p = $"{dir}/{file}";
        lock (found) found[key] = p;
        return p;
    }
    private static string SaveRoot(Session f)
    {
        var top = f.List("/");
        var s = top.FirstOrDefault(x => x.TrimEnd(':').Equals("save", StringComparison.OrdinalIgnoreCase));
        if (s == null) throw new IOException("FTP 맨 위에 save: 가 없습니다 (ftpsrv config.ini [Nx] mount_devices=1 확인) · 있는 것: " + string.Join(", ", top));
        return "/" + (s.EndsWith(':') ? s : s + ":");
    }

    /// <summary>연결 확인용: 맨 위 · save: · 사용자별 files 목록을 글로</summary>
    public string Probe()
    {
        var sb = new StringBuilder();
        using var f = Open();
        var top = f.List("/"); sb.AppendLine("/ : " + string.Join(" | ", top));
        string root; try { root = SaveRoot(f); } catch (Exception ex) { sb.AppendLine(ex.Message); return sb.ToString(); }
        var users = f.List(root); sb.AppendLine($"{root} : " + string.Join(" | ", users));
        foreach (var u in users.Where(x => x.Contains('[')))
        {
            try
            {
                var files = $"{root}/{u}/files"; var apps = f.List(files);
                sb.AppendLine($"{files} : " + string.Join(" | ", apps));
                foreach (var a in apps.Where(a => Orchestrator.SaveGames.SelectMany(TitlesOf).Any(t => a.Contains("[" + t.title + "]", StringComparison.OrdinalIgnoreCase))))
                    try { sb.AppendLine($"  {a} : " + string.Join(" | ", f.List($"{files}/{a}"))); } catch (Exception ex) { sb.AppendLine($"  {a} : {ex.Message}"); }
            }
            catch (Exception ex) { sb.AppendLine($"{u}: {ex.Message}"); }
        }
        return sb.ToString();
    }
}
