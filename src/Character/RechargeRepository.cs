using Npgsql;

namespace Character;

/// <summary>
/// 一张内部充值订单的只读快照：编号、角色、商品、元宝数、状态，以及这个角色
/// 当前元宝余额。状态与余额都从库里读，不另算。
/// </summary>
public sealed record RechargeOrderState(
    bool Found,
    string OrderId,
    long CharacterId,
    string ProductId,
    long Yuanbao,
    string Status,
    bool Paid,
    long PaidAtMs,
    long NowMs,
    long Balance);

/// <summary>一笔充值的结算结果。Reason 空串表示这一笔真发了；duplicate 表示整笔没做。</summary>
public sealed record RechargeSettleOutcome(
    bool Found,
    string Reason,
    string OrderId,
    string Status,
    long Yuanbao,
    long PaidAtMs,
    long Balance,
    bool Duplicate);

/// <summary>
/// 内部充值订单的唯一读写点（P1-5d）。一笔订单一行，`order_id` 是主键：
///
///   - CreateAsync 只插 pending 行，元宝数由调用方按配置传入，请求里带不进来。
///   - SettleAsync 是唯一发元宝的地方：锁订单行 → 认归属 → 已经是 paid 就整笔不做 →
///     抢 `grant_txn`（号是 `recharge:订单号`，双保险）→ 加元宝 → 把订单改成 paid →
///     读回余额提交。同一笔回调来两次，第二次看到 paid 直接回 duplicate，一分不加。
///
/// 状态落在库里，不在任何进程内存里——网关重发、进程重启、换一台机器都是同一个答案。
/// </summary>
public sealed class RechargeRepository(NpgsqlDataSource dataSource)
{
    public const string Yuanbao = "yuanbao";
    public const string TxnPrefix = "recharge:";

