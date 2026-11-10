using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using Mmo.Proto.World;

namespace Mmo.Tools;

/// <summary>
/// P1-3a2 核对探针：几条到网关 /world/stream 的连接，扮演同一分线里「周围的人」。
/// 和手机一样走 WebSocket + Protobuf 二进制帧，不直连世界服。
/// </summary>
internal static class Program
{
    static readonly Dictionary<string, string> Args = ParseArgs(Environment.GetCommandLineArgs().Skip(1).ToArray());

    static string Arg(string name, string fallback = "") =>
        Args.TryGetValue(name, out var value) ? value : fallback;

    static ulong ArgUlong(string name) => ulong.TryParse(Arg(name), out var value) ? value : 0;

    static int ArgInt(string name, int fallback) => int.TryParse(Arg(name), out var value) ? value : fallback;

    static async Task<int> Main()
    {
        var name = Arg("--case");
        try
        {
            return name switch
            {
                "stream" => await CaseStreamAsync(),
                "move" => await CaseMoveAsync(),
                "rate" => await CaseRateAsync(),
                "isolate" => await CaseIsolateAsync(),
                "speed" => await CaseSpeedAsync(),
                "leave" => await CaseLeaveAsync(),
                "no_line" => await CaseNoLineAsync(),
                "tier_stages" => await CaseTierStagesAsync(),
                "tier_spoof" => await CaseTierSpoofAsync(),
                "tier_isolate" => await CaseTierIsolateAsync(),
                "flight_frame" => await CaseFlightFrameAsync(),
                _ => Fail("unknown_case=" + name),
            };
        }
        catch (Exception ex)
        {
            return Fail("exception=" + ex.GetType().Name + " " + ex.Message);
        }
    }

    static string Url(string token, ulong characterId) =>
        Arg("--base") + "?token=" + Uri.EscapeDataString(token) + "&character_id=" + characterId;

    static async Task<WorldSession> ConnectAsync(string label, string token, ulong characterId) =>
        await WorldSession.ConnectAsync(Url(token, characterId), label);

    /// <summary>
    /// P1-3c：没飞行权限的人不能靠改包在上报帧里塞一个高 y 把自己抬起来。
    /// 服务端按 flight_forbidden 拉回，别人看到的仍是地面坐标与 flying=false；
    /// 顺带核对技术架构第 4 节写定的 flying / can_fly 少量状态确实随快照下发。
    /// </summary>
    static async Task<int> CaseFlightFrameAsync()
    {
        var a = ArgUlong("--char-a");
        var b = ArgUlong("--char-b");
        await using var sessionA = await ConnectAsync("a", Arg("--token-a"), a);
        await using var sessionB = await ConnectAsync("b", Arg("--token-b"), b);
        if (await sessionA.SnapshotAsync(_ => true, 4000) is null
            || await sessionB.SnapshotAsync(_ => true, 4000) is null)
        {
            return Fail("flight_frame=fail stream_open");
        }

        // 绕开 /world/fly，直接在上报帧里报一个高 y。
        await sessionA.SendAsync(0, 5f, 0, 0, 41);
        var rejected = await sessionA.RejectedAsync("flight_forbidden", 4000);

        // B 这一秒里的快照都得是地面：不许出现「自己飞起来了别人也看到」。
        var peerY = -1f;
        var peerFlying = false;
        var peerCanFly = false;
        var seen = 0;
        var end = Environment.TickCount64 + 700;
        while (Environment.TickCount64 < end)
        {
            var frame = await sessionB.ReadAsync((int)Math.Max(1, end - Environment.TickCount64));
            if (frame?.Snapshot is not { } snapshot)
            {
                continue;
            }

            foreach (var avatar in snapshot.Avatars)
            {
                if (avatar.CharacterId != a)
                {
                    continue;
                }

                peerY = Math.Max(peerY, avatar.Y);
                peerFlying |= avatar.Flying;
                peerCanFly |= avatar.CanFly;
                seen++;
            }
        }

        var ok = rejected is not null && seen > 0 && peerY == 0f
                 && !peerFlying && !peerCanFly;
        return ok
            ? Ok("flight_frame=ok rejected=1 peer_y=" + peerY
                 + " flying=false can_fly=false samples=" + seen)
            : Fail("flight_frame=fail rejected=" + (rejected is not null)
                   + " peer_y=" + peerY + " flying=" + peerFlying
                   + " can_fly=" + peerCanFly + " samples=" + seen);
    }

