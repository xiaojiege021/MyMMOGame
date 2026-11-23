using System.Diagnostics;
using Mmo.Proto.Battle;
using Mmo.Proto.Character;
using TalismanPool = Mmo.Proto.Character.TalismanPool;

namespace Battle;

// 14.8 D3c：Encounter 与四个小类型按域拆进 Encounter/ 目录（纯搬代码，判定与回包不变）。
// 这个文件只留 usings 与这条指针；核对脚本仍按老路径读它，由 lib-common 的 src_agg 展开。
