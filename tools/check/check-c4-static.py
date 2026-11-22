#!/usr/bin/env python3
"""C4 静态段：强化等级挂背包行 id，不挂格子。

这一份只读仓库里的文件，不连库、不启动进程；连库段在 check-c4.sh。
被 check-c4.sh 当子进程跑，失败时把最后一行当原因打出去。
"""
import json
import os
import re

root = os.environ['ROOT']


def read(path):
    with open(os.path.join(root, path), encoding='utf-8') as handle:
        return handle.read()


def need(ok, message):
    if not ok:
        raise AssertionError(message)


# 1. 迁移清单：两张表各新增一条分卡迁移，已应用的版本一条都没改。
manifest = json.loads(read('db/migrations.json'))
entries = {s['key']: s['entries'] for s in manifest['schemas']}
char = {e['file']: e for e in entries['character']}
trade = {e['file']: e for e in entries['trade']}
need('db/character/enhance_item.sql' in char, '迁移清单里没有 db/character/enhance_item.sql')
need('db/trade/auction_item.sql' in trade, '迁移清单里没有 db/trade/auction_item.sql')
need(char['db/character/enhance_item.sql'].get('reapply', False) is False, 'enhance_item 必须是分卡迁移（只跑一次）')
need(trade['db/trade/auction_item.sql'].get('reapply', False) is False, 'auction_item 必须是分卡迁移（只跑一次）')

# 2. 角色 schema：等级改挂 inventory_id，老库先回填再收紧；格子列删掉。
sql = read('db/character/enhance_item.sql')
for piece in ('ADD COLUMN IF NOT EXISTS inventory_id bigint',
              'SET inventory_id = i.id',
              'DELETE FROM "角色".enhance WHERE inventory_id IS NULL',
              'ALTER COLUMN inventory_id SET NOT NULL',
              'ADD CONSTRAINT enhance_pkey PRIMARY KEY (inventory_id)',
              'FOREIGN KEY (inventory_id) REFERENCES "角色".inventory (id) ON DELETE CASCADE',
              'DROP COLUMN IF EXISTS slot'):
    need(piece in sql, 'enhance_item.sql 里缺：' + piece)
need("conname <> 'enhance_character_id_fkey'" in sql, '旧的主键/外键要按定义摘掉，指向角色表的那条留着')

# 3. 交易 schema：挂单记住挂的是哪一件。
tsql = read('db/trade/auction_item.sql')
need('ADD COLUMN IF NOT EXISTS item_id text NOT NULL DEFAULT' in tsql, 'auction_item.sql 要给挂单加 item_id')

# 4. 源码里不能再有「按格子读 / 写强化」。
source = {}
for base, _, files in os.walk(os.path.join(root, 'src')):
    for name in files:
        if name.endswith('.cs'):
            path = os.path.join(base, name)
            # Windows 上 relpath 用反斜杠，下面按 'src/Character/...' 取键，先归一再存。
            source[os.path.relpath(path, root).replace(os.sep, '/')] = open(path, encoding='utf-8').read()
joined = '\n'.join(source.values())
need('e.slot = i.slot' not in joined, '还有人按 e.slot = i.slot 联强化表')
need('enhance (character_id, slot, level)' not in joined, '还有按格子写强化等级的语句')

enhance = source['src/Character/EnhanceRepository.cs']
need('on conflict (inventory_id) do update' in enhance, '强化写入要按 inventory_id 落')
need('left join \\"角色\\".enhance e on e.inventory_id = i.id' in enhance, '强化读取要按 inventory_id 联背包行')
need('for update of i' in enhance, '强化仍要先锁装备那一行')
need('inventoryId' in enhance, 'ApplyAsync 要拿到背包行 id')

transfer = source['src/Character/TransferRepository.cs']
need('e.inventory_id = i.id' in transfer, '转移读取要按 inventory_id')
need('write.Parameters.AddWithValue(rows[slot]);' in transfer, '转移写入要按背包行 id')
need('levels[slot] = reader.GetInt16(4);' in transfer, '两格的等级要和背包行一起读出来')

crystal = source['src/Character/CrystalRepository.cs']
need('e.inventory_id = i.id' in crystal, '结晶读强化的联表条件要跟着换')

auction = source['src/Character/AuctionRepository.cs']
need('listingItemId' in auction, '结算要读挂单记下的 item_id')
need('enhanceLevel' in auction, '结算要把强化等级交给买方')
need('insert into \\"角色\\".enhance (character_id, inventory_id, level)' in auction, '买方新行要按行 id 收等级')
need('if (listingItemId.Length > 0' in auction, '老挂单（item_id 空）要继续按格位结')

listing = source['src/Global/AuctionListingRepository.cs']
need('slot, item_id, count' in listing, '挂单要写 item_id')

global_service = source['src/Global/GlobalGrpcService.cs']
need('ListInventoryAsync' in global_service and 'itemId = entry.ItemId' in global_service,
     '挂单要从只读背包口读物品编号')

# 5. 协议面：C4 自己不加 RPC、不给已有消息加字段（客户端不用为 C4 重出包）。
# Q10（第 33 行）按自己的卡合法加了 MoveItem / DiscardItem / SortInventory / UseItemBatch，
# 所以这里收窄成「C4 的改动面之外没有第二个换格 RPC」：SwapItem 仍不该存在，
# C4 依赖的那两个老消息也还在。收窄不放松口径——C4 那四处一个字段都没加这条仍成立。
proto = read('proto/mmo/character.proto')
need('rpc SwapItem' not in proto, 'C4 不新增换格 RPC（SwapItem 不该存在）')
need('message EnhanceRequest' in proto and 'message CanTradeResponse' in proto,
     'C4 依赖的两个老消息要原样在')
req = re.search(r'message EnhanceRequest \{(.*?)\}', proto, re.S).group(1)
fields = set(re.findall(r'(?:string|uint64|uint32|int64|int32|bool)\s+(\w+)\s*=', req))
need(fields == {'account', 'character_id', 'slot', 'stone_id', 'txn_id', 'use_coupon'}, 'EnhanceRequest 字段被动过')

# 6. 数值与上限仍只在配置里，代码不写第二套。
cfg = json.loads(read('config/enhance.json'))
need(cfg['max_level'] == 12, '强化上限必须仍是 12')
for path in ('src/Character/EnhanceRepository.cs', 'src/Character/TransferRepository.cs'):
    need(not re.search(r'success_rate\s*=\s*[0-9]', source[path]), path + ' 里出现写死的概率')

print('c4_static=ok identity=inventory_id listing=item_id proto=unchanged')