    static async Task<int> CaseStreamAsync()
    {
        var a = ArgUlong("--char-a");
        var b = ArgUlong("--char-b");
        await using var sessionA = await ConnectAsync("a", Arg("--token-a"), a);
        await using var sessionB = await ConnectAsync("b", Arg("--token-b"), b);

        var firstA = await sessionA.SnapshotAsync(_ => true, 4000);
        var firstB = await sessionB.SnapshotAsync(_ => true, 4000);
        if (firstA is null || firstB is null)
        {
            return Fail("stream_open=fail a=" + (firstA is not null) + " b=" + (firstB is not null));
        }

        var line = Arg("--line");
        var lineOk = firstA.LineId == line && firstB.LineId == line;
        var hzOk = firstA.SyncHz >= 5 && firstA.SyncHz <= 10 && firstB.SyncHz == firstA.SyncHz;
        var seeOther = firstA.Avatars.Any(v => v.CharacterId == b)
                       && firstB.Avatars.Any(v => v.CharacterId == a);
        var noSelf = firstA.SelfCharacterId == a && firstB.SelfCharacterId == b
                     && firstA.Avatars.All(v => v.CharacterId != a)
                     && firstB.Avatars.All(v => v.CharacterId != b);
        return lineOk && hzOk && seeOther && noSelf
            ? Ok("stream_open=ok line=" + firstA.LineId + " hz=" + firstA.SyncHz
                 + " peers_a=" + firstA.Avatars.Count + " peers_b=" + firstB.Avatars.Count)
            : Fail("stream_open=fail line=" + lineOk + " hz=" + hzOk
                   + " see_other=" + seeOther + " no_self=" + noSelf
                   + " line_a=" + firstA.LineId + " line_b=" + firstB.LineId);
    }

