using Character.Cli;

// 入口只留启动与迁移：flag 解析、只读核对与连库入口都在 Character/Cli/*（D1a）。
// 133 个 flag 名与回包一行未改，核对脚本仍按原命令调。
return await CliEntry.RunAsync(args);
