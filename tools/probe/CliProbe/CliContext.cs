using Npgsql;

namespace Character.Cli;

/// <summary>连库核对共用的一份数据源与角色仓储；与拆分前 Program 里的两个局部变量同源。</summary>
internal sealed class CliContext(NpgsqlDataSource dataSource, CharacterRepository characters)
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public CharacterRepository Characters { get; } = characters;
}