    static async Task<int> CaseMoveAsync()
    {
        var a = ArgUlong("--char-a");
        var b = ArgUlong("--char-b");
        await using var sessionA = await ConnectAsync("a", Arg("--token-a"), a);
        await using var sessionB = await ConnectAsync("b", Arg("--token-b"), b);
        await sessionA.SnapshotAsync(_ => true, 4000);
        await sessionB.SnapshotAsync(_ => true, 4000);

        var steps = new[] { 0.4f, 0.8f, 1.2f };
        var facings = new[] { 0f, 1f, 2f };
        ulong seq = 1;
        for (var i = 0; i < steps.Length; i++)
        {
            await sessionA.SendAsync(steps[i], 0, 0, facings[i], seq++);
            await Task.Delay(180);
        }

        var seenX = new List<float>();
        var last = await sessionB.SnapshotAsync(snapshot =>
        {
            var peer = snapshot.Avatars.FirstOrDefault(v => v.CharacterId == a);
            if (peer is null)
            {
                return false;
            }

            seenX.Add(peer.X);
            return peer.X >= 1.1f && peer.Facing >= 1.9f && peer.Seq >= 3;
        }, 6000);

        var moved = last is not null && seenX.Count >= 2 && seenX[^1] > seenX[0];
        return moved
            ? Ok("move_visible=ok samples=" + seenX.Count
                 + " first=" + seenX[0].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                 + " last=" + seenX[^1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                 + " seq=" + last!.Avatars.First(v => v.CharacterId == a).Seq)
            : Fail("move_visible=fail samples=" + seenX.Count + " last=" + (last is not null));
    }

    static async Task<int> CaseRateAsync()
    {
        var hz = ArgInt("--hz", 8);
        var window = ArgInt("--window-ms", 2000);
        await using var session = await ConnectAsync("rate", Arg("--token-a"), ArgUlong("--char-a"));

        // 先来一阵密集上报：广播频率不随客户端上报快慢变化。
        ulong seq = 1;
        for (var i = 0; i < 30; i++)
        {
            await session.SendAsync(i * 0.002f, 0, 0, 0, seq++);
        }

        var start = Environment.TickCount64;
        var count = 0;
        var hzSeen = new HashSet<uint>();
        while (true)
        {
            var left = window - (int)(Environment.TickCount64 - start);
            if (left <= 0)
            {
                break;
            }

            var frame = await session.ReadAsync(left);
            if (frame?.Snapshot is not { } snapshot)
            {
                continue;
            }

            count++;
            hzSeen.Add(snapshot.SyncHz);
        }

        var expected = hz * window / 1000.0;
        var rateOk = count >= expected - 3 && count <= expected + 3;
        var hzOk = hzSeen.Count == 1 && hzSeen.Contains((uint)hz);
        return rateOk && hzOk
            ? Ok("rate=ok hz=" + hz + " window_ms=" + window + " snapshots=" + count
                 + " expected=" + expected.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
            : Fail("rate=fail hz=" + hz + " window_ms=" + window + " snapshots=" + count
                   + " expected=" + expected.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                   + " hz_seen=" + string.Join(",", hzSeen));
    }

    static async Task<int> CaseIsolateAsync()
    {
        var a = ArgUlong("--char-a");
        var b = ArgUlong("--char-b");
        var c = ArgUlong("--char-c");
        await using var sessionA = await ConnectAsync("a", Arg("--token-a"), a);
        await using var sessionB = await ConnectAsync("b", Arg("--token-b"), b);
        await using var sessionC = await ConnectAsync("c", Arg("--token-c"), c);

        var sa = await sessionA.SnapshotAsync(_ => true, 4000);
        var sb = await sessionB.SnapshotAsync(_ => true, 4000);
        var sc = await sessionC.SnapshotAsync(_ => true, 4000);
        if (sa is null || sb is null || sc is null)
        {
            return Fail("isolate=fail snapshots=" + (sa is not null) + (sb is not null) + (sc is not null));
        }

        var sameLine = sa.LineId == sb.LineId && sc.LineId != sa.LineId;
        var cBlind = sc.Avatars.All(v => v.CharacterId != a && v.CharacterId != b);
        var bSeesA = sb.Avatars.Any(v => v.CharacterId == a);
        var bBlindToC = sb.Avatars.All(v => v.CharacterId != c);
        return sameLine && cBlind && bSeesA && bBlindToC
            ? Ok("isolate=ok line_ab=" + sa.LineId + " line_c=" + sc.LineId
                 + " peers_c=" + sc.Avatars.Count + " peers_b=" + sb.Avatars.Count)
            : Fail("isolate=fail same_line=" + sameLine + " c_blind=" + cBlind
                   + " b_sees_a=" + bSeesA + " b_blind_to_c=" + bBlindToC
                   + " line_ab=" + sa.LineId + " line_c=" + sc.LineId);
    }

    static async Task<int> CaseSpeedAsync()
    {
        var a = ArgUlong("--char-a");
        var b = ArgUlong("--char-b");
        await using var sessionA = await ConnectAsync("a", Arg("--token-a"), a);
        await using var sessionB = await ConnectAsync("b", Arg("--token-b"), b);
        await sessionA.SnapshotAsync(_ => true, 4000);
        await sessionB.SnapshotAsync(_ => true, 4000);

        await sessionA.SendAsync(0.3f, 0, 0, 0.5f, 1);
        await Task.Delay(180);
        await sessionA.SendAsync(100000f, 0, 0, 0, 2);
        var speedReject = await sessionA.RejectedAsync("speed_limit", 4000);

        // 把 B 这一秒里的快照都看一遍：别人从头到尾都不该看到那个瞬移坐标。
        var peerX = 0f;
        var seen = 0;
        var end = Environment.TickCount64 + 900;
        while (Environment.TickCount64 < end)
        {
            var frame = await sessionB.ReadAsync((int)Math.Max(1, end - Environment.TickCount64));
            if (frame?.Snapshot is not { } snapshot)
            {
                continue;
            }

            foreach (var avatar in snapshot.Avatars)
            {
                if (avatar.CharacterId == a)
                {
                    peerX = Math.Max(peerX, avatar.X);
                    seen++;
                }
            }
        }

        await sessionA.SendAsync(float.NaN, 0, 0, 0, 3);
        var finiteReject = await sessionA.RejectedAsync("invalid_position", 4000);

        var correctedOk = speedReject?.Corrected is { } corrected && corrected.X < 1f;
        var ok = speedReject is not null && finiteReject is not null && correctedOk
                 && seen > 0 && peerX < 1f;
        return ok
            ? Ok("position_check=ok speed_limit=1 invalid_position=1 corrected_x="
                 + (speedReject?.Corrected?.X ?? -1)
                 + " peer_max_x=" + peerX + " peer_samples=" + seen)
            : Fail("position_check=fail speed=" + (speedReject is not null)
                   + " finite=" + (finiteReject is not null)
                   + " corrected=" + correctedOk + " peer_max_x=" + peerX + " samples=" + seen);
    }

    static async Task<int> CaseLeaveAsync()
    {
        var a = ArgUlong("--char-a");
        await using var sessionA = await ConnectAsync("a", Arg("--token-a"), a);
        await using var sessionB = await ConnectAsync("b", Arg("--token-b"), ArgUlong("--char-b"));
        await sessionA.SnapshotAsync(_ => true, 4000);
        await sessionB.SnapshotAsync(_ => true, 4000);

        using var http = new HttpClient();
        var body = "{\"character_id\":" + a + "}";
        var response = await http.PostAsync(
            Arg("--http") + "/world/leave?token=" + Uri.EscapeDataString(Arg("--token-a")),
            new StringContent(body, Encoding.UTF8, "application/json"));
        var leftHttp = (int)response.StatusCode == 200;

        var gone = await sessionB.SnapshotAsync(
            s => s.Avatars.All(v => v.CharacterId != a), 5000);
        var closed = await sessionA.RejectedAsync("left_line", 5000);
        return leftHttp && gone is not null && closed is not null
            ? Ok("leave_visible=ok http=" + (int)response.StatusCode
                 + " peers_after=" + gone!.Avatars.Count + " reason=" + closed!.Reason)
            : Fail("leave_visible=fail http=" + (int)response.StatusCode
                   + " gone=" + (gone is not null) + " closed=" + (closed is not null));
    }

    static async Task<int> CaseNoLineAsync()
    {
        await using var session = await ConnectAsync("no_line", Arg("--token-a"), ArgUlong("--char-a"));
        var snapshot = await session.SnapshotAsync(_ => true, 2500);
        return snapshot is null && session.Completed
            ? Ok("no_line=ok stream_closed=1")
            : Fail("no_line=fail snapshot=" + (snapshot is not null) + " completed=" + session.Completed);
    }

    /// <summary>
    /// 同屏三档：A 从 B 身边按固定坐标走远，B 每个阶段看到的档位应与期望一致。
    /// 阶段坐标由核对脚本给定，改配置重启后同一段距离的档位会变。
    /// </summary>
    static async Task<int> CaseTierStagesAsync()
    {
        var a = ArgUlong("--char-a");
        var b = ArgUlong("--char-b");
        var expectedName = Arg("--name-a", "");
        var stages = Arg("--stage-x", "0,3.5,6").Split(',')
            .Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var expect = Arg("--expect", "1,2,3").Split(',').Select(int.Parse).ToArray();
        if (stages.Length != expect.Length)
        {
            return Fail("tier_stages=fail stages_and_expect_mismatch");
        }

        await using var sessionA = await ConnectAsync("a", Arg("--token-a"), a);
        await using var sessionB = await ConnectAsync("b", Arg("--token-b"), b);
        if (await sessionA.SnapshotAsync(_ => true, 4000) is null
            || await sessionB.SnapshotAsync(_ => true, 4000) is null)
        {
            return Fail("tier_stages=fail stream_open");
        }

        // 前面的用例可能把 A 留在别处：先小步走回原点，让三档阈值只由配置决定。
        var first = await sessionB.SnapshotAsync(_ => true, 4000);
        var from = first?.Avatars.FirstOrDefault(v => v.CharacterId == a)?.X ?? 0f;
        var seen = new List<int>();
        var namesOk = true;
        var current = 0f;
        ulong seq = 1;
        while (Math.Abs(from) > 0.001f)
        {
            var back = (float)Math.Min(0.5, Math.Abs(from));
            from -= Math.Sign(from) * back;
            await sessionA.SendAsync(from, 0, 0, 0, seq++);
            await Task.Delay(180);
        }

        foreach (var target in stages)
        {
            // 服务端按 5–10Hz 判瞬时速度，这里小步慢走，别撞上 speed_limit。
            while (current < target - 0.001f)
            {
                current += Math.Min(0.5f, target - current);
                await sessionA.SendAsync(current, 0, 0, 0, seq++);
                await Task.Delay(180);
            }

            var snapshot = await sessionB.SnapshotAsync(s =>
            {
                var peer = s.Avatars.FirstOrDefault(v => v.CharacterId == a);
                return peer is not null && peer.X >= target - 0.01f;
            }, 6000);
            var avatar = snapshot?.Avatars.FirstOrDefault(v => v.CharacterId == a);
            if (avatar is null)
            {
                return Fail("tier_stages=fail no_avatar_at=" + target);
            }

            seen.Add((int)avatar.AppearanceTier);
            if (expectedName.Length > 0 && avatar.DisplayName != expectedName)
            {
                namesOk = false;
            }

            // 仍推同分线全量名单，不按距离删人；自己不出现在自己的快照里。
            if (snapshot!.Avatars.Any(v => v.CharacterId == b))
            {
                return Fail("tier_stages=fail self_in_snapshot");
            }
        }

        var ok = seen.SequenceEqual(expect) && namesOk;
        return ok
            ? Ok("tier_stages=ok seen=" + string.Join(",", seen)
                 + " expect=" + string.Join(",", expect) + " name=" + namesOk)
            : Fail("tier_stages=fail seen=" + string.Join(",", seen)
                   + " expect=" + string.Join(",", expect) + " name=" + namesOk);
    }

    /// <summary>
    /// 改包：在上报帧里塞一个伪造的档位字段与名字字段（proto 未定义的未知字段）。
    /// 网关与世界服只认 PositionReport 的五个字段，别人看到的档位仍按距离算、
    /// 名字仍是角色服给的那一份。
    /// </summary>
    static async Task<int> CaseTierSpoofAsync()
    {
        var a = ArgUlong("--char-a");
        var b = ArgUlong("--char-b");
        var expectedName = Arg("--name-a", "");
        await using var sessionA = await ConnectAsync("a", Arg("--token-a"), a);
        await using var sessionB = await ConnectAsync("b", Arg("--token-b"), b);
        if (await sessionA.SnapshotAsync(_ => true, 4000) is null
            || await sessionB.SnapshotAsync(_ => true, 4000) is null)
        {
            return Fail("tier_spoof=fail stream_open");
        }

        // WorldClientFrame { PositionReport { ... } }，内层尾部追加两个未知字段：
        // field 9 varint 3（伪装成 NAME 档）、field 10 string "hacked"（伪装名字）。
        var inner = new PositionReport { X = 0, Y = 0, Z = 0, Facing = 0, Seq = 99 }
            .ToByteArray().ToList();
        inner.Add(0x48);
        inner.Add(0x03);
        var forged = Encoding.UTF8.GetBytes("hacked");
        inner.Add(0x52);
        inner.Add((byte)forged.Length);
        inner.AddRange(forged);
        var outer = new List<byte> { 0x0A, (byte)inner.Count };
        outer.AddRange(inner);
        await sessionA.SendRawAsync(outer.ToArray());

        var snapshot = await sessionB.SnapshotAsync(
            s => s.Avatars.Any(v => v.CharacterId == a && v.Seq >= 99), 6000);
        var avatar = snapshot?.Avatars.FirstOrDefault(v => v.CharacterId == a);
        var tierOk = avatar is not null && avatar.AppearanceTier == AppearanceTier.Full;
        var nameOk = avatar is not null && avatar.DisplayName == expectedName
                     && snapshot!.Avatars.All(v => v.DisplayName != "hacked");
        return tierOk && nameOk
            ? Ok("tier_spoof=ok tier=" + (avatar?.AppearanceTier.ToString() ?? "?")
                 + " name=" + (avatar?.DisplayName ?? ""))
            : Fail("tier_spoof=fail tier=" + (avatar?.AppearanceTier.ToString() ?? "?")
                   + " name=" + (avatar?.DisplayName ?? "") + " want=" + expectedName);
    }

    /// <summary>分线隔离：另一条线的人收不到这条线的快照，本线名单仍是全量。</summary>
    static async Task<int> CaseTierIsolateAsync()
    {
        var a = ArgUlong("--char-a");
        var b = ArgUlong("--char-b");
        var c = ArgUlong("--char-c");
        await using var sessionA = await ConnectAsync("a", Arg("--token-a"), a);
        await using var sessionB = await ConnectAsync("b", Arg("--token-b"), b);
        await using var sessionC = await ConnectAsync("c", Arg("--token-c"), c);

        var sb = await sessionB.SnapshotAsync(_ => true, 4000);
        var sc = await sessionC.SnapshotAsync(_ => true, 4000);
        if (sb is null || sc is null)
        {
            return Fail("tier_isolate=fail stream_open");
        }

        var peerA = sb.Avatars.FirstOrDefault(v => v.CharacterId == a);
        var fullList = sb.Avatars.Count == 1 && sb.Avatars.All(v => v.CharacterId != b);
        var tiered = peerA is not null
                     && peerA.AppearanceTier != AppearanceTier.Unspecified
                     && peerA.DisplayName.Length > 0;
        var isolated = sc.Avatars.All(v => v.CharacterId != a && v.CharacterId != b)
                       && sb.Avatars.All(v => v.CharacterId != c);
        return fullList && tiered && isolated
            ? Ok("tier_isolate=ok peers_b=" + sb.Avatars.Count
                 + " tier=" + (peerA?.AppearanceTier.ToString() ?? "?")
                 + " peers_c=" + sc.Avatars.Count)
            : Fail("tier_isolate=fail full=" + fullList + " tiered=" + tiered
                   + " isolated=" + isolated);
    }

    static int Ok(string detail)
    {
        Console.WriteLine(detail);
        Console.WriteLine("verdict=ok");
        return 0;
    }

    static int Fail(string detail)
    {
        Console.WriteLine(detail);
        Console.WriteLine("verdict=fail");
        return 1;
    }

    static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            result[args[i]] = args[i + 1];
        }

        return result;
    }
}