    /// <summary>
    /// 建一张待支付订单。角色不存在返回 false（不建孤儿单）。
    /// 订单号由调用方生成；同一个号重复插入会被主键挡住。
    /// </summary>
    public async Task<bool> CreateAsync(
        string orderId, long characterId, string productId, long yuanbao,
        CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "insert into \"角色\".recharge_order "
            + "(order_id, character_id, product_id, yuanbao, status) "
            + "select $1, c.id, $2, $3, 'pending' from \"角色\".character c where c.id = $4 "
            + "returning order_id");
        command.Parameters.AddWithValue(orderId);
        command.Parameters.AddWithValue(productId);
        command.Parameters.AddWithValue(yuanbao);
        command.Parameters.AddWithValue(characterId);
        return await command.ExecuteScalarAsync(stopping) is not null;
    }

    /// <summary>按订单号读回状态与当前元宝余额。找不到返回 Found=false。</summary>
    public async Task<RechargeOrderState> StateAsync(string orderId, CancellationToken stopping)
    {
        await using var command = dataSource.CreateCommand(
            "select o.order_id, o.character_id, o.product_id, o.yuanbao, o.status, "
            + "coalesce(extract(epoch from o.paid_at) * 1000, 0), "
            + "extract(epoch from now()) * 1000, "
            + "coalesce((select amount from \"角色\".currency "
            + "          where character_id = o.character_id and kind = $2), 0) "
            + "from \"角色\".recharge_order o where o.order_id = $1");
        command.Parameters.AddWithValue(orderId);
        command.Parameters.AddWithValue(Yuanbao);

        await using var reader = await command.ExecuteReaderAsync(stopping);
        if (!await reader.ReadAsync(stopping))
        {
            return new RechargeOrderState(false, orderId, 0, "", 0, "", false, 0, 0, 0);
        }

        var status = reader.GetString(4);
        return new RechargeOrderState(
            true,
            reader.GetString(0),
            Convert.ToInt64(reader.GetValue(1)),
            reader.GetString(2),
            Convert.ToInt64(reader.GetValue(3)),
            status,
            string.Equals(status, "paid", StringComparison.Ordinal),
            (long)Math.Round(Convert.ToDouble(reader.GetValue(5))),
            (long)Math.Round(Convert.ToDouble(reader.GetValue(6))),
            Convert.ToInt64(reader.GetValue(7)));
    }

    /// <summary>
    /// 结算一笔订单。account 非空时核归属（网关转发一定带）；同一笔重复调用整笔不做。
    /// </summary>
    public async Task<RechargeSettleOutcome> SettleAsync(
        string orderId, string? account, CancellationToken stopping)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stopping);
        await using var transaction = await connection.BeginTransactionAsync(stopping);

        // 1. 锁订单行（连带角色行一起读，后面核归属要用 account）。
        long characterId;
        string status;
        long yuanbao;
        string owner;
        var found = false;
        characterId = 0;
        status = "";
        yuanbao = 0;
        owner = "";
        await using (var order = new NpgsqlCommand(
            "select o.character_id, o.status, o.yuanbao, c.account "
            + "from \"角色\".recharge_order o "
            + "join \"角色\".character c on c.id = o.character_id "
            + "where o.order_id = $1 for update of o",
            connection, transaction))
        {
            order.Parameters.AddWithValue(orderId);
            // reader 必须在回滚之前读完全部行并关掉：连接上挂着 reader 时 rollback 会抛
            // NpgsqlOperationInProgressException，表现为一个笼统的 502。
            await using (var reader = await order.ExecuteReaderAsync(stopping))
            {
                if (await reader.ReadAsync(stopping))
                {
                    found = true;
                    characterId = Convert.ToInt64(reader.GetValue(0));
                    status = reader.GetString(1);
                    yuanbao = Convert.ToInt64(reader.GetValue(2));
                    owner = reader.GetString(3);
                }
            }
        }

        if (!found)
        {
            await transaction.RollbackAsync(stopping);
            return new RechargeSettleOutcome(
                false, "order_not_found", orderId, "", 0, 0, 0, false);
        }

        // 不是自己的订单：按「找不到」回，不泄露别人订单的存在。
        if (!string.IsNullOrEmpty(account)
            && !string.Equals(account, owner, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(stopping);
            return new RechargeSettleOutcome(
                false, "order_not_found", orderId, "", 0, 0, 0, false);
        }

        var balance = await ReadBalanceAsync(connection, transaction, characterId, stopping);

        // 2. 已经发过：整笔不做，把当前事实值带回去。
        if (string.Equals(status, "paid", StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(stopping);
            return new RechargeSettleOutcome(
                true, "duplicate", orderId, status, yuanbao, 0, balance, true);
        }

        // 3. 抢事务号：同一个订单号只发一次。订单行的 paid 已经挡了一层，
        //    这里是第二层，防「状态没读对」时发出第二笔。
        await using (var claim = new NpgsqlCommand(
            "insert into \"角色\".grant_txn (txn_id, character_id) values ($1, $2) "
            + "on conflict (txn_id) do nothing returning txn_id",
            connection, transaction))
        {
            claim.Parameters.AddWithValue(TxnPrefix + orderId);
            claim.Parameters.AddWithValue(characterId);
            if (await claim.ExecuteScalarAsync(stopping) is null)
            {
                await transaction.RollbackAsync(stopping);
                return new RechargeSettleOutcome(
                    true, "duplicate", orderId, status, yuanbao, 0, balance, true);
            }
        }

        // 4. 加元宝。只走 upsert 的加法分支，充值只加不减。
        long after;
        await using (var add = new NpgsqlCommand(
            "insert into \"角色\".currency (character_id, kind, amount) "
            + "values ($1, $2, $3) "
            + "on conflict (character_id, kind) do update "
            + "set amount = currency.amount + excluded.amount, updated_at = now() "
            + "returning amount",
            connection, transaction))
        {
            add.Parameters.AddWithValue(characterId);
            add.Parameters.AddWithValue(Yuanbao);
            add.Parameters.AddWithValue(yuanbao);
            after = Convert.ToInt64(await add.ExecuteScalarAsync(stopping));
        }

        // 5. 订单改成 paid，paid_at 与状态一起写（表上有 CHECK 要求两者对得上）。
        await using (var paid = new NpgsqlCommand(
            "update \"角色\".recharge_order set status = 'paid', paid_at = now() "
            + "where order_id = $1 and status = 'pending'", connection, transaction))
        {
            paid.Parameters.AddWithValue(orderId);
            if (await paid.ExecuteNonQueryAsync(stopping) != 1)
            {
                // 理论上到不了：订单行已经锁住且状态是 pending。真到了就整笔回滚。
                await transaction.RollbackAsync(stopping);
                return new RechargeSettleOutcome(
                    true, "duplicate", orderId, "paid", yuanbao, 0, balance, true);
            }
        }

        long paidAtMs;
        await using (var read = new NpgsqlCommand(
            "select coalesce(extract(epoch from paid_at) * 1000, 0) "
            + "from \"角色\".recharge_order where order_id = $1", connection, transaction))
        {
            read.Parameters.AddWithValue(orderId);
            paidAtMs = (long)Math.Round(Convert.ToDouble(await read.ExecuteScalarAsync(stopping)));
        }

        await transaction.CommitAsync(stopping);
        return new RechargeSettleOutcome(
            true, "", orderId, "paid", yuanbao, paidAtMs, after, false);
    }

    static async Task<long> ReadBalanceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        long characterId, CancellationToken stopping)
    {
        await using var command = new NpgsqlCommand(
            "select coalesce((select amount from \"角色\".currency "
            + "where character_id = $1 and kind = $2), 0)",
            connection, transaction);
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(Yuanbao);
        return Convert.ToInt64(await command.ExecuteScalarAsync(stopping));
    }
}
