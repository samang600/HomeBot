using Microsoft.Data.Sqlite;
namespace HomeBot.Core;

/// <summary>SQLite 한 파일. 모든 접근은 lock으로 직렬화 (봇·오케스트레이터가 다른 스레드).</summary>
public sealed class Db : IDisposable
{
    private readonly SqliteConnection c;
    private readonly object sync = new();
    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        c = new SqliteConnection($"Data Source={path}"); c.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS users (discord_id INTEGER PRIMARY KEY, home_friend TEXT DEFAULT '', friend_ok INTEGER DEFAULT 0, blocked INTEGER DEFAULT 0, misses INTEGER DEFAULT 0, trainer_name TEXT DEFAULT '', created_at TEXT);
            CREATE TABLE IF NOT EXISTS orders (id INTEGER PRIMARY KEY AUTOINCREMENT, discord_id INTEGER NOT NULL, text TEXT NOT NULL, describe TEXT DEFAULT '', key TEXT DEFAULT '', status TEXT NOT NULL, route TEXT NOT NULL, pool_id INTEGER, error TEXT DEFAULT '', note TEXT DEFAULT '', created_at TEXT, updated_at TEXT, claimed_at TEXT);
            CREATE TABLE IF NOT EXISTS pool (id INTEGER PRIMARY KEY AUTOINCREMENT, pk_bytes BLOB NOT NULL, format TEXT DEFAULT '', species INTEGER, form INTEGER, shiny INTEGER, key TEXT DEFAULT '', location TEXT NOT NULL, box INTEGER DEFAULT -1, slot INTEGER DEFAULT -1, reserved_by INTEGER, created_at TEXT, verified_at TEXT);
            CREATE TABLE IF NOT EXISTS trades (id INTEGER PRIMARY KEY AUTOINCREMENT, order_id INTEGER, pool_id INTEGER, route TEXT, started_at TEXT, finished_at TEXT, result TEXT DEFAULT '', screenshot TEXT DEFAULT '');
            CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT);
            CREATE INDEX IF NOT EXISTS ix_orders_status ON orders(status);
            CREATE INDEX IF NOT EXISTS ix_pool_key ON pool(key, location);
            CREATE INDEX IF NOT EXISTS ix_pool_loc ON pool(location);
            """);
        Migrate();
    }
    private void Migrate()
    {
        foreach (var sql in new[] { "ALTER TABLE orders ADD COLUMN kind TEXT DEFAULT 'text'", "ALTER TABLE orders ADD COLUMN channel_id INTEGER DEFAULT 0", "ALTER TABLE orders ADD COLUMN key_json TEXT DEFAULT ''", "ALTER TABLE orders ADD COLUMN priority REAL", "ALTER TABLE orders ADD COLUMN game TEXT DEFAULT ''" })
            try { Exec(sql); } catch { }   // 이미 있으면 무시
    }
    public void Dispose() => c.Dispose();

    private static string Now() => DateTime.UtcNow.ToString("o");
    private static DateTime Dt(object o) => o is string s && s.Length > 0 ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind) : DateTime.MinValue;
    private static DateTime? DtN(object o) => o is string s && s.Length > 0 ? Dt(o) : null;

    private SqliteCommand Cmd(string sql, params (string, object)[] ps)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }
    private void Exec(string sql, params (string, object)[] ps) { lock (sync) using (var cmd = Cmd(sql, ps)) cmd.ExecuteNonQuery(); }
    private long Insert(string sql, params (string, object)[] ps) { lock (sync) { using var cmd = Cmd(sql + "; SELECT last_insert_rowid();", ps); return (long)cmd.ExecuteScalar()!; } }
    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object)[] ps)
    {
        lock (sync) { using var cmd = Cmd(sql, ps); using var r = cmd.ExecuteReader(); var l = new List<T>(); while (r.Read()) l.Add(map(r)); return l; }
    }

    // ---- kv
    public string Get(string k, string def = "") => Query("SELECT v FROM kv WHERE k=@k", r => r.GetString(0), ("@k", k)).FirstOrDefault() ?? def;
    public List<(string k, string v)> KvPrefix(string prefix) => Query("SELECT k, v FROM kv WHERE k LIKE @p", r => (r.GetString(0), r.GetString(1)), ("@p", prefix + "%"));
    /// <summary>여러 키를 한 번에 (없는 키는 빠짐)</summary>
    public Dictionary<string, string> GetMany(IEnumerable<string> keys)
    {
        var ks = keys.Distinct().ToList(); var d = new Dictionary<string, string>();
        for (int i = 0; i < ks.Count; i += 500)
        {
            var part = ks.Skip(i).Take(500).ToList();
            var ps = part.Select((k, j) => ($"@k{j}", (object)k)).ToArray();
            foreach (var (k, v) in Query($"SELECT k, v FROM kv WHERE k IN ({string.Join(",", ps.Select(p => p.Item1))})", r => (r.GetString(0), r.GetString(1)), ps)) d[k] = v;
        }
        return d;
    }
    public void KvDelete(string k) => Exec("DELETE FROM kv WHERE k=@k", ("@k", k));
    public void Set(string k, string v) => Exec("INSERT INTO kv(k,v) VALUES(@k,@v) ON CONFLICT(k) DO UPDATE SET v=excluded.v", ("@k", k), ("@v", v));

    // ---- users
    private static User MapUser(SqliteDataReader r) => new() { DiscordId = (ulong)r.GetInt64(0), HomeFriend = r.GetString(1), FriendOk = r.GetInt64(2) != 0, Blocked = r.GetInt64(3) != 0, Misses = (int)r.GetInt64(4), TrainerName = r.GetString(5), CreatedAt = Dt(r.GetValue(6)) };
    public User GetUser(ulong id) => Query("SELECT discord_id,home_friend,friend_ok,blocked,misses,trainer_name,created_at FROM users WHERE discord_id=@i", MapUser, ("@i", (long)id)).FirstOrDefault();
    public User EnsureUser(ulong id)
    {
        var u = GetUser(id); if (u != null) return u;
        Exec("INSERT INTO users(discord_id,created_at) VALUES(@i,@t)", ("@i", (long)id), ("@t", Now()));
        return GetUser(id);
    }
    public void SaveUser(User u) => Exec("UPDATE users SET home_friend=@f,friend_ok=@ok,blocked=@b,misses=@m,trainer_name=@tn WHERE discord_id=@i",
        ("@f", u.HomeFriend ?? ""), ("@ok", u.FriendOk ? 1 : 0), ("@b", u.Blocked ? 1 : 0), ("@m", u.Misses), ("@tn", u.TrainerName ?? ""), ("@i", (long)u.DiscordId));

    // ---- orders
    private const string OrderCols = "id,discord_id,text,describe,key,status,route,pool_id,error,note,created_at,updated_at,claimed_at,kind,channel_id,key_json,game";
    private static Order MapOrder(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), DiscordId = (ulong)r.GetInt64(1), Text = r.GetString(2), Describe = r.GetString(3), Key = r.GetString(4), Status = Enums.ParseStatus(r.GetString(5)), Route = Enums.ParseRoute(r.GetString(6)),
        PoolId = r.IsDBNull(7) ? null : r.GetInt64(7), Error = r.GetString(8), Note = r.GetString(9), CreatedAt = Dt(r.GetValue(10)), UpdatedAt = Dt(r.GetValue(11)), ClaimedAt = DtN(r.GetValue(12)),
        Kind = r.IsDBNull(13) ? "text" : r.GetString(13), ChannelId = r.IsDBNull(14) ? 0 : (ulong)r.GetInt64(14),
        Key2 = r.IsDBNull(15) || r.GetString(15).Length == 0 ? new KeyPoke() : System.Text.Json.JsonSerializer.Deserialize<KeyPoke>(r.GetString(15)) ?? new KeyPoke(),
        Game = r.IsDBNull(16) ? "" : r.GetString(16),
    };
    public void SetOrderGame(long id, string game) => Exec("UPDATE orders SET game=@g WHERE id=@i", ("@g", game), ("@i", id));
    // 게임을 처음부터 같이 넣음 (따로 고치면 그 사이에 기본 게임 신청으로 보일 수 있음: 게임 동시 운영)
    public long AddOrder(ulong user, string text, string describe, string key, Route route, string kind, ulong channel, KeyPoke kp, OrderStatus status = OrderStatus.Queued, string game = "") =>
        Insert("INSERT INTO orders(discord_id,text,describe,key,status,route,created_at,updated_at,kind,channel_id,key_json,game) VALUES(@u,@t,@d,@k,@s,@r,@n,@n,@kind,@ch,@kj,@g)",
            ("@u", (long)user), ("@t", text), ("@d", describe), ("@k", key), ("@s", status.Db()), ("@r", route.Db()), ("@n", Now()), ("@kind", kind), ("@ch", (long)channel), ("@kj", System.Text.Json.JsonSerializer.Serialize(kp ?? new KeyPoke())), ("@g", game ?? ""));
    public void SetKey(long id, KeyPoke kp) => Exec("UPDATE orders SET key_json=@k, updated_at=@n WHERE id=@i", ("@k", System.Text.Json.JsonSerializer.Serialize(kp ?? new KeyPoke())), ("@n", Now()), ("@i", id));
    public void SetDescribe(long id, string d) => Exec("UPDATE orders SET describe=@d WHERE id=@i", ("@d", d), ("@i", id));
    public Order GetOrder(long id) => Query($"SELECT {OrderCols} FROM orders WHERE id=@i", MapOrder, ("@i", id)).FirstOrDefault();
    /// <summary>그 시각 뒤에 들어온 신청의 게임 (빈 칸은 ""). 게임 동시 운영에서 신청 속도를 잴 때</summary>
    public List<string> GamesCreatedSince(DateTime utc) => Query("SELECT COALESCE(game,'') FROM orders WHERE created_at >= @t", r => r.GetString(0), ("@t", utc.ToString("o")));
    public List<Order> Orders(params OrderStatus[] st)
    {
        if (st.Length == 0) return Query($"SELECT {OrderCols} FROM orders ORDER BY id", MapOrder);
        var inList = string.Join(",", st.Select(s => $"'{s.Db()}'"));
        return Query($"SELECT {OrderCols} FROM orders WHERE status IN ({inList}) ORDER BY COALESCE(priority, id), id", MapOrder);
    }
    public List<Order> OrdersOf(ulong user, bool activeOnly) => Query($"SELECT {OrderCols} FROM orders WHERE discord_id=@u" + (activeOnly ? " AND status NOT IN ('done','failed','cancelled')" : "") + " ORDER BY id DESC", MapOrder, ("@u", (long)user));
    public int OrdersToday(ulong user) => (int)Query("SELECT COUNT(*) FROM orders WHERE discord_id=@u AND created_at>=@d AND status<>'cancelled'", r => r.GetInt64(0), ("@u", (long)user), ("@d", DateTime.UtcNow.Date.ToString("o")))[0];
    public void SetStatus(long id, OrderStatus s, string error = null, string note = null)
    {
        Exec("UPDATE orders SET status=@s, updated_at=@n" + (error != null ? ", error=@e" : "") + (note != null ? ", note=@o" : "") + " WHERE id=@i",
            ("@s", s.Db()), ("@n", Now()), ("@e", error ?? ""), ("@o", note ?? ""), ("@i", id));
    }
    /// <summary>상태가 아직 expected일 때만 바꿈 (그 사이 취소된 신청을 되살리지 않게). 바뀌었으면 true</summary>
    public bool SetStatusIf(long id, OrderStatus expected, OrderStatus s, string error = null, string note = null)
    {
        lock (sync)
        {
            using var cmd = Cmd("UPDATE orders SET status=@s, updated_at=@n" + (error != null ? ", error=@er" : "") + (note != null ? ", note=@o" : "") + " WHERE id=@i AND status=@e",
                ("@s", s.Db()), ("@n", Now()), ("@i", id), ("@e", expected.Db()), ("@er", error ?? ""), ("@o", note ?? ""));
            return cmd.ExecuteNonQuery() > 0;
        }
    }
    public void SetOrderPool(long id, long? poolId) => Exec("UPDATE orders SET pool_id=@p, updated_at=@n WHERE id=@i", ("@p", poolId), ("@n", Now()), ("@i", id));
    public void SetClaimed(long id, DateTime? at) => Exec("UPDATE orders SET claimed_at=@c WHERE id=@i", ("@c", at?.ToString("o")), ("@i", id));
    public int QueuePosition(long id) => (int)Query("SELECT COUNT(*) FROM orders o, orders me WHERE me.id=@i AND o.status NOT IN ('done','failed','cancelled') AND (COALESCE(o.priority,o.id) < COALESCE(me.priority,me.id) OR (COALESCE(o.priority,o.id) = COALESCE(me.priority,me.id) AND o.id < me.id))", r => r.GetInt64(0), ("@i", id))[0] + 1;
    /// <summary>예상 시간 계산용: HOME까지 올라갔다가 끝난 신청들 (끝난 순서대로, 최근 n건). (생성 시각, HOME 도착, 끝난 시각)</summary>
    public List<(DateTime created, DateTime claimed, DateTime finished)> RecentRuns(int n) =>
        Query("SELECT created_at, claimed_at, updated_at FROM (SELECT created_at, claimed_at, updated_at FROM orders WHERE claimed_at IS NOT NULL AND status IN ('done','failed','cancelled') ORDER BY updated_at DESC LIMIT @n) ORDER BY updated_at",
            r => (Dt(r.GetValue(0)), Dt(r.GetValue(1)), Dt(r.GetValue(2))), ("@n", n));
    public double PriorityOf(long id) => Query("SELECT COALESCE(priority, id) FROM orders WHERE id=@i", r => r.GetDouble(0), ("@i", id)).FirstOrDefault();
    public void SetPriority(long id, double p) => Exec("UPDATE orders SET priority=@p WHERE id=@i", ("@p", p), ("@i", id));

    // ---- pool
    private const string PoolCols = "id,pk_bytes,format,species,form,shiny,key,location,box,slot,reserved_by,created_at,verified_at";
    private static PoolItem MapPool(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), PkBytes = (byte[])r.GetValue(1), Format = r.GetString(2), Species = (ushort)r.GetInt64(3), Form = (byte)r.GetInt64(4), Shiny = r.GetInt64(5) != 0, Key = r.GetString(6),
        Location = Enums.ParseLocation(r.GetString(7)), Box = (int)r.GetInt64(8), Slot = (int)r.GetInt64(9), ReservedBy = r.IsDBNull(10) ? null : r.GetInt64(10), CreatedAt = Dt(r.GetValue(11)), VerifiedAt = DtN(r.GetValue(12)),
    };
    public long AddPool(byte[] bytes, string format, ushort species, byte form, bool shiny, string key, Location loc = Location.Pc) =>
        Insert("INSERT INTO pool(pk_bytes,format,species,form,shiny,key,location,created_at) VALUES(@b,@f,@s,@fo,@sh,@k,@l,@n)", ("@b", bytes), ("@f", format), ("@s", (long)species), ("@fo", (long)form), ("@sh", shiny ? 1 : 0), ("@k", key), ("@l", loc.Db()), ("@n", Now()));
    public PoolItem GetPool(long id) => Query($"SELECT {PoolCols} FROM pool WHERE id=@i", MapPool, ("@i", id)).FirstOrDefault();
    public List<PoolItem> Pool(Location? loc = null, bool freeOnly = false)
    {
        var w = new List<string>(); if (loc != null) w.Add($"location='{loc.Value.Db()}'"); if (freeOnly) w.Add("reserved_by IS NULL");
        return Query($"SELECT {PoolCols} FROM pool" + (w.Count > 0 ? " WHERE " + string.Join(" AND ", w) : "") + " ORDER BY id", MapPool);
    }
    public void Reserve(long poolId, long? orderId) => Exec("UPDATE pool SET reserved_by=@o WHERE id=@i", ("@o", orderId), ("@i", poolId));
    public void SetLocation(long poolId, Location loc, int box, int slot, bool verified = false) =>
        Exec("UPDATE pool SET location=@l, box=@b, slot=@s" + (verified ? ", verified_at=@v" : "") + " WHERE id=@i", ("@l", loc.Db()), ("@b", box), ("@s", slot), ("@v", Now()), ("@i", poolId));
    public PoolItem PoolAt(Location loc, int box, int slot) => Query($"SELECT {PoolCols} FROM pool WHERE location=@l AND box=@b AND slot=@s", MapPool, ("@l", loc.Db()), ("@b", box), ("@s", slot)).FirstOrDefault();

    // ---- trades
    public long AddTrade(long orderId, long? poolId, Route route) => Insert("INSERT INTO trades(order_id,pool_id,route,started_at) VALUES(@o,@p,@r,@n)", ("@o", orderId), ("@p", poolId), ("@r", route.Db()), ("@n", Now()));
    public void EndTrade(long id, string result, string shot = "") => Exec("UPDATE trades SET finished_at=@n, result=@r, screenshot=@s WHERE id=@i", ("@n", Now()), ("@r", result), ("@s", shot), ("@i", id));
    public int TradesLastHour() => (int)Query("SELECT COUNT(*) FROM trades WHERE started_at>=@d", r => r.GetInt64(0), ("@d", DateTime.UtcNow.AddHours(-1).ToString("o")))[0];
    public List<(string key, int n)> TopKeys(int days, int take) => Query("SELECT key, COUNT(*) n FROM orders WHERE created_at>=@d AND key<>'' GROUP BY key ORDER BY n DESC LIMIT @t", r => (r.GetString(0), (int)r.GetInt64(1)), ("@d", DateTime.UtcNow.AddDays(-days).ToString("o")), ("@t", take));
}
