namespace Mahjong.Autotable.Api.Persistence;

public class PersistenceOptions
{
    public string Provider { get; set; } = PersistenceProvider.Sqlite.ToString();
}
