using System.Collections.Generic;
using Mmo.Proto.World;

namespace Mmo.Net
{
    /// <summary>快照里一个人的最新坐标与朝向。表现层拿它去插值，不在这里算移动。</summary>
    public struct PeerPosition
    {
        public ulong CharacterId;
        public float X;
        public float Y;
        public float Z;
        public float Facing;
        public ulong Seq;

        public PeerPosition(ulong characterId, float x, float y, float z, float facing, ulong seq)
        {
            CharacterId = characterId;
            X = x;
            Y = y;
            Z = z;
            Facing = facing;
            Seq = seq;
        }
    }

    /// <summary>
    /// 周围的人：按角色编号留下最新一份快照。同屏三档、模型与 UI 都还在后面的卡，
    /// 这一行只负责让「另一个人在动」这件事在客户端有据可查。
    /// </summary>
    public sealed class WorldPeerPositions
    {
        readonly Dictionary<ulong, PeerPosition> peers = new Dictionary<ulong, PeerPosition>();

        public string LineId { get; private set; } = string.Empty;
        public uint LastSyncHz { get; private set; }
        public ulong LastServerTimeMs { get; private set; }
        public int Snapshots { get; private set; }
        public int Count => peers.Count;

        public bool TryGet(ulong characterId, out PeerPosition position)
        {
            return peers.TryGetValue(characterId, out position);
        }

        /// <summary>快照是全量名单：这一帧没出现的人就是离开了这条分线。</summary>
        public bool Apply(PositionSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.LineId))
            {
                return false;
            }

            var seen = new HashSet<ulong>();
            foreach (var avatar in snapshot.Avatars)
            {
                if (avatar.CharacterId == 0)
                {
                    continue;
                }

                peers[avatar.CharacterId] = new PeerPosition(
                    avatar.CharacterId, avatar.X, avatar.Y, avatar.Z, avatar.Facing, avatar.Seq);
                seen.Add(avatar.CharacterId);
            }

            var gone = new List<ulong>();
            foreach (var id in peers.Keys)
            {
                if (!seen.Contains(id))
                {
                    gone.Add(id);
                }
            }

            foreach (var id in gone)
            {
                peers.Remove(id);
            }

            LineId = snapshot.LineId;
            LastSyncHz = snapshot.SyncHz;
            LastServerTimeMs = snapshot.ServerTimeMs;
            Snapshots++;
            return true;
        }
    }
}