internal sealed class WorldSession : IAsyncDisposable
{
    readonly ClientWebSocket _socket = new();
    readonly Channel<WorldServerFrame> _inbox = Channel.CreateUnbounded<WorldServerFrame>();
    readonly CancellationTokenSource _cts = new();
    Task? _pump;
    ulong _seq;

    public bool Completed { get; private set; }

    WorldSession(string label)
    {
        Label = label;
    }

    public string Label { get; }

    public static async Task<WorldSession> ConnectAsync(string url, string label)
    {
        var session = new WorldSession(label);
        using var timeout = new CancellationTokenSource(5000);
        await session._socket.ConnectAsync(new Uri(url), timeout.Token);
        // 连接成功后才起收包循环，否则 Open 之前就退出了。
        session._pump = Task.Run(session.PumpAsync);
        return session;
    }

    public Task SendAsync(float x, float y, float z, float facing, ulong seq)
    {
        var frame = new WorldClientFrame
        {
            Position = new PositionReport
            {
                X = x, Y = y, Z = z, Facing = facing, Seq = seq,
            },
        };
        _seq = Math.Max(_seq, seq);
        return _socket.SendAsync(
            new ArraySegment<byte>(frame.ToByteArray()),
            WebSocketMessageType.Binary, true, _cts.Token);
    }

