using System.IO;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;

namespace Chronicler.Projects;

public sealed record RevisionHistoryEntry(ulong Number, string Revision, string? Message, string? Author, DateTimeOffset? Timestamp)
{
    public string DisplayMessage => string.IsNullOrWhiteSpace(Message) ? "メッセージなし" : Message;
    public string DisplayAuthor => string.IsNullOrWhiteSpace(Author) ? "作成者不明" : Author;
    public string DisplayTimestamp => Timestamp?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "日時不明";
}

public interface IRevisionHistoryService
{
    Task<IReadOnlyList<RevisionHistoryEntry>> ReadAsync(string path);
}

public sealed class LoreRevisionHistoryService : IRevisionHistoryService
{
    public const uint MaximumEntries = 100;

    public async Task<IReadOnlyList<RevisionHistoryEntry>> ReadAsync(string path)
    {
        var entries = new List<EntryBuilder>();
        var sync = new object();
        string? error = null;
        using var globals = new LoreGlobalArgs { RepositoryPath = path };
        using var args = new LoreRevisionHistoryArgs { Length = MaximumEntries };
        await Lore.RevisionHistory(globals, args).Callback((ev, _) =>
        {
            lock (sync)
            {
                if (ev.Tag == LoreEventTag.REVISION_HISTORY_ENTRY)
                {
                    var data = ev.GetData<LoreRevisionHistoryEntryEventDataFFI>();
                    entries.Add(new EntryBuilder(data.RevisionNumber, Convert.ToHexString(data.Revision.Data)));
                }
                else if (ev.Tag == LoreEventTag.METADATA && entries.Count > 0)
                {
                    var data = ev.GetData<LoreMetadataEventDataFFI>();
                    var entry = entries[^1];
                    var value = data.Value;
                    switch (data.Key)
                    {
                        case "message":
                            if (value.Tag == LoreMetadataType.STRING) entry.Message = value.String;
                            else error = "リビジョンのメッセージの形式が不正です。";
                            break;
                        case "created-by":
                            if (value.Tag == LoreMetadataType.STRING) entry.CreatedBy = value.String;
                            else error = "リビジョンの作成者の形式が不正です。";
                            break;
                        case "committed-by":
                            if (value.Tag == LoreMetadataType.STRING) entry.CommittedBy = value.String;
                            else error = "リビジョンの記録者の形式が不正です。";
                            break;
                        case "timestamp":
                            if (value.Tag != LoreMetadataType.NUMERIC || value.Numeric > 253402300799999UL)
                                error = "リビジョンの日時の形式が不正です。";
                            else if (value.Numeric != 0)
                                entry.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)value.Numeric);
                            break;
                    }
                }
            }
        }).WaitAsync();

        if (error is not null) throw new InvalidDataException(error);
        if (entries.Count > MaximumEntries) throw new InvalidDataException("リビジョン履歴が指定した件数を超えています。");
        return entries.Select(entry => new RevisionHistoryEntry(entry.Number, entry.Revision, entry.Message,
            entry.CreatedBy ?? entry.CommittedBy, entry.Timestamp)).ToArray();
    }

    private sealed class EntryBuilder(ulong number, string revision)
    {
        public ulong Number { get; } = number;
        public string Revision { get; } = revision;
        public string? Message { get; set; }
        public string? CreatedBy { get; set; }
        public string? CommittedBy { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