    /// <summary>改包用例用：原样发一段二进制帧，绕过生成的消息类。</summary>
    public Task SendRawAsync(byte[] payload) =>
        _socket.SendAsync(
            new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, _cts.Token);

    public async Task<WorldServerFrame?> ReadAsync(int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            return await _inbox.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async Task<PositionSnapshot?> SnapshotAsync(Func<PositionSnapshot, bool> match, int timeoutMs)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var left = (int)(end - Environment.TickCount64);
            if (left <= 0)
            {
                return null;
            }

            var frame = await ReadAsync(left);
            if (frame?.Snapshot is { } snapshot && match(snapshot))
            {
                return snapshot;
            }

            if (frame is null && Completed)
            {
                return null;
            }
        }
    }

    public async Task<PositionRejected?> RejectedAsync(string reason, int timeoutMs)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var left = (int)(end - Environment.TickCount64);
            if (left <= 0)
            {
                return null;
            }

            var frame = await ReadAsync(left);
            if (frame?.Rejected is { } rejected && rejected.Reason == reason)
            {
                return rejected;
            }

            if (frame is null && Completed)
            {
                return null;
            }
        }
    }

    async Task PumpAsync()
    {
        var buffer = new byte[4096];
        using var assembled = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                assembled.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    assembled.Write(buffer, 0, received.Count);
                    if (assembled.Length > 64 * 1024)
                    {
                        return;
                    }
                }
                while (!received.EndOfMessage);

                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    _inbox.Writer.TryWrite(WorldServerFrame.Parser.ParseFrom(assembled.ToArray()));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
            // 服务端主动关流（没进分线、离开分线）在核对里是正常结果。
        }
        catch (Exception ex)
        {
            Console.WriteLine("probe_read_error=" + Label + " " + ex.GetType().Name);
        }
        finally
        {
            Completed = true;
            _inbox.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(1000);
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
        }
        catch (Exception)
        {
        }

        _socket.Dispose();
        _cts.Dispose();
    }
}
